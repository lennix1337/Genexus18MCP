using System;
using System.Collections.Generic;
using System.Linq;
using Artech.Architecture.Common.Objects;
using Artech.Genexus.Common.Objects;
using Artech.Genexus.Common.Parts;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;
using Newtonsoft.Json.Linq;
using System.IO;
using GenexusServices = Artech.Genexus.Common.Services;
using GenexusCommands = Artech.Genexus.Common.Commands;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// genexus_db action=reorg_impact / action=reorg_preview — reorg / DDL impact
    /// preview (P1 #5, issue #61). Both are read-only.
    ///
    /// reorg_impact: cheap <c>IModelInformationService</c> timestamp heuristic by
    /// default (<c>reorgLikelyNeeded</c> = a table changed after the last reorg);
    /// <c>deep=true</c> runs <c>ISpecifierService.ImpactDatabase</c> (specification,
    /// build-heavy) for the authoritative <c>AnalysisResult</c>.
    ///
    /// reorg_preview (issue #61): model-level before/after diff of the physical
    /// schema for a Transaction/Table target — nullable per issue #57 (the logical
    /// structure can declare Nullable while the physical Table column is still
    /// NOT NULL), type/length/decimals changes, index inventory, proposed DDL and
    /// destructive-change warnings. Fast mode uses the timestamp heuristic for
    /// <c>requiresReorganization</c>; <c>deep=true</c> replaces it with the SDK's
    /// ImpactDatabase verdict. The worker opens no live DB connection, so the
    /// "before" is the model's Table structure, not the live database.
    ///
    /// Both services are non-<c>IGxService</c> → resolved via <see cref="SdkServiceLocator"/>.
    /// </summary>
    public class ReorgImpactService
    {
        // Plan 070: deep=true runs ISpecifierService.ImpactDatabase — a full specification
        // pass that can take minutes on a large KB. Shared const so the two emission sites
        // (reorg_impact + reorg_preview) can't drift.
        private const string DeepImpactRuntimeNote =
            "ImpactDatabase runs a full specification pass and can take minutes on a large KB. The gateway sync ceiling for deep=true calls is 10 minutes; prefer deep=false for the fast timestamp heuristic when an instant verdict is enough.";

        private readonly KbService _kb;
        private readonly ObjectService _objectService;

        public ReorgImpactService(KbService kb, ObjectService objectService = null)
        {
            _kb = kb;
            _objectService = objectService;
        }

        public string Run(JObject args)
        {
            if (!KbModelGuard.TryGetDesignModel(_kb, out var model, out var kbErr))
                return kbErr;

            var info = SdkServiceLocator.ConstructOrResolve<GenexusServices.IModelInformationService>(
                () => new Artech.Packages.Genexus.BL.Services.ModelInformationService());
            if (info == null)
                return McpResponse.Err(
                    code: "ModelInformationServiceUnavailable",
                    message: "The GeneXus SDK's IModelInformationService is not registered in this worker session.",
                    hint: "Restart the worker (genexus_worker_reload mode=hard) and retry.");

            DateTime lastTbl = SafeDate(() => info.GetLastModifiedTableTimestamp(model));
            DateTime lastReorg = SafeDate(() => info.GetLastReorgTimestamp(model));
            bool likely = lastTbl != default && lastTbl > lastReorg;

            bool preview = string.Equals(args?["action"]?.ToString(), "reorg_preview", StringComparison.OrdinalIgnoreCase);
            var result = new JObject
            {
                ["lastModifiedTable"] = Iso(lastTbl),
                ["lastReorg"] = Iso(lastReorg),
                ["reorgLikelyNeeded"] = likely,
                ["source"] = "sdk:IModelInformationService",
                ["hint"] = preview
                    ? "Pass deep=true to run non-mutating Impact Analysis and capture the exact generated reorganization SQL artifact."
                    : "Cheap heuristic (timestamps). For the authoritative signal pass deep=true (runs specification, build-heavy)."
            };

            bool deep = args?["deep"]?.ToObject<bool?>() ?? false;
            DateTime impactStartedUtc = DateTime.UtcNow;
            ApplyDeepAnalysis(model, result, deep);
            if (preview)
                AttachSqlPreview(result, deep, likely, impactStartedUtc, "preview");

            return McpResponse.Ok(code: preview ? "ReorgPreviewRetrieved" : "ReorgImpactRetrieved", result: result);
        }

        // ---------------------------------------------------------------------
        // reorg_preview — model-level before/after diff (issue #61). Read-only.
        // ---------------------------------------------------------------------

        /// <summary>
        /// genexus_db action=reorg_preview name=&lt;Transaction|Table&gt; [deep=true].
        /// Diffs the LOGICAL model structure (Transaction levels / Table structure,
        /// nullable read per issue #57) against the PHYSICAL Table structure the
        /// model currently records. The divergence is what a reorg would apply.
        /// </summary>
        public string Preview(JObject args)
        {
            if (!KbModelGuard.TryGetDesignModel(_kb, out var model, out var kbErr))
                return kbErr;

            string name = args?["name"]?.ToString() ?? args?["target"]?.ToString();
            if (string.IsNullOrWhiteSpace(name))
                return McpResponse.Err(
                    code: "TargetRequired",
                    message: "reorg_preview requires a target name.",
                    hint: "Pass name=<Transaction|Table> (e.g. genexus_db action=reorg_preview name=Customer).",
                    target: name);

            if (_objectService == null)
                return McpResponse.Err(
                    code: "ObjectServiceUnavailable",
                    message: "The object resolver is not available in this worker session.",
                    hint: "Restart the worker (genexus_worker_reload mode=hard) and retry.",
                    target: name);

            string requestedType = args?["type"]?.ToString();
            var obj = _objectService.FindObject(name, requestedType);
            if (obj == null)
                return McpResponse.Err(
                    code: "ObjectNotFound",
                    message: "Object not found.",
                    hint: "Verify the object name and ensure the KB is open. reorg_preview targets a Transaction or Table.",
                    nextSteps: new JArray(McpResponse.NextStep(
                        tool: "genexus_list_objects",
                        args: new JObject { ["type"] = "Transaction" },
                        why: "Lists available Transaction objects in the KB.")),
                    target: name);

            Transaction trn = obj as Transaction;
            Table tbl = null;
            if (trn != null)
            {
                // Same disambiguation as sql_ddl: ask for the Table explicitly so the
                // logical object (the Transaction itself) doesn't win the re-resolve.
                tbl = _objectService.FindObject(trn.Name, "Table") as Table;
            }
            else if (obj is Table t)
            {
                tbl = t;
            }

            var tables = new JArray();
            var topWarnings = new JArray();

            // Aggregate per-table warnings into the top-level `warnings` array (the
            // issue #61 conceptual response puts warnings at the top level), each
            // tagged with the owning table so an agent can still map them back.
            void CollectTopWarnings(JObject entry)
            {
                if (entry["warning"] is JObject w)
                {
                    w["severity"] = "info";
                    if (w["tableName"] == null) w["tableName"] = entry["tableName"];
                    topWarnings.Add(w);
                }
                if (entry["warnings"] is JArray changeWarnings)
                {
                    foreach (var cw in changeWarnings.OfType<JObject>())
                    {
                        var tagged = (JObject)cw.DeepClone();
                        if (tagged["tableName"] == null) tagged["tableName"] = entry["tableName"];
                        topWarnings.Add(tagged);
                    }
                }
            }

            if (trn != null)
            {
                // Walk root + subordinated levels; pair each with its physical table.
                foreach (var pair in EnumerateLevelTables(trn))
                {
                    var tableEntry = BuildTableDiff(pair.Level, pair.Table, model);
                    CollectTopWarnings(tableEntry);
                    tables.Add(tableEntry);
                }
            }
            else if (tbl != null)
            {
                // Table-only target: report its current physical structure as both the
                // before and after (no logical level to diff against).
                var entry = BuildPhysicalSnapshot(tbl);
                CollectTopWarnings(entry);
                tables.Add(entry);
            }
            else
            {
                return McpResponse.Err(
                    code: "TableNotFound",
                    message: "The target does not resolve to a Transaction or Table with physical structure.",
                    hint: "Ensure the target is a Transaction or Table object. reorg_preview compares the model structure before/after a reorg.",
                    target: name);
            }

            // requiresReorganization: cheap timestamp heuristic by default; deep=true
            // overrides with the authoritative ImpactDatabase verdict.
            var info = SdkServiceLocator.ConstructOrResolve<GenexusServices.IModelInformationService>(
                () => new Artech.Packages.Genexus.BL.Services.ModelInformationService());
            bool? requiresReorg = null;
            string source = "sdk:IModelInformationService";
            if (info != null)
            {
                DateTime lastTbl = SafeDate(() => info.GetLastModifiedTableTimestamp(model));
                DateTime lastReorg = SafeDate(() => info.GetLastReorgTimestamp(model));
                requiresReorg = lastTbl != default && lastTbl > lastReorg;
            }

            bool deep = args?["deep"]?.ToObject<bool?>() ?? false;
            DateTime impactStartedUtc = DateTime.UtcNow;
            var deepBlock = new JObject();
            if (deep)
            {
                var spec = SdkServiceLocator.ConstructOrResolve<GenexusServices.ISpecifierService>(
                    () => new Artech.Packages.Specifier.Services.SpecifierService());
                if (spec == null)
                {
                    deepBlock["deepError"] = "ISpecifierService unavailable";
                }
                else
                {
                    try
                    {
                        var options = GenexusCommands.BuildOptions.ImpactAnalysis | GenexusCommands.BuildOptions.CreateAnalysis;
                        var analysis = spec.ImpactDatabase(model, options);
                        deepBlock["deepAnalysis"] = analysis.ToString();
                        deepBlock["requiresReorganization"] =
                            string.Equals(analysis.ToString(), "ReorganizationNeeded", StringComparison.OrdinalIgnoreCase);
                        requiresReorg = deepBlock["requiresReorganization"].Value<bool>();
                        source = "sdk:ISpecifierService";
                        // Plan 070: surface the expected runtime so callers don't mistake a
                        // multi-minute specification pass for a hung worker — the gateway's
                        // sync ceiling for deep=true calls is 10 minutes (600s).
                        deepBlock["runtimeNote"] = DeepImpactRuntimeNote;
                    }
                    catch (Exception ex)
                    {
                        deepBlock["deepError"] = ex.Message;
                    }
                }
            }

            int totalChanges = tables.OfType<JObject>().Sum(t => (t["changes"] as JArray)?.Count ?? 0);
            int destructive = tables.OfType<JObject>().Sum(t => (t["warnings"] as JArray)?.Count ?? 0);

            var result = new JObject
            {
                ["requiresReorganization"] = requiresReorg.HasValue ? (JToken)requiresReorg.Value : JValue.CreateNull(),
                ["source"] = source,
                ["target"] = name,
                ["objectType"] = trn != null ? "Transaction" : "Table",
                ["tables"] = tables,
                ["summary"] = new JObject
                {
                    ["tables"] = tables.Count,
                    ["changes"] = totalChanges,
                    ["destructiveWarnings"] = destructive
                },
                ["hint"] = "Before = the physical Table structure the model records; after = the logical model structure. The worker opens no live DB connection, so the authoritative reorg-needed verdict requires deep=true (ISpecifierService.ImpactDatabase, specification) — it reports the AnalysisResult verdict, not statement-level DDL. For the desired-schema DDL use genexus_db action=sql_ddl; to apply the delta run genexus_lifecycle action=reorg on a non-production environment."
            };
            if (deepBlock.Count > 0) result["deep"] = deepBlock;
            if (topWarnings.Count > 0) result["warnings"] = topWarnings;
            AttachSqlPreview(result, deep, requiresReorg ?? false, impactStartedUtc, "sqlPreview");
            result["nonMutating"] = true;
            result["logicalPhysicalDivergence"] = requiresReorg ?? false;

            return McpResponse.Ok(target: name, code: "ReorgPreviewRetrieved", result: result);
        }

        private sealed class LevelTablePair
        {
            public dynamic Level;
            public Table Table;
        }

        // Enumerate (level, physical table) pairs for a Transaction: the root level
        // plus every subordinated level that resolves to its own table. Mirrors the
        // navigation used by sql_ddl (DataInsightService.ResolveSubordinatedTables).
        private List<LevelTablePair> EnumerateLevelTables(Transaction trn)
        {
            var result = new List<LevelTablePair>();
            var tablesByName = new Dictionary<string, Table>(StringComparer.OrdinalIgnoreCase);
            try
            {
                dynamic root = trn.Structure.Root;
                if (root == null) return result;

                var stack = new Stack<dynamic>();
                stack.Push(root);
                while (stack.Count > 0)
                {
                    dynamic level = stack.Pop();

                    string tableName = null;
                    try { tableName = (string)level.AssociatedTableName; } catch { }
                    if (string.IsNullOrEmpty(tableName))
                        try { tableName = (string)level.AssociatedTable?.Name; } catch { }
                    if (string.IsNullOrEmpty(tableName))
                        try { tableName = (string)level.Name; } catch { }

                    Table table = null;
                    if (!string.IsNullOrEmpty(tableName))
                    {
                        if (!tablesByName.TryGetValue(tableName, out table))
                        {
                            try { table = _objectService.FindObject(tableName, "Table") as Table; } catch { }
                            // Cache both successful and unsuccessful lookups. A
                            // shared table on an extended level must reuse the same
                            // physical structure instead of becoming a false
                            // "table not found" diff on the second occurrence.
                            tablesByName[tableName] = table;
                        }
                    }
                    result.Add(new LevelTablePair { Level = level, Table = table });

                    try
                    {
                        if (level.Levels != null)
                            foreach (dynamic child in level.Levels) stack.Push(child);
                    }
                    catch { }
                }
            }
            catch { }
            return result;
        }

        // Diff one Transaction level against its physical table. Pure-ish: all SDK
        // reads are wrapped in try/catch and the comparison itself is delegated to
        // the static DiffColumns helper (unit-tested without an SDK).
        private JObject BuildTableDiff(dynamic level, Table table, KBModel model)
        {
            var after = ReadLogicalLevel(level);
            JArray before = null;
            if (table != null)
            {
                before = ReadPhysicalTable(table);
            }
            else
            {
                before = new JArray();
            }

            var entry = new JObject
            {
                ["tableName"] = table != null ? table.Name : (SafeString(() => (string)level.Name) ?? "?"),
                ["level"] = SafeString(() => (string)level.Name) ?? "",
                ["columns"] = after,
                ["changes"] = DiffColumns(before, after),
                ["indexes"] = table != null ? ReadIndexes(table) : new JArray()
            };

            if (table == null)
            {
                entry["warning"] = new JObject
                {
                    ["level"] = entry["level"],
                    ["message"] = "No physical Table structure resolved for this level — either the table is not yet generated (reorg would CREATE it from the logical structure) or this is an extended level sharing its parent's table."
                };
            }

            // Proposed DDL from the "after" (logical) columns — heuristic, labeled
            // like sql_ddl so it is never mistaken for authoritative reorg output.
            string ddl = RenderCreateTable(entry["tableName"]?.ToString(), after);
            if (!string.IsNullOrEmpty(ddl))
            {
                entry["proposedDdl"] = ddl;
                entry["ddlAccuracy"] = DataInsightService.BuildDdlAccuracy(hasNativeSql: false);
            }

            var warnings = new JArray();
            foreach (var c in (entry["changes"] as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                var w = c["warning"];
                if (w != null) warnings.Add(w);
            }
            entry["warnings"] = warnings;
            return entry;
        }

        // Table-only target: current physical structure is both before and after.
        private JObject BuildPhysicalSnapshot(Table tbl)
        {
            var physical = ReadPhysicalTable(tbl);
            var entry = new JObject
            {
                ["tableName"] = tbl.Name,
                ["level"] = "",
                ["columns"] = physical,
                ["changes"] = new JArray(),
                ["indexes"] = ReadIndexes(tbl),
                ["warnings"] = new JArray(),
                ["note"] = "Table-only target: no Transaction level to diff against, so before == after (the current model structure). For a logical-vs-physical diff target the owning Transaction instead."
            };
            string ddl = RenderCreateTable(tbl.Name, physical);
            if (!string.IsNullOrEmpty(ddl))
            {
                entry["proposedDdl"] = ddl;
                entry["ddlAccuracy"] = DataInsightService.BuildDdlAccuracy(hasNativeSql: false);
            }
            return entry;
        }

        // Read the LOGICAL column definitions of a Transaction level. Nullable is
        // read per issue #57: the logical structure can declare Nullable (via the
        // global attribute's Nullable property) while the physical column is still
        // NOT NULL — that divergence is exactly what reorg would fix.
        private JArray ReadLogicalLevel(dynamic level)
        {
            var cols = new JArray();
            try
            {
                if (level.Attributes == null) return cols;
                foreach (dynamic attr in level.Attributes)
                {
                    var col = new JObject();
                    col["name"] = SafeString(() => (string)attr.Name) ?? "";
                    try { col["isKey"] = (bool)attr.IsKey; } catch { col["isKey"] = false; }

                    string typeName = null;
                    int length = 0, decimals = 0;
                    try { if (attr.Attribute != null) typeName = attr.Attribute.Type.ToString(); } catch { }
                    if (string.IsNullOrEmpty(typeName))
                        try { typeName = attr.Type.ToString(); } catch { }
                    try { if (attr.Attribute != null) { length = attr.Attribute.Length; decimals = attr.Attribute.Decimals; } } catch { }

                    col["type"] = typeName ?? "Unknown";
                    col["length"] = length;
                    col["decimals"] = decimals;

                    bool nullable = false;
                    try
                    {
                        dynamic pNullable = attr.Attribute.Properties.Get("Nullable");
                        string nVal = pNullable?.ToString();
                        nullable = nVal != null &&
                                   (nVal.Equals("Yes", StringComparison.OrdinalIgnoreCase) ||
                                    nVal.Equals("Nullable", StringComparison.OrdinalIgnoreCase));
                    }
                    catch
                    {
                        try { nullable = attr.IsNullable != null && attr.IsNullable.ToString().Contains("True"); } catch { }
                    }
                    col["nullable"] = nullable;
                    cols.Add(col);
                }
            }
            catch { }
            return cols;
        }

        // Read the PHYSICAL column definitions of a Table structure. Nullable per
        // issue #57: TableAttribute.IsNullableValue.True is the authoritative signal.
        private JArray ReadPhysicalTable(Table tbl)
        {
            var cols = new JArray();
            try
            {
                foreach (var attr in tbl.TableStructure.Attributes)
                {
                    var col = new JObject();
                    col["name"] = attr.Name;
                    col["isKey"] = attr.IsKey;
                    string typeName = "Unknown";
                    int length = 0, decimals = 0;
                    try { if (attr.Attribute != null) typeName = attr.Attribute.Type.ToString(); } catch { }
                    try { if (attr.Attribute != null) { length = attr.Attribute.Length; decimals = attr.Attribute.Decimals; } } catch { }
                    col["type"] = typeName;
                    col["length"] = length;
                    col["decimals"] = decimals;
                    bool nullable = false;
                    try { nullable = attr.IsNullable == TableAttribute.IsNullableValue.True; }
                    catch
                    {
                        try { int nVal = Convert.ToInt32(attr.IsNullable); nullable = nVal == 1; } catch { }
                    }
                    col["nullable"] = nullable;
                    cols.Add(col);
                }
            }
            catch { }
            return cols;
        }

        private JArray ReadIndexes(Table tbl)
        {
            var indexes = new JArray();
            try
            {
                dynamic dIndexesPart = ((dynamic)tbl).TableIndexes;
                if (dIndexesPart != null && dIndexesPart.Indexes != null)
                {
                    foreach (dynamic idxObj in dIndexesPart.Indexes)
                    {
                        dynamic idx = idxObj.Index;
                        if (idx == null) continue;
                        var item = new JObject();
                        item["name"] = idx.Name;
                        string typeStr = idx.IndexType != null ? idx.IndexType.ToString() : "";
                        bool isPrimary = typeStr.Contains("Primary");
                        item["isPrimary"] = isPrimary;
                        item["isUnique"] = typeStr.Contains("Unique") || isPrimary;
                        try { item["source"] = idx.Source != null ? idx.Source.ToString() : ""; } catch { item["source"] = ""; }
                        var attrs = new JArray();
                        if (idx.IndexStructure != null && idx.IndexStructure.Members != null)
                        {
                            foreach (dynamic m in idx.IndexStructure.Members)
                            {
                                attrs.Add(new JObject
                                {
                                    ["name"] = m.Attribute != null ? m.Attribute.Name : m.Name,
                                    ["isAscending"] = SafeBool(() => m.Order.ToString().Contains("Ascending"), true)
                                });
                            }
                        }
                        item["attributes"] = attrs;
                        indexes.Add(item);
                    }
                }
            }
            catch { }
            return indexes;
        }

        // ---------------------------------------------------------------------
        // Pure comparison helpers — unit-tested without an SDK (ReorgPreviewTests).
        // Column shape: { name, type, length, decimals, nullable, isKey }.
        // ---------------------------------------------------------------------

        /// <summary>
        /// Diff a physical "before" column list against a logical "after" list.
        /// Emits one change object per column that is added, dropped, or has a
        /// differing type/length/decimals/nullable. Destructive transitions
        /// (cross-family type change, length shrink, nullable True→False, drop)
        /// carry a <c>warning</c> object.
        /// </summary>
        internal static JArray DiffColumns(JArray before, JArray after)
        {
            var changes = new JArray();
            var beforeByName = (before ?? new JArray()).OfType<JObject>()
                .ToDictionary(c => (c["name"]?.ToString() ?? "").ToLowerInvariant(), StringComparer.OrdinalIgnoreCase);
            var afterByName = (after ?? new JArray()).OfType<JObject>()
                .ToDictionary(c => (c["name"]?.ToString() ?? "").ToLowerInvariant(), StringComparer.OrdinalIgnoreCase);

            foreach (var aCol in afterByName.Values)
            {
                string aName = aCol["name"]?.ToString() ?? "";
                if (!beforeByName.TryGetValue(aName, out var bCol))
                {
                    changes.Add(new JObject
                    {
                        ["column"] = aName,
                        ["field"] = "added",
                        ["before"] = (JToken)"<absent>",
                        ["after"] = RenderColumnDef(aCol),
                        ["destructive"] = false
                    });
                    continue;
                }

                string bType = bCol["type"]?.ToString() ?? "Unknown";
                string aType = aCol["type"]?.ToString() ?? "Unknown";
                int bLen = bCol["length"]?.ToObject<int?>() ?? 0;
                int aLen = aCol["length"]?.ToObject<int?>() ?? 0;
                int bDec = bCol["decimals"]?.ToObject<int?>() ?? 0;
                int aDec = aCol["decimals"]?.ToObject<int?>() ?? 0;
                bool bNull = bCol["nullable"]?.ToObject<bool?>() ?? false;
                bool aNull = aCol["nullable"]?.ToObject<bool?>() ?? false;

                if (TypeFamily(bType) != TypeFamily(aType))
                {
                    changes.Add(MakeChange(aName, "type", RenderColumnDef(bCol), RenderColumnDef(aCol),
                        destructive: true,
                        warning: Warning(aName, $"Type conversion {TypeFamily(bType)} → {TypeFamily(aType)} on column '{aName}' may fail or lose data. Verify values before reorganizing.")));
                }
                else if (aDec < bDec)
                {
                    // Precision shrink (NUMERIC(18,2) → NUMERIC(18,0)) can truncate
                    // fractional data — treated as destructive like a length shrink.
                    changes.Add(MakeChange(aName, "decimals", RenderColumnDef(bCol), RenderColumnDef(aCol),
                        destructive: true,
                        warning: Warning(aName, $"Decimal precision shrink on column '{aName}' ({bDec} → {aDec} decimals) may truncate fractional values.")));
                }
                else if (aLen < bLen)
                {
                    changes.Add(MakeChange(aName, "length", RenderColumnDef(bCol), RenderColumnDef(aCol),
                        destructive: true,
                        warning: Warning(aName, $"Length shrink on column '{aName}' ({bLen} → {aLen}) may truncate existing values.")));
                }
                else if (aLen != bLen || aDec != bDec)
                {
                    changes.Add(MakeChange(aName, "length", RenderColumnDef(bCol), RenderColumnDef(aCol), destructive: false));
                }

                if (bNull && !aNull)
                {
                    changes.Add(MakeChange(aName, "nullable", RenderColumnDef(bCol), RenderColumnDef(aCol),
                        destructive: true,
                        warning: Warning(aName, $"Column '{aName}' becomes NOT NULL; the reorg may fail if NULL rows exist.")));
                }
                else if (!bNull && aNull)
                {
                    changes.Add(MakeChange(aName, "nullable", RenderColumnDef(bCol), RenderColumnDef(aCol), destructive: false));
                }
            }

            foreach (var bCol in beforeByName.Values)
            {
                string bName = bCol["name"]?.ToString() ?? "";
                if (!afterByName.ContainsKey(bName))
                {
                    changes.Add(new JObject
                    {
                        ["column"] = bName,
                        ["field"] = "dropped",
                        ["before"] = RenderColumnDef(bCol),
                        ["after"] = (JToken)"<absent>",
                        ["destructive"] = true,
                        ["warning"] = Warning(bName, $"Column '{bName}' is dropped from the model: existing data in this column would be lost on reorg.")
                    });
                }
            }

            return changes;
        }

        private static JObject MakeChange(string column, string field, string before, string after, bool destructive, JObject warning = null)
        {
            var o = new JObject
            {
                ["column"] = column,
                ["field"] = field,
                ["before"] = before,
                ["after"] = after,
                ["destructive"] = destructive
            };
            if (warning != null) o["warning"] = warning;
            return o;
        }

        // Structured destructive warning shape shared by every branch, so consumers
        // can rely on warning.message regardless of which field changed.
        private static JObject Warning(string column, string message)
        {
            return new JObject
            {
                ["column"] = column,
                ["severity"] = "destructive",
                ["message"] = message
            };
        }

        /// <summary>
        /// Normalize a GeneXus type name to a comparison family: "NUMERIC(8,0)",
        /// "Numeric" → "numeric"; "CHARACTER", "VARCHAR(40)" → "character"; etc.
        /// </summary>
        internal static string TypeFamily(string typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName)) return "unknown";
            string upper = typeName.ToUpperInvariant().Trim();
            // Strip any parenthesized precision.
            int paren = upper.IndexOf('(');
            if (paren > 0) upper = upper.Substring(0, paren).Trim();

            // NOTE: LONGVARCHAR (GeneXus LongVarChar) must NOT land here — the
            // character branch below catches it via Contains("VARCHAR").
            if (upper.Contains("NUMERIC") || upper.Contains("DECIMAL") || upper == "INT" ||
                upper.Contains("BIGINT") || upper.Contains("INTEGER") || upper.Contains("FLOAT") ||
                upper.Contains("DOUBLE") || upper.Contains("MONEY"))
                return "numeric";
            if (upper.Contains("CHAR") || upper.Contains("VARCHAR") || upper.Contains("STRING") || upper.Contains("TEXT"))
                return "character";
            if (upper.Contains("DATE") || upper.Contains("TIME") || upper.Contains("DATETIME"))
                return "date";
            if (upper.Contains("GUID") || upper.Contains("UUID"))
                return "guid";
            if (upper.Contains("BOOLEAN") || upper.Contains("LOGICAL"))
                return "boolean";
            return upper.Length > 0 ? upper : "unknown";
        }

        /// <summary>
        /// Render one column as a human-readable definition: "NUMERIC(18) NOT NULL".
        /// Matches the issue #61 example shape (before/after comparison strings).
        /// </summary>
        internal static string RenderColumnDef(JObject col)
        {
            if (col == null) return "<absent>";
            string type = col["type"]?.ToString() ?? "Unknown";
            int length = col["length"]?.ToObject<int?>() ?? 0;
            int decimals = col["decimals"]?.ToObject<int?>() ?? 0;
            bool nullable = col["nullable"]?.ToObject<bool?>() ?? false;

            string def = type;
            if (length > 0)
                def += decimals > 0 ? $"({length},{decimals})" : $"({length})";
            return def + (nullable ? " NULL" : " NOT NULL");
        }

        /// <summary>
        /// Render a compact heuristic CREATE TABLE from "after" columns — the
        /// desired-schema shape, labeled heuristic (never authoritative reorg DDL).
        /// </summary>
        internal static string RenderCreateTable(string tableName, JArray columns)
        {
            if (string.IsNullOrWhiteSpace(tableName) || columns == null || columns.Count == 0)
                return null;

            var lines = new List<string>();
            var pk = new List<string>();
            foreach (var c in columns.OfType<JObject>())
            {
                string name = c["name"]?.ToString() ?? "";
                if (string.IsNullOrWhiteSpace(name)) continue;
                string def = RenderColumnDef(c);
                string quotedName = QuoteSqlIdentifier(name);
                lines.Add($"  {quotedName} {def}");
                if (c["isKey"]?.ToObject<bool?>() ?? false) pk.Add(quotedName);
            }
            if (lines.Count == 0) return null;
            if (pk.Count > 0) lines.Add($"  PRIMARY KEY ({string.Join(", ", pk)})");
            return "CREATE TABLE " + QuoteSqlIdentifier(tableName) + " (\n" + string.Join(",\n", lines) + "\n);";
        }

        private static string QuoteSqlIdentifier(string value)
        {
            return "[" + (value ?? string.Empty).Replace("]", "]]") + "]";
        }

        private void ApplyDeepAnalysis(KBModel model, JObject result, bool deep)
        {
            if (!deep) return;
            var spec = SdkServiceLocator.ConstructOrResolve<GenexusServices.ISpecifierService>(
                () => new Artech.Packages.Specifier.Services.SpecifierService());
            if (spec == null)
            {
                result["deepError"] = "ISpecifierService unavailable";
                return;
            }
            try
            {
                // ImpactAnalysis | CreateAnalysis: analyse the DB impact without executing a reorg.
                var options = GenexusCommands.BuildOptions.ImpactAnalysis | GenexusCommands.BuildOptions.CreateAnalysis;
                var analysis = spec.ImpactDatabase(model, options);
                result["deepAnalysis"] = analysis.ToString();
                result["reorgNeeded"] = string.Equals(analysis.ToString(), "ReorganizationNeeded", StringComparison.OrdinalIgnoreCase);
                result["deepNote"] = "Ran ISpecifierService.ImpactDatabase (specification). AnalysisResult enum reported.";
                // Plan 070: surface the expected runtime so callers don't mistake a
                // multi-minute specification pass for a hung worker — the gateway's
                // sync ceiling for deep=true calls is 10 minutes (600s).
                result["runtimeNote"] = DeepImpactRuntimeNote;
            }
            catch (Exception ex)
            {
                result["deepError"] = ex.Message;
            }
        }

        private void AttachSqlPreview(JObject result, bool deep, bool likely, DateTime impactStartedUtc, string propertyName)
        {
            JObject plan;
            try
            {
                bool currentRun;
                string artifact = ReorgSqlPreview.FindLatestArtifact(_kb.GetKbPath(), impactStartedUtc, out currentRun);
                if (!string.IsNullOrEmpty(artifact))
                {
                    bool exact = deep && currentRun;
                    plan = ReorgSqlPreview.Parse(File.ReadAllText(artifact), exact);
                    plan["artifact"] = artifact;
                    plan["artifactFromCurrentImpact"] = currentRun;
                    plan["source"] = exact ? "sdk:ImpactDatabase generated artifact" : "existing generated artifact";
                    plan["beforeDdl"] = JValue.CreateNull();
                    plan["proposedDdl"] = plan["ddl"].DeepClone();
                    if (!exact)
                        plan["warning"] = "The SQL artifact was not generated by this preview run; it is shown as cached evidence and is not labelled effective DDL.";
                }
                else
                {
                    plan = EmptySqlPreview(deep
                        ? "Impact Analysis completed but no reorganization SQL artifact was found below the KB path; exact DDL is unavailable."
                        : "No cached reorganization SQL artifact was found. Re-run with deep=true for non-mutating Impact Analysis.");
                }
            }
            catch (Exception ex)
            {
                plan = EmptySqlPreview("A reorganization SQL artifact was found but could not be read: " + ex.Message);
            }

            result[propertyName] = plan;
            string deepVerdict = result["deepAnalysis"]?.ToString() ?? result["deep"]?["deepAnalysis"]?.ToString();
            JObject verdictEvidence = deep ? ReorgSqlPreview.AssessVerdict(deepVerdict, plan["ddlEffective"]?.ToObject<bool?>() ?? false) : null;
            if (verdictEvidence != null) result["verdictEvidence"] = verdictEvidence;
            if (string.Equals(propertyName, "preview", StringComparison.OrdinalIgnoreCase))
            {
                result["nonMutating"] = true;
                result["logicalPhysicalDivergence"] = likely;
            }
        }

        private static JObject EmptySqlPreview(string warning)
        {
            return new JObject
            {
                ["ddlEffective"] = false,
                ["ddl"] = new JArray(),
                ["affectedTables"] = new JArray(),
                ["affectedColumns"] = new JArray(),
                ["indexes"] = new JArray(),
                ["destructiveConversions"] = new JArray(),
                ["warning"] = warning
            };
        }

        private static DateTime SafeDate(Func<DateTime> f) { try { return f(); } catch { return default; } }
        private static JToken Iso(DateTime d) => d == default ? (JToken)JValue.CreateNull() : d.ToUniversalTime().ToString("o");
        private static string SafeString(Func<string> f) { try { return f(); } catch { return null; } }
        private static bool SafeBool(Func<bool> f, bool fallback) { try { return f(); } catch { return fallback; } }
    }
}
