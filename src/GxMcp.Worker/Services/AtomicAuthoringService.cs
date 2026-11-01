using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// Caller-atomic object authoring.  GX18 services persist individual parts, so the
    /// operation uses a complete preflight plus a compensating snapshot: either every
    /// requested part verifies (and optional specification succeeds), or a new object is
    /// removed / an existing object is restored before an error is returned.
    /// </summary>
    public sealed class AtomicAuthoringService
    {
        private readonly ObjectService _objects;
        private readonly WriteService _write;
        private readonly PropertyService _properties;
        private readonly BuildService _build;

        public AtomicAuthoringService(ObjectService objects, WriteService write, PropertyService properties, BuildService build)
        {
            _objects = objects; _write = write; _properties = properties; _build = build;
        }

        public string Run(JObject args)
        {
            string name = args?["name"]?.ToString();
            string type = args?["type"]?.ToString();
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(type))
                return McpResponse.Err(code: "AtomicAuthoringNeedsIdentity", message: "name and type are required.", target: name);

            var diagnostics = Preflight(args);
            if (diagnostics.Count > 0)
                return McpResponse.Err(code: "AtomicPreflightFailed", message: "The atomic authoring preflight failed.",
                    target: name, extra: new JObject { ["diagnostics"] = diagnostics, ["saved"] = false, ["rolledBack"] = false });

            var existing = _objects.FindObject(name, type);
            bool updateExisting = args?["updateExisting"]?.ToObject<bool?>() ?? false;
            if (existing != null && !updateExisting)
                return McpResponse.Err(code: "AlreadyExists", message: type + " '" + name + "' already exists; pass updateExisting=true to mutate it atomically.", target: name);

            string baseVersion = args?["baseVersion"]?.ToString();
            if (existing != null && !string.IsNullOrWhiteSpace(baseVersion))
            {
                string actual = WriteService.ComputeVersionToken(existing);
                if (!string.Equals(baseVersion, actual, StringComparison.Ordinal))
                    return McpResponse.Err(code: "VersionConflict", message: "The object changed after the caller read it.", target: name,
                        extra: new JObject { ["baseVersion"] = baseVersion, ["currentVersion"] = actual });
            }

            JObject plan = BuildPlan(args, existing != null);
            if (args?["dryRun"]?.ToObject<bool?>() == true)
                return McpResponse.Ok(target: name, code: "DryRun", result: new JObject
                {
                    ["plan"] = plan, ["saved"] = false, ["specified"] = false, ["generated"] = false, ["rolledBack"] = false
                });

            JObject snapshot = existing == null ? null : CaptureSnapshot(name, type, args);
            bool created = false;
            var phases = new JArray();
            try
            {
                Artech.Architecture.Common.Objects.KBObject obj = existing;
                if (obj == null)
                {
                    JObject createArgs = (JObject)args.DeepClone();
                    createArgs.Remove("variables"); createArgs.Remove("rules"); createArgs.Remove("source"); createArgs.Remove("properties");
                    if (createArgs["module"] != null)
                    {
                        createArgs["destModule"] = createArgs["module"].DeepClone();
                        createArgs.Remove("module");
                    }
                    obj = _objects.CreateObjectInstance(type, name, createArgs, out _, out _);
                    created = true;
                    phases.Add(new JObject { ["phase"] = "create", ["status"] = "ok" });
                }

                if (args?["variables"] is JArray variables && variables.Count > 0)
                {
                    var varPart = GxMcp.Worker.Structure.PartAccessor.GetVariablesPart(obj);
                    if (varPart == null)
                        throw new AtomicFailure("variables", "The object has no Variables part.", null);
                    else
                    {
                        var normalizedVariables = new JArray();
                        foreach (JToken item in variables)
                        {
                            JObject variable = item as JObject;
                            if (variable == null) { normalizedVariables.Add(item.DeepClone()); continue; }
                            variable = (JObject)variable.DeepClone();
                            if (variable["typeName"] == null && variable["basedOnAttribute"] != null)
                            {
                                string attrName = variable["basedOnAttribute"].ToString().Trim();
                                if (VariableInjector.TryParseAttributeReference(attrName, out string parsedAttr))
                                    attrName = parsedAttr;
                                else
                                    attrName = attrName.TrimStart('&');
                                variable["typeName"] = "Attribute:" + attrName;
                            }
                            else if (variable["typeName"] == null && variable["basedOn"] != null)
                                variable["typeName"] = variable["basedOn"].DeepClone();
                            normalizedVariables.Add(variable);
                        }
                        _write.PopulateVariablesInto(varPart, normalizedVariables, out var outcomes, out int added, out int existed, out int failed, out _, out _);
                        RecordVariablePhase(phases, added, existed, failed, outcomes);
                    }
                }

                string rules = JoinText(args?["rules"]);
                if (rules != null)
                {
                    var rPart = GxMcp.Worker.Structure.PartAccessor.GetPart(obj, "Rules");
                    if (rPart is Artech.Architecture.Common.Objects.ISource rSrc) rSrc.Source = rules;
                    else (rPart?.GetType().GetProperty("Source") ?? rPart?.GetType().GetProperty("Content"))?.SetValue(rPart, rules);
                    phases.Add(new JObject { ["phase"] = "rules", ["status"] = "ok" });
                }

                string source = JoinText(args?["source"]);
                if (source != null)
                {
                    var sPart = GxMcp.Worker.Structure.PartAccessor.GetPart(obj, "Source");
                    if (sPart is Artech.Architecture.Common.Objects.ISource sSrc) sSrc.Source = source;
                    else (sPart?.GetType().GetProperty("Source") ?? sPart?.GetType().GetProperty("Content"))?.SetValue(sPart, source);
                    phases.Add(new JObject { ["phase"] = "source", ["status"] = "ok" });
                }

                if (args?["properties"] is JObject properties)
                {
                    _properties.ApplyPropertiesDirect(obj, properties);
                    phases.Add(new JObject { ["phase"] = "properties", ["status"] = "ok" });
                }

                // A save that only writes the properties of an existing object is a property write: it does not run the pattern
                // on save either (see PropertyApplyOnSaveRule). A save that also writes variables, rules or source runs as before.
                bool propertyOnlySave = existing != null && args?["properties"] is JObject
                    && !(args?["variables"] is JArray atomicVariables && atomicVariables.Count > 0)
                    && !Helpers.JsonUtil.IsSupplied(args?["rules"]) && !Helpers.JsonUtil.IsSupplied(args?["source"]);
                using (IDisposable suppression = propertyOnlySave
                    ? PropertyApplyOnSaveRule.Begin(obj, null, ((JObject)args["properties"]).Properties().Select(p => p.Name))
                    : null)
                {
                    obj.EnsureSave(check: false);
                }
                WriteService.NotePerTargetWrite(name);

                bool validate = args?["validate"]?.ToObject<bool?>() ?? false;
                string validationMode = args?["validationMode"]?.ToString() ?? "specify";
                JObject specification = null;
                bool specified = false, generated = false;
                if (validate)
                {
                    if (!validationMode.Equals("specify", StringComparison.OrdinalIgnoreCase))
                        throw new AtomicFailure("validate", "Unsupported validationMode '" + validationMode + "'. Use specify.", null);
                    specification = JObject.Parse(_build.Specify(name));
                    string status = specification["status"]?.ToString();
                    if (string.Equals(status, "error", StringComparison.OrdinalIgnoreCase))
                        throw new AtomicFailure("specify", "Specification failed.", specification);
                    if (string.Equals(status, "accepted", StringComparison.OrdinalIgnoreCase))
                    {
                        string taskId = specification["taskId"]?.ToString();
                        var accepted = new JObject
                        {
                            ["created"] = created,
                            ["updated"] = !created,
                            ["saved"] = true,
                            ["specified"] = false,
                            ["generated"] = false,
                            ["rolledBack"] = false,
                            ["validationPending"] = true,
                            ["taskId"] = taskId,
                            ["pollTarget"] = string.IsNullOrWhiteSpace(taskId) ? null : "op:" + taskId,
                            ["validation"] = specification,
                            ["phases"] = phases,
                            ["diagnostics"] = new JArray(),
                            ["plan"] = plan
                        };
                        return McpResponse.Ok(target: name, code: "AtomicValidationAccepted", result: accepted);
                    }
                    specified = string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase)
                             || string.Equals(status, "success", StringComparison.OrdinalIgnoreCase);
                    generated = specified;
                }

                var persistedObject = _objects.FindObject(name, type);
                string version = persistedObject == null ? null : WriteService.ComputeVersionToken(persistedObject);
                var result = new JObject
                {
                    ["created"] = created,
                    ["updated"] = !created,
                    ["saved"] = true,
                    ["specified"] = specified,
                    ["generated"] = generated,
                    ["rolledBack"] = false,
                    ["versionToken"] = version,
                    ["phases"] = phases,
                    ["diagnostics"] = new JArray(),
                    ["plan"] = plan
                };
                if (specification != null) result["validation"] = specification;
                return McpResponse.Ok(target: name, code: "AtomicAuthoringCompleted", result: result);
            }
            catch (Exception ex)
            {
                bool rollbackRequested = args?["rollbackOnFailure"]?.ToObject<bool?>() ?? true;
                JObject rollback = rollbackRequested ? Rollback(name, type, created, snapshot) : new JObject { ["attempted"] = false, ["succeeded"] = false };
                JObject detail = ex is AtomicFailure af && af.Detail != null ? af.Detail : null;
                string phase = ex is AtomicFailure atomic ? atomic.Phase : "apply";
                return McpResponse.Err(code: "AtomicAuthoringFailed", message: ex.Message, target: name,
                    extra: new JObject
                    {
                        ["saved"] = false,
                        ["specified"] = false,
                        ["generated"] = false,
                        ["rolledBack"] = rollback["succeeded"]?.ToObject<bool?>() == true,
                        ["rollback"] = rollback,
                        ["diagnostics"] = new JArray(new JObject
                        {
                            ["code"] = "AtomicPhaseFailed", ["object"] = name, ["member"] = phase,
                            ["message"] = ex.Message, ["detail"] = detail
                        }),
                        ["phases"] = phases
                    });
            }
        }

        internal static void RecordVariablePhase(JArray phases, int added, int existed, int failed, JArray outcomes)
        {
            var phase = AtomicCreateService.DescribeVariablePhase(added, existed, failed, outcomes);
            phase["phase"] = "variables";
            phases.Add(phase);
            EnsureSuccess(phase, "variables");
        }

        private static void EnsureSuccess(JObject phase, string name)
        {
            if (string.Equals((string)phase?["status"], "error", StringComparison.OrdinalIgnoreCase))
                throw new AtomicFailure(name, "One or more requested variables could not be added.", phase);
        }

        private JArray Preflight(JObject args)
        {
            var errors = new JArray();
            AddTextPreflightDiagnostic(errors, args?["name"]?.ToString(), "rules", JoinText(args?["rules"]));
            AddTextPreflightDiagnostic(errors, args?["name"]?.ToString(), "source", JoinText(args?["source"]));
            foreach (JObject variable in (args?["variables"] as JArray ?? new JArray()).OfType<JObject>())
            {
                string typeName = (variable["basedOnAttribute"] ?? variable["basedOn"] ?? variable["typeName"])?.ToString();
                if (string.IsNullOrWhiteSpace((variable["name"] ?? variable["varName"])?.ToString()))
                    errors.Add(Diagnostic("MissingVariableName", args?["name"]?.ToString(), "variables", "A variable is missing name/varName."));
                if (!string.IsNullOrWhiteSpace(typeName))
                {
                    // issue #281: Attribute references ("Attribute:X" or bare
                    // basedOnAttribute) are validated against the KB, not the
                    // primitive synonym table.
                    string attrCheck = typeName.Trim();
                    if (VariableInjector.TryParseAttributeReference(attrCheck, out string parsedAttrCheck))
                        attrCheck = parsedAttrCheck;
                    else if (variable["basedOnAttribute"] != null)
                        attrCheck = attrCheck.TrimStart('&');
                    bool isAttrRef = VariableInjector.TryParseAttributeReference(typeName, out _)
                        || variable["basedOnAttribute"] != null;
                    if (isAttrRef)
                    {
                        if (_objects.FindObject(attrCheck) == null)
                            errors.Add(Diagnostic("ReferencedTypeNotFound", args?["name"]?.ToString(), typeName, "Referenced Attribute was not found."));
                        continue;
                    }
                    var resolution = VariableTypeResolver.Resolve(typeName);
                    if (!resolution.Recognized)
                        errors.Add(Diagnostic("UnknownVariableType", args?["name"]?.ToString(), typeName, "Unknown variable type."));
                    else if (resolution.CanonicalType == "AttributeReference"
                        && _objects.FindObject(resolution.AttributeName ?? typeName) == null)
                        errors.Add(Diagnostic("ReferencedTypeNotFound", args?["name"]?.ToString(), typeName, "Referenced Attribute was not found."))
;
                    else if (resolution.CanonicalType == "DomainReference"
                          && _objects.FindObject(resolution.DomainName ?? typeName) == null
                          && !VariableInjector.IsBuiltinUserDefinedType(typeName))
                        errors.Add(Diagnostic("ReferencedTypeNotFound", args?["name"]?.ToString(), typeName, "Referenced Domain, SDT or Business Component was not found."));
                }
            }
            return errors;
        }

        private static void AddTextPreflightDiagnostic(JArray errors, string objectName, string member, string text)
        {
            var fieldError = TextPayloadGuard.BuildFieldError(member, text);
            if (fieldError == null) return;

            var diagnostic = Diagnostic(TextPayloadGuard.ErrorCode, objectName, member, TextPayloadGuard.BuildMessage(member));
            diagnostic["literalSequences"] = fieldError["literalSequences"]?.DeepClone();
            diagnostic["hasActualLineBreaks"] = fieldError["hasActualLineBreaks"];
            errors.Add(diagnostic);
        }

        private JObject CaptureSnapshot(string name, string type, JObject args)
        {
            var snapshot = JObject.Parse(_objects.ReadObjectSourceParts(name, new[] { "Variables", "Rules", "Source" }, type));
            if (args?["properties"] is JObject requestedProperties)
            {
                JObject read = JObject.Parse(_properties.GetProperties(name, null, type));
                var values = new JObject();
                foreach (JObject p in (read["result"]?["properties"] as JArray ?? new JArray()).OfType<JObject>())
                    if (requestedProperties[p["name"]?.ToString()] != null) values[p["name"].ToString()] = p["value"]?.DeepClone();
                snapshot["propertyValues"] = values;
            }
            return snapshot;
        }

        private JObject Rollback(string name, string type, bool created, JObject snapshot)
        {
            var attempts = new JArray(); bool success = true;
            try
            {
                if (created)
                {
                    var existingOnDisk = _objects.FindObject(name, type);
                    if (existingOnDisk != null)
                    {
                        JObject deletion = JObject.Parse(_objects.DeleteObject(name, type, true, false));
                        attempts.Add(new JObject { ["phase"] = "delete-created-object", ["response"] = deletion });
                        success = IsSuccess(deletion);
                    }
                    else
                    {
                        attempts.Add(new JObject { ["phase"] = "discard-in-memory", ["status"] = "ok" });
                        success = true;
                    }
                }
                else if (snapshot != null)
                {
                    var parts = snapshot["parts"] as JObject;
                    foreach (string part in new[] { "Variables", "Rules", "Source" })
                        if (parts?[part] != null)
                        {
                            JObject restored = JObject.Parse(WritePart(name, part, parts[part].ToString()));
                            attempts.Add(new JObject { ["phase"] = "restore:" + part, ["response"] = restored });
                            success &= IsSuccess(restored);
                        }
                    foreach (var p in (snapshot["propertyValues"] as JObject)?.Properties() ?? Enumerable.Empty<JProperty>())
                    {
                        JObject restored = JObject.Parse(_properties.SetProperty(name, p.Name, p.Value.ToString(), null, type));
                        attempts.Add(new JObject { ["phase"] = "restore-property:" + p.Name, ["response"] = restored });
                        success &= IsSuccess(restored);
                    }
                }
            }
            catch (Exception ex) { success = false; attempts.Add(new JObject { ["phase"] = "rollback", ["error"] = ex.Message }); }
            return new JObject { ["attempted"] = true, ["succeeded"] = success, ["attempts"] = attempts };
        }

        private string WritePart(string name, string part, string content) => _write.WriteObject(name, new JObject
        {
            ["part"] = part, ["mode"] = "full", ["content"] = content, ["validate"] = true
        });
        private static bool IsSuccess(JObject response)
        {
            string s = response?["status"]?.ToString();
            return string.Equals(s, "ok", StringComparison.OrdinalIgnoreCase)
                || string.Equals(s, "success", StringComparison.OrdinalIgnoreCase)
                || string.Equals(s, "accepted", StringComparison.OrdinalIgnoreCase);
        }
        private static string JoinText(JToken token) => token is JArray a ? string.Join(Environment.NewLine, a.Select(x => x.ToString())) : token?.ToString();
        private static JObject Diagnostic(string code, string obj, string member, string message) => new JObject
        { ["code"] = code, ["object"] = obj, ["member"] = member, ["message"] = message };
        private static JObject BuildPlan(JObject args, bool existing) => new JObject
        {
            ["operation"] = existing ? "update" : "create",
            ["type"] = args?["type"]?.ToString(), ["name"] = args?["name"]?.ToString(),
            ["variables"] = (args?["variables"] as JArray)?.Count ?? 0,
            // IsSupplied, not != null: an explicit `"rules": null` read back as a live
            // JValue of type Null, so the plan reported a rule set the caller never
            // sent, and reported it to whoever reads the plan back.
            ["rules"] = Helpers.JsonUtil.IsSupplied(args?["rules"]),
            ["source"] = Helpers.JsonUtil.IsSupplied(args?["source"]),
            ["properties"] = (args?["properties"] as JObject)?.Count ?? 0,
            ["validate"] = args?["validate"]?.ToObject<bool?>() ?? false,
            ["validationMode"] = args?["validationMode"]?.ToString() ?? "specify"
        };

        private sealed class AtomicFailure : Exception
        {
            public string Phase { get; } public JObject Detail { get; }
            public AtomicFailure(string phase, string message, JObject detail) : base(message) { Phase = phase; Detail = detail; }
        }
    }
}
