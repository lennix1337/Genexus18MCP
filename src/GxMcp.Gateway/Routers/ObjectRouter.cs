using System.Linq;
using Newtonsoft.Json.Linq;
namespace GxMcp.Gateway.Routers
{
    public class ObjectRouter : IMcpModuleRouter
    {
        public string ModuleName => "Object";

        private static readonly string[] _validEditModes = { "xml", "ops", "patch", "full" };

        // Keep in sync with GxMcp.Worker.Services.SemanticOpsService.Dispatch — adding an op
        // there without adding it here will cause the gateway to reject the call before it
        // ever reaches the worker.
        private static readonly string[] _validSemanticOps = {
            "set_attribute", "add_attribute", "remove_attribute",
            "add_rule", "remove_rule", "set_property"
        };

        private static void ValidateEditMode(string? mode)
        {
            if (string.IsNullOrEmpty(mode)) return;
            if (_validEditModes.Contains(mode)) return;
            throw new UsageException(
                "usage_error",
                DidYouMean.FormatSuggestionMessage("edit mode", mode, _validEditModes)
            );
        }

        private static void ValidateSemanticOps(JToken? opsTok)
        {
            if (!(opsTok is JArray ops)) return;
            for (int i = 0; i < ops.Count; i++)
            {
                string? opName = (ops[i] as JObject)?["op"]?.ToString();
                if (string.IsNullOrEmpty(opName))
                {
                    throw new UsageException("usage_error", $"ops[{i}]: 'op' field is required.");
                }
                if (_validSemanticOps.Contains(opName)) continue;
                throw new UsageException(
                    "usage_error",
                    DidYouMean.FormatSuggestionMessage($"ops[{i}].op", opName, _validSemanticOps)
                );
            }
        }

        /// <summary>
        /// Issue #205/#206: reject `scope` / `indentation` on every form except the abbreviated
        /// mode=patch replace shorthand. The rejection is a coded usage error
        /// (`ScopeUnsupportedPatchForm` / `IndentationUnsupportedPatchForm`) raised before any
        /// normalization to `context`/`content`, so the protection can never be silently
        /// dropped — and never a partially-applied write.
        /// </summary>
        private static void RejectProtectedOptionsUnlessAbbreviatedPatch(JObject? args)
        {
            if (args == null) return;
            // Only the object shape is routed as the abbreviated patch form (the same check the
            // Patch routing below makes), so anything else — including a JSON string — falls
            // through to the fail-closed rejection instead of dropping the protection.
            JObject? patchObj = args["patch"] as JObject;
            JToken? scopeTok = patchObj?["scope"] ?? args["scope"];
            JToken? indentationTok = patchObj?["indentation"] ?? args["indentation"];
            if (scopeTok == null && indentationTok == null) return;

            bool hasScope = scopeTok != null;
            bool objectShaped = (scopeTok == null || scopeTok is JObject)
                && (indentationTok == null || indentationTok is JObject);
            bool hasTargets = args["targets"] is JArray;
            bool hasParts = args["parts"] is JArray partsArr && partsArr.Count > 0;
            bool hasOperation = !string.IsNullOrWhiteSpace(args["operation"]?.ToString());
            bool abbreviatedPatchForm = objectShaped
                && patchObj != null
                && (patchObj["find"] != null || patchObj["replace"] != null)
                && string.Equals(args["mode"]?.ToString(), "patch", StringComparison.OrdinalIgnoreCase)
                && !hasTargets && !hasParts && !hasOperation
                && args["changeSet"] == null;
            if (abbreviatedPatchForm)
            {
                // The form is supported; the anchors still have to be usable. A scope without a
                // start anchor would silently search the whole part, so it is rejected up front
                // too (issue #205 rule 1), before any read.
                if (scopeTok is JObject scopeObj && string.IsNullOrWhiteSpace(scopeObj["start"]?.ToString()))
                {
                    throw new UsageException(
                        "ScopeStartRequired",
                        "patch.scope.start is required; a scope without a start anchor would silently search the whole part. "
                        + "No write was attempted.");
                }
                return;
            }

            throw new UsageException(
                hasScope ? "ScopeUnsupportedPatchForm" : "IndentationUnsupportedPatchForm",
                $"patch.{(hasScope ? "scope" : "indentation")} is supported only in the abbreviated mode=patch form "
                + "(patch={find,replace}); it cannot be combined with operation, mode=ops, targets[], parts[], "
                + "Insert_After or Append, and it must be a JSON object. No write was attempted.");
        }

