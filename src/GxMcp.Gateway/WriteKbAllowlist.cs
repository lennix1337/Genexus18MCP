using System;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    /// <summary>
    /// Optional operator fence (<c>GXMCP_WRITE_KB_ALLOW</c>, ';'-separated globs over the whole KB path):
    /// a mutation on a KB that matches no pattern is refused before it reaches the worker.
    /// Unset means no restriction. Previews (dryRun / validate) are not mutations and pass.
    /// </summary>
    internal sealed class WriteKbAllowlist
    {
        internal const string EnvironmentVariable = "GXMCP_WRITE_KB_ALLOW";
        private readonly Regex[] _patterns;
        private WriteKbAllowlist(string? raw, Regex[] patterns) { Raw = raw ?? string.Empty; _patterns = patterns; }
        internal string Raw { get; }
        internal bool IsConfigured => _patterns.Length > 0;

        internal static WriteKbAllowlist FromEnvironment() => Parse(Environment.GetEnvironmentVariable(EnvironmentVariable));

        internal static WriteKbAllowlist Parse(string? raw)
        {
            var patterns = (raw ?? string.Empty)
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(glob => new Regex(
                    "^" + Regex.Escape(Normalize(glob)).Replace("\\*", ".*").Replace("\\?", ".") + "$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                .ToArray();
            return new WriteKbAllowlist(raw, patterns);
        }

        internal bool Allows(string? kbPath)
            => !IsConfigured || (!string.IsNullOrWhiteSpace(kbPath) && _patterns.Any(pattern => pattern.IsMatch(Normalize(kbPath!))));

        private static string Normalize(string path) => path.Trim().Replace('/', '\\').TrimEnd('\\');

        internal static JObject BuildRefusal(string toolName, string kbPath, string allowed) => new JObject
        {
            ["status"] = "error",
            ["code"] = "WriteBlockedKbNotAllowed",
            ["message"] = $"{toolName} would write to KB '{kbPath}', which matches none of {EnvironmentVariable}='{allowed}'. Nothing was written. Use dryRun, or open an allowed KB.",
            ["wrote"] = false,
            ["persisted"] = false
        };
    }
}
