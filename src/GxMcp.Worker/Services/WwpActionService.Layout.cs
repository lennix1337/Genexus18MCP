using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Artech.Architecture.Common.Objects;
using Artech.Genexus.Common;
using Artech.Packages.Patterns.Objects;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;
using GxMcp.Worker.Structure;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    public sealed partial class WwpActionService
    {
        private string RunLayoutOperation(string target, KBObject instance, JObject args)
        {
            string unsupportedPlacement = UnsupportedLayoutPlacement(args);
            if (unsupportedPlacement != null)
                return McpResponse.Err(code: "WwpLayoutPlacementUnsupported", target: target,
                    message: "add_layout appends the new children after the existing ones of tablePath and does not support " + unsupportedPlacement + "; nothing was changed.");
            string expected = (string)(args["expectedVersion"] ?? args["baseVersion"] ?? args["versionToken"]);
            if (string.IsNullOrWhiteSpace(expected))
                return McpResponse.Err(code: "ExpectedVersionRequired", target: target,
                    message: "add_layout requires the PatternInstance version read before the call (expectedVersion), including for dryRun.");
            var host = instance as PatternInstance;
            if (!IsLayoutParentType(host?.KBObject?.GetType()))
                return McpResponse.Err(code: "WwpLayoutParentUnsupported", target: target,
                    message: "add_layout requires a native WorkWithPlus instance bound to a WebPanel or WebComponent.");
            Guid hostId = host.Guid, parentId = host.KBObject.Guid;
            var kb = host.KB;
            lock (WriteService.AcquirePerTargetLock(target))
            lock (WriteService.AcquirePerTargetLock(hostId.ToString()))
            {
                host = (PatternInstance)EventsSaveIsolation.Fresh(kb, hostId);
                KBObject parent = EventsSaveIsolation.Fresh(kb, parentId);
                string original = LayoutPatternText(host);
                string currentVersion = WriteService.ComputeContentVersionToken(host, original);
                if (currentVersion != expected)
                    return McpResponse.Err(code: "StaleObject", message: "PatternInstance changed; no save attempted.", target: target,
                        extra: new JObject { ["currentVersion"] = currentVersion, ["persisted"] = false });
                var before = XDocument.Parse(original);
                var planned = new XDocument(before);
                JObject plan = PlanLayout(planned, args);
                if (plan["error"] != null)
                    return McpResponse.Err(code: (string)plan["code"], message: (string)plan["error"], target: target);
                // Preflight every declared variable before creating any native child.
                Dictionary<string, Variable> variables;
                try { variables = LayoutVariables(parent, (JArray)args["children"]); }
                catch (WwpTabException ex) { return McpResponse.Err(code: ex.Code, message: ex.Message, target: target); }
                string formBefore = WebFormXmlHelper.ReadEditableXml(parent)
                    ?? throw new InvalidOperationException("The complete parent WebForm is required.");
                string eventsBefore = LayoutEvents(parent);
                var receipt = new JObject
                {
                    ["operation"] = "add_layout", ["instanceGuid"] = hostId.ToString(), ["parentGuid"] = parentId.ToString(),
                    ["versionToken"] = currentVersion, ["typedDiff"] = plan, ["persisted"] = false,
                    ["saveAttempted"] = false, ["commitCompleted"] = false, ["rollbackPerformed"] = false,
                    ["persistedStateKnown"] = true, ["specified"] = false, ["generated"] = false, ["built"] = false
                };
                if (args["dryRun"]?.Value<bool>() == true)
                    return McpResponse.Ok(code: "WwpLayoutDryRun", target: target, result: receipt);
                var hostSnapshot = CaptureLayoutSnapshot(host);
                var parentSnapshot = CaptureLayoutSnapshot(parent);
                var snapshots = CaptureSnapshots(host, original, parent, formBefore);
                if (snapshots.Pattern == null || snapshots.WebForm == null)
                    throw new InvalidOperationException("PatternInstance and WebForm snapshots are required.");
                string snapshotRoot = EditSnapshotStore.ResolveRoot(kb.Location);
                var eventsSnapshot = EditSnapshotStore.SaveSnapshot(snapshotRoot, parentId.ToString(), "Events", eventsBefore);
                var variablesPart = PartAccessor.GetVariablesPart(parent)
                    ?? throw new InvalidOperationException("The Variables snapshot is unavailable.");
                var variablesSnapshot = EditSnapshotStore.SaveSnapshot(snapshotRoot, parentId.ToString(), "Variables", variablesPart.SerializeToXml());
                if (eventsSnapshot == null || variablesSnapshot == null)
                    throw new InvalidOperationException("Events and Variables snapshots are required.");
                receipt["snapshot"] = snapshots.ToJson();
                receipt["snapshot"]["events"] = eventsSnapshot.Path;
                receipt["snapshot"]["variables"] = variablesSnapshot.Path;
                receipt["snapshot"]["hostHash"] = hostSnapshot.Hash;
                receipt["snapshot"]["parentHash"] = parentSnapshot.Hash;
                string parentName = parent.Name, hostName = host.Name;
                IndexCacheService readIndex = _objects.GetKbService()?.GetIndexCache();
                bool committed = false, rolledBack = false, rollbackAttempted = false, mutationAttempted = false;
                string failure = null;
                try
                {
                    using (var transaction = kb.BeginTransaction())
                    {
                        try
                        {
                            LayoutStage.Enter(receipt, LayoutStage.Boundary);
                            host = (PatternInstance)EventsSaveIsolation.Fresh(kb, hostId);
                            parent = EventsSaveIsolation.Fresh(kb, parentId);
                            bool identityMatches = host.KBObject?.Guid == parentId;
                            bool patternMatches = LayoutPatternText(host) == original;
                            var boundaryHost = CompareLayoutSnapshot(hostSnapshot, host);
                            var boundaryParent = CompareLayoutSnapshot(parentSnapshot, parent);
                            receipt["boundaryVerification"] = new JObject
                            {
                                ["identityConfirmed"] = identityMatches, ["patternXmlConfirmed"] = patternMatches,
                                ["host"] = LayoutComparisonReceipt(boundaryHost), ["parent"] = LayoutComparisonReceipt(boundaryParent)
                            };
                            if (!identityMatches || !patternMatches || !boundaryHost.Equal || !boundaryParent.Equal)
                                throw new WwpTabException("StaleObject", "Host or parent changed at the transaction boundary.");
                            var part = host.PatternPart;
                            if (!TryFindNativeTable(GetProperty(part, "RootElement"), (string)args["tablePath"], out object table, out string error))
                                throw new WwpTabException("WwpTableNotFound", error);
                            mutationAttempted = true;
                            foreach (JObject child in (JArray)args["children"])
                                AttachLayoutChild(table, child, variables, receipt);
                            WriteService.ForcePatternPartDirty(part);
                            // nativeOperation.stage is the stage a failure is attributed to: its savePhase
                            // and, for an exception this operation does not type, its code come from it.
                            LayoutStage.Enter(receipt, LayoutStage.Save);
                            receipt["saveAttempted"] = true;
                            // Save the native instance, then project it through the supported
                            // WWP callback inside this transaction. Save alone does not project.
                            host.Save(new KBObjectSavePreferences());
                            receipt["objectSaveReturned"] = true;
                            LayoutStage.Enter(receipt, LayoutStage.Project);
                            host = (PatternInstance)EventsSaveIsolation.Fresh(kb, hostId);
                            parent = EventsSaveIsolation.Fresh(kb, parentId);
                            bool projected = WwpProjectionHelper.TryProjectHostOntoParent(parent, host, out var projection);
                            receipt["projection"] = LayoutProjectionReceipt(projection);
                            if (!projected)
                                throw new WwpTabException("WwpProjectionFailed", "The WorkWithPlus projection callback did not complete; transaction aborted.");
                            LayoutStage.Enter(receipt, LayoutStage.Verify);
                            host = (PatternInstance)EventsSaveIsolation.Fresh(kb, hostId);
                            parent = EventsSaveIsolation.Fresh(kb, parentId);
                            JObject verification = VerifyLayoutState(before, host, parent, args, variables, formBefore, eventsBefore, hostSnapshot, parentSnapshot);
                            receipt["preCommitVerification"] = verification;
                            if (verification["confirmed"]?.Value<bool>() != true)
                            {
                                // The cause stays reachable in the terse error: the failed checks go in the
                                // message and in two small fields, the full receipt stays in verbose_errors.
                                NoteLayoutVerificationFailure(receipt, verification);
                                try
                                {
                                    var attemptedForm = EditSnapshotStore.SaveSnapshot(snapshotRoot, parentId.ToString(),
                                        "AttemptedWebForm", WebFormXmlHelper.ReadEditableXml(parent));
                                    var attemptedEvents = EditSnapshotStore.SaveSnapshot(snapshotRoot, parentId.ToString(),
                                        "AttemptedEvents", LayoutEvents(parent));
                                    receipt["attemptedStateSnapshots"] = new JObject
                                    {
                                        ["webForm"] = attemptedForm?.Path, ["events"] = attemptedEvents?.Path
                                    };
                                }
                                catch (Exception diagnosticError)
                                {
                                    Logger.Error("[WWP-LAYOUT] Failed to snapshot attempted projection: " + diagnosticError);
                                    receipt["attemptedStateSnapshotsUnavailable"] = true;
                                }
                                throw new WwpTabException("WwpLayoutNotVerified",
                                    "The native save did not preserve and project the requested state; transaction aborted" + LayoutFailedChecksSuffix(verification) + ".");
                            }
                            LayoutStage.Enter(receipt, LayoutStage.Commit);
                            transaction.Commit();
                            committed = true;
                        }
                        catch (Exception ex)
                        {
                            Logger.Error("[WWP-LAYOUT] Native mutation/save failed: " + ex);
                            string failedStage = LayoutStage.Of(receipt);
                            // The SDK keeps the reasons on the instance and the exception: read them before the rollback.
                            string sdkDetail = AddLayoutSaveDiagnostics(receipt, ex,
                                () => new[] { new KeyValuePair<string, object>("PatternInstance", host), new KeyValuePair<string, object>("WebForm", parent) },
                                hostName, LayoutStage.Phase(failedStage));
                            failure = ex is WwpTabException ? ex.Message : sdkDetail;
                            receipt["failureCode"] = (ex as WwpTabException)?.Code ?? LayoutStage.FailureCode(failedStage);
                            rollbackAttempted = true;
                            try { transaction.Rollback(); rolledBack = true; }
                            catch (Exception rollbackError) { receipt["rollbackError"] = LayoutErrorText(rollbackError); }
                        }
                    }
                }
                catch (Exception ex)
                {
                    // The transaction's own end (its disposal) is reported beside the cause, never instead of it.
                    string finalization = LayoutErrorText(ex);
                    receipt["transactionFinalizationError"] = finalization;
                    failure = KeepFirstFailure(failure, finalization);
                }
                finally
                {
                    FinalizeLayoutReadCaches(mutationAttempted, receipt, () =>
                        InvalidateLayoutReadCaches(true, readIndex,
                            target, parentName, parentId.ToString(), hostName, hostId.ToString()));
                }
                receipt["commitCompleted"] = committed;
                receipt["rollbackPerformed"] = rolledBack;
                bool? restored = null, confirmed = null;
                JObject postSaveVerification = null;
                try
                {
                    host = (PatternInstance)EventsSaveIsolation.Fresh(kb, hostId);
                    parent = EventsSaveIsolation.Fresh(kb, parentId);
                    receipt["versionToken"] = WriteService.ComputeContentVersionToken(host, LayoutPatternText(host));
                    restored = NoteLayoutRestoration(receipt, rollbackAttempted, rolledBack,
                        CompareLayoutSnapshot(hostSnapshot, host), CompareLayoutSnapshot(parentSnapshot, parent));
                    if (committed)
                    {
                        postSaveVerification = VerifyLayoutState(before, host, parent, args, variables, formBefore, eventsBefore, hostSnapshot, parentSnapshot);
                        receipt["postSaveVerification"] = postSaveVerification;
                        confirmed = postSaveVerification["confirmed"]?.Value<bool>() == true;
                        if (confirmed != true)
                        {
                            receipt["savePhase"] = IsolatedEventsSavePhase.PostCommitVerification;
                            NoteLayoutVerificationFailure(receipt, postSaveVerification);
                        }
                    }
                }
                catch (Exception ex) { receipt["verificationError"] = LayoutErrorText(ex); }
                LayoutOutcome outcome = ClassifyLayoutOutcome(mutationAttempted, committed, restored, confirmed);
                // A commit, or a state that could not be proven restored, must send the next build down the full path.
                if (outcome.MarkTargetDirty) WriteService.NotePerTargetWrite(target);
                receipt["persisted"] = outcome.Persisted.HasValue ? (JToken)outcome.Persisted.Value : JValue.CreateNull();
                receipt["persistedStateKnown"] = outcome.PersistedStateKnown;
                if (outcome.Success)
                    return McpResponse.Ok(code: "WwpLayoutUpdated", target: target, result: receipt);
                if (outcome.PartialPersistenceDetected.HasValue)
                    receipt["partialPersistenceDetected"] = outcome.PartialPersistenceDetected.Value;
                return McpResponse.Err(code: LayoutFailureCodeFor(receipt, committed), target: target,
                    message: committed ? LayoutPostSaveFailureMessage(postSaveVerification)
                        : failure ?? LayoutPostSaveFailureMessage(null), extra: receipt);
            }
        }

        // ---- failure vocabulary of add_layout ---------------------------------------------------------------

        // The stages of add_layout, in the order a write goes through them. nativeOperation.stage in the receipt is
        // the current one (AttachLayoutChild refines the native edit as create / set:<attribute> / attach). A
        // failure's savePhase and, for an exception the operation did not type itself, its code are derived from
        // the stage, so neither claims a save that never started.
        internal static class LayoutStage
        {
            internal const string Boundary = "boundary";
            internal const string Save = "save";
            internal const string Project = "project";
            internal const string Verify = "verify";
            internal const string Commit = "commit";

            internal const string NativeMutationPhase = "native-mutation";
            internal const string ProjectionPhase = "projection";

            internal static void Enter(JObject receipt, string stage) =>
                receipt["nativeOperation"] = new JObject { ["stage"] = stage };

            internal static string Of(JObject receipt) => (string)receipt?["nativeOperation"]?["stage"];

            // savePhase uses the Events save vocabulary wherever a stage means the same thing.
            internal static string Phase(string stage)
            {
                switch (stage)
                {
                    case null:
                    case "":
                    case Boundary: return IsolatedEventsSavePhase.TransactionFreshRead;
                    case Save: return IsolatedEventsSavePhase.ObjectSave;
                    case Project: return ProjectionPhase;
                    case Verify: return IsolatedEventsSavePhase.PreCommitVerification;
                    case Commit: return IsolatedEventsSavePhase.Commit;
                    default: return NativeMutationPhase;
                }
            }

            // The code of an exception the operation did not type: only the save stage may say the save failed.
            internal static string FailureCode(string stage)
            {
                switch (stage)
                {
                    case Save: return "WwpLayoutSaveFailed";
                    case Project: return "WwpProjectionFailed";
                    case Verify: return "WwpLayoutNotVerified";
                    case Commit: return "WwpLayoutCommitFailed";
                    default: return "WwpLayoutPreSaveFailed";
                }
            }
        }

        // What the write amounted to, from facts only: whether a native edit was attempted, whether the transaction
        // committed, whether the state read back equals the baseline (null: it could not be read) and whether the
        // post-commit verification confirmed the request (null: it did not run to the end).
        internal sealed class LayoutOutcome
        {
            internal bool Success { get; set; }
            // true: written and confirmed; false: nothing of this call is in the instance; null: it cannot be told.
            internal bool? Persisted { get; set; }
            internal bool PersistedStateKnown { get; set; }
            // null while the comparison with the baseline is unavailable.
            internal bool? PartialPersistenceDetected { get; set; }
            internal bool MarkTargetDirty { get; set; }
        }

        internal static LayoutOutcome ClassifyLayoutOutcome(bool mutationAttempted, bool committed, bool? restored, bool? postCommitConfirmed)
        {
            bool success = committed && postCommitConfirmed == true;
            var outcome = new LayoutOutcome
            {
                Success = success,
                // The next build takes the full path after a commit and after any attempt whose restoration is not
                // proven: the same rule ShouldMarkIsolatedEventsDirty applies to the Events save.
                MarkTargetDirty = committed || (mutationAttempted && restored != true),
                PersistedStateKnown = success || !mutationAttempted
                    || (restored.HasValue && (!committed || postCommitConfirmed.HasValue))
            };
            if (success)
            {
                outcome.Persisted = true;
                outcome.PartialPersistenceDetected = false;
            }
            else if (!mutationAttempted || restored == true)
            {
                // Before the native edit began, a state that differs from the baseline is somebody else's change
                // (a concurrent edit that the boundary check refused), never a partial write of this call.
                outcome.Persisted = false;
                outcome.PartialPersistenceDetected = false;
            }
            else
            {
                outcome.PartialPersistenceDetected = restored.HasValue ? (bool?)true : null;
            }
            return outcome;
        }

        // The comparison with the baseline is a restoration only after a rollback: after a commit the state
        // differs from it by design, so the receipt does not call it a restoration. Returns whether the state
        // equals the baseline.
        internal static bool NoteLayoutRestoration(JObject receipt, bool rollbackAttempted, bool rolledBack,
            ObjectMoveSnapshot.Comparison host, ObjectMoveSnapshot.Comparison parent)
        {
            bool restored = host.Equal && parent.Equal;
            if (rollbackAttempted)
            {
                receipt["restorationVerification"] = new JObject
                {
                    ["host"] = LayoutComparisonReceipt(host), ["parent"] = LayoutComparisonReceipt(parent)
                };
                receipt["stateRestoredExactly"] = restored;
                receipt["rollbackVerified"] = rolledBack && restored;
            }
            return restored;
        }

        // The checks VerifyLayoutState makes, in the order it makes them. A failure names them, never their values.
        internal static readonly string[] LayoutVerificationChecks =
        {
            "patternReReadConfirmed", "projection", "unrelatedStatePreserved", "eventsPreserved",
            "nativeVariableBindingsConfirmed", "identityConfirmed"
        };

        internal static string[] FailedLayoutChecks(JObject verification)
        {
            if (verification == null) return new string[0];
            return LayoutVerificationChecks.Where(check => check == "projection"
                ? verification["projection"]?["confirmed"]?.Value<bool>() != true
                : verification[check]?.Value<bool>() != true).ToArray();
        }

        internal static string DescribeLayoutVerificationFailure(JObject verification)
        {
            return string.Join(", ", FailedLayoutChecks(verification).Select(check =>
            {
                if (check != "projection") return check;
                // The projection check says which of its own steps failed: a short fixed name.
                string detail = (string)verification["projection"]?["failureStage"] ?? (string)verification["projection"]?["code"];
                return string.IsNullOrEmpty(detail) ? check : check + " (" + detail + ")";
            }));
        }

        internal static string LayoutFailedChecksSuffix(JObject verification)
        {
            string checks = DescribeLayoutVerificationFailure(verification);
            return checks.Length == 0 ? string.Empty : " (failed checks: " + checks + ")";
        }

        internal static string LayoutPostSaveFailureMessage(JObject verification) =>
            "Post-save verification failed" + LayoutFailedChecksSuffix(verification)
            + ". Inspect saved state before retrying; no compensating write was attempted.";

        // Two small fields beside the full verification receipt: the terse error projection keeps them, while the
        // receipt itself (preCommitVerification, postSaveVerification) is only in verbose_errors.
        internal static void NoteLayoutVerificationFailure(JObject receipt, JObject verification)
        {
            receipt["failedChecks"] = new JArray(FailedLayoutChecks(verification));
            string stage = (string)verification?["projection"]?["failureStage"];
            if (!string.IsNullOrEmpty(stage)) receipt["failureStage"] = stage;
        }

        // An exception's message as it may appear in a receipt: no local path, credential or connection part.
        internal static string LayoutErrorText(Exception ex) => IsolatedEventsSaveDiagnostics.Sanitize(ex?.Message);

        // The first failure is the cause; a later one (the end of the transaction) is reported beside it.
        internal static string KeepFirstFailure(string first, string later) => string.IsNullOrEmpty(first) ? later : first;

        internal static string LayoutFailureCodeFor(JObject receipt, bool committed)
        {
            string recorded = (string)receipt?["failureCode"];
            if (!string.IsNullOrEmpty(recorded)) return recorded;
            return committed ? "WwpLayoutNotVerified" : LayoutStage.FailureCode(LayoutStage.Of(receipt));
        }

        // add_layout only appends after the existing children. The schema's placement arguments belong to
        // add_table and move_control: they are refused rather than ignored.
        internal static string UnsupportedLayoutPlacement(JObject args)
        {
            var present = new[] { "position", "before", "after" }
                .Where(name => args?[name] != null && args[name].Type != JTokenType.Null).ToList();
            return present.Count == 0 ? null : string.Join(", ", present);
        }

        // SourcePart.SerializeData consumes its lazy m_Source field before the getter
        // materializes it. Snapshot native bytes only after reading all source parts.
        internal static void PrepareLayoutSnapshotSources(IEnumerable<ISource> sources)
        {
            foreach (ISource source in sources) { _ = source.Source; }
        }

        private static ObjectMoveSnapshot CaptureLayoutSnapshot(KBObject obj)
        {
            PrepareLayoutSnapshotSources(obj.Parts.Cast<KBObjectPart>().OfType<ISource>());
            return ObjectMoveSnapshot.Capture(obj);
        }

        private static ObjectMoveSnapshot.Comparison CompareLayoutSnapshot(ObjectMoveSnapshot snapshot,
            KBObject obj, params string[] ignoredParts)
        {
            PrepareLayoutSnapshotSources(obj.Parts.Cast<KBObjectPart>().OfType<ISource>());
            return snapshot.Compare(obj, ignoredParts);
        }

        internal static JObject LayoutComparisonReceipt(ObjectMoveSnapshot.Comparison comparison) => new JObject
        {
            ["equal"] = comparison.Equal,
            ["changedParts"] = new JArray(comparison.ChangedParts.Select(part => SanitizeSnapshotFailure(part.ToString())).ToArray()),
            ["changedPartKeys"] = new JArray(comparison.ChangedPartKeys), ["persistedHash"] = comparison.PersistedHash
        };

        // A snapshot that could not be read reports the exception's own message in place of a part name.
        private static string SanitizeSnapshotFailure(string changedPart) =>
            changedPart.StartsWith("SnapshotReadFailed:", StringComparison.Ordinal)
                ? IsolatedEventsSaveDiagnostics.Sanitize(changedPart) : changedPart;

        // Why the native save was refused, in the Events save diagnostics format: the SDK's own validation
        // messages (a ValidationException keeps them in Output), the inner exception types and messages,
        // sanitized and truncated. Returns the one-line cause that the error text reports. The phase is the
        // stage the failure is attributed to (LayoutStage.Phase), the save stage unless the caller knows better.
        internal static string AddLayoutSaveDiagnostics(JObject receipt, Exception ex,
            Func<IEnumerable<KeyValuePair<string, object>>> sdkSources, string target,
            string phase = IsolatedEventsSavePhase.ObjectSave)
        {
            try
            {
                IsolatedEventsFailure failure = IsolatedEventsSaveDiagnostics.Describe(ex, phase, sdkSources, target);
                failure.WriteTo(receipt);
                return failure.Detail;
            }
            catch (Exception diagnosticError)
            {
                receipt["sdkDiagnosticsError"] = LayoutErrorText(diagnosticError);
                return LayoutErrorText(ex);
            }
        }

        internal static JObject LayoutProjectionReceipt(WwpProjectionHelper.ProjectionResult result) => new JObject
        {
            ["lifecycleAttempted"] = result.LifecycleAttempted, ["shouldBuild"] = result.ShouldBuild,
            ["beforeStartBuild"] = result.BeforeStartBuild, ["afterImportResources"] = result.AfterImportResources,
            ["updateParentObject"] = result.UpdateParentObject, ["afterEndBuild"] = result.AfterEndBuild,
            ["parentSaved"] = result.ParentSaved, ["confirmed"] = result.LifecycleExecuted,
            // The projection's failure text is an exception message: sanitized like every other one in the receipt.
            ["failure"] = result.Failure == null ? null : IsolatedEventsSaveDiagnostics.Sanitize(result.Failure)
        };

        private string LayoutPatternText(KBObject host)
        {
            string xml = _patterns.ReadPatternPartXml(host, "PatternInstance", PatternRegistry.WorkWithPlusPatternId, out KBObject resolved, out _);
            if (resolved?.Guid != host.Guid || string.IsNullOrWhiteSpace(xml))
                throw new InvalidOperationException("PatternInstance identity could not be confirmed.");
            return xml;
        }

        internal static JObject PlanLayout(XDocument document, JObject args)
        {
            try
            {
                XElement table = FindXmlTableOrNull(document, (string)args["tablePath"], out string error);
                if (table == null) throw new WwpTabException("WwpTableNotFound", error);
                if (!(args["children"] is JArray children) || children.Count == 0)
                    throw new WwpTabException("WwpLayoutChildrenRequired", "children must be a non-empty typed array.");
                var names = new HashSet<string>(document.Descendants().Select(e => Attr(e, "name"))
                    .Where(n => n.Length > 0), StringComparer.OrdinalIgnoreCase);
                var additions = children.Select(c => PlanLayoutChild(c as JObject, names, 0)).ToList();
                if (!string.IsNullOrWhiteSpace(Attr(table, "childrenOrderedList"))
                    && additions.Any(e => Is(e, "table") || Is(e, "userAction")))
                    throw new WwpTabException("WwpOrderedContainerUnsupported",
                        "Adding tables or actions to a container with an existing childrenOrderedList requires an independently verified native IDE ordering adapter. Choose an unordered container; no metadata was changed.");
                table.Add(additions);
                return new JObject { ["tablePath"] = args["tablePath"], ["children"] = children.DeepClone(), ["changed"] = true };
            }
            catch (WwpTabException ex) { return new JObject { ["code"] = ex.Code, ["error"] = ex.Message }; }
        }

        private static XElement PlanLayoutChild(JObject child, HashSet<string> names, int depth)
        {
            if (child == null || depth > 10) throw new WwpTabException("InvalidWwpChild", "A typed child object is required; maximum nesting depth is 10.");
            string type = (string)child["type"], name = (string)child["name"];
            string[] allowed = type == "variable" ? new[] { "type", "name" }
                : type == "userAction" ? new[] { "type", "name", "caption" }
                : type == "table" ? new[] { "type", "name", "tableType", "children" } : null;
            if (allowed == null || child.Properties().Any(p => !allowed.Contains(p.Name)))
                throw new WwpTabException("InvalidWwpChild", "Only variable(name), userAction(name,caption), and table(name,tableType,children) are supported; declarations and default metadata cannot be edited.");
            if (name == null || !Regex.IsMatch(name, @"^[A-Za-z_][A-Za-z0-9_]*$") || !names.Add(name))
                throw new WwpTabException("WwpLayoutNameConflict", "Every new layout name must be valid and unique in the instance.");
            var element = new XElement(type, new XAttribute("name", name));
            if (type == "userAction") element.SetAttributeValue("caption", (string)child["caption"] ?? name);
            if (type == "table")
            {
                string tableType = (string)child["tableType"] ?? "Responsive";
                if (tableType != "Responsive" && tableType != "Regular")
                    throw new WwpTabException("InvalidWwpTableType", "tableType must be Responsive or Regular.");
                element.SetAttributeValue("type", tableType);
                if (child["children"] != null && !(child["children"] is JArray))
                    throw new WwpTabException("InvalidWwpChild", "Table children must be a typed array.");
                foreach (JToken nested in child["children"] as JArray ?? new JArray())
                    element.Add(PlanLayoutChild(nested as JObject, names, depth + 1));
            }
            return element;
        }

        private static IEnumerable<JObject> LayoutChildren(JArray children)
        {
            foreach (JObject child in children)
            {
                yield return child;
                foreach (JObject nested in LayoutChildren(child["children"] as JArray ?? new JArray())) yield return nested;
            }
        }

        private static Dictionary<string, Variable> LayoutVariables(KBObject parent, JArray children)
        {
            var declared = PartAccessor.GetVariablesPart(parent)?.Variables
                ?? throw new InvalidOperationException("The parent Variables part is unavailable.");
            var result = new Dictionary<string, Variable>(StringComparer.OrdinalIgnoreCase);
            foreach (JObject child in LayoutChildren(children).Where(c => (string)c["type"] == "variable"))
            {
                string name = (string)child["name"];
                var matches = declared.Where(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
                if (matches.Count != 1) throw new WwpTabException("WwpVariableNotDeclared", "Declare variable '" + name + "' on the parent before adding it to the layout.");
                Variable variable = matches[0];
                if (!string.IsNullOrWhiteSpace(DomainPropertyApplier.GetAttributeBasedOnName(variable)))
                    throw new WwpTabException("WwpVariableBindingUnsupported", "Attribute-based variables are not supported by add_layout.");
                if (variable.DomainBasedOn == null) LayoutBasicType(variable);
                result.Add(name, variable);
            }
            return result;
        }

        private static string LayoutBasicType(Variable variable)
        {
            switch (variable.Type.ToString().ToUpperInvariant())
            {
                case "CHAR": case "CHARACTER": return "Character";
                case "VARCHAR": return "VarChar";
                case "LONGVARCHAR": return "LongVarChar";
                case "NUMERIC": return "Numeric";
                case "DATE": return "Date";
                case "DATETIME": return "DateTime";
                case "BOOLEAN": return "Boolean";
                case "GUID": return "GUID";
                default: throw new WwpTabException("WwpVariableBindingUnsupported", "This variable type has no verified layout adapter: " + variable.Type);
            }
        }

        private static void AttachLayoutChild(object parent, JObject spec, Dictionary<string, Variable> variables, JObject receipt)
        {
            string type = (string)spec["type"], name = (string)spec["name"];
            Action<string> stage = operation => receipt["nativeOperation"] = new JObject
            {
                ["stage"] = operation, ["childType"] = type, ["childName"] = name
            };
            object child = null;
            Action<string, object> set = (attribute, value) =>
            {
                stage("set:" + attribute);
                if (!PatternSemanticAttributeWriter.ApplySemanticAttributeObject(child, attribute, value,
                    value is string || value is int ? Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) : null))
                    throw new WwpTabException("WwpAttributeRejected", "WorkWithPlus rejected layout attribute '" + attribute + "'.");
            };
            stage("create");
            child = CreateNativeChild(parent, type);
            set("name", name);
            if (type == "variable")
            {
                Variable variable = variables[name];
                if (variable.DomainBasedOn != null)
                {
                    set("dataType", "Based on");
                    set("domain", variable.DomainBasedOn);
                }
                else
                {
                    string basicType = LayoutBasicType(variable);
                    set("dataType", "Basic");
                    set("basicType", basicType);
                    if (basicType == "Numeric")
                    {
                        // The native specification declares these attributes as int;
                        // ChangeAttributeValueCommand does not parse string values.
                        set("basicLength", (int)variable.Length);
                        set("basicDecimals", (int)variable.Decimals);
                    }
                    else if (basicType == "Character" || basicType == "VarChar" || basicType == "LongVarChar")
                        set(basicType == "LongVarChar" ? "basicLVCLength" : "basicCLength", (int)variable.Length);
                }
            }
            else if (type == "userAction") set("caption", (string)spec["caption"] ?? name);
            else
            {
                set("type", (string)spec["tableType"] ?? "Responsive");
                foreach (JObject nested in spec["children"] as JArray ?? new JArray()) AttachLayoutChild(child, nested, variables, receipt);
            }
            stage("attach");
            ExecuteElementCommand(parent, child, "AddElementCommand", null);
        }

        // PatternInstanceElement.Type is the specification type (TableVariable),
        // while Name is the XML child name (variable).
        internal static bool IsLayoutVariableElement(object element) =>
            string.Equals(GetProperty(element, "Name")?.ToString(), "variable", StringComparison.OrdinalIgnoreCase);

        private JObject VerifyLayoutState(XDocument before, PatternInstance host, KBObject parent, JObject args,
            Dictionary<string, Variable> variables, string formBefore, string eventsBefore, ObjectMoveSnapshot hostSnapshot, ObjectMoveSnapshot parentSnapshot)
        {
            bool pattern = VerifyLayoutPattern(before, XDocument.Parse(LayoutPatternText(host)), args);
            JObject projection = VerifyLayoutProjection(formBefore, WebFormXmlHelper.ReadEditableXml(parent), (JArray)args["children"],
                variables.ToDictionary(v => v.Key, v => "var:" + v.Value.Id, StringComparer.OrdinalIgnoreCase));
            // Variables, Events, Rules, all other parts, and authored object properties must remain intact.
            bool preserved = CompareLayoutSnapshot(hostSnapshot, host, host.PatternPart.TypeDescriptor?.Name ?? host.PatternPart.GetType().Name).Equal
                && CompareLayoutSnapshot(parentSnapshot, parent, "WebForm", "Events").Equal;
            bool events = VerifyLayoutEvents(eventsBefore, LayoutEvents(parent), (JArray)args["children"]);
            bool bindings = true;
            object nativeRoot = GetProperty(host.PatternPart, "RootElement");
            foreach (var pair in variables)
            {
                var nativeMatches = Walk(nativeRoot).Where(e => IsLayoutVariableElement(e)
                    && string.Equals(NativeAttribute(e, "name"), pair.Key, StringComparison.OrdinalIgnoreCase)).ToList();
                if (nativeMatches.Count != 1) { bindings = false; break; }
                if (pair.Value.DomainBasedOn != null)
                {
                    var domain = PatternSemanticAttributeWriter.ReadSemanticAttributeObject(nativeMatches[0], "domain") as KBObject;
                    bindings &= domain?.Guid == pair.Value.DomainBasedOn.Guid && NativeAttribute(nativeMatches[0], "dataType") == "Based on";
                }
                else bindings &= NativeAttribute(nativeMatches[0], "dataType") == "Basic"
                    && NativeAttribute(nativeMatches[0], "basicType") == LayoutBasicType(pair.Value);
            }
            bool identity = host.KBObject?.Guid == parent.Guid;
            return new JObject { ["confirmed"] = pattern && projection["confirmed"]?.Value<bool>() == true && preserved && events && bindings && identity,
                ["patternReReadConfirmed"] = pattern, ["projection"] = projection, ["unrelatedStatePreserved"] = preserved, ["eventsPreserved"] = events, ["nativeVariableBindingsConfirmed"] = bindings,
                ["identityConfirmed"] = identity };
        }

        private static string LayoutEvents(KBObject parent)
        {
            if (!(PartAccessor.GetPart(parent, "Events") is ISource source))
                throw new InvalidOperationException("The Events source is required.");
            return source.Source ?? string.Empty;
        }

        internal static bool VerifyLayoutEvents(string before, string after, JArray children)
        {
            if (before == null || after == null) return false;
            foreach (JObject action in LayoutChildren(children).Where(c => (string)c["type"] == "userAction"))
            {
                string eventName = "Do" + (string)action["name"];
                string header = @"(?im)^[ \t]*Event[ \t]+['"" ]?" + Regex.Escape(eventName) + @"['"" ]?[ \t]*\r?$";
                // Existing event bodies are never rewritten or stripped.
                if (Regex.IsMatch(before, header)) continue;
                string markers = Regex.Escape("/* Generated by DVelop Work With Plus Pattern [Start] - Do not change */")
                    + @"[ \t\r\n]*" + Regex.Escape("/* Generated by DVelop Work With Plus Pattern [End] - Do not change */");
                string empty = @"(?im)^[ \t]*Event[ \t]+['"" ]" + Regex.Escape(eventName)
                    + @"['"" ][ \t]*\r?\n[ \t\r\n]*(?:(?-i:" + markers + @")[ \t\r\n]*)?EndEvent[ \t\r\n]*(?=\S|$)";
                var matches = Regex.Matches(after, empty);
                if (matches.Count > 1) return false;
                if (matches.Count == 1) after = after.Remove(matches[0].Index, matches[0].Length);
            }
            return string.Equals(before.TrimEnd('\r', '\n'), after.TrimEnd('\r', '\n'), StringComparison.Ordinal);
        }

        internal static bool VerifyLayoutPattern(XDocument before, XDocument after, JObject args)
        {
            var oldCopy = new XDocument(before);
            var newCopy = new XDocument(after);
            XElement oldTable = FindXmlTableOrNull(oldCopy, (string)args["tablePath"]);
            XElement newTable = FindXmlTableOrNull(newCopy, (string)args["tablePath"]);
            if (oldTable == null || newTable == null) return false;
            int count = oldTable.Elements().Count();
            var additions = newTable.Elements().Skip(count).ToList();
            var requested = (JArray)args["children"];
            if (additions.Count != requested.Count) return false;
            for (int i = 0; i < additions.Count; i++)
                if (!LayoutChildMatches(additions[i], (JObject)requested[i])) return false;
            additions.Remove();
            return LayoutXmlEqual(oldCopy.Root, newCopy.Root);
        }

        private static bool LayoutChildMatches(XElement element, JObject spec)
        {
            string type = (string)spec["type"];
            if (!Is(element, type) || Attr(element, "name") != (string)spec["name"]) return false;
            if (type == "userAction" && Attr(element, "caption") != ((string)spec["caption"] ?? (string)spec["name"])) return false;
            if (type == "table" && Attr(element, "type") != ((string)spec["tableType"] ?? "Responsive")) return false;
            var children = spec["children"] as JArray ?? new JArray();
            var actual = element.Elements().ToList();
            if (actual.Count != children.Count) return false;
            for (int i = 0; i < actual.Count; i++) if (!LayoutChildMatches(actual[i], (JObject)children[i])) return false;
            return true;
        }

        internal static JObject VerifyLayoutProjection(string beforeXml, string afterXml, JArray children, IDictionary<string, string> bindings)
        {
            var result = new JObject { ["confirmed"] = false };
            Func<string, JObject> failed = stage => { result["failureStage"] = stage; return result; };
            try
            {
                var before = XDocument.Parse(beforeXml);
                var after = XDocument.Parse(afterXml);
                LayoutStructuralIds.Normalize(before);
                LayoutStructuralIds.Normalize(after);
                var originalNames = before.Descendants().Select(e => Attr(e, "ControlName")).Where(n => n.Length > 0).ToList();
                var originalSet = new HashSet<string>(originalNames, StringComparer.OrdinalIgnoreCase);
                var persistedOrder = after.Descendants().Select(e => Attr(e, "ControlName")).Where(originalSet.Contains).ToList();
                if (!originalNames.SequenceEqual(persistedOrder, StringComparer.OrdinalIgnoreCase)) return failed("existingControlOrder");
                // Compare every original named control, including binding, position and authored properties.
                foreach (XElement oldControl in before.Descendants().Where(e => Attr(e, "ControlName").Length > 0))
                {
                    string name = Attr(oldControl, "ControlName");
                    var current = after.Descendants().Where(e => Attr(e, "ControlName").Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (current.Count != 1 || oldControl.Name != current[0].Name
                        || !LayoutXmlEqual(new XElement(oldControl.Name, oldControl.Attributes()), new XElement(current[0].Name, current[0].Attributes())))
                        return failed("existingControlIdentityOrAttributes");
                    var oldParents = oldControl.Ancestors().Select(e => Attr(e, "ControlName")).Where(n => n.Length > 0);
                    var newParents = current[0].Ancestors().Select(e => Attr(e, "ControlName")).Where(n => n.Length > 0);
                    if (!oldParents.SequenceEqual(newParents, StringComparer.OrdinalIgnoreCase)) return failed("existingControlAncestry");
                    // Leaf controls can carry event/property subelements; preserve those too.
                    bool container = Is(oldControl, "table") || Is(oldControl, "gxTable") || Is(oldControl, "gxResponsiveTable");
                    if (!container && !oldControl.Descendants().Any(e => Attr(e, "ControlName").Length > 0)
                        && !LayoutXmlEqual(new XElement(oldControl), new XElement(current[0]))) return failed("existingControlContent");
                }
                var addedControls = new HashSet<XElement>();
                foreach (JObject child in LayoutChildren(children))
                {
                    string type = (string)child["type"], name = (string)child["name"];
                    List<XElement> matches;
                    if (type == "variable")
                    {
                        if (!bindings.TryGetValue(name, out string binding)) return failed("variableDeclarationMissing");
                        matches = after.Descendants().Where(e => LayoutControlBinding(e) == binding).ToList();
                        if (before.Descendants().Any(e => LayoutControlBinding(e) == binding)) return failed("variableBindingAlreadyPresent");
                    }
                    else
                    {
                        matches = after.Descendants().Where(e => type == "table"
                            ? Attr(e, "ControlName").Equals(name, StringComparison.OrdinalIgnoreCase) && (Is(e, "table") || Is(e, "gxTable") || Is(e, "gxResponsiveTable"))
                            : Is(e, "action") ? Attr(e, "ControlName").Equals("Btn" + name, StringComparison.OrdinalIgnoreCase)
                            : Is(e, "gxButton") && Attr(e, "ControlName").Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
                        if (before.Descendants().Any(e => Attr(e, "ControlName").Equals(name, StringComparison.OrdinalIgnoreCase))) return failed("controlNameAlreadyPresent");
                    }
                    if (matches.Count != 1) return failed(type + "ControlMissingOrAmbiguous");
                    addedControls.Add(matches[0]);
                    if (type == "userAction" && (Attr(matches[0], "Caption") != ((string)child["caption"] ?? name)
                        || (Is(matches[0], "action") ? Attr(matches[0], "onClickEvent") != "'Do" + name + "'"
                            : !WebFormContainsEvent(after, name)))) return failed("actionCaptionOrEventBinding");
                }
                if (!LayoutProjectionPreserves(before.Root, after.Root, addedControls)) return failed("unnamedContentOrStructure");
                result["confirmed"] = true;
                result["unnamedContentPreserved"] = true;
                result["bindingConfirmed"] = true;
                result["existingControlsPreserved"] = true;
            }
            catch (Exception ex) when (ex is System.Xml.XmlException || ex is ArgumentException) { result["code"] = "WwpProjectionSchemaUnsupported"; }
            return result;
        }

        // WEB_COMP changes the descriptor to WebComponent while retaining the
        // same native WebPanel CLR type and entity type GUID.
        internal static bool IsLayoutParentType(Type type) => type == typeof(Artech.Genexus.Common.Objects.WebPanel);

        internal static void InvalidateLayoutReadCaches(bool mutationAttempted, IndexCacheService index = null, params string[] targets)
        {
            // The public read path may return a cached payload before fresh SDK lookup.
            // Clear every identity alias after either commit or rollback, never on preview.
            if (mutationAttempted) WriteService.InvalidateIsolatedEventsReadCaches();
        }

        internal static void FinalizeLayoutReadCaches(bool mutationAttempted, JObject receipt, Action invalidate,
            Action managedFallback = null)
        {
            if (!mutationAttempted) return;
            try { invalidate(); }
            catch (Exception ex)
            {
                receipt["readCacheInvalidationError"] = LayoutErrorText(ex);
                // Preserve the native persistence receipt even when indexed invalidation
                // fails, and still attempt the existing managed-only cache clear.
                Action managedClear = managedFallback ?? (() => WriteService.InvalidateIsolatedEventsReadCaches());
                try { managedClear(); }
                catch (Exception fallback) { receipt["managedReadCacheInvalidationError"] = LayoutErrorText(fallback); }
            }
        }

        private static string LayoutControlBinding(XElement element) => Is(element, "data")
            ? Attr(element, "attribute") : Attr(element, "AttId");

        private static bool LayoutProjectionPreserves(XNode before, XNode after, HashSet<XElement> additions)
        {
            if (!(before is XElement oldElement) || !(after is XElement newElement))
                return XNode.DeepEquals(before, after);
            if (!LayoutXmlEqual(new XElement(oldElement.Name, oldElement.Attributes()),
                new XElement(newElement.Name, newElement.Attributes()))) return false;
            var original = oldElement.Nodes().ToList();
            var persisted = newElement.Nodes().ToList();
            int position = 0;
            foreach (XNode node in original)
            {
                while (position < persisted.Count && !LayoutProjectionPreserves(node, persisted[position], additions))
                {
                    if (!IsAddedProjectionSubtree(persisted[position], additions)) return false;
                    position++;
                }
                if (position == persisted.Count) return false;
                position++;
            }
            return persisted.Skip(position).All(n => IsAddedProjectionSubtree(n, additions));
        }

        private static bool IsAddedProjectionSubtree(XNode node, HashSet<XElement> additions)
        {
            if (!(node is XElement element)) return false;
            if (additions.Contains(element)) return true;
            // New anonymous layout wrappers may surround only requested controls.
            // Existing text, comments, HTML, labels and empty cells must still match.
            return string.IsNullOrEmpty(Attr(element, "ControlName"))
                && element.Elements().Any()
                && element.Nodes().All(n => n is XText text && string.IsNullOrWhiteSpace(text.Value)
                    || IsAddedProjectionSubtree(n, additions));
        }

        private static bool LayoutXmlEqual(XElement left, XElement right)
        {
            foreach (XElement element in left.DescendantsAndSelf().Concat(right.DescendantsAndSelf()))
            {
                element.ReplaceAttributes(element.Attributes().OrderBy(a => a.Name.ToString(), StringComparer.Ordinal).ToList());
            }
            return XNode.DeepEquals(left, right);
        }
    }
}