        /// <summary>
        /// Issue #357: <c>ifUnchangedSince</c> is defined for exactly one shape —
        /// a single named part, one pagination window, one object. Every other read
        /// form (batch, multi-part, full object) has no single representation to
        /// bind a token to, so accepting the argument and ignoring it would hand the
        /// caller a silent no-op. Reject it, by name, before routing.
        /// </summary>
        private static void RejectConditionalReadForNonSinglePart(JObject? args, string readForm)
        {
            string? token = args?["ifUnchangedSince"]?.ToString();
            if (string.IsNullOrWhiteSpace(token)) return;
            throw new UsageException(
                "ConditionalReadUnsupportedForm",
                $"ifUnchangedSince is supported only on a single-part read with one object and one "
                + $"pagination window; it cannot be used with {readForm}. The token returned by a "
                + "single-part read is bound to that exact part and window, so it cannot answer for "
                + "another form. No read was performed.");
        }

        private static void NormalizeVersionAliases(JObject? input)
        {
            if (input != null)
            {
                string? baseVersion = input["baseVersion"]?.ToString();
                string? expectedVersion = input["expectedVersion"]?.ToString();
                if (!string.IsNullOrWhiteSpace(baseVersion) && !string.IsNullOrWhiteSpace(expectedVersion)
                    && !string.Equals(baseVersion, expectedVersion, StringComparison.Ordinal))
                    throw new UsageException("VersionAliasesConflict", "baseVersion and expectedVersion must identify the same read; no write was attempted.");
                if (string.IsNullOrWhiteSpace(baseVersion) && !string.IsNullOrWhiteSpace(expectedVersion))
                    input["baseVersion"] = expectedVersion;
            }
        }

        public object? ConvertToolCall(string toolName, JObject? args)
        {
            string? nameArg = args?["name"]?.ToString();
            string? target = nameArg ?? args?["path"]?.ToString() ?? args?["entityKey"]?.ToString() ?? args?["guid"]?.ToString();
            string part = args?["part"]?.ToString() ?? "Source";

