using System;
using System.Collections.Generic;
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

        private string ValidateVariableDryRunTarget(string target)
        {
            if (string.IsNullOrWhiteSpace(target))
                return McpResponse.Err(
                    code: "MissingParameter",
                    message: "Object name is required for a variable preview.",
                    hint: "Pass name=<object>.",
                    target: target);
            try
            {
                if (_objectService?.FindObject(target) != null) return null;
                return McpResponse.Err(
                    code: "ObjectNotFound",
                    message: "The variable preview target was not found in the Knowledge Base.",
                    hint: "Verify the object name with genexus_query or genexus_list_objects, then retry the preview.",
                    nextSteps: new JArray(Models.McpResponse.NextStep(
                        "genexus_query",
                        new JObject { ["query"] = target },
                        "Find the exact object name before retrying the variable preview.")),
                    target: target);
            }
            catch (Exception ex)
            {
                return McpResponse.Err(
                    code: "ObjectResolutionFailed",
                    message: "The variable preview target could not be resolved: " + ex.Message,
                    hint: "Retry after the KB index is ready or pass a fully qualified object identity.",
                    target: target);
            }
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
            {
                string targetError = ValidateVariableDryRunTarget(target);
                if (targetError != null) return targetError;
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
            }
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
        private enum VarBuildResult { Added, DomainNotFound, DomainNotPersistable, PrimitiveNotApplied, AttributeNotFound, AttributeNotPersistable, ObjectNotPersistable, DimensionsNotPersistable }

        internal sealed class ExpectedDomainBinding
        {
            public string VarName { get; set; }
            public string DomainName { get; set; }
            public global::Artech.Udm.Framework.EntityKey DomainKey { get; set; }
            public global::Artech.Genexus.Common.eDBType EffectiveType { get; set; }
            public int Length { get; set; }
            public int Decimals { get; set; }
        }

        internal sealed class ExpectedAttributeBinding
        {
            public string VarName { get; set; }
            public string AttributeName { get; set; }
        }

        internal sealed class ExpectedObjectBinding
        {
            public string VarName { get; set; }
            public string ObjectName { get; set; }
            public Guid ObjectGuid { get; set; }
            public string BindingKind { get; set; }
        }

        // Builds one Variable from an already-validated TypeResolution and adds it to
        // varPart IN MEMORY (no save, no envelope — the caller owns those). Returns
        // DomainNotFound when the typeName looked like an SDT/BC/Domain reference but the
        // SDK couldn't resolve it in the KB, so the caller can surface UnknownType.
        private VarBuildResult BuildResolvedVariableInto(
            global::Artech.Genexus.Common.Parts.VariablesPart varPart, string varName,
            GxMcp.Worker.Helpers.TypeResolution resolution, string resolvedTypeForSdk,
            int? resolvedLength, int? resolvedDecimals,
            int? length, int? decimals, bool? collection, int? dimensions, JArray dimensionSizes, string originalTypeName,
            out ExpectedDomainBinding domainBinding, out string bindFailure)
        {
            return BuildResolvedVariableInto(varPart, varName, resolution, resolvedTypeForSdk,
                resolvedLength, resolvedDecimals, length, decimals, collection, dimensions, dimensionSizes, originalTypeName,
                out domainBinding, out _, out _, out bindFailure);
        }

        private VarBuildResult BuildResolvedVariableInto(
            global::Artech.Genexus.Common.Parts.VariablesPart varPart, string varName,
            GxMcp.Worker.Helpers.TypeResolution resolution, string resolvedTypeForSdk,
            int? resolvedLength, int? resolvedDecimals,
            int? length, int? decimals, bool? collection, int? dimensions, JArray dimensionSizes, string originalTypeName,
            out ExpectedDomainBinding domainBinding, out ExpectedAttributeBinding attributeBinding, out ExpectedObjectBinding objectBinding, out string bindFailure)
        {
            domainBinding = null;
            attributeBinding = null;
            objectBinding = null;
            bindFailure = null;
            var newVar = new global::Artech.Genexus.Common.Variable(varPart);
            newVar.Name = varName;

            // issue #281: attribute-bound variables. The resolution carries
            // CanonicalType="AttributeReference" with AttributeName set (from
            // typeName="Attribute:X", basedOn="Attribute:X", or basedOnAttribute).
            if (resolution != null && string.Equals(resolution.CanonicalType, "AttributeReference", StringComparison.OrdinalIgnoreCase))
            {
                string attrName = resolution.AttributeName ?? resolution.DomainName ?? resolvedTypeForSdk;
                if (VariableInjector.TryParseAttributeReference(attrName, out string parsed))
                    attrName = parsed;
                else if (VariableInjector.TryParseAttributeReference(resolvedTypeForSdk, out string parsedSdk))
                    attrName = parsedSdk;
                attrName = (attrName ?? string.Empty).Trim().TrimStart('&');
                var attrObj = VariableInjector.FindAttribute(varPart.Model, attrName);
                if (attrObj == null)
                {
                    bindFailure = "Attribute '" + attrName + "' not found in KB.";
                    return VarBuildResult.AttributeNotFound;
                }
                if (!VariableInjector.BindVariableToAttribute(newVar, attrObj, out bindFailure))
                    return VarBuildResult.AttributeNotPersistable;
                attributeBinding = new ExpectedAttributeBinding { VarName = varName, AttributeName = attrObj.Name };
                if (collection == true) { try { newVar.IsCollection = true; } catch { } }
                if (dimensions.HasValue && !VariableDimensionSupport.TryApply(newVar, dimensions, dimensionSizes, out bindFailure))
                    return VarBuildResult.DimensionsNotPersistable;
                varPart.Variables.Add(newVar);
                return VarBuildResult.Added;
            }

            if (resolution != null && resolution.CanonicalType != "DomainReference"
                && resolution.CanonicalType != "AttributeReference"
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
                if (resolution != null && resolution.CanonicalType != "DomainReference"
                    && resolution.CanonicalType != "AttributeReference")
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
                    {
                        if (!VariableInjector.BindVariableToSdt(newVar, targetObj, out bindFailure))
                            return VarBuildResult.ObjectNotPersistable;
                        objectBinding = new ExpectedObjectBinding
                        {
                            VarName = varName,
                            ObjectName = targetObj.Name,
                            ObjectGuid = targetObj.Guid,
                            BindingKind = "SDT"
                        };
                    }
                    else if (targetObj is global::Artech.Genexus.Common.Objects.Transaction trn && trn.IsBusinessComponent)
                    {
                        try
                        {
                            VariableInjector.BindVariableToBC(newVar, targetObj);
                        }
                        catch (Exception ex)
                        {
                            bindFailure = ex.InnerException?.Message ?? ex.Message;
                            return VarBuildResult.ObjectNotPersistable;
                        }
                        objectBinding = new ExpectedObjectBinding
                        {
                            VarName = varName,
                            ObjectName = targetObj.Name,
                            ObjectGuid = targetObj.Guid,
                            BindingKind = "BusinessComponent"
                        };
                    }
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
            if (dimensions.HasValue && !VariableDimensionSupport.TryApply(newVar, dimensions, dimensionSizes, out bindFailure))
                return VarBuildResult.DimensionsNotPersistable;
            varPart.Variables.Add(newVar);
            return VarBuildResult.Added;
        }

        // No typeName: CreateVariable inherits a same-named attribute's type (issue #28
        // item 11) or applies the naming heuristic. Explicit length/decimals/collection
        // args still override the result. Adds to varPart in memory (no save).
        private void AddInferredVariableInto(global::Artech.Genexus.Common.Parts.VariablesPart varPart,
            string varName, int? length, int? decimals, bool? collection, int? dimensions, JArray dimensionSizes)
        {
            var newVar = VariableInjector.CreateVariable(varPart, varName);
            try
            {
                if (length.HasValue) newVar.Length = length.Value;
                if (decimals.HasValue) newVar.Decimals = decimals.Value;
            }
            catch { /* best-effort */ }
            if (collection == true) { try { newVar.IsCollection = true; } catch { } }
            if (dimensions.HasValue && !VariableDimensionSupport.TryApply(newVar, dimensions, dimensionSizes, out var dimensionFailure))
                throw new InvalidOperationException("Variable dimensions could not be applied: " + dimensionFailure);
            varPart.Variables.Add(newVar);
        }

        // issue #32 item 1 — batch add. Resolves the target once and adds every variable in
        // `variables` before a single EnsureSave / ScheduleFlush. Each item is
        // { varName|name, typeName?, length?, decimals?, collection? }. Per-item outcomes let
        // the agent see which vars were Added / already Exist / Failed without N round-trips.
        public string AddVariables(string target, JArray variables, bool dryRun = false)
        {
            string dimensionValidationError;
            if (!ValidateVariableDimensionBatch(variables, out dimensionValidationError))
                return dimensionValidationError;

            if (dryRun)
            {
                string targetError = ValidateVariableDryRunTarget(target);
                if (targetError != null) return targetError;
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

        private static bool ValidateVariableDimensionBatch(JArray variables, out string errorResponse)
        {
            errorResponse = null;
            if (variables == null) return true;

            for (int index = 0; index < variables.Count; index++)
            {
                var item = variables[index] as JObject;
                if (item == null)
                {
                    errorResponse = McpResponse.Err(
                        code: "InvalidVariableDimensions",
                        message: "Variable batch item " + index + " must be an object.",
                        hint: "Send each variables[] item as an object with varName and optional dimension fields.");
                    return false;
                }

                int? dimensions = null;
                JToken dimensionsToken = item["dimensions"];
                if (dimensionsToken != null && dimensionsToken.Type != JTokenType.Null)
                {
                    int parsedDimensions;
                    if (dimensionsToken.Type != JTokenType.Integer
                        || !int.TryParse(dimensionsToken.ToString(), out parsedDimensions))
                    {
                        errorResponse = McpResponse.Err(
                            code: "InvalidVariableDimensions",
                            message: "variables[" + index + "].dimensions must be an integer 1 or 2.",
                            hint: "Use dimensions=1 with one positive size or dimensions=2 with two positive sizes.");
                        return false;
                    }
                    dimensions = parsedDimensions;
                }

                JToken sizesToken = item["dimensionSizes"];
                if (sizesToken != null && sizesToken.Type != JTokenType.Null
                    && sizesToken.Type != JTokenType.Array)
                {
                    errorResponse = McpResponse.Err(
                        code: "InvalidVariableDimensions",
                        message: "variables[" + index + "].dimensionSizes must be an array.",
                        hint: "Use dimensionSizes=[size] for a vector or [rows,columns] for a matrix.");
                    return false;
                }
                JArray sizes = sizesToken as JArray;

                JToken collectionToken = item["collection"];
                bool? collection = null;
                if (collectionToken != null && collectionToken.Type != JTokenType.Null)
                {
                    if (collectionToken.Type != JTokenType.Boolean)
                    {
                        errorResponse = McpResponse.Err(
                            code: "InvalidVariableDimensions",
                            message: "variables[" + index + "].collection must be a boolean.",
                            hint: "Use collection=true only when dimensions/dimensionSizes are omitted.");
                        return false;
                    }
                    collection = collectionToken.Value<bool>();
                }

                string code, message, hint;
                JObject extra;
                if (!VariableDimensionSupport.TryValidate(dimensions, sizes, collection, out code,
                    out message, out hint, out extra))
                {
                    errorResponse = McpResponse.Err(
                        code: code,
                        message: "variables[" + index + "]: " + message,
                        hint: hint,
                        extra: extra);
                    return false;
                }
            }

            return true;
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
                PopulateVariablesInto(varPart, variables, out var outcomes, out int added, out int existed, out int failed, out var domainBound, out var attributeBound, out var objectBound, out var addedNames);

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
                    var attrVerifyErr = VerifyAttributeReferencesPersisted(target, attributeBound);
                    if (attrVerifyErr != null)
                    {
                        foreach (var addedName in addedNames)
                        {
                            var addedVariable = varPart.Variables.FirstOrDefault(v =>
                                string.Equals(v.Name, addedName, StringComparison.OrdinalIgnoreCase));
                            if (addedVariable != null) varPart.Variables.Remove(addedVariable);
                        }
                        obj.EnsureSave();
                        ScheduleFlush();
                        return attrVerifyErr;
                    }
                    var objectVerifyErr = VerifyObjectReferencesPersisted(target, objectBound);
                    if (objectVerifyErr != null)
                    {
                        foreach (var addedName in addedNames)
                        {
                            var addedVariable = varPart.Variables.FirstOrDefault(v =>
                                string.Equals(v.Name, addedName, StringComparison.OrdinalIgnoreCase));
                            if (addedVariable != null) varPart.Variables.Remove(addedVariable);
                        }
                        obj.EnsureSave();
                        ScheduleFlush();
                        return objectVerifyErr;
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
                        string requestedType = RequestedTypeForVerify(
                            requestItem?["typeName"]?.ToString(),
                            requestItem?["basedOn"]?.ToString(),
                            requestItem?["basedOnAttribute"]?.ToString());
                        string verifyError = VerifyPersistedVariable(persistedPart, persistedName, requestedType);
                        if (verifyError != null)
                        {
                            outcome["status"] = "NotPersisted";
                            outcome["reason"] = verifyError;
                            added--; failed++;
                        }
                        else outcome["persisted"] = true;
                    }
                    var dimensionErr = VerifyBatchVariableDimensionsPersisted(target, variables, addedNames);
                    if (dimensionErr != null)
                    {
                        foreach (var addedName in addedNames)
                        {
                            var addedVariable = varPart.Variables.FirstOrDefault(v =>
                                string.Equals(v.Name, addedName, StringComparison.OrdinalIgnoreCase));
                            if (addedVariable != null) varPart.Variables.Remove(addedVariable);
                        }
                        try { obj.EnsureSave(); ScheduleFlush(); } catch { }
                        return dimensionErr;
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

        private string VerifyBatchVariableDimensionsPersisted(
            string target, JArray variables, IEnumerable<string> addedNames)
        {
            if (variables == null || addedNames == null) return null;
            var added = new HashSet<string>(addedNames, StringComparer.OrdinalIgnoreCase);
            foreach (JObject item in variables.OfType<JObject>())
            {
                string name = (item["varName"] ?? item["name"])?.ToString()?.TrimStart('&');
                if (string.IsNullOrWhiteSpace(name) || !added.Contains(name)) continue;
                int? dimensions = item["dimensions"]?.ToObject<int?>();
                var sizes = item["dimensionSizes"] as JArray;
                string error = VerifyVariableDimensionsPersisted(target, name, dimensions, sizes);
                if (error != null) return error;
            }
            return null;
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
            PopulateVariablesInto(varPart, variables, out outcomes, out added, out existed, out failed,
                out domainBound, out _, out addedNames);
        }

        internal void PopulateVariablesInto(
            global::Artech.Genexus.Common.Parts.VariablesPart varPart,
            JArray variables,
            out JArray outcomes,
            out int added,
            out int existed,
            out int failed,
            out System.Collections.Generic.List<ExpectedDomainBinding> domainBound,
            out System.Collections.Generic.List<ExpectedAttributeBinding> attributeBound,
            out System.Collections.Generic.List<string> addedNames)
        {
            PopulateVariablesInto(varPart, variables, out outcomes, out added, out existed, out failed,
                out domainBound, out attributeBound, out _, out addedNames);
        }

        internal void PopulateVariablesInto(
            global::Artech.Genexus.Common.Parts.VariablesPart varPart,
            JArray variables,
            out JArray outcomes,
            out int added,
            out int existed,
            out int failed,
            out System.Collections.Generic.List<ExpectedDomainBinding> domainBound,
            out System.Collections.Generic.List<ExpectedAttributeBinding> attributeBound,
            out System.Collections.Generic.List<ExpectedObjectBinding> objectBound,
            out System.Collections.Generic.List<string> addedNames)
        {
            outcomes = new JArray();
            added = 0; existed = 0; failed = 0;
            domainBound = new System.Collections.Generic.List<ExpectedDomainBinding>();
            attributeBound = new System.Collections.Generic.List<ExpectedAttributeBinding>();
            objectBound = new System.Collections.Generic.List<ExpectedObjectBinding>();
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
                string vBasedOnAttribute = jo["basedOnAttribute"]?.ToString();
                int? vLen = jo["length"]?.ToObject<int?>();
                int? vDec = jo["decimals"]?.ToObject<int?>();
                bool? vColl = jo["collection"]?.ToObject<bool?>();
                int? vDimensions = jo["dimensions"]?.ToObject<int?>();
                JArray vDimensionSizes = jo["dimensionSizes"] as JArray;

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
                    if (res.CanonicalType == "AttributeReference" && !string.IsNullOrEmpty(res.AttributeName))
                    {
                        rSdk = res.AttributeName;
                    }
                    else if (res.CanonicalType == "DomainReference" && !string.IsNullOrEmpty(res.DomainName))
                    {
                        rSdk = res.DomainName;
                    }
                    else { rLen = res.Length; rDec = res.Decimals; rSdk = res.CanonicalType; }
                }
                if (!string.IsNullOrWhiteSpace(vBasedOnAttribute))
                {
                    string attrName = vBasedOnAttribute.Trim();
                    if (VariableInjector.TryParseAttributeReference(attrName, out string parsedAttr))
                        attrName = parsedAttr;
                    rSdk = attrName;
                    res = new GxMcp.Worker.Helpers.TypeResolution
                    {
                        Recognized = true,
                        CanonicalType = "AttributeReference",
                        AttributeName = attrName,
                        DomainName = attrName,
                        Suggestion = attrName
                    };
                }
                else if (!string.IsNullOrWhiteSpace(vBasedOn))
                {
                    string trimmedBasedOn = vBasedOn.Trim();
                    if (VariableInjector.TryParseAttributeReference(trimmedBasedOn, out string parsedBasedOnAttr))
                    {
                        rSdk = parsedBasedOnAttr;
                        res = new GxMcp.Worker.Helpers.TypeResolution
                        {
                            Recognized = true,
                            CanonicalType = "AttributeReference",
                            AttributeName = parsedBasedOnAttr,
                            DomainName = parsedBasedOnAttr,
                            Suggestion = parsedBasedOnAttr
                        };
                    }
                    else
                    {
                        rSdk = trimmedBasedOn;
                        res = new GxMcp.Worker.Helpers.TypeResolution
                        {
                            Recognized = true,
                            CanonicalType = "DomainReference",
                            DomainName = rSdk,
                            Suggestion = rSdk
                        };
                    }
                }

                try
                {
                    if (!string.IsNullOrEmpty(vType) || !string.IsNullOrWhiteSpace(vBasedOn) || !string.IsNullOrWhiteSpace(vBasedOnAttribute))
                    {
                        var batchBuild = BuildResolvedVariableInto(varPart, vName, res, rSdk, rLen, rDec, vLen, vDec, vColl, vDimensions, vDimensionSizes, vBasedOnAttribute ?? vBasedOn ?? vType,
                            out var domainBinding, out var attributeBinding, out var objectBinding, out var bindFailure);
                        if (batchBuild == VarBuildResult.DomainNotFound || batchBuild == VarBuildResult.AttributeNotFound)
                        {
                            failed++;
                            outcomes.Add(new JObject
                            {
                                ["name"] = vName,
                                ["status"] = "Failed",
                                ["reason"] = "UnknownType",
                                ["details"] = batchBuild == VarBuildResult.AttributeNotFound
                                    ? $"Attribute '{rSdk}' not found in KB."
                                    : $"Type '{vType}' not found in KB."
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
                        if (batchBuild == VarBuildResult.DomainNotPersistable || batchBuild == VarBuildResult.AttributeNotPersistable || batchBuild == VarBuildResult.ObjectNotPersistable)
                        {
                            failed++;
                            outcomes.Add(new JObject
                            {
                                ["name"] = vName,
                                ["status"] = "Failed",
                                ["reason"] = "VariableTypeNotPersisted",
                                ["details"] = bindFailure ?? "The requested type could not be represented as a native SDK reference."
                            });
                            continue;
                        }
                        if (batchBuild == VarBuildResult.DimensionsNotPersistable)
                        {
                            failed++;
                            outcomes.Add(new JObject
                            {
                                ["name"] = vName,
                                ["status"] = "Failed",
                                ["reason"] = "VariableDimensionsNotPersisted",
                                ["details"] = bindFailure ?? "The requested dimensions could not be represented as native SDK variable properties."
                            });
                            continue;
                        }
                        if (domainBinding != null) domainBound.Add(domainBinding);
                        if (attributeBinding != null) attributeBound.Add(attributeBinding);
                        if (objectBinding != null) objectBound.Add(objectBinding);
                    }
                    else
                    {
                        AddInferredVariableInto(varPart, vName, vLen, vDec, vColl, vDimensions, vDimensionSizes);
                    }
                    added++;
                    addedNames.Add(vName);
                    var addedOutcome = new JObject { ["name"] = vName, ["status"] = "Added" };
                    if (vDimensions.HasValue)
                    {
                        addedOutcome["dimensions"] = vDimensions.Value;
                        addedOutcome["dimensionSizes"] = vDimensionSizes?.DeepClone();
                    }
                    outcomes.Add(addedOutcome);
                }
                catch (Exception exItem)
                {
                    failed++;
                    outcomes.Add(new JObject { ["name"] = vName, ["status"] = "Failed", ["reason"] = exItem.Message });
                }
            }
        }

        public string AddVariable(string target, string varName, string typeName = null, bool dryRun = false,
            int? length = null, int? decimals = null, bool? collection = null, string basedOn = null, string basedOnAttribute = null,
            int? dimensions = null, JArray dimensionSizes = null)
        {
            string dimensionCode, dimensionMessage, dimensionHint;
            JObject dimensionExtra;
            if (!VariableDimensionSupport.TryValidate(dimensions, dimensionSizes, collection,
                out dimensionCode, out dimensionMessage, out dimensionHint, out dimensionExtra))
            {
                return McpResponse.Err(
                    code: dimensionCode,
                    message: dimensionMessage,
                    hint: dimensionHint,
                    target: target,
                    extra: dimensionExtra);
            }

            if (dryRun)
            {
                string targetError = ValidateVariableDryRunTarget(target);
                if (targetError != null) return targetError;
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
                            ["basedOnAttribute"] = basedOnAttribute,
                            ["length"] = length,
                            ["decimals"] = decimals,
                            ["collection"] = collection,
                            ["dimensions"] = dimensions,
                            ["dimensionSizes"] = dimensionSizes?.DeepClone()
                        }
                    });
            }
            var raw = AddVariableInternal(target, varName, typeName, length, decimals, collection, basedOn, basedOnAttribute, dimensions, dimensionSizes);
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
            int? length = null, int? decimals = null, bool? collection = null, string basedOn = null, string basedOnAttribute = null,
            int? dimensions = null, JArray dimensionSizes = null)
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
                var attributeBound = new System.Collections.Generic.List<ExpectedAttributeBinding>();
                var objectBound = new System.Collections.Generic.List<ExpectedObjectBinding>();
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
                    if (resolution.CanonicalType == "AttributeReference" && !string.IsNullOrEmpty(resolution.AttributeName))
                    {
                        resolvedTypeForSdk = resolution.AttributeName;
                    }
                    else if (resolution.CanonicalType == "DomainReference" && !string.IsNullOrEmpty(resolution.DomainName))
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
                if (!string.IsNullOrWhiteSpace(basedOnAttribute))
                {
                    string attrName = basedOnAttribute.Trim();
                    if (VariableInjector.TryParseAttributeReference(attrName, out string parsedAttr))
                        attrName = parsedAttr;
                    resolvedTypeForSdk = attrName;
                    resolution = new GxMcp.Worker.Helpers.TypeResolution
                    {
                        Recognized = true,
                        CanonicalType = "AttributeReference",
                        AttributeName = attrName,
                        DomainName = attrName,
                        Suggestion = attrName
                    };
                }
                else if (!string.IsNullOrWhiteSpace(basedOn))
                {
                    string trimmedBasedOn = basedOn.Trim();
                    if (VariableInjector.TryParseAttributeReference(trimmedBasedOn, out string parsedBasedOnAttr))
                    {
                        resolvedTypeForSdk = parsedBasedOnAttr;
                        resolution = new GxMcp.Worker.Helpers.TypeResolution
                        {
                            Recognized = true,
                            CanonicalType = "AttributeReference",
                            AttributeName = parsedBasedOnAttr,
                            DomainName = parsedBasedOnAttr,
                            Suggestion = parsedBasedOnAttr
                        };
                    }
                    else
                    {
                        resolvedTypeForSdk = trimmedBasedOn;
                        resolution = new GxMcp.Worker.Helpers.TypeResolution
                        {
                            Recognized = true,
                            CanonicalType = "DomainReference",
                            DomainName = resolvedTypeForSdk,
                            Suggestion = resolvedTypeForSdk
                        };
                    }
                }

                var err = ResolveVariableTarget(target, ref varName, out var obj, out var varPart, out var existing);
                if (err != null) return err;

                if (existing != null)
                    return McpResponse.Ok(
                        target: target,
                        code: "WriteNoChange",
                        result: new JObject { ["details"] = "Variable already exists; no change applied." });

                if (!string.IsNullOrEmpty(typeName) || !string.IsNullOrWhiteSpace(basedOn) || !string.IsNullOrWhiteSpace(basedOnAttribute))
                {
                    // issue #32 item 1: construction extracted into BuildResolvedVariableInto so
                    // the batch AddVariables path reuses the exact same SDK binding logic.
                    var buildResult = BuildResolvedVariableInto(varPart, varName, resolution, resolvedTypeForSdk,
                            resolvedLength, resolvedDecimals, length, decimals, collection, dimensions, dimensionSizes, basedOnAttribute ?? basedOn ?? typeName,
                            out var domainBinding, out var attributeBinding, out var objectBinding, out var bindFailure);
                    if (buildResult == VarBuildResult.DomainNotFound || buildResult == VarBuildResult.AttributeNotFound)
                    {
                        bool isAttr = buildResult == VarBuildResult.AttributeNotFound;
                        // FR#4 (friction-report 2026-05-19): resolver accepted the bare name as a
                        // potential SDT/BC/Domain reference but SDK couldn't find it in the KB.
                        return McpResponse.Err(
                            code: "UnknownType",
                            message: isAttr
                                ? $"Attribute '{(basedOnAttribute ?? basedOn ?? typeName)}' not found in KB. Expected an existing Attribute name."
                                : $"Type '{(basedOn ?? typeName)}' not found in KB. Expected primitive (Character/Numeric/etc), SDT name (e.g. SdtFoo), BC, or Domain.",
                            hint: isAttr
                                ? "Verify the Attribute name via genexus_list_objects, then retry with basedOnAttribute=<name> or typeName=Attribute:<name>."
                                : "Verify the SDT/Domain name via genexus_list_objects or use a primitive type like Character(40).",
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
                    if (buildResult == VarBuildResult.DomainNotPersistable || buildResult == VarBuildResult.AttributeNotPersistable || buildResult == VarBuildResult.ObjectNotPersistable)
                    {
                        bool isAttr = buildResult == VarBuildResult.AttributeNotPersistable;
                        bool isObj = buildResult == VarBuildResult.ObjectNotPersistable;
                        return McpResponse.Err(
                            code: "VariableTypeNotPersisted",
                            message: isAttr
                                ? $"Attribute '{resolvedTypeForSdk}' could not be represented as a native SDK reference. The variable was not created."
                                : isObj
                                    ? $"Object type '{resolvedTypeForSdk}' could not be represented as a native SDK reference. The variable was not created."
                                    : $"Domain '{resolvedTypeForSdk}' could not be represented as a native SDK reference. The variable was not created.",
                            hint: isAttr
                                ? "Verify that the Attribute belongs to the active KB/model."
                                : isObj
                                    ? "Verify that the SDT/Business Component belongs to the active KB/model."
                                    : "Verify that the Domain belongs to the active KB/model and can be selected by the GeneXus SDK type picker.",
                            target: target,
                            extra: new JObject { ["typeName"] = typeName, ["details"] = bindFailure });
                    }
                    if (buildResult == VarBuildResult.DimensionsNotPersistable)
                    {
                        return McpResponse.Err(
                            code: "VariableDimensionsNotPersisted",
                            message: "The requested fixed-size dimensions could not be represented as native GeneXus variable properties. The variable was not created.",
                            hint: "Verify that this GeneXus SDK exposes the variable Dimensions/AttRows/AttCols properties, then retry with positive sizes.",
                            target: target,
                            extra: new JObject { ["dimensions"] = dimensions, ["dimensionSizes"] = dimensionSizes?.DeepClone(), ["details"] = bindFailure });
                    }
                    if (domainBinding != null) domainBound.Add(domainBinding);
                    if (attributeBinding != null) attributeBound.Add(attributeBinding);
                    if (objectBinding != null) objectBound.Add(objectBinding);
                }
                else
                {
                    AddInferredVariableInto(varPart, varName, length, decimals, collection, dimensions, dimensionSizes);
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
                // issue #281: same fail-closed readback for Attribute-bound variables.
                var attrVerifyErr = VerifyAttributeReferencesPersisted(target, attributeBound);
                if (attrVerifyErr != null)
                {
                    var addedVariable = varPart.Variables.FirstOrDefault(v =>
                        string.Equals(v.Name, varName, StringComparison.OrdinalIgnoreCase));
                    if (addedVariable != null) varPart.Variables.Remove(addedVariable);
                    obj.EnsureSave();
                    ScheduleFlush();
                    return attrVerifyErr;
                }
                // SDT / Business Component bindings get the same treatment: a
                // binding the SDK drops at save must surface as
                // VariableTypeNotPersisted, never as a silent primitive.
                var objectVerifyErr = VerifyObjectReferencesPersisted(target, objectBound);
                if (objectVerifyErr != null)
                {
                    var addedVariable = varPart.Variables.FirstOrDefault(v =>
                        string.Equals(v.Name, varName, StringComparison.OrdinalIgnoreCase));
                    if (addedVariable != null) varPart.Variables.Remove(addedVariable);
                    obj.EnsureSave();
                    ScheduleFlush();
                    return objectVerifyErr;
                }

                string verifyName = varName;
                ResolveVariableTarget(target, ref verifyName, out _, out var persistedPart, out _);
                string persistError = VerifyPersistedVariable(persistedPart, varName, RequestedTypeForVerify(typeName, basedOn, basedOnAttribute));
                if (persistError != null)
                    return McpResponse.Err(code: "VariableNotPersisted", message: persistError, target: target,
                        extra: new JObject { ["variable"] = varName, ["requestedType"] = basedOnAttribute ?? basedOn ?? typeName, ["saved"] = false });

                // issue #59: confirm the single added variable actually landed in the
                // persisted part (see VerifyVariablesPersisted).
                var singleVerify = VerifyVariablesPersisted(target, new System.Collections.Generic.List<string> { varName });
                if (singleVerify != null) return singleVerify;

                var dimensionVerifyError = VerifyVariableDimensionsPersisted(target, varName, dimensions, dimensionSizes);
                if (dimensionVerifyError != null)
                {
                    var addedVariable = varPart.Variables.FirstOrDefault(v =>
                        string.Equals(v.Name, varName, StringComparison.OrdinalIgnoreCase));
                    if (addedVariable != null) varPart.Variables.Remove(addedVariable);
                    try { obj.EnsureSave(); ScheduleFlush(); } catch { }
                    return dimensionVerifyError;
                }

                var addedResult = new JObject
                {
                    ["variable"] = varName,
                    ["requestedType"] = typeName,
                    ["persisted"] = true,
                    ["saved"] = true,
                    ["reReadConfirmed"] = true
                };
                if (dimensions.HasValue)
                {
                    addedResult["dimensions"] = dimensions;
                    addedResult["dimensionSizes"] = dimensionSizes?.DeepClone();
                }
                return McpResponse.Ok(target: target, code: "VariableAdded", result: addedResult);
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
                        if (dimensions.HasValue)
                        {
                            var dimensionReconcileError = VerifyVariableDimensionsPersisted(
                                target, varName, dimensions, dimensionSizes);
                            if (dimensionReconcileError != null)
                            {
                                try
                                {
                                    var rollbackObject = _objectService.FindObject(target);
                                    var rollbackPart = GxMcp.Worker.Structure.PartAccessor.GetVariablesPart(rollbackObject);
                                    var addedVariable = rollbackPart?.Variables.FirstOrDefault(v =>
                                        string.Equals(v.Name, varName, StringComparison.OrdinalIgnoreCase));
                                    if (addedVariable != null) rollbackPart.Variables.Remove(addedVariable);
                                    if (rollbackObject != null) ForceSaveVariableOwner(rollbackObject);
                                    ScheduleFlush();
                                }
                                catch { }
                                return dimensionReconcileError;
                            }
                        }

                        Logger.Warn("[VARIABLES-POST-CHECK] AddVariable threw after persistence for " + target
                            + "/" + varName + "; returning reconciled success. Cause: " + ex.Message);
                        var reconciledResult = new JObject
                        {
                            ["variable"] = varName,
                            ["requestedType"] = typeName,
                            ["persisted"] = true,
                            ["saved"] = true,
                            ["reReadConfirmed"] = dimensions.HasValue,
                            ["verification"] = "reconciledAfterFailure",
                            ["warning"] = "The SDK reported an error after save, but a fresh full read confirmed the variable is persisted."
                        };
                        if (dimensions.HasValue)
                        {
                            reconciledResult["dimensions"] = dimensions;
                            reconciledResult["dimensionSizes"] = dimensionSizes?.DeepClone();
                        }
                        return McpResponse.Ok(target: target, code: "VariableAdded", result: reconciledResult);
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

        /// <summary>
        /// Independently verifies fixed-size dimension metadata after a save. The
        /// in-memory VariablesPart is not sufficient evidence: some SDK versions
        /// accept AttNumDim/AttRows/AttCols in memory and silently drop one of the
        /// properties during EnsureSave.
        /// </summary>
        private string VerifyVariableDimensionsPersisted(
            string target, string varName, int? dimensions, JArray dimensionSizes)
        {
            if (!dimensions.HasValue) return null;

            List<int> expectedSizes = new List<int>();
            if (!VariableDimensionSupport.TryParseSizes(dimensionSizes, out expectedSizes, out string sizeError))
            {
                return McpResponse.Err(
                    code: "VariableDimensionsNotPersisted",
                    message: "The requested dimension sizes are not valid for verification: " + sizeError,
                    hint: "Use positive integer dimensionSizes matching dimensions=1 or dimensions=2.",
                    target: target,
                    extra: new JObject
                    {
                        ["variable"] = varName,
                        ["dimensions"] = dimensions.GetValueOrDefault(),
                        ["dimensionSizes"] = dimensionSizes?.DeepClone(),
                        ["saved"] = false
                    });
            }
            int expectedDimensions = dimensions.GetValueOrDefault();

            try
            {
                // Use the same complete, cache-bypassing public read path that the
                // writer uses for other post-save checks. The structured Variables
                // projection is the independent evidence; the direct SDK fallback
                // below is retained for older/kind-specific parts that do not expose
                // that projection yet.
                string freshReadJson = _objectService.ReadObjectSourceForVerification(target, "Variables");
                if (string.IsNullOrWhiteSpace(freshReadJson))
                {
                    return McpResponse.Err(
                        code: "VariableDimensionsNotPersisted",
                        message: "The variable dimensions could not be independently verified because the fresh Variables read was empty.",
                        hint: "Re-read the Variables part and verify the array metadata before retrying.",
                        target: target,
                        extra: new JObject
                        {
                            ["variable"] = varName,
                            ["expectedDimensions"] = expectedDimensions,
                            ["expectedDimensionSizes"] = new JArray(expectedSizes.Cast<object>().ToArray()),
                            ["saved"] = false
                        });
                }

                JObject freshRead;
                try { freshRead = JObject.Parse(freshReadJson); }
                catch (Exception parseException)
                {
                    return McpResponse.Err(
                        code: "VariableDimensionsNotPersisted",
                        message: "The fresh Variables read could not be parsed for dimension verification: " + parseException.Message,
                        hint: "Inspect the saved Variables part and the Worker log before retrying.",
                        target: target,
                        extra: new JObject
                        {
                            ["variable"] = varName,
                            ["expectedDimensions"] = expectedDimensions,
                            ["expectedDimensionSizes"] = new JArray(expectedSizes.Cast<object>().ToArray()),
                            ["saved"] = false
                        });
                }
                if (freshRead["error"] != null)
                {
                    JObject readErrorObject = freshRead["error"] as JObject;
                    string readError = readErrorObject?["message"]?.ToString()
                        ?? freshRead["error"]?.ToString()
                        ?? "The fresh Variables read returned an error.";
                    return McpResponse.Err(
                        code: "VariableDimensionsNotPersisted",
                        message: "The variable dimensions could not be independently verified: " + readError,
                        hint: "Re-read the Variables part and verify the array metadata before retrying.",
                        target: target,
                        extra: new JObject
                        {
                            ["variable"] = varName,
                            ["expectedDimensions"] = expectedDimensions,
                            ["expectedDimensionSizes"] = new JArray(expectedSizes.Cast<object>().ToArray()),
                            ["saved"] = false
                        });
                }
                if (freshRead["variables"] is JArray freshRows)
                {
                    JObject freshRow = freshRows.OfType<JObject>().FirstOrDefault(row =>
                        string.Equals((row["name"]?.ToString() ?? string.Empty).TrimStart('&'),
                            (varName ?? string.Empty).TrimStart('&'), StringComparison.OrdinalIgnoreCase));
                    if (freshRow == null)
                    {
                        return McpResponse.Err(
                            code: "VariableDimensionsNotPersisted",
                            message: "The SDK did not retain the variable after save; dimension metadata cannot be confirmed.",
                            hint: "Re-read Variables and retry the add or modify operation.",
                            target: target,
                            extra: new JObject
                            {
                                ["variable"] = varName,
                                ["expectedDimensions"] = expectedDimensions,
                                ["expectedDimensionSizes"] = new JArray(expectedSizes.Cast<object>().ToArray()),
                                ["saved"] = false
                            });
                    }
                    if (freshRow["dimensionsMalformed"]?.ToObject<bool>() == true)
                    {
                        return McpResponse.Err(
                            code: "VariableDimensionsNotPersisted",
                            message: freshRow["dimensionsError"]?.ToString()
                                ?? "The SDK returned malformed variable dimension metadata after save.",
                            hint: "Repair the array metadata in GeneXus before retrying.",
                            target: target,
                            extra: new JObject
                            {
                                ["variable"] = varName,
                                ["expectedDimensions"] = expectedDimensions,
                                ["expectedDimensionSizes"] = new JArray(expectedSizes.Cast<object>().ToArray()),
                                ["saved"] = false
                            });
                    }
                    int actualDimensions;
                    List<int> actualSizes = new List<int>();
                    bool validProjection = int.TryParse(freshRow["dimensions"]?.ToString(), out actualDimensions)
                        && VariableDimensionSupport.TryParseSizes(freshRow["dimensionSizes"] as JArray,
                            out actualSizes, out _);
                    bool sameProjection = validProjection && actualDimensions == expectedDimensions
                        && actualSizes.Count == expectedSizes.Count;
                    if (sameProjection)
                    {
                        for (int i = 0; i < expectedSizes.Count; i++)
                        {
                            if (actualSizes[i] != expectedSizes[i])
                            {
                                sameProjection = false;
                                break;
                            }
                        }
                    }
                    if (sameProjection) return null;

                    return McpResponse.Err(
                        code: "VariableDimensionsNotPersisted",
                        message: "The SDK accepted the variable write but did not retain the requested fixed-size dimensions.",
                        hint: "The variable was not reported as persisted. Re-read Variables and retry only after checking the GeneXus SDK compatibility.",
                        target: target,
                        extra: new JObject
                        {
                            ["variable"] = varName,
                            ["expectedDimensions"] = expectedDimensions,
                            ["expectedDimensionSizes"] = new JArray(expectedSizes.Cast<object>().ToArray()),
                            ["actualDimensions"] = freshRow["dimensions"],
                            ["actualDimensionSizes"] = freshRow["dimensionSizes"]?.DeepClone(),
                            ["saved"] = false
                        });
                }

                var freshObject = _objectService.FindObjectFresh(target);
                if (freshObject == null)
                {
                    return McpResponse.Err(
                        code: "VariableDimensionsNotPersisted",
                        message: "The variable dimensions could not be independently verified because the saved object was not readable.",
                        hint: "Re-read the Variables part and verify the array metadata before retrying.",
                        target: target,
                        extra: new JObject
                        {
                            ["variable"] = varName,
                            ["dimensions"] = dimensions.GetValueOrDefault(),
                            ["dimensionSizes"] = dimensionSizes?.DeepClone(),
                            ["saved"] = false
                        });
                }

                var freshVariable = GxMcp.Worker.Structure.PartAccessor.GetVariableObjects(freshObject)
                    .FirstOrDefault(v => string.Equals(
                        GxMcp.Worker.Structure.PartAccessor.GetVariableName(v),
                        (varName ?? string.Empty).TrimStart('&'), StringComparison.OrdinalIgnoreCase));
                if (freshVariable == null)
                {
                    return McpResponse.Err(
                        code: "VariableDimensionsNotPersisted",
                        message: "The SDK did not retain the variable after save; dimension metadata cannot be confirmed.",
                        hint: "Re-read Variables and retry the add or modify operation.",
                        target: target,
                        extra: new JObject
                        {
                            ["variable"] = varName,
                            ["dimensions"] = dimensions.GetValueOrDefault(),
                            ["dimensionSizes"] = dimensionSizes?.DeepClone(),
                            ["saved"] = false
                        });
                }

                VariableDimensionInfo actual;
                if (!VariableDimensionSupport.TryRead(freshVariable, out actual) || !actual.IsValid)
                {
                    return McpResponse.Err(
                        code: "VariableDimensionsNotPersisted",
                        message: actual.Error ?? "The SDK returned malformed variable dimension metadata after save.",
                        hint: "Repair the array metadata in GeneXus before retrying; the writer will not report an unverified dimension as persisted.",
                        target: target,
                        extra: new JObject
                        {
                            ["variable"] = varName,
                            ["expectedDimensions"] = expectedDimensions,
                            ["expectedDimensionSizes"] = new JArray(expectedSizes.Cast<object>().ToArray()),
                            ["saved"] = false
                        });
                }

                bool same = actual.Dimensions == expectedDimensions
                    && actual.DimensionSizes != null
                    && actual.DimensionSizes.Count == expectedSizes.Count;
                if (same)
                {
                    for (int i = 0; i < expectedSizes.Count; i++)
                    {
                        if (actual.DimensionSizes[i] != expectedSizes[i])
                        {
                            same = false;
                            break;
                        }
                    }
                }
                if (!same)
                {
                    return McpResponse.Err(
                        code: "VariableDimensionsNotPersisted",
                        message: "The SDK accepted the variable write but did not retain the requested fixed-size dimensions.",
                        hint: "The variable was not reported as persisted. Re-read Variables and retry only after checking the GeneXus SDK compatibility.",
                        target: target,
                        extra: new JObject
                        {
                            ["variable"] = varName,
                            ["expectedDimensions"] = expectedDimensions,
                            ["expectedDimensionSizes"] = new JArray(expectedSizes.Cast<object>().ToArray()),
                            ["actualDimensions"] = actual.Dimensions,
                            ["actualDimensionSizes"] = actual.SizesToJson(),
                            ["saved"] = false
                        });
                }
            }
            catch (Exception ex)
            {
                return McpResponse.Err(
                    code: "VariableDimensionsNotPersisted",
                    message: "Independent variable-dimension verification failed: " + ex.Message,
                    hint: "Do not retry blindly; inspect the saved Variables part and the Worker log.",
                    target: target,
                    extra: new JObject
                    {
                        ["variable"] = varName,
                        ["expectedDimensions"] = expectedDimensions,
                        ["expectedDimensionSizes"] = new JArray(expectedSizes.Cast<object>().ToArray()),
                        ["saved"] = false
                    });
            }

            return null;
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
                string pattern = @"^&\b" + Regex.Escape(name.TrimStart('&')) + @"\b(?:\s*\(\s*\d+(?:\s*,\s*\d+)?\s*\))?\s*:";
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

        // issue #281 — post-save read-back for Attribute-based variable types.
        // Confirms the persisted variable still carries the same AttributeBasedOn
        // (VarBasedOn/DataTypeString "Attribute:<name>") instead of a flattened
        // primitive with the same length but a different picture (999... vs ZZZ...).
        private string VerifyAttributeReferencesPersisted(string target,
            System.Collections.Generic.List<ExpectedAttributeBinding> expected)
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
                    string persistedAttr = null;
                    try { persistedAttr = GxMcp.Worker.Helpers.DomainPropertyApplier.GetAttributeBasedOnName((object)variable); } catch { }
                    if (variable == null || !string.Equals(persistedAttr, binding.AttributeName, StringComparison.OrdinalIgnoreCase))
                    {
                        invalid.Add(new JObject
                        {
                            ["name"] = binding.VarName,
                            ["attribute"] = binding.AttributeName,
                            ["reason"] = variable == null
                                ? "Variable is missing after save."
                                : "Variable reloaded without AttributeBasedOn='" + binding.AttributeName + "' (persisted as '" + (persistedAttr ?? "") + "').",
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
                message: "The SDK did not persist the requested Attribute as a native entity reference.",
                hint: "The operation cannot be completed safely on this GeneXus build. Verify the Attribute belongs to the active KB.",
                nextSteps: new JArray(McpResponse.NextStep(
                    tool: "genexus_read",
                    args: new JObject { ["name"] = target, ["part"] = "Variables" },
                    why: "Shows the object state after the failed persistence check.")),
                target: target,
                extra: new JObject { ["variables"] = invalid });
        }

        // Post-save read-back for SDT / Business Component variable types. The SDK
        // can accept the structural reference in memory and drop it at save,
        // leaving a bare primitive behind. Re-resolve the persisted variable to
        // its bound KB object and compare the GUID plus the binding kind.
        private string VerifyObjectReferencesPersisted(string target,
            System.Collections.Generic.List<ExpectedObjectBinding> expected)
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
                    global::Artech.Architecture.Common.Objects.KBObject bound = null;
                    try
                    {
                        if (variable != null)
                            bound = VariableInjector.ResolveBoundTypeObject(variable, persistedPart?.Model);
                    }
                    catch { bound = null; }
                    string actualKind = null;
                    Guid? actualGuid = null;
                    if (bound != null)
                    {
                        actualGuid = bound.Guid;
                        if (bound is global::Artech.Genexus.Common.Objects.Transaction trn)
                            actualKind = trn.IsBusinessComponent ? "BusinessComponent" : "Transaction";
                        else
                        {
                            try { actualKind = bound.TypeDescriptor?.Name; } catch { }
                        }
                    }
                    string failure = null;
                    bool valid = variable != null && VariableInjector.IsNativeObjectBindingParts(
                        binding.ObjectGuid, binding.BindingKind, actualGuid, actualKind, out failure);
                    if (!valid)
                    {
                        invalid.Add(new JObject
                        {
                            ["name"] = binding.VarName,
                            ["object"] = binding.ObjectName,
                            ["kind"] = binding.BindingKind,
                            ["reason"] = variable == null
                                ? "Variable is missing after save."
                                : failure,
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
                message: "The SDK did not persist the requested SDT/Business Component as a native object reference.",
                hint: "The operation cannot be completed safely on this GeneXus build. Verify the object belongs to the active KB and retry.",
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

            // A Domain rollback that restores DomainKey without DomainBasedOn can
            // itself flatten the read/binding surface (reads key off
            // DomainBasedOn). Restore the live Domain reference too; the Domain
            // object is KB-scoped, so the detached snapshot's reference stays valid.
            try
            {
                global::Artech.Genexus.Common.Objects.Domain originalDomain = null;
                try { originalDomain = originalSnapshot.DomainBasedOn; } catch { }
                if (originalDomain != null)
                {
                    try { restored.DomainBasedOn = originalDomain; }
                    catch { bindingNotRestored = true; restoredDomain = false; }
                }
            }
            catch { bindingNotRestored = true; restoredDomain = false; }

            // issue #281: restore an attribute binding the same way as a Domain.
            // The scalar Type/Length copy above is not enough — without
            // AttributeBasedOn the picture flattens (999... -> ZZZ...).
            bool restoredAttribute = false;
            string originalAttrName = null;
            try { originalAttrName = GxMcp.Worker.Helpers.DomainPropertyApplier.GetAttributeBasedOnName((object)originalSnapshot); } catch { }
            if (!restoredDomain && !string.IsNullOrEmpty(originalAttrName))
            {
                try
                {
                    var attrObj = VariableInjector.FindAttribute(varPart.Model, originalAttrName);
                    if (attrObj != null && VariableInjector.BindVariableToAttribute(restored, attrObj, out _))
                        restoredAttribute = true;
                    else
                        bindingNotRestored = true;
                }
                catch { bindingNotRestored = true; }
            }

            if (!restoredDomain && !restoredAttribute && !string.IsNullOrEmpty(originalTypeName))
            {
                try
                {
                    bool rebound = false;
                    if (VariableInjector.TryParseAttributeReference(originalTypeName, out string restoreAttrName))
                    {
                        var restoreAttr = VariableInjector.FindAttribute(varPart.Model, restoreAttrName);
                        if (restoreAttr != null && VariableInjector.BindVariableToAttribute(restored, restoreAttr, out _))
                            rebound = true;
                    }
                    else if (originalSnapshot.Type == global::Artech.Genexus.Common.eDBType.GX_SDT
                        || originalSnapshot.Type == global::Artech.Genexus.Common.eDBType.GX_BUSCOMP)
                    {
                        var originalBoundObject = VariableInjector.ResolveTypeObject(varPart.Model, originalTypeName);
                        if (originalBoundObject != null && originalBoundObject.TypeDescriptor.Name.Equals("SDT", StringComparison.OrdinalIgnoreCase))
                        {
                            if (VariableInjector.BindVariableToSdt(restored, originalBoundObject, out _))
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

            // Apply dimensions after all type/binding restore operations: several
            // SDK binding setters rebuild the ATT property bag and would otherwise
            // erase a vector/matrix copied earlier in the rollback.
            try
            {
                if (!VariableDimensionSupport.TryCopy(originalSnapshot, restored, out string dimensionRestoreError))
                    bindingNotRestored = true;
            }
            catch { bindingNotRestored = true; }

            varPart.Variables.Add(restored);
            return bindingNotRestored;
        }

        // ── Task 4.3 (v2.3.8) — genexus_modify_variable ──────────────────────────
        // Atomically change a variable's type while preserving its name (and
        // description when possible). Implemented as delete+add over the same
        // VariablesPart, with a snapshot of the pre-change variable set so we
        // can roll back if obj.Save() throws.
        public string ModifyVariable(string target, string varName, string newTypeName, string basedOn = null, bool dryRun = false,
            int? length = null, int? decimals = null, bool? collection = null, string basedOnAttribute = null,
            int? dimensions = null, JArray dimensionSizes = null)
        {
            string dimensionCode, dimensionMessage, dimensionHint;
            JObject dimensionExtra;
            if (!VariableDimensionSupport.TryValidate(dimensions, dimensionSizes, collection,
                out dimensionCode, out dimensionMessage, out dimensionHint, out dimensionExtra))
            {
                return McpResponse.Err(
                    code: dimensionCode,
                    message: dimensionMessage,
                    hint: dimensionHint,
                    target: target,
                    extra: dimensionExtra);
            }

            if (dryRun)
            {
                string targetError = ValidateVariableDryRunTarget(target);
                if (targetError != null) return targetError;
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
                            ["basedOnAttribute"] = basedOnAttribute,
                            ["length"] = length,
                            ["decimals"] = decimals,
                            ["collection"] = collection,
                            ["dimensions"] = dimensions,
                            ["dimensionSizes"] = dimensionSizes?.DeepClone()
                        }
                    });
            }
            var raw = ModifyVariableInternal(target, varName, newTypeName, basedOn, length, decimals, collection, basedOnAttribute, dimensions, dimensionSizes);
            MarkDirtyIfSuccess(raw, target);
            return WrapWithPersistedState(raw, target, "Variables", GxMcp.Worker.Helpers.WriteResultMeta.TypedWriter);
        }

        private string ModifyVariableInternal(string target, string varName, string newTypeName, string basedOn,
            int? length = null, int? decimals = null, bool? collection = null, string basedOnAttribute = null,
            int? dimensions = null, JArray dimensionSizes = null)
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

            if (resolution.CanonicalType == "AttributeReference" && !string.IsNullOrEmpty(resolution.AttributeName))
            {
                resolvedTypeForSdk = resolution.AttributeName;
            }
            else if (resolution.CanonicalType == "DomainReference" && !string.IsNullOrEmpty(resolution.DomainName))
            {
                resolvedTypeForSdk = resolution.DomainName;
            }
            else
            {
                resolvedLength = resolution.Length;
                resolvedDecimals = resolution.Decimals;
                resolvedTypeForSdk = resolution.CanonicalType;
            }
            // `basedOnAttribute` / `basedOn="Attribute:X"` take precedence — they bind
            // the live Attribute object (issue #281) instead of flattening to a primitive.
            if (!string.IsNullOrWhiteSpace(basedOnAttribute))
            {
                string attrName = basedOnAttribute.Trim();
                if (VariableInjector.TryParseAttributeReference(attrName, out string parsedAttr))
                    attrName = parsedAttr;
                resolvedTypeForSdk = attrName;
                resolution = new GxMcp.Worker.Helpers.TypeResolution
                {
                    Recognized = true,
                    CanonicalType = "AttributeReference",
                    AttributeName = attrName,
                    DomainName = attrName,
                    Suggestion = attrName,
                    AcceptedList = resolution?.AcceptedList
                };
            }
            // `basedOn` (optional) takes precedence over a parsed DomainReference —
            // gives the caller explicit control when the typeName is ambiguous.
            else if (!string.IsNullOrWhiteSpace(basedOn))
            {
                string trimmedBasedOn = basedOn.Trim();
                if (VariableInjector.TryParseAttributeReference(trimmedBasedOn, out string parsedBasedOnAttr))
                {
                    resolvedTypeForSdk = parsedBasedOnAttr;
                    resolution = new GxMcp.Worker.Helpers.TypeResolution
                    {
                        Recognized = true,
                        CanonicalType = "AttributeReference",
                        AttributeName = parsedBasedOnAttr,
                        DomainName = parsedBasedOnAttr,
                        Suggestion = parsedBasedOnAttr,
                        AcceptedList = resolution?.AcceptedList
                    };
                }
                else
                {
                    resolvedTypeForSdk = trimmedBasedOn;
                    resolution = new GxMcp.Worker.Helpers.TypeResolution
                    {
                        Recognized = true,
                        CanonicalType = "DomainReference",
                        DomainName = trimmedBasedOn,
                        Suggestion = trimmedBasedOn,
                        AcceptedList = resolution?.AcceptedList
                    };
                }
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

                VariableDimensionInfo existingDimensions;
                bool existingDimensionsRead = VariableDimensionSupport.TryRead(existing, out existingDimensions);
                if (!existingDimensionsRead || !existingDimensions.IsValid)
                {
                    return McpResponse.Err(
                        code: "VariableDimensionsMalformed",
                        message: "The existing variable has malformed dimension metadata; it was not modified.",
                        hint: "Re-read the Variables part and repair the array metadata in the GeneXus IDE before retrying.",
                        target: target,
                        extra: new JObject { ["variable"] = varName, ["details"] = existingDimensions.Error });
                }

                bool existingIsCollection = false;
                try { existingIsCollection = existing.IsCollection; } catch { }
                if (dimensions.HasValue && existingIsCollection && collection != false)
                {
                    return McpResponse.Err(
                        code: "CollectionDimensionConflict",
                        message: "The existing variable is a collection; dimensions require collection=false.",
                        hint: "Pass collection=false explicitly to convert it to a fixed-size vector/matrix.",
                        target: target,
                        extra: new JObject { ["variable"] = varName, ["dimensions"] = dimensions.Value });
                }

                // The delete+add implementation starts with a scalar. Preserve the
                // existing collection/array shape when the request is silent, and
                // make explicit conversion semantics unambiguous:
                //   collection=true  -> collection, no fixed dimensions
                //   collection=false -> scalar, no fixed dimensions
                //   omitted          -> retain the existing shape
                bool effectiveIsCollection = collection ?? existingIsCollection;
                int? effectiveDimensions = dimensions;
                JArray effectiveDimensionSizes = dimensionSizes;
                if (effectiveIsCollection)
                {
                    effectiveDimensions = null;
                    effectiveDimensionSizes = null;
                }
                else if (collection == false)
                {
                    effectiveDimensions = null;
                    effectiveDimensionSizes = null;
                }
                else if (!effectiveDimensions.HasValue && existingDimensions.Dimensions > 0)
                {
                    effectiveDimensions = existingDimensions.Dimensions;
                    effectiveDimensionSizes = existingDimensions.SizesToJson();
                }
                bool? effectiveCollection = effectiveIsCollection;

                global::Artech.Genexus.Common.Objects.Domain requestedDomain = null;
                global::Artech.Genexus.Common.Objects.Attribute requestedAttribute = null;
                if (resolution.CanonicalType == "AttributeReference")
                {
                    string attrName = resolution.AttributeName ?? resolvedTypeForSdk;
                    requestedAttribute = VariableInjector.FindAttribute(varPart.Model, attrName);
                    if (requestedAttribute == null)
                    {
                        return McpResponse.Err(
                            code: "UnknownType",
                            message: $"Attribute '{resolvedTypeForSdk}' was not found. The original variable was not changed.",
                            hint: "Verify the Attribute name via genexus_list_objects, then retry with basedOnAttribute=<name> or typeName=Attribute:<name>.",
                            target: target,
                            extra: new JObject { ["basedOnAttribute"] = resolvedTypeForSdk });
                    }
                }
                else if (resolution.CanonicalType == "DomainReference")
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

                // issue #281: fail closed instead of silently flattening an
                // attribute-based variable (Attribute:X, picture 999... vs ZZZ...)
                // into a primitive/Domain/SDT with the same length. A retype that
                // drops AttributeBasedOn is only allowed when the caller explicitly
                // targets the same attribute.
                string existingAttrName = null;
                try { existingAttrName = GxMcp.Worker.Helpers.DomainPropertyApplier.GetAttributeBasedOnName((object)existing); } catch { }
                if (!string.IsNullOrEmpty(existingAttrName)
                    && !string.Equals(resolution.CanonicalType, "AttributeReference", StringComparison.OrdinalIgnoreCase))
                {
                    return McpResponse.Err(
                        code: "AttributeBindingWouldBeLost",
                        message: $"Variable '&{varName}' is based on Attribute '{existingAttrName}'; retyping it to '{newTypeName}' would drop VarBasedOn/DataTypeString and change its picture semantics. The variable was not changed.",
                        hint: "Pass basedOnAttribute='" + existingAttrName + "' (or newTypeName='Attribute:" + existingAttrName + "') to preserve the binding, or delete + add a new variable if a primitive type is really intended.",
                        nextSteps: new JArray(McpResponse.NextStep(
                            tool: "genexus_properties",
                            args: new JObject { ["action"] = "get", ["name"] = target, ["control"] = "&" + varName },
                            why: "Shows VarBasedOn/DataTypeString/ATT_PICTURE so the attribute binding can be confirmed before retrying.")),
                        target: target,
                        extra: new JObject { ["varName"] = varName, ["basedOnAttribute"] = existingAttrName });
                }
                if (!string.IsNullOrEmpty(existingAttrName)
                    && string.Equals(resolution.CanonicalType, "AttributeReference", StringComparison.OrdinalIgnoreCase))
                {
                    string requestedAttrName = resolution.AttributeName ?? resolvedTypeForSdk;
                    if (!string.Equals(existingAttrName, requestedAttrName, StringComparison.OrdinalIgnoreCase))
                    {
                        // Retargeting from one attribute to another is allowed — it is
                        // an explicit attribute-to-attribute move, not a silent flatten.
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
                ExpectedAttributeBinding expectedAttributeBinding = null;
                ExpectedObjectBinding expectedObjectBinding = null;
                try
                {
                    varPart.Variables.Remove(existing);

                    var newVar = new global::Artech.Genexus.Common.Variable(varPart);
                    newVar.Name = varName;
                    if (!string.IsNullOrEmpty(preservedDescription))
                    {
                        try { newVar.Description = preservedDescription; } catch { /* best-effort */ }
                    }

                    if (string.Equals(resolution.CanonicalType, "AttributeReference", StringComparison.OrdinalIgnoreCase))
                    {
                        if (requestedAttribute == null)
                            throw new InvalidOperationException($"Attribute '{resolvedTypeForSdk}' was not found; original variable preserved.");
                        if (!VariableInjector.BindVariableToAttribute(newVar, requestedAttribute, out var attrBindFailure))
                            throw new InvalidOperationException("VariableTypeNotPersisted: " + attrBindFailure);
                        boundTypeName = "Attribute:" + requestedAttribute.Name;
                        expectedAttributeBinding = new ExpectedAttributeBinding { VarName = varName, AttributeName = requestedAttribute.Name };
                    }
                    else if (resolution.CanonicalType != "DomainReference"
                        && resolution.CanonicalType != "AttributeReference"
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
                    else if (resolution.CanonicalType != "DomainReference" && resolution.CanonicalType != "AttributeReference")
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
                            if (!VariableInjector.BindVariableToSdt(newVar, targetObj, out var sdtBindFailure))
                                throw new InvalidOperationException("VariableTypeNotPersisted: " + sdtBindFailure);
                            boundTypeName = targetObj.Name;
                            expectedObjectBinding = new ExpectedObjectBinding
                            {
                                VarName = varName,
                                ObjectName = targetObj.Name,
                                ObjectGuid = targetObj.Guid,
                                BindingKind = "SDT"
                            };
                        }
                        else if (targetObj is global::Artech.Genexus.Common.Objects.Transaction trn && trn.IsBusinessComponent)
                        {
                            try
                            {
                                VariableInjector.BindVariableToBC(newVar, targetObj);
                            }
                            catch (Exception ex)
                            {
                                throw new InvalidOperationException("VariableTypeNotPersisted: " + (ex.InnerException?.Message ?? ex.Message));
                            }
                            boundTypeName = targetObj.Name;
                            expectedObjectBinding = new ExpectedObjectBinding
                            {
                                VarName = varName,
                                ObjectName = targetObj.Name,
                                ObjectGuid = targetObj.Guid,
                                BindingKind = "BusinessComponent"
                            };
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

                    // Preserve an existing collection when the caller does not
                    // explicitly change it. A retype must not silently turn a
                    // collection into a scalar (or vice versa).
                    if (effectiveCollection.HasValue) { try { newVar.IsCollection = effectiveCollection.Value; } catch { } }
                    if (effectiveDimensions.HasValue && !VariableDimensionSupport.TryApply(newVar, effectiveDimensions, effectiveDimensionSizes, out var dimensionFailure))
                        throw new InvalidOperationException("VariableDimensionsNotPersisted: " + dimensionFailure);
                    varPart.Variables.Add(newVar);

                    ForceSaveVariableOwner(obj);
                    ScheduleFlush(force: true);

                    string verifyName = varName;
                    ResolveVariableTarget(target, ref verifyName, out _, out var persistedPart, out _);
                    string persistError = VerifyPersistedVariable(persistedPart, varName, RequestedTypeForVerify(newTypeName, basedOn, basedOnAttribute));
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
                    var attrVerifyError = VerifyAttributeReferencesPersisted(target,
                        expectedAttributeBinding == null
                            ? new System.Collections.Generic.List<ExpectedAttributeBinding>()
                            : new System.Collections.Generic.List<ExpectedAttributeBinding> { expectedAttributeBinding });
                    if (attrVerifyError != null)
                    {
                        bindingNotRestored = RestoreVariableSnapshot(
                            varPart, varName, originalSnapshot, preservedDescription, originalTypeName);
                        obj.EnsureSave();
                        ScheduleFlush();
                        return attrVerifyError;
                    }
                    var objectVerifyError = VerifyObjectReferencesPersisted(target,
                        expectedObjectBinding == null
                            ? new System.Collections.Generic.List<ExpectedObjectBinding>()
                            : new System.Collections.Generic.List<ExpectedObjectBinding> { expectedObjectBinding });
                    if (objectVerifyError != null)
                    {
                        bindingNotRestored = RestoreVariableSnapshot(
                            varPart, varName, originalSnapshot, preservedDescription, originalTypeName);
                        obj.EnsureSave();
                        ScheduleFlush();
                        return objectVerifyError;
                    }

                    var dimensionVerifyError = VerifyVariableDimensionsPersisted(
                        target, varName, effectiveDimensions, effectiveDimensionSizes);
                    if (dimensionVerifyError != null)
                    {
                        bindingNotRestored = RestoreVariableSnapshot(
                            varPart, varName, originalSnapshot, preservedDescription, originalTypeName);
                        try { obj.EnsureSave(); ScheduleFlush(); } catch { }
                        return dimensionVerifyError;
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
                    if (effectiveDimensions.HasValue)
                    {
                        resultPayload["dimensions"] = effectiveDimensions.Value;
                        resultPayload["dimensionSizes"] = effectiveDimensionSizes?.DeepClone();
                    }
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
            bool rollbackOnFailure = true, bool? collection = null,
            int? dimensions = null, JArray dimensionSizes = null)
        {
            string dimensionCode, dimensionMessage, dimensionHint;
            JObject dimensionExtra;
            if (!VariableDimensionSupport.TryValidate(dimensions, dimensionSizes, collection,
                out dimensionCode, out dimensionMessage, out dimensionHint, out dimensionExtra))
            {
                return McpResponse.Err(
                    code: dimensionCode,
                    message: dimensionMessage,
                    hint: dimensionHint,
                    target: target,
                    extra: dimensionExtra);
            }

            action = (action ?? "add").Trim().ToLowerInvariant();
            if (action != "add" && action != "modify")
                return McpResponse.Err(code: "InvalidAction",
                    message: "Business Component typing supports action=add or action=modify.", target: target);

            string normalizedName = varName;
            var targetError = ResolveVariableTarget(target, ref normalizedName,
                out var owner, out var variables, out var existing);
            if (targetError != null) return targetError;

            bool existingIsCollection = false;
            try { existingIsCollection = existing?.IsCollection == true; } catch { }
            VariableDimensionInfo existingDimensions;
            bool existingDimensionsRead = VariableDimensionSupport.TryRead(existing, out existingDimensions);
            if (existing != null && (!existingDimensionsRead || !existingDimensions.IsValid))
            {
                return McpResponse.Err(
                    code: "VariableDimensionsMalformed",
                    message: "The existing variable has malformed dimension metadata; it was not modified.",
                    hint: "Repair the array metadata in GeneXus before retrying.",
                    target: target,
                    extra: new JObject { ["variable"] = normalizedName, ["details"] = existingDimensions.Error });
            }
            if (dimensions.HasValue && existingIsCollection && collection != false)
            {
                return McpResponse.Err(
                    code: "CollectionDimensionConflict",
                    message: "The existing variable is a collection; dimensions require collection=false.",
                    hint: "Pass collection=false explicitly to convert it to a fixed-size vector/matrix.",
                    target: target,
                    extra: new JObject { ["variable"] = normalizedName, ["dimensions"] = dimensions.Value });
            }

            bool effectiveIsCollection = collection ?? existingIsCollection;
            int? effectiveDimensions = dimensions;
            JArray effectiveDimensionSizes = dimensionSizes;
            if (effectiveIsCollection || collection == false)
            {
                effectiveDimensions = null;
                effectiveDimensionSizes = null;
            }
            else if (!effectiveDimensions.HasValue && existingDimensions.Dimensions > 0)
            {
                effectiveDimensions = existingDimensions.Dimensions;
                effectiveDimensionSizes = existingDimensions.SizesToJson();
            }

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
            if (action == "modify" && alreadyBound && !dryRun
                && !dimensions.HasValue && collection == null)
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
            if (effectiveDimensions.HasValue)
            {
                diff["dimensions"] = effectiveDimensions.Value;
                diff["dimensionSizes"] = effectiveDimensionSizes?.DeepClone();
            }
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
                        if (currentVariable != null)
                        {
                            try { description = currentVariable.Description; } catch { }
                            currentPart.Variables.Remove(currentVariable);
                        }

                        var replacement = new global::Artech.Genexus.Common.Variable(currentPart)
                        {
                            Name = normalizedName
                        };
                        try { replacement.Description = description; } catch { }
                        VariableInjector.BindVariableToBC(replacement, bc);
                        try { replacement.IsCollection = effectiveIsCollection; } catch { }
                        if (effectiveDimensions.HasValue
                            && !VariableDimensionSupport.TryApply(replacement, effectiveDimensions,
                                effectiveDimensionSizes, out var dimensionFailure))
                            throw new InvalidOperationException("VariableDimensionsNotPersisted: " + dimensionFailure);
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

                var dimensionVerifyError = VerifyVariableDimensionsPersisted(
                    target, normalizedName, effectiveDimensions, effectiveDimensionSizes);
                if (dimensionVerifyError != null)
                    throw new InvalidOperationException("VariableDimensionsNotPersisted: "
                        + (dimensionVerifyError ?? string.Empty));

                string versionAfter = versionAfterCommit;
                JObject persistedIdentity = DescribeVariableBinding(persistedVariable, persistedPart.Model);
                diff["persisted"] = persistedIdentity.DeepClone();
                MarkDirtyIfSuccess("{\"status\":\"ok\"}", target);
                var result = new JObject
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
                };
                if (effectiveDimensions.HasValue)
                {
                    result["dimensions"] = effectiveDimensions.Value;
                    result["dimensionSizes"] = effectiveDimensionSizes?.DeepClone();
                }
                return McpResponse.Ok(target: target,
                    code: action == "add" ? "VariableAdded" : "VariableRetyped",
                    result: result);
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
                bool dimensionFailure = ex.Message.StartsWith("VariableDimensionsNotPersisted:", StringComparison.Ordinal);
                string code = versionConflict
                    ? "VersionConflict"
                    : dimensionFailure ? "VariableDimensionsNotPersisted" : "VariableTypeNotPersisted";
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

        // issue #281: normalize an attribute request to its canonical
        // "Attribute:<name>" verify form so VerifyPersistedVariable checks the
        // AttributeBasedOn binding unconditionally. A bare basedOnAttribute name
        // would otherwise be ambiguous with a Domain/SDT bare name.
        internal static string RequestedTypeForVerify(string typeName, string basedOn, string basedOnAttribute)
        {
            if (!string.IsNullOrWhiteSpace(basedOnAttribute))
            {
                string attr = basedOnAttribute.Trim();
                if (VariableInjector.TryParseAttributeReference(attr, out string parsed))
                    attr = parsed;
                else
                    attr = attr.TrimStart('&');
                if (!string.IsNullOrEmpty(attr)) return "Attribute:" + attr;
            }
            if (!string.IsNullOrWhiteSpace(basedOn)) return basedOn.Trim();
            return typeName;
        }

        private string VerifyPersistedVariable(global::Artech.Genexus.Common.Parts.VariablesPart part,
            string variableName, string requestedType)
        {
            if (part == null) return "The Variables part could not be reloaded after save.";
            var variable = part.Variables.FirstOrDefault(v => string.Equals(v.Name, variableName, StringComparison.OrdinalIgnoreCase));
            if (variable == null) return "Variable '&" + variableName + "' was not present after reload.";
            if (!string.IsNullOrWhiteSpace(requestedType))
            {
                // issue #281: Attribute requests ("Attribute:X" or bare name via
                // basedOnAttribute) must reload with the same AttributeBasedOn.
                string requestedAttr = requestedType.Trim();
                if (VariableInjector.TryParseAttributeReference(requestedAttr, out string parsedRequestedAttr))
                    requestedAttr = parsedRequestedAttr;
                else if (!requestedAttr.Contains(":") && !requestedAttr.Contains("(") && !requestedAttr.Contains(" ")
                    && requestedAttr.IndexOf('.') < 0)
                {
                    // A bare basedOnAttribute name arrives here without the prefix
                    // (e.g. VerifyPersistedVariable(persistedPart, name, "CttCar")
                    // from a basedOnAttribute path). Only treat it as an attribute
                    // check when the variable actually carries an attribute binding
                    // or the requested name resolves to a KB Attribute; otherwise
                    // fall through to the Domain check below.
                    var maybeAttr = VariableInjector.FindAttribute(part.Model, requestedAttr);
                    string persistedAttrPeek = null;
                    try { persistedAttrPeek = GxMcp.Worker.Helpers.DomainPropertyApplier.GetAttributeBasedOnName((object)variable); } catch { }
                    if (maybeAttr != null && !string.IsNullOrEmpty(persistedAttrPeek))
                        requestedAttr = maybeAttr.Name;
                    else
                        requestedAttr = null;
                }
                else requestedAttr = null;
                if (!string.IsNullOrEmpty(requestedAttr))
                {
                    string persistedAttr = null;
                    try { persistedAttr = GxMcp.Worker.Helpers.DomainPropertyApplier.GetAttributeBasedOnName((object)variable); } catch { }
                    if (!string.Equals(persistedAttr, requestedAttr, StringComparison.OrdinalIgnoreCase))
                        return "Variable '&" + variableName + "' reloaded without the requested Attribute '" + requestedAttr + "'.";
                    return null;
                }
                var referenced = _objectService.FindObject(requestedType.TrimStart('&'));
                if (referenced is global::Artech.Genexus.Common.Objects.Domain requestedDomain)
                {
                    string persistedDomain = null;
                    try { persistedDomain = variable.DomainBasedOn?.Name; } catch { }
                    Guid? expectedKeyType = null;
                    int? expectedKeyId = null;
                    Guid? persistedKeyType = null;
                    int? persistedKeyId = null;
                    try { expectedKeyType = requestedDomain.Key?.Type; expectedKeyId = requestedDomain.Key?.Id; } catch { }
                    try { persistedKeyType = variable.DomainKey?.Type; persistedKeyId = variable.DomainKey?.Id; } catch { }
                    if (!VariableInjector.IsNativeDomainBindingParts(
                            requestedDomain.Name, expectedKeyType, expectedKeyId,
                            persistedDomain, persistedKeyType, persistedKeyId, out string domainFailure))
                        return "Variable '&" + variableName + "' reloaded without the requested Domain '" + requestedDomain.Name + "'. " + domainFailure;
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
