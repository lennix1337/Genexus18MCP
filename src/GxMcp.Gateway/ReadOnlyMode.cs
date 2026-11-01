using System;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    /// <summary>
    /// Optional operator switch (<c>GXMCP_READ_ONLY=1</c>): the Gateway runs only calls the
    /// classifier proves read-only, plus dryRun previews of tools whose preview is known not to
    /// persist. Everything else, including tools the classifier does not know, is refused before
    /// it reaches the Worker, so a session meant to only look at a KB cannot write to it even
    /// when its caller believes a call is harmless.
    /// </summary>
    internal sealed class ReadOnlyMode
    {
        internal const string EnvironmentVariable = "GXMCP_READ_ONLY";
        private ReadOnlyMode(bool enabled) { IsEnabled = enabled; }
        internal bool IsEnabled { get; }

        /// <summary>Lets a test switch the mode for its own async flow without changing the
        /// process environment that parallel tests share.</summary>
        internal static readonly AsyncLocal<ReadOnlyMode?> Override = new AsyncLocal<ReadOnlyMode?>();

        internal static ReadOnlyMode FromEnvironment()
            => Override.Value ?? Parse(Environment.GetEnvironmentVariable(EnvironmentVariable));

        internal static ReadOnlyMode Parse(string? raw)
        {
            string value = (raw ?? string.Empty).Trim();
            return new ReadOnlyMode(value == "1"
                || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>True when the call must be refused: read-only mode is on and the call is
        /// neither a proven read nor an effect-free dryRun preview.</summary>
        internal bool Refuses(string? toolName, JObject? args)
            => IsEnabled
               && !OperationClassifier.IsReadOnly(toolName, args)
               && !OperationClassifier.IsEffectFreeDryRunPreview(toolName, args);

        internal static JObject BuildRefusal(string toolName) => new JObject
        {
            ["status"] = "error",
            ["code"] = "WriteBlockedReadOnlyMode",
            ["message"] = $"{toolName} is not a proven read and the Gateway runs with {EnvironmentVariable}=1. Nothing was written. Use a read tool or dryRun, or restart without {EnvironmentVariable}.",
            ["wrote"] = false,
            ["persisted"] = false
        };
    }
}