            switch (toolName)
            {
                case "genexus_read":
                {
                    // genexus_search_source and genexus_refactor name the object `objectName`; agents carry that
                    // spelling over to genexus_read, where it used to be dropped silently (empty-name lookup).
                    if (string.IsNullOrEmpty(nameArg))
                    {
                        string? objectNameArg = args?["objectName"]?.ToString();
                        if (!string.IsNullOrEmpty(objectNameArg))
                        {
                            nameArg = objectNameArg;
                            target = objectNameArg;
                        }
                    }
                    var targetsTokRead = args?["targets"];
                    bool hasTargetsRead = targetsTokRead is JArray;
                    bool hasNameRead = !string.IsNullOrEmpty(nameArg) || !string.IsNullOrEmpty(args?["path"]?.ToString())
                        || !string.IsNullOrEmpty(args?["entityKey"]?.ToString()) || !string.IsNullOrEmpty(args?["guid"]?.ToString());
                    if (!hasNameRead && !hasTargetsRead)
                        throw new UsageException("usage_error", "genexus_read requires an object identity: pass 'name' (alias 'objectName'), 'guid', 'entityKey' or 'path', or 'targets' for a batch read. No lookup was performed.");
                    if (hasNameRead && hasTargetsRead)
                        throw new UsageException("usage_error", "name and targets are mutually exclusive");
                    if (hasTargetsRead)
                    {
                        RejectConditionalReadForNonSinglePart(args, "targets[] batch reads");
                        return new {
                            module = "Batch",
                            action = "BatchRead",
                            refresh = args?["refresh"]?.ToObject<bool?>() ?? false,
                            items = (JArray)targetsTokRead!,
                            part = part,
                            // A batch read must preserve the same field-selection contract as
                            // a single-object read. Previously targets short-circuited before
                            // parts was inspected, silently turning parts:["Variables"] into
                            // the default Source read.
                            parts = args?["parts"] as JArray
                        };
                    }
                    var partsTok = args?["parts"];
                    bool hasParts = partsTok is JArray partsArr && partsArr.Count > 0;
                    if (hasParts)
                    {
                        RejectConditionalReadForNonSinglePart(args, "parts[] multi-part reads");
                        return new {
                            module = "Read",
                            action = "ExtractParts",
                            refresh = args?["refresh"]?.ToObject<bool?>() ?? false,
                            target = target,
                            parts = (JArray)partsTok!,
                            // Each part is paged with the same window; without these the
                            // worker capped every part at the MCP default and the
                            // advertised offset/limit were silently dropped (#881).
                            offset = args?["offset"]?.ToObject<int?>(),
                            limit = args?["limit"]?.ToObject<int?>(),
                            type = args?["type"]?.ToString(),
                            guid = args?["guid"]?.ToString(),
                            entityKey = args?["entityKey"]?.ToString(),
                            path = args?["path"]?.ToString()
                        };
                    }
                    string partStr = args?["part"]?.ToString()?.Trim() ?? string.Empty;
                    bool isFullOrAll = string.IsNullOrEmpty(partStr)
                        || string.Equals(partStr, "all", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(partStr, "full", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(partStr, "summary", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(partStr, "360", StringComparison.OrdinalIgnoreCase);

                    if (isFullOrAll)
                    {
                        // SOTA 1-roundtrip default: omitting 'part' or requesting 'all'/'full'/'summary'/'360'
                        // extracts the full object (rules, source/events, variables, structure, signatures) tailored to the type.
                        RejectConditionalReadForNonSinglePart(args, "full-object reads");
                        return new {
                            module = "Read",
                            action = "ExtractFullObject",
                            refresh = args?["refresh"]?.ToObject<bool?>() ?? false,
                            target = target,
                            type = args?["type"]?.ToString(),
                            guid = args?["guid"]?.ToString(),
                            entityKey = args?["entityKey"]?.ToString(),
                            path = args?["path"]?.ToString()
                        };
                    }
                    return new {
                        module = "Read",
                        action = "ExtractSource",
                        refresh = args?["refresh"]?.ToObject<bool?>() ?? false,
                        target = target,
                        part = part,
                        offset = args?["offset"]?.ToObject<int?>(),
                        limit = args?["limit"]?.ToObject<int?>(),
                        type = args?["type"]?.ToString(),
                        guid = args?["guid"]?.ToString(),
                        entityKey = args?["entityKey"]?.ToString(),
                        path = args?["path"]?.ToString(),
                        // Issue #357: an opaque token bound to one exact part representation.
                        // Omitted entirely when absent, so the unconditional route is unchanged.
                        ifUnchangedSince = args?["ifUnchangedSince"]?.ToString(),
                        // Internal transport metadata (see Program.ToolDispatch). Never a
                        // public argument; the Gateway sets it when a write fence applies.
                        requireAuthoritativeRead = args?["_requireAuthoritativeRead"]?.ToObject<bool?>() ?? false
                    };
                }

                case "genexus_edit":
                {
                    NormalizeVersionAliases(args);
                    // Issue #205/#206: `scope` / `indentation` are honored only by the
                    // abbreviated mode=patch form. Evaluated before ANY routing decision
                    // (changeSet / targets / parts / ops / JSON-Patch) so the protection can
                    // never be silently dropped while the call is normalized to context/content.
                    RejectProtectedOptionsUnlessAbbreviatedPatch(args);

                    if (args?["changeSet"] is JObject)
                    {
                        return new {
                            module = "Mutation",
                            action = "ChangeSet",
                            target = target,
                            @params = args
                        };
                    }

                    if (args?["changes"] != null)
                        throw new UsageException("usage_error", "argument 'changes' removed in v2.0.0; use 'targets' instead");

                    var targetsTokEdit = args?["targets"];
                    bool hasTargetsEdit = targetsTokEdit is JArray;
                    bool hasNameEdit = !string.IsNullOrEmpty(target);
                    if (hasNameEdit && hasTargetsEdit)
                        throw new UsageException("usage_error", "name and targets are mutually exclusive");
                    if (hasTargetsEdit)
                    {
                        return new {
                            module = "Batch",
                            action = "MultiEdit",
                            items = (JArray)targetsTokEdit!,
                            dryRun = args?["dryRun"]?.ToObject<bool?>() ?? false,
                            rollbackOnFailure = args?["rollbackOnFailure"]?.ToObject<bool?>() ?? true
                        };
                    }

                    if (hasNameEdit && args?["parts"] is JArray partsEditArr && partsEditArr.Count > 0)
                    {
                        return new {
                            module = "Batch",
                            action = "BatchEdit",
                            target = target,
                            changes = partsEditArr,
                            dryRun = args?["dryRun"]?.ToObject<bool?>() ?? false
                        };
                    }

                    string? mode = args?["mode"]?.ToString();
                    ValidateEditMode(mode);

                    // issue #43 #1 (data-loss): `operation` (Replace/Insert_After/Append) is a
                    // mode=patch parameter. Before this guard, passing it WITHOUT mode=patch fell
                    // through to the else-branch below, which runs a FULL-PART replace using
                    // `content` and silently discards `operation` — so Append/Insert_After
                    // overwrote the entire part with just the payload (~888 lines → payload).
                    // Any explicit operation now implies patch semantics; combining it with an
                    // incompatible mode is a hard usage error instead of a silent destructive write.
                    string? editOperation = args?["operation"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(editOperation)
                        && !string.Equals(mode, "patch", StringComparison.OrdinalIgnoreCase))
                    {
                        if (string.Equals(mode, "full", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(mode, "ops", StringComparison.OrdinalIgnoreCase))
                            throw new UsageException(
                                "usage_error",
                                $"operation='{editOperation}' is a mode=patch parameter and cannot be combined with mode={mode}. "
                                + "Omit mode (or set mode=patch) for Replace/Insert_After/Append; use mode=full only to replace the entire part with `content`.");
                        // mode was null/empty → reinterpret as patch so operation is honored.
                        mode = "patch";
                    }

                    // Same data-loss shape as `operation` above: a `patch` payload without mode
                    // fell through to the full-write branch, which only reads `content` — so
                    // {patch:{find,replace}} wrote the whole part EMPTY and reported success.
                    // A supplied patch implies patch semantics; combining it with a full-write
                    // mode is contradictory and rejected rather than silently discarding the patch.
                    JToken? patchPayload = args?["patch"];
                    bool hasPatchPayload = patchPayload != null && patchPayload.Type != JTokenType.Null;
                    if (hasPatchPayload && !string.Equals(mode, "patch", StringComparison.OrdinalIgnoreCase))
                    {
                        if (string.Equals(mode, "full", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(mode, "ops", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(mode, "xml", StringComparison.OrdinalIgnoreCase))
                            throw new UsageException(
                                "usage_error",
                                $"`patch` is a mode=patch parameter and cannot be combined with mode={mode}. "
                                + "Omit mode (or set mode=patch) to apply it; use mode=full only to replace the entire part with `content`.");
                        mode = "patch";
                    }

                    bool returnPostState = args?["return_post_state"]?.ToObject<bool?>() ?? true;
                    bool verbose = args?["verbose"]?.ToObject<bool?>() ?? false;
                    // Items 5 + 37 (friction 2026-05-22): forward visualVerify to the
                    // worker so it can shell out to chrome-devtools-axi / playwright
                    // after the edit lands and attach a screenshot + pixel-diff envelope.
                    bool visualVerify = args?["visualVerify"]?.ToObject<bool?>() ?? false;
                    if (mode == "ops")
                    {
                        ValidateSemanticOps(args?["ops"]);
                        return new {
                            module = "SemanticOps",
                            action = "Apply",
                            target = target,
                            part = part,
                            ops = args?["ops"],
                            // issue #34: forward `type` so the ops write path can disambiguate a
                            // homonym Transaction/Table on the persist path.
                            type = args?["type"]?.ToString(),
                            dryRun = args?["dryRun"]?.ToObject<bool?>() ?? false,
                            return_post_state = returnPostState,
                            verbose = verbose,
                            visualVerify = visualVerify,
                            // issue #60 — save+specify: run the inline Specify pass after the
                            // write when validationMode="specify" (rollback on spec errors).
                            validationMode = args?["validationMode"]?.ToString(),
                            rollbackOnFailure = args?["rollbackOnFailure"]?.ToObject<bool?>() ?? true,
                            verifyMode = args?["verifyMode"]?.ToString(),
                            baseVersion = args?["baseVersion"]?.ToString(),
                            transactionModule = args?["module"]?.ToString()
                        };
                    }
                    if (mode == "patch")
                    {
                        var patchTok = args?["patch"];
                        // issue #31.4: some clients serialize the nested `patch` object as a
                        // JSON string (common when the find/replace text contains newlines).
                        // Reparse it so the {find,replace} shorthand resolves to an object
                        // instead of falling through to the bare-string path (which has no
                        // context and fails with "Replace needs the text to find").
                        if (patchTok is JValue pv && pv.Type == JTokenType.String)
                        {
                            var s = pv.ToString().TrimStart();
                            if (s.StartsWith("{") || s.StartsWith("["))
                            {
                                try { patchTok = JToken.Parse(pv.ToString()); } catch { /* leave as string */ }
                            }
                        }
                        if (patchTok is JArray patchArr)
                        {
                            // RFC 6902 JSON-Patch (array payload)
                            return new {
                                module = "JsonPatch",
                                action = "Apply",
                                target = target,
                                part = part,
                                patch = patchArr,
                                // issue #34: forward `type` so the write path can disambiguate a
                                // homonym Transaction/Table. Without it the worker re-resolves by
                                // name only and fails with "Ambiguous object name".
                                type = args?["type"]?.ToString(),
                                dryRun = args?["dryRun"]?.ToObject<bool?>() ?? false,
                                return_post_state = returnPostState,
                                verbose = verbose,
                                visualVerify = visualVerify,
                                // issue #60 — save+specify: run the inline Specify pass after the
                                // write when validationMode="specify" (rollback on spec errors).
                                validationMode = args?["validationMode"]?.ToString(),
                                rollbackOnFailure = args?["rollbackOnFailure"]?.ToObject<bool?>() ?? false
                            };
                        }

                        // FR#16 (friction-report 2026-05-14): accept the {find, replace} JSON form.
                        // The schema advertised it but only the legacy (operation, context, content)
                        // string form was implemented, so callers got
                        // "'context' (old_string) is required for Replace" even with a valid object.
                        // Map find→context and replace→payload to reuse the existing patch pipeline.
                        string? opFromObj = null;
                        string? contextFromObj = null;
                        string? payloadFromObj = null;
                        JObject? patchObject = null;
                        if (patchTok is JObject patchObj)
                        {
                            var find = patchObj["find"]?.ToString();
                            var replace = patchObj["replace"]?.ToString();
                            if (find != null || replace != null)
                            {
                                patchObject = patchObj;
                                contextFromObj = find;
                                payloadFromObj = replace ?? string.Empty;
                                opFromObj = "Replace";
                            }
                        }

                        // Legacy text-patch (string payload) — unchanged path otherwise.
                        return new {
                            module = "Patch",
                            action = "Apply",
                            target = target,
                            part = part,
                            operation = opFromObj ?? args?["operation"]?.ToString() ?? "Replace",
                            // Worker dispatcher reads request["payload"] for the replacement text
                            // (see CommandDispatcher.cs:154 `payload = request["payload"]`).
                            payload = payloadFromObj
                                   ?? (patchTok is JValue ? patchTok.ToString() : null)
                                   ?? args?["content"]?.ToString(),
                            context = contextFromObj ?? args?["context"]?.ToString(),
                            expectedCount = args?["expectedCount"]?.ToObject<int?>() ?? 1,
                            // issue #34: forward `type` so PatchService.ApplyPatch can pass a
                            // typeFilter into WriteService and disambiguate a homonym
                            // Transaction/Table on the write (persist) path — read/dryRun already
                            // resolved it, but the write re-resolved by name only.
                            type = args?["type"]?.ToString(),
                            dryRun = args?["dryRun"]?.ToObject<bool?>() ?? false,
                            verifyRollback = args?["verifyRollback"]?.ToObject<bool?>() ?? false,
                            return_post_state = returnPostState,
                            verbose = verbose,
                            // v2.6.6 FR#13 follow-up: forward `validate` so the worker's
                            // CommandDispatcher.patch.Apply branch can honor validate=only
                            // (mapped to dryRun=true). Without this the gateway silently
                            // stripped the flag and the schema lied to the LLM.
                            validate = args?["validate"]?.ToString(),
                            // Item 9 (friction 2026-05-22): replaceAll=true applies patch to all
                            // occurrences instead of requiring expectedCount to match exactly.
                            replaceAll = args?["replaceAll"]?.ToObject<bool?>() ?? false,
                            visualVerify = visualVerify,
                            // issue #60 — save+specify: run the inline Specify pass after the
                            // write when validationMode="specify" (rollback on spec errors).
                            validationMode = args?["validationMode"]?.ToString(),
                            rollbackOnFailure = args?["rollbackOnFailure"]?.ToObject<bool?>() ?? false,
                            verifyMode = args?["verifyMode"]?.ToString(),
                            baseVersion = args?["baseVersion"]?.ToString(),
                            autoDeclareVariables = args?["autoDeclareVariables"]?.ToObject<bool?>() ?? args?["autoInjectVariables"]?.ToObject<bool?>() ?? false,
                            // Events complete-save contract: keep the flag on the Patch
                            // command so the worker can capture/compare the full object.
                            requireObjectSave = args?["requireObjectSave"]?.ToObject<bool?>() ?? false,
                            // Issues #205/#206: forward the opt-in protections. `patchShorthand`
                            // records that the caller used the abbreviated {find,replace} form,
                            // which is the only form allowed to carry them; without it a
                            // normalized `operation=Replace` is indistinguishable from an
                            // explicit operation and the worker must reject the pair.
                            scope = patchObject?["scope"] ?? args?["scope"],
                            indentation = patchObject?["indentation"] ?? args?["indentation"],
                            patchShorthand = patchObject != null
                        };
                    }
                    else
                    {
                        // A full write with no `content` key would persist an empty part. An explicit
                        // empty string stays allowed (intentional clear); only absent/null is rejected.
                        JToken? fullContent = args?["content"];
                        if (fullContent == null || fullContent.Type == JTokenType.Null)
                            throw new UsageException(
                                "ContentRequired",
                                "A full-part write needs `content` (use content=\"\" to clear the part on purpose). "
                                + "To edit part of the text send mode=patch with patch={find,replace}. No write was attempted.");

                        // issue #60 — forward validationMode/rollbackOnFailure so the worker's
                        // SaveSpecifyOrchestrator can run the inline Specify pass after the
                        // write (see CommandDispatcher.Handle_Write). Also pass `validate`
                        // (strict|best-effort|only) which the schema already advertised.
                        return new {
                            module = "Write",
                            action = part,
                            part = part,
                            mode = "full",
                            target = target,
                            payload = args?["content"]?.ToString(),
                            content = args?["content"]?.ToString(),
                            type = args?["type"]?.ToString(),
                            dryRun = args?["dryRun"]?.ToObject<bool?>() ?? false,
                            visualVerify = visualVerify,
                            validate = args?["validate"]?.ToString(),
                            validationMode = args?["validationMode"]?.ToString(),
                            rollbackOnFailure = args?["rollbackOnFailure"]?.ToObject<bool?>() ?? false,
                            baseVersion = args?["baseVersion"]?.ToString(),
                            expectedVersion = args?["expectedVersion"]?.ToString(),
                            verifyMode = args?["verifyMode"]?.ToString(),
                            requireObjectSave = args?["requireObjectSave"]?.ToObject<bool?>() ?? false,
                            return_post_state = returnPostState,
                            autoDeclareVariables = args?["autoDeclareVariables"]?.ToObject<bool?>() ?? args?["autoInjectVariables"]?.ToObject<bool?>() ?? false
                        };
                    }
                }

                // Aliases legados (escondidos mas funcionais para a Gateway interna se necessário)
                case "genexus_read_source":
                    return new { module = "Read", action = "ExtractSource", target = target, part = part };
                case "genexus_patch":
                    NormalizeVersionAliases(args);
                    return new {
                        module = "Patch",
                        action = "Apply",
                        target = target,
                        part = part,
                        operation = args?["operation"]?.ToString(),
                        content = args?["content"]?.ToString(),
                        context = args?["context"]?.ToString(),
                        expectedCount = args?["expectedCount"]?.ToObject<int?>() ?? 1,
                        dryRun = args?["dryRun"]?.ToObject<bool?>() ?? false,
                        verifyRollback = args?["verifyRollback"]?.ToObject<bool?>() ?? false,
                        verifyMode = args?["verifyMode"]?.ToString(),
                        baseVersion = args?["baseVersion"]?.ToString(),
                        rollbackOnFailure = args?["rollbackOnFailure"]?.ToObject<bool?>() ?? false,
                        requireObjectSave = args?["requireObjectSave"]?.ToObject<bool?>() ?? false
                    };
                case "genexus_write_object":
                    return new { module = "Write", action = part, target = target, payload = args?["code"]?.ToString() };
                case "genexus_get_variables":
                    return new { module = "Read", action = "GetVariables", target = target };
                case "genexus_get_attribute":
                    return new { module = "Read", action = "GetAttribute", target = target };
                case "genexus_get_properties":
                    return new { module = "Property", action = "Get", target = target, control = args?["control"]?.ToString() };

                // export_object + import_object merged into genexus_io umbrella (OperationsRouter).

                case "genexus_edit_and_build":
                    // Items 5 + 37: hoist visualVerify + part to the top-level
                    // of the worker command so the dispatcher's post-edit hook
                    // can read them straight off `args` without unwrapping the
                    // nested orchestrator envelope.
                    return new
                    {
                        module = "EditAndBuild",
                        action = "Orchestrate",
                        target = args?["name"]?.ToString(),
                        part = args?["part"]?.ToString(),
                        args = args,
                        visualVerify = args?["visualVerify"]?.ToObject<bool?>() ?? false
                    };

                default:
                    return null;
            }
        }
    }
}
