using System;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;

namespace GxMcp.Worker.Services
{
    // Variable CRUD (add/delete/modify) extracted from WriteService.cs (plan 007).
    // Pure move, no logic changes — see plans/007-decompose-writeservice.md.
    public partial class WriteService
    {
        private string ResolveVariableTarget(string target, ref string varName,
            out global::Artech.Architecture.Common.Objects.KBObject obj,
            out global::Artech.Genexus.Common.Parts.VariablesPart varPart,
            out global::Artech.Genexus.Common.Variable existing)
        {
            obj = null; varPart = null; existing = null;
            if (string.IsNullOrEmpty(varName)) return McpResponse.Err(
                code: "MissingParameter",
                message: "Variable name is required.",
                hint: "Pass the variable name without the leading '&'.",
                nextSteps: new JArray(McpResponse.NextStep(
                    tool: "genexus_read",
                    args: new JObject { ["name"] = target, ["part"] = "Variables" },
                    why: "Lists current variables on the object.")),
                target: target);
            varName = varName.TrimStart('&');

            obj = _objectService.FindObject(target);
            if (obj == null) return CreateWriteError("Object not found", target, "Variables", "The requested object is not available in the active Knowledge Base.");

            // v2.3.8 Task 4.4 — kind-aware accessor. Falls back through typed Get<>,
            // name-based candidates, and reflective Variables-property discovery so that
            // WebPanel / Transaction / WorkPanel / DataProvider resolve symmetrically.
            varPart = GxMcp.Worker.Structure.PartAccessor.GetVariablesPart(obj);
            if (varPart == null) return CreateWriteError("Variables part not found", target, "Variables", "The object does not expose a Variables part.", obj);

            string searchName = varName;
            existing = varPart.Variables.FirstOrDefault(v => string.Equals(v.Name, searchName, StringComparison.OrdinalIgnoreCase));
            return null;
        }

        /// Batch variant: removes all `varNames` from `target`, calling EnsureSave / ScheduleFlush once.
        /// Skips framework-managed names. Returns per-name outcomes plus aggregate counts.
        public string DeleteVariables(string target, System.Collections.Generic.IEnumerable<string> varNames)
        {
            string raw = DeleteVariablesInternal(target, varNames);
            MarkDirtyIfSuccess(raw, target);
            return WrapWithPersistedState(raw, target, "Variables", GxMcp.Worker.Helpers.WriteResultMeta.TypedWriter);
        }

        private string DeleteVariablesInternal(string target, System.Collections.Generic.IEnumerable<string> varNames)
        {
            try
            {
                if (varNames == null) return McpResponse.Ok(target: target, code: "WriteNoChange");
                string firstName = null;
                foreach (var n in varNames) { firstName = n; break; }
                if (firstName == null) return McpResponse.Ok(target: target, code: "WriteNoChange");

                string scratch = firstName;
                var err = ResolveVariableTarget(target, ref scratch, out var obj, out var varPart, out _);
                if (err != null) return err;

                var outcomes = new JArray();
                int removed = 0, refused = 0, missing = 0;
                foreach (var raw in varNames)
                {
                    if (string.IsNullOrEmpty(raw)) continue;
                    var name = raw.TrimStart('&');
                    if (GxMcp.Worker.Helpers.FrameworkManagedVariables.IsManaged(name))
                    {
                        outcomes.Add(new JObject { ["name"] = name, ["status"] = "Refused", ["reason"] = "framework-managed" });
                        refused++;
                        continue;
                    }
                    var hit = varPart.Variables.FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase));
                    if (hit == null) { outcomes.Add(new JObject { ["name"] = name, ["itemStatus"] = "NotFound" }); missing++; continue; }
                    varPart.Variables.Remove(hit);
                    outcomes.Add(new JObject { ["name"] = name, ["status"] = "Removed" });
                    removed++;
                }

                if (removed > 0)
                {
                    obj.EnsureSave();
                    ScheduleFlush();
                }

