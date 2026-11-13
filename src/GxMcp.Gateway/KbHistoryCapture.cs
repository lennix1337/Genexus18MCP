using System;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    internal enum KbHistoryJobKind { Write, Delete }

    /// <summary>
    /// Everything the background committer needs. Plain values only: the request that
    /// produced it has already returned by the time the job runs.
    /// </summary>
    internal sealed class KbHistoryJob
    {
        public required KbHistoryJobKind Kind { get; init; }
        public required string KbPath { get; init; }
        public string? KbAlias { get; init; }
        public required string ObjectType { get; init; }
        public required string ObjectName { get; init; }
        /// <summary>Part file name stem; null for a deletion, which removes the whole object folder.</summary>
        public string? Part { get; init; }
        /// <summary>Persisted part text exactly as the Worker re-read it after the save.</summary>
        public string? Content { get; init; }
        /// <summary>SHA-256 the Worker reported for <see cref="Content"/>; checked off the request path.</summary>
        public string? ExpectedHash { get; init; }
        public required string ToolName { get; init; }
        public required string Action { get; init; }
        public string? SessionId { get; init; }
        public string? OperationId { get; init; }
        public string? ClientRequestId { get; init; }
    }

    /// <summary>
    /// Decides, from data the Gateway already holds, whether a finished tool call is a confirmed
    /// persisted write worth a history commit. Runs on the request path, so it only reads fields of
    /// the Worker envelope: no hashing, no serialization, no I/O. The Worker's write receipt already
    /// carries the re-read part text (<c>source</c>), so history needs no extra Worker read.
    /// </summary>
    internal static class KbHistoryCapture
    {
        internal const string EditTool = "genexus_edit";
        internal const string DeleteTool = "genexus_delete_object";
        private const string FallbackType = "Object";

        private static readonly Regex ActionToken = new Regex("^[A-Za-z0-9_-]{1,32}$", RegexOptions.CultureInvariant);

        internal static bool IsHistoryTool(string? toolName)
            => string.Equals(toolName, EditTool, StringComparison.OrdinalIgnoreCase)
               || string.Equals(toolName, DeleteTool, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Returns the job for a confirmed write or deletion, or null (never throws on malformed
        /// payloads). Dry runs, previews, errors and anything whose persistence the Worker did not
        /// confirm by a fresh re-read yield null.
        /// </summary>
        internal static KbHistoryJob? TryCreate(
            string toolName, JObject? args, JToken? workerPayload,
            string? kbPath, string? kbAlias, string? sessionId)
        {
            if (!IsHistoryTool(toolName) || args == null || string.IsNullOrWhiteSpace(kbPath)
                || workerPayload is not JObject payload || payload["error"] != null)
                return null;
            if (IsTrue(args["dryRun"]) || !Program.IsMutatingTool(toolName, args)
                || OperationClassifier.IsEffectFreeDryRunPreview(toolName, args))
                return null;

            JObject? inner = payload["result"] as JObject;
            JToken? Pick(string field) => payload[field] ?? inner?[field];

            string status = Pick("status")?.ToString() ?? string.Empty;
            if (status.Equals("error", StringComparison.OrdinalIgnoreCase)) return null;
            if (IsTrue(Pick("dryRun"))) return null;

            return string.Equals(toolName, DeleteTool, StringComparison.OrdinalIgnoreCase)
                ? CreateDelete(toolName, args, payload, inner, Pick, kbPath!, kbAlias, sessionId)
                : CreateWrite(toolName, args, payload, Pick, kbPath!, kbAlias, sessionId);
        }

        private static KbHistoryJob? CreateWrite(
            string toolName, JObject args, JObject payload, Func<string, JToken?> pick,
            string kbPath, string? kbAlias, string? sessionId)
        {
            // Multi-target and multi-part receipts do not carry one text per part; v1 records
            // the single-part edit routes only.
            if (args["parts"] != null || args["targets"] != null || args["changeSet"] != null || IsTrue(args["async"]))
                return null;

            string code = pick("code")?.ToString() ?? string.Empty;
            if (code.Equals("WriteDryRun", StringComparison.OrdinalIgnoreCase)
                || code.Equals("WriteNoChange", StringComparison.OrdinalIgnoreCase)
                || pick("changed") is { Type: JTokenType.Boolean } changed && !changed.Value<bool>())
                return null;

            // The receipt is only trusted when the Worker itself says a fresh read-back matched.
            if (!string.Equals(pick("persistedState")?.ToString(), "confirmed", StringComparison.OrdinalIgnoreCase)
                || !IsTrue(pick("persisted"))
                || !IsTrue(pick("postSaveVerification")?["reReadConfirmed"]))
                return null;

            if (pick("source") is not { Type: JTokenType.String } source) return null;

            string? name = FirstText(args["name"], args["target"], pick("target"));
            string? type = FirstText(args["type"], pick("resolvedType"), pick("objectType")) ?? FallbackType;
            string part = FirstText(pick("part"), args["part"]) ?? "Source";
            if (!IsSafeSegment(name) || !IsSafeSegment(type) || !IsSafeSegment(part)) return null;

            return new KbHistoryJob
            {
                Kind = KbHistoryJobKind.Write,
                KbPath = kbPath,
                KbAlias = kbAlias,
                ObjectType = type!,
                ObjectName = name!,
                Part = part,
                Content = source.Value<string>() ?? string.Empty,
                ExpectedHash = pick("persistedHash")?.ToString(),
                ToolName = toolName,
                Action = SafeAction(FirstText(args["mode"], args["action"])) ?? "write",
                SessionId = sessionId,
                OperationId = FirstText(pick("operationId"), pick("correlationId")),
                ClientRequestId = FirstText(args["clientRequestId"], args["idempotencyKey"])
            };
        }

        private static KbHistoryJob? CreateDelete(
            string toolName, JObject args, JObject payload, JObject? inner, Func<string, JToken?> pick,
            string kbPath, string? kbAlias, string? sessionId)
        {
            if (!IsTrue(args["confirm"])) return null;
            if (!string.Equals(pick("code")?.ToString(), "ObjectDeleted", StringComparison.OrdinalIgnoreCase)
                || !IsTrue(inner?["persisted"]) || !IsTrue(inner?["rereadConfirmed"]))
                return null;

            string? name = FirstText(inner?["deleted"], args["name"]);
            string? type = FirstText(inner?["resolvedType"], inner?["type"], args["type"]);
            if (!IsSafeSegment(name) || !IsSafeSegment(type)) return null;

            return new KbHistoryJob
            {
                Kind = KbHistoryJobKind.Delete,
                KbPath = kbPath,
                KbAlias = kbAlias,
                ObjectType = type!,
                ObjectName = name!,
                ToolName = toolName,
                Action = "delete",
                SessionId = sessionId,
                OperationId = FirstText(pick("operationId"), pick("correlationId")),
                ClientRequestId = FirstText(args["clientRequestId"], args["idempotencyKey"])
            };
        }

        private static bool IsTrue(JToken? token) => token is { Type: JTokenType.Boolean } && token.Value<bool>();

        private static bool IsSafeSegment(string? value) => value != null && SafePathSegment.IsSafe(value);

        private static string? SafeAction(string? value)
            => value != null && ActionToken.IsMatch(value) ? value.ToLowerInvariant() : null;

        private static string? FirstText(params JToken?[] tokens)
        {
            foreach (var token in tokens)
            {
                if (token is { Type: JTokenType.String })
                {
                    string text = token.Value<string>() ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(text)) return text.Trim();
                }
            }
            return null;
        }
    }
}
