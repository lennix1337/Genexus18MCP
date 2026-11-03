using System;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// Friction 2026-05-22 #62: standardize the <c>code</c> field on every
    /// warning/gotcha emit site. Codes are PascalCase prefixed with
    /// <c>Gotcha</c> (write-time generator/runtime trap) or <c>Lint</c>
    /// (style / preflight issue). Each code resolves to a doc URL the
    /// agent can fetch via <c>resources/read</c>:
    ///
    ///   <c>genexus://kb/tool-help/gotchas/&lt;code&gt;</c>
    ///
    /// The doc resource is served by the gateway out of
    /// <see cref="GxMcp.Gateway.ToolHelpCatalog"/> (gotcha section).
    /// </summary>
    public static class GotchaCodes
    {
        public const string DocUrlPrefix = "genexus://kb/tool-help/gotchas/";

        /// <summary>
        /// Build the canonical doc URL for a code. Returns null when the code
        /// is null/empty so callers can safely set <c>warning["docUrl"]</c>
        /// without a guard.
        /// </summary>
        public static string DocUrlFor(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return null;
            return DocUrlPrefix + code.Trim();
        }

        // Lint codes — preflight / style / charset issues.
        public const string LintKbCharsetLossy = "LintKbCharsetLossy";
        public const string LintSpc0150ForEachAttributeWrite = "LintSpc0150ForEachAttributeWrite";

        // Gotcha codes — emitted by LayoutGotchaScanner (declared there but mirrored here
        // so the audit-test enumerator finds them in one place).
        public const string GotchaGxButtonHtmlFormCustomEvent = "GotchaGxButtonHtmlFormCustomEvent";
        public const string GotchaGxAttributeHtmlFormDiscreteReadOnly = "GotchaGxAttributeHtmlFormDiscreteReadOnly";
        public const string GotchaGxAttributeMissingDataField = "GotchaGxAttributeMissingDataField";
        public const string GotchaUnknownControlType = "GotchaUnknownControlType";
        public const string GotchaWebComponentMissingObjectCall = "GotchaWebComponentMissingObjectCall";
        public const string GotchaHtmlFormatScriptStripped = "GotchaHtmlFormatScriptStripped";
        public const string GotchaCellOutsideTable = "GotchaCellOutsideTable";
        public const string GotchaDuplicateControlName = "GotchaDuplicateControlName";
        public const string GotchaGridConditionIgnored = "GotchaGridConditionIgnored";

        // Concurrency codes — emitted by IdeConcurrencyDetector (Issue #128)
        public const string GotchaIdeObjectOpenInEditor = "GotchaIdeObjectOpenInEditor";
        public const string GotchaIdeActiveOnKb = "GotchaIdeActiveOnKb";
    }
}
