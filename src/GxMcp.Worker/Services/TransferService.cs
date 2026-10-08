using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Xml;
using System.Xml.Linq;
using GxMcp.Worker.Helpers;
using Artech.Architecture.Common.Objects;
using Artech.Architecture.Common.Services;
using GxMcp.Worker.Models;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// genexus_transfer — real XPZ export / import over the SDK's
    /// <c>IKnowledgeManagerService</c> (P0 #1). Unlike genexus_io / genexus_kb_import
    /// (filesystem part-file copies that don't resolve dependencies), this is the IDE
    /// Export/Import code path: dependency-aware, identity-mapped.
    ///
    /// Actions:
    ///   • export  — targets[] + outputFile → dependency-aware .xpz. Read of KB, writes a file.
    ///   • inspect — explore an .xpz (ExploreExport) without importing. Read-only.
    ///   • import  — apply an .xpz into the active KB. DESTRUCTIVE; dryRun defaults true
    ///               (dryRun=true is an inspect); dryRun=false requires confirm=true.
    ///
    /// <c>IKnowledgeManagerService</c> implements <c>IGxService</c> → resolved via the
    /// generic <see cref="SdkServiceResolver"/>. Missing service → clean <c>*Unavailable</c>.
    /// </summary>
    public class TransferService
    {
        private readonly KbService _kb;
        private readonly ObjectService _objects;
        private readonly IndexCacheService _indexCache;
        private readonly WriteService _writeService;

        public TransferService(KbService kb, ObjectService objects, IndexCacheService indexCache = null,
            WriteService writeService = null)
        {
            _kb = kb;
            _objects = objects;
            _indexCache = indexCache;
            _writeService = writeService;
        }

        public string Run(JObject args)
        {
            string action = (args?["action"]?.ToString() ?? "").Trim().ToLowerInvariant();
            if (action != "export" && action != "import" && action != "inspect")
                return McpResponse.Err(
                    code: "BadAction",
                    message: "Unknown action '" + action + "'. Expected export, inspect, or import.",
                    hint: "genexus_transfer action=export|inspect|import.");

            if (!KbModelGuard.TryGetDesignModel(_kb, out var model, out var kbErr))
                return kbErr;

            var svc = SdkServiceResolver.Resolve<IKnowledgeManagerService>();
            if (svc == null)
                return McpResponse.Err(
                    code: "KnowledgeManagerServiceUnavailable",
                    message: "The GeneXus SDK's IKnowledgeManagerService is not registered in this worker session.",
                    hint: "Restart the worker (genexus_worker_reload mode=hard) and retry.");

            try
            {
                if (action == "export") return Export(svc, model, args);
                if (action == "inspect") return Inspect(svc, model, args, isDryRunImport: false);
                return Import(svc, model, args);
            }
            catch (Exception ex)
            {
                return McpResponse.Err(code: "TransferFailed", message: ex.Message, hint: "Check the worker log for the full stack trace.");
            }
        }

        private string Export(IKnowledgeManagerService svc, KBModel model, JObject args)
        {
            string outputFile = args?["outputFile"]?.ToString();
            if (string.IsNullOrWhiteSpace(outputFile))
                return McpResponse.Err(code: "BadArgs", message: "action=export requires outputFile.", hint: "Pass outputFile=<absolute .xpz path>.");

            var targets = args?["targets"] as JArray;
            if (targets == null || targets.Count == 0)
                return McpResponse.Err(code: "BadArgs", message: "action=export requires targets[] (object names).", hint: "Pass targets=[\"ObjName1\",\"ObjName2\"].");

            string typeFilter = args?["type"]?.ToString();
            bool includeDependencies = args?["includeDependencies"]?.ToObject<bool?>()
                                    ?? args?["withDependencies"]?.ToObject<bool?>()
                                    ?? false;

            var objs = new List<KBObject>();
            var missing = new JArray();
            var lookupErrors = new JArray();
            foreach (var t in targets)
            {
                string name = t?.ToString();
                if (string.IsNullOrWhiteSpace(name)) continue;
                KBObject o = null;
                try { o = _objects?.FindObject(name, typeFilter); }
                catch (Exception ex) { lookupErrors.Add(new JObject { ["name"] = name, ["error"] = ex.Message }); continue; }
                if (o == null) missing.Add(name); else objs.Add(o);
            }

            if (objs.Count == 0)
                return McpResponse.Err(
                    code: "ObjectsNotFound",
                    message: "None of the requested objects were found.",
                    hint: "Check the names (genexus_query).",
                    target: string.Join(",", missing),
                    errorExtra: lookupErrors.Count > 0 ? new JObject { ["lookupErrors"] = lookupErrors } : null);

            int seedCount = objs.Count;
            var resolvedDependencies = new List<string>();

            if (includeDependencies && _indexCache != null)
            {
                var index = _indexCache.GetIndex();
                if (index != null && index.Objects != null)
                {
                    var visitedGuids = new HashSet<Guid>(objs.Select(o => o.Guid));
                    var visitedNames = new HashSet<string>(objs.Select(o => o.Name), StringComparer.OrdinalIgnoreCase);
                    var queue = new Queue<KBObject>(objs);

                    while (queue.Count > 0)
                    {
                        var current = queue.Dequeue();
                        string typeName = current.TypeDescriptor?.Name ?? "Object";
                        string storageKey = typeName + ":" + current.Name;

                        SearchIndex.IndexEntry entry = null;
                        if (!index.Objects.TryGetValue(storageKey, out entry))
                        {
                            entry = index.FindByName(current.Name).FirstOrDefault();
                        }

                        if (entry != null)
                        {
                            var depNames = new List<string>();
                            if (entry.Calls != null) depNames.AddRange(entry.Calls);
                            if (entry.Tables != null) depNames.AddRange(entry.Tables);

                            foreach (var depName in depNames)
                            {
                                if (string.IsNullOrWhiteSpace(depName) || visitedNames.Contains(depName)) continue;
                                visitedNames.Add(depName);

                                KBObject depObj = null;
                                try { depObj = _objects?.FindObject(depName); } catch { }
                                if (depObj != null && !visitedGuids.Contains(depObj.Guid))
                                {
                                    visitedGuids.Add(depObj.Guid);
                                    objs.Add(depObj);
                                    resolvedDependencies.Add(depObj.Name);
                                    queue.Enqueue(depObj);
                                }
                            }
                        }
                    }
                }
            }

            var options = SilentExportOptions();
            bool ok = svc.Export(model, objs, outputFile, options);

            return McpResponse.Ok(
                code: ok ? "TransferExported" : "TransferExportDeclined",
                result: new JObject
                {
                    ["success"] = ok,
                    ["outputFile"] = outputFile,
                    ["exportedCount"] = objs.Count,
                    ["seedCount"] = seedCount,
                    ["includeDependencies"] = includeDependencies,
                    ["dependenciesAdded"] = resolvedDependencies.Count,
                    ["resolvedDependencies"] = new JArray(resolvedDependencies),
                    ["notFound"] = missing,
                    ["lookupErrors"] = lookupErrors,
                    ["dependencyAware"] = true,
                    ["source"] = "sdk:IKnowledgeManagerService.Export"
                });
        }

        private string Inspect(IKnowledgeManagerService svc, KBModel model, JObject args, bool isDryRunImport)
        {
            string file = args?["file"]?.ToString() ?? args?["inputPath"]?.ToString();
            if (string.IsNullOrWhiteSpace(file))
                return McpResponse.Err(code: "BadArgs", message: "action=inspect requires file.", hint: "Pass file=<absolute .xpz path>.");
            if (!System.IO.File.Exists(file))
                return McpResponse.Err(code: "FileNotFound", message: "XPZ file not found: " + file, hint: "Pass an absolute path to an existing .xpz.");

            var opts = new ExploreExportOptions();
            svc.ExploreExport(file, model, opts, out var objects, out var actions, out var idMap);

            var items = new JArray();
            bool identityMissing = false;
            var exportItems = AsEnumerable(objects).ToList();
            foreach (var o in exportItems)
            {
                var descriptor = DescribeExportItem(o);
                identityMissing |= !(descriptor["identityAvailable"]?.Value<bool>() ?? false);
                items.Add(descriptor);
            }

            if (exportItems.Count > 0 && identityMissing)
                return McpResponse.Err(
                    code: "TransferImportPreviewUnavailable",
                    message: "The SDK exposed XPZ entries but did not expose a stable object identity; no import was attempted.",
                    hint: "Use an XPZ produced by the GeneXus Export path supported by this Worker and retry inspect first.");

            var result = new JObject
            {
                ["file"] = file,
                ["objectCount"] = exportItems.Count,
                ["actionCount"] = Math.Max(Count(actions), exportItems.Count),
                ["packageActionCount"] = Count(actions),
                ["objects"] = items,
                ["wouldImport"] = isDryRunImport,
                ["source"] = "sdk:IKnowledgeManagerService.ExploreExport"
            };
            if (isDryRunImport) AddImportPreviewGuidance(result, args);

            return McpResponse.Ok(
                code: isDryRunImport ? "TransferImportPreview" : "TransferInspected",
                result: result);
        }

        /// <summary>
        /// An import that ran as a preview (dryRun omitted or true) changed nothing. Say so,
        /// and say how to really import; confirm=true alone does NOT import.
        /// </summary>
        internal static void AddImportPreviewGuidance(JObject result, JObject args)
        {
            bool confirm = args?["confirm"]?.ToObject<bool?>() ?? false;
            bool dryRunExplicit = args?["dryRun"]?.Type == JTokenType.Boolean;
            result["previewOnly"] = true;
            result["imported"] = false;
            result["nextAction"] = new JObject
            {
                ["tool"] = "genexus_transfer",
                ["args"] = new JObject { ["action"] = "import", ["file"] = args?["file"] ?? args?["inputPath"], ["dryRun"] = false, ["confirm"] = true }
            };
            if (confirm && !dryRunExplicit)
                result["warning"] = "confirm=true alone does NOT import: dryRun defaults to true, so this call was a preview and NOTHING was imported into the KB.";
            result["hint"] = "Preview only; nothing was imported. To apply, re-issue action=import with dryRun=false and confirm=true.";
        }

        internal static JObject DescribeExportItem(object raw)
        {
            var native = raw as KBObject;
            string name = native?.Name ?? ReadExportProperty(raw, "Name", "ObjectName", "QualifiedName");
            string type = native?.TypeDescriptor?.Name ?? ReadExportProperty(raw, "TypeName", "ObjectType", "Type");
            string qualifiedName = ReadExportProperty(raw, "QualifiedName", "FullName");
            string displayName = ReadExportProperty(raw, "DisplayName", "Caption");
            string guid = native != null ? native.Guid.ToString("D") : ReadExportProperty(raw, "Guid", "ObjectGuid", "Id");
            string baseOperation = ReadExportProperty(raw, "BaseOperation", "Operation");
            string status = ReadExportProperty(raw, "Status", "State");
            string typeDescriptor = ReadExportProperty(raw, "TypeDescriptor");
            bool identityAvailable = !string.IsNullOrWhiteSpace(name) || !string.IsNullOrWhiteSpace(guid);
            return new JObject
            {
                ["name"] = name,
                ["type"] = type,
                ["qualifiedName"] = qualifiedName,
                ["displayName"] = displayName,
                ["guid"] = guid,
                ["baseOperation"] = baseOperation,
                ["status"] = status,
                ["typeDescriptor"] = typeDescriptor,
                ["identityAvailable"] = identityAvailable,
                ["sdkItemType"] = raw?.GetType().FullName
            };
        }

        private static string ReadExportProperty(object raw, params string[] names)
        {
            if (raw == null) return null;
            foreach (string name in names)
            {
                try
                {
                    var property = raw.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    var value = property?.GetValue(raw, null);
                    if (value is KBObject obj) return obj.Name;
                    string text = value?.ToString();
                    if (!string.IsNullOrWhiteSpace(text) && !string.Equals(text, raw.GetType().Name, StringComparison.Ordinal))
                        return text;
                }
                catch { }
            }
            return null;
        }

        private string Import(IKnowledgeManagerService svc, KBModel model, JObject args)
        {
            string file = args?["file"]?.ToString() ?? args?["inputPath"]?.ToString();
            if (string.IsNullOrWhiteSpace(file))
                return McpResponse.Err(code: "BadArgs", message: "action=import requires file.", hint: "Pass file=<absolute .xpz path>.");
            if (!System.IO.File.Exists(file))
                return McpResponse.Err(code: "FileNotFound", message: "XPZ file not found: " + file, hint: "Pass an absolute path to an existing .xpz.");

            // dryRun defaults TRUE — an import mutates the KB. dryRun=true previews via ExploreExport.
            bool dryRun = args?["dryRun"]?.ToObject<bool?>() ?? true;
            if (dryRun) return Inspect(svc, model, args, isDryRunImport: true);

            bool confirm = args?["confirm"]?.ToObject<bool?>() ?? false;
            if (!confirm)
                return McpResponse.Err(
                    code: "ConfirmRequired",
                    message: "action=import with dryRun=false requires confirm=true (it mutates the KB).",
                    hint: "Preview first with dryRun=true, then pass confirm=true to apply.");

            ImportOptions options;
            try
            {
                options = SilentImportOptions(args);
            }
            catch (Exception ex)
            {
                return McpResponse.Err(
                    code: "InvalidTransferOptions",
                    message: "The requested XPZ import options could not be applied; no import was attempted. " + ex.Message,
                    hint: "Use the supported conflict/theme option values or omit them to use the safe defaults.");
            }
            using (var diagnostics = new TransferDiagnostics(CommonServices.IsOutputAvailable ? CommonServices.Output : null))
            {
            ImportFidelityPlan fidelityPlan;
            try
            {
                fidelityPlan = CaptureImportFidelity(svc, model, file, options);
            }
            catch (Exception ex)
            {
                return McpResponse.Err(
                    code: "TransferImportVerificationUnavailable",
                    message: "The XPZ could not be inspected for WebForm preservation; no import was attempted. " + ex.Message,
                    hint: "Retry after the XPZ is readable by the GeneXus SDK.",
                    extra: ImportDiagnostics(diagnostics, imported: false));
            }

            bool ok;
            try { ok = svc.ImportFile(file, model, options); }
            catch (Exception ex)
            {
                diagnostics.Record("Error", ex.Message, ex.GetType().Name);
                return McpResponse.Err(code: "TransferImportFailed", message: "The SDK import threw an exception.",
                    hint: "Inspect SDK diagnostics and persisted objects before retrying; preview is not proof of import.",
                    extra: ImportDiagnostics(diagnostics, imported: null));
            }

            if (!ok)
            {
                return ImportDeclined(file, diagnostics);
            }

            var fidelity = VerifyImportedWebForms(fidelityPlan);
            if (!fidelity.Verified)
            {
                return McpResponse.Err(
                    code: "TransferImportFidelityFailed",
                    message: "The XPZ import completed, but one or more WebForm parts did not survive the SDK import unchanged.",
                    hint: "The affected existing objects were restored when possible; inspect the fidelity block before retrying.",
                    extra: new JObject
                    {
                        ["imported"] = true,
                        ["file"] = file,
                        ["sdkDiagnostics"] = diagnostics.Messages,
                        ["fidelityVerified"] = false,
                        ["fidelity"] = fidelity.Result
                    });
            }

            return McpResponse.Ok(
                code: "TransferImported",
                result: new JObject
                {
                    ["success"] = true,
                    ["file"] = file,
                    ["source"] = "sdk:IKnowledgeManagerService.ImportFile",
                    ["sdkDiagnostics"] = diagnostics.Messages,
                    ["fidelityVerified"] = true,
                    ["fidelity"] = fidelity.Result
                });
            }
        }

        internal static string ImportDeclined(string file, TransferDiagnostics diagnostics)
        {
            return McpResponse.Err(
                    code: "TransferImportDeclined",
                    message: "GeneXus ImportFile returned false; import was not confirmed.",
                    hint: "Inspect sdkDiagnostics; an empty list means the SDK supplied no output diagnostic. Verify persisted objects before retrying.",
                    extra: new JObject
                    {
                        ["success"] = false,
                        ["file"] = file,
                        ["source"] = "sdk:IKnowledgeManagerService.ImportFile",
                        ["imported"] = false,
                        ["sdkDiagnostics"] = diagnostics.Messages,
                        ["diagnosticsAvailable"] = diagnostics.Available,
                        ["diagnosticsTruncated"] = diagnostics.Truncated,
                        ["fidelityVerified"] = false,
                        ["fidelity"] = new JObject { ["objectsChecked"] = 0 }
                    });
        }

        private static JObject ImportDiagnostics(TransferDiagnostics diagnostics, bool? imported)
        {
            return new JObject { ["imported"] = imported, ["success"] = false,
                ["sdkDiagnostics"] = diagnostics.Messages, ["diagnosticsAvailable"] = diagnostics.Available,
                ["diagnosticsTruncated"] = diagnostics.Truncated };
        }

        private ImportFidelityPlan CaptureImportFidelity(IKnowledgeManagerService svc, KBModel model,
            string file, ImportOptions options)
        {
            var plan = new ImportFidelityPlan();
            // IExportItem.Object is guarded by PrepareImport in GeneXus 18 U5.
            // Read the source WebForm from the XPZ package before preparing the
            // item; otherwise the SDK exposes only its normalized projection and
            // the fidelity check becomes circular (issue #102).
            var exportedWebForms = ReadExportWebForms(file);
            var exploreOptions = new ExploreExportOptions();
            svc.ExploreExport(file, model, exploreOptions, out var exportedObjects, out _, out _);
            var candidates = AsEnumerable(exportedObjects).ToList();
            // Validate the package's stable metadata before invoking PrepareImport.
            // Some GeneXus SDKs perform normalization while preparing an item, so
            // an unreadable/anonymous XPZ must fail before that call can touch the KB.
            foreach (var raw in candidates)
            {
                var descriptor = DescribeExportItem(raw);
                if (!(descriptor["identityAvailable"]?.Value<bool>() ?? false))
                    throw new InvalidDataException("The XPZ contains an import item without a stable object identity.");
            }
            if (candidates.Count == 0 && exportedWebForms.Count > 0)
                throw new InvalidDataException("The XPZ contains raw WebForm payloads but the SDK exposed no import candidates for them.");
            var prepared = svc.PrepareImport(file, model, options);
            if (candidates.Count == 0)
                candidates = AsEnumerable(prepared?.Items).ToList();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool sawWebFormCandidate = false;

            foreach (var raw in candidates)
            {
                var rawDescriptor = DescribeExportItem(raw);
                if (!(rawDescriptor["identityAvailable"]?.Value<bool>() ?? false))
                    throw new InvalidDataException("The XPZ contains an import item without a stable object identity.");
                var item = raw as IExportItem;
                if (item == null) continue;

                // Object is intentionally accessed only after PrepareImport: U5
                // throws when the guarded getter is used earlier.
                item.PrepareImport(item.BaseModel ?? model, model, prepared);
                var source = item.Object;
                string sourceType = source?.TypeDescriptor?.Name;
                if (source == null) continue;
                var sourcePart = WebFormXmlHelper.GetWebFormPart(source);
                // GetWebFormPart also returns a Procedure's print layout, which the XPZ
                // serializes outside <Source>; it is not the WebForm payload this check guards.
                if (sourcePart == null || !IsWebFormFidelityCandidate(sourcePart.GetType().FullName)) continue;
                sawWebFormCandidate = true;

                if (string.IsNullOrWhiteSpace(source.Name)
                    || !exportedWebForms.TryGetValue(source.Name, out string expectedXml)
                    || string.IsNullOrWhiteSpace(expectedXml))
                    throw new InvalidDataException(
                        "The XPZ contains a WebForm candidate without a readable raw WebForm payload for '"
                        + (source.Name ?? "<unnamed>") + "'. Fidelity verification cannot use the SDK projection as a baseline.");

                string typeFilter = sourceType ?? source.TypeDescriptor?.Name;
                string partName = sourcePart.TypeDescriptor?.Name ?? "WebForm";
                string key = (typeFilter ?? string.Empty) + "|" + source.Name + "|" + partName;
                if (!seen.Add(key)) continue;

                var existing = _objects?.FindObjectFresh(source.Name, typeFilter);
                plan.Items.Add(new ImportWebFormSnapshot
                {
                    Name = source.Name,
                    TypeFilter = typeFilter,
                    PartName = partName,
                    ExpectedXml = expectedXml,
                    ExistingBefore = existing != null,
                    BeforeXml = existing == null ? null : WebFormXmlHelper.ReadEditableXml(existing)
                });
            }

            if (exportedWebForms.Count > 0 && !sawWebFormCandidate)
                throw new InvalidDataException("The XPZ contains raw WebForm payloads, but the SDK could not map them to import candidates.");
            if (sawWebFormCandidate && plan.Items.Count == 0)
                throw new InvalidDataException("The XPZ exposed WebForm objects but no raw WebForm payload could be mapped.");

            return plan;
        }

        internal static bool IsWebFormFidelityCandidate(string partClassFullName)
            => !ReportLayoutHelper.IsReportPartTypeName(partClassFullName);

        internal static Dictionary<string, string> ReadExportWebForms(string file)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(file) || !File.Exists(file)) return result;

            const int maxXmlEntries = 2048;
            const long maxEntryBytes = 8L * 1024 * 1024;
            const long maxTotalBytes = 64L * 1024 * 1024;
            int xmlEntries = 0;
            long totalBytes = 0;

            using (var archive = ZipFile.OpenRead(file))
            {
                foreach (var entry in archive.Entries.Where(e =>
                    e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)))
                {
                    if (++xmlEntries > maxXmlEntries)
                        throw new InvalidDataException("The XPZ contains too many XML entries for safe fidelity inspection.");
                    if (entry.Length > maxEntryBytes || (totalBytes += entry.Length) > maxTotalBytes)
                        throw new InvalidDataException("The XPZ XML payload exceeds the safe fidelity-inspection limit.");

                    XDocument document;
                    using (var stream = entry.Open())
                    using (var reader = XmlReader.Create(stream, new XmlReaderSettings
                    {
                        DtdProcessing = DtdProcessing.Prohibit,
                        XmlResolver = null,
                        MaxCharactersFromEntities = 0
                    }))
                        document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);

                    foreach (var obj in document.Descendants().Where(e => string.Equals(e.Name.LocalName, "Object", StringComparison.OrdinalIgnoreCase)))
                    {
                        string name = obj.Attributes().FirstOrDefault(a =>
                            string.Equals(a.Name.LocalName, "name", StringComparison.OrdinalIgnoreCase))?.Value;
                        if (string.IsNullOrWhiteSpace(name)) continue;

                        foreach (var source in obj.Descendants().Where(e =>
                            string.Equals(e.Name.LocalName, "Source", StringComparison.OrdinalIgnoreCase)
                            && e.Parent != null
                            && string.Equals(e.Parent.Name.LocalName, "Part", StringComparison.OrdinalIgnoreCase)))
                        {
                            string xml = source.Value;
                            if (string.IsNullOrWhiteSpace(xml)) continue;
                            if (IsWebFormPayload(xml))
                            {
                                result[name] = xml.Trim();
                                break;
                            }
                        }
                    }
                }
            }

            return result;
        }

        private static bool IsWebFormPayload(string xml)
        {
            try
            {
                using (var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    MaxCharactersFromEntities = 0
                }))
                {
                    var root = XDocument.Load(reader, LoadOptions.PreserveWhitespace).Root;
                    string local = root?.Name.LocalName;
                    return string.Equals(local, "GxMultiForm", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(local, "BODY", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(local, "Layout", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch { return false; }
        }

        private ImportFidelityResult VerifyImportedWebForms(ImportFidelityPlan plan)
        {
            var mismatches = new JArray();
            int repaired = 0;
            bool rollbackAttempted = false;
            bool rollbackSucceeded = true;

            foreach (var expected in plan.Items)
            {
                var current = _objects?.FindObjectFresh(expected.Name, expected.TypeFilter);
                string actualXml = current == null ? string.Empty : WebFormXmlHelper.ReadEditableXml(current);
                string diff;
                if (XmlEquivalence.AreEquivalent(expected.ExpectedXml, actualXml, out diff)) continue;

                var mismatch = new JObject
                {
                    ["name"] = expected.Name,
                    ["part"] = expected.PartName,
                    ["initialDiff"] = diff ?? "n/a"
                };

                bool repairedHere = false;
                if (_writeService != null)
                {
                    string writeRaw = _writeService.WriteObject(
                        expected.Name,
                        expected.PartName,
                        expected.ExpectedXml,
                        expected.TypeFilter,
                        autoValidate: true,
                        preferFastSourceSave: false,
                        autoInjectVariables: true,
                        dryRun: false,
                        explicitBase64: false,
                        strictVerify: true);
                    JObject write = ParseObject(writeRaw);
                    repairedHere = IsSuccessfulWrite(write);
                    mismatch["repairResponse"] = write;

                    if (repairedHere)
                    {
                        var repairedObject = _objects?.FindObjectFresh(expected.Name, expected.TypeFilter);
                        string repairedXml = repairedObject == null ? string.Empty : WebFormXmlHelper.ReadEditableXml(repairedObject);
                        string repairedDiff;
                        repairedHere = XmlEquivalence.AreEquivalent(expected.ExpectedXml, repairedXml, out repairedDiff);
                        if (!repairedHere) mismatch["repairDiff"] = repairedDiff ?? "n/a";
                    }
                }

                if (repairedHere)
                {
                    repaired++;
                    mismatch["repaired"] = true;
                    continue;
                }

                mismatch["repaired"] = false;
                mismatches.Add(mismatch);
                rollbackAttempted = true;
                bool restoredOrDeleted = expected.ExistingBefore
                    ? TryRestoreImportedObject(expected)
                    : TryDeleteImportedObject(expected);
                mismatch["rollbackAction"] = expected.ExistingBefore ? "restore" : "delete_imported_object";
                mismatch["rollbackSucceeded"] = restoredOrDeleted;
                if (!restoredOrDeleted) rollbackSucceeded = false;
            }

            var result = new JObject
            {
                ["objectsChecked"] = plan.Items.Count,
                ["repaired"] = repaired,
                ["mismatches"] = mismatches,
                ["rollbackAttempted"] = rollbackAttempted,
                ["rollbackSucceeded"] = rollbackSucceeded
            };
            return new ImportFidelityResult
            {
                Verified = mismatches.Count == 0,
                Result = result
            };
        }

        private bool TryRestoreImportedObject(ImportWebFormSnapshot expected)
        {
            if (!expected.ExistingBefore || string.IsNullOrWhiteSpace(expected.BeforeXml) || _writeService == null)
                return false;

            string raw = _writeService.WriteObject(
                expected.Name,
                expected.PartName,
                expected.BeforeXml,
                expected.TypeFilter,
                autoValidate: true,
                preferFastSourceSave: false,
                autoInjectVariables: true,
                dryRun: false,
                explicitBase64: false,
                strictVerify: true);
            return IsSuccessfulWrite(ParseObject(raw));
        }

        private bool TryDeleteImportedObject(ImportWebFormSnapshot expected)
        {
            if (expected == null || _objects == null) return false;
            try
            {
                string raw = _objects.DeleteObject(expected.Name, expected.TypeFilter, confirm: true);
                var response = ParseObject(raw);
                if (!IsSuccessfulWrite(response) && !string.Equals(response["code"]?.ToString(), "ObjectDeleted", StringComparison.OrdinalIgnoreCase))
                    return false;
                return _objects.FindObjectFresh(expected.Name, expected.TypeFilter) == null;
            }
            catch { return false; }
        }

        private static JObject ParseObject(string raw)
        {
            try { return string.IsNullOrWhiteSpace(raw) ? new JObject() : JObject.Parse(raw); }
            catch { return new JObject { ["raw"] = raw }; }
        }

        private static bool IsSuccessfulWrite(JObject response)
        {
            string status = response?["status"]?.ToString();
            return string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "success", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "partial", StringComparison.OrdinalIgnoreCase);
        }

        private sealed class ImportFidelityPlan
        {
            public List<ImportWebFormSnapshot> Items { get; } = new List<ImportWebFormSnapshot>();
        }

        private sealed class ImportWebFormSnapshot
        {
            public string Name { get; set; }
            public string TypeFilter { get; set; }
            public string PartName { get; set; }
            public string ExpectedXml { get; set; }
            public bool ExistingBefore { get; set; }
            public string BeforeXml { get; set; }
        }

        private sealed class ImportFidelityResult
        {
            public bool Verified { get; set; }
            public JObject Result { get; set; }
        }

        // The SDK's incremental defaults can normalize visual XML while importing.
        // FullOverwrite is the lossless baseline; the post-import verifier still
        // repairs any SDK projection drift that survives the import call.
        internal static ImportOptions SilentImportOptions(JObject args)
        {
            ImportOptions o = null;
            // FullOverwrite is the SDK mode that preserves the complete exported
            // WebForm payload (including GxWidth/GxHeight) when overwriting an
            // existing object.  Default performs incremental integration and was
            // the direct cause of the reported WebForm regression.
            try { o = ImportOptions.FullOverwrite; } catch { }
            if (o == null)
            {
                try { o = ImportOptions.Default; } catch { }
            }
            if (o == null) o = new ImportOptions();

            try { o.AutomaticBackup = false; } catch { }
            try { o.RollBackOnError = true; } catch { }
            try { o.AutomaticRollbackOnCancel = true; } catch { }
            o.OutputLevel = System.Diagnostics.TraceLevel.Verbose;
            o.ShowPropertiesErrors = true;

            string classConflicts = args?["classConflicts"]?.ToString();
            if (!string.IsNullOrWhiteSpace(classConflicts))
            {
                if (!string.Equals(classConflicts, "UseExisting", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(classConflicts, "UseFromExport", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("classConflicts must be UseExisting or UseFromExport.");
                if (!SetEnumValue(o, "ClassConflicts", classConflicts))
                    throw new InvalidOperationException("The installed GeneXus SDK does not expose a writable ClassConflicts option.");
            }

            string themeImportBehavior = args?["themeImportBehavior"]?.ToString();
            if (!string.IsNullOrWhiteSpace(themeImportBehavior))
            {
                if (!string.Equals(themeImportBehavior, "IncrementalIntegration", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(themeImportBehavior, "Overwrite", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("themeImportBehavior must be IncrementalIntegration or Overwrite.");
                if (!SetEnumValue(o, "ThemeImportBehavior", themeImportBehavior))
                    throw new InvalidOperationException("The installed GeneXus SDK does not expose a writable ThemeImportBehavior option.");
            }

            return o;
        }

        internal static string ResolveImportClassConflicts(JObject args)
        {
            return string.Equals(args?["classConflicts"]?.ToString(), "UseExisting", StringComparison.OrdinalIgnoreCase)
                ? "UseExisting"
                : "UseFromExport";
        }

        internal static string ResolveImportThemeBehavior(JObject args)
        {
            return string.Equals(args?["themeImportBehavior"]?.ToString(), "IncrementalIntegration", StringComparison.OrdinalIgnoreCase)
                ? "IncrementalIntegration"
                : "Overwrite";
        }

        private static bool SetEnumValue(object target, string propertyName, string enumValueName)
        {
            try
            {
                if (target == null) return false;
                var prop = target.GetType().GetProperty(propertyName);
                if (prop == null || !prop.PropertyType.IsEnum || !prop.CanWrite) return false;
                var val = Enum.Parse(prop.PropertyType, enumValueName, true);
                prop.SetValue(target, val, null);
                return true;
            }
            catch { return false; }
        }

        // A fresh ExportOptions with the dialog-free defaults the SDK uses for silent exports;
        // falls back to a plain instance if the static isn't available.
        private static ExportOptions SilentExportOptions()
        {
            try { var d = ExportOptions.SilentDefault; if (d != null) return d; } catch { }
            var o = new ExportOptions();
            try { o.IncludeReferencesDependencies = true; } catch { }
            try { o.ExportCurrentVersion = true; } catch { }
            try
            {
                var ucProp = typeof(ExportOptions).GetProperty("IncludeCustomUserControls");
                if (ucProp != null) ucProp.SetValue(o, true, null);
            }
            catch { }
            return o;
        }

        private static IEnumerable<object> AsEnumerable(object o)
        {
            if (o is IEnumerable e) foreach (var x in e) yield return x;
        }

        private static int Count(object o)
        {
            try { if (o is ICollection c) return c.Count; int n = 0; foreach (var _ in AsEnumerable(o)) n++; return n; }
            catch { return 0; }
        }
    }
}
