using System;
using System.Collections.Generic;
using System.Linq;
using Artech.Genexus.Common.Objects;
using Artech.Genexus.Common.Parts;
using GxMcp.Worker.Helpers;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    // Full replace of a Data Selector's condition list (genexus_edit part=Conditions).
    // Parameters, orders and defined-by live in sibling items of the same structure part
    // and are never touched. Post-save verification is the generic text receipt in
    // WrapWithPersistedState (genexus_read part=Conditions exposes the same canonical text).
    public partial class WriteService
    {
        // Conditions that do not sit directly on the root level are inside a nested
        // AND/OR group. Extracted as a pure helper so the policy is testable without an
        // SDK KBObject: the write refuses these rather than silently flattening them.
        internal static List<string> NestedConditionExpressions(IEnumerable<KeyValuePair<string, bool>> conditions)
        {
            var nested = new List<string>();
            if (conditions == null) return nested;
            foreach (var condition in conditions)
            {
                if (condition.Key != null && condition.Value && !string.IsNullOrEmpty(condition.Key))
                    nested.Add(condition.Key);
            }
            return nested;
        }

        private string WriteDataSelectorConditions(DataSelector selector, string target, string content, bool dryRun, bool forceWrite)
        {
            var part = selector.DataSelectorStructure ?? selector.Parts.Get<DataSelectorStructurePart>();
            if (part == null)
            {
                return Models.McpResponse.Err(
                    code: "StructurePartNotFound",
                    message: "Data Selector has no structure part.",
                    target: target);
            }

            List<string> desired = DataSelectorConditionsEditor.Parse(content);
            List<string> before = DataSelectorConditionsEditor.ReadExpressions(part);
            bool changed = !DataSelectorConditionsEditor.SameConditions(before, desired);

            var details = new JObject
            {
                ["part"] = DataSelectorConditionsEditor.PartName,
                ["dataSelector"] = selector.Name,
                ["conditionsBefore"] = before.Count,
                ["conditionsRequested"] = desired.Count,
                ["changed"] = changed
            };

            if (dryRun)
            {
                details["savePathExercised"] = false;
                return Models.McpResponse.Ok(target: target, code: "WriteDryRun", result: details);
            }

            if (!changed && !forceWrite)
            {
                details["details"] = "No change";
                return Models.McpResponse.Ok(target: target, code: "WriteNoChange", result: details);
            }

            var kb = _objectService.GetKbService().GetKB();
            if (kb == null)
            {
                return LayoutService.ReportLayoutKbNotOpened(target, "Open a Knowledge Base before writing Data Selector conditions.");
            }

            using (var transaction = kb.BeginTransaction())
            {
                try
                {
                    if (part.Root == null) part.Root = new DataSelectorLevel(part);
                    DataSelectorLevel root = part.Root;

                    // A nested group cannot survive this write. The request format is one
                    // condition per line and carries no grouping, so replacing the list would
                    // drop the AND/OR structure and leave an empty level behind. Refuse
                    // instead: silently flattening a Data Selector's grouping is worse than
                    // not applying the edit.
                    var existingConditions = part.GetConditions()?.ToList()
                        ?? new List<DataSelectorCondition>();
                    var nested = NestedConditionExpressions(existingConditions.Select(c =>
                        new KeyValuePair<string, bool>(
                            DataSelectorConditionsEditor.NormalizeExpression(c?.Source?.Source ?? c?.ToString() ?? string.Empty),
                            c?.Parent != null && !ReferenceEquals(c.Parent, root))));
                    if (nested.Count > 0)
                    {
                        transaction.Rollback();
                        return Models.McpResponse.Err(
                            code: "DataSelectorConditionsNestedGroupsUnsupported",
                            message: "This Data Selector has conditions inside a nested AND/OR group, which a whole-list replace cannot preserve; nothing was saved.",
                            hint: "Flatten the Data Selector's conditions in GeneXus (or write the nested levels separately) and retry. The one-condition-per-line form has no way to express grouping.",
                            target: target,
                            extra: new JObject
                            {
                                ["part"] = DataSelectorConditionsEditor.PartName,
                                ["nestedConditions"] = new JArray(nested),
                                ["conditionsRequested"] = desired.Count
                            });
                    }

                    foreach (DataSelectorCondition existing in existingConditions)
                        (existing.Parent ?? root).Items.Remove(existing);
                    foreach (string expression in desired)
                        root.AddCondition(expression);

                    List<string> staged = DataSelectorConditionsEditor.ReadExpressions(part);
                    if (!DataSelectorConditionsEditor.SameConditions(staged, desired))
                    {
                        transaction.Rollback();
                        return Models.McpResponse.Err(
                            code: "DataSelectorConditionsStageMismatch",
                            message: "The SDK condition list differs from the request after staging; nothing was saved.",
                            target: target,
                            extra: new JObject
                            {
                                ["part"] = DataSelectorConditionsEditor.PartName,
                                ["requested"] = new JArray(desired),
                                ["staged"] = new JArray(staged.Select(DataSelectorConditionsEditor.NormalizeExpression))
                            });
                    }

                    selector.EnsureSave();
                    transaction.Commit();
                    ScheduleFlush(force: true);
                    NotePerTargetWrite(target);
                    _objectService.MarkReadCacheDirty(selector, DataSelectorConditionsEditor.PartName);

                    details["conditionsAfter"] = desired.Count;
                    details["savePathExercised"] = true;
                    return Models.McpResponse.Ok(target: target, code: "WriteApplied", result: details);
                }
                catch (Exception ex)
                {
                    try { transaction?.Rollback(); }
                    catch (Exception rollbackEx) { Logger.Debug("[WRITE] DataSelector conditions transaction rollback failed: " + rollbackEx.Message); }
                    return Models.McpResponse.Err(
                        code: "DataSelectorConditionsWriteFailed",
                        message: FormatExceptionChain(ex),
                        hint: "Each line must be a valid condition over attributes/parameters of the Data Selector, e.g. 'SampleId = &SampleId'. The failed edit was rolled back.",
                        target: target,
                        extra: new JObject { ["part"] = DataSelectorConditionsEditor.PartName });
                }
            }
        }
    }
}
