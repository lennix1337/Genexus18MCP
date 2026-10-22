using System;
using System.Collections.Generic;
using System.Linq;
using Artech.Architecture.Common.Objects;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    // regenerate_derived: the WorkWithPlus host, its parent Transaction/WebPanel and every object
    // the instance derives (the LoadDVCombo helper, the WW/DS/export family, ...) handled as ONE
    // unit. An edit that changes how the instance calls a helper (combo Parameters, conditions)
    // is saved on the instance and projected on the parent, but the helper is a separate KB
    // object with its own Source/Rules/Variables and stays on the old contract until the engine
    // rebuilds it. This operation rebuilds them through the pattern engine, then proves from a
    // fresh re-read that the helpers and every caller agree, and otherwise restores everything.
    public sealed partial class WwpActionService
    {
        internal const string RegenerateDerivedOperation = "regenerate_derived";

        private static bool IsRegenerateDerivedOperation(string operation) => operation == RegenerateDerivedOperation;

        private sealed class WwpRegenerateException : Exception
        {
            internal readonly string Code;
            internal readonly JObject Detail;
            internal WwpRegenerateException(string code, string message, JObject detail = null) : base(message)
            {
                Code = code;
                Detail = detail;
            }
        }

        // One derived object: the live SDK object, what was read from it and its version token.
        private sealed class DerivedEntry
        {
            internal KBObject Object;
            internal WwpObjectState State;
            internal string Token;
            internal bool Helper;
        }

        // The whole unit at one moment: host, parent and every derived object.
        private sealed class DerivedUnit
        {
            internal KBObject Host;
            internal string HostXml;
            internal string HostToken;
            internal KBObject Parent;
            internal DerivedEntry ParentEntry;
            internal List<DerivedEntry> Derived = new List<DerivedEntry>();

            internal IEnumerable<WwpObjectState> DerivedStates => Derived.Select(d => d.State);
            internal ISet<string> HelperKeys => new HashSet<string>(Derived.Where(d => d.Helper).Select(d => d.State.Key),
                StringComparer.OrdinalIgnoreCase);
        }

        private static string[] DerivedPartsFor(string typeName)
        {
            switch (typeName)
            {
                case "Procedure":
                case "DataProvider":
                case "API":
                case "Report":
                    return new[] { "Source", "Rules", "Variables" };
                case "WebPanel":
                case "WebComponent":
                case "Transaction":
                case "SDPanel":
                    return new[] { "WebForm", "Events", "Rules", "Variables" };
                case "SDT":
                    return new[] { "Structure" };
                case "DataSelector":
                    return new[] { "Conditions", "Source" };
                default:
                    return new[] { "Source", "Rules", "Variables", "Structure" };
            }
        }

        private WwpObjectState CaptureObjectState(KBObject obj)
        {
            string type = obj.TypeDescriptor?.Name;
            var state = new WwpObjectState
            {
                Name = obj.Name,
                Type = type,
                Guid = obj.Guid,
                QualifiedName = obj.QualifiedName?.ToString() ?? obj.Name
            };
            // One fresh handle per object, then every part: resolving a handle per part dominated the cost of a capture.
            foreach (KeyValuePair<string, string> part in _objects.ReadObjectPartsForVerification(obj.Name, DerivedPartsFor(type), type))
                state.Parts[part.Key] = part.Value;
            return state;
        }

        private DerivedEntry CaptureEntry(KBObject obj)
        {
            WwpObjectState state = CaptureObjectState(obj);
            return new DerivedEntry
            {
                Object = obj,
                State = state,
                Token = WriteService.ComputeContentVersionToken(obj, state.Fingerprint())
            };
        }

        private DerivedUnit CaptureUnit(KBObject host, string hostXml, KBObject parent)
        {
            var unit = new DerivedUnit
            {
                Host = host,
                HostXml = hostXml,
                HostToken = WriteService.ComputeContentVersionToken(host, hostXml),
                Parent = parent,
                ParentEntry = CaptureEntry(parent)
            };
            foreach (KBObject derived in WwpDerivedEngine.EnumerateDerived(host))
                unit.Derived.Add(CaptureEntry(derived));
            MarkHelpers(unit);
            return unit;
        }

        // The helper rule and the contract check are SDK-free (WwpDerivedUnit), so they run the same
        // on a live capture and in the unit tests.
        private static void MarkHelpers(DerivedUnit unit)
        {
            List<WwpObjectState> everyone = EveryoneIn(unit);
            foreach (DerivedEntry entry in unit.Derived) entry.Helper = WwpDerivedUnit.IsHelper(entry.State, everyone);
        }

        private static List<WwpObjectState> EveryoneIn(DerivedUnit unit) =>
            new[] { unit.ParentEntry.State }.Concat(unit.DerivedStates).ToList();

        private static JObject EvaluateContract(DerivedUnit unit) =>
            WwpDerivedUnit.EvaluateContract(unit.Derived.Where(d => d.Helper).Select(d => d.State), EveryoneIn(unit));

        private static JObject DescribeEntry(DerivedEntry entry, string role) => new JObject
        {
            ["name"] = entry.State.Name,
            ["type"] = entry.State.Type,
            ["qualifiedName"] = entry.State.QualifiedName,
            ["guid"] = entry.State.Guid.ToString("D"),
            ["role"] = role,
            ["versionToken"] = entry.Token,
            ["parts"] = entry.State.PartSummary()
        };

        private static JObject ExpectedVersionMap(DerivedUnit unit)
        {
            var map = new JObject { [unit.Host.TypeDescriptor?.Name + ":" + unit.Host.Name] = unit.HostToken };
            map[unit.ParentEntry.State.Key] = unit.ParentEntry.Token;
            foreach (DerivedEntry entry in unit.Derived) map[entry.State.Key] = entry.Token;
            return map;
        }

        // The tokens the caller supplied against the tokens read now (see WwpDerivedUnit.CompareVersions).
        private static List<JObject> CompareExpected(DerivedUnit unit, JObject supplied, string hostBaseVersion,
            out List<string> missing)
        {
            var objects = new List<WwpDerivedUnit.VersionedObject>
            {
                new WwpDerivedUnit.VersionedObject
                {
                    Key = unit.Host.TypeDescriptor?.Name + ":" + unit.Host.Name, Name = unit.Host.Name,
                    Current = unit.HostToken, Fallback = hostBaseVersion
                },
                new WwpDerivedUnit.VersionedObject
                {
                    Key = unit.ParentEntry.State.Key, Name = unit.ParentEntry.State.Name, Current = unit.ParentEntry.Token
                }
            };
            objects.AddRange(unit.Derived.Select(d => new WwpDerivedUnit.VersionedObject
            {
                Key = d.State.Key, Name = d.State.Name, Current = d.Token
            }));
            return WwpDerivedUnit.CompareVersions(objects, supplied, out missing);
        }

        private string RunRegenerateDerivedOperation(string target, KBObject requestedObject, KBObject instance,
            KBObjectPart instancePart, string xml, JObject args)
        {
            bool dryRun = args?["dryRun"]?.ToObject<bool?>() == true;
            bool rollbackOnFailure = args?["rollbackOnFailure"]?.ToObject<bool?>() ?? true;
            string scope = (args?["scope"]?.ToString() ?? "helpers").Trim().ToLowerInvariant();
            if (scope != "helpers" && scope != "all")
                return McpResponse.Err(code: "InvalidRegenerateScope",
                    message: "scope must be 'helpers' (default) or 'all'.", target: target);
            string hostBaseVersion = args?["baseVersion"]?.ToString() ?? args?["expectedVersion"]?.ToString()
                ?? args?["versionToken"]?.ToString();

            KBObject parent = WwpProjectionHelper.ResolveHostParent(instance, _objects, xml);
            if (parent == null)
                return McpResponse.Err(code: "WwpParentNotResolved",
                    message: "The Transaction/WebPanel that owns '" + instance.Name + "' could not be resolved, so the unit cannot be regenerated.",
                    target: target);

            DerivedUnit unit = CaptureUnit(instance, xml, parent);
            JObject contract = EvaluateContract(unit);
            var helperNames = new JArray(unit.Derived.Where(d => d.Helper).Select(d => d.State.Name));

            if (dryRun)
                return McpResponse.Ok(target: target, code: "WwpRegenerateDerivedDryRun", result: new JObject
                {
                    ["instance"] = instance.Name,
                    ["parent"] = parent.Name,
                    ["operation"] = RegenerateDerivedOperation,
                    ["scope"] = scope,
                    ["host"] = new JObject
                    {
                        ["name"] = instance.Name,
                        ["type"] = instance.TypeDescriptor?.Name,
                        ["versionToken"] = unit.HostToken,
                        ["patternSha256"] = Sha256(xml)
                    },
                    ["parentObject"] = DescribeEntry(unit.ParentEntry, "parent"),
                    ["derived"] = new JArray(unit.Derived.Select(d => DescribeEntry(d, d.Helper ? "helper" : "derived"))),
                    ["helpers"] = helperNames,
                    ["contractConsistency"] = contract,
                    ["expectedVersions"] = ExpectedVersionMap(unit),
                    ["engineAvailable"] = WwpDerivedEngine.IsAvailable(instance),
                    ["requires"] = "The WorkWithPlus Design System imported in the KB, as for the IDE's Apply Pattern; without it the engine refuses and nothing changes.",
                    ["plan"] = new JArray(
                        "Rebuild the derived objects of '" + instance.Name + "' through PatternEngine.ApplyPattern.",
                        "Update and save '" + parent.Name + "' after the derived objects.",
                        "Re-read host, parent and every derived object and compare them with this plan.",
                        "Restore every object from the captured snapshot if any check fails."),
                    ["persisted"] = false,
                    ["lifecycleExecuted"] = false
                });

            return RegenerateDerived(target, instance, parent, scope, rollbackOnFailure, hostBaseVersion,
                args?["expectedVersions"] as JObject);
        }

        private string RegenerateDerived(string target, KBObject requestedHost, KBObject requestedParent, string scope,
            bool rollbackOnFailure, string hostBaseVersion, JObject suppliedVersions)
        {
            lock (WriteService.AcquirePerTargetLock(target))
            {
                string readXml = _patterns.ReadPatternPartXml(requestedHost, "PatternInstance", PatternRegistry.WorkWithPlusPatternId,
                    out _, out _);
                _patterns.BuildPatternPartEnvelope(requestedHost, "PatternInstance", readXml, PatternRegistry.WorkWithPlusPatternId,
                    out KBObject host, out KBObjectPart hostPart);
                if (host == null || hostPart == null || string.IsNullOrWhiteSpace(readXml))
                    return BuildWwpInstanceNotResolvable(target);
                string hostXml = _patterns.ReadPatternPartXml(host, "PatternInstance", PatternRegistry.WorkWithPlusPatternId,
                    out _, out _);
                if (string.IsNullOrWhiteSpace(hostXml)) hostXml = readXml;

                DerivedUnit before = CaptureUnit(host, hostXml, requestedParent);
                List<JObject> stale = CompareExpected(before, suppliedVersions, hostBaseVersion, out List<string> missing);
                if (missing.Count > 0)
                    return McpResponse.Err(code: "ExpectedVersionRequired",
                        message: "regenerate_derived writes " + (before.Derived.Count + 2) + " object(s); pass expectedVersions with a token for each (read them with dryRun first). Missing: "
                            + string.Join(", ", missing) + ".",
                        target: target, extra: new JObject
                        {
                            ["missing"] = new JArray(missing),
                            ["expectedVersions"] = ExpectedVersionMap(before),
                            ["persisted"] = false
                        });
                if (stale.Count > 0)
                    return McpResponse.Err(code: "StaleObject",
                        message: stale.Count + " object(s) changed after the caller's read; nothing was regenerated.",
                        target: target, extra: new JObject
                        {
                            ["staleObjects"] = new JArray(stale),
                            ["expectedVersions"] = ExpectedVersionMap(before),
                            ["persisted"] = false
                        });

                JObject contractBefore = EvaluateContract(before);
                byte[] nativeBytes = ReadPartBytes(hostPart);
                string applyOnSaveBefore = ReadObjectProperty(host, "SDPlus_Editor_Apply_On_Save");
                JObject manifest;
                try { manifest = CaptureUnitSnapshots(before); }
                catch (Exception ex)
                {
                    return McpResponse.Err(code: "WwpSnapshotRequired",
                        message: "The exact snapshots of the regeneration unit could not be captured; nothing was regenerated: " + ExceptionRoot.Message(ex),
                        target: target, extra: new JObject { ["persisted"] = false });
                }
                if (nativeBytes == null)
                    return McpResponse.Err(code: "WwpSnapshotRequired",
                        message: "The native PatternInstance bytes could not be captured; nothing was regenerated.",
                        target: target, extra: new JObject { ["snapshot"] = manifest, ["persisted"] = false });

                WwpDerivedEngine.RunResult run = null;
                List<KBObject> createdObjects = new List<KBObject>();
                try
                {
                    run = WwpDerivedEngine.Apply(host);
                    WriteService.NotePerTargetWrite(target);
                    if (run.Failure != null)
                        throw new WwpRegenerateException("WwpRegenerateEngineFailed", run.Failure,
                            new JObject { ["engine"] = DescribeRun(run) });

                    string finalXml = ReadFreshInstance(host, out KBObject freshHost);
                    KBObject currentHost = freshHost ?? host;
                    string applyOnSaveAfter = ReadObjectProperty(currentHost, "SDPlus_Editor_Apply_On_Save");
                    bool applyOnSaveReenabled = false;
                    if (!IsFalse(applyOnSaveBefore) && IsFalse(applyOnSaveAfter))
                    {
                        applyOnSaveReenabled = WwpApplyOnSaveHelper.TryEnable(currentHost);
                        finalXml = ReadFreshInstance(host, out freshHost);
                        currentHost = freshHost ?? host;
                        applyOnSaveAfter = ReadObjectProperty(currentHost, "SDPlus_Editor_Apply_On_Save");
                        if (IsFalse(applyOnSaveAfter))
                            throw new WwpRegenerateException("WwpRegenerateApplyOnSaveDisabled",
                                "SDPlus_Editor_Apply_On_Save became False and could not be restored.");
                    }

                    DerivedUnit after = CaptureUnit(currentHost, finalXml, requestedParent);
                    WwpDerivedDiff diff = WwpDerivedDiff.Compare(before.DerivedStates, after.DerivedStates);
                    createdObjects = after.Derived.Where(d => diff.Created.Any(c => c.Guid == d.State.Guid)).Select(d => d.Object).ToList();

                    bool hostUnchanged = ContentMatches(finalXml, hostXml);
                    if (!hostUnchanged)
                        throw new WwpRegenerateException("WwpRegenerateHostChanged",
                            "The PatternInstance content differs after the regeneration; the operation must not change the instance it regenerates from.",
                            new JObject { ["patternSha256Before"] = Sha256(hostXml), ["patternSha256After"] = Sha256(finalXml) });

                    if (scope == "helpers")
                    {
                        List<string> outOfScope = diff.OutOfScope(before.HelperKeys);
                        if (outOfScope.Count > 0)
                            throw new WwpRegenerateException("WwpRegenerateOutOfScope",
                                "The engine changed objects that are not helpers: " + string.Join(", ", outOfScope)
                                + ". Review them and retry with scope=all if that is intended.",
                                new JObject { ["outOfScope"] = new JArray(outOfScope), ["diff"] = DescribeDiff(diff) });
                    }

                    JObject contractAfter = EvaluateContract(after);
                    if (!(bool)contractAfter["consistent"])
                        throw new WwpRegenerateException("WwpRegenerateContractInconsistent",
                            "After the regeneration a helper and its callers still disagree on the call contract.",
                            new JObject { ["contractConsistency"] = contractAfter });

                    TouchRegenerated(diff, before, after);
                    bool changed = diff.Changed.Count > 0 || diff.Created.Count > 0 || diff.Removed.Count > 0
                        || !ParentUnchanged(before, after);
                    var helpersRegenerated = new JArray(diff.Changed
                        .Where(c => before.HelperKeys.Contains(c.After.Key)).Select(c => c.After.Name));
                    return McpResponse.Ok(target: target, code: changed ? "WwpDerivedRegenerated" : "WwpDerivedUpToDate", result: new JObject
                    {
                        ["instance"] = currentHost.Name,
                        ["parent"] = requestedParent.Name,
                        ["operation"] = RegenerateDerivedOperation,
                        ["scope"] = scope,
                        ["changed"] = changed,
                        ["persisted"] = true,
                        ["verified"] = true,
                        ["helpers"] = new JArray(before.Derived.Where(d => d.Helper).Select(d => d.State.Name)),
                        ["helpersRegenerated"] = helpersRegenerated,
                        ["contractConsistency"] = new JObject
                        {
                            ["before"] = contractBefore["consistent"],
                            ["after"] = contractAfter["consistent"],
                            ["consistent"] = contractAfter["consistent"],
                            ["helpers"] = contractAfter["helpers"]
                        },
                        ["diff"] = DescribeDiff(diff),
                        ["parentChanged"] = !ParentUnchanged(before, after),
                        ["hostUnchanged"] = true,
                        ["patternSha256"] = Sha256(finalXml),
                        ["expectedVersions"] = ExpectedVersionMap(after),
                        ["engine"] = DescribeRun(run),
                        ["applyOnSaveBefore"] = applyOnSaveBefore,
                        ["applyOnSaveAfter"] = applyOnSaveAfter,
                        ["applyOnSaveReenabled"] = applyOnSaveReenabled,
                        ["snapshot"] = manifest,
                        ["rollbackPerformed"] = false,
                        ["lifecycleExecuted"] = false,
                        ["specified"] = false,
                        ["generated"] = false,
                        ["built"] = false
                    });
                }
                catch (Exception ex)
                {
                    Exception root = ExceptionRoot.Unwrap(ex) ?? ex;
                    WwpRegenerateException typed = root as WwpRegenerateException;
                    JObject rollback = rollbackOnFailure
                        ? RestoreDerivedUnit(before, host, hostPart, nativeBytes, hostXml, applyOnSaveBefore, createdObjects, requestedParent)
                        : new JObject { ["attempted"] = false, ["rollbackVerified"] = false, ["reason"] = "rollbackOnFailure=false" };
                    bool restored = (bool?)rollback["rollbackVerified"] == true;
                    ExceptionRoot.Log("[WWP-REGENERATE] failed", ex);
                    var extra = new JObject
                    {
                        ["exceptionType"] = root.GetType().Name,
                        ["failureTrace"] = ExceptionRoot.FailureTrace(ex),
                        ["persisted"] = rollbackOnFailure ? !restored : run?.Invoked == true,
                        ["partialPersistence"] = run?.Invoked == true && !restored,
                        ["snapshot"] = manifest,
                        ["rollback"] = rollback,
                        ["rollbackPerformed"] = (bool?)rollback["attempted"] != false,
                        ["rollbackVerified"] = restored,
                        ["lifecycleExecuted"] = false
                    };
                    if (run != null) extra["engine"] = DescribeRun(run);
                    if (typed?.Detail != null) foreach (JProperty property in typed.Detail.Properties()) extra[property.Name] = property.Value;
                    return McpResponse.Err(code: typed?.Code ?? "WwpRegenerateFailed", message: ExceptionRoot.Message(ex), target: target, extra: extra);
                }
            }
        }

        private static JObject DescribeRun(WwpDerivedEngine.RunResult run) => new JObject
        {
            ["invoked"] = run.Invoked,
            ["reportedSuccess"] = run.EngineReportedSuccess,
            ["failure"] = run.Failure,
            ["messages"] = run.Messages,
            ["messagesTruncated"] = run.MessagesTruncated
        };

        private static JObject DescribeDiff(WwpDerivedDiff diff) => new JObject
        {
            ["changed"] = new JArray(diff.Changed.Select(c => new JObject
            {
                ["name"] = c.After.Name,
                ["type"] = c.After.Type,
                ["parts"] = new JArray(c.Parts.Select(part => new JObject
                {
                    ["part"] = part,
                    ["sha256Before"] = Sha256(c.Before.Parts.TryGetValue(part, out string a) ? a : null),
                    ["sha256After"] = Sha256(c.After.Parts.TryGetValue(part, out string b) ? b : null)
                }))
            })),
            ["created"] = new JArray(diff.Created.Select(o => new JObject { ["name"] = o.Name, ["type"] = o.Type })),
            ["removed"] = new JArray(diff.Removed.Select(o => new JObject { ["name"] = o.Name, ["type"] = o.Type })),
            ["unchanged"] = diff.Unchanged
        };

        private static bool ParentUnchanged(DerivedUnit before, DerivedUnit after) =>
            WwpDerivedDiff.ChangedParts(before.ParentEntry.State, after.ParentEntry.State).Count == 0;

        // The regeneration saved these objects: the next build must not take the compile-only path
        // for them, and the search index must see the new ones.
        private void TouchRegenerated(WwpDerivedDiff diff, DerivedUnit before, DerivedUnit after)
        {
            var names = diff.Changed.Select(c => c.After.Name).Concat(diff.Created.Select(o => o.Name)).ToList();
            if (!ParentUnchanged(before, after)) names.Add(after.ParentEntry.State.Name);
            foreach (string name in names.Distinct(StringComparer.OrdinalIgnoreCase)) WriteService.NotePerTargetWrite(name);
            try
            {
                var index = _objects.GetKbService()?.GetIndexCache();
                if (index == null) return;
                foreach (DerivedEntry entry in after.Derived.Where(d => names.Contains(d.State.Name)))
                    index.UpdateEntry(entry.Object);
            }
            catch (Exception ex) { Logger.Debug("[WWP-REGENERATE] index update skipped: " + ex.Message); }
        }

        // Every part of every object in the unit is written to the snapshot store before anything is
        // regenerated, so a failure that cannot be rolled back still leaves the exact previous text on disk.
        private JObject CaptureUnitSnapshots(DerivedUnit unit)
        {
            string root = EditSnapshotStore.ResolveRoot(_objects.GetKbService().GetKbPath());
            var entries = new JArray();

            void Save(KBObject obj, string part, string text)
            {
                EditSnapshotStore.SnapshotInfo info = EditSnapshotStore.SaveSnapshot(root, obj.Guid.ToString(), part, text);
                if (info == null) throw new InvalidOperationException("No snapshot was written for " + obj.Name + "/" + part + ".");
                entries.Add(new JObject
                {
                    ["object"] = obj.Name, ["part"] = part, ["path"] = info.Path,
                    ["bytes"] = info.Bytes, ["sha256"] = Sha256(text)
                });
            }

            Save(unit.Host, "PatternInstance", unit.HostXml);
            foreach (KeyValuePair<string, string> part in unit.ParentEntry.State.Parts) Save(unit.Parent, part.Key, part.Value);
            foreach (DerivedEntry entry in unit.Derived)
                foreach (KeyValuePair<string, string> part in entry.State.Parts) Save(entry.Object, part.Key, part.Value);
            return new JObject { ["root"] = root, ["files"] = entries.Count, ["entries"] = entries };
        }

        // Puts every object back as captured: derived objects first (so the old parent text is again
        // consistent with the old helpers), then the objects this run created are removed, then the
        // parent and the instance. Proved by a fresh capture of the whole unit against the snapshot.
        private JObject RestoreDerivedUnit(DerivedUnit before, KBObject host, KBObjectPart hostPart, byte[] nativeBytes,
            string hostXml, string applyOnSaveBefore, List<KBObject> created, KBObject parent)
        {
            var errors = new JArray();
            var rollback = new JObject { ["attempted"] = true };
            foreach (DerivedEntry entry in before.Derived)
                RestoreObjectParts(entry.State, entry.Object, errors);

            var deletedCreated = new JArray();
            foreach (KBObject obj in created)
            {
                try { obj.Delete(); deletedCreated.Add(obj.Name); }
                catch (Exception ex) { errors.Add(obj.Name + ": delete failed: " + ExceptionRoot.Message(ex)); }
            }
            RestoreObjectParts(before.ParentEntry.State, parent, errors);

            // The instance is rewritten only if it differs: a run that failed before the engine saved
            // anything (a missing Design System, a stale check) must not touch it, not even to put it back.
            try
            {
                string currentHost = _patterns.ReadPatternPartXmlFresh(host, "PatternInstance", PatternRegistry.WorkWithPlusPatternId,
                    out _, out _, out _);
                if (!ContentMatches(currentHost, hostXml))
                {
                    RestorePartBytes(hostPart, nativeBytes);
                    SaveNativePattern(host, hostPart);
                    if (!IsFalse(applyOnSaveBefore)) WwpApplyOnSaveHelper.TryEnable(host);
                    rollback["hostRestored"] = true;
                }
                else rollback["hostRestored"] = false;
            }
            catch (Exception ex) { errors.Add("PatternInstance: " + ExceptionRoot.Message(ex)); }

            // Independent fresh re-read of everything against the snapshot.
            var mismatches = new JArray();
            foreach (DerivedEntry entry in before.Derived)
            {
                KBObject live = _objects.FindObjectFreshByIdentity(entry.Object) ?? entry.Object;
                List<string> parts = WwpDerivedDiff.ChangedParts(entry.State, CaptureObjectState(live));
                if (parts.Count > 0) mismatches.Add(entry.State.Key + " [" + string.Join(", ", parts) + "]");
            }
            List<string> parentParts = WwpDerivedDiff.ChangedParts(before.ParentEntry.State, CaptureObjectState(parent));
            if (parentParts.Count > 0) mismatches.Add(before.ParentEntry.State.Key + " [" + string.Join(", ", parentParts) + "]");
            string freshHost = null;
            try { freshHost = _patterns.ReadPatternPartXmlFresh(host, "PatternInstance", PatternRegistry.WorkWithPlusPatternId, out _, out _, out _); }
            catch (Exception ex) { errors.Add("PatternInstance re-read: " + ExceptionRoot.Message(ex)); }
            if (!ContentMatches(freshHost, hostXml)) mismatches.Add("PatternInstance");
            foreach (KBObject obj in created)
                if (_objects.FindObject(obj.Name, obj.TypeDescriptor?.Name) != null && !deletedCreated.Any(n => string.Equals(n.ToString(), obj.Name, StringComparison.Ordinal)))
                    mismatches.Add(obj.TypeDescriptor?.Name + ":" + obj.Name + " still exists");

            rollback["errors"] = errors;
            rollback["mismatches"] = mismatches;
            rollback["deletedCreated"] = deletedCreated;
            rollback["verifiedByFreshReread"] = errors.Count == 0 && mismatches.Count == 0;
            rollback["rollbackVerified"] = errors.Count == 0 && mismatches.Count == 0;
            return rollback;
        }

        private void RestoreObjectParts(WwpObjectState snapshot, KBObject obj, JArray errors)
        {
            WwpObjectState current = CaptureObjectState(obj);
            foreach (string part in WwpDerivedDiff.ChangedParts(snapshot, current))
            {
                if (!snapshot.Parts.TryGetValue(part, out string text)) continue;
                try
                {
                    JObject response = JObject.Parse(_write.WriteObject(obj.Name, new JObject
                    {
                        ["type"] = snapshot.Type, ["part"] = part, ["mode"] = "full", ["content"] = text,
                        ["validate"] = "best-effort", ["rollbackOnFailure"] = true, ["forceWrite"] = true
                    }));
                    if (!IsSuccess(response)) errors.Add(snapshot.Key + "/" + part + ": " + (response["message"] ?? response["error"]?["message"]));
                }
                catch (Exception ex) { errors.Add(snapshot.Key + "/" + part + ": " + ExceptionRoot.Message(ex)); }
            }
        }
    }
}
