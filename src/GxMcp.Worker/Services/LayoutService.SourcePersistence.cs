using System;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Artech.Architecture.Common.Objects;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Structure;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    // Transactional persistence of visual XML plus Procedure-Source print-command
    // synchronization/flush helpers. Extracted from LayoutService.cs
    // (plan TECHDEBT-03). Pure move, no logic changes — see plans/README.md TECHDEBT-03.
    public partial class LayoutService
    {
        private string PersistVisualXml(KBObject obj, LayoutContextResult context, string target, string normalizedXml, string baselineXml = null, string compositionRepairToken = null, string baseVersion = null)
        {
            if (!string.IsNullOrWhiteSpace(baseVersion))
            {
                var versionContext = LoadVisualContext(obj, target,
                    context?.Surface ?? VisualSurface.Any);
                if (versionContext.Error != null) return versionContext.Error;
                string actualVersion = WriteService.ComputeContentVersionToken(
                    obj, versionContext.Document?.ToString(SaveOptions.DisableFormatting));
                if (!string.Equals(baseVersion, actualVersion, StringComparison.Ordinal))
                {
                    return Models.McpResponse.Err(
                        code: "StaleObject",
                        message: "The visual layout changed before the guarded write could be applied.",
                        hint: "Read the current layout again and retry with the new versionToken.",
                        target: target,
                        extra: new JObject
                        {
                            ["expectedVersion"] = baseVersion,
                            ["actualVersion"] = actualVersion
                        });
                }
            }

            var kb = _objectService.GetKbService().GetKB();
            if (kb == null)
            {
                return Models.McpResponse.Err(
                    code: "KbNotOpened",
                    message: "KB not opened.",
                    hint: "Open a Knowledge Base before writing visual metadata.",
                    nextSteps: new JArray(Models.McpResponse.NextStep("genexus_kb", new JObject { ["action"] = "open" }, "Opens the configured Knowledge Base.")),
                    retryAfterMs: 2000,
                    target: target);
            }

            // Snapshot pre-save EntityVersionId so the composition repair can identify rows
            // that the upcoming Save() inserts. Only meaningful for WebForm surfaces today.
            long preSaveMaxEntityVersionId = -1;
            string kbPath = null;
            if (context.Surface == VisualSurface.WebForm && !string.IsNullOrEmpty(compositionRepairToken))
            {
                try { kbPath = kb.Location; } catch { kbPath = null; }
                if (!string.IsNullOrEmpty(kbPath))
                {
                    preSaveMaxEntityVersionId = WebFormCompositionRepair.SnapshotMaxEntityVersionId(obj, kbPath);
                }
            }

            using (var transaction = kb.BeginTransaction())
            {
                try
                {
                    if (context.Surface == VisualSurface.Report)
                    {
                        if (!TryNormalizeReportPrintCommandsInSourceInMemory(obj, normalizedXml, out string normalizeError))
                        {
                            transaction.Rollback();
                            return Models.McpResponse.Err(
                                code: "LayoutMutationFailed",
                                message: "Layout mutation failed: " + normalizeError,
                                hint: "The source normalisation step failed; the transaction was rolled back.",
                                nextSteps: new JArray(Models.McpResponse.NextStep("genexus_layout", new JObject { ["action"] = "get_tree", ["name"] = target }, "Re-reads the layout to confirm the current state.")),
                                target: target);
                        }

                        if (!TryFlushSourceForLayoutMutation(obj, out string flushSourceError))
                        {
                            transaction.Rollback();
                            return Models.McpResponse.Err(
                                code: "LayoutMutationFailed",
                                message: "Layout mutation failed: " + flushSourceError,
                                hint: "The source flush step failed; the transaction was rolled back.",
                                nextSteps: new JArray(Models.McpResponse.NextStep("genexus_layout", new JObject { ["action"] = "get_tree", ["name"] = target }, "Re-reads the layout to confirm the current state.")),
                                target: target);
                        }

                        if (!ReportLayoutHelper.WriteLayout(context.VisualPart, normalizedXml, baselineXml,
                            allowEnsureSaveFallback: false))
                        {
                            transaction.Rollback();
                            return Models.McpResponse.Err(
                                code: "LayoutMutationFailed",
                                message: "Layout mutation failed: ReportLayoutHelper failed to write XML to the ReportPart.",
                                hint: "The SDK could not accept the updated XML; ensure the XML structure matches the expected report layout format.",
                                nextSteps: new JArray(Models.McpResponse.NextStep("genexus_layout", new JObject { ["action"] = "get_tree", ["name"] = target }, "Re-reads the layout to confirm the current state.")),
                                target: target);
                        }
                    }
                    else if (context.Surface == VisualSurface.WebForm)
                    {
                        WebFormXmlHelper.ApplyEditableXml(context.WebFormPart, normalizedXml);
                        try { context.WebFormPart.Save(); }
                        catch (Exception saveEx)
                        {
                            Logger.Warn($"[LAYOUT-SAVE] {context.Surface} save failed: {saveEx.Message}");
                            throw;
                        }
                    }
                    else if (context.Surface == VisualSurface.LayoutSource)
                    {
                        context.SourcePart.Source = normalizedXml;
                        try
                        {
                            var saveMethod = context.SourcePart.GetType().GetMethod("Save", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                            saveMethod?.Invoke(context.SourcePart, null);
                        }
                        catch (Exception saveEx)
                        {
                            Logger.Warn($"[LAYOUT-SAVE] {context.Surface} save failed: {saveEx.Message}");
                            throw;
                        }
                    }
                    else if (context.Surface == VisualSurface.PartXml)
                    {
                        context.VisualPart.DeserializeFromXml(normalizedXml);
                        try
                        {
                            var saveMethod = context.VisualPart.GetType().GetMethod("Save", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                            saveMethod?.Invoke(context.VisualPart, null);
                        }
                        catch (Exception saveEx)
                        {
                            Logger.Warn($"[LAYOUT-SAVE] {context.Surface} save failed: {saveEx.Message}");
                            throw;
                        }
                    }
                    else if (context.Surface == VisualSurface.PartMemberXml)
                    {
                        bool persisted = false;

                        if (!string.IsNullOrWhiteSpace(context.MemberName) && context.MemberWritable)
                        {
                            var prop = context.VisualPart.GetType().GetProperty(context.MemberName, BindingFlags.Public | BindingFlags.Instance);
                            if (prop != null && prop.CanWrite && prop.PropertyType == typeof(string))
                            {
                                prop.SetValue(context.VisualPart, normalizedXml);
                                persisted = true;
                            }
                        }

                        if (!persisted)
                        {
                            persisted = TryPersistViaSourcePath(context.VisualPart, context.MemberSourcePath, normalizedXml);
                        }

                        if (!persisted)
                        {
                            try
                            {
                                context.VisualPart.DeserializeFromXml(normalizedXml);
                                persisted = true;
                            }
                            catch (Exception deserializeEx)
                            {
                                return Models.McpResponse.Err(
                                    code: "LayoutMutationFailed",
                                    message: "Layout mutation failed: resolved visual member is not writable and DeserializeFromXml fallback failed: " + deserializeEx.Message,
                                    hint: "The visual part has no writable XML path; use a different action or inspect the surface to find a supported mutation path.",
                                    nextSteps: new JArray(Models.McpResponse.NextStep("genexus_layout", new JObject { ["action"] = "inspect_surface", ["name"] = target }, "Diagnoses available writable visual surfaces.")),
                                    target: target);
                            }
                        }

                        try
                        {
                            var saveMethod = context.VisualPart.GetType().GetMethod("Save", BindingFlags.Public | BindingFlags.Instance);
                            saveMethod?.Invoke(context.VisualPart, null);
                        }
                        catch (Exception saveEx)
                        {
                            Logger.Warn($"[LAYOUT-SAVE] {context.Surface} save failed: {saveEx.Message}");
                            throw;
                        }
                    }
                    else
                    {
                        return Models.McpResponse.Err(
                            code: "UnsupportedVisualSurface",
                            message: "Unsupported visual surface.",
                            hint: "The selected visual surface cannot be persisted; use inspect_surface to find a writable surface.",
                            nextSteps: new JArray(Models.McpResponse.NextStep("genexus_layout", new JObject { ["action"] = "inspect_surface", ["name"] = target }, "Lists available and writable visual surfaces for this object.")),
                            target: target);
                    }

                    obj.EnsureSave(true);
                    transaction.Commit();

                    Logger.Info("[CompositionRepair] post-commit gate: surface=" + context.Surface +
                                " tokenPresent=" + (!string.IsNullOrEmpty(compositionRepairToken)) +
                                " preSaveMax=" + preSaveMaxEntityVersionId +
                                " kbPathPresent=" + (!string.IsNullOrEmpty(kbPath)));
                    if (context.Surface == VisualSurface.WebForm &&
                        !string.IsNullOrEmpty(compositionRepairToken) &&
                        preSaveMaxEntityVersionId >= 0 &&
                        !string.IsNullOrEmpty(kbPath))
                    {
                        WebFormCompositionRepair.TryRepair(obj, kbPath, compositionRepairToken, preSaveMaxEntityVersionId);
                    }

                    _objectService.MarkReadCacheDirty(obj, context.PartName ?? "Layout");
                    return null;
                }
                catch (Exception ex)
                {
                    transaction.Rollback();
                    return Models.McpResponse.Err(
                        code: "LayoutMutationFailed",
                        message: "Layout mutation failed: " + ex.Message,
                        hint: "An unexpected exception occurred; the transaction was rolled back.",
                        nextSteps: new JArray(Models.McpResponse.NextStep("genexus_layout", new JObject { ["action"] = "get_tree", ["name"] = target }, "Re-reads the layout to confirm the current state.")),
                        target: target);
                }
            }
        }

        private bool TryRenamePrintCommandInSource(KBObject obj, string currentName, string newName, out string error)
        {
            error = null;
            if (obj == null)
            {
                error = "Object was not available for source synchronization.";
                return false;
            }

            string sourceJson = _objectService.ReadObjectSource(obj.Name, "Source", null, null, "mcp", false, obj.TypeDescriptor?.Name);
            JObject sourcePayload;
            try
            {
                sourcePayload = JObject.Parse(sourceJson);
            }
            catch
            {
                error = "Could not parse Source payload while renaming print block.";
                return false;
            }

            string source = sourcePayload["source"]?.ToString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(source))
            {
                error = "Procedure Source is empty; unable to rename print command.";
                return false;
            }

            string pattern = @"(?im)(^|\s)print\s+" + Regex.Escape(currentName) + @"(\s|$)";
            int replacements = 0;
            string updated = Regex.Replace(source, pattern, m =>
            {
                replacements++;
                string prefix = m.Groups[1].Value;
                string suffix = m.Groups[2].Value;
                return prefix + "print " + newName + suffix;
            });

            if (replacements == 0)
            {
                error = "No matching print command was found in Source for '" + currentName + "'.";
                return false;
            }

            return TryPersistSourceText(obj, updated, out error);
        }

        private bool TryRenamePrintCommandInSourceInMemory(KBObject obj, string currentName, string newName, out string error)
        {
            error = null;
            if (obj == null)
            {
                error = "Object was not available for source synchronization.";
                return false;
            }

            var sourcePart = PartAccessor.GetPart(obj, "Source") as ISource;
            if (sourcePart == null)
            {
                error = "Procedure Source part was not available for in-memory synchronization.";
                return false;
            }

            string source = sourcePart.Source ?? string.Empty;
            if (string.IsNullOrWhiteSpace(source))
            {
                error = "Procedure Source is empty; unable to rename print command.";
                return false;
            }

            string pattern = @"(?im)(^|\s)print\s+" + Regex.Escape(currentName) + @"(\s|$)";
            int replacements = 0;
            string updated = Regex.Replace(source, pattern, m =>
            {
                replacements++;
                string prefix = m.Groups[1].Value;
                string suffix = m.Groups[2].Value;
                return prefix + "print " + newName + suffix;
            });

            if (replacements == 0)
            {
                bool alreadyRenamed = Regex.IsMatch(
                    source,
                    @"(?im)(^|\s)print\s+" + Regex.Escape(newName) + @"(\s|$)");
                if (alreadyRenamed)
                {
                    return true;
                }

                error = "No matching print command was found in Source for '" + currentName + "'.";
                return false;
            }

            sourcePart.Source = updated;
            return true;
        }

        private bool TryInsertPrintCommandInSource(KBObject obj, string printBlockName, out string error)
        {
            error = null;
            if (obj == null)
            {
                error = "Object was not available for source synchronization.";
                return false;
            }

            string sourceJson = _objectService.ReadObjectSource(obj.Name, "Source", null, null, "mcp", false, obj.TypeDescriptor?.Name);
            JObject sourcePayload;
            try
            {
                sourcePayload = JObject.Parse(sourceJson);
            }
            catch
            {
                error = "Could not parse Source payload while inserting print command.";
                return false;
            }

            string source = sourcePayload["source"]?.ToString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(source))
            {
                error = "Procedure Source is empty; unable to insert print command.";
                return false;
            }

            if (Regex.IsMatch(source, @"(?im)(^|\s)print\s+" + Regex.Escape(printBlockName) + @"(\s|$)"))
            {
                // Source already synchronized.
                return true;
            }

            string lineEnding = source.Contains("\r\n") ? "\r\n" : "\n";
            string insertion = "print " + printBlockName;
            string updated;

            var anchor = Regex.Match(source, @"(?im)^[ \t]*print[ \t]+printblock2[ \t]*$");
            if (anchor.Success)
            {
                updated = source.Insert(anchor.Index, insertion + lineEnding);
            }
            else
            {
                var footerAnchor = Regex.Match(source, @"(?im)^[ \t]*Footer[ \t]*$");
                if (footerAnchor.Success)
                {
                    updated = source.Insert(footerAnchor.Index, insertion + lineEnding);
                }
                else
                {
                    if (!source.EndsWith(lineEnding, StringComparison.Ordinal))
                    {
                        source += lineEnding;
                    }

                    updated = source + insertion + lineEnding;
                }
            }

            return TryPersistSourceText(obj, updated, out error);
        }

        private bool TryInsertPrintCommandInSourceInMemory(KBObject obj, string printBlockName, out string error)
        {
            error = null;
            if (obj == null)
            {
                error = "Object was not available for source synchronization.";
                return false;
            }

            var sourcePart = PartAccessor.GetPart(obj, "Source") as ISource;
            if (sourcePart == null)
            {
                error = "Procedure Source part was not available for in-memory synchronization.";
                return false;
            }

            string source = sourcePart.Source ?? string.Empty;
            if (string.IsNullOrWhiteSpace(source))
            {
                error = "Procedure Source is empty; unable to insert print command.";
                return false;
            }

            if (Regex.IsMatch(source, @"(?im)(^|\s)print\s+" + Regex.Escape(printBlockName) + @"(\s|$)"))
            {
                return true;
            }

            string lineEnding = source.Contains("\r\n") ? "\r\n" : "\n";
            string insertion = "print " + printBlockName;
            string updated;

            var anchor = Regex.Match(source, @"(?im)^[ \t]*print[ \t]+printblock2[ \t]*$");
            if (anchor.Success)
            {
                updated = source.Insert(anchor.Index, insertion + lineEnding);
            }
            else
            {
                var footerAnchor = Regex.Match(source, @"(?im)^[ \t]*Footer[ \t]*$");
                if (footerAnchor.Success)
                {
                    updated = source.Insert(footerAnchor.Index, insertion + lineEnding);
                }
                else
                {
                    if (!source.EndsWith(lineEnding, StringComparison.Ordinal))
                    {
                        source += lineEnding;
                    }

                    updated = source + insertion + lineEnding;
                }
            }

            sourcePart.Source = updated;
            return true;
        }

        private bool TryRemovePrintCommandFromSourceInMemory(KBObject obj, string printBlockName, out string error)
        {
            error = null;
            if (obj == null)
            {
                error = "Object was not available for source synchronization.";
                return false;
            }

            var sourcePart = PartAccessor.GetPart(obj, "Source") as ISource;
            if (sourcePart == null)
            {
                // No Source part to synchronize — nothing to remove.
                return true;
            }

            string source = sourcePart.Source ?? string.Empty;
            if (string.IsNullOrWhiteSpace(source))
            {
                return true;
            }

            string pattern = @"(?im)^[ 	]*print[ 	]+" + Regex.Escape(printBlockName) + @"[ 	]*(?
|$)";;
            string updated = Regex.Replace(source, pattern, string.Empty);

            if (string.Equals(updated, source, StringComparison.Ordinal))
            {
                // No print command referencing this block — acceptable.
                return true;
            }

            sourcePart.Source = updated;
            return true;
        }

        private static string GetProcedureSourceSnapshot(KBObject obj)
        {
            var sourcePart = PartAccessor.GetPart(obj, "Source") as ISource;
            return sourcePart?.Source;
        }

        private bool TryRestoreProcedureSource(KBObject obj, string sourceSnapshot)
        {
            if (obj == null || sourceSnapshot == null)
            {
                return false;
            }

            var sourcePart = PartAccessor.GetPart(obj, "Source") as ISource;
            if (sourcePart == null)
            {
                return false;
            }

            try
            {
                sourcePart.Source = sourceSnapshot;
                obj.EnsureSave(false);
                _objectService.MarkReadCacheDirty(obj, "Source");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Warn("TryRestoreProcedureSource failed: " + ex.Message);
                return false;
            }
        }

        private bool TryFlushSourceForLayoutMutation(KBObject obj, out string error)
        {
            error = null;
            if (obj == null)
            {
                error = "Object was not available for source flush.";
                return false;
            }

            var sourcePart = PartAccessor.GetPart(obj, "Source") as ISource;
            if (sourcePart == null)
            {
                return true;
            }

            try
            {
                var saveMethod = sourcePart.GetType().GetMethod("Save", BindingFlags.Public | BindingFlags.Instance);
                if (saveMethod != null)
                {
                    try
                    {
                        saveMethod.Invoke(sourcePart, null);
                    }
                    catch (TargetInvocationException tiex)
                    {
                        string inner = tiex.InnerException?.Message;
                        Logger.Warn("TryFlushSourceForLayoutMutation Source.Save failed: " + (inner ?? tiex.Message));
                    }
                    catch (Exception saveEx)
                    {
                        Logger.Warn("TryFlushSourceForLayoutMutation Source.Save failed: " + saveEx.Message);
                    }
                }

                obj.EnsureSave(false);
                _objectService.MarkReadCacheDirty(obj, "Source");
                return true;
            }
            catch (Exception ex)
            {
                error = "Failed to flush Procedure Source before report mutation: " + ex.Message;
                return false;
            }
        }

        private static bool TrySaveVisualPart(KBObjectPart visualPart, out string error)
        {
            error = null;
            if (visualPart == null)
            {
                error = "Visual part was not available for save.";
                return false;
            }

            try
            {
                visualPart.Save();
                return true;
            }
            catch (Exception ex)
            {
                error = "Visual part save failed after staging layout mutation: " + ex.Message;
                return false;
            }
        }

        private bool TryNormalizeReportPrintCommandsInSourceInMemory(KBObject obj, string reportXml, out string error)
        {
            error = null;
            if (obj == null)
            {
                error = "Object was not available for source normalization.";
                return false;
            }

            var sourcePart = PartAccessor.GetPart(obj, "Source") as ISource;
            if (sourcePart == null)
            {
                // Some report procedures may not expose editable Source in this context.
                return true;
            }

            string source = sourcePart.Source ?? string.Empty;
            if (string.IsNullOrWhiteSpace(source))
            {
                return true;
            }

            XDocument doc;
            try
            {
                doc = XDocument.Parse(reportXml);
            }
            catch
            {
                // If report xml cannot be parsed here, keep source untouched.
                return true;
            }

            var canonicalByLower = doc
                .Descendants("PrintBlock")
                .Select(pb => Attr(pb, "Name") ?? Attr(pb, "ControlName"))
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToDictionary(n => n.ToLowerInvariant(), n => n, StringComparer.OrdinalIgnoreCase);

            if (canonicalByLower.Count == 0)
            {
                return true;
            }

            bool changed = false;
            string normalized = Regex.Replace(
                source,
                @"(?im)^(?<indent>\s*)print\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*(?<tail>(//.*)?)$",
                m =>
                {
                    string original = m.Groups["name"].Value;
                    string lower = original.ToLowerInvariant();
                    if (!canonicalByLower.TryGetValue(lower, out string canonical))
                    {
                        if (lower.EndsWith("_mcp", StringComparison.OrdinalIgnoreCase))
                        {
                            string baseName = original.Substring(0, original.Length - 4);
                            canonicalByLower.TryGetValue(baseName.ToLowerInvariant(), out canonical);
                        }
                    }

                    if (string.IsNullOrWhiteSpace(canonical))
                    {
                        if (lower.StartsWith("printblock", StringComparison.OrdinalIgnoreCase))
                        {
                            changed = true;
                            return string.Empty;
                        }

                        return m.Value;
                    }

                    if (string.Equals(original, canonical, StringComparison.Ordinal))
                    {
                        return m.Value;
                    }

                    changed = true;
                    return $"{m.Groups["indent"].Value}print {canonical}{m.Groups["tail"].Value}";
                });

            if (changed)
            {
                sourcePart.Source = normalized;
            }

            return true;
        }

        private bool TryPersistSourceText(KBObject obj, string sourceText, out string error)
        {
            error = null;
            string tempPath = null;
            try
            {
                tempPath = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(),
                    "gxmcp-layout-source-" + Guid.NewGuid().ToString("N") + ".txt");
                System.IO.File.WriteAllText(tempPath, sourceText ?? string.Empty);

                string importResult = _objectService.ImportObjectFromText(
                    obj.Name,
                    tempPath,
                    "Source",
                    obj.TypeDescriptor?.Name);

                JObject parsed;
                try
                {
                    parsed = JObject.Parse(importResult);
                }
                catch
                {
                    error = "Source import returned an invalid payload.";
                    return false;
                }

                string status = parsed["status"]?.ToString();
                if (!string.Equals(status, "Success", StringComparison.OrdinalIgnoreCase))
                {
                    error = parsed["error"]?.ToString() ?? parsed["details"]?.ToString() ?? "Source import failed.";
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
            finally
            {
                if (!string.IsNullOrWhiteSpace(tempPath))
                {
                    try { System.IO.File.Delete(tempPath); } catch { }
                }
            }
        }
    }
}
