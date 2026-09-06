using System;
using System.Linq;
using System.Collections.Generic;
using System.Xml.Linq;
using Artech.Architecture.Common.Objects;
using Artech.Architecture.Common.Services;
using Artech.Genexus.Common.Parts;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;

namespace GxMcp.Worker.Services
{
    public class RefactorService
    {
        private readonly KbService _kbService;
        private readonly ObjectService _objectService;
        private readonly IndexCacheService _indexCacheService;
        private readonly WriteService _writeService;
        private readonly PatternAnalysisService _patternAnalysisService;

        public RefactorService(KbService kbService, ObjectService objectService, IndexCacheService indexCacheService,
            WriteService writeService = null, PatternAnalysisService patternAnalysisService = null)
        {
            _kbService = kbService;
            _objectService = objectService;
            _indexCacheService = indexCacheService;
            _writeService = writeService;
            _patternAnalysisService = patternAnalysisService;
        }

        public string Refactor(string target, string action, string payload, bool dryRun = false)
        {
            try {
                if (action == "ExtractProcedure") {
                    var data = JObject.Parse(payload);
                    return ExtractProcedure(target,
                        data["code"]?.ToString() ?? data["codeToExtract"]?.ToString(),
                        data["procedureName"]?.ToString() ?? data["name"]?.ToString(),
                        dryRun);
                }

                if (action == "ExtractSubroutine" || string.Equals(action, "extract_subroutine", StringComparison.OrdinalIgnoreCase)) {
                    var data = JObject.Parse(payload);
                    return ExtractSubroutine(target,
                        data["code"]?.ToString() ?? data["codeToExtract"]?.ToString(),
                        data["subroutineName"]?.ToString() ?? data["subroutine"]?.ToString() ?? data["name"]?.ToString(),
                        dryRun);
                }

                if (action == "WWPSetCondition")
                {
                    var data = JObject.Parse(payload);
                    return WWPSetCondition(target, data["controlAttribute"]?.ToString(), data["value"]?.ToString(), data["typeFilter"]?.ToString());
                }

                string oldName = null;
                string newName = null;
                string typeFilter = null;

                if (payload.Trim().StartsWith("{"))
                {
                    var data = JObject.Parse(payload);
                    oldName = data["oldName"]?.ToString();
                    newName = data["newName"]?.ToString();
                    typeFilter = data["type"]?.ToString() ?? data["typeFilter"]?.ToString();
                }
                else
                {
                    oldName = target;
                    newName = payload;
                }

                if (action == "RenameVariable" || (oldName != null && oldName.StartsWith("&"))) {
                    if (dryRun)
                        return Models.McpResponse.Ok(
                            target: target,
                            code: "DryRun",
                            result: new JObject
                            {
                                ["preview"] = new JObject
                                {
                                    ["action"] = "RenameVariable",
                                    ["objectName"] = target,
                                    ["oldName"] = oldName,
                                    ["newName"] = newName
                                }
                            });
                    return RenameVariable(target, oldName, newName);
                }

                if (action == "RenameAttribute") {
                    if (dryRun)
                    {
                        var index = _indexCacheService.GetIndex();
                        return Models.McpResponse.Ok(
                            target: oldName,
                            code: "DryRun",
                            result: new JObject
                            {
                                ["preview"] = BuildRenamePreview(index, oldName, newName, "Attribute")
                            });
                    }
                    return RenameAttribute(oldName, newName);
                }

                if (action == "RenameObject") {
                    if (dryRun)
                    {
                        // Resolve the object first (honoring typeFilter) so the CalledBy
                        // lookup uses the object's real type, not a hardcoded "Attribute:".
                        var obj = _objectService.FindObject(oldName, typeFilter);
                        string objType = obj?.TypeDescriptor?.Name;
                        var index = _indexCacheService.GetIndex();
                        return Models.McpResponse.Ok(
                            target: oldName,
                            code: "DryRun",
                            result: new JObject
                            {
                                ["preview"] = BuildRenamePreview(index, oldName, newName, objType)
                            });
                    }
                    return RenameObject(oldName, newName, typeFilter);
                }

                return Models.McpResponse.Err(
                    code: "RefactorActionNotFound",
                    message: $"Refactor action '{action}' not found.",
                    hint: "Supported actions are RenameVariable, RenameAttribute, RenameObject, ExtractProcedure, WWPSetCondition.",
                    nextSteps: new JArray(Models.McpResponse.NextStep(
                        tool: "genexus_refactor",
                        args: new JObject { ["target"] = target, ["action"] = "RenameVariable" },
                        why: "Example of a valid refactor call; replace action with the intended one.")),
                    target: target);
            } catch (Exception ex) {
                return Models.McpResponse.Err(
                    code: "RefactorFailed",
                    message: ex.Message,
                    hint: "Inspect the worker log for a stack trace; verify the target object exists and is writable.",
                    nextSteps: new JArray(Models.McpResponse.NextStep(
                        tool: "genexus_inspect",
                        args: new JObject { ["name"] = target },
                        why: "Confirm the target object exists and its parts are accessible before retrying.")),
                    target: target);
            }
        }

