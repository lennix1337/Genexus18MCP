using System;
using Newtonsoft.Json.Linq;
using Artech.Architecture.Common.Objects;
using Artech.Genexus.Common.Parts;
using GxMcp.Worker.Helpers;

namespace GxMcp.Worker.Services
{
    // Issue #963: the WinForm part of a WorkPanel is written by running the importer an XPZ import
    // runs (KMW.WinForm.Deserialize) over the submitted Part XML inside a KB transaction, saving
    // the object, then reading the part back from a fresh instance. A dry run validates the XML
    // only: the importer mutates the live part, so running it would not be side-effect free.
    public partial class WriteService
    {
        internal static bool IsWinFormPart(string partName)
            => string.Equals(partName, "WinForm", StringComparison.OrdinalIgnoreCase);

        private string WriteWinFormPart(KBObject obj, string target, string xml, bool dryRun, bool forceWrite)
        {
            var part = WinFormPartXml.Find(obj);
            if (part == null)
                return CreateWriteError("WinForm part not found", target, "WinForm",
                    "The object does not expose a WinForm part.", obj);

            string normalized;
            try { normalized = WinFormPartXml.Validate(xml); }
            catch (FormatException ex)
            {
                return CreateWriteError("Invalid WinForm XML", target, "WinForm", ex.Message, obj, code: "WinFormXmlInvalid");
            }
            int expectedElements = WinFormPartXml.CountElements(normalized);

            if (dryRun)
            {
                return Models.McpResponse.Ok(target: target, code: "WriteDryRun", result: new JObject
                {
                    ["part"] = "WinForm",
                    ["versionToken"] = ComputeVersionToken(obj),
                    ["elementCount"] = expectedElements,
                    ["validationScope"] = "winform-xml",
                    ["details"] = "The WinForm XML is well-formed and carries a Form the SDK importer accepts; the SDK import and save were skipped.",
                    ["verified"] = new JArray("inputReceived", "partExists", "winFormXml"),
                    ["savePathExercised"] = false,
                    ["persisted"] = false
                });
            }

            var kb = _objectService.GetKbService().GetKB();
            if (kb == null)
                return CreateWriteError("Knowledge Base not opened", target, "WinForm",
                    "Open a Knowledge Base before writing the WinForm part.", obj, code: "KbNotOpened");

            string before = WinFormPartXml.Serialize(part);
            string restorePoint = before;
            using (var transaction = kb.BeginTransaction())
            {
                try
                {
                    WinFormPartXml.Apply(part, normalized);
                    string staged = WinFormPartXml.Serialize(part);

                    if (WinFormPartXml.CountElements(staged) != expectedElements)
                    {
                        transaction.Rollback();
                        RestoreWinFormPart(part, restorePoint);
                        return CreateWriteError("WinForm not applied", target, "WinForm",
                            "The SDK importer kept " + WinFormPartXml.CountElements(staged) + " of the " + expectedElements
                            + " submitted controls; nothing was saved.", obj, code: "WinFormNotApplied");
                    }

                    if (!forceWrite && string.Equals(before, staged, StringComparison.Ordinal))
                    {
                        transaction.Rollback();
                        return Models.McpResponse.Ok(target: target, code: "WriteNoChange", result: new JObject
                        {
                            ["part"] = "WinForm",
                            ["changed"] = false,
                            [WinFormPartXml.CanonicalContentField] = staged,
                            ["details"] = "The WinForm already holds this form."
                        });
                    }

                    ForceSaveThemeObject(obj);
                    transaction.Commit();
                    ScheduleFlush(force: true);
                    NotePerTargetWrite(target);
                    _objectService.MarkReadCacheDirty(obj, "WinForm");

                    var result = new JObject
                    {
                        ["part"] = "WinForm",
                        ["changed"] = true,
                        ["elementCount"] = expectedElements,
                        [WinFormPartXml.CanonicalContentField] = staged,
                        ["persisted"] = true,
                        ["savePathExercised"] = true
                    };

                    var fresh = _objectService.FindObjectFreshByIdentity(obj);
                    var freshPart = fresh == null ? null : WinFormPartXml.Find(fresh);
                    if (freshPart == null)
                    {
                        result["verified"] = false;
                        result["verification"] = "unavailable";
                        result["verificationNote"] = _objectService.GetLastResolutionDiagnostic()?["message"]?.ToString()
                            ?? "A fresh copy of the object could not be loaded to compare the persisted WinForm.";
                        return Models.McpResponse.Ok(target: target, code: "WriteApplied", result: result);
                    }

                    string persisted = WinFormPartXml.Serialize(freshPart);
                    if (!string.Equals(staged, persisted, StringComparison.Ordinal))
                    {
                        return CreateWriteError("WinForm not persisted", target, "WinForm",
                            "The save completed, but a fresh read of the part differs from what was staged.", obj,
                            code: "WriteNotPersisted");
                    }

                    result["verified"] = true;
                    result["verification"] = "fresh-reload";
                    return Models.McpResponse.Ok(target: target, code: "WriteApplied", result: result);
                }
                catch (Exception ex)
                {
                    try { transaction.Rollback(); }
                    catch (Exception rollbackEx) { Logger.Debug("[WRITE] WinForm transaction rollback failed: " + rollbackEx.Message); }
                    RestoreWinFormPart(part, restorePoint);
                    return CreateWriteError("WinForm write failed", target, "WinForm", FormatExceptionChain(ex), obj, code: "WinFormWriteFailed");
                }
            }
        }

        // The importer mutates the live part, which a transaction rollback does not undo; putting the
        // prior form back keeps a later unrelated save of the object from persisting the failed one.
        private static void RestoreWinFormPart(WinFormPart part, string restorePoint)
        {
            try
            {
                WinFormPartXml.Apply(part, WinFormPartXml.Validate(restorePoint));
                part.Dirty = false;
            }
            catch (Exception ex)
            {
                Logger.Warn("[WINFORM-WRITE] restoring the prior form failed: " + ex.Message);
            }
        }
    }
}
