using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Helpers;
using Artech.Architecture.Common.Objects;
using Artech.Genexus.Common.Objects;
using Artech.Genexus.Common.Parts;

namespace GxMcp.Worker.Services
{
    public class LinterService
    {
        private readonly ObjectService _objectService;
        private readonly NavigationService _navigationService;
        private WriteService _writeService;

        public LinterService(ObjectService objectService, NavigationService navigationService)
        {
            _objectService = objectService;
            _navigationService = navigationService;
        }

        public void SetWriteService(WriteService ws) => _writeService = ws;

        /// Run Lint, then auto-fix the safe issues (GX008 unused vars that aren't framework-managed)
        /// via the existing DeleteVariable path. Returns the lint report plus a fixed[] array.
        public string LintAndFix(string target)
        {
            var raw = Lint(target);
            JObject report;
            try { report = JObject.Parse(raw); } catch { return raw; }
            var issues = report["issues"] as JArray;
            if (issues == null || _writeService == null) return raw;

            var toRemove = new List<string>();
            var skipped = new JArray();
            foreach (var issue in issues)
            {
                string code = issue["code"]?.ToString();
                string symbol = ResolveFixSymbol(issue);
                if (code == "GX008" && !string.IsNullOrEmpty(symbol) && symbol.StartsWith("&"))
                    toRemove.Add(symbol.TrimStart('&'));
                else
                    skipped.Add(new JObject { ["code"] = code, ["symbol"] = symbol, ["reason"] = "no auto-fix for this rule" });
            }
            // Single open-save-flush for all unused vars instead of N round-trips.
            var batchResult = _writeService.DeleteVariables(target, toRemove);
            report["fixed"] = JsonUtil.SafeParse(batchResult);
            report["skipped"] = skipped;
            return report.ToString();
        }

        internal static string ResolveFixSymbol(JToken issue)
        {
            if (issue == null || !string.Equals(issue["code"]?.ToString(), "GX008", StringComparison.OrdinalIgnoreCase))
                return null;

            // Older linter reports expose the variable as the issue snippet;
            // newer reports may provide the explicit symbol field.
            return issue["symbol"]?.ToString() ?? issue["snippet"]?.ToString();
        }

        public string Lint(string target, string specificPart = null)
        {
            try
            {
                KBObject obj = _objectService.FindObject(target);
                if (obj == null) return Models.McpResponse.Err(code: "ObjectNotFound", message: "Object not found.", hint: "Verify the object name with genexus_query.", nextSteps: new JArray(Models.McpResponse.NextStep("genexus_query", new JObject { ["name"] = target }, "Search for the object by name.")), target: target);

                var issues = new JArray();
                var parts = obj.Parts.Cast<KBObjectPart>().ToList();
                // WWP+ patterns prescribe direct table access in Event Start; suppress GX012 when present.
                bool hasPatternInstance = parts.Any(p => string.Equals(p.TypeDescriptor?.Name, "PatternInstance", StringComparison.OrdinalIgnoreCase));

                foreach (var part in parts)
                {
                    string rawPartName = GetPartName(part);
                    string uiPartName = rawPartName;
                    if (rawPartName == "Procedure") uiPartName = "Source";
                    
                    if (part is ISource sourcePart)
                    {
                        string content = sourcePart.Source ?? "";
                        if (string.IsNullOrEmpty(content)) continue;

                        string cleanContent = StripComments(content);

                        if (part is RulesPart)
                        {
                            CheckRulesPart(cleanContent, content, issues, "Rules");
                            if (obj is Procedure || obj is WebPanel)
                                CheckParmRule(cleanContent, obj.Name, issues, "Rules");
                        }
                        else if (rawPartName == "Events" || rawPartName == "Procedure" || rawPartName == "Source")
                        {
                            if (!string.IsNullOrEmpty(specificPart) && !specificPart.Equals(uiPartName, StringComparison.OrdinalIgnoreCase))
                                continue;

                            CheckLogicPart(cleanContent, content, issues, uiPartName);
                            if (!hasPatternInstance)
                                CheckDirectTableAccess(cleanContent, issues, content, uiPartName);
                            if (rawPartName == "Procedure" || rawPartName == "Source")
                                CheckSubroutines(cleanContent, issues, content, uiPartName);
                        }
                    }
                    else if (part is VariablesPart varPart)
                    {
                        if (string.IsNullOrEmpty(specificPart) || specificPart == "Variables")
                            CheckVariableUsageInObject(obj, issues, "Variables");
                    }
                }

                // FR#15 + FR#20 + FR#21 (friction-report 2026-05-14): cross-part checks that
                // the existing single-part walkers can't see. Pure read-only inspections.
                CheckOutParmEnabled(obj, issues);
                CheckGxButtonEvents(obj, issues);
                CheckLayoutNonPrefixedElements(obj, issues);

                // Integration with Navigation Intelligence
                CheckNavigationPerformance(target, issues);

                var sdkIssues = SdkDiagnosticsHelper.GetDiagnostics(obj);
                foreach (var issue in sdkIssues) issues.Add(issue);

                var result = new JObject();
                result["target"] = target;
                result["issueCount"] = issues.Count;
                result["issues"] = issues;

                return result.ToString();
            }
            catch (Exception ex)
            {
                return Models.McpResponse.Err(
                    code: "LintFailed",
                    message: ex.Message,
                    target: target);
            }
        }