        private static JObject BuildRenamePreview(
            SearchIndex index,
            string oldName,
            string newName,
            string objectType)
        {
            var references = new JArray();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (index?.Objects != null)
            {
                foreach (var entry in index.Objects.Values)
                {
                    if (entry == null || string.IsNullOrEmpty(entry.Name)) continue;

                    if (string.Equals(entry.Name, oldName, StringComparison.OrdinalIgnoreCase)
                        && entry.CalledBy != null)
                    {
                        foreach (var caller in entry.CalledBy)
                        {
                            if (string.IsNullOrEmpty(caller)) continue;
                            string key = caller + "|sdk";
                            if (seen.Add(key))
                            {
                                references.Add(new JObject
                                {
                                    ["object"] = caller,
                                    ["part"] = "Source",
                                    ["source"] = "sdk",
                                    ["location"] = "unknown"
                                });
                            }
                        }
                    }

                    if (entry.Calls != null && entry.Calls.Any(c =>
                        string.Equals(c, oldName, StringComparison.OrdinalIgnoreCase)))
                    {
                        string key = entry.Name + "|sdk";
                        if (seen.Add(key))
                        {
                            references.Add(new JObject
                            {
                                ["object"] = entry.Name,
                                ["part"] = "Source",
                                ["source"] = "sdk",
                                ["location"] = "unknown"
                            });
                        }
                    }

                    if (!string.IsNullOrEmpty(entry.SourceSnippet))
                    {
                        foreach (var occurrence in SymbolRenameTokenizer.Find(entry.SourceSnippet, oldName))
                        {
                            string key = entry.Name + "|" + occurrence.Line + "|" + occurrence.Column;
                            if (!seen.Add(key)) continue;
                            references.Add(new JObject
                            {
                                ["object"] = entry.Name,
                                ["part"] = "Source",
                                ["source"] = "tokenizer",
                                ["line"] = occurrence.Line,
                                ["column"] = occurrence.Column,
                                ["location"] = "known"
                            });
                        }
                    }
                }
            }

            return new JObject
            {
                ["from"] = oldName,
                ["to"] = newName,
                ["type"] = objectType,
                ["references"] = references,
                ["referenceCount"] = references.Count,
                ["graphRevision"] = index?.GraphRevision ?? 0,
                ["rewriteStrategy"] = "identifier-tokenizer-preserves-comments-and-strings"
            };
        }