                return McpResponse.Ok(
                    target: target,
                    code: removed > 0 ? "AttributeRemoved" : "WriteNoChange",
                    result: new JObject
                    {
                        ["counts"] = new JObject { ["removed"] = removed, ["refused"] = refused, ["missing"] = missing },
                        ["outcomes"] = outcomes,
                    });
            }
            catch (Exception ex)
            {
                return McpResponse.Err(
                    code: "DeleteVariableFailed",
                    message: ex.Message,
                    hint: "Check that the variable names are correct and not framework-managed.",
                    nextSteps: new JArray(McpResponse.NextStep(
                        tool: "genexus_read",
                        args: new JObject { ["name"] = target, ["part"] = "Variables" },
                        why: "Lists the current variables so you can verify names before retrying.")),
                    target: target);
            }
        }

        public string DeleteVariable(string target, string varName, bool dryRun = false)
        {
            if (dryRun)
                return McpResponse.Ok(
                    target: target,
                    code: "DryRun",
                    result: new Newtonsoft.Json.Linq.JObject
                    {
                        ["preview"] = new Newtonsoft.Json.Linq.JObject
                        {
                            ["action"] = "delete",
                            ["target"] = target,
                            ["varName"] = varName
                        }
                    });
            var raw = DeleteVariableInternal(target, varName);
            MarkDirtyIfSuccess(raw, target);
            return WrapWithPersistedState(raw, target, "Variables", GxMcp.Worker.Helpers.WriteResultMeta.TypedWriter);
        }

        // v2.6.9 — parse the typed-writer raw response for a confirmed mutation.
        // WriteNoChange and changed=false are not writes, even though the typed
        // writer reports them with status=ok.
        internal static void MarkDirtyIfSuccess(string raw, string target)
        {
            if (string.IsNullOrWhiteSpace(raw) || string.IsNullOrWhiteSpace(target)) return;
            try
            {
                var jo = Newtonsoft.Json.Linq.JObject.Parse(raw);
                string status = jo?["status"]?.ToString();
                if (string.Equals(status, "Success", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(status, "PartialSuccess", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(status, "partial", StringComparison.OrdinalIgnoreCase))
                {
                    string code = jo["code"]?.ToString();
                    if (string.Equals(code, "WriteNoChange", StringComparison.OrdinalIgnoreCase)
                        || jo["changed"]?.Value<bool?>() == false)
                        return;
                    NotePerTargetWrite(target);
                }
            }
            catch { /* best-effort */ }
        }

        private string DeleteVariableInternal(string target, string varName)
        {
            try
            {
                var err = ResolveVariableTarget(target, ref varName, out var obj, out var varPart, out var existing);
                if (err != null) return err;

                if (existing == null)
                    return McpResponse.Ok(
                        target: target,
                        code: "WriteNoChange",
                        result: new JObject { ["details"] = "Variable not present; nothing to delete." });

                if (GxMcp.Worker.Helpers.FrameworkManagedVariables.IsManaged(varName))
                {
                    return McpResponse.Err(
                        code: "FrameworkManagedVariable",
                        message: "Framework-managed variable",
                        hint: "Variable '&" + varName + "' is managed by " + GxMcp.Worker.Helpers.FrameworkManagedVariables.GetManagedBy(varName) + " and will be re-injected on save. Do not delete it.",
                        nextSteps: new JArray(McpResponse.NextStep(
                            tool: "genexus_read",
                            args: new JObject { ["name"] = target, ["part"] = "Variables" },
                            why: "Lists the current variables so you can verify which ones are user-defined.")),
                        target: target);
                }

                // Snapshot the var's internal id BEFORE Remove() — some SDK
                // builds null out the parent reference once a variable is
                // detached, which would otherwise lose the id needed to scan
                // for ghost bindings if the save throws.
                int? existingId = null;
                try
                {
                    int idx = 1;
                    foreach (var v in varPart.Variables)
                    {
                        if (ReferenceEquals(v, existing))
                        {
                            existingId = GxMcp.Worker.Helpers.VariableInjector.GetVariableInternalId(v, idx);
                            break;
                        }
                        idx++;
                    }
                }
                catch { /* best-effort */ }

                try
                {
                    varPart.Variables.Remove(existing);
                    obj.EnsureSave();
                    ScheduleFlush();
                    return McpResponse.Ok(target: target, code: "AttributeRemoved");
                }
                catch (Exception saveEx)
                {
                    var boundResp = TryBuildBoundToControlsError(saveEx, obj, varName, existingId);
                    if (boundResp != null) return boundResp;
                    throw;
                }
            }
            catch (Exception ex)
            {
                return McpResponse.Err(
                    code: "DeleteVariableFailed",
                    message: ex.Message,
                    hint: "Check that the variable is not bound to controls or used by generated code.",
                    nextSteps: new JArray(McpResponse.NextStep(
                        tool: "genexus_read",
                        args: new JObject { ["name"] = target, ["part"] = "Variables" },
                        why: "Lists current variables to verify state before retrying.")),
                    target: target);
            }
        }

        // Task 4.5 — When the SDK rejects a delete/modify because the variable
        // is still bound to a control, surface a structured envelope instead of
        // a raw error string. We use a heuristic message match because the
        // concrete SDK exception type that signals this varies across GeneXus
        // builds and isn't documented; the regex catches both EN and PT-BR
        // phrasings observed in friction reports.
        private static readonly System.Text.RegularExpressions.Regex _boundToControlsRegex =
            new System.Text.RegularExpressions.Regex(
                @"(\[var:\d+\])|(control reference)|(referência de controle)|(bound to control)|(is being used)|(está sendo (usada|utilizada))",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase |
                System.Text.RegularExpressions.RegexOptions.Compiled);

        internal string TryBuildBoundToControlsError(Exception ex, global::Artech.Architecture.Common.Objects.KBObject obj, string varName, int? variableId)
        {
            if (ex == null) return null;
            string flat = FlattenExceptionMessages(ex);
            if (string.IsNullOrEmpty(flat) || !_boundToControlsRegex.IsMatch(flat)) return null;

            string resolved = GxMcp.Worker.Helpers.WebFormSchemaHints.ResolveVarBindings(flat, obj);

            var bindings = new JArray();
            try
            {
                if (variableId.HasValue && variableId.Value > 0)
                {
                    string xml = GxMcp.Worker.Helpers.WebFormXmlHelper.ReadEditableXml(obj);
                    var hits = GxMcp.Worker.Helpers.WebFormSchemaHints.FindVarBindings(xml, variableId.Value);
                    foreach (var b in hits)
                    {
                        bindings.Add(new JObject
                        {
                            ["element"] = b.Element,
                            ["attribute"] = b.Attribute,
                            ["controlId"] = b.ControlId,
                            ["controlName"] = b.ControlName,
                        });
                    }
                }
            }
            catch { /* best-effort — bindings list is advisory */ }

            return McpResponse.Err(
                code: "BoundToControls",
                message: $"Variable '&{varName}' is bound to one or more controls; remove the bindings before deleting/modifying.",
                hint: "Remove or rebind the controls listed in 'bindings' from the WebForm layout before deleting/modifying this variable.",
                nextSteps: new JArray(McpResponse.NextStep(
                    tool: "genexus_read",
                    args: new JObject { ["name"] = resolved ?? varName, ["part"] = "WebForm" },
                    why: "Read the WebForm layout to locate and remove the controls bound to this variable.")),
                target: null,
                extra: new JObject { ["details"] = resolved, ["bindings"] = bindings });
        }

        private static string FlattenExceptionMessages(Exception ex)
        {
            var sb = new System.Text.StringBuilder();
            for (var cur = ex; cur != null; cur = cur.InnerException)
            {
                if (sb.Length > 0) sb.Append(" | ");
                sb.Append(cur.Message);
            }
            return sb.ToString();
        }

        // issue #32 item 1 — shared SDK construction used by AddVariable (single) and
        // AddVariables (batch). Result of building one typed variable into a part.
        private enum VarBuildResult { Added, DomainNotFound, DomainNotPersistable, PrimitiveNotApplied }

        internal sealed class ExpectedDomainBinding
        {
            public string VarName { get; set; }
            public string DomainName { get; set; }
            public global::Artech.Udm.Framework.EntityKey DomainKey { get; set; }
            public global::Artech.Genexus.Common.eDBType EffectiveType { get; set; }
            public int Length { get; set; }
            public int Decimals { get; set; }
        }

        // Builds one Variable from an already-validated TypeResolution and adds it to
        // varPart IN MEMORY (no save, no envelope — the caller owns those). Returns
        // DomainNotFound when the typeName looked like an SDT/BC/Domain reference but the
        // SDK couldn't resolve it in the KB, so the caller can surface UnknownType.
        private VarBuildResult BuildResolvedVariableInto(
            global::Artech.Genexus.Common.Parts.VariablesPart varPart, string varName,
            GxMcp.Worker.Helpers.TypeResolution resolution, string resolvedTypeForSdk,
            int? resolvedLength, int? resolvedDecimals,
            int? length, int? decimals, bool? collection, string originalTypeName,
            out ExpectedDomainBinding domainBinding, out string bindFailure)
        {
            domainBinding = null;
            bindFailure = null;
            var newVar = new global::Artech.Genexus.Common.Variable(varPart);
            newVar.Name = varName;

            if (resolution != null && resolution.CanonicalType != "DomainReference"
                && VariableInjector.TryParseDbType(resolvedTypeForSdk, out var dbType))
            {
                newVar.Type = dbType;
                try
                {
                    // Explicit length/decimals args (issue #28 item 8) win over the
                    // value parsed out of typeName; otherwise fall back to the parsed one.
                    int? effLen = length ?? resolvedLength;
                    int? effDec = decimals ?? resolvedDecimals;
                    if (effLen.HasValue) newVar.Length = effLen.Value;
                    if (effDec.HasValue) newVar.Decimals = effDec.Value;
                }
                catch { /* best-effort — SDK may reject for some types */ }
            }
            else
            {
                // issue #34: a recognized primitive (resolver said so, not a DomainReference)
                // that TryParseDbType can't map is a mapping bug, not a KB-object reference.
                // Never fall through to add a default-typed (NUMERIC) variable and report
                // success — surface it so the caller sees the type wasn't applied.
                if (resolution != null && resolution.CanonicalType != "DomainReference")
                {
                    return VarBuildResult.PrimitiveNotApplied;
                }

                var resolvedDomain = resolution != null && resolution.CanonicalType == "DomainReference"
                    ? VariableInjector.ResolveDomain(varPart.Model, resolvedTypeForSdk, varPart.KBObject?.Module)
                    : null;
                var targetObj = resolvedDomain ?? VariableInjector.ResolveTypeObject(varPart.Model, resolvedTypeForSdk);
                // Dotted SDT-item type (e.g. "Messages.Message") — a single element of a collection
                // SDT. Tried BEFORE the ResolveTypeObject binding, which strips ".Message" and binds
                // the whole (collection) SDT, collapsing item and collection to the same variable.
                // The SDK's type-picker resolver returns the item-level AttCustomType for the dotted
                // form. Only fires for dotted names, so the plain-SDT path below is unaffected.
                if (VariableInjector.TryBindSdtItemType(newVar, resolvedTypeForSdk))
                {
                }
                else if (targetObj != null)
                {
                    if (targetObj is global::Artech.Genexus.Common.Objects.Domain dom)
                    {
                        if (!VariableInjector.BindVariableToDomain(newVar, dom, out bindFailure))
                            return VarBuildResult.DomainNotPersistable;
                        domainBinding = new ExpectedDomainBinding
                        {
                            VarName = varName,
                            DomainName = dom.QualifiedName?.ToString() ?? dom.Name,
                            DomainKey = dom.Key,
                            EffectiveType = newVar.Type,
                            Length = newVar.Length,
                            Decimals = newVar.Decimals
                        };
                    }
                    else if (targetObj.TypeDescriptor.Name.Equals("SDT", StringComparison.OrdinalIgnoreCase))
                        VariableInjector.BindVariableToSdt(newVar, targetObj);
                    else if (targetObj is global::Artech.Genexus.Common.Objects.Transaction trn && trn.IsBusinessComponent)
                        VariableInjector.BindVariableToBC(newVar, targetObj);
                }
                // Built-in GeneXus data types (HttpClient, WebSession, Location, ...) aren't KB
                // objects, so ResolveTypeObject can't find them — resolve by name through the SDK's
                // own type registry (issue #45). Covers all ~137 built-ins generically.
                else if (VariableInjector.TryBindGenexusDataType(newVar, resolvedTypeForSdk))
                {
                }
                // Legacy hardcoded fallback (issue #33) — only if the SDK registry path above is
                // unavailable in a given headless build.
                else if (VariableInjector.TryBindBuiltinUserDefinedType(newVar, resolvedTypeForSdk))
                {
                }
                else if (resolution != null && resolution.CanonicalType == "DomainReference"
                         && !string.IsNullOrEmpty(originalTypeName) && !originalTypeName.StartsWith("&"))
                {
                    return VarBuildResult.DomainNotFound;
                }
            }
            if (collection == true) { try { newVar.IsCollection = true; } catch { /* not all types collectible */ } }
            varPart.Variables.Add(newVar);
            return VarBuildResult.Added;
        }

        // No typeName: CreateVariable inherits a same-named attribute's type (issue #28
        // item 11) or applies the naming heuristic. Explicit length/decimals/collection
        // args still override the result. Adds to varPart in memory (no save).
        private void AddInferredVariableInto(global::Artech.Genexus.Common.Parts.VariablesPart varPart,
            string varName, int? length, int? decimals, bool? collection)
        {
            var newVar = VariableInjector.CreateVariable(varPart, varName);
            try
            {
                if (length.HasValue) newVar.Length = length.Value;
                if (decimals.HasValue) newVar.Decimals = decimals.Value;
            }
            catch { /* best-effort */ }
            if (collection == true) { try { newVar.IsCollection = true; } catch { } }
            varPart.Variables.Add(newVar);
        }

        // issue #32 item 1 — batch add. Resolves the target once and adds every variable in
        // `variables` before a single EnsureSave / ScheduleFlush. Each item is
        // { varName|name, typeName?, length?, decimals?, collection? }. Per-item outcomes let
        // the agent see which vars were Added / already Exist / Failed without N round-trips.
        public string AddVariables(string target, JArray variables, bool dryRun = false)
        {
            if (dryRun)
            {
                var preview = new JArray();
                if (variables != null)
                    foreach (var v in variables) preview.Add(v.DeepClone());
                return McpResponse.Ok(
                    target: target,
                    code: "DryRun",
                    result: new JObject
                    {
                        ["preview"] = new JObject
                        {
                            ["action"] = "add",
                            ["target"] = target,
                            ["variables"] = preview
                        }
                    });
            }
            var raw = AddVariablesInternal(target, variables);
            MarkDirtyIfSuccess(raw, target);
            return WrapWithPersistedState(raw, target, "Variables", GxMcp.Worker.Helpers.WriteResultMeta.TypedWriter);
        }

        private string AddVariablesInternal(string target, JArray variables)
        {
            try
            {
                if (variables == null || variables.Count == 0)
                    return McpResponse.Ok(target: target, code: "WriteNoChange",
                        result: new JObject { ["details"] = "No variables provided." });

                // Resolve the target object / VariablesPart once for the whole batch.
                string scratch = "_";
                var err = ResolveVariableTarget(target, ref scratch, out var obj, out var varPart, out _);
                PopulateVariablesInto(varPart, variables, out var outcomes, out int added, out int existed, out int failed, out var domainBound, out var addedNames);

                if (added > 0)
                {
                    ForceSaveVariableOwner(obj);
                    ScheduleFlush(force: true);

                    // Keep the v2.37.0 Domain-specific contract before applying the broader
                    // typed reload verification below.
                    var verifyErr = VerifyDomainReferencesPersisted(target, domainBound);
                    if (verifyErr != null)
                    {
                        foreach (var addedName in addedNames)
                        {
                            var addedVariable = varPart.Variables.FirstOrDefault(v =>
                                string.Equals(v.Name, addedName, StringComparison.OrdinalIgnoreCase));
                            if (addedVariable != null) varPart.Variables.Remove(addedVariable);
                        }
                        obj.EnsureSave();
                        ScheduleFlush();
                        return verifyErr;
                    }
                    // Re-resolve after the synchronous flush. Per-item outcomes are the
                    // contract: a variable that vanished or lost its Domain binding is a
                    // failed persistence, never an Added success.
                    string refreshName = "_";
                    ResolveVariableTarget(target, ref refreshName, out _, out var persistedPart, out _);
                    foreach (JObject outcome in outcomes.OfType<JObject>().Where(o => string.Equals(o["status"]?.ToString(), "Added", StringComparison.OrdinalIgnoreCase)))
                    {
                        string persistedName = outcome["name"]?.ToString();
                        JObject requestItem = variables.OfType<JObject>().FirstOrDefault(v =>
                            string.Equals((v["varName"] ?? v["name"])?.ToString()?.TrimStart('&'), persistedName, StringComparison.OrdinalIgnoreCase));
                        string requestedType = (requestItem?["basedOn"] ?? requestItem?["typeName"])?.ToString();
                        string verifyError = VerifyPersistedVariable(persistedPart, persistedName, requestedType);
                        if (verifyError != null)
                        {
                            outcome["status"] = "NotPersisted";
                            outcome["reason"] = verifyError;
                            added--; failed++;
                        }
                        else outcome["persisted"] = true;
                    }
                    // issue #59: verify EVERY added variable (not just Domain-bound ones)
                    // actually landed in the persisted Variables part. A silent drop is
                    // reported as VariableAddNotPersisted instead of a false VariableAdded.
                    var perItemErr = VerifyVariablesPersisted(target, addedNames);
                    if (perItemErr != null) return perItemErr;
                }

                if (outcomes.OfType<JObject>().Any(o => string.Equals(o["status"]?.ToString(), "NotPersisted", StringComparison.OrdinalIgnoreCase)))
                    return McpResponse.Err(code: "VariableNotPersisted",
                        message: "One or more variables did not survive the save/reload cycle.", target: target,
                        extra: new JObject
                        {
                            ["counts"] = new JObject { ["added"] = added, ["existed"] = existed, ["failed"] = failed },
                            ["outcomes"] = outcomes, ["saved"] = false
                        });

                return McpResponse.Ok(
                    target: target,
                    code: added > 0 ? "VariableAdded" : "WriteNoChange",
                    result: new JObject
                    {
                        ["counts"] = new JObject { ["added"] = added, ["existed"] = existed, ["failed"] = failed },
                        ["outcomes"] = outcomes
                    });
            }
            catch (Exception ex)
            {
                return McpResponse.Err(
                    code: "AddVariableFailed",
                    message: ex.Message,
                    hint: "Verify each variable name and type. Check that the object exists and has a Variables part.",
                    nextSteps: new JArray(McpResponse.NextStep(
                        tool: "genexus_read",
                        args: new JObject { ["name"] = target, ["part"] = "Variables" },
                        why: "Lists current variables to verify existence before retrying.")),
                    target: target);
            }
        }

        internal void PopulateVariablesInto(
            global::Artech.Genexus.Common.Parts.VariablesPart varPart,
            JArray variables,
            out JArray outcomes,
            out int added,
            out int existed,
            out int failed,
            out System.Collections.Generic.List<ExpectedDomainBinding> domainBound,
            out System.Collections.Generic.List<string> addedNames)
        {
            outcomes = new JArray();
            added = 0; existed = 0; failed = 0;
            domainBound = new System.Collections.Generic.List<ExpectedDomainBinding>();
            addedNames = new System.Collections.Generic.List<string>();

            if (varPart == null || variables == null || variables.Count == 0) return;

            foreach (var item in variables)
            {
                var jo = item as JObject;
                if (jo == null)
                {
                    failed++;
                    outcomes.Add(new JObject { ["status"] = "Failed", ["reason"] = "Item is not an object." });
                    continue;
                }

                string vName = (jo["varName"] ?? jo["name"])?.ToString();
                if (string.IsNullOrWhiteSpace(vName))
                {
                    failed++;
                    outcomes.Add(new JObject { ["status"] = "Failed", ["reason"] = "Missing varName." });
                    continue;
                }
                vName = vName.TrimStart('&');

                string vType = jo["typeName"]?.ToString();
                string vBasedOn = jo["basedOn"]?.ToString();
                int? vLen = jo["length"]?.ToObject<int?>();
                int? vDec = jo["decimals"]?.ToObject<int?>();
                bool? vColl = jo["collection"]?.ToObject<bool?>();

                if (varPart.Variables.Any(v => string.Equals(v.Name, vName, StringComparison.OrdinalIgnoreCase)))
                {
                    existed++;
                    outcomes.Add(new JObject { ["name"] = vName, ["itemStatus"] = "Exists" });
                    continue;
                }

                // Type resolution
                GxMcp.Worker.Helpers.TypeResolution res = null;
                string rSdk = vType;
                int? rLen = null, rDec = null;
                if (!string.IsNullOrEmpty(vType))
                {
                    res = GxMcp.Worker.Helpers.VariableTypeResolver.Resolve(vType);
                    if (!res.Recognized)
                    {
                        failed++;
                        outcomes.Add(new JObject
                        {
                            ["name"] = vName,
                            ["status"] = "Failed",
                            ["reason"] = "UnknownType",
                            ["suggestion"] = res.Suggestion
                        });
                        continue;
                    }
                    if (res.CanonicalType == "DomainReference" && !string.IsNullOrEmpty(res.DomainName))
                    {
                        rSdk = res.DomainName;
                    }
                    else { rLen = res.Length; rDec = res.Decimals; rSdk = res.CanonicalType; }
                }
                if (!string.IsNullOrWhiteSpace(vBasedOn))
                {
                    rSdk = vBasedOn.Trim();
                    res = new GxMcp.Worker.Helpers.TypeResolution
                    {
                        Recognized = true,
                        CanonicalType = "DomainReference",
                        DomainName = rSdk,
                        Suggestion = rSdk
                    };
                }

                try
                {
                    if (!string.IsNullOrEmpty(vType) || !string.IsNullOrWhiteSpace(vBasedOn))
                    {
                        var batchBuild = BuildResolvedVariableInto(varPart, vName, res, rSdk, rLen, rDec, vLen, vDec, vColl, vBasedOn ?? vType,
                            out var domainBinding, out var bindFailure);
                        if (batchBuild == VarBuildResult.DomainNotFound)
                        {
                            failed++;
                            outcomes.Add(new JObject
                            {
                                ["name"] = vName,
                                ["status"] = "Failed",
                                ["reason"] = "UnknownType",
                                ["details"] = $"Type '{vType}' not found in KB."
                            });
                            continue;
                        }
                        if (batchBuild == VarBuildResult.PrimitiveNotApplied)
                        {
                            failed++;
                            outcomes.Add(new JObject
                            {
                                ["name"] = vName,
                                ["status"] = "Failed",
                                ["reason"] = "TypeNotApplied",
                                ["details"] = $"Recognized type '{vType}' could not be applied (SDK type-map gap); variable skipped."
                            });
                            continue;
                        }
                        if (batchBuild == VarBuildResult.DomainNotPersistable)
                        {
                            failed++;
                            outcomes.Add(new JObject
                            {
                                ["name"] = vName,
                                ["status"] = "Failed",
                                ["reason"] = "VariableTypeNotPersisted",
                                ["details"] = bindFailure ?? "The Domain could not be represented as a native SDK reference."
                            });
                            continue;
                        }
                        if (domainBinding != null) domainBound.Add(domainBinding);
                    }
                    else
                    {
                        AddInferredVariableInto(varPart, vName, vLen, vDec, vColl);
                    }
                    added++;
                    addedNames.Add(vName);
                    outcomes.Add(new JObject { ["name"] = vName, ["status"] = "Added" });
                }
                catch (Exception exItem)
                {
                    failed++;
                    outcomes.Add(new JObject { ["name"] = vName, ["status"] = "Failed", ["reason"] = exItem.Message });
                }
            }
        }

        public string AddVariable(string target, string varName, string typeName = null, bool dryRun = false,
            int? length = null, int? decimals = null, bool? collection = null, string basedOn = null)
        {
            if (dryRun)
                return McpResponse.Ok(
                    target: target,
                    code: "DryRun",
                    result: new Newtonsoft.Json.Linq.JObject
                    {
                        ["preview"] = new Newtonsoft.Json.Linq.JObject
                        {
                            ["action"] = "add",
                            ["target"] = target,
                            ["varName"] = varName,
                            ["typeName"] = typeName,
                            ["basedOn"] = basedOn,
                            ["length"] = length,
                            ["decimals"] = decimals,
                            ["collection"] = collection
                        }
                    });
            var raw = AddVariableInternal(target, varName, typeName, length, decimals, collection, basedOn);
            MarkDirtyIfSuccess(raw, target);
            return WrapWithPersistedState(raw, target, "Variables", GxMcp.Worker.Helpers.WriteResultMeta.TypedWriter);
        }

        // issue #28 items 8/9/11:
        //   length/decimals  — explicit override of the type-embedded length (fixes the
        //                       Character(20) default that was too short for API keys /
        //                       message strings). When omitted, the length parsed from
        //                       typeName (e.g. Character(200)) still applies.
        //   collection        — sets Variable.IsCollection so SDT/scalar collection vars
        //                       are declarable directly, without the AttCollection dance.
        //   (item 11) when typeName is omitted, CreateVariable already inherits the type of
        //   a same-named attribute via FindAttribute — length/decimals below still override.
        private string AddVariableInternal(string target, string varName, string typeName = null,
            int? length = null, int? decimals = null, bool? collection = null, string basedOn = null)
        {
            try
            {
                // Task 4.2 — validate typeName via VariableTypeResolver before any SDK work,
                // so unknown types never silently default to NUMERIC.
                GxMcp.Worker.Helpers.TypeResolution resolution = null;
                string resolvedTypeForSdk = typeName;
                int? resolvedLength = null;
                int? resolvedDecimals = null;
                var domainBound = new System.Collections.Generic.List<ExpectedDomainBinding>();
                if (!string.IsNullOrEmpty(typeName))
                {
                    resolution = GxMcp.Worker.Helpers.VariableTypeResolver.Resolve(typeName);
                    if (!resolution.Recognized)
                    {
                        var accepted = new JArray();
                        if (resolution.AcceptedList != null)
                            foreach (var a in resolution.AcceptedList) accepted.Add(a);
                        return McpResponse.Err(
                            code: "UnknownType",
                            message: $"Unknown typeName '{typeName}'. Did you mean '{resolution.Suggestion}'?",
                            hint: $"Use one of the accepted type names. Nearest match: '{resolution.Suggestion}'.",
                            nextSteps: new JArray(McpResponse.NextStep(
                                tool: "genexus_add_variable",
                                args: new JObject { ["target"] = target, ["varName"] = varName, ["typeName"] = resolution.Suggestion },
                                why: "Retries the add with the nearest recognized type name.")),
                            target: target,
                            extra: new JObject { ["suggestion"] = resolution.Suggestion, ["accepted"] = accepted });
                    }
                    if (resolution.CanonicalType == "DomainReference" && !string.IsNullOrEmpty(resolution.DomainName))
                    {
                        // Pass the raw name to the existing ResolveTypeObject path (SDT / BC / Domain).
                        resolvedTypeForSdk = resolution.DomainName;
                    }
                    else
                    {
                        // Canonicalise — e.g. VarChar(120) → Character(120) — so TryParseDbType picks
                        // up the canonical eDBType instead of an alias that may not round-trip.
                        resolvedLength = resolution.Length;
                        resolvedDecimals = resolution.Decimals;
                        resolvedTypeForSdk = resolution.CanonicalType;
                    }
                }
                if (!string.IsNullOrWhiteSpace(basedOn))
                {
                    resolvedTypeForSdk = basedOn.Trim();
                    resolution = new GxMcp.Worker.Helpers.TypeResolution
                    {
                        Recognized = true,
                        CanonicalType = "DomainReference",
                        DomainName = resolvedTypeForSdk,
                        Suggestion = resolvedTypeForSdk
                    };
                }

                var err = ResolveVariableTarget(target, ref varName, out var obj, out var varPart, out var existing);
                if (err != null) return err;

                if (existing != null)
                    return McpResponse.Ok(
                        target: target,
                        code: "WriteNoChange",
                        result: new JObject { ["details"] = "Variable already exists; no change applied." });

                if (!string.IsNullOrEmpty(typeName) || !string.IsNullOrWhiteSpace(basedOn))
                {
                    // issue #32 item 1: construction extracted into BuildResolvedVariableInto so
                    // the batch AddVariables path reuses the exact same SDK binding logic.
                    var buildResult = BuildResolvedVariableInto(varPart, varName, resolution, resolvedTypeForSdk,
                            resolvedLength, resolvedDecimals, length, decimals, collection, typeName,
                            out var domainBinding, out var bindFailure);
                    if (buildResult == VarBuildResult.DomainNotFound)
                    {
                        // FR#4 (friction-report 2026-05-19): resolver accepted the bare name as a
                        // potential SDT/BC/Domain reference but SDK couldn't find it in the KB.
                        return McpResponse.Err(
                            code: "UnknownType",
                            message: $"Type '{(basedOn ?? typeName)}' not found in KB. Expected primitive (Character/Numeric/etc), SDT name (e.g. SdtFoo), BC, or Domain.",
                            hint: "Verify the SDT/Domain name via genexus_list_objects or use a primitive type like Character(40).",
                            nextSteps: new JArray(McpResponse.NextStep(
                                tool: "genexus_list_objects",
                                args: new JObject { ["name"] = typeName },
                                why: "Finds SDTs and Domains whose name matches, confirming the correct spelling.")),
                            target: target,
                            extra: new JObject { ["typeName"] = typeName });
                    }
                    if (buildResult == VarBuildResult.PrimitiveNotApplied)
                    {
                        // issue #34: the resolver recognized a primitive but the SDK type map
                        // couldn't apply it. Fail loudly instead of persisting a default NUMERIC(4).
                        return McpResponse.Err(
                            code: "TypeNotApplied",
                            message: $"Recognized type '{typeName}' could not be applied to the variable (no matching SDK type). The variable was NOT created to avoid a silent NUMERIC fallback.",
                            hint: "Report this type name; it is a mapping gap. Use a known primitive (Character/Numeric/Date/DateTime/Boolean/Blob/Image/GUID) meanwhile.",
                            target: target,
                            extra: new JObject { ["typeName"] = typeName });
                    }
                    if (buildResult == VarBuildResult.DomainNotPersistable)
                    {
                        return McpResponse.Err(
                            code: "VariableTypeNotPersisted",
                            message: $"Domain '{resolvedTypeForSdk}' could not be represented as a native SDK reference. The variable was not created.",
                            hint: "Verify that the Domain belongs to the active KB/model and can be selected by the GeneXus SDK type picker.",
                            target: target,
                            extra: new JObject { ["typeName"] = typeName, ["details"] = bindFailure });
                    }
                    if (domainBinding != null) domainBound.Add(domainBinding);
                }
                else
                {
                    AddInferredVariableInto(varPart, varName, length, decimals, collection);
                }

                ForceSaveVariableOwner(obj);
                ScheduleFlush(force: true);

                // issue #56: read back the persisted Variables part and confirm every
                // Domain-bound variable kept its reference (see VerifyDomainReferencesPersisted).
                var verifyErr = VerifyDomainReferencesPersisted(target, domainBound);
                if (verifyErr != null)
                {
                    var addedVariable = varPart.Variables.FirstOrDefault(v =>
                        string.Equals(v.Name, varName, StringComparison.OrdinalIgnoreCase));
                    if (addedVariable != null) varPart.Variables.Remove(addedVariable);
                    obj.EnsureSave();
                    ScheduleFlush();
                    return verifyErr;
                }

                string verifyName = varName;
                ResolveVariableTarget(target, ref verifyName, out _, out var persistedPart, out _);
                string persistError = VerifyPersistedVariable(persistedPart, varName, typeName);
                if (persistError != null)
                    return McpResponse.Err(code: "VariableNotPersisted", message: persistError, target: target,
                        extra: new JObject { ["variable"] = varName, ["requestedType"] = typeName, ["saved"] = false });

                // issue #59: confirm the single added variable actually landed in the
                // persisted part (see VerifyVariablesPersisted).
                var singleVerify = VerifyVariablesPersisted(target, new System.Collections.Generic.List<string> { varName });
                if (singleVerify != null) return singleVerify;

                return McpResponse.Ok(target: target, code: "VariableAdded", result: new JObject
                { ["variable"] = varName, ["requestedType"] = typeName, ["persisted"] = true, ["saved"] = true });
            }
            catch (Exception ex)
            {
                // A GeneXus save can throw after committing the variable. Reconcile from a
                // fresh, complete Variables read before reporting AddVariableFailed.
                try
                {
                    string verifyJson = _objectService.ReadObjectSourceForVerification(target, "Variables");
                    var verifyObj = JObject.Parse(verifyJson);
                    string verifyText = verifyObj["source"]?.ToString()
                        ?? verifyObj["content"]?.ToString()
                        ?? verifyObj["parts"]?["Variables"]?.ToString()
                        ?? "";
                    if (!string.IsNullOrWhiteSpace(verifyText)
                        && MissingVariableNames(verifyText, new[] { varName }).Count == 0)
                    {
                        Logger.Warn("[VARIABLES-POST-CHECK] AddVariable threw after persistence for " + target
                            + "/" + varName + "; returning reconciled success. Cause: " + ex.Message);
                        return McpResponse.Ok(target: target, code: "VariableAdded", result: new JObject
                        {
                            ["variable"] = varName,
                            ["requestedType"] = typeName,
                            ["persisted"] = true,
                            ["saved"] = true,
                            ["verification"] = "reconciledAfterFailure",
                            ["warning"] = "The SDK reported an error after save, but a fresh full read confirmed the variable is persisted."
                        });
                    }
                }
                catch (Exception verifyEx)
                {
                    Logger.Debug("[VARIABLES-POST-CHECK] Exception reconciliation failed for " + target + ": " + verifyEx.Message);
                }
                return McpResponse.Err(
                    code: "AddVariableFailed",
                    message: ex.Message,
                    hint: "Verify the variable name and type are valid. Check that the object exists and has a Variables part.",
                    nextSteps: new JArray(McpResponse.NextStep(
                        tool: "genexus_read",
                        args: new JObject { ["name"] = target, ["part"] = "Variables" },
                        why: "Lists current variables to confirm state.")),
                    target: target);
            }
        }

        // Post-save read-back for Domain-based variable types. The formatted Variables
        // text is deliberately NOT used here: it can project "IDManual" while the raw
        // metadata contains the non-importable token "dom:IDManual". Re-resolve the
        // object through the SDK and compare the persisted DomainKey plus ATTCUSTOMTYPE.
        // issue #59 — post-save read-back confirming EVERY requested variable name is present
        // in the persisted Variables part (catches SDK silent drops beyond the Domain ref case
        // #56 already covers). Returns a serialized error envelope (code VariableAddNotPersisted)
        // listing the missing names, or null when all landed (or the re-read is unverifiable).
        private string VerifyVariablesPersisted(string target,
            System.Collections.Generic.List<string> addedNames)
        {
            if (addedNames == null || addedNames.Count == 0) return null;

            string text = "";
            try
            {
                string readJson = _objectService.ReadObjectSourceForVerification(target, "Variables");
                if (!string.IsNullOrWhiteSpace(readJson))
                {
                    var readObj = JObject.Parse(readJson);
                    text = readObj["source"]?.ToString()
                        ?? readObj["content"]?.ToString()
                        ?? readObj["parts"]?["Variables"]?.ToString()
                        ?? "";
                }
            }
            catch (Exception ex)
            {
                Logger.Debug("[VARIABLES-POST-CHECK] Re-read failed for " + target + ": " + ex.Message);
                return null;
            }
            if (string.IsNullOrWhiteSpace(text)) return null;

            var missing = MissingVariableNames(text, addedNames);
            if (missing.Count == 0) return null;

            return McpResponse.Err(
                code: "VariableAddNotPersisted",
                message: $"The SDK saved the object but the re-read of the Variables part did not confirm {missing.Count} of {addedNames.Count} added variable(s).",
                hint: "On this GeneXus build the variable write may not have fully survived. Re-read with genexus_read part=Variables and re-add the missing variables if they recur.",
                nextSteps: new JArray(McpResponse.NextStep(
                    tool: "genexus_read",
                    args: new JObject { ["name"] = target, ["part"] = "Variables" },
                    why: "Shows the persisted variables so you can see exactly which ones landed.")),
                target: target,
                extra: new JObject { ["variables"] = new JArray(missing) });
        }

        // Pure matcher: which of the expected variable names are absent from the persisted
        // Variables text (format: one `&Name : Type` per line)? Unit-testable without a KB.
        internal static System.Collections.Generic.List<string> MissingVariableNames(
            string variablesText, System.Collections.Generic.IEnumerable<string> expectedNames)
        {
            var missing = new System.Collections.Generic.List<string>();
            if (string.IsNullOrEmpty(variablesText) || expectedNames == null) return missing;
            foreach (var name in expectedNames)
            {
                if (string.IsNullOrWhiteSpace(name)) continue;
                string pattern = @"^&\b" + Regex.Escape(name.TrimStart('&')) + @"\b\s*:";
                if (!Regex.IsMatch(variablesText, pattern, RegexOptions.IgnoreCase | RegexOptions.Multiline))
                    missing.Add(name.TrimStart('&'));
            }
            return missing;
        }

        // issue #56 — post-save read-back for Domain-based variable types. On some
        // GeneXus 18.0.16 builds the SDK accepts DomainBasedOn but drops it at save,
        // leaving the variable with an empty BasedOnReference — the persisted variable
        // then fails spec with spc0056 (Data:249,...,[]). Returns a serialized error
        // envelope (code VariableDomainReferenceNotPersisted) listing every binding the
        // re-read no longer shows, or null when all Domain references persisted.
        private string VerifyDomainReferencesPersisted(string target,
            System.Collections.Generic.List<ExpectedDomainBinding> expected)
        {
            if (expected == null || expected.Count == 0) return null;
            var invalid = new JArray();
            try
            {
                var persistedObject = _objectService.FindObject(target);
                var persistedPart = GxMcp.Worker.Structure.PartAccessor.GetVariablesPart(persistedObject);
                foreach (var binding in expected)
                {
                    var variable = persistedPart?.Variables.FirstOrDefault(v =>
                        string.Equals(v.Name, binding.VarName.TrimStart('&'), StringComparison.OrdinalIgnoreCase));
                    string customToken = null;
                    try { customToken = variable?.GetPropertyValue("ATTCUSTOMTYPE")?.ToString(); } catch { }
                    string failure = null;
                    bool valid = variable != null && VariableInjector.IsNativeDomainReference(
                        binding.DomainKey, variable.DomainKey, customToken, out failure);
                    if (valid && (variable.Type != binding.EffectiveType
                                  || variable.Length != binding.Length
                                  || variable.Decimals != binding.Decimals))
                    {
                        valid = false;
                        failure = $"Persisted primitive shape {variable.Type}({variable.Length},{variable.Decimals}) "
                                  + $"does not match the Domain shape {binding.EffectiveType}({binding.Length},{binding.Decimals}).";
                    }
                    if (!valid)
                    {
                        invalid.Add(new JObject
                        {
                            ["name"] = binding.VarName,
                            ["domain"] = binding.DomainName,
                            ["reason"] = failure ?? "Variable is missing after save.",
                            ["customType"] = customToken
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                invalid.Add(new JObject
                {
                    ["reason"] = "SDK re-read failed: " + ex.Message
                });
            }
            if (invalid.Count == 0) return null;

            return McpResponse.Err(
                code: "VariableTypeNotPersisted",
                message: "The SDK did not persist the requested Domain as a native entity reference.",
                hint: "The operation cannot be completed safely on this GeneXus build. The writer rejects display-only dom:<name> metadata instead of reporting a false success.",
                nextSteps: new JArray(McpResponse.NextStep(
                    tool: "genexus_read",
                    args: new JObject { ["name"] = target, ["part"] = "Variables" },
                    why: "Shows the object state after the failed persistence check.")),
                target: target,
                extra: new JObject { ["variables"] = invalid });
        }

        // Reconstruct a removed variable instead of reusing the detached SDK instance. This is
        // used both for save exceptions and for a failed post-save DomainKey check, so a rejected
        // Domain conversion cannot leave Source, Rules, parameter signatures, or sibling variables
        // in a partially modified object.
        private bool RestoreVariableSnapshot(
            global::Artech.Genexus.Common.Parts.VariablesPart varPart,
            string varName,
            global::Artech.Genexus.Common.Variable originalSnapshot,
            string preservedDescription,
            string originalTypeName)
        {
            bool bindingNotRestored = false;
            var current = varPart.Variables.FirstOrDefault(v =>
                string.Equals(v.Name, varName, StringComparison.OrdinalIgnoreCase));
            if (current != null) varPart.Variables.Remove(current);

            var restored = new global::Artech.Genexus.Common.Variable(varPart) { Name = varName };
            try { if (preservedDescription != null) restored.Description = preservedDescription; } catch { }
            try { restored.Type = originalSnapshot.Type; } catch { }
            try { restored.Length = originalSnapshot.Length; } catch { }
            try { restored.Decimals = originalSnapshot.Decimals; } catch { }
            try { restored.Signed = originalSnapshot.Signed; } catch { }
            try { restored.IsCollection = originalSnapshot.IsCollection; } catch { }

            bool restoredDomain = false;
            try
            {
                if (originalSnapshot.DomainKey != null)
                {
                    restored.DomainKey = originalSnapshot.DomainKey;
                    restoredDomain = true;
                }
            }
            catch { bindingNotRestored = true; }

            if (!restoredDomain && !string.IsNullOrEmpty(originalTypeName))
            {
                try
                {
                    bool rebound = false;
                    if (originalSnapshot.Type == global::Artech.Genexus.Common.eDBType.GX_SDT
                        || originalSnapshot.Type == global::Artech.Genexus.Common.eDBType.GX_BUSCOMP)
                    {
                        var originalBoundObject = VariableInjector.ResolveTypeObject(varPart.Model, originalTypeName);
                        if (originalBoundObject != null && originalBoundObject.TypeDescriptor.Name.Equals("SDT", StringComparison.OrdinalIgnoreCase))
                        {
                            VariableInjector.BindVariableToSdt(restored, originalBoundObject);
                            rebound = true;
                        }
                        else if (originalBoundObject is global::Artech.Genexus.Common.Objects.Transaction originalBoundTrn && originalBoundTrn.IsBusinessComponent)
                        {
                            VariableInjector.BindVariableToBC(restored, originalBoundObject);
                            rebound = true;
                        }
                    }
                    else
                    {
                        rebound = VariableInjector.TryBindGenexusDataType(restored, originalTypeName)
                                  || VariableInjector.TryBindBuiltinUserDefinedType(restored, originalTypeName);
                    }
                    if (!rebound) bindingNotRestored = true;
                }
                catch { bindingNotRestored = true; }
            }

            varPart.Variables.Add(restored);
            return bindingNotRestored;
        }

        // ── Task 4.3 (v2.3.8) — genexus_modify_variable ──────────────────────────
        // Atomically change a variable's type while preserving its name (and
        // description when possible). Implemented as delete+add over the same
        // VariablesPart, with a snapshot of the pre-change variable set so we
        // can roll back if obj.Save() throws.
        public string ModifyVariable(string target, string varName, string newTypeName, string basedOn = null, bool dryRun = false,
            int? length = null, int? decimals = null, bool? collection = null)
        {
            if (dryRun)
                return McpResponse.Ok(
                    target: target,
                    code: "DryRun",
                    result: new Newtonsoft.Json.Linq.JObject
                    {
                        ["preview"] = new Newtonsoft.Json.Linq.JObject
                        {
                            ["action"] = "modify",
                            ["target"] = target,
                            ["varName"] = varName,
                            ["newTypeName"] = newTypeName,
                            ["basedOn"] = basedOn,
                            ["length"] = length,
                            ["decimals"] = decimals,
                            ["collection"] = collection
                        }
                    });
            var raw = ModifyVariableInternal(target, varName, newTypeName, basedOn, length, decimals, collection);
            MarkDirtyIfSuccess(raw, target);
            return WrapWithPersistedState(raw, target, "Variables", GxMcp.Worker.Helpers.WriteResultMeta.TypedWriter);
        }

        private string ModifyVariableInternal(string target, string varName, string newTypeName, string basedOn,
            int? length = null, int? decimals = null, bool? collection = null)
        {
            // Gate 1 — resolve newTypeName up front, before any SDK / KB call.
            // Mirrors AddVariable's Task 4.2 envelope shape exactly.
            GxMcp.Worker.Helpers.TypeResolution resolution = null;
            string resolvedTypeForSdk = newTypeName;
            int? resolvedLength = null;
            int? resolvedDecimals = null;
            if (string.IsNullOrEmpty(newTypeName))
            {
                return McpResponse.Err(
                    code: "UnknownType",
                    message: "newTypeName is required for genexus_modify_variable.",
                    hint: "Pass a valid type such as Character(40), Numeric(8.0), Date, DateTime, Boolean, VarChar(N), or a Domain name.",
                    nextSteps: new JArray(McpResponse.NextStep(
                        tool: "genexus_modify_variable",
                        args: new JObject { ["target"] = target, ["varName"] = varName, ["newTypeName"] = "Character(40)" },
                        why: "Example retry with Character(40).")),
                    target: target,
                    extra: new JObject
                    {
                        ["suggestion"] = "Character(40)",
                        ["accepted"] = new JArray { "Character(N)", "Numeric(N.D)", "Date", "DateTime", "Boolean", "VarChar(N)", "<DomainName>" }
                    });
            }

            resolution = GxMcp.Worker.Helpers.VariableTypeResolver.Resolve(newTypeName);
            if (!resolution.Recognized)
            {
                var accepted = new JArray();
                if (resolution.AcceptedList != null)
                    foreach (var a in resolution.AcceptedList) accepted.Add(a);
                return McpResponse.Err(
                    code: "UnknownType",
                    message: $"Unknown typeName '{newTypeName}'. Did you mean '{resolution.Suggestion}'?",
                    hint: $"Use one of the accepted type names. Nearest match: '{resolution.Suggestion}'.",
                    nextSteps: new JArray(McpResponse.NextStep(
                        tool: "genexus_modify_variable",
                        args: new JObject { ["target"] = target, ["varName"] = varName, ["newTypeName"] = resolution.Suggestion },
                        why: "Retries the modify with the nearest recognized type name.")),
                    target: target,
                    extra: new JObject { ["suggestion"] = resolution.Suggestion, ["accepted"] = accepted });
            }

            if (resolution.CanonicalType == "DomainReference" && !string.IsNullOrEmpty(resolution.DomainName))
            {
                resolvedTypeForSdk = resolution.DomainName;
            }
            else
            {
                resolvedLength = resolution.Length;
                resolvedDecimals = resolution.Decimals;
                resolvedTypeForSdk = resolution.CanonicalType;
            }
            // `basedOn` (optional) takes precedence over a parsed DomainReference —
            // gives the caller explicit control when the typeName is ambiguous.
            if (!string.IsNullOrWhiteSpace(basedOn))
            {
                resolvedTypeForSdk = basedOn;
                resolution = new GxMcp.Worker.Helpers.TypeResolution
                {
                    Recognized = true,
                    CanonicalType = "DomainReference",
                    DomainName = basedOn,
                    Suggestion = basedOn,
                    AcceptedList = resolution?.AcceptedList
                };
            }

            try
            {
                var err = ResolveVariableTarget(target, ref varName, out var obj, out var varPart, out var existing);
                if (err != null) return err;

                if (existing == null)
                {
                    return McpResponse.Err(
                        code: "VariableNotFound",
                        message: $"Variable '&{varName}' not found on '{target}'.",
                        hint: "Read the Variables part to see which variables exist on this object.",
                        nextSteps: new JArray(McpResponse.NextStep(
                            tool: "genexus_read",
                            args: new JObject { ["name"] = target, ["part"] = "Variables" },
                            why: "Lists all declared variables on the object.")),
                        target: target);
                }

                global::Artech.Genexus.Common.Objects.Domain requestedDomain = null;
                if (resolution.CanonicalType == "DomainReference")
                {
                    requestedDomain = VariableInjector.ResolveDomain(
                        varPart.Model, resolvedTypeForSdk, varPart.KBObject?.Module);
                    if (requestedDomain == null)
                    {
                        return McpResponse.Err(
                            code: "UnknownType",
                            message: $"Domain '{resolvedTypeForSdk}' was not found. The original variable was not changed.",
                            hint: "Use the Domain's qualified name when it belongs to another Module.",
                            target: target,
                            extra: new JObject { ["basedOn"] = resolvedTypeForSdk });
                    }
                }

                if (GxMcp.Worker.Helpers.FrameworkManagedVariables.IsManaged(varName))
                {
                    return McpResponse.Err(
                        code: "FrameworkManagedVariable",
                        message: "Framework-managed variable",
                        hint: "Variable '&" + varName + "' is managed by " + GxMcp.Worker.Helpers.FrameworkManagedVariables.GetManagedBy(varName) + " and will be re-injected on save. Do not modify it.",
                        nextSteps: new JArray(McpResponse.NextStep(
                            tool: "genexus_read",
                            args: new JObject { ["name"] = target, ["part"] = "Variables" },
                            why: "Lists current variables so you can identify user-defined ones.")),
                        target: target);
                }

                // Snapshot for rollback: capture every variable's identity + shape so we
                // can re-add the original if obj.Save() throws halfway through.
                string preservedDescription = null;
                try { preservedDescription = existing.Description; } catch { /* SDK may not expose */ }

                // Task 4.5 — capture internal id before Remove() so a
                // BoundToControls rejection can still scan the layout XML.
                int? existingVarId = null;
                try
                {
                    int idx = 1;
                    foreach (var v in varPart.Variables)
                    {
                        if (ReferenceEquals(v, existing))
                        {
                            existingVarId = GxMcp.Worker.Helpers.VariableInjector.GetVariableInternalId(v, idx);
                            break;
                        }
                        idx++;
                    }
                }
                catch { /* best-effort */ }

                // Atomic delete + add: keep the VariablesPart change in memory until
                // obj.Save() either succeeds or we restore the original variable.
                global::Artech.Genexus.Common.Variable originalSnapshot = existing;

                // issue #47 — capture the original variable's non-primitive type name (SDT / BC /
                // built-in GeneXus data type) BEFORE Remove(), via the same read-path resolver
                // GetVariablesAsText uses internally. Rollback needs this to re-bind the original
                // shape instead of silently downgrading to a bare scalar. Stays null for a plain
                // scalar/domain original, or when the resolver itself can't name the binding —
                // either way rollback then falls back to exactly today's scalar-only restore.
                string originalTypeName = null;
                bool bindingNotRestored = false;
                try
                {
                    if (originalSnapshot.Type == global::Artech.Genexus.Common.eDBType.GX_SDT
                        || originalSnapshot.Type == global::Artech.Genexus.Common.eDBType.GX_BUSCOMP
                        || originalSnapshot.Type == global::Artech.Genexus.Common.eDBType.GX_USRDEFTYP
                        || originalSnapshot.Type == global::Artech.Genexus.Common.eDBType.GX_EXTERNAL_OBJECT)
                    {
                        string dumpedText = VariableInjector.GetVariablesAsText(varPart);
                        originalTypeName = ExtractOriginalTypeNameFromDump(dumpedText, varName);
                    }
                }
                catch { /* best-effort; null means fall back to scalar-only restore */ }

                // issue #36.2 — track whether a primitive eDBType was actually applied so the
                // success message can report the REAL persisted type (e.g. Blob→BINARY), never
                // a bare echo of the requested canonical.
                bool appliedPrimitive = false;
                // issue #46 — the meaningful type name for a non-primitive bind (SDT / BC / Domain /
                // built-in GeneXus data type like "Properties"). Stays null until a bind actually
                // succeeds, so a bind that silently applied nothing can be caught below instead of
                // persisting a default NUMERIC(4) and reporting the internal "DomainReference" token.
                string boundTypeName = null;
                ExpectedDomainBinding expectedDomainBinding = null;
                try
                {
                    varPart.Variables.Remove(existing);

                    var newVar = new global::Artech.Genexus.Common.Variable(varPart);
                    newVar.Name = varName;
                    if (!string.IsNullOrEmpty(preservedDescription))
                    {
                        try { newVar.Description = preservedDescription; } catch { /* best-effort */ }
                    }

                    if (resolution.CanonicalType != "DomainReference"
                        && VariableInjector.TryParseDbType(resolvedTypeForSdk, out var dbType))
                    {
                        newVar.Type = dbType;
                        appliedPrimitive = true;
                        try
                        {
                            // Explicit length/decimals args (issue #28 item 8) win over the parsed value.
                            int? effLen = length ?? resolvedLength;
                            int? effDec = decimals ?? resolvedDecimals;
                            if (effLen.HasValue) newVar.Length = effLen.Value;
                            if (effDec.HasValue) newVar.Decimals = effDec.Value;
                        }
                        catch { /* SDK may reject for some types */ }
                    }
                    else if (resolution.CanonicalType != "DomainReference")
                    {
                        // issue #34: recognized primitive that TryParseDbType couldn't map —
                        // throw so the rollback restores the original variable instead of
                        // silently retyping it to the default NUMERIC(4) and reporting success.
                        throw new InvalidOperationException(
                            $"Recognized type '{newTypeName}' could not be applied (no matching SDK type); original variable preserved.");
                    }
                    else
                    {
                        var targetObj = requestedDomain ?? VariableInjector.ResolveTypeObject(varPart.Model, resolvedTypeForSdk);
                        // Dotted SDT-item type (e.g. "Messages.Message") — bind the item level, not the
                        // whole (collection) SDT ResolveTypeObject strips down to. Tried first; only
                        // fires for dotted names so plain-SDT/Domain/BC retypes are unaffected.
                        if (VariableInjector.TryBindSdtItemType(newVar, resolvedTypeForSdk))
                        {
                            boundTypeName = resolvedTypeForSdk;
                        }
                        else if (targetObj is global::Artech.Genexus.Common.Objects.Domain dom)
                        {
                            if (!VariableInjector.BindVariableToDomain(newVar, dom, out var domainBindFailure))
                                throw new InvalidOperationException("VariableTypeNotPersisted: " + domainBindFailure);
                            boundTypeName = dom.Name;
                            expectedDomainBinding = new ExpectedDomainBinding
                            {
                                VarName = varName,
                                DomainName = dom.QualifiedName?.ToString() ?? dom.Name,
                                DomainKey = dom.Key,
                                EffectiveType = newVar.Type,
                                Length = newVar.Length,
                                Decimals = newVar.Decimals
                            };
                        }
                        else if (targetObj != null && targetObj.TypeDescriptor.Name.Equals("SDT", StringComparison.OrdinalIgnoreCase))
                        {
                            VariableInjector.BindVariableToSdt(newVar, targetObj);
                            boundTypeName = targetObj.Name;
                        }
                        else if (targetObj is global::Artech.Genexus.Common.Objects.Transaction trn && trn.IsBusinessComponent)
                        {
                            VariableInjector.BindVariableToBC(newVar, targetObj);
                            boundTypeName = targetObj.Name;
                        }
                        // Built-in GeneXus data types (HttpClient, WebSession, Properties, ...) via the
                        // SDK registry (issue #45/#46), with the legacy hardcoded map as fallback (#33).
                        else if (VariableInjector.TryBindGenexusDataType(newVar, resolvedTypeForSdk))
                        {
                            boundTypeName = resolvedTypeForSdk;
                        }
                        else if (VariableInjector.TryBindBuiltinUserDefinedType(newVar, resolvedTypeForSdk))
                        {
                            boundTypeName = resolvedTypeForSdk;
                        }
                        else
                        {
                            // issue #46 — nothing resolved the name: not a KB Domain/SDT/BC, not a
                            // built-in GeneXus data type. Throwing here rolls back to the original
                            // variable instead of persisting a silent default NUMERIC(4) and reporting
                            // success — the exact failure the reporter hit on the pre-#45 build.
                            throw new InvalidOperationException(
                                $"Type '{newTypeName}' could not be resolved to a Domain, SDT, Business Component, or built-in GeneXus data type; original variable preserved.");
                        }
                    }

                    // issue #28 item 9: collection flag (null = leave as-is on retype).
                    if (collection.HasValue) { try { newVar.IsCollection = collection.Value; } catch { } }
                    varPart.Variables.Add(newVar);

                    ForceSaveVariableOwner(obj);
                    ScheduleFlush(force: true);

                    string verifyName = varName;
                    ResolveVariableTarget(target, ref verifyName, out _, out var persistedPart, out _);
                    string persistError = VerifyPersistedVariable(persistedPart, varName, newTypeName);
                    if (persistError != null)
                        throw new InvalidOperationException(persistError);

                    var domainVerifyError = VerifyDomainReferencesPersisted(target,
                        expectedDomainBinding == null
                            ? new System.Collections.Generic.List<ExpectedDomainBinding>()
                            : new System.Collections.Generic.List<ExpectedDomainBinding> { expectedDomainBinding });
                    if (domainVerifyError != null)
                    {
                        bindingNotRestored = RestoreVariableSnapshot(
                            varPart, varName, originalSnapshot, preservedDescription, originalTypeName);
                        obj.EnsureSave();
                        ScheduleFlush();
                        return domainVerifyError;
                    }

                    // issue #36.2 — report the ACTUAL persisted type. For a primitive, read the
                    // eDBType the SDK stored (Blob/Binary persist as BINARY) plus the effective
                    // length/decimals; if it differs from what was requested, say so explicitly so
                    // a coercion can never masquerade as the requested type. Non-primitive (SDT /
                    // Domain / BC / WebSession) binds keep the canonical name, which is meaningful.
                    // issue #46 — the requested type as the caller named it. "DomainReference" is an
                    // internal resolver token, never a real type: for a non-primitive bind report the
                    // name that was actually requested/bound (e.g. "Properties", a Domain/SDT name).
                    string requestedDisplay = appliedPrimitive || boundTypeName == null
                        ? resolution.CanonicalType
                        : boundTypeName;
                    var resultPayload = new JObject { ["requestedType"] = requestedDisplay };
                    string persistedDesc = requestedDisplay;
                    if (appliedPrimitive)
                    {
                        try
                        {
                            persistedDesc = newVar.Type.ToString();
                            int? shownLen = length ?? resolvedLength;
                            int? shownDec = decimals ?? resolvedDecimals;
                            if (shownLen.HasValue)
                                persistedDesc += "(" + shownLen.Value + (shownDec.HasValue && shownDec.Value > 0 ? "," + shownDec.Value : "") + ")";
                        }
                        catch { persistedDesc = resolution.CanonicalType; }
                    }
                    else if (boundTypeName != null)
                    {
                        persistedDesc = boundTypeName;
                    }
                    resultPayload["persistedType"] = persistedDesc;
                    bool coerced = appliedPrimitive && !string.Equals(persistedDesc.Split('(')[0], resolution.CanonicalType, StringComparison.OrdinalIgnoreCase);
                    resultPayload["details"] = coerced
                        ? $"Variable '&{varName}' retyped to '{persistedDesc}' (requested '{resolution.CanonicalType}', persisted as its SDK type)."
                        : $"Variable '&{varName}' retyped to '{persistedDesc}'.";

                    return McpResponse.Ok(
                        target: target,
                        code: "VariableRenamed",
                        result: resultPayload);
                }
                catch (Exception ex)
                {
                    // Best-effort rollback: reconstruct the original variable because the SDK may
                    // consider the captured instance detached after Remove().
                    try
                    {
                        bindingNotRestored = RestoreVariableSnapshot(
                            varPart, varName, originalSnapshot, preservedDescription, originalTypeName);
                        obj.EnsureSave();
                        ScheduleFlush();
                    }
                    catch { /* swallow — rollback is best-effort */ }
                    // Task 4.5 — prefer a structured BoundToControls envelope
                    // when the SDK rejection message looks like a ghost-binding
                    // failure; falls back to the legacy raw error envelope
                    // when the message doesn't match the heuristic.
                    var boundResp = TryBuildBoundToControlsError(ex, obj, varName, existingVarId);
                    if (boundResp != null) return boundResp;
                    // issue #47 — don't claim a full restore when the original had a non-primitive
                    // binding (SDT / BC / built-in) that couldn't be re-bound; only the scalar
                    // shape was recovered. The common primitive/domain case keeps the plain hint.
                    string rollbackHint = "The modify+save failed; the original variable was restored. Check if the variable is bound to controls.";
                    if (bindingNotRestored)
                    {
                        rollbackHint += " The original had a non-primitive type; verify its binding with genexus_read part=Variables.";
                    }
                    return McpResponse.Err(
                        code: "ModifyVariableFailed",
                        message: ex.Message,
                        hint: rollbackHint,
                        nextSteps: new JArray(McpResponse.NextStep(
                            tool: "genexus_read",
                            args: new JObject { ["name"] = target, ["part"] = "Variables" },
                            why: "Verifies which variables exist after the rollback.")),
                        target: target);
                }
            }
            catch (Exception ex)
            {
                return McpResponse.Err(
                    code: "ModifyVariableFailed",
                    message: ex.Message,
                    hint: "Verify the variable name and type are valid.",
                    nextSteps: new JArray(McpResponse.NextStep(
                        tool: "genexus_read",
                        args: new JObject { ["name"] = target, ["part"] = "Variables" },
                        why: "Lists current variables to confirm state.")),
                    target: target);
            }
        }

        /// <summary>
        /// Adds or retypes a variable using a native Business Component object reference.
        /// This path deliberately does not pass a module-qualified display string through the
        /// Domain resolver: it resolves the Transaction first, binds its EntityKey, and verifies
        /// the same GUID from a fresh VariablesPart read after commit.
        /// </summary>
        public string ChangeBusinessComponentVariable(string action, string target, string varName,
            string objectName, string moduleName, bool dryRun, string expectedVersion,
            bool rollbackOnFailure = true, bool? collection = null)
        {
            action = (action ?? "add").Trim().ToLowerInvariant();
            if (action != "add" && action != "modify")
                return McpResponse.Err(code: "InvalidAction",
                    message: "Business Component typing supports action=add or action=modify.", target: target);

            string normalizedName = varName;
            var targetError = ResolveVariableTarget(target, ref normalizedName,
                out var owner, out var variables, out var existing);
            if (targetError != null) return targetError;

            var bc = VariableInjector.ResolveBusinessComponent(variables.Model, objectName, moduleName,
                out string resolutionError);
            if (bc == null)
                return McpResponse.Err(code: "UnknownBusinessComponent",
                    message: resolutionError ?? "The Business Component could not be resolved.",
                    hint: "Pass objectName and module separately; the Transaction must have Business Component=True.",
                    target: target,
                    extra: new JObject
                    {
                        ["objectType"] = "BusinessComponent",
                        ["objectName"] = objectName,
                        ["module"] = moduleName,
                        ["persisted"] = false
                    });

            string versionBefore = ComputeVersionToken(owner);
            if (!string.IsNullOrWhiteSpace(expectedVersion)
                && !string.Equals(expectedVersion, versionBefore, StringComparison.Ordinal))
                return McpResponse.Err(code: "VersionConflict",
                    message: "The variable owner changed after expectedVersion was captured.",
                    hint: "Re-read the object and retry with its current versionToken.",
                    target: target,
                    extra: new JObject
                    {
                        ["expectedVersion"] = expectedVersion,
                        ["currentVersion"] = versionBefore,
                        ["persisted"] = false
                    });

            JObject requestedIdentity = DescribeBusinessComponent(bc);
            JObject beforeIdentity = DescribeVariableBinding(existing, variables.Model);
            bool alreadyBound = VariableReferencesBusinessComponent(existing, variables.Model, bc, out _);
            if (action == "add" && existing != null)
            {
                if (!alreadyBound)
                    return McpResponse.Err(code: "VariableAlreadyExists",
                        message: "The variable already exists with a different type.",
                        hint: "Use action=modify to retype it atomically.", target: target,
                        extra: new JObject { ["beforeVersion"] = versionBefore, ["persisted"] = false,
                            ["typedIdentity"] = beforeIdentity });
                if (dryRun)
                    return McpResponse.Ok(target: target, code: "DryRun", result: new JObject
                    {
                        ["persisted"] = false,
                        ["mutationDetected"] = false,
                        ["beforeVersion"] = versionBefore,
                        ["afterVersion"] = versionBefore,
                        ["versionToken"] = versionBefore,
                        ["diff"] = new JObject { ["action"] = "none", ["variable"] = normalizedName },
                        ["typedIdentity"] = beforeIdentity,
                        ["reReadConfirmed"] = true,
                        ["implicitLifecycleActions"] = new JArray()
                    });
                return McpResponse.Ok(target: target, code: "WriteNoChange", result: new JObject
                {
                    ["persisted"] = true,
                    ["mutationDetected"] = false,
                    ["beforeVersion"] = versionBefore,
                    ["afterVersion"] = versionBefore,
                    ["versionToken"] = versionBefore,
                    ["typedIdentity"] = beforeIdentity,
                    ["reReadConfirmed"] = true,
                    ["implicitLifecycleActions"] = new JArray()
                });
            }
            if (action == "modify" && existing == null)
                return McpResponse.Err(code: "VariableNotFound",
                    message: "Variable '&" + normalizedName + "' was not found.",
                    hint: "Use action=add to create it.", target: target,
                    extra: new JObject { ["persisted"] = false, ["beforeVersion"] = versionBefore });
            if (action == "modify" && alreadyBound && !dryRun)
                return McpResponse.Ok(target: target, code: "WriteNoChange", result: new JObject
                {
                    ["persisted"] = true,
                    ["mutationDetected"] = false,
                    ["beforeVersion"] = versionBefore,
                    ["afterVersion"] = versionBefore,
                    ["versionToken"] = versionBefore,
                    ["typedIdentity"] = beforeIdentity,
                    ["reReadConfirmed"] = true,
                    ["implicitLifecycleActions"] = new JArray()
                });

            var diff = new JObject
            {
                ["action"] = action,
                ["variable"] = normalizedName,
                ["before"] = beforeIdentity,
                ["requested"] = requestedIdentity.DeepClone()
            };
            if (dryRun)
                return McpResponse.Ok(target: target, code: "DryRun", result: new JObject
                {
                    ["persisted"] = false,
                    ["mutationDetected"] = false,
                    ["beforeVersion"] = versionBefore,
                    ["afterVersion"] = versionBefore,
                    ["versionToken"] = versionBefore,
                    ["diff"] = diff,
                    ["typedIdentity"] = requestedIdentity,
                    ["implicitLifecycleActions"] = new JArray()
                });

            ObjectMoveSnapshot snapshot;
            byte[] originalVariableData;
            try
            {
                snapshot = ObjectMoveSnapshot.Capture(owner);
                originalVariableData = existing == null ? null : ObjectMoveSnapshot.CaptureEntity(existing);
                if (existing != null && originalVariableData == null)
                    throw new InvalidOperationException("The original variable could not be serialized losslessly.");
            }
            catch (Exception ex)
            {
                return McpResponse.Err(code: "SnapshotFailed", message: ex.Message,
                    hint: "No write was attempted because a lossless object snapshot could not be captured.",
                    target: target, extra: new JObject { ["persisted"] = false, ["beforeVersion"] = versionBefore });
            }

            global::Artech.Architecture.Common.Objects.KnowledgeBase kb =
                _objectService.GetKbService().GetKB();
            bool committed = false;
            string versionAfterCommit = null;
            try
            {
                using (var tx = kb.BeginTransaction())
                {
                    try
                    {
                        var currentOwner = kb.DesignModel.Objects.Get(owner.Guid) ?? owner;
                        string lockedVersion = ComputeVersionToken(currentOwner);
                        if (!string.Equals(lockedVersion, versionBefore, StringComparison.Ordinal))
                            throw new InvalidOperationException("VersionConflict: the object changed before the atomic save.");

                        var currentPart = GxMcp.Worker.Structure.PartAccessor.GetVariablesPart(currentOwner)
                            ?? throw new InvalidOperationException("Variables part not found during the atomic save.");
                        var currentVariable = currentPart.Variables.FirstOrDefault(v =>
                            string.Equals(v.Name, normalizedName, StringComparison.OrdinalIgnoreCase));
                        string description = null;
                        bool isCollection = collection ?? false;
                        if (currentVariable != null)
                        {
                            try { description = currentVariable.Description; } catch { }
                            if (!collection.HasValue)
                            {
                                try { isCollection = currentVariable.IsCollection; } catch { }
                            }
                            currentPart.Variables.Remove(currentVariable);
                        }

                        var replacement = new global::Artech.Genexus.Common.Variable(currentPart)
                        {
                            Name = normalizedName
                        };
                        try { replacement.Description = description; } catch { }
                        VariableInjector.BindVariableToBC(replacement, bc);
                        try { replacement.IsCollection = isCollection; } catch { }
                        currentPart.Variables.Add(replacement);
                        ForceSaveVariableOwner(currentOwner);
                        tx.Commit();
                        committed = true;
                    }
                    finally
                    {
                        if (!committed) try { tx.Rollback(); } catch { }
                    }
                }

                ScheduleFlush(force: true);
                var persistedOwner = kb.DesignModel.Objects.Get(owner.Guid) ?? owner;
                var persistedPart = GxMcp.Worker.Structure.PartAccessor.GetVariablesPart(persistedOwner);
                var persistedVariable = persistedPart?.Variables.FirstOrDefault(v =>
                    string.Equals(v.Name, normalizedName, StringComparison.OrdinalIgnoreCase));
                versionAfterCommit = ComputeVersionToken(persistedOwner);
                bool bindingValid = VariableReferencesBusinessComponent(persistedVariable,
                    persistedPart?.Model, bc, out string bindingError);
                var authoredComparison = snapshot.Compare(persistedOwner, "Variables", "ProcedureVariables",
                    "Documentation", "Layout", "WinForm", "WebForm");
                if (!bindingValid || !authoredComparison.Equal)
                    throw new InvalidOperationException(!bindingValid
                        ? bindingError
                        : "A non-Variables authored part changed unexpectedly: " + authoredComparison.ChangedParts);

                string versionAfter = versionAfterCommit;
                JObject persistedIdentity = DescribeVariableBinding(persistedVariable, persistedPart.Model);
                diff["persisted"] = persistedIdentity.DeepClone();
                MarkDirtyIfSuccess("{\"status\":\"ok\"}", target);
                return McpResponse.Ok(target: target, code: action == "add" ? "VariableAdded" : "VariableRetyped",
                    result: new JObject
                    {
                        ["persisted"] = true,
                        ["mutationDetected"] = true,
                        ["beforeVersion"] = versionBefore,
                        ["afterVersion"] = versionAfter,
                        ["versionToken"] = versionAfter,
                        ["diff"] = diff,
                        ["typedIdentity"] = persistedIdentity,
                        ["collection"] = persistedVariable?.IsCollection,
                        ["reReadConfirmed"] = true,
                        ["rollbackOnFailure"] = rollbackOnFailure,
                        ["implicitLifecycleActions"] = new JArray()
                    });
            }
            catch (Exception ex)
            {
                bool versionConflict = ex.Message.StartsWith("VersionConflict:", StringComparison.Ordinal);
                bool concurrentMutation = false;
                if (!versionConflict && versionAfterCommit != null)
                {
                    try
                    {
                        var current = kb.DesignModel.Objects.Get(owner.Guid) ?? owner;
                        concurrentMutation = !string.Equals(
                            ComputeVersionToken(current), versionAfterCommit, StringComparison.Ordinal);
                    }
                    catch { concurrentMutation = true; }
                }
                JObject rollback = versionConflict || concurrentMutation
                    ? new JObject
                    {
                        ["attempted"] = false,
                        ["verified"] = false,
                        ["skipped"] = true,
                        ["reason"] = versionConflict
                            ? "Version conflict was detected before mutation."
                            : "The owner changed after this operation; restoring the old snapshot would overwrite a concurrent edit."
                    }
                    : RestoreObjectSnapshot(kb, owner.Guid, snapshot, normalizedName, originalVariableData);
                bool restored = rollback["verified"]?.ToObject<bool?>() == true;
                string code = versionConflict ? "VersionConflict" : "VariableTypeNotPersisted";
                return McpResponse.Err(code: code,
                    message: restored
                        ? "The Business Component variable change failed and the complete object snapshot was restored. " + ex.Message
                        : "The Business Component variable change failed and rollback could not be verified. " + ex.Message,
                    hint: restored
                        ? "Re-read the object before retrying."
                        : "Stop writing this object and inspect the rollback details.",
                    target: target,
                    extra: new JObject
                    {
                        ["persisted"] = !restored && committed,
                        ["mutationDetected"] = committed && !restored,
                        ["beforeVersion"] = versionBefore,
                        ["rollback"] = rollback,
                        ["diff"] = diff,
                        ["implicitLifecycleActions"] = new JArray()
                    });
            }
        }

        private static JObject DescribeBusinessComponent(global::Artech.Genexus.Common.Objects.Transaction bc)
        {
            string module = null;
            try { module = bc.Module?.Name; } catch { }
            return new JObject
            {
                ["objectType"] = "BusinessComponent",
                ["objectName"] = bc.Name,
                ["module"] = module,
                ["guid"] = bc.Guid.ToString("D"),
                ["entityKey"] = bc.Key?.ToString(),
                ["isBusinessComponent"] = bc.IsBusinessComponent,
                ["methods"] = new JArray("Load", "Save", "Success", "GetMessages")
            };
        }

        private static JObject DescribeVariableBinding(global::Artech.Genexus.Common.Variable variable,
            global::Artech.Architecture.Common.Objects.KBModel model)
        {
            if (variable == null) return null;
            var bound = VariableInjector.ResolveBoundTypeObject(variable, model);
            var bc = bound as global::Artech.Genexus.Common.Objects.Transaction;
            if (bc != null && bc.IsBusinessComponent)
            {
                var identity = DescribeBusinessComponent(bc);
                identity["variableType"] = variable.Type.ToString();
                return identity;
            }
            return new JObject
            {
                ["variableType"] = variable.Type.ToString(),
                ["objectType"] = null,
                ["objectName"] = null,
                ["module"] = null,
                ["guid"] = null
            };
        }

        private static bool VariableReferencesBusinessComponent(global::Artech.Genexus.Common.Variable variable,
            global::Artech.Architecture.Common.Objects.KBModel model,
            global::Artech.Genexus.Common.Objects.Transaction expected, out string error)
        {
            error = null;
            if (variable == null) { error = "The variable is missing after save."; return false; }
            if (variable.Type != global::Artech.Genexus.Common.eDBType.GX_BUSCOMP)
            {
                error = "The persisted variable is not GX_BUSCOMP.";
                return false;
            }
            var bound = VariableInjector.ResolveBoundTypeObject(variable, model);
            if (bound == null) { error = "The persisted variable has no resolvable native object reference."; return false; }
            if (bound.Guid != expected.Guid)
            {
                error = "The persisted variable references GUID '" + bound.Guid.ToString("D")
                    + "', not the requested Business Component GUID '" + expected.Guid.ToString("D") + "'.";
                return false;
            }
            var trn = bound as global::Artech.Genexus.Common.Objects.Transaction;
            if (trn == null || !trn.IsBusinessComponent)
            {
                error = "The referenced Transaction is not a Business Component.";
                return false;
            }
            return true;
        }

        private static JObject RestoreObjectSnapshot(global::Artech.Architecture.Common.Objects.KnowledgeBase kb,
            Guid objectGuid, ObjectMoveSnapshot snapshot, string variableName, byte[] originalVariableData)
        {
            var result = new JObject { ["attempted"] = true, ["verified"] = false };
            try
            {
                using (var tx = kb.BeginTransaction())
                {
                    bool rollbackCommitted = false;
                    try
                    {
                        var current = kb.DesignModel.Objects.Get(objectGuid)
                            ?? throw new InvalidOperationException("The object no longer exists.");
                        snapshot.RestoreObject(current);
                        snapshot.RestoreParts(current, "Variables", "ProcedureVariables");
                        var variables = GxMcp.Worker.Structure.PartAccessor.GetVariablesPart(current)
                            ?? throw new InvalidOperationException("Variables part is missing during rollback.");
                        var currentVariable = variables.Variables.FirstOrDefault(v =>
                            string.Equals(v.Name, variableName, StringComparison.OrdinalIgnoreCase));
                        if (currentVariable != null) variables.Variables.Remove(currentVariable);
                        if (originalVariableData != null)
                        {
                            var recreatedVariable = new global::Artech.Genexus.Common.Variable(variables);
                            ObjectMoveSnapshot.RestoreEntity(recreatedVariable, originalVariableData);
                            variables.Variables.Add(recreatedVariable);
                        }
                        ForceSaveVariableOwner(current);
                        tx.Commit();
                        rollbackCommitted = true;
                    }
                    finally { if (!rollbackCommitted) try { tx.Rollback(); } catch { } }
                }
                var restored = kb.DesignModel.Objects.Get(objectGuid);
                var comparison = snapshot.Compare(restored,
                    "Variables", "ProcedureVariables", "Documentation", "Layout", "WinForm", "WebForm");
                var restoredPart = GxMcp.Worker.Structure.PartAccessor.GetVariablesPart(restored);
                var restoredVariable = restoredPart?.Variables.FirstOrDefault(v =>
                    string.Equals(v.Name, variableName, StringComparison.OrdinalIgnoreCase));
                bool variableVerified = originalVariableData == null
                    ? restoredVariable == null
                    : restoredVariable != null
                        && (ObjectMoveSnapshot.CaptureEntity(restoredVariable) ?? new byte[0])
                            .SequenceEqual(originalVariableData);
                result["verified"] = comparison.Equal && variableVerified;
                result["variableVerified"] = variableVerified;
                result["changedParts"] = comparison.ChangedParts;
                result["persistedHash"] = comparison.PersistedHash;
            }
            catch (Exception ex) { result["error"] = ex.Message; }
            return result;
        }

        private static void ForceSaveVariableOwner(global::Artech.Architecture.Common.Objects.KBObject obj)
        {
            obj.Save(new global::Artech.Architecture.Common.Objects.KBObjectSavePreferences
            {
                ForceSave = true,
                ForceSaveDefaultParts = true,
                SkipValidation = false
            });
        }

        private string VerifyPersistedVariable(global::Artech.Genexus.Common.Parts.VariablesPart part,
            string variableName, string requestedType)
        {
            if (part == null) return "The Variables part could not be reloaded after save.";
            var variable = part.Variables.FirstOrDefault(v => string.Equals(v.Name, variableName, StringComparison.OrdinalIgnoreCase));
            if (variable == null) return "Variable '&" + variableName + "' was not present after reload.";
            if (!string.IsNullOrWhiteSpace(requestedType))
            {
                var referenced = _objectService.FindObject(requestedType.TrimStart('&'));
                if (referenced is global::Artech.Genexus.Common.Objects.Domain requestedDomain)
                {
                    string persistedDomain = null;
                    try { persistedDomain = variable.DomainBasedOn?.Name; } catch { }
                    if (!string.Equals(persistedDomain, requestedDomain.Name, StringComparison.OrdinalIgnoreCase))
                        return "Variable '&" + variableName + "' reloaded without the requested Domain '" + requestedDomain.Name + "'.";
                }
            }
            return null;
        }

        // issue #47 — pure helper: given the "&Name : TypeRepr [Collection]" text
        // VariableInjector.GetVariablesAsText emits for a VariablesPart, extract the type token
        // for `varName`, or null when it's unresolvable/absent. ResolveTypeRepresentation's
        // fallback format ("<eDBType>(<len>[,<dec>])") means the read path couldn't name the
        // binding either, so that shape is treated the same as "not found" — the caller must not
        // guess a type name from it. Internal + no SDK types in its signature so it's unit-testable
        // without a live KB (see GxMcp.Worker.Tests via InternalsVisibleTo).
        internal static string ExtractOriginalTypeNameFromDump(string dumpedText, string varName)
        {
            if (string.IsNullOrEmpty(dumpedText) || string.IsNullOrEmpty(varName)) return null;
            foreach (var dumpedLine in dumpedText.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var m = System.Text.RegularExpressions.Regex.Match(
                    dumpedLine,
                    @"^&" + System.Text.RegularExpressions.Regex.Escape(varName) + @"\s*:\s*(.+?)(\s+Collection)?$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (m.Success)
                {
                    string candidate = m.Groups[1].Value.Trim();
                    if (System.Text.RegularExpressions.Regex.IsMatch(candidate, @"^GX_\w+\(\d+(,\d+)?\)$"))
                        return null;
                    return candidate;
                }
            }
            return null;
        }
    }
}