        private void CheckNavigationPerformance(string target, JArray issues)
        {
            try
            {
                string navJson = _navigationService.GetNavigation(target);
                if (navJson.StartsWith("{") && !navJson.Contains("error"))
                {
                    var nav = JObject.Parse(navJson);
                    var levels = nav["levels"] as JArray;
                    if (levels != null)
                    {
                        foreach (var level in levels)
                        {
                            bool isOptimized = level["isOptimized"]?.Value<bool>() ?? false;
                            string index = level["index"]?.ToString();
                            string type = level["type"]?.ToString();

                            if (type == "For Each" && string.IsNullOrEmpty(index) && !isOptimized)
                            {
                                int line = level["line"]?.Value<int>() ?? 0;
                                string table = level["baseTable"]?.ToString() ?? "unknown";
                                issues.Add(CreateIssue("GX013", "Confirmed Full Scan", "Error", $"Navigation confirms a FULL SCAN on table '{table}'. No index used and no optimizations found.", "For Each", line, "Navigation"));
                            }
                        }
                    }
                }
            }
            catch { }
        }

        private string GetPartName(KBObjectPart part)
        {
            if (part is RulesPart) return "Rules";
            if (part is VariablesPart) return "Variables";
            return part.TypeDescriptor.Name;
        }

        private void CheckLogicPart(string cleanCode, string originalCode, JArray issues, string partName)
        {
            CheckCommitInsideLoop(cleanCode, issues, originalCode, partName);
            CheckUnfilteredLoop(cleanCode, issues, originalCode, partName);
            CheckNestedForEach(cleanCode, issues, originalCode, partName);
            CheckMissingWhenNone(cleanCode, issues, originalCode, partName);
            CheckSleepWait(cleanCode, issues, originalCode, partName);
            CheckDynamicCall(cleanCode, issues, originalCode, partName);
            CheckNewWhenDuplicate(cleanCode, issues, originalCode, partName);
        }

        private void CheckDirectTableAccess(string cleanCode, JArray issues, string originalCode, string partName)
        {
            // Only relevant for UI objects like WebPanels where direct access is discouraged in some architectures
            if (partName.Equals("Events", StringComparison.OrdinalIgnoreCase))
            {
                var forEachBlocks = Regex.Matches(cleanCode, @"(?is)\bfor\s+each\b\s*.*?\s*\bendfor\b", RegexOptions.Compiled);
                foreach (Match m in forEachBlocks)
                {
                    int line = GetLineNumber(originalCode, m.Index);
                    issues.Add(CreateIssue("GX012", "Direct Table Access in UI", "Info", "Direct 'For Each' in UI events detected. Consider using Data Providers for better separation of concerns.", "For Each", line, partName));
                }
            }
        }

        private void CheckNestedForEach(string cleanCode, JArray issues, string originalCode, string partName)
        {
            var nestedMatch = Regex.Matches(cleanCode, @"(?is)\bfor\s+each\b\s*.*?\bfor\s+each\b\s*.*?\bendfor\b\s*.*?\bendfor\b", RegexOptions.Compiled);
            foreach (Match m in nestedMatch)
            {
                int line = GetLineNumber(originalCode, m.Index);
                issues.Add(CreateIssue("GX010", "Potential N+1 Query", "Warning", "Nested For Each detected. Consider using a single Join.", "Nested For Each", line, partName));
            }
        }

        private void CheckMissingWhenNone(string cleanCode, JArray issues, string originalCode, string partName)
        {
            var forEachBlocks = Regex.Matches(cleanCode, @"(?is)\bfor\s+each\b\s*.*?\bendfor\b", RegexOptions.Compiled);
            foreach (Match m in forEachBlocks)
            {
                if (!Regex.IsMatch(m.Value, @"(?i)\bwhen\s+none\b", RegexOptions.Compiled))
                {
                    if (m.Value.Length > 200) {
                        int line = GetLineNumber(originalCode, m.Index);
                        issues.Add(CreateIssue("GX011", "Missing When None", "Info", "Consider adding a 'when none' clause.", "For Each", line, partName));
                    }
                }
            }
        }