        private string WWPSetCondition(string target, string controlAttribute, string newConditionValue, string typeFilter)
        {
            if (_writeService == null || _patternAnalysisService == null)
                return Models.McpResponse.Err(
                    code: "WWPSetConditionUnavailable",
                    message: "WWPSetCondition unavailable: RefactorService missing WriteService/PatternAnalysisService dependencies.",
                    hint: "This is an internal wiring issue; ensure RefactorService is constructed with all dependencies.",
                    target: target);
            // no-nextStep: internal dependency wiring failure; no user-actionable tool call can resolve it
            if (string.IsNullOrEmpty(controlAttribute))
                return Models.McpResponse.Err(
                    code: "ControlAttributeRequired",
                    message: "controlAttribute is required.",
                    hint: "Provide the gridAttribute attribute name (e.g., 'DocCod').",
                    nextSteps: new JArray(Models.McpResponse.NextStep(
                        tool: "genexus_analyze",
                        args: new JObject { ["target"] = target, ["mode"] = "pattern_metadata" },
                        why: "Lists all gridAttribute names available in the PatternInstance so you can pick the correct one.")),
                    target: target);
            if (newConditionValue == null)
                return Models.McpResponse.Err(
                    code: "ConditionValueRequired",
                    message: "value is required.",
                    hint: "Provide the conditions string (e.g., 'DocTipOri = 24;'). Pass empty string to clear.",
                    target: target);
            // no-nextStep: missing scalar argument; no tool call can supply it

            var obj = _objectService.FindObject(target, typeFilter);
            if (obj == null) return Models.McpResponse.Err(
                code: "ObjectNotFound",
                message: "Object not found.",
                hint: "Use type=<Transaction> to disambiguate when multiple objects share the name.",
                nextSteps: new JArray(Models.McpResponse.NextStep(
                    tool: "genexus_search",
                    args: new JObject { ["query"] = target },
                    why: "Search for objects matching the name to find the correct identifier.")),
                target: target);

            string xml = _patternAnalysisService.ReadPatternPartXml(obj, "PatternInstance", out _, out _);
            if (string.IsNullOrWhiteSpace(xml))
                return Models.McpResponse.Err(
                    code: "PatternInstanceNotFound",
                    message: "PatternInstance not found.",
                    hint: "Object does not expose a WorkWithPlus PatternInstance.",
                    nextSteps: new JArray(Models.McpResponse.NextStep(
                        tool: "genexus_analyze",
                        args: new JObject { ["target"] = target, ["mode"] = "pattern_metadata" },
                        why: "Diagnose whether a WorkWithPlus instance exists for this object.")),
                    target: target);

            XDocument doc;
            try { doc = XDocument.Parse(xml, LoadOptions.PreserveWhitespace); }
            catch (Exception ex) {
                return Models.McpResponse.Err(
                    code: "PatternInstanceParseFailed",
                    message: "PatternInstance parse failed: " + ex.Message,
                    hint: "The PatternInstance XML is malformed; inspect the raw part content.",
                    nextSteps: new JArray(Models.McpResponse.NextStep(
                        tool: "genexus_read",
                        args: new JObject { ["name"] = target, ["part"] = "PatternInstance" },
                        why: "Read the raw PatternInstance XML to diagnose the parse error.")),
                    target: target);
            }

            var matches = doc.Descendants("gridAttribute")
                .Where(e => {
                    var a = e.Attribute("attribute")?.Value;
                    if (string.IsNullOrEmpty(a)) return false;
                    var dash = a.LastIndexOf('-');
                    var localName = dash >= 0 ? a.Substring(dash + 1) : a;
                    return string.Equals(localName, controlAttribute, StringComparison.OrdinalIgnoreCase);
                }).ToList();

            if (matches.Count == 0)
                return Models.McpResponse.Err(
                    code: "ControlNotFound",
                    message: $"No gridAttribute named '{controlAttribute}' found in PatternInstance.",
                    hint: "Check the attribute name spelling; use pattern_metadata to list valid gridAttribute names.",
                    nextSteps: new JArray(Models.McpResponse.NextStep(
                        tool: "genexus_analyze",
                        args: new JObject { ["target"] = target, ["mode"] = "pattern_metadata" },
                        why: "Lists all gridAttribute names in the PatternInstance so you can pick the correct one.")),
                    target: target);

            foreach (var el in matches)
            {
                if (string.IsNullOrEmpty(newConditionValue)) el.SetAttributeValue("conditions", null);
                else el.SetAttributeValue("conditions", newConditionValue);
            }

            string newXml = doc.ToString(SaveOptions.DisableFormatting);
            return _writeService.WriteObject(target, "PatternInstance", newXml, typeFilter);
        }

