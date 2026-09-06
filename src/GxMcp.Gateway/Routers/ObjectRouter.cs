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

        public object? ConvertToolCall(string toolName, JObject? args)
        {
            string? nameArg = args?["name"]?.ToString();
            string? target = nameArg ?? args?["path"]?.ToString() ?? args?["entityKey"]?.ToString() ?? args?["guid"]?.ToString();
            string part = args?["part"]?.ToString() ?? "Source";

            switch (toolName)
            {
                case "genexus_read":
                {
                    var targetsTokRead = args?["targets"];
                    bool hasTargetsRead = targetsTokRead is JArray;
                    bool hasNameRead = !string.IsNullOrEmpty(nameArg) || !string.IsNullOrEmpty(args?["path"]?.ToString())
                        || !string.IsNullOrEmpty(args?["entityKey"]?.ToString()) || !string.IsNullOrEmpty(args?["guid"]?.ToString());
                    if (hasNameRead && hasTargetsRead)
                        throw new UsageException("usage_error", "name and targets are mutually exclusive");
                    if (hasTargetsRead)
                    {
                        return new {
                            module = "Batch",
                            action = "BatchRead",
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
                        return new {
                            module = "Read",
                            action = "ExtractParts",
                            target = target,
                            parts = (JArray)partsTok!,
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
                        return new {
                            module = "Read",
                            action = "ExtractFullObject",
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
                        target = target,
                        part = part,
                        offset = args?["offset"]?.ToObject<int?>(),
                        limit = args?["limit"]?.ToObject<int?>(),
                        type = args?["type"]?.ToString(),
                        guid = args?["guid"]?.ToString(),
                        entityKey = args?["entityKey"]?.ToString(),
                        path = args?["path"]?.ToString()
                    };
                }

                case "genexus_edit":
                {
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
                        string opFromObj = null;
                        string contextFromObj = null;
                        string payloadFromObj = null;
                        if (patchTok is JObject patchObj)
                        {
                            var find = patchObj["find"]?.ToString();
                            var replace = patchObj["replace"]?.ToString();
                            if (find != null || replace != null)
                            {
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
                            autoDeclareVariables = args?["autoDeclareVariables"]?.ToObject<bool?>() ?? args?["autoInjectVariables"]?.ToObject<bool?>() ?? false
                        };
                    }
                    else
                    {
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
                            autoDeclareVariables = args?["autoDeclareVariables"]?.ToObject<bool?>() ?? args?["autoInjectVariables"]?.ToObject<bool?>() ?? false
                        };
                    }
                }

                // Aliases legados (escondidos mas funcionais para a Gateway interna se necessário)
                case "genexus_read_source":
                    return new { module = "Read", action = "ExtractSource", target = target, part = part };
                case "genexus_patch":
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
                        rollbackOnFailure = args?["rollbackOnFailure"]?.ToObject<bool?>() ?? false
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
