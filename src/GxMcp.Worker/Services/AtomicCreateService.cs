using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// Issue #62 — atomic create/update of an object with variables, rules,
    /// parameters, properties and Source in a SINGLE validated operation.
    ///
    /// The caller supplies the whole object definition at once; the service
    /// pre-validates every field BEFORE the first save — variable type names are
    /// syntax-checked through VariableTypeResolver AND reference-checked against the
    /// open KB (the issue #56 failure mode: a bare name that looks like a Domain/SDT/BC
    /// reference but doesn't exist), rules get a parenthesis sanity check, mode/type/
    /// name are validated — then mutates all parts and properties directly in-memory
    /// on the single KBObject instance and executes EnsureSave() exactly once.
    ///
    /// On any step failure it compensates so the operation is all-or-nothing:
    /// create mode → delete the freshly-created object; update mode → restore the
    /// pre-write snapshots (captured by this orchestrator for every part it touches)
    /// of the parts written so far. A failed call never leaves a partially-configured
    /// object behind.
    ///
    /// Dry-run previews the full plan with the same field validation and NO writes.
    /// validate=true reuses the issue #60 inline Specify pass (via
    /// <see cref="SaveSpecifyOrchestrator"/>) before confirming success, so a
    /// spec-invalid object is caught in the same call.
    ///
    /// Update mode carries optimistic version control: pass expectedVersion (the
    /// `version` token returned by a prior atomic create/update) and the call fails
    /// with ConcurrentModification when the object changed in between — the same
    /// intent as genexus_multi_agent_lock, but intrinsic to this operation.
    /// </summary>
    public class AtomicCreateService
    {
        private readonly ObjectService _objectService;
        private readonly WriteService _writeService;
        private readonly PropertyService _propertyService;
        private readonly SaveSpecifyOrchestrator _saveSpecifyOrchestrator;
        private readonly HistoryService _historyService;

        public AtomicCreateService(ObjectService objectService, WriteService writeService,
            PropertyService propertyService, SaveSpecifyOrchestrator saveSpecifyOrchestrator,
            HistoryService historyService)
        {
            _objectService = objectService;
            _writeService = writeService;
            _propertyService = propertyService;
            _saveSpecifyOrchestrator = saveSpecifyOrchestrator;
            _historyService = historyService;
        }

        // ── Parse + pure field validation (unit-testable, no KB access) ────────

        public sealed class ParsedSpec
        {
            public string Type;
            public string Name;
            public string Description;
            public string Mode = "auto";               // create | update | auto
            public string ExpectedVersion;             // update-mode optimistic token
            public bool? LegacyUpdateExisting;          // PR63 alias when mode is omitted
            public JArray Variables = new JArray();    // {varName, typeName?, length?, decimals?, collection?}
            public string RulesText = "";              // joined rules[] + rendered parms[]
            public string Source = "";
            public JObject Properties = new JObject(); // object-level property → value
            public bool Validate;                      // validate=true → inline Specify pass
            public bool RollbackOnFailure;
            public bool UpdateRequested;               // mode=update explicitly requested
            public JArray Errors = new JArray();       // {field, errors:[...]}
        }

        internal static JObject DescribeVariablePhase(int added, int existed, int failed, JArray outcomes)
        {
            return new JObject
            {
                ["status"] = failed == 0 ? "ok" : "error",
                ["counts"] = new JObject { ["added"] = added, ["existed"] = existed, ["failed"] = failed },
                ["outcomes"] = outcomes
            };
        }

        // Rule strings must be non-empty; a missing trailing ';' is a warning, not
        // an error (the IDE tolerates it), so it never blocks an otherwise valid plan.
        internal static void ValidateRules(ParsedSpec spec)
        {
            if (string.IsNullOrWhiteSpace(spec.RulesText)) return;
            string[] lines = spec.RulesText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var raw in lines)
            {
                string rule = raw.Trim();
                if (string.IsNullOrWhiteSpace(rule)) continue;
                int opens = rule.Count(c => c == '(');
                int closes = rule.Count(c => c == ')');
                if (opens != closes)
                {
                    spec.Errors.Add(new JObject
                    {
                        ["field"] = "rules",
                        ["errors"] = new JArray($"Rule '{rule}' has unbalanced parentheses ({opens} '(' vs {closes} ')').")
                    });
                }
            }
        }

        // Renders the `parms` array (["&Id", "out:&Msg", ...]) into a Parm rule
        // line. Types live on the variables, so entries are bare var names with an
        // optional in/out/inout: prefix.
        internal static string RenderParmRule(JArray parms)
        {
            if (parms == null || parms.Count == 0) return "";
            var parts = new System.Collections.Generic.List<string>();
            foreach (var p in parms)
            {
                string raw = p?.ToString();
                if (string.IsNullOrWhiteSpace(raw)) continue;
                raw = raw.Trim();
                string mode = "in";
                string name = raw;
                int colon = raw.IndexOf(':');
                if (colon > 0)
                {
                    string prefix = raw.Substring(0, colon).Trim().ToLowerInvariant();
                    if (prefix == "in" || prefix == "out" || prefix == "inout")
                    {
                        mode = prefix;
                        name = raw.Substring(colon + 1).Trim();
                    }
                }
                name = name.TrimStart('&');
                if (string.IsNullOrEmpty(name)) continue;
                parts.Add(mode + ":&" + name);
            }
            if (parts.Count == 0) return "";
            return "Parm(" + string.Join(", ", parts) + ");";
        }

        // Syntax-level variable validation: each entry needs a varName; a typeName
        // must resolve through the same resolver the write path uses. A bare name
        // ("IDManual") is a legitimate Domain/SDT/BC candidate, so existence in the KB
        // is checked separately by ValidateKbReferences (which has model access).
        internal static void ValidateVariables(ParsedSpec spec)
        {
            if (spec.Variables == null || spec.Variables.Count == 0) return;
            for (int i = 0; i < spec.Variables.Count; i++)
            {
                var v = spec.Variables[i] as JObject;
                string field = "variables[" + i + "]";
                if (v == null)
                {
                    spec.Errors.Add(new JObject { ["field"] = field, ["errors"] = new JArray("Item is not an object.") });
                    continue;
                }
                string vName = (v["varName"] ?? v["name"])?.ToString();
                if (string.IsNullOrWhiteSpace(vName))
                {
                    spec.Errors.Add(new JObject { ["field"] = field, ["errors"] = new JArray("Missing varName.") });
                    continue;
                }
                string vType = v["typeName"]?.ToString();
                if (string.IsNullOrWhiteSpace(vType)) continue; // inferred type — valid
                var res = VariableTypeResolver.Resolve(vType);
                if (!res.Recognized)
                {
                    spec.Errors.Add(new JObject
                    {
                        ["field"] = field,
                        ["errors"] = new JArray($"Unknown typeName '{vType}'. Did you mean '{res.Suggestion}'?")
                    });
                }
            }
        }

        // KB-level reference pre-flight (issue #56): a bare type name resolves to
        // DomainReference via VariableTypeResolver, but the name may not exist as a
        // Domain / SDT / Business Component / built-in GeneXus data type in the open
        // KB. Runs BEFORE the first save so a spec-invalid variable never persists.
        // `nameResolves(name)` is injected so this stays pure and unit-testable.
        internal static void ValidateKbReferences(ParsedSpec spec, Func<string, bool> nameResolves)
        {
            if (nameResolves == null || spec.Variables == null || spec.Variables.Count == 0) return;
            for (int i = 0; i < spec.Variables.Count; i++)
            {
                var v = spec.Variables[i] as JObject;
                if (v == null) continue;
                string vType = v["typeName"]?.ToString();
                if (string.IsNullOrWhiteSpace(vType)) continue;
                var res = VariableTypeResolver.Resolve(vType);
                if (!res.Recognized || res.CanonicalType != "DomainReference") continue;
                string refName = res.DomainName ?? vType;
                if (!nameResolves(refName))
                {
                    spec.Errors.Add(new JObject
                    {
                        ["field"] = "variables[" + i + "]",
                        ["errors"] = new JArray($"Type '{vType}' not found in the KB. Expected a Domain, SDT, Business Component, or built-in GeneXus data type.")
                    });
                }
            }
        }

        internal static ParsedSpec ParseAndValidate(JObject args)
        {
            bool? legacyUpdateExisting = args?["updateExisting"]?.ToObject<bool?>();
            bool modeWasProvided = args?["mode"] != null;
            var spec = new ParsedSpec
            {
                Type = args?["type"]?.ToString(),
                Name = args?["name"]?.ToString(),
                Description = (args?["description"] ?? args?["Description"])?.ToString(),
                Mode = (args?["mode"]?.ToString()
                    ?? (legacyUpdateExisting.HasValue && !legacyUpdateExisting.Value ? "create" : "auto")).ToLowerInvariant(),
                ExpectedVersion = (args?["expectedVersion"] ?? args?["baseVersion"])?.ToString(),
                LegacyUpdateExisting = modeWasProvided ? null : legacyUpdateExisting,
                Source = args?["source"]?.ToString(),
                Validate = args?["validate"]?.ToObject<bool?>() ?? false,
                RollbackOnFailure = args?["rollbackOnFailure"]?.ToObject<bool?>() ?? false,
                UpdateRequested = string.Equals(args?["mode"]?.ToString(), "update", StringComparison.OrdinalIgnoreCase)
            };
            spec.Validate = spec.Validate || string.Equals(args?["validationMode"]?.ToString(), "specify", StringComparison.OrdinalIgnoreCase);

            if (args?["variables"] is JArray vars) spec.Variables = vars;
            if (args?["properties"] is JObject props) spec.Properties = props;

            var rules = new System.Collections.Generic.List<string>();
            if (args?["rules"] is JArray rulesArr)
            {
                foreach (var r in rulesArr)
                {
                    string s = r?.ToString();
                    if (!string.IsNullOrWhiteSpace(s)) rules.Add(s.Trim());
                }
            }
            else if (!string.IsNullOrWhiteSpace(args?["rules"]?.ToString()))
            {
                rules.Add(args["rules"].ToString().Trim());
            }
            string parmLine = RenderParmRule(args?["parms"] as JArray);
            if (!string.IsNullOrWhiteSpace(parmLine)) rules.Add(parmLine);
            spec.RulesText = string.Join(Environment.NewLine, rules);

            var rulesTextError = TextPayloadGuard.BuildFieldError("rules", spec.RulesText);
            if (rulesTextError != null) spec.Errors.Add(rulesTextError);
            var sourceTextError = TextPayloadGuard.BuildFieldError("source", spec.Source);
            if (sourceTextError != null) spec.Errors.Add(sourceTextError);

            if (string.IsNullOrWhiteSpace(spec.Type))
                spec.Errors.Add(new JObject { ["field"] = "type", ["errors"] = new JArray("type is required (e.g. Procedure, Transaction, WebPanel).") });
            // Refused in the spec, not at the write, so a dryRun cannot preview a template that the real call would refuse.
            if (ObjectCreateTypes.IsWorkWithPlusTemplate(spec.Type))
                spec.Errors.Add(new JObject { ["field"] = "type", ["errors"] = new JArray(ObjectCreateTypes.WorkWithPlusTemplateHint) });
            if (string.IsNullOrWhiteSpace(spec.Name))
                spec.Errors.Add(new JObject { ["field"] = "name", ["errors"] = new JArray("name is required.") });
            if (spec.Mode != "create" && spec.Mode != "update" && spec.Mode != "auto")
                spec.Errors.Add(new JObject { ["field"] = "mode", ["errors"] = new JArray("mode must be create | update | auto.") });

            ValidateVariables(spec);
            ValidateRules(spec);
            return spec;
        }

        // ── Version fingerprint (pure) ──────────────────────────────────────────

        // SHA-256 over the object's Source+Rules+Variables text. Returned as the
        // `version` token on every successful atomic create/update so the caller can
        // pass it back as expectedVersion and get optimistic concurrency control.
        internal static string ComputeVersion(string source, string rules, string variables)
        {
            using (var sha = SHA256.Create())
            {
                string joined = (source ?? "") + "\n" + (rules ?? "") + "\n" + (variables ?? "");
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(joined));
                var sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        private string ReadFingerprint(string name, string type)
        {
            return TryReadFingerprint(name, type, out string fingerprint) ? fingerprint : "";
        }

        private bool TryReadFingerprint(string name, string type, out string fingerprint)
        {
            fingerprint = "";
            try
            {
                // An empty part is valid, but a missing/unparseable read is not an
                // empty part. Keep that distinction for optimistic concurrency:
                // silently treating a failed read as an empty part would make
                // expectedVersion fail open and allow a stale update through.
                if (!TryReadPartText(name, "Source", type, out string src)
                    || !TryReadPartText(name, "Rules", type, out string rules)
                    || !TryReadPartText(name, "Variables", type, out string vars))
                {
                    Logger.Debug("[ATOMIC-CREATE] fingerprint read incomplete for " + name);
                    return false;
                }

                fingerprint = ComputeVersion(src, rules, vars);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Debug("[ATOMIC-CREATE] fingerprint read failed for " + name + ": " + ex.Message);
                return false;
            }
        }

        private string ReadPartText(string name, string part, string type)
        {
            return TryReadPartText(name, part, type, out string content) ? content : "";
        }

        private bool TryReadPartText(string name, string part, string type, out string content)
        {
            content = "";
            try
            {
                string readJson = _objectService.ReadObjectSource(name, part, null, null, "mcp", true, type);
                if (string.IsNullOrWhiteSpace(readJson)) return false;
                var jo = JObject.Parse(readJson);
                JToken value = jo["source"] ?? jo["content"] ?? jo["parts"]?[part];
                if (value == null) return false;
                content = value.ToString();
                return true;
            }
            catch { return false; }
        }

        // KB-backed resolver for ValidateKbReferences. Returns true when no KB is
        // open (defer to the write path, which reports NoKbOpen properly) or when
        // the lookup itself throws (never false-accuse a type the probe couldn't
        // check); false only when the name definitively resolves to nothing.
        private bool ResolvesInKb(string name)
        {
            try
            {
                if (!KbModelGuard.TryGetDesignModel(_objectService.GetKbService(), out var model, out _))
                    return true;
                if (VariableInjector.ResolveTypeObject(model, name) != null) return true;
                if (VariableInjector.IsBuiltinUserDefinedType(name)) return true;
                var provider = Artech.Genexus.Common.Types.DataTypeProvider.GetProvider(model);
                if (provider != null && provider.GetTypeByName(name.Trim(), model) != null) return true;
                return false;
            }
            catch { return true; }
        }

        // ── Orchestration ───────────────────────────────────────────────────────

        public string Run(JObject args)
        {
            // Keep orchestration state outside the try: both returned error envelopes
            // and thrown SDK exceptions must compensate the same writes.
            ParsedSpec spec = null;
            PlacementPlan placement = null;
            bool exists = false;
            bool createdFreshObject = false;
            string currentField = null;
            var touchedParts = new System.Collections.Generic.List<string>();
            try
            {
                spec = ParseAndValidate(args);

                // KB-level reference pre-flight (issue #56) — before the first save.
                ValidateKbReferences(spec, ResolvesInKb);

                if (spec.Errors.Count > 0)
                {
                    return McpResponse.Err(
                        code: "AtomicCreateValidationFailed",
                        message: $"The object definition has {spec.Errors.Count} validation error(s); nothing was saved.",
                        hint: "Fix the field errors in error.fieldErrors and retry. Each error is attributed to the exact payload field (e.g. variables[2].typeName).",
                        target: spec.Name,
                        errorExtra: new JObject { ["fieldErrors"] = spec.Errors });
                }

                bool dryRun = args?["dryRun"]?.ToObject<bool?>() ?? false;

                // Where the caller wants the object (folder, module or parentPath). Resolved BEFORE
                // anything is created, by the resolver genexus_properties action=move uses, and for
                // dryRun too: a path that does not exist, is ambiguous or names the wrong kind is
                // refused here with nothing written.
                var placementRequest = PlacementRequest.FromCreateArgs(args, out string placementConflict);
                if (placementConflict != null)
                {
                    return McpResponse.Err(
                        code: "DestinationInvalid",
                        message: placementConflict,
                        hint: "Pass only one of folder, module or parentPath, or make them name the same place.",
                        target: spec.Name,
                        errorExtra: new JObject
                        {
                            ["reasonCode"] = "DestinationInvalid", ["phase"] = "destination-resolution",
                            ["persisted"] = false, ["implicitOperations"] = new JArray()
                        });
                }
                if (placementRequest != null)
                {
                    var resolution = _objectService.ResolvePlacement(placementRequest.Raw, placementRequest.Kind, out PlacementCatalog catalog);
                    if (!resolution.Ok) return ObjectService.PlacementRefused(resolution, spec.Name, "destination-resolution");
                    string importedPath = _objectService.ImportedDestinationPath(resolution);
                    if (importedPath != null)
                    {
                        var blocker = MovePreflight.DestinationReadOnly(importedPath);
                        return McpResponse.Err(
                            code: blocker.Code, message: blocker.Message, hint: blocker.Remedy, target: spec.Name,
                            errorExtra: new JObject
                            {
                                ["reasonCode"] = blocker.Code, ["phase"] = "preflight",
                                ["placement"] = resolution.Describe(),
                                ["persisted"] = false, ["implicitOperations"] = new JArray()
                            });
                    }
                    placement = new PlacementPlan { Request = placementRequest, Resolution = resolution, Catalog = catalog };
                }

                var existing = _objectService.FindObject(spec.Name, spec.Type);
                exists = existing != null;

                if (spec.Mode == "create" && exists)
                {
                    return McpResponse.Err(
                        code: "AlreadyExists",
                        message: spec.Type + " '" + spec.Name + "' already exists; pass mode=update (and expectedVersion for concurrent-write protection) to update it atomically.",
                        hint: "Use mode=update with the latest `version` token from a prior read/update to overwrite safely.",
                        target: spec.Name);
                }
                if (spec.LegacyUpdateExisting.HasValue && exists && !spec.LegacyUpdateExisting.Value)
                {
                    return McpResponse.Err(
                        code: "AlreadyExists",
                        message: spec.Type + " '" + spec.Name + "' already exists; pass updateExisting=true to mutate it atomically.",
                        hint: "Use updateExisting=true (or the preferred mode=update with expectedVersion) to update an existing object.",
                        target: spec.Name);
                }
                if (spec.UpdateRequested && !exists)
                {
                    return McpResponse.Err(
                        code: "ObjectNotFound",
                        message: "mode=update requested but '" + spec.Name + "' does not exist.",
                        hint: "Drop mode=update (or use mode=auto) to create it atomically.",
                        nextSteps: new JArray(McpResponse.NextStep(
                            tool: "genexus_create",
                            args: new JObject { ["type"] = spec.Type, ["name"] = spec.Name, ["mode"] = "auto" },
                            why: "Creates the missing object atomically instead of updating a non-existent one.")),
                        target: spec.Name);
                }

                if (placement != null)
                {
                    if (exists)
                    {
                        // object_atomic updates an existing object where it is; it never moves one. A
                        // placement that differs from where the object is would otherwise be dropped silently.
                        Guid? actualParent = ObjectService.ContainerGuidOf(existing);
                        Guid? wanted = placement.Resolution.IsRoot ? (Guid?)null : placement.Resolution.Container.Guid;
                        if (actualParent != wanted)
                        {
                            string actualPath = ObjectService.ContainerPathOf(existing.Parent);
                            return McpResponse.Err(
                                code: "ObjectExistsElsewhere",
                                message: spec.Type + " '" + spec.Name + "' already exists in '" + actualPath + "'; object_atomic does not move an existing object to '"
                                    + placement.Resolution.ResolvedPath + "'.",
                                hint: "Drop folder/module/parentPath to update it where it is, or move it first with genexus_properties action=move (dryRun=true shows the plan).",
                                target: spec.Name,
                                errorExtra: new JObject
                                {
                                    ["reasonCode"] = "ObjectExistsElsewhere", ["phase"] = "preflight",
                                    ["placement"] = placement.Resolution.Describe(),
                                    ["actualParent"] = new JObject { ["guid"] = actualParent?.ToString(), ["path"] = actualPath },
                                    ["persisted"] = false, ["implicitOperations"] = new JArray()
                                });
                        }
                    }
                    else
                    {
                        var nameConflict = _objectService.FindCreateNameConflict(spec.Type, spec.Name, placement.Resolution);
                        if (nameConflict != null)
                        {
                            return McpResponse.Err(
                                code: MovePreflight.NameConflictCode,
                                message: "'" + (nameConflict.QualifiedName ?? spec.Name) + "' already exists in the destination (" + nameConflict.Path + "); the SDK refuses two objects with one name in a module.",
                                hint: "Choose another name or another destination.",
                                target: spec.Name,
                                errorExtra: new JObject
                                {
                                    ["reasonCode"] = MovePreflight.NameConflictCode, ["phase"] = "preflight",
                                    ["placement"] = placement.Resolution.Describe(),
                                    ["persisted"] = false, ["implicitOperations"] = new JArray()
                                });
                        }
                    }
                }

                // Optimistic version control — only meaningful for an existing object.
                if (exists && !string.IsNullOrEmpty(spec.ExpectedVersion))
                {
                    if (!TryReadFingerprint(spec.Name, spec.Type, out string current))
                    {
                        return McpResponse.Err(
                            code: "VersionUnavailable",
                            message: "The current object version could not be read; refusing to apply expectedVersion without a reliable concurrency check.",
                            hint: "Re-read the object and retry. If the version remains unavailable, inspect the worker log or use the GeneXus IDE before updating.",
                            target: spec.Name,
                            errorExtra: new JObject
                            {
                                ["expectedVersion"] = spec.ExpectedVersion,
                                ["currentVersion"] = null
                            });
                    }

                    if (!string.Equals(current, spec.ExpectedVersion, StringComparison.Ordinal))
                    {
                        return McpResponse.Err(
                            code: "ConcurrentModification",
                            message: "The object changed since the expectedVersion was captured; refusing to overwrite concurrent changes.",
                            hint: "Re-read the object, merge your changes, and retry with the fresh `version` token from that read.",
                            target: spec.Name,
                            errorExtra: new JObject
                            {
                                ["expectedVersion"] = spec.ExpectedVersion,
                                ["currentVersion"] = current
                            });
                    }
                }

                if (dryRun)
                {
                    var preview = new JObject
                    {
                        ["action"] = exists ? "update" : "create",
                        ["type"] = spec.Type,
                        ["name"] = spec.Name,
                        ["mode"] = spec.Mode,
                        ["variables"] = spec.Variables.DeepClone(),
                        ["rules"] = spec.RulesText,
                        ["source"] = spec.Source ?? "",
                        ["properties"] = spec.Properties.DeepClone(),
                        ["validate"] = spec.Validate,
                        ["version"] = exists ? ReadFingerprint(spec.Name, spec.Type) : null,
                        ["fieldErrors"] = spec.Errors,
                        ["note"] = "dryRun=true: full field validation passed; nothing was saved."
                    };
                    if (!string.IsNullOrWhiteSpace(spec.Description)) preview["description"] = spec.Description;
                    if (placement != null)
                    {
                        var placementPreview = placement.Resolution.Describe();
                        placementPreview["wouldCreateAt"] = placement.Resolution.ResolvedPath + "/" + spec.Name;
                        placementPreview["persisted"] = false;
                        placementPreview["verified"] = false;
                        placementPreview["note"] = "The destination resolved to one Folder/Module with the same resolver the write uses; the write re-reads the created object freshly and deletes it again (confirmed by a fresh lookup) if it is not at this path.";
                        preview["placement"] = placementPreview;
                    }
                    preview["implicitOperations"] = new JArray();
                    return McpResponse.Ok(target: spec.Name, code: "DryRun", result: preview);
                }

                var steps = new JObject();
                if (exists) CapturePreWriteSnapshots(spec);

                Artech.Architecture.Common.Objects.KBObject obj = null;
                if (!exists)
                {
                    createdFreshObject = true;
                    obj = _objectService.CreateObjectInstance(spec.Type, spec.Name, args, out string seededDesc, out JObject domainMeta);
                    if (!string.IsNullOrWhiteSpace(spec.Description))
                    {
                        try { obj.Description = spec.Description; } catch { }
                    }
                    steps["create"] = new JObject
                    {
                        ["status"] = "ok",
                        ["type"] = spec.Type,
                        ["name"] = spec.Name,
                        ["seededDescription"] = seededDesc
                    };
                    if (placement != null)
                    {
                        // Give the new object its parent before the first save, as the IDE does when it
                        // creates inside a Folder: the object is then born in place. Whether the SDK
                        // honoured it is proved after the save by a fresh read, not assumed here.
                        string parentRefused = placement.Resolution.IsRoot
                            ? null
                            : ObjectMover.TrySetParent(obj, _objectService.LoadContainer(placement.Resolution));
                        steps["placement"] = new JObject
                        {
                            ["requested"] = placement.Request.Raw,
                            ["resolvedPath"] = placement.Resolution.ResolvedPath,
                            ["parentSetBeforeSave"] = !placement.Resolution.IsRoot && parentRefused == null,
                            ["parentSetRefusedBecause"] = parentRefused
                        };
                    }
                }
                else
                {
                    obj = _objectService.FindObject(spec.Name, spec.Type);
                    if (obj == null)
                        return McpResponse.Err(
                            code: "ObjectNotFound",
                            message: $"Object '{spec.Name}' not found for update.",
                            hint: "Verify the target name and that the KB is open, or use mode=create.",
                            nextSteps: new JArray(McpResponse.NextStep(
                                tool: "genexus_list_objects",
                                args: new JObject { ["name"] = spec.Name },
                                why: "Checks if the object exists under a different type or casing.")),
                            target: spec.Name);
                    if (!string.IsNullOrWhiteSpace(spec.Description))
                    {
                        try { obj.Description = spec.Description; } catch { }
                    }
                }

                // Step 2 — variables (in-memory batch)
                if (spec.Variables != null && spec.Variables.Count > 0)
                {
                    currentField = "variables";
                    touchedParts.Add("Variables");
                    var varPart = GxMcp.Worker.Structure.PartAccessor.GetVariablesPart(obj);
                    if (varPart == null)
                        return CompensateAndFail(spec, exists, new JObject { ["error"] = new JObject { ["message"] = "Variables part not found in " + obj.TypeDescriptor.Name } }, "variables", touchedParts);

                    _writeService.PopulateVariablesInto(varPart, spec.Variables, out var outcomes, out int added, out int existed, out int failed, out var domainBound, out var addedNames);

                    steps["variables"] = DescribeVariablePhase(added, existed, failed, outcomes);
                    if ((string)steps["variables"]["status"] == "error")
                    {
                        return CompensateAndFail(spec, exists, (JObject)steps["variables"], "variables", touchedParts);
                    }
                }

                // Step 3 — rules (in-memory)
                if (!string.IsNullOrWhiteSpace(spec.RulesText))
                {
                    currentField = "rules";
                    touchedParts.Add("Rules");
                    var rulesPart = GxMcp.Worker.Structure.PartAccessor.GetPart(obj, "Rules");
                    if (rulesPart is Artech.Architecture.Common.Objects.ISource rulesSrc)
                    {
                        rulesSrc.Source = spec.RulesText;
                        steps["rules"] = new JObject { ["status"] = "ok", ["part"] = "Rules" };
                    }
                    else if (rulesPart != null)
                    {
                        var p = rulesPart.GetType().GetProperty("Source") ?? rulesPart.GetType().GetProperty("Content");
                        p?.SetValue(rulesPart, spec.RulesText);
                        steps["rules"] = new JObject { ["status"] = "ok", ["part"] = "Rules" };
                    }
                    else
                    {
                        return CompensateAndFail(spec, exists, new JObject { ["error"] = new JObject { ["message"] = "Rules part not found in " + obj.TypeDescriptor.Name } }, "rules", touchedParts);
                    }
                }

                // Step 4 — source (in-memory)
                if (!string.IsNullOrWhiteSpace(spec.Source))
                {
                    currentField = "source";
                    touchedParts.Add("Source");
                    var srcPart = GxMcp.Worker.Structure.PartAccessor.GetPart(obj, "Source");
                    if (srcPart is Artech.Architecture.Common.Objects.ISource srcSrc)
                    {
                        srcSrc.Source = spec.Source;
                        steps["source"] = new JObject { ["status"] = "ok", ["part"] = "Source" };
                    }
                    else if (srcPart != null)
                    {
                        var p = srcPart.GetType().GetProperty("Source") ?? srcPart.GetType().GetProperty("Content");
                        p?.SetValue(srcPart, spec.Source);
                        steps["source"] = new JObject { ["status"] = "ok", ["part"] = "Source" };
                    }
                    else
                    {
                        return CompensateAndFail(spec, exists, new JObject { ["error"] = new JObject { ["message"] = "Source part not found in " + obj.TypeDescriptor.Name } }, "source", touchedParts);
                    }
                }

                // Step 5 — properties (in-memory)
                if (spec.Properties != null && spec.Properties.Count > 0)
                {
                    currentField = "properties";
                    _propertyService.ApplyPropertiesDirect(obj, spec.Properties);
                    var propResults = new JObject();
                    foreach (var p in spec.Properties.Properties())
                    {
                        propResults[p.Name] = new JObject { ["status"] = "ok", ["value"] = p.Value?.ToString() };
                    }
                    steps["properties"] = propResults;
                }

                // SINGLE ATOMIC SAVE FOR THE ENTIRE OBJECT
                currentField = "save";
                obj.EnsureSave(check: false);
                WriteService.NotePerTargetWrite(spec.Name);

                try
                {
                    var idx = _objectService.GetKbService()?.GetIndexCache();
                    if (idx != null) idx.UpdateEntry(obj);
                }
                catch { }

                // Position proof: a fresh read of the saved object must find it in the requested
                // Folder/Module. If the SDK left it elsewhere it is moved with the genexus_properties
                // move pipeline; if that fails too the object is deleted again and its absence confirmed,
                // so a failed placement never leaves an object in the Root Module behind.
                JObject placementReceipt = null;
                JObject persistedIdentity = null;
                if (!exists && placement != null)
                {
                    currentField = "placement";
                    var outcome = AtomicPlacementFlow.Verify(placement.Resolution, BuildPlacementPorts(obj, spec, placement));
                    if (!outcome.Ok) return PlacementFailure(spec, placement, outcome);

                    var freshForIdentity = _objectService.FindObjectFreshByIdentity(obj) ?? obj;
                    try
                    {
                        var idx = _objectService.GetKbService()?.GetIndexCache();
                        if (idx != null)
                        {
                            idx.InvalidateHierarchy(freshForIdentity.Guid);
                            idx.UpdateEntry(freshForIdentity);
                        }
                    }
                    catch { }
                    persistedIdentity = _objectService.DescribePersistedIdentity(freshForIdentity);
                    placementReceipt = placement.Resolution.Describe();
                    placementReceipt["persisted"] = true;
                    placementReceipt["verified"] = true;
                    placementReceipt["freshRead"] = true;
                    placementReceipt["strategy"] = outcome.Strategy;
                    placementReceipt["path"] = persistedIdentity["path"];
                }

                string newVersion = ReadFingerprint(spec.Name, spec.Type);
                var result = new JObject
                {
                    ["mode"] = exists ? "update" : "create",
                    ["type"] = spec.Type,
                    ["name"] = spec.Name,
                    ["steps"] = steps,
                    ["version"] = newVersion
                };
                if (placementReceipt != null)
                {
                    result["identity"] = persistedIdentity;
                    result["placement"] = placementReceipt;
                }
                result["implicitOperations"] = new JArray();

                string response = McpResponse.Ok(target: spec.Name,
                    code: exists ? "ObjectUpdatedAtomic" : "ObjectCreatedAtomic", result: result);

                // Step 6 — validate=true: inline Specify pass (issue #60 machinery).
                if (spec.Validate)
                {
                    var specifyArgs = (JObject)args.DeepClone();
                    specifyArgs["validationMode"] = "specify";
                    specifyArgs["rollbackOnFailure"] = spec.RollbackOnFailure;
                    string validated = _saveSpecifyOrchestrator.MaybeValidateAfterWrite(response, spec.Name, specifyArgs, "Source");
                    var validatedJson = SafeParse(validated);
                    if (IsError(validatedJson) && spec.RollbackOnFailure)
                    {
                        var extra = validatedJson["error"] as JObject;
                        if (extra == null) { extra = new JObject(); validatedJson["error"] = extra; }

                        if (!exists)
                        {
                            // A fresh object has no pre-write snapshot, so a spec failure
                            // with rollbackOnFailure=true leaves it behind — delete it.
                            string delResp = _objectService.DeleteObject(spec.Name, spec.Type, true);
                            var delJson = SafeParse(delResp);
                            extra["compensation"] = new JObject
                            {
                                ["action"] = "delete_created_object",
                                ["status"] = IsOk(delJson) ? "Deleted" : "Failed",
                                ["note"] = "Object was created by this call but failed the Specify pass; deleted to keep the operation all-or-nothing."
                            };
                        }
                        else
                        {
                            // Update mode: SaveSpecifyOrchestrator only rolls back the part it
                            // was told about (Source) — it already did that internally since
                            // rollbackOnFailure was forwarded. Restoring Source again below is
                            // idempotent (same pre-write snapshot), so keep the loop simple and
                            // let RestoreTouchedParts restore EVERY part this call touched,
                            // undoing the Variables/Rules changes too.
                            extra["compensation"] = RestoreTouchedParts(spec, touchedParts);
                        }
                        validated = validatedJson.ToString(Newtonsoft.Json.Formatting.None);
                    }
                    return validated;
                }

                return response;
            }
            catch (Exception ex)
            {
                Logger.Error("[ATOMIC-CREATE] " + ex.Message);
                // The exception path must also be all-or-nothing: if we created a fresh
                // object and a step THREW (rather than returning an error envelope),
                // delete it so nothing partial survives.
                JObject compensation = null;
                if (createdFreshObject)
                {
                    try
                    {
                        var onDisk = _objectService.FindObject(args?["name"]?.ToString(), args?["type"]?.ToString());
                        if (onDisk != null)
                        {
                            string delResp = _objectService.DeleteObject(args?["name"]?.ToString(), args?["type"]?.ToString(), true);
                            var delJson = SafeParse(delResp);
                            compensation = new JObject
                            {
                                ["action"] = "delete_created_object",
                                ["status"] = IsOk(delJson) ? "Deleted" : "Failed",
                                ["note"] = IsOk(delJson)
                                    ? "The freshly-created object was deleted after the exception."
                                    : "The freshly-created object may remain (delete via genexus_delete_object if so)."
                            };
                        }
                        else
                        {
                            compensation = new JObject
                            {
                                ["action"] = "discard_in_memory_instance",
                                ["status"] = "Discarded",
                                ["note"] = "The object was never committed to the Knowledge Base; the in-memory instance was discarded."
                            };
                        }
                    }
                    catch
                    {
                        compensation = new JObject
                        {
                            ["action"] = "delete_created_object",
                            ["status"] = "Failed",
                            ["note"] = "Deletion after exception failed; verify the object state with genexus_read."
                        };
                    }
                }
                else if (exists && spec != null && touchedParts.Count > 0)
                {
                    compensation = RestoreTouchedParts(spec, touchedParts);
                    if (!string.IsNullOrEmpty(currentField) && currentField.StartsWith("properties.", StringComparison.OrdinalIgnoreCase))
                    {
                        compensation["note"] = (compensation["note"]?.ToString() + " " ?? string.Empty)
                            + "Object-level properties have no pre-write snapshot; verify them with genexus_read.";
                    }
                }
                else if (spec != null && !string.IsNullOrEmpty(currentField))
                {
                    compensation = new JObject
                    {
                        ["action"] = "none",
                        ["status"] = "NotRequired",
                        ["field"] = currentField,
                        ["note"] = "No persisted part was recorded before the exception. Verify the object state with genexus_read."
                    };
                }
                var exExtra = new JObject();
                if (compensation != null) exExtra["compensation"] = compensation;
                if (placement != null && string.Equals(currentField, "save", StringComparison.Ordinal))
                {
                    // The new object was given its parent before this save: the SDK's own message follows.
                    exExtra["reasonCode"] = "PlacementSaveRefused";
                    exExtra["phase"] = "save";
                    exExtra["placement"] = placement.Resolution.Describe();
                    exExtra["persisted"] = false;
                    exExtra["implicitOperations"] = new JArray();
                }
                return McpResponse.Err(
                    code: "AtomicCreateFailed",
                    message: ex.Message,
                    hint: "Inspect the worker log for the full exception chain.",
                    target: args?["name"]?.ToString(),
                    errorExtra: exExtra);
            }
        }

        // The destination of this call, resolved once (see Run) and used by the preview and the write.
        private sealed class PlacementPlan
        {
            public PlacementRequest Request;
            public PlacementResolution Resolution;
            public PlacementCatalog Catalog;
        }

        // The SDK-facing side of AtomicPlacementFlow: a fresh read, the move pipeline of
        // genexus_properties action=move, and a delete that proves the object is gone.
        private AtomicPlacementFlow.Ports BuildPlacementPorts(Artech.Architecture.Common.Objects.KBObject created,
            ParsedSpec spec, PlacementPlan placement)
        {
            return new AtomicPlacementFlow.Ports
            {
                FreshProbe = () => _objectService.ProbeFresh(created),
                MoveIntoContainer = () =>
                {
                    var kb = _objectService.GetKbService().GetKB();
                    string json = _objectService.MoveResolved(kb, created, spec.Name, placement.Resolution, placement.Catalog,
                        dryRun: false, baseVersion: WriteService.ComputeVersionToken(created), rollbackOnFailure: true);
                    var parsed = SafeParse(json);
                    if (IsOk(parsed))
                        return new AtomicPlacementFlow.MoveAttempt { Ok = true, Strategy = parsed["result"]?["strategy"]?.ToString() };
                    var error = parsed["error"] as JObject;
                    return new AtomicPlacementFlow.MoveAttempt
                    {
                        Ok = false,
                        Code = error?["code"]?.ToString(),
                        Error = error?["message"]?.ToString() ?? json
                    };
                },
                DeleteCreated = () =>
                {
                    // By GUID: a name could resolve to a same-named object elsewhere in the KB.
                    string deleteJson = _objectService.DeleteObject(created.Guid.ToString("D"), spec.Type, true);
                    var deleted = SafeParse(deleteJson);
                    bool ok = IsOk(deleted);
                    bool absent = false;
                    try
                    {
                        ObjectService.InvalidateAllReadCaches();
                        absent = _objectService.IsAbsentFromModel(created);
                    }
                    catch { }
                    return new AtomicPlacementFlow.DeleteAttempt
                    {
                        Deleted = ok,
                        AbsentVerified = ok && absent,
                        Error = ok ? null : (deleted["error"]?["message"]?.ToString() ?? deleteJson)
                    };
                }
            };
        }

        private string PlacementFailure(ParsedSpec spec, PlacementPlan placement, AtomicPlacementFlow.Outcome outcome)
        {
            bool rolledBack = outcome.Compensation?["rolledBack"]?.ToObject<bool>() ?? false;
            var placementJson = placement.Resolution.Describe();
            placementJson["attempt"] = outcome.Attempt;
            var errorExtra = new JObject
            {
                ["reasonCode"] = outcome.ReasonCode,
                ["phase"] = "placement",
                ["placement"] = placementJson,
                ["compensation"] = outcome.Compensation,
                ["rolledBack"] = rolledBack,
                ["rollbackVerified"] = rolledBack,
                ["implicitOperations"] = new JArray()
            };
            if (rolledBack) errorExtra["persisted"] = false;
            else errorExtra["persistenceUncertain"] = true;
            return McpResponse.Err(
                code: "AtomicCreatePlacementFailed",
                message: outcome.Message + (rolledBack
                    ? " The new object was deleted again and a fresh lookup confirmed it is gone, so nothing was left behind."
                    : " The new object may still exist; see compensation."),
                hint: rolledBack
                    ? "Nothing was left behind. Check the destination with genexus_query type:Folder|Module, then retry; dryRun=true previews the placement."
                    : "Compensation did not fully complete: find the object with genexus_query and remove it with genexus_delete_object before retrying.",
                target: spec.Name,
                errorExtra: errorExtra);
        }

        // Capture pre-write EditSnapshotStore snapshots for every part this update
        // will touch (Variables/Rules/Source), so update-mode compensation can restore
        // them even though AddVariables itself does not snapshot.
        private void CapturePreWriteSnapshots(ParsedSpec spec)
        {
            try
            {
                var obj = _objectService.FindObject(spec.Name, spec.Type);
                if (obj == null) return;
                string guid = obj.Guid.ToString();
                string root = EditSnapshotStore.ResolveRoot(_objectService.GetKbService()?.GetKbPath());

                if (spec.Variables != null && spec.Variables.Count > 0)
                    SavePartSnapshot(root, guid, "Variables", ReadPartText(spec.Name, "Variables", spec.Type));
                if (!string.IsNullOrWhiteSpace(spec.RulesText))
                    SavePartSnapshot(root, guid, "Rules", ReadPartText(spec.Name, "Rules", spec.Type));
                if (!string.IsNullOrWhiteSpace(spec.Source))
                    SavePartSnapshot(root, guid, "Source", ReadPartText(spec.Name, "Source", spec.Type));
            }
            catch (Exception ex)
            {
                Logger.Debug("[ATOMIC-CREATE] pre-write snapshot capture skipped: " + ex.Message);
            }
        }

        private static void SavePartSnapshot(string root, string guid, string part, string content)
        {
            try { EditSnapshotStore.SaveSnapshot(root, guid, part, content ?? ""); }
            catch { /* best-effort — restore reports honestly when missing */ }
        }

        // Restore every touched part from its pre-write snapshot (update mode).
        // Returns the compensation JObject with restoredParts/failedParts/status.
        private JObject RestoreTouchedParts(ParsedSpec spec, System.Collections.Generic.List<string> touchedParts)
        {
            var compensation = new JObject
            {
                ["action"] = "restore_snapshots",
                ["rolledBack"] = false
            };
            var restoredParts = new JArray();
            var failedParts = new JArray();
            foreach (var part in touchedParts)
            {
                string restoreResp = _historyService.Execute(spec.Name, "restore", partName: part, snapshotToken: "latest");
                var restoreJson = SafeParse(restoreResp);
                bool restored = IsOk(restoreJson)
                    || string.Equals(restoreJson["code"]?.ToString(), "SnapshotRestored", StringComparison.OrdinalIgnoreCase);
                if (restored) restoredParts.Add(part);
                else failedParts.Add(part);
            }
            compensation["restoredParts"] = restoredParts;
            compensation["failedParts"] = failedParts;
            compensation["status"] = failedParts.Count == 0 ? "Restored" : "Partial";
            if (failedParts.Count > 0)
                compensation["note"] = "No pre-write snapshot was available to restore: " + string.Join(", ", failedParts) + ". Verify the object state with genexus_read.";
            return compensation;
        }

        // On a mid-pipeline failure, compensate so the operation is all-or-nothing:
        // create mode → delete the object just created; update mode → restore the
        // pre-write snapshot of every part written so far. The compensation is
        // reported inside the error envelope; the original step error is preserved.
        private string CompensateAndFail(ParsedSpec spec, bool exists, JObject failingStep, string failingField,
            System.Collections.Generic.List<string> touchedParts)
        {
            var compensation = new JObject { ["field"] = failingField, ["rolledBack"] = false };

            if (!exists)
            {
                var onDisk = _objectService.FindObject(spec.Name, spec.Type);
                if (onDisk != null)
                {
                    string delResp = _objectService.DeleteObject(spec.Name, spec.Type, true);
                    var delJson = SafeParse(delResp);
                    compensation["action"] = "delete_created_object";
                    compensation["status"] = IsOk(delJson) ? "Deleted" : "Failed";
                    if (!IsOk(delJson))
                        compensation["note"] = "Object deletion after the failed step did not complete; verify and remove '" + spec.Name + "' with genexus_delete_object.";
                    else
                        compensation["note"] = "The freshly-created object was deleted so the failed operation leaves nothing behind.";
                }
                else
                {
                    compensation["action"] = "discard_in_memory_instance";
                    compensation["status"] = "Discarded";
                    compensation["note"] = "The object was never committed to the Knowledge Base; the in-memory instance was discarded.";
                }
            }
            else
            {
                var restored = RestoreTouchedParts(spec, touchedParts);
                foreach (var prop in restored.Properties())
                    compensation[prop.Name] = prop.Value?.DeepClone();
                // Property writes have no pre-write snapshot (repo-wide limitation), so
                // properties applied BEFORE the failing one keep their new values — say so
                // explicitly instead of implying a full restore. Append (don't overwrite) any
                // restore-failure note RestoreTouchedParts already set.
                if (failingField.StartsWith("properties", StringComparison.OrdinalIgnoreCase))
                {
                    string propNote = "Parts " + string.Join(", ", touchedParts) + " were restored from their pre-write snapshots, but object-level properties applied earlier in this call keep their new values — verify with genexus_read.";
                    string existingNote = compensation["note"]?.ToString();
                    compensation["note"] = string.IsNullOrEmpty(existingNote) ? propNote : existingNote + " " + propNote;
                }
            }

            string stepMessage = failingStep["error"]?["message"]?.ToString()
                ?? failingStep["message"]?.ToString()
                ?? failingStep.ToString(Newtonsoft.Json.Formatting.None);

            var extra = new JObject
            {
                ["field"] = failingField,
                ["step"] = failingStep,
                ["compensation"] = compensation
            };

            return McpResponse.Err(
                code: "AtomicCreateStepFailed",
                message: $"Step '{failingField}' failed; the operation was rolled back to stay all-or-nothing. Step error: {stepMessage}",
                hint: compensation["status"]?.ToString() == "Deleted" || compensation["status"]?.ToString() == "Restored"
                    ? "The object was left in its pre-operation state. Fix the reported field and retry."
                    : "Compensation did not fully complete — verify the object state with genexus_read before retrying.",
                target: spec.Name,
                errorExtra: extra);
        }

        // ── Envelope helpers ────────────────────────────────────────────────────

        private static JObject SafeParse(string json)
        {
            try { return JObject.Parse(json); }
            catch { return new JObject { ["raw"] = json }; }
        }

        private static bool IsOk(JObject jo)
        {
            if (jo == null) return false;
            string status = jo["status"]?.ToString() ?? jo["Status"]?.ToString() ?? string.Empty;
            return string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "Success", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "PartialSuccess", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "partial", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "Applied", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "ObjectCreated", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsError(JObject jo)
        {
            if (jo == null) return true;
            string status = jo["status"]?.ToString() ?? jo["Status"]?.ToString() ?? string.Empty;
            return string.Equals(status, "error", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "Error", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "Failed", StringComparison.OrdinalIgnoreCase);
        }
    }
}