        private string ExtractProcedure(string sourceObjectName, string codeToExtract, string newProcName, bool dryRun = false)
        {
            if (string.IsNullOrEmpty(codeToExtract) || string.IsNullOrEmpty(newProcName))
                return Models.McpResponse.Err(
                    code: "ExtractProcedureArgsMissing",
                    message: "Code and new procedure name are required.",
                    hint: "Provide both the extracted code block and the new procedure name.",
                    target: sourceObjectName);
            // no-nextStep: missing scalar arguments; no tool call can supply them

            var sourceObj = _objectService.FindObject(sourceObjectName);
            if (sourceObj == null) return Models.McpResponse.Err(
                code: "ObjectNotFound",
                message: "Source object not found.",
                hint: "The source object for extraction is not available in the active Knowledge Base.",
                nextSteps: new JArray(Models.McpResponse.NextStep(
                    tool: "genexus_search",
                    args: new JObject { ["query"] = sourceObjectName },
                    why: "Search for the object to confirm it exists and find its exact name.")),
                target: sourceObjectName);

            var variablesFound = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var matches = System.Text.RegularExpressions.Regex.Matches(codeToExtract, @"&(\w+)");
            foreach (System.Text.RegularExpressions.Match match in matches) variablesFound.Add(match.Groups[1].Value);

            string callCode = newProcName + ".call(" + string.Join(", ", variablesFound.Select(v => "&" + v)) + ")";
            string normCode = codeToExtract.Replace("\r\n", "\n");
            bool codeFound = false;

            foreach (var part in sourceObj.Parts.Cast<KBObjectPart>())
            {
                if (part is ISource sourcePart)
                {
                    string original = sourcePart.Source;
                    if (!string.IsNullOrEmpty(original))
                    {
                        string normOriginal = original.Replace("\r\n", "\n");
                        if (normOriginal.Contains(normCode))
                        {
                            codeFound = true;
                            break;
                        }
                    }
                }
            }

            if (!codeFound)
            {
                return Models.McpResponse.Err(
                    code: "CodeBlockNotFound",
                    message: "Code block not found in source object.",
                    hint: "The exact code block to extract was not found in the source object; ensure the code matches the source verbatim.",
                    nextSteps: new JArray(Models.McpResponse.NextStep(
                        tool: "genexus_read",
                        args: new JObject { ["name"] = sourceObjectName, ["part"] = "Source" },
                        why: "Read the current source to locate the exact code block before retrying.")),
                    target: sourceObjectName);
            }

            if (dryRun)
            {
                return Models.McpResponse.Ok(
                    target: sourceObjectName,
                    code: "DryRun",
                    result: new JObject
                    {
                        ["action"] = "ExtractProcedure",
                        ["procedure"] = newProcName,
                        ["call"] = callCode,
                        ["variables"] = new JArray(variablesFound.Select(v => (JToken)v).ToArray()),
                        ["dryRun"] = true
                    });
            }

            try {
                Logger.Info($"Extracting code to new procedure: {newProcName} from {sourceObjectName}");

                // WriteObject only writes to existing objects — the procedure must be
                // created first via the same path genexus_create uses.
                string createResult = _objectService.CreateObject("Procedure", newProcName);

                // Use parsed envelope status instead of fragile string.Contains("error").
                bool createFailed = false;
                try
                {
                    var createObj = JObject.Parse(createResult);
                    string createStatus = createObj["status"]?.ToString() ?? string.Empty;
                    createFailed = !string.Equals(createStatus, "ok", StringComparison.OrdinalIgnoreCase)
                                && !string.Equals(createStatus, "success", StringComparison.OrdinalIgnoreCase);
                }
                catch { createFailed = createResult.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0; }
                if (createFailed) return createResult;

                var newProc = _objectService.FindObject(newProcName) as global::Artech.Genexus.Common.Objects.Procedure;
                if (newProc == null) return Models.McpResponse.Err(
                    code: "ExtractProcedureCreateFailed",
                    message: "Failed to create new procedure object.",
                    hint: "The new procedure could not be created or resolved after extraction; the KB may be read-only.",
                    nextSteps: new JArray(Models.McpResponse.NextStep(
                        tool: "genexus_inspect",
                        args: new JObject { ["name"] = newProcName },
                        why: "Check whether the procedure was partially created before the failure.")),
                    target: newProcName);

                newProc.ProcedurePart.Source = codeToExtract;

                string parmRule = "parm(" + string.Join(", ", variablesFound.Select(v => "inout:&" + v)) + ");";
                newProc.Rules.Source = parmRule + "\r\n" + newProc.Rules.Source;
                
                var sourceVarPart = sourceObj.Parts.Get<VariablesPart>();
                var targetVarPart = newProc.Parts.Get<VariablesPart>();
                if (sourceVarPart != null && targetVarPart != null) {
                    foreach (var vName in variablesFound) {
                        var sourceVar = sourceVarPart.Variables.FirstOrDefault(x => x.Name.Equals(vName, StringComparison.OrdinalIgnoreCase));
                        if (sourceVar != null) {
                            dynamic targetVar = targetVarPart.Variables.FirstOrDefault(x => x.Name.Equals(vName, StringComparison.OrdinalIgnoreCase));
                            if (targetVar == null) {
                                // Use VariablesPart.Add(string) typed overload — it returns the wrapped
                                // Variable already linked into the part with the bookkeeping that
                                // EnsureSave honors. The previous Activator.CreateInstance + Variables.Add
                                // path could silently drop new variables (mirror of commit 8c8f433's BC fix).
                                targetVar = targetVarPart.Add(vName);
                                if (targetVar == null) { Logger.Error($"[RefactorService] VariablesPart.Add('{vName}') returned null on '{newProcName}'."); continue; }
                            }
                            targetVar.Type = sourceVar.Type;
                            targetVar.Length = sourceVar.Length;
                            targetVar.Decimals = sourceVar.Decimals;
                            targetVar.Description = sourceVar.Description;
                        }
                    }
                }
                newProc.EnsureSave();

                bool updated = false;
                foreach (var part in sourceObj.Parts.Cast<KBObjectPart>()) {
                    if (part is ISource sourcePart) {
                        string original = sourcePart.Source;
                        if (!string.IsNullOrEmpty(original)) {
                            string normOriginal = original.Replace("\r\n", "\n");
                            if (normOriginal.Contains(normCode)) {
                                int idx = normOriginal.IndexOf(normCode, StringComparison.Ordinal);
                                string replaced = normOriginal.Substring(0, idx) + callCode + normOriginal.Substring(idx + normCode.Length);
                                sourcePart.Source = replaced.Replace("\n", "\r\n");
                                updated = true;
                            }
                        }
                    }
                }

                if (updated) {
                    sourceObj.EnsureSave();
                    _indexCacheService.UpdateEntry(sourceObj);
                    _indexCacheService.UpdateEntry(newProc);
                    return Models.McpResponse.Ok(
                        target: sourceObjectName,
                        code: "ProcedureExtracted",
                        result: new JObject { ["procedure"] = newProcName, ["call"] = callCode });
                }

                return Models.McpResponse.Err(
                    code: "CodeBlockNotFound",
                    message: "Code block not found in source object.",
                    hint: "The exact code block to extract was not found in the source object; ensure the code matches the source verbatim.",
                    nextSteps: new JArray(Models.McpResponse.NextStep(
                        tool: "genexus_read",
                        args: new JObject { ["name"] = sourceObjectName, ["part"] = "Source" },
                        why: "Read the current source to locate the exact code block before retrying.")),
                    target: sourceObjectName);
            } catch (Exception ex) {
                return Models.McpResponse.Err(
                    code: "ExtractProcedureFailed",
                    message: ex.Message,
                    hint: "Inspect the worker log for a stack trace; verify the source object and target procedure name.",
                    nextSteps: new JArray(Models.McpResponse.NextStep(
                        tool: "genexus_inspect",
                        args: new JObject { ["name"] = sourceObjectName },
                        why: "Confirm the source object is accessible and its parts are readable before retrying.")),
                    target: sourceObjectName);
            }
        }