        private void CheckRulesPart(string cleanCode, string originalCode, JArray issues, string partName)
        {
        }

        private void CheckCommitInsideLoop(string cleanCode, JArray issues, string originalCode, string partName)
        {
            var forEachBlocks = Regex.Matches(cleanCode, @"(?is)\bfor\s+each\b\s*.*?\s*\bendfor\b", RegexOptions.Compiled);
            foreach (Match m in forEachBlocks)
            {
                if (Regex.IsMatch(m.Value, @"(?i)\bcommit\b", RegexOptions.Compiled))
                {
                    int line = GetLineNumber(originalCode, m.Index);
                    issues.Add(CreateIssue("GX001", "Commit inside loop", "Critical", "Avoid Commit inside For Each.", "Commit", line, partName));
                }
            }
        }

        private void CheckUnfilteredLoop(string cleanCode, JArray issues, string originalCode, string partName)
        {
            var forEachBlocks = Regex.Matches(cleanCode, @"(?is)\bfor\s+each\b\s*.*?\s*\bendfor\b", RegexOptions.Compiled);
            foreach (Match m in forEachBlocks)
            {
                if (!Regex.IsMatch(m.Value, @"(?i)\bwhere\b|\bdefined\s+by\b", RegexOptions.Compiled))
                {
                    int line = GetLineNumber(originalCode, m.Index);
                    issues.Add(CreateIssue("GX002", "Unfiltered loop", "Critical", "Full table scan detected.", "For Each", line, partName));
                }
            }
        }

        private void CheckSleepWait(string cleanCode, JArray issues, string originalCode, string partName)
        {
            var matches = Regex.Matches(cleanCode, @"(?i)\b(?:sleep|wait)\s*\(\s*\d+\s*\)", RegexOptions.Compiled);
            foreach (Match m in matches)
            {
                int line = GetLineNumber(originalCode, m.Index);
                issues.Add(CreateIssue("GX003", "Blocking call", "Warning", "Sleep/Wait calls block server threads.", m.Value, line, partName));
            }
        }

        private void CheckDynamicCall(string cleanCode, JArray issues, string originalCode, string partName)
        {
            var matches = Regex.Matches(cleanCode, @"(?i)\b(?:call|udp)\s*\(\s*&\w+\s*.*?\)", RegexOptions.Compiled);
            foreach (Match m in matches)
            {
                int line = GetLineNumber(originalCode, m.Index);
                issues.Add(CreateIssue("GX004", "Dynamic call", "Warning", "Call via variable breaks the call tree.", m.Value, line, partName));
            }
        }

        private void CheckVariableUsageInObject(KBObject obj, JArray issues, string partName)
        {
            var varPart = obj.Parts.Get<VariablesPart>();
            if (varPart == null) return;
            
            string allCode = "";
            foreach (var p in obj.Parts)
                if (p is ISource s) allCode += (s.Source ?? "") + "\n";
            
            string cleanAllCode = StripComments(allCode);
            var usedVariables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var matches = Regex.Matches(cleanAllCode, @"&(\w+)\b", RegexOptions.IgnoreCase);
            foreach (Match m in matches) usedVariables.Add(m.Groups[1].Value);
            string variablesText = VariableInjector.GetVariablesAsText(varPart);
            foreach (var v in varPart.Variables)
            {
                if (GxMcp.Worker.Helpers.FrameworkManagedVariables.ShouldSkipUnusedCheck(v.Name)) continue;
                int declarationLine = FindVariableDeclarationLine(variablesText, v.Name);
                if (!usedVariables.Contains(v.Name))
                    issues.Add(CreateIssue("GX008", "Unused variable", "Warning", $"Variable '&{v.Name}' is never used.", "&" + v.Name, declarationLine, "Variables"));
            }
        }

