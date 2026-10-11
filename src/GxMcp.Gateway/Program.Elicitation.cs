using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    partial class Program
    {
        internal const string HumanDeclinedCode = "HUMAN_DECLINED";
        internal const string HumanCancelledCode = "HUMAN_CANCELLED";
        internal const string HumanConfirmationTimeoutCode = "HUMAN_CONFIRMATION_TIMEOUT";

        /// <summary>
        /// Describes the irreversible effect of a destructive call that a human should approve,
        /// or returns null when the call is a preview or not one of the gated operations.
        /// </summary>
        internal static string? DescribeDestructiveOperation(string? toolName, JObject? args)
        {
            if (string.IsNullOrEmpty(toolName) || args == null) return null;
            string action = args["action"]?.ToString()?.Trim().ToLowerInvariant() ?? string.Empty;
            bool dryRunTrue = args["dryRun"]?.Type == JTokenType.Boolean && args["dryRun"]!.Value<bool>();
            bool dryRunFalse = args["dryRun"]?.Type == JTokenType.Boolean && !args["dryRun"]!.Value<bool>();
            string kbSuffix = string.IsNullOrWhiteSpace(args["kb"]?.ToString()) ? string.Empty : $" in KB '{args["kb"]}'";

            switch (toolName)
            {
                case "genexus_delete_object":
                    if (dryRunTrue) return null;
                    string type = args["type"]?.ToString() ?? string.Empty;
                    string name = args["name"]?.ToString() ?? "(unnamed)";
                    string label = string.IsNullOrWhiteSpace(type) ? $"'{name}'" : $"{type} '{name}'";
                    return $"Delete {label}{kbSuffix}? This is irreversible.";
                case "genexus_data_view":
                    if (action != "delete" || dryRunTrue) return null;
                    return $"Delete Transaction '{args["transaction"]}' and Data View '{args["dataViewName"]}'{kbSuffix}? This is irreversible.";
                case "genexus_transfer":
                    if (action != "import" || !dryRunFalse) return null;
                    return $"Import XPZ '{args["file"]}'{kbSuffix}? Objects in the KB will be created or overwritten.";
                case "genexus_deploy":
                    if (action != "deploy") return null;
                    return $"Build and deploy the application{kbSuffix}?";
                default:
                    return null;
            }
        }

        private static async Task<JObject?> ProcessMcpRequestWithElicitation(
            JObject request,
            string sessionId,
            bool sessionContextEnabled,
            CancellationToken transportCancellation,
            bool taskScopeEnabled)
        {
            if (ElicitationBroker.TryCompleteResponse(request)) return null;

            string? method = request["method"]?.ToString();
            if (string.Equals(method, "initialize", StringComparison.Ordinal))
                ElicitationBroker.ObserveInitialize(request, sessionId);

            bool isToolCall = string.Equals(method, "tools/call", StringComparison.Ordinal);
            if (!isToolCall || !ElicitationBroker.IsAvailable(sessionId))
            {
                return await ProcessMcpRequestCore(
                    request, sessionId, sessionContextEnabled, transportCancellation, taskScopeEnabled)
                    .ConfigureAwait(false);
            }

            var confirmation = await ConfirmDestructiveCallAsync(request, sessionId, transportCancellation).ConfigureAwait(false);
            if (confirmation.Refusal != null) return confirmation.Refusal;
            request = confirmation.Request;

            var response = await ProcessMcpRequestCore(
                request, sessionId, sessionContextEnabled, transportCancellation, taskScopeEnabled)
                .ConfigureAwait(false);

            return await RecoverKbContextWithElicitationAsync(
                request, response, sessionId, sessionContextEnabled, transportCancellation, taskScopeEnabled)
                .ConfigureAwait(false) ?? response;
        }

        internal static async Task<(JObject Request, JObject? Refusal)> ConfirmDestructiveCallAsync(
            JObject request, string sessionId, CancellationToken cancellationToken)
        {
            var parameters = request["params"] as JObject;
            string? toolName = parameters?["name"]?.ToString();
            var args = parameters?["arguments"] as JObject;
            string? effect = DescribeDestructiveOperation(toolName, args);
            if (effect == null) return (request, null);

            bool agentConfirmed = args?["confirm"]?.Type == JTokenType.Boolean && args["confirm"]!.Value<bool>();
            if (agentConfirmed && ElicitationBroker.Mode != ElicitationMode.Strict) return (request, null);

            var schema = new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["confirm"] = new JObject
                    {
                        ["type"] = "boolean",
                        ["title"] = "Approve this operation",
                        ["description"] = effect,
                        ["default"] = false
                    }
                },
                ["required"] = new JArray("confirm")
            };
            string message = $"GeneXus MCP: the agent requested {toolName}. {effect}";
            var outcome = await ElicitationBroker.RequestAsync(sessionId, message, schema, cancellationToken).ConfigureAwait(false);
            LogElicitation("confirm", toolName, outcome);

            switch (outcome.Action)
            {
                case ElicitationAction.Accept when outcome.Content["confirm"]?.Type == JTokenType.Boolean
                                                   && outcome.Content["confirm"]!.Value<bool>():
                    var approved = (JObject)request.DeepClone();
                    var approvedArgs = approved["params"]!["arguments"] as JObject;
                    if (approvedArgs == null)
                    {
                        approvedArgs = new JObject();
                        approved["params"]!["arguments"] = approvedArgs;
                    }
                    approvedArgs["confirm"] = true;
                    return (approved, null);
                case ElicitationAction.Unavailable:
                case ElicitationAction.Failed:
                    // The client could not ask: keep today's contract (the Worker's own
                    // confirm=true gate still protects the KB).
                    return (request, null);
                case ElicitationAction.Timeout:
                    return (request, BuildHumanRefusal(request, toolName, args, HumanConfirmationTimeoutCode,
                        $"No human answer to the confirmation for {toolName} within {ElicitationBroker.Timeout.TotalSeconds:0}s; nothing was changed.",
                        "Ask the user in chat whether to proceed before retrying."));
                case ElicitationAction.Cancel:
                    return (request, BuildHumanRefusal(request, toolName, args, HumanCancelledCode,
                        $"The user dismissed the confirmation for {toolName}; nothing was changed.",
                        "Do not retry automatically. Ask the user how to proceed."));
                default:
                    return (request, BuildHumanRefusal(request, toolName, args, HumanDeclinedCode,
                        $"The user declined {toolName}; nothing was changed.",
                        "Do not retry this operation (with or without confirm=true) unless the user explicitly asks again."));
            }
        }

        private static JObject BuildHumanRefusal(JObject request, string? toolName, JObject? args, string code, string message, string hint)
        {
            _currentKb.Value = null;
            var payload = new JObject
            {
                ["status"] = "error",
                ["error"] = new JObject
                {
                    ["code"] = code,
                    ["message"] = message,
                    ["hint"] = hint
                },
                ["humanInTheLoop"] = new JObject { ["channel"] = "elicitation", ["outcome"] = code }
            };
            return BuildToolTextResponse(request["id"], payload, isError: true, toolName: toolName, toolArgs: args, payloadOwned: true);
        }

        /// <summary>
        /// When a tool call failed only because no KB is selected for the session, asks the
        /// human to pick one, selects it for the session through <c>genexus_kb action=select</c>
        /// and replays the original call once. Returns null when no recovery was attempted.
        /// </summary>
        private static async Task<JObject?> RecoverKbContextWithElicitationAsync(
            JObject request,
            JObject? response,
            string sessionId,
            bool sessionContextEnabled,
            CancellationToken cancellationToken,
            bool taskScopeEnabled)
        {
            if (response == null || !sessionContextEnabled) return null;
            var parameters = request["params"] as JObject;
            string? toolName = parameters?["name"]?.ToString();
            var args = parameters?["arguments"] as JObject;
            if (string.IsNullOrEmpty(toolName) || IsMetaTool(toolName!)) return null;
            if (!string.IsNullOrWhiteSpace(args?["kb"]?.ToString())) return null;

            string? code = ExtractErrorCode(response);
            if (!string.Equals(code, "KB_AMBIGUOUS", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(code, "KB_CONTEXT_REQUIRED", StringComparison.OrdinalIgnoreCase))
                return null;

            var candidates = ListKbCandidates();
            if (candidates.Count == 0) return null;

            var schema = new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["kb"] = new JObject
                    {
                        ["type"] = "string",
                        ["title"] = "Knowledge Base",
                        ["description"] = "Selected for this MCP session only; the configuration file is not changed.",
                        ["enum"] = new JArray(candidates.Select(c => c.Alias)),
                        ["enumNames"] = new JArray(candidates.Select(c => c.Label))
                    }
                },
                ["required"] = new JArray("kb")
            };
            string message = $"GeneXus MCP: {toolName} needs a Knowledge Base and this session has none selected. Which KB should it use?";
            var outcome = await ElicitationBroker.RequestAsync(sessionId, message, schema, cancellationToken).ConfigureAwait(false);
            LogElicitation("kb", toolName, outcome);
            if (outcome.Action != ElicitationAction.Accept) return null;

            string? alias = outcome.Content["kb"]?.ToString();
            if (string.IsNullOrWhiteSpace(alias)
                || !candidates.Any(c => string.Equals(c.Alias, alias, StringComparison.OrdinalIgnoreCase)))
                return null;

            var select = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = "gxmcp-elicit-select",
                ["method"] = "tools/call",
                ["params"] = new JObject
                {
                    ["name"] = "genexus_kb",
                    ["arguments"] = new JObject { ["action"] = "select", ["alias"] = alias }
                }
            };
            var selectResponse = await ProcessMcpRequestCore(
                select, sessionId, sessionContextEnabled, cancellationToken, taskScopeEnabled).ConfigureAwait(false);
            if (selectResponse == null || ExtractErrorCode(selectResponse) != null
                || selectResponse["result"]?["isError"]?.Value<bool>() == true)
                return null;

            var replay = await ProcessMcpRequestCore(
                request, sessionId, sessionContextEnabled, cancellationToken, taskScopeEnabled).ConfigureAwait(false);
            if (replay?["result"] is JObject result)
            {
                var meta = result["_meta"] as JObject ?? new JObject();
                meta["gxmcp/humanSelectedKb"] = alias;
                result["_meta"] = meta;
            }
            return replay;
        }

        private sealed class KbCandidate
        {
            internal KbCandidate(string alias, string label)
            {
                Alias = alias;
                Label = label;
            }

            internal string Alias { get; }
            internal string Label { get; }
        }

        private static List<KbCandidate> ListKbCandidates()
        {
            var candidates = new List<KbCandidate>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var open in _workerPool?.ListOpen() ?? (IReadOnlyList<KbHandle>)Array.Empty<KbHandle>())
            {
                if (string.IsNullOrWhiteSpace(open.Alias) || !seen.Add(open.Alias)) continue;
                candidates.Add(new KbCandidate(open.Alias, $"{open.Alias} (open)"));
            }
            foreach (var declared in _activeConfig?.Environment?.KBs ?? new List<KbEntry>())
            {
                if (string.IsNullOrWhiteSpace(declared.Alias) || !seen.Add(declared.Alias)) continue;
                candidates.Add(new KbCandidate(declared.Alias, declared.Alias));
            }
            return candidates;
        }

        internal static string? ExtractErrorCode(JObject response)
        {
            if (response["error"] is JObject rpcError)
                return rpcError["data"]?["code"]?.ToString() ?? rpcError["code"]?.ToString();

            if (response["result"] is not JObject result || result["isError"]?.Value<bool>() != true) return null;
            JObject? payload = result["structuredContent"] as JObject;
            if (payload == null && result["content"] is JArray content && content.Count > 0)
            {
                try { payload = JObject.Parse(content[0]?["text"]?.ToString() ?? string.Empty); }
                catch (Newtonsoft.Json.JsonException) { payload = null; }
            }
            return payload?["error"]?["code"]?.ToString() ?? payload?["code"]?.ToString();
        }

        private static void LogElicitation(string kind, string? toolName, ElicitationOutcome outcome)
        {
            Log($"[Elicitation] {kind} for {toolName}: {outcome.Action}"
                + (string.IsNullOrEmpty(outcome.Detail) ? string.Empty : " (" + outcome.Detail + ")"));
        }
    }
}