        private string ExtractSubroutine(string sourceObjectName, string codeToExtract, string subroutineName, bool dryRun = false)
        {
            if (string.IsNullOrEmpty(codeToExtract) || string.IsNullOrEmpty(subroutineName))
                return Models.McpResponse.Err(
                    code: "ExtractSubroutineArgsMissing",
                    message: "Code and subroutine name are required.",
                    hint: "Provide both the extracted code block and the subroutine name.",
                    target: sourceObjectName);

            var sourceObj = _objectService.FindObject(sourceObjectName);
            if (sourceObj == null) return Models.McpResponse.Err(
                code: "ObjectNotFound",
                message: "Source object not found.",
                hint: "The source object for extraction is not available in the active Knowledge Base.",
                nextSteps: new JArray(Models.McpResponse.NextStep(
                    tool: "genexus_search",
                    args: new JObject { ["query"] = sourceObjectName },
                    why: "Search for the object to confirm it exists and find its exact name.")),
                target: sourceObjectName);

            string cleanSubName = subroutineName.Trim('\'', '\"', ' ');
            string callCode = $"Do '{cleanSubName}'";

            bool updated = false;
            string matchedPartName = null;

            foreach (var part in sourceObj.Parts.Cast<KBObjectPart>())
            {
                if (part is ISource sourcePart)
                {
                    string original = sourcePart.Source;
                    if (string.IsNullOrEmpty(original)) continue;

                    string normOriginal = original.Replace("\r\n", "\n");
                    string normCode = codeToExtract.Replace("\r\n", "\n");

                    if (normOriginal.Contains(normCode))
                    {
                        int idx = normOriginal.IndexOf(normCode, StringComparison.Ordinal);
                        string replaced = normOriginal.Substring(0, idx) + callCode + normOriginal.Substring(idx + normCode.Length);
                        string subDefinition = $"\n\nSub '{cleanSubName}'\n{normCode.Trim()}\nEndSub\n";
                        string updatedSource = (replaced.TrimEnd() + subDefinition).Replace("\n", "\r\n");

                        matchedPartName = part is ProcedurePart ? "Source" : (part is EventsPart ? "Events" : part.GetType().Name);
                        if (!dryRun)
                        {
                            sourcePart.Source = updatedSource;
                        }
                        updated = true;
                        break;
                    }
                }
            }

            if (!updated)
            {
                return Models.McpResponse.Err(
                    code: "CodeBlockNotFound",
                    message: "Code block not found in source object.",
                    hint: "The exact code block to extract was not found in any source part of the target object; ensure whitespace and formatting match verbatim.",
                    nextSteps: new JArray(Models.McpResponse.NextStep(
                        tool: "genexus_read",
                        args: new JObject { ["name"] = sourceObjectName, ["part"] = "Source" },
                        why: "Read the current source to locate the exact code block before retrying.")),
                    target: sourceObjectName);
            }

            if (!dryRun)
            {
                sourceObj.EnsureSave();
                _indexCacheService.UpdateEntry(sourceObj);
            }

            return Models.McpResponse.Ok(
                target: sourceObjectName,
                code: "SubroutineExtracted",
                result: new JObject
                {
                    ["subroutine"] = cleanSubName,
                    ["call"] = callCode,
                    ["part"] = matchedPartName,
                    ["dryRun"] = dryRun
                });
        }

