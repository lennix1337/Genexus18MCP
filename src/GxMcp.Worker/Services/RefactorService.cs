using System;
using System.Linq;
using System.Collections.Generic;
using Artech.Architecture.Common.Objects;
using Artech.Architecture.Common.Services;
using Artech.Genexus.Common.Parts;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Helpers;

namespace GxMcp.Worker.Services
{
    public class RefactorService
    {
        private readonly KbService _kbService;
        private readonly ObjectService _objectService;
        private readonly IndexCacheService _indexCacheService;

        public RefactorService(KbService kbService, ObjectService objectService, IndexCacheService indexCacheService)
        {
            _kbService = kbService;
            _objectService = objectService;
            _indexCacheService = indexCacheService;
        }

        public string Refactor(string target, string action, string payload)
        {
            try {
                if (action == "ExtractProcedure") {
                    var data = JObject.Parse(payload);
                    return ExtractProcedure(target, data["code"]?.ToString(), data["procedureName"]?.ToString());
                }

                string oldName = null;
                string newName = null;

                if (payload.Trim().StartsWith("{"))
                {
                    var data = JObject.Parse(payload);
                    oldName = data["oldName"]?.ToString();
                    newName = data["newName"]?.ToString();
                }
                else
                {
                    oldName = target;
                    newName = payload;
                }

                if (action == "RenameVariable" || (oldName != null && oldName.StartsWith("&"))) {
                    return RenameVariable(target, oldName, newName);
                }
                
                if (action == "RenameAttribute" || action == "RenameObject") {
                    return RenameAttribute(oldName, newName);
                }

                return Models.McpResponse.Error("Refactor action not found", target, action, "Supported actions are RenameVariable, RenameAttribute, RenameObject and ExtractProcedure.");
            } catch (Exception ex) {
                return "{\"error\":\"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        private string ExtractProcedure(string sourceObjectName, string codeToExtract, string newProcName)
        {
            if (string.IsNullOrEmpty(codeToExtract) || string.IsNullOrEmpty(newProcName))
                return Models.McpResponse.Error("Code and new procedure name are required", sourceObjectName, "Source", "Provide both the extracted code block and the new procedure name.");

            var sourceObj = _objectService.FindObject(sourceObjectName);
            if (sourceObj == null) return Models.McpResponse.Error("Source object not found", sourceObjectName, "Source", "The source object for extraction is not available in the active Knowledge Base.");

            try {
                Logger.Info($"Extracting code to new procedure: {newProcName} from {sourceObjectName}");

                var writeService = new WriteService(_objectService);
                string createResult = writeService.WriteObject(newProcName, "Procedure", codeToExtract);
                
                if (createResult.Contains("error")) return createResult;

                var newProc = _objectService.FindObject(newProcName) as global::Artech.Genexus.Common.Objects.Procedure;
                if (newProc == null) return Models.McpResponse.Error("Failed to create new procedure object", newProcName, "Procedure", "The new procedure could not be created or resolved after extraction.");

                var variablesFound = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var matches = System.Text.RegularExpressions.Regex.Matches(codeToExtract, @"&(\w+)");
                foreach (System.Text.RegularExpressions.Match match in matches) variablesFound.Add(match.Groups[1].Value);

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
                                targetVar = Activator.CreateInstance(sourceVar.GetType(), new object[] { vName, targetVarPart });
                                targetVarPart.Variables.Add(targetVar);
                            }
                            targetVar.Type = sourceVar.Type;
                            targetVar.Length = sourceVar.Length;
                            targetVar.Decimals = sourceVar.Decimals;
                            targetVar.Description = sourceVar.Description;
                        }
                    }
                }
                newProc.EnsureSave();

                string callCode = newProcName + ".call(" + string.Join(", ", variablesFound.Select(v => "&" + v)) + ")";
                bool updated = false;
                foreach (var part in sourceObj.Parts.Cast<KBObjectPart>()) {
                    if (part is ISource sourcePart) {
                        string original = sourcePart.Source;
                        if (!string.IsNullOrEmpty(original) && original.Contains(codeToExtract)) {
                            sourcePart.Source = original.Replace(codeToExtract, callCode);
                            updated = true;
                        }
                    }
                }