        private int FindVariableDeclarationLine(string variablesText, string variableName)
        {
            if (string.IsNullOrWhiteSpace(variablesText) || string.IsNullOrWhiteSpace(variableName))
                return 1;

            var lines = variablesText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            for (int i = 0; i < lines.Length; i++)
            {
                if (Regex.IsMatch(
                    lines[i],
                    @"^\s*&" + Regex.Escape(variableName) + @"\s*:",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                {
                    return i + 1;
                }
            }

            return 1;
        }

        private void CheckSubroutines(string cleanCode, JArray issues, string originalCode, string partName)
        {
            var subDefinitions = Regex.Matches(cleanCode, @"(?is)\bsub\s+'([^']+)'(.*?)\bendsub\b", RegexOptions.Compiled);
            var subCalls = Regex.Matches(cleanCode, @"(?i)\bdo\s+'([^']+)'", RegexOptions.Compiled);
            var calledSubs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in subCalls) calledSubs.Add(m.Groups[1].Value);

            foreach (Match m in subDefinitions)
            {
                if (!calledSubs.Contains(m.Groups[1].Value))
                {
                    int line = GetLineNumber(originalCode, m.Index);
                    issues.Add(CreateIssue("GX009", "Unused subroutine", "Warning", $"Subroutine '{m.Groups[1].Value}' is never called.", "Sub", line, partName));
                }
            }
        }

        private void CheckParmRule(string cleanCode, string objName, JArray issues, string partName)
        {
            if (string.IsNullOrWhiteSpace(cleanCode) || !Regex.IsMatch(cleanCode, @"(?i)\bparm\s*\(", RegexOptions.Compiled))
                issues.Add(CreateIssue("GX006", "Parm rule missing", "Warning", "No parameters defined.", "parm(...)", 1, partName));
        }

        // FR#20 (friction-report 2026-05-14): `parm(in: ..., out: &X)` makes GeneXus generate
        // `gx_radio_ctrl(..., enabled=0, readonly=1, ...)` for &X. Inputs render disabled and
        // users can't interact. Workaround is `&X.Enabled = 1` in Event Start. Warn when an
        // out: parm has no matching Enabled assignment.
        private void CheckGxButtonEvents(KBObject obj, JArray issues)
        {
            try
            {
                if (!(obj is WebPanel || obj is Transaction)) return;
                var webFormPart = obj.Parts.Cast<KBObjectPart>().FirstOrDefault(p => p is WebFormPart) as WebFormPart;
                if (webFormPart?.Document?.DocumentElement == null) return;

                var eventsPart = obj.Parts.Cast<KBObjectPart>().FirstOrDefault(p =>
                    p.TypeDescriptor?.Name?.Equals("Events", StringComparison.OrdinalIgnoreCase) == true);
                string eventsSrc = (eventsPart as ISource)?.Source ?? string.Empty;
                bool hasEventEnter = Regex.IsMatch(eventsSrc, @"(?i)\bEvent\s+Enter\b");

                var buttons = webFormPart.Document.DocumentElement.SelectNodes("//*[local-name()='gxButton']");
                if (buttons == null || buttons.Count == 0) return;
                foreach (System.Xml.XmlNode btn in buttons)
                {
                    string onClick = btn.Attributes?["onClickEvent"]?.Value
                                  ?? btn.Attributes?["eventGX"]?.Value;
                    if (string.IsNullOrEmpty(onClick)) continue;
                    if (!hasEventEnter)
                    {
                        string btnName = btn.Attributes?["id"]?.Value ?? btn.Attributes?["ControlName"]?.Value ?? "<unnamed>";
                        issues.Add(CreateIssue(
                            "GX020",
                            "gxButton onClickEvent ignored",
                            "Warning",
                            $"gxButton '{btnName}' has onClickEvent={onClick} but only `Event Enter` is bound for gxButton in HTML layouts. " +
                            "Rename your handler to `Event Enter` or use <gxBitmap eventGX=\"...\"/> for custom events.",
                            "<gxButton onClickEvent=\"" + onClick + "\"/>",
                            1,
                            "Layout"));
                    }
                }
            }
            catch (Exception ex) { Logger.Debug("CheckGxButtonEvents: " + ex.Message); }
        }

        // FR#15 (friction-report 2026-05-14): non-prefixed Layout elements like
        // <Button>/<Bitmap>/<TextBlock> render as literal HTML (no handlers) but build
        // succeeds without warnings, so the agent burns 2-3 build cycles discovering it.
        // Warn when an element looks like a GeneXus control but is missing the `gx` prefix.
        private static readonly HashSet<string> _gxControlElements = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Button", "Bitmap", "TextBlock", "Attribute", "Grid", "FreeStyleGrid",
            "EmbeddedPage", "Tab", "TabPage", "Card", "Group", "Image"
        };