        private string RenameAttribute(string oldName, string newName)
        {
            if (string.IsNullOrEmpty(oldName) || string.IsNullOrEmpty(newName))
                return Models.McpResponse.Err(
                    code: "RenameArgsMissing",
                    message: "Old and new names are required.",
                    hint: "Provide both the current name and the replacement name.",
                    target: oldName);
            // no-nextStep: missing scalar arguments; no tool call can supply them

            var kb = _kbService.GetKB();
            if (kb == null) return Models.McpResponse.Err(
                code: "KbNotOpen",
                message: "KB not open.",
                hint: "Open a Knowledge Base before running refactor operations.",
                nextSteps: new JArray(Models.McpResponse.NextStep(
                    tool: "genexus_kb",
                    args: new JObject { ["action"] = "open" },
                    why: "Opens the configured Knowledge Base so refactor operations can proceed.")),
                target: oldName);

            var attrObj = _objectService.FindObject(oldName);
            if (attrObj == null || !attrObj.TypeDescriptor.Name.Equals("Attribute", StringComparison.OrdinalIgnoreCase))
                return Models.McpResponse.Err(
                    code: "AttributeNotFound",
                    message: "Attribute not found.",
                    hint: "The requested attribute is not available in the active Knowledge Base.",
                    nextSteps: new JArray(Models.McpResponse.NextStep(
                        tool: "genexus_search",
                        args: new JObject { ["query"] = oldName, ["type"] = "Attribute" },
                        why: "Search for attributes matching the name to find the correct identifier.")),
                    target: oldName);

            // Patch callers FIRST (before renaming the attribute) so that the old
            // name is still resolvable while we enumerate and save each caller.
            // Renaming the attribute before patching callers can cause reference
            // failures inside EnsureSave for objects that still reference oldName.
            var index = _indexCacheService.GetIndex();
            var affectedObjects = new List<string>();
            // index.Objects is keyed as "Type:Name" via GetEntryStorageKey, not bare Name.
            if (index != null && index.Objects.TryGetValue("Attribute:" + oldName, out var entry) && entry.CalledBy != null)
                affectedObjects.AddRange(entry.CalledBy);

            var patched = new List<string>();
            var failed = new List<JObject>();
            var patchedKbObjects = new List<KBObject>();

            // Patch-callers + target rename are wrapped in a single SDK transaction
            // so a hard failure (including the final EnsureSave) rolls back every
            // caller save too, instead of leaving callers referencing newName while
            // the attribute itself still has oldName. Index updates are deferred
            // until after Commit() (mirrors StructureService.UpdateVisualStructure).
            using (var sdkTrans = attrObj.Model.KB.BeginTransaction())
            {
                try
                {
                    foreach (var objName in affectedObjects.Distinct()) {
                        var obj = _objectService.FindObject(objName);
                        if (obj == null) { failed.Add(new JObject { ["name"] = objName, ["reason"] = "object not found" }); continue; }
                        bool changed = false;
                        try
                        {
                            foreach (var part in obj.Parts.Cast<KBObjectPart>()) {
                                if (part is ISource sourcePart) {
                                    string original = sourcePart.Source;
                                    if (!string.IsNullOrEmpty(original)) {
                                        string updated = SymbolRenameTokenizer.Rewrite(original, oldName, newName, out _);
                                        if (updated != original) { sourcePart.Source = updated; changed = true; }
                                    }
                                }
                            }
                            if (changed) { obj.EnsureSave(); patched.Add(objName); patchedKbObjects.Add(obj); }
                        }
                        catch (Exception patchEx)
                        {
                            // Preserve the existing partial-success contract: a caller that
                            // can't be patched is recorded and tolerated, not a transaction abort.
                            failed.Add(new JObject { ["name"] = objName, ["reason"] = patchEx.Message });
                        }
                    }

                    // Now rename the attribute itself.
                    attrObj.Name = newName;
                    attrObj.EnsureSave();
                    sdkTrans.Commit();
                }
                catch (Exception)
                {
                    sdkTrans.Rollback();
                    throw;
                }
            }

            foreach (var pObj in patchedKbObjects) _indexCacheService.UpdateEntry(pObj);
            _indexCacheService.UpdateEntry(attrObj);

            bool hasFailures = failed.Count > 0;
            var resultObj = new JObject
            {
                ["oldName"] = oldName,
                ["newName"] = newName,
                ["patched"] = new JArray(patched.Select(p => (JToken)p).ToArray()),
                ["failed"] = new JArray(failed.Cast<JToken>().ToArray()),
                ["affectedObjects"] = patched.Count
            };

            if (hasFailures)
            {
                // Partial success — attribute was renamed but some callers could not be patched.
                return Models.McpResponse.Ok(
                    target: oldName,
                    code: "AttributeRenamedPartial",
                    result: resultObj);
            }

            return Models.McpResponse.Ok(
                target: oldName,
                code: "AttributeRenamed",
                result: resultObj);
        }