                if (updated) {
                    sourceObj.EnsureSave();
                    _indexCacheService.UpdateEntry(sourceObj);
                    _indexCacheService.UpdateEntry(newProc);
                    return "{\"status\":\"Success\", \"procedure\":\"" + newProcName + "\", \"call\":\"" + callCode + "\"}";
                }

                return Models.McpResponse.Error("Code block not found in source object", sourceObjectName, "Source", "The exact code block to extract was not found in the source object.");
            } catch (Exception ex) {
                return "{\"error\":\"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        private string RenameAttribute(string oldName, string newName)
        {
            if (string.IsNullOrEmpty(oldName) || string.IsNullOrEmpty(newName))
                return Models.McpResponse.Error("Old and new names are required", oldName, null, "Provide both the current name and the replacement name.");

            var kb = _kbService.GetKB();
            if (kb == null) return Models.McpResponse.Error("KB not open", oldName, null, "Open a Knowledge Base before running refactor operations.");

            var attrObj = _objectService.FindObject(oldName);
            if (attrObj == null || !attrObj.TypeDescriptor.Name.Equals("Attribute", StringComparison.OrdinalIgnoreCase))
                return Models.McpResponse.Error("Attribute not found", oldName, null, "The requested attribute is not available in the active Knowledge Base.");

            attrObj.Name = newName;
            attrObj.EnsureSave();

            var index = _indexCacheService.GetIndex();
            var affectedObjects = new List<string>();
            // index.Objects is keyed as "Type:Name" via GetEntryStorageKey, not bare Name.
            if (index != null && index.Objects.TryGetValue("Attribute:" + oldName, out var entry) && entry.CalledBy != null)
                affectedObjects.AddRange(entry.CalledBy);

            string pattern = @"(?i)\b" + System.Text.RegularExpressions.Regex.Escape(oldName) + @"\b";
            int updatedCount = 0;

            foreach (var objName in affectedObjects.Distinct()) {
                var obj = _objectService.FindObject(objName);
                if (obj == null) continue;
                bool changed = false;
                foreach (var part in obj.Parts.Cast<KBObjectPart>()) {
                    if (part is ISource sourcePart) {
                        string original = sourcePart.Source;
                        if (!string.IsNullOrEmpty(original)) {
                            string updated = System.Text.RegularExpressions.Regex.Replace(original, pattern, newName);
                            if (updated != original) { sourcePart.Source = updated; changed = true; }
                        }
                    }
                }
                if (changed) { obj.EnsureSave(); _indexCacheService.UpdateEntry(obj); updatedCount++; }
            }

            _indexCacheService.UpdateEntry(attrObj);
            return "{\"status\":\"Success\", \"affectedObjects\":" + updatedCount + "}";
        }

        private string RenameVariable(string target, string oldName, string newName)
        {
            string cleanOld = oldName.StartsWith("&") ? oldName.Substring(1) : oldName;
            string cleanNew = newName.StartsWith("&") ? newName.Substring(1) : newName;
            var obj = _objectService.FindObject(target);
            if (obj == null) return Models.McpResponse.Error("Object not found", target, "Variables", "The requested object is not available in the active Knowledge Base.");

            bool changed = false;
            var varPart = obj.Parts.Get<VariablesPart>();
            if (varPart != null) {
                var v = varPart.Variables.FirstOrDefault(x => x.Name.Equals(cleanOld, StringComparison.OrdinalIgnoreCase));
                if (v != null) { v.Name = cleanNew; changed = true; }
            }

            string pattern = @"(?i)&" + System.Text.RegularExpressions.Regex.Escape(cleanOld) + @"\b";
            string replacement = "&" + cleanNew;

            foreach (var part in obj.Parts.Cast<KBObjectPart>()) {
                if (part is ISource sourcePart) {
                    string original = sourcePart.Source;
                    if (!string.IsNullOrEmpty(original)) {
                        string updated = System.Text.RegularExpressions.Regex.Replace(original, pattern, replacement);
                        if (updated != original) { sourcePart.Source = updated; changed = true; }
                    }
                }
            }

            if (changed) { obj.EnsureSave(); _indexCacheService.UpdateEntry(obj); return "{\"status\":\"Success\"}"; }
            return "{\"status\":\"No changes made\"}";
        }
    }
}
