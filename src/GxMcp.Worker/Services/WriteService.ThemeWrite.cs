using System;
using Newtonsoft.Json.Linq;
using Artech.Architecture.Common.Objects;
using GxMcp.Worker.Helpers;

namespace GxMcp.Worker.Services
{
    // Theme/StyleSheet writes use the SDK's own CSS/class model. The concrete
    // part types differ between GeneXus releases, so ThemeStyleEditHelper owns
    // the small reflection bridge while this file owns transactions and MCP
    // response semantics.
    public partial class WriteService
    {
        private string WriteThemeStylePart(
            KBObject obj,
            string target,
            string partName,
            object stylePart,
            string content,
            bool dryRun,
            bool forceWrite)
        {
            if (stylePart == null)
            {
                return CreateThemeWriteError(
                    target,
                    partName,
                    "Theme or StyleSheet part was not found on the object.",
                    "ThemeStylePartNotFound");
            }

            string requestError = ThemeStyleEditHelper.ValidateRequest(content);
            if (requestError != null)
            {
                return CreateThemeWriteError(target, partName, requestError, "ThemeStyleValidationFailed");
            }

            string before = ThemeStyleEditHelper.ReadText(stylePart) ?? string.Empty;
            var kb = _objectService.GetKbService().GetKB();
            if (kb == null)
            {
                return CreateThemeWriteError(
                    target,
                    partName,
                    "Open a Knowledge Base before writing Theme or StyleSheet metadata.",
                    "KbNotOpened");
            }

            using (var transaction = kb.BeginTransaction())
            {
                try
                {
                    if (!ThemeStyleEditHelper.TryApply(stylePart, content, out JObject details, out string applyError, ImageObjectExists))
                    {
                        transaction.Rollback();
                        return CreateThemeWriteError(target, partName, applyError, ThemeStyleEditHelper.FailureCode(details), details);
                    }

                    string staged = ThemeStyleEditHelper.ReadText(stylePart) ?? string.Empty;
                    bool changed = !string.Equals(before, staged, StringComparison.Ordinal);

                    if (dryRun)
                    {
                        transaction.Rollback();
                        details["beforeLength"] = before.Length;
                        details["afterLength"] = staged.Length;
                        details["changed"] = changed;
                        details["savePathExercised"] = false;
                        details["verified"] = new JArray("styleSyntax", "sdkStyleMutation", "transactionRollback");
                        return Models.McpResponse.Ok(target: target, code: "WriteDryRun", result: details);
                    }

                    if (!changed && !forceWrite)
                    {
                        transaction.Rollback();
                        details["beforeLength"] = before.Length;
                        details["afterLength"] = staged.Length;
                        details["changed"] = false;
                        details["details"] = "No style change detected.";
                        return Models.McpResponse.Ok(target: target, code: "WriteNoChange", result: details);
                    }

                    ForceSaveThemeObject(obj);
                    transaction.Commit();
                    ScheduleFlush(force: true);
                    NotePerTargetWrite(target);
                    _objectService.MarkReadCacheDirty(obj, partName);

                    string persisted = ReadPersistedThemeText(target, partName, obj.TypeDescriptor?.Name);
                    bool verified = persisted != null;
                    if (verified && !string.Equals(staged, persisted, StringComparison.Ordinal))
                    {
                        return CreateThemeWriteError(
                            target,
                            partName,
                            "The SDK save completed, but the persisted Theme/StyleSheet source differs from the staged content.",
                            "ThemeStyleVerificationFailed",
                            details,
                            extra: new JObject
                            {
                                ["stagedLength"] = staged.Length,
                                ["persistedLength"] = persisted.Length,
                                ["stagedHash"] = ComputeContentFingerprint(staged),
                                ["persistedHash"] = ComputeContentFingerprint(persisted),
                                ["persisted"] = true
                            });
                    }

                    details["beforeLength"] = before.Length;
                    details["afterLength"] = (persisted ?? staged).Length;
                    details["changed"] = changed || forceWrite;
                    details["persisted"] = true;
                    details["verified"] = verified;
                    details["savePathExercised"] = true;
                    return Models.McpResponse.Ok(target: target, code: "WriteApplied", result: details);
                }
                catch (Exception ex)
                {
                    try { transaction?.Rollback(); }
                    catch (Exception rollbackEx) { Logger.Debug("[WRITE] Theme transaction rollback failed: " + rollbackEx.Message); }
                    return CreateThemeWriteError(
                        target,
                        partName,
                        FormatExceptionChain(ex),
                        "ThemeStyleWriteFailed");
                }
            }
        }

        // Consulted only after the SDK refuses a style source, to name the gx-image() references that point at no Image
        // object. null means the lookup could not tell, which keeps the name out of the answer.
        private bool? ImageObjectExists(string name)
        {
            try { return _objectService.FindObject(name, "Image") != null; }
            catch (Exception ex)
            {
                Logger.Debug("[THEME-WRITE] image lookup skipped for '" + name + "': " + ex.Message);
                return null;
            }
        }

        private static void ForceSaveThemeObject(KBObject obj)
        {
            try
            {
                obj.Save(new KBObjectSavePreferences
                {
                    ForceSave = true,
                    ForceSaveDefaultParts = true,
                    SkipValidation = true
                });
            }
            catch
            {
                obj.EnsureSave(true);
            }
        }

        private string ReadPersistedThemeText(string target, string partName, string typeName)
        {
            try
            {
                var refreshed = _objectService.FindObject(target, typeName);
                if (refreshed == null) return null;
                var part = GxMcp.Worker.Structure.PartAccessor.GetPart(refreshed, partName);
                return ThemeStyleEditHelper.ReadText(part);
            }
            catch (Exception ex)
            {
                Logger.Debug("[THEME-WRITE] persisted read skipped: " + ex.Message);
                return null;
            }
        }

        private string CreateThemeWriteError(
            string target,
            string partName,
            string details,
            string code,
            JObject styleDetails = null,
            JObject extra = null)
        {
            var payload = extra ?? new JObject();
            payload["part"] = partName;
            if (!string.IsNullOrWhiteSpace(details))
                payload["details"] = details;
            if (styleDetails != null)
            {
                foreach (var property in styleDetails.Properties())
                    payload[property.Name] = property.Value.DeepClone();
            }

            ThemeStyleEditHelper.DescribeFailure(code, styleDetails, out string message, out string hint);
            return Models.McpResponse.Err(
                code: code,
                message: message,
                hint: hint,
                target: target,
                extra: payload);
        }
    }
}