        private string RenameObject(string oldName, string newName, string typeFilter)
        {
            if (string.IsNullOrEmpty(oldName) || string.IsNullOrEmpty(newName))
                return Models.McpResponse.Err(
                    code: "RenameArgsMissing",
                    message: "Old and new names are required.",
                    hint: "Provide both the current name and the replacement name.",
                    target: oldName);
            // no-nextStep: missing scalar arguments; no tool call can supply them

            var kb = _kbService.GetKB();
            if (kb == null) return Models.McpResponse.Err(
                code: "KbNotOpen",
                message: "KB not open.",
                hint: "Open a Knowledge Base before running refactor operations.",
                nextSteps: new JArray(Models.McpResponse.NextStep(
                    tool: "genexus_kb",
                    args: new JObject { ["action"] = "open" },
                    why: "Opens the configured Knowledge Base so refactor operations can proceed.")),
                target: oldName);

            var obj = _objectService.FindObject(oldName, typeFilter);
            if (obj == null)
                return Models.McpResponse.Err(
                    code: "ObjectNotFound",
                    message: "Object not found.",
                    hint: "Pass type=<WebPanel|Transaction|...> to disambiguate when multiple objects share this name (e.g. a Table and a WebPanel).",
                    nextSteps: new JArray(Models.McpResponse.NextStep(
                        tool: "genexus_list_objects",
                        args: new JObject { ["query"] = oldName },
                        why: "List objects matching the name to find the correct type/identifier.")),
                    target: oldName);

            string objType = obj.TypeDescriptor?.Name;

            // Patch callers FIRST (before renaming the object) so that the old
            // name is still resolvable while we enumerate and save each caller.
            // Renaming the object before patching callers can cause reference
            // failures inside EnsureSave for objects that still reference oldName.
            var index = _indexCacheService.GetIndex();
            var affectedObjects = new List<string>();
            // index.Objects is keyed as "Type:Name" via GetEntryStorageKey, not bare Name.
            if (index != null && index.Objects.TryGetValue(objType + ":" + oldName, out var entry) && entry.CalledBy != null)
                affectedObjects.AddRange(entry.CalledBy);

            var patched = new List<string>();
            var failed = new List<JObject>();
            var patchedKbObjects = new List<KBObject>();

            // Patch-callers + target rename are wrapped in a single SDK transaction
            // so a hard failure (including the final EnsureSave) rolls back every
            // caller save too, instead of leaving callers referencing newName while
            // the object itself still has oldName. Index updates are deferred until
            // after Commit() (mirrors StructureService.UpdateVisualStructure).
            using (var sdkTrans = obj.Model.KB.BeginTransaction())
            {
                try
                {
                    foreach (var objName in affectedObjects.Distinct()) {
                        var caller = _objectService.FindObject(objName);
                        if (caller == null) { failed.Add(new JObject { ["name"] = objName, ["reason"] = "object not found" }); continue; }
                        bool changed = false;
                        try
                        {
                            foreach (var part in caller.Parts.Cast<KBObjectPart>()) {
                                if (part is ISource sourcePart) {
                                    string original = sourcePart.Source;
                                    if (!string.IsNullOrEmpty(original)) {
                                        string updated = SymbolRenameTokenizer.Rewrite(original, oldName, newName, out _);
                                        if (updated != original) { sourcePart.Source = updated; changed = true; }
                                    }
                                }
                            }
                            if (changed) { caller.EnsureSave(); patched.Add(objName); patchedKbObjects.Add(caller); }
                        }
                        catch (Exception patchEx)
                        {
                            // Preserve the existing partial-success contract: a caller that
                            // can't be patched is recorded and tolerated, not a transaction abort.
                            failed.Add(new JObject { ["name"] = objName, ["reason"] = patchEx.Message });
                        }
                    }

                    // Now rename the object itself.
                    obj.Name = newName;
                    obj.EnsureSave();
                    sdkTrans.Commit();
                }
                catch (Exception)
                {
                    sdkTrans.Rollback();
                    throw;
                }
            }

            foreach (var pObj in patchedKbObjects) _indexCacheService.UpdateEntry(pObj);
            _indexCacheService.UpdateEntry(obj);

            bool hasFailures = failed.Count > 0;
            var resultObj = new JObject
            {
                ["oldName"] = oldName,
                ["newName"] = newName,
                ["type"] = objType,
                ["patched"] = new JArray(patched.Select(p => (JToken)p).ToArray()),
                ["failed"] = new JArray(failed.Cast<JToken>().ToArray()),
                ["affectedObjects"] = patched.Count
            };

            if (hasFailures)
            {
                // Partial success — object was renamed but some callers could not be patched.
                return Models.McpResponse.Ok(
                    target: oldName,
                    code: "ObjectRenamedPartial",
                    result: resultObj);
            }

            return Models.McpResponse.Ok(
                target: oldName,
                code: "ObjectRenamed",
                result: resultObj);
        }