        private void CheckLayoutNonPrefixedElements(KBObject obj, JArray issues)
        {
            try
            {
                if (!(obj is WebPanel || obj is Transaction)) return;
                var webFormPart = obj.Parts.Cast<KBObjectPart>().FirstOrDefault(p => p is WebFormPart) as WebFormPart;
                if (webFormPart?.Document?.DocumentElement == null) return;

                // Use SelectNodes with XPath that matches any element whose local-name has no gx prefix.
                var nodes = webFormPart.Document.DocumentElement.SelectNodes("//*");
                if (nodes == null) return;
                foreach (System.Xml.XmlNode node in nodes)
                {
                    string ln = node.LocalName;
                    if (string.IsNullOrEmpty(ln)) continue;
                    if (ln.StartsWith("gx", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!_gxControlElements.Contains(ln)) continue;

                    issues.Add(CreateIssue(
                        "GX022",
                        "Layout element missing gx prefix",
                        "Warning",
                        $"<{ln}> in the Layout will render as literal HTML without GeneXus handlers. " +
                        $"Did you mean <gx{ln}>? Non-prefixed elements are silently accepted by the build but never wired up.",
                        $"<{ln}/>",
                        1,
                        "Layout"));
                }
            }
            catch (Exception ex) { Logger.Debug("CheckLayoutNonPrefixedElements: " + ex.Message); }
        }

        private void CheckOutParmEnabled(KBObject obj, JArray issues)
        {
            try
            {
                if (!(obj is WebPanel || obj is Transaction || obj is Procedure)) return;
                var rulesPart = obj.Parts.Cast<KBObjectPart>().FirstOrDefault(p => p is RulesPart) as RulesPart;
                string rulesSrc = (rulesPart as ISource)?.Source ?? string.Empty;
                if (string.IsNullOrWhiteSpace(rulesSrc)) return;

                var parmMatch = Regex.Match(rulesSrc, @"(?is)\bparm\s*\(([^)]*)\)", RegexOptions.Compiled);
                if (!parmMatch.Success) return;
                string parmBody = parmMatch.Groups[1].Value;

                var outVars = new List<string>();
                foreach (Match m in Regex.Matches(parmBody, @"(?i)\bout\s*:\s*&(\w+)"))
                {
                    outVars.Add(m.Groups[1].Value);
                }
                if (outVars.Count == 0) return;

                var eventsPart = obj.Parts.Cast<KBObjectPart>().FirstOrDefault(p =>
                    p.TypeDescriptor?.Name?.Equals("Events", StringComparison.OrdinalIgnoreCase) == true);
                string eventsSrc = (eventsPart as ISource)?.Source ?? string.Empty;

                foreach (var v in outVars)
                {
                    var rx = new Regex(@"(?i)&" + Regex.Escape(v) + @"\s*\.\s*Enabled\s*=\s*1");
                    if (!rx.IsMatch(eventsSrc))
                    {
                        issues.Add(CreateIssue(
                            "GX021",
                            "out: parm may render disabled",
                            "Info",
                            $"&{v} is declared `out:` in parm rule — GeneXus may render its control as disabled. " +
                            $"If editable, add `&{v}.Enabled = 1` in Event Start.",
                            $"out: &{v}",
                            1,
                            "Rules"));
                    }
                }
            }
            catch (Exception ex) { Logger.Debug("CheckOutParmEnabled: " + ex.Message); }
        }

        private void CheckNewWhenDuplicate(string cleanCode, JArray issues, string originalCode, string partName)
        {
            var newBlocks = Regex.Matches(cleanCode, @"(?is)\bnew\b\s*.*?\s*\bendnew\b", RegexOptions.Compiled);
            foreach (Match m in newBlocks)
            {
                if (!Regex.IsMatch(m.Value, @"(?i)\bwhen\s+duplicate\b", RegexOptions.Compiled))
                {
                    int line = GetLineNumber(originalCode, m.Index);
                    issues.Add(CreateIssue("GX005", "New without When Duplicate", "Info", "Consider adding 'when duplicate'.", "New", line, partName));
                }
            }
        }

        private string StripComments(string code)
        {
            return Regex.Replace(code, @"/\*.*?\*/|//.*?\n", " ", RegexOptions.Singleline);
        }

        private int GetLineNumber(string text, int index)
        {
            int line = 1;
            for (int i = 0; i < index && i < text.Length; i++) if (text[i] == '\n') line++;
            return line;
        }

        private JObject CreateIssue(string id, string title, string severity, string description, string snippet, int line, string part)
        {
            var issue = new JObject();
            issue["code"] = id;
            issue["title"] = title;
            issue["severity"] = severity;
            issue["description"] = description;
            issue["line"] = line;
            issue["snippet"] = snippet;
            issue["part"] = part;
            return issue;
        }
    }
}
