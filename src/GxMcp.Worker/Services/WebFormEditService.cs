using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// Item 19 (mcp-improvements-2026-05-22) — semantic WebForm edits.
    ///
    /// Action-based mutations on the WebForm XML tree, layered over the existing
    /// WriteService visual-write path so descriptor-name auto-routing (gxButton
    /// OnClickEvent → Event etc.) still applies. Three high-frequency actions are
    /// implemented in this batch:
    ///
    ///   - add_textblock   { name, target, parent?, caption, format?, position? }
    ///   - add_button      { name, target, parent?, caption, event?, controlId? }
    ///   - set_visibility  { name, controlId, visible }
    ///   - remove_control  { name, controlId }
    ///   - wrap_in_fieldset{ name, controlIds[], legend? }
    ///
    /// XML mutation is pure (testable without SDK). Persistence routes through
    /// the existing WebForm write pipeline (LayoutGotchaScanner / typed-property
    /// fixup) so all the guard-rails apply.
    /// </summary>
    public class WebFormEditService
    {
        public interface IWebFormBackend
        {
            string ReadWebFormXml(string target);
            string WriteWebFormXml(string target, string xml, bool dryRun, JObject options);
        }

        private sealed class DefaultBackend : IWebFormBackend
        {
            private readonly ObjectService _obj;
            private readonly WriteService _write;
            public DefaultBackend(ObjectService obj, WriteService write)
            {
                _obj = obj;
                _write = write;
            }
            public string ReadWebFormXml(string target) =>
                _obj.ReadObjectSourceForVerification(target, "WebForm");
            public string WriteWebFormXml(string target, string xml, bool dryRun, JObject options)
            {
                var writeArgs = new JObject
                {
                    ["part"] = "WebForm",
                    ["mode"] = "full",
                    ["content"] = xml,
                    ["validate"] = true,
                    ["autoInjectVariables"] = true,
                    ["strictVerify"] = true,
                    ["dryRun"] = dryRun
                };
                // WebFormEditService performs the compensating write after an
                // independent identity/version check. The generic writer's rollback
                // path consumes a post-save read token and can otherwise restore over
                // a newer edit that landed after this write.
                writeArgs["rollbackOnFailure"] = false;
                if (options?["baseVersion"] != null)
                    writeArgs["baseVersion"] = options["baseVersion"];
                return _write.WriteObject(target, writeArgs);
            }
        }

        private readonly IWebFormBackend _backend;

        public WebFormEditService(ObjectService objectService, WriteService writeService)
            : this(new DefaultBackend(objectService, writeService)) { }

        public WebFormEditService(IWebFormBackend backend)
        {
            _backend = backend;
        }

        public string Execute(string action, JObject args)
        {
            if (string.IsNullOrWhiteSpace(action))
                return Err("MissingAction", "action is required.");
            if (args == null) args = new JObject();

            string target = args["name"]?.ToString() ?? args["target"]?.ToString();
            if (string.IsNullOrWhiteSpace(target))
                return Err("MissingName", "name (or target) is required.");

            bool dryRun = args["dryRun"]?.ToObject<bool?>() ?? false;
            bool rollbackRequested = args["rollbackOnFailure"]?.ToObject<bool?>() ?? true;
            var backendOptions = (JObject)args.DeepClone();
            backendOptions["rollbackOnFailure"] = rollbackRequested;

            var t0 = DateTime.UtcNow;
            string xmlBefore;
            try
            {
                string read = _backend.ReadWebFormXml(target);
                if (!TryReadCompleteWebFormXml(
                    read, out xmlBefore, out string _, out bool truncated, out JObject readEvidence))
                {
                    return IncompleteWebFormReadError(
                        target,
                        truncated,
                        truncated
                            ? "The WebForm baseline read was truncated; the edit was not started."
                            : "A complete WebForm baseline could not be read; the edit was not started.",
                        readEvidence);
                }
            }
            catch (Exception ex)
            {
                return IncompleteWebFormReadError(
                    target,
                    truncated: false,
                    "Failed to obtain a complete WebForm baseline: " + ex.Message,
                    new JObject
                    {
                        ["readComplete"] = false,
                        ["errorCode"] = ex.GetType().Name,
                        ["errorMessage"] = ex.Message,
                        ["readOptions"] = new JObject { ["offset"] = 0, ["limit"] = 0 }
                    });
            }
            if (string.IsNullOrWhiteSpace(xmlBefore))
                return Err("EmptyWebForm", "The object does not expose a WebForm part, or it is empty.");

            string xmlAfter;
            var warnings = new List<string>();
            var controlsAdded = new List<string>();
            try
            {
                xmlAfter = ApplyAction(xmlBefore, action, args, warnings, controlsAdded);
            }
            catch (ArgumentException aex)
            {
                return Err("InvalidArgs", aex.Message);
            }
            catch (InvalidOperationException iex)
            {
                return Err("ActionFailed", iex.Message);
            }
            catch (Exception ex)
            {
                return Err("MutationFailed", ex.Message);
            }

            if (string.Equals(xmlBefore, xmlAfter, StringComparison.Ordinal))
            {
                return McpResponse.Ok(target: target, code: "NoChange", result: new JObject
                {
                    ["action"] = action,
                    ["warnings"] = JArray.FromObject(warnings)
                });
            }

            if (!dryRun && WriteService.WasTargetWrittenSince(target, t0))
            {
                return Err("StaleWrite", "The WebForm was modified by another write between this read and write. Re-read and retry.");
            }

            JObject writeObj;
            try
            {
                string writeResult = _backend.WriteWebFormXml(target, xmlAfter, dryRun, backendOptions);
                try { writeObj = JObject.Parse(writeResult ?? "{}"); }
                catch { writeObj = new JObject { ["raw"] = writeResult }; }
            }
            catch (Exception ex)
            {
                writeObj = new JObject
                {
                    ["status"] = "error",
                    ["code"] = "WebFormWriterException",
                    ["error"] = new JObject
                    {
                        ["code"] = "WebFormWriterException",
                        ["message"] = ex.Message
                    },
                    ["saveAttempted"] = !dryRun,
                    ["persistenceState"] = dryRun ? "NotPersisted" : "Indeterminate"
                };
            }
            if (dryRun) writeObj = CompactDryRunWriteResult(writeObj);

            string writeStatus = writeObj["status"]?.ToString() ?? string.Empty;
            bool writeOk = string.Equals(writeStatus, "ok", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(writeStatus, "Success", StringComparison.OrdinalIgnoreCase);

            var resultPayload = new JObject
            {
                ["action"] = action,
                ["controlsAdded"] = JArray.FromObject(controlsAdded),
                ["warnings"] = JArray.FromObject(warnings),
                ["xmlBeforeAfterDiff"] = BuildShortDiff(xmlBefore, xmlAfter),
                ["write"] = writeObj
            };

            if (!writeOk)
            {
                return HandleWriteFailure(
                    target, xmlBefore, xmlAfter, backendOptions,
                    rollbackRequested, dryRun, writeObj, resultPayload);
            }

            if (!dryRun)
            {
                if (!TryReadCurrentWebForm(
                    target, out string readBack, out string readBackVersion,
                    out bool readBackTruncated, out JObject readBackEvidence))
                {
                    JObject rollbackEvidence = NewRollbackEvidence();
                    string attemptedVersion = ExtractWriteVersionToken(writeObj);
                    if (!string.IsNullOrWhiteSpace(attemptedVersion))
                        rollbackEvidence["baseVersion"] = attemptedVersion;
                    rollbackEvidence["reason"] = readBackTruncated ? "ReadBackTruncated" : "ReadBackUnavailable";
                    bool rollbackSucceeded = false;
                    bool reconciliationRequired = !rollbackSucceeded;
                    bool reReadRequired = readBackEvidence?["readComplete"]?.ToObject<bool?>() != true;
                    var extra = BuildWriteRecoveryEvidence(
                        true,
                        false,
                        rollbackRequested,
                        rollbackEvidence,
                        rollbackSucceeded,
                        reReadRequired,
                        reconciliationRequired,
                        readBackEvidence);
                    extra["write"] = writeObj;
                    return McpResponse.Err(
                        code: "WebFormReadbackUnavailable",
                        message: "The WebForm write returned success, but a complete independent read-back was unavailable.",
                        target: target,
                        extra: extra,
                        reconciliationRequired: reconciliationRequired);
                }

                bool equivalent = false;
                string readBackDiff = null;
                try { equivalent = XmlEquivalence.AreEquivalent(xmlAfter, readBack, out readBackDiff); }
                catch (Exception ex) { readBackDiff = ex.Message; }
                if (string.IsNullOrWhiteSpace(readBack))
                {
                    equivalent = false;
                    readBackDiff = "The read-back contained no WebForm XML.";
                }
                if (!equivalent || writeObj["verified"]?.ToObject<bool?>() == false)
                {
                    JObject rollbackEvidence = NewRollbackEvidence();
                    string attemptedVersion = ExtractWriteVersionToken(writeObj);
                    if (!string.IsNullOrWhiteSpace(attemptedVersion))
                        rollbackEvidence["baseVersion"] = attemptedVersion;
                    if (!equivalent)
                        rollbackEvidence["reason"] = "CurrentStateDiverged";
                    bool rollbackSucceeded = false;
                    if (equivalent)
                    {
                        rollbackSucceeded = rollbackRequested
                            && TryRestoreBaseline(
                                target, xmlBefore, backendOptions, writeObj, readBack, out rollbackEvidence);
                    }
                    bool reconciliationRequired = !rollbackSucceeded;
                    var extra = BuildWriteRecoveryEvidence(
                        true,
                        false,
                        rollbackRequested,
                        rollbackEvidence,
                        rollbackSucceeded,
                        false,
                        reconciliationRequired,
                        new JObject { ["readComplete"] = true, ["versionToken"] = readBackVersion });
                    extra["diff"] = readBackDiff;
                    if (!equivalent) extra["persistenceState"] = "Divergent";
                    extra["write"] = writeObj;
                    return McpResponse.Err(
                        code: "WebFormWriteVerificationFailed",
                        message: "The WebForm write returned success, but the independent read-back did not match the requested XML.",
                        target: target,
                        extra: extra,
                        reconciliationRequired: reconciliationRequired);
                }
                resultPayload["persisted"] = true;
                resultPayload["verified"] = true;
                resultPayload["rereadConfirmed"] = true;
            }

            string editCode = dryRun ? "DryRun" : "WebFormEdited";
            return McpResponse.Ok(target: target, code: editCode, result: resultPayload);
        }

        private string HandleWriteFailure(
            string target,
            string baselineXml,
            string requestedXml,
            JObject writeOptions,
            bool rollbackRequested,
            bool dryRun,
            JObject writeResponse,
            JObject resultPayload)
        {
            string writeMessage = writeResponse?["error"]?["message"]?.ToString()
                ?? writeResponse?["error"]?.ToString()
                ?? writeResponse?["message"]?.ToString()
                ?? "WebForm write failed.";

            if (dryRun)
            {
                resultPayload["persisted"] = false;
                resultPayload["persistenceState"] = "NotPersisted";
                resultPayload["verified"] = false;
                return McpResponse.Err(
                    code: "WebFormEditFailed",
                    message: writeMessage,
                    target: target,
                    extra: resultPayload,
                    reconciliationRequired: false);
            }

            var recoveryAnchor = (JObject)(writeResponse?.DeepClone() ?? new JObject());
            recoveryAnchor["saveAttempted"] = true;
            recoveryAnchor["persistenceState"] = recoveryAnchor["persistenceState"] ?? "Indeterminate";

            bool readAvailable = TryReadCurrentWebForm(
                target, out string currentXml, out string currentVersion,
                out bool readTruncated, out JObject readEvidence);
            if (readAvailable && !string.IsNullOrWhiteSpace(currentVersion))
                recoveryAnchor["observedVersion"] = currentVersion;

            bool currentIsBaseline = false;
            bool currentIsRequested = false;
            string currentDiff = null;
            if (readAvailable)
            {
                try
                {
                    currentIsBaseline = XmlEquivalence.AreEquivalent(
                        baselineXml, currentXml, out string baselineDiff);
                    if (!currentIsBaseline) currentDiff = baselineDiff;
                    if (!currentIsBaseline)
                    {
                        currentIsRequested = XmlEquivalence.AreEquivalent(
                            requestedXml, currentXml, out string requestedDiff);
                        if (!currentIsRequested) currentDiff = requestedDiff;
                    }
                }
                catch (Exception ex)
                {
                    currentDiff = ex.Message;
                }
            }

            JObject rollbackEvidence = NewRollbackEvidence();
            string attemptedVersion = ExtractWriteVersionToken(writeResponse);
            if (!string.IsNullOrWhiteSpace(attemptedVersion))
                rollbackEvidence["baseVersion"] = attemptedVersion;
            if (readAvailable && !string.IsNullOrWhiteSpace(currentVersion))
                rollbackEvidence["observedVersion"] = currentVersion;
            bool rollbackSucceeded = false;
            if (currentIsBaseline)
            {
                rollbackEvidence["attempted"] = false;
                rollbackEvidence["succeeded"] = true;
                rollbackEvidence["deferred"] = false;
                rollbackEvidence["reason"] = "BaselineAlreadyPresent";
                rollbackSucceeded = true;
            }
            else if (rollbackRequested && currentIsRequested)
            {
                rollbackSucceeded = TryRestoreBaseline(
                    target, baselineXml, writeOptions, recoveryAnchor, currentXml, out rollbackEvidence);
            }
            else if (rollbackRequested)
            {
                rollbackEvidence["reason"] = readAvailable
                    ? "CurrentStateDiverged"
                    : string.IsNullOrWhiteSpace(attemptedVersion)
                        ? "VersionTokenUnavailable"
                        : "ReadBackUnavailable";
            }

            bool rollbackFenceDiverged =
                string.Equals(rollbackEvidence["reason"]?.ToString(), "CurrentVersionDiverged", StringComparison.Ordinal)
                || string.Equals(rollbackEvidence["reason"]?.ToString(), "CurrentStateDiverged", StringComparison.Ordinal)
                || string.Equals(rollbackEvidence["reason"]?.ToString(), "BaselineMismatch", StringComparison.Ordinal)
                || string.Equals(rollbackEvidence["reason"]?.ToString(), "BaselineComparisonFailed", StringComparison.Ordinal);
            bool reReadRequired = !readAvailable || readTruncated;
            bool reconciliationRequired;
            bool recoveryRequired;
            bool? persisted;
            bool verified = false;
            string persistenceState;
            string code = "WebFormEditFailed";
            string message = writeMessage;

            if (rollbackSucceeded)
            {
                reconciliationRequired = false;
                recoveryRequired = false;
                persisted = false;
                persistenceState = rollbackEvidence?["attempted"]?.ToObject<bool>() == true
                    ? "Restored"
                    : "NotPersisted";
            }
            else if (currentIsBaseline)
            {
                reconciliationRequired = false;
                recoveryRequired = false;
                persisted = false;
                persistenceState = "NotPersisted";
            }
            else if (currentIsRequested && !rollbackFenceDiverged)
            {
                reconciliationRequired = rollbackRequested;
                recoveryRequired = rollbackRequested;
                persisted = true;
                verified = true;
                persistenceState = "Verified";
            }
            else
            {
                reconciliationRequired = true;
                recoveryRequired = true;
                bool writerCommitted = writeResponse?["persisted"]?.ToObject<bool?>() == true
                    || string.Equals(writeResponse?["commitState"]?.ToString(), "Committed", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(writeResponse?["result"]?["persisted"]?.ToString(), "True", StringComparison.OrdinalIgnoreCase);
                persisted = readAvailable || writerCommitted ? (bool?)true : null;
                persistenceState = readAvailable ? "Divergent" : writerCommitted ? "CommittedUnverified" : "Indeterminate";
                code = "WebFormWriteIndeterminate";
                message = writeMessage
                    + " The persisted state could not be reconciled to either the baseline or the requested XML.";
            }

            var extra = BuildWriteRecoveryEvidence(
                persisted,
                verified,
                rollbackRequested,
                rollbackEvidence,
                rollbackSucceeded,
                reReadRequired,
                reconciliationRequired,
                readEvidence);
            extra["persistenceState"] = persistenceState;
            extra["recoveryRequired"] = recoveryRequired;
            extra["currentDiff"] = currentDiff;
            foreach (var property in resultPayload.Properties())
            {
                if (extra[property.Name] == null)
                    extra[property.Name] = property.Value?.DeepClone();
            }

            return McpResponse.Err(
                code: code,
                message: message,
                hint: reconciliationRequired
                    ? "Do not retry the edit. Read the complete WebForm and record a recovery decision before another write."
                    : null,
                target: target,
                extra: extra,
                reconciliationRequired: reconciliationRequired);
        }

        private bool TryReadCurrentWebForm(
            string target,
            out string xml,
            out string versionToken,
            out bool truncated,
            out JObject evidence)
        {
            xml = null;
            versionToken = null;
            truncated = false;
            try
            {
                return TryReadCompleteWebFormXml(
                    _backend.ReadWebFormXml(target), out xml, out versionToken, out truncated, out evidence);
            }
            catch (Exception ex)
            {
                evidence = new JObject
                {
                    ["readComplete"] = false,
                    ["errorCode"] = ex.GetType().Name,
                    ["errorMessage"] = ex.Message,
                    ["readOptions"] = new JObject { ["offset"] = 0, ["limit"] = 0 }
                };
                return false;
            }
        }

        private static string ExtractWriteVersionToken(JObject response)
        {
            if (response == null) return null;
            JObject result = response["result"] as JObject;
            JObject error = response["error"] as JObject;
            string[] tokens =
            {
                response["writeVersionToken"]?.ToString(),
                response["attemptedVersionToken"]?.ToString(),
                response["persistedVersionToken"]?.ToString(),
                response["postSaveVerification"]?["versionToken"]?.ToString(),
                response["verification"]?["versionToken"]?.ToString(),
                response["versionToken"]?.ToString(),
                result?["writeVersionToken"]?.ToString(),
                result?["attemptedVersionToken"]?.ToString(),
                result?["persistedVersionToken"]?.ToString(),
                result?["postSaveVerification"]?["versionToken"]?.ToString(),
                result?["verification"]?["versionToken"]?.ToString(),
                result?["versionToken"]?.ToString(),
                error?["postSaveVerification"]?["versionToken"]?.ToString(),
                error?["verification"]?["versionToken"]?.ToString(),
                error?["versionToken"]?.ToString()
            };
            return tokens.FirstOrDefault(token => !string.IsNullOrWhiteSpace(token));
        }

        private static JObject NewRollbackEvidence()
        {
            return new JObject
            {
                ["attempted"] = false,
                ["succeeded"] = false,
                ["deferred"] = true
            };
        }

        private static JObject BuildWriteRecoveryEvidence(
            bool? persisted,
            bool verified,
            bool rollbackRequested,
            JObject rollbackEvidence,
            bool rollbackSucceeded,
            bool reReadRequired,
            bool reconciliationRequired,
            JObject readEvidence)
        {
            bool rollbackReadConfirmed = rollbackEvidence?["readEvidence"]?["readComplete"]?.ToObject<bool?>() == true;
            bool readConfirmed = readEvidence?["readComplete"]?.ToObject<bool?>() == true
                || rollbackReadConfirmed;
            bool finalReReadRequired = reReadRequired && !readConfirmed;
            return new JObject
            {
                ["persisted"] = persisted.HasValue ? JToken.FromObject(persisted.Value) : JValue.CreateNull(),
                ["persistenceState"] = rollbackSucceeded
                    ? "Restored"
                    : persisted.HasValue
                        ? verified ? "Verified" : persisted.Value ? "CommittedUnverified" : "NotPersisted"
                        : "Indeterminate",
                ["verified"] = verified,
                ["rereadConfirmed"] = readConfirmed,
                ["readBackRequired"] = finalReReadRequired,
                ["reReadRequired"] = finalReReadRequired,
                ["readEvidence"] = readEvidence ?? new JObject(),
                ["rollbackRequested"] = rollbackRequested,
                ["rollbackAttempted"] = rollbackEvidence?["attempted"]?.ToObject<bool>() ?? false,
                ["rollbackSucceeded"] = rollbackSucceeded,
                ["rollbackDeferred"] = rollbackRequested && !rollbackSucceeded,
                ["rollbackEvidence"] = rollbackEvidence ?? new JObject(),
                ["recoveryRequired"] = reconciliationRequired,
                ["reconciliationRequired"] = reconciliationRequired,
                ["stateRestored"] = rollbackSucceeded
            };
        }

        private bool TryRestoreBaseline(
            string target,
            string baselineXml,
            JObject writeOptions,
            JObject writeResponse,
            string expectedCurrentXml,
            out JObject evidence)
        {
            evidence = NewRollbackEvidence();
            string version = ExtractWriteVersionToken(writeResponse);
            if (string.IsNullOrWhiteSpace(version))
            {
                evidence["reason"] = "VersionTokenUnavailable";
                return false;
            }
            if (string.IsNullOrWhiteSpace(expectedCurrentXml))
            {
                evidence["reason"] = "WriteStateUnproven";
                evidence["baseVersion"] = version;
                return false;
            }
            evidence["baseVersion"] = version;

            // Re-read immediately before the compensating write. The version captured
            // from the failed write is the fence; a version from this fresh read could
            // belong to an independent edit and must never authorize a restore.
            if (!TryReadCurrentWebForm(
                target, out string currentXml, out string currentVersion,
                out bool preReadTruncated, out JObject preReadEvidence))
            {
                evidence["reason"] = preReadTruncated ? "ReadBackTruncated" : "ReadBackUnavailable";
                evidence["readEvidence"] = preReadEvidence;
                return false;
            }
            evidence["readEvidence"] = preReadEvidence;
            if (!string.IsNullOrWhiteSpace(currentVersion))
            {
                evidence["observedVersion"] = currentVersion;
                if (!string.Equals(currentVersion, version, StringComparison.Ordinal))
                {
                    evidence["reason"] = "CurrentVersionDiverged";
                    return false;
                }
            }

            bool currentIsAttemptedState;
            string currentDiff = null;
            try
            {
                currentIsAttemptedState = XmlEquivalence.AreEquivalent(
                    expectedCurrentXml, currentXml, out currentDiff);
            }
            catch (Exception ex)
            {
                currentIsAttemptedState = false;
                currentDiff = ex.Message;
            }
            if (!currentIsAttemptedState)
            {
                evidence["reason"] = "CurrentStateDiverged";
                evidence["diff"] = currentDiff;
                return false;
            }

            var options = (JObject)(writeOptions?.DeepClone() ?? new JObject());
            // This service owns the compensating write and verifies it below. Do not
            // let the generic writer perform a second, un-fenced rollback pass.
            options["rollbackOnFailure"] = false;
            options["baseVersion"] = version;
            options["recoveryRestore"] = true;
            evidence["attempted"] = true;

            try
            {
                string restoreRaw = _backend.WriteWebFormXml(target, baselineXml, false, options);
                try
                {
                    evidence["write"] = JObject.Parse(string.IsNullOrWhiteSpace(restoreRaw) ? "{}" : restoreRaw);
                }
                catch
                {
                    evidence["write"] = new JObject { ["raw"] = restoreRaw };
                }
            }
            catch (Exception ex)
            {
                // A restore writer can fail after persisting. The independent read below
                // remains authoritative over the writer status/error.
                evidence["writeError"] = ex.Message;
            }

            if (!TryReadCurrentWebForm(
                target, out string restoredXml, out string restoredVersion,
                out bool readTruncated, out JObject readEvidence))
            {
                evidence["reason"] = readTruncated ? "ReadBackTruncated" : "ReadBackUnavailable";
                evidence["readEvidence"] = readEvidence;
                return false;
            }

            evidence["readEvidence"] = readEvidence;
            try
            {
                if (XmlEquivalence.AreEquivalent(baselineXml, restoredXml, out string restoreDiff))
                {
                    evidence["succeeded"] = true;
                    evidence["deferred"] = false;
                    evidence["versionToken"] = restoredVersion ?? version;
                    return true;
                }
                evidence["reason"] = "BaselineMismatch";
                evidence["diff"] = restoreDiff;
            }
            catch (Exception ex)
            {
                evidence["reason"] = "BaselineComparisonFailed";
                evidence["error"] = ex.Message;
            }
            return false;
        }

        // --- Pure XML mutation core (testable) -------------------------------------------------

        public static string ApplyAction(string xml, string action, JObject args,
            List<string> warnings = null, List<string> controlsAdded = null)
        {
            warnings ??= new List<string>();
            controlsAdded ??= new List<string>();

            XDocument doc;
            try { doc = XDocument.Parse(xml); }
            catch (Exception ex)
            {
                throw new InvalidOperationException("WebForm XML failed to parse: " + ex.Message);
            }
            if (doc.Root == null)
                throw new InvalidOperationException("WebForm XML has no root element.");

            switch ((action ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "add_textblock":
                    AddTextBlock(doc, args, controlsAdded);
                    break;
                case "add_button":
                    AddButton(doc, args, controlsAdded);
                    break;
                case "set_visibility":
                    SetVisibility(doc, args);
                    break;
                case "remove_control":
                    RemoveControl(doc, args);
                    break;
                case "wrap_in_fieldset":
                    WrapInFieldset(doc, args, controlsAdded, warnings);
                    break;
                default:
                    throw new ArgumentException("Unknown action: " + action);
            }

            return doc.Declaration != null
                ? doc.Declaration + Environment.NewLine + doc.Root.ToString(SaveOptions.None)
                : doc.Root.ToString(SaveOptions.None);
        }

        private static void AddTextBlock(XDocument doc, JObject args, List<string> controlsAdded)
        {
            string caption = args["caption"]?.ToString() ?? string.Empty;
            string format = args["format"]?.ToString() ?? "Text";
            string controlId = ResolveControlId(doc, args, "TextBlock");
            string position = args["position"]?.ToString() ?? "last";
            string parentId = args["parent"]?.ToString();
            var flavor = WebFormXmlHelper.DetectMarkupFlavor(doc);
            var tb = CreateControl(
                doc, "gxTextBlock", controlId, caption, format, null, flavor, position, parentId);

            InsertControl(doc, tb, parentId, position, flavor);
            controlsAdded.Add(controlId);
        }

        private static void AddButton(XDocument doc, JObject args, List<string> controlsAdded)
        {
            string caption = args["caption"]?.ToString() ?? "Button";
            string eventName = args["event"]?.ToString();
            string controlId = ResolveControlId(doc, args, "Btn");
            string parentId = args["parent"]?.ToString();
            string position = args["position"]?.ToString() ?? "last";
            var flavor = WebFormXmlHelper.DetectMarkupFlavor(doc);
            var btn = CreateControl(
                doc, "gxButton", controlId, caption, null, eventName, flavor, position, parentId);

            InsertControl(doc, btn, parentId, position, flavor);
            controlsAdded.Add(controlId);
        }

        private static XElement CreateControl(
            XDocument doc,
            string elementName,
            string controlId,
            string caption,
            string format,
            string eventName,
            WebFormXmlHelper.WebFormMarkupFlavor flavor,
            string position,
            string parentId)
        {
            var sibling = FindSiblingControl(doc, elementName, position, parentId);
            var control = sibling == null ? new XElement(elementName) : new XElement(sibling);
            if (sibling != null) control.RemoveNodes();

            string identityAttribute = ChooseIdentityAttribute(doc, sibling, flavor);
            SetControlIdentity(control, controlId, identityAttribute, sibling);

            bool legacy = flavor == WebFormXmlHelper.WebFormMarkupFlavor.LegacyHtml;
            if (legacy)
            {
                control.SetAttributeValue("CaptionExpression", BuildConstantCaptionTokens(caption));
                // A newly authored legacy control must not carry the modern plain
                // Caption attribute inherited from a mixed/legacy sibling.
                control.Attribute("Caption")?.Remove();
            }
            else
            {
                control.SetAttributeValue("Caption", caption ?? string.Empty);
                // CaptionExpression is not the modern default. Remove it only from
                // the new element; existing sibling markup is never edited.
                control.Attribute("CaptionExpression")?.Remove();
            }

            if (string.Equals(elementName, "gxTextBlock", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrEmpty(format)) control.SetAttributeValue("Format", format);
                else control.Attribute("Format")?.Remove();
            }

            string normalizedEvent = NormalizeEvent(eventName);
            if (string.Equals(elementName, "gxButton", StringComparison.OrdinalIgnoreCase))
            {
                string eventAttribute = ChooseEventAttribute(sibling, legacy);
                foreach (var name in new[] { "Event", "OnClickEvent", "eventGX" })
                    if (!string.Equals(name, eventAttribute, StringComparison.OrdinalIgnoreCase))
                        control.Attribute(name)?.Remove();
                if (normalizedEvent != null) control.SetAttributeValue(eventAttribute, normalizedEvent);
                else control.Attribute(eventAttribute)?.Remove();
            }

            return control;
        }

        private static XElement FindSiblingControl(XDocument doc, string elementName, string position, string parentId)
        {
            XElement anchor = null;
            if (!string.IsNullOrWhiteSpace(position) &&
                position.StartsWith("after:", StringComparison.OrdinalIgnoreCase))
                anchor = FindControl(doc, position.Substring("after:".Length).Trim());

            if (anchor?.Parent != null)
            {
                var localSibling = anchor.Parent.Elements().FirstOrDefault(e =>
                    string.Equals(e.Name.LocalName, elementName, StringComparison.OrdinalIgnoreCase));
                if (localSibling != null) return localSibling;
            }

            if (!string.IsNullOrWhiteSpace(parentId))
            {
                var parent = FindControl(doc, parentId);
                if (parent != null)
                {
                    var scoped = parent.Descendants().FirstOrDefault(e =>
                        string.Equals(e.Name.LocalName, elementName, StringComparison.OrdinalIgnoreCase));
                    if (scoped != null) return scoped;
                }
            }

            return doc.Descendants().FirstOrDefault(e =>
                string.Equals(e.Name.LocalName, elementName, StringComparison.OrdinalIgnoreCase));
        }

        private static string ChooseIdentityAttribute(XDocument doc, XElement sibling, WebFormXmlHelper.WebFormMarkupFlavor flavor)
        {
            if (sibling?.Attribute("ControlName") != null || sibling?.Attribute("controlName") != null)
                return sibling.Attribute("ControlName") != null ? "ControlName" : "controlName";
            if (sibling?.Attribute("id") != null) return "id";
            if (sibling?.Attribute("InternalName") != null) return "InternalName";

            bool usesId = doc.Descendants().Any(e =>
                e.Attribute("id") != null && e.Attribute("ControlName") == null);
            if (flavor == WebFormXmlHelper.WebFormMarkupFlavor.LegacyHtml && !usesId)
                return "ControlName";

            return usesId ? "id" : "ControlName";
        }

        private static void SetControlIdentity(XElement control, string controlId, string identityAttribute, XElement sibling)
        {
            foreach (var name in new[] { "id", "ControlName", "controlName", "InternalName" })
            {
                if (!string.Equals(name, identityAttribute, StringComparison.OrdinalIgnoreCase))
                    control.Attribute(name)?.Remove();
            }
            control.SetAttributeValue(identityAttribute, controlId);

            // If the sibling deliberately carried both equivalent identity attrs,
            // retain that shape; otherwise one canonical identity avoids id/name drift.
            if (sibling?.Attribute("id") != null && sibling?.Attribute("ControlName") != null &&
                string.Equals(sibling.Attribute("id").Value, sibling.Attribute("ControlName").Value, StringComparison.OrdinalIgnoreCase))
                control.SetAttributeValue("ControlName", controlId);
        }

        private static string ChooseEventAttribute(XElement sibling, bool legacy)
        {
            if (legacy) return "Event";
            if (sibling?.Attribute("Event") != null) return "Event";
            if (sibling?.Attribute("OnClickEvent") != null) return "OnClickEvent";
            if (sibling?.Attribute("eventGX") != null) return "eventGX";
            return "OnClickEvent";
        }

        private static string NormalizeEvent(string value)
        {
            string normalized = (value ?? string.Empty).Trim();
            while (normalized.Length >= 2 &&
                ((normalized[0] == '\'' && normalized[normalized.Length - 1] == '\'') ||
                 (normalized[0] == '"' && normalized[normalized.Length - 1] == '"')))
                normalized = normalized.Substring(1, normalized.Length - 2).Trim();
            if (normalized.Length == 0) return null;
            return "'" + normalized.Replace("'", "''") + "'";
        }

        private static string BuildConstantCaptionTokens(string value)
        {
            return new XElement("Tokens",
                new XElement("Token",
                    new XElement("Type", "Constant"),
                    new XElement("Data", new XCData(value ?? string.Empty))))
                .ToString(SaveOptions.DisableFormatting);
        }

        private static void SetVisibility(XDocument doc, JObject args)
        {
            string controlId = args["controlId"]?.ToString();
            if (string.IsNullOrWhiteSpace(controlId))
                throw new ArgumentException("controlId is required.");
            bool? visibleNullable = args["visible"]?.ToObject<bool?>();
            if (!visibleNullable.HasValue)
                throw new ArgumentException("visible (bool) is required.");

            var ctl = FindControl(doc, controlId);
            if (ctl == null)
                throw new InvalidOperationException("Control not found: " + controlId);
            ctl.SetAttributeValue("Visible", visibleNullable.Value ? "True" : "False");
        }

        private static void RemoveControl(XDocument doc, JObject args)
        {
            string controlId = args["controlId"]?.ToString();
            if (string.IsNullOrWhiteSpace(controlId))
                throw new ArgumentException("controlId is required.");
            var ctl = FindControl(doc, controlId);
            if (ctl == null)
                throw new InvalidOperationException("Control not found: " + controlId);
            ctl.Remove();
        }

        private static void WrapInFieldset(XDocument doc, JObject args, List<string> controlsAdded, List<string> warnings)
        {
            var idsToken = args["controlIds"] as JArray;
            if (idsToken == null || idsToken.Count == 0)
                throw new ArgumentException("controlIds[] is required.");
            string legend = args["legend"]?.ToString();
            string groupId = args["controlId"]?.ToString() ?? GenerateId(doc, "Grp");

            var ids = idsToken.Select(t => t.ToString()).ToList();
            var firstControl = FindControl(doc, ids[0]);
            if (firstControl == null)
                throw new InvalidOperationException("Control not found: " + ids[0]);

            var fieldset = new XElement("gxFieldSet",
                new XAttribute("ControlName", groupId));
            if (!string.IsNullOrEmpty(legend))
                fieldset.SetAttributeValue("Caption", legend);

            // Insert fieldset where the first control was; then move the named controls into it.
            firstControl.AddBeforeSelf(fieldset);
            foreach (var id in ids)
            {
                var ctl = FindControl(doc, id);
                if (ctl == null)
                {
                    warnings.Add("control_not_found:" + id);
                    continue;
                }
                ctl.Remove();
                fieldset.Add(ctl);
            }
            controlsAdded.Add(groupId);
        }

        // --- Helpers ---------------------------------------------------------------------------

        private static XElement FindControl(XDocument doc, string controlId)
        {
            if (string.IsNullOrWhiteSpace(controlId)) return null;
            return doc.Descendants()
                .FirstOrDefault(e =>
                {
                    foreach (var name in new[] { "ControlName", "controlName", "id", "Id", "InternalName", "name" })
                    {
                        var attr = e.Attributes().FirstOrDefault(a =>
                            string.Equals(a.Name.LocalName, name, StringComparison.OrdinalIgnoreCase));
                        if (string.Equals(attr?.Value, controlId, StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                    return false;
                });
        }

        private static string ResolveControlId(XDocument doc, JObject args, string prefix)
        {
            string requested = args?["controlId"]?.ToString();
            if (string.IsNullOrWhiteSpace(requested)) return GenerateId(doc, prefix);
            if (FindControl(doc, requested) != null)
                throw new ArgumentException("controlId '" + requested + "' already exists; choose a unique id or ControlName.");
            return requested.Trim();
        }

        private static void InsertControl(
            XDocument doc,
            XElement newElement,
            string parentId,
            string position,
            WebFormXmlHelper.WebFormMarkupFlavor flavor)
        {
            position = (position ?? "last").Trim();
            if (position.StartsWith("after:", StringComparison.OrdinalIgnoreCase))
            {
                string anchorId = position.Substring("after:".Length).Trim();
                var anchor = FindControl(doc, anchorId);
                if (anchor == null)
                    throw new InvalidOperationException("Anchor control not found for position: " + anchorId);
                // Insert beside the anchor so the sibling's parent/cell shape is
                // retained whenever the source XML already has one.
                anchor.AddAfterSelf(newElement);
                return;
            }

            XElement parent;
            if (!string.IsNullOrWhiteSpace(parentId))
            {
                parent = FindControl(doc, parentId);
                if (parent == null)
                    throw new InvalidOperationException("Parent control not found: " + parentId);
            }
            else
            {
                parent = FindDefaultParent(doc);
            }

            bool first = position.Equals("first", StringComparison.OrdinalIgnoreCase);
            AddToContainer(parent, newElement, flavor, first);
        }

        private static XElement FindDefaultParent(XDocument doc)
        {
            var table = doc.Descendants().FirstOrDefault(e =>
                string.Equals(e.Name.LocalName, "TABLE", StringComparison.OrdinalIgnoreCase));
            if (table != null) return table;

            return doc.Descendants().FirstOrDefault(e =>
                       string.Equals(e.Name.LocalName, "body", StringComparison.OrdinalIgnoreCase))
                   ?? doc.Descendants().FirstOrDefault(e =>
                       string.Equals(e.Name.LocalName, "Form", StringComparison.OrdinalIgnoreCase))
                   ?? doc.Root;
        }

        private static void AddToContainer(
            XElement parent,
            XElement control,
            WebFormXmlHelper.WebFormMarkupFlavor flavor,
            bool first)
        {
            if (parent == null) return;
            bool isTable = string.Equals(parent.Name.LocalName, "TABLE", StringComparison.OrdinalIgnoreCase);
            if (isTable)
            {
                var existingRows = parent.Elements().Where(e =>
                    string.Equals(e.Name.LocalName, "TR", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(e.Name.LocalName, "row", StringComparison.OrdinalIgnoreCase)).ToList();
                string rowName = flavor == WebFormXmlHelper.WebFormMarkupFlavor.LegacyHtml
                    ? "TR"
                    : existingRows.Select(e => e.Name.LocalName).FirstOrDefault() ?? "row";
                string cellName = flavor == WebFormXmlHelper.WebFormMarkupFlavor.LegacyHtml
                    ? "TD"
                    : existingRows.SelectMany(e => e.Elements())
                        .Select(e => e.Name.LocalName)
                        .FirstOrDefault(n => string.Equals(n, "TD", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(n, "cell", StringComparison.OrdinalIgnoreCase)) ?? "cell";
                var rows = existingRows;
                XElement row = first ? rows.FirstOrDefault() : rows.LastOrDefault();
                if (row == null)
                {
                    row = new XElement(rowName);
                    if (first) parent.AddFirst(row); else parent.Add(row);
                }
                var cell = new XElement(cellName, control);
                if (first) row.AddFirst(cell); else row.Add(cell);
                return;
            }

            if (first) parent.AddFirst(control); else parent.Add(control);
        }

        private static string GenerateId(XDocument doc, string prefix)
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in doc.Descendants())
            {
                foreach (var name in new[] { "ControlName", "controlName", "id", "Id", "InternalName", "name" })
                {
                    var attr = e.Attributes().FirstOrDefault(a =>
                        string.Equals(a.Name.LocalName, name, StringComparison.OrdinalIgnoreCase));
                    if (!string.IsNullOrWhiteSpace(attr?.Value)) used.Add(attr.Value);
                }
            }
            for (int i = 1; i < 10000; i++)
            {
                string candidate = prefix + i;
                if (!used.Contains(candidate)) return candidate;
            }
            return prefix + Guid.NewGuid().ToString("N").Substring(0, 6);
        }

        private static JObject CompactDryRunWriteResult(JObject source)
        {
            var compact = source == null ? new JObject() : (JObject)source.DeepClone();
            compact.Remove("source");
            compact.Remove("persistedSnippet");
            compact.Remove("raw");
            compact["sourceEchoOmitted"] = true;
            var result = compact["result"] as JObject;
            if (result != null)
            {
                result.Remove("source");
                result.Remove("persistedSnippet");
            }
            return compact;
        }

        private static string BuildShortDiff(string before, string after)
        {
            // A semantic edit should expose the changed fragment, not repeat a
            // 50 KB WebForm several times in a dry-run response.
            string diff;
            try { diff = GxMcp.Worker.Helpers.DiffBuilder.UnifiedDiff(before, after, 1); }
            catch { diff = string.Empty; }
            if (diff.Length > 2000) diff = diff.Substring(0, 2000) + "\n…diff truncated…";
            if (string.IsNullOrEmpty(diff))
                return string.Format("{0} chars → {1} chars (Δ {2:+0;-0;0})",
                    before?.Length ?? 0, after?.Length ?? 0, (after?.Length ?? 0) - (before?.Length ?? 0));
            return diff;
        }

        private static bool TryReadCompleteWebFormXml(
            string read,
            out string xml,
            out string versionToken,
            out bool truncated,
            out JObject evidence)
        {
            xml = null;
            versionToken = null;
            truncated = false;
            evidence = new JObject
            {
                ["readComplete"] = false,
                ["readOptions"] = new JObject { ["offset"] = 0, ["limit"] = 0 }
            };

            if (string.IsNullOrWhiteSpace(read))
            {
                evidence["errorCode"] = "EmptyRead";
                evidence["errorMessage"] = "The complete-read request returned no response.";
                return false;
            }

            string trimmed = read.TrimStart();
            if (!trimmed.StartsWith("{", StringComparison.Ordinal))
            {
                xml = read;
                evidence["readComplete"] = true;
                evidence["representation"] = "raw";
                return true;
            }

            JObject payload;
            try
            {
                payload = JObject.Parse(read);
            }
            catch (Exception ex)
            {
                evidence["errorCode"] = "InvalidReadEnvelope";
                evidence["errorMessage"] = ex.Message;
                return false;
            }

            foreach (string name in new[]
            {
                "part", "contentType", "representation", "verificationSource",
                "offset", "limit", "totalLines", "totalBytes", "suggestedNextOffset",
                "suggestedNextLimit", "versionToken"
            })
            {
                if (payload[name] != null) evidence[name] = payload[name].DeepClone();
            }

            bool hasError = string.Equals(payload["status"]?.ToString(), "error", StringComparison.OrdinalIgnoreCase)
                || string.Equals(payload["status"]?.ToString(), "failed", StringComparison.OrdinalIgnoreCase)
                || (payload["error"] != null && payload["error"].Type != JTokenType.Null);
            if (hasError)
            {
                evidence["errorCode"] = payload["error"]?["code"]?.ToString()
                    ?? payload["code"]?.ToString()
                    ?? "ReadUnavailable";
                evidence["errorMessage"] = payload["error"]?["message"]?.ToString()
                    ?? payload["message"]?.ToString()
                    ?? "The WebForm read returned an error envelope.";
                return false;
            }

            truncated = payload["truncated"]?.ToObject<bool?>() == true
                || payload["isTruncatedByWorker"]?.ToObject<bool?>() == true
                || payload["suggestedNextOffset"] != null
                && payload["suggestedNextOffset"].Type != JTokenType.Null;
            evidence["truncated"] = truncated;
            evidence["isTruncatedByWorker"] =
                payload["isTruncatedByWorker"]?.ToObject<bool?>() == true;
            if (truncated) return false;

            if (payload["isBase64"]?.ToObject<bool?>() == true
                || payload["projected"]?.ToObject<bool?>() == true
                || payload["serializedPart"]?.ToObject<bool?>() == true)
            {
                evidence["errorCode"] = "IncompleteReadRepresentation";
                evidence["errorMessage"] = "The WebForm read was projected, serialized, or base64-encoded instead of returning editable XML text.";
                return false;
            }

            JToken source = payload["source"] ?? payload["content"] ?? payload["xml"];
            if (source == null || source.Type != JTokenType.String || string.IsNullOrWhiteSpace(source.ToString()))
            {
                evidence["errorCode"] = "CompleteXmlUnavailable";
                evidence["errorMessage"] = "The WebForm read did not contain editable XML text.";
                return false;
            }

            xml = source.ToString();
            if (xml.IndexOf("[CONTENT TRUNCATED.", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                truncated = true;
                evidence["truncated"] = true;
                return false;
            }

            versionToken = payload["versionToken"]?.ToString();
            evidence["versionToken"] = versionToken;
            evidence["readComplete"] = true;
            evidence["representation"] = payload["representation"]?.ToString() ?? "genexus_read";
            return true;
        }

        private static string IncompleteWebFormReadError(
            string target,
            bool truncated,
            string message,
            JObject evidence)
        {
            return McpResponse.Err(
                code: truncated ? "WebFormReadTruncated" : "WebFormReadUnavailable",
                message: message,
                hint: "Request a complete WebForm read (offset=0, limit=0) and retry only after the full source is available.",
                target: target,
                extra: new JObject
                {
                    ["readEvidence"] = evidence ?? new JObject(),
                    ["reReadRequired"] = true
                },
                retryable: true);
        }

        private static string Err(string code, string message)
        {
            return McpResponse.Err(code: code, message: message);
        }
    }
}