        private string RenameVariable(string target, string oldName, string newName)
        {
            string cleanOld = oldName.StartsWith("&") ? oldName.Substring(1) : oldName;
            string cleanNew = newName.StartsWith("&") ? newName.Substring(1) : newName;
            var obj = _objectService.FindObject(target);
            if (obj == null) return Models.McpResponse.Err(
                code: "ObjectNotFound",
                message: "Object not found.",
                hint: "The requested object is not available in the active Knowledge Base.",
                nextSteps: new JArray(Models.McpResponse.NextStep(
                    tool: "genexus_search",
                    args: new JObject { ["query"] = target },
                    why: "Search for objects matching the name to find the correct identifier.")),
                target: target);

            bool changed = false;
            var varPart = obj.Parts.Get<VariablesPart>();
            if (varPart != null) {
                var v = varPart.Variables.FirstOrDefault(x => x.Name.Equals(cleanOld, StringComparison.OrdinalIgnoreCase));
                if (v != null) { v.Name = cleanNew; changed = true; }
            }

            foreach (var part in obj.Parts.Cast<KBObjectPart>()) {
                if (part is ISource sourcePart) {
                    string original = sourcePart.Source;
                    if (!string.IsNullOrEmpty(original)) {
                        string updated = SymbolRenameTokenizer.RewritePrefixed(original, cleanOld, cleanNew, out _);
                        if (updated != original) { sourcePart.Source = updated; changed = true; }
                    }
                }
            }

            if (changed) {
                obj.EnsureSave();
                _indexCacheService.UpdateEntry(obj);
                return Models.McpResponse.Ok(
                    target: target,
                    code: "VariableRenamed",
                    result: new JObject { ["oldName"] = "&" + cleanOld, ["newName"] = "&" + cleanNew });
            }
            return Models.McpResponse.Ok(target: target, code: "NoChange", result: new JObject { ["reason"] = "Variable not found or no occurrences matched." });
        }
    }
}
