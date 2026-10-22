using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Principal;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;
using Artech.Architecture.Common.Objects;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// Applies (and re-applies) GeneXus patterns to KBObjects. Equivalent to the
    /// IDE's "Right-click → Apply Pattern" entry point.
    ///
    /// SDK surface lives in Artech.Packages.Patterns.dll inside the GeneXus
    /// install's Packages\ folder, which is NOT statically referenced by the
    /// worker. We load it on demand by reflection so the worker can build/run
    /// on machines without the pattern engine installed; in that case we return
    /// a graceful "pattern_unavailable" status instead of throwing.
    ///
    /// Tests inject a fake <see cref="IPatternEngineAdapter"/> so the unit
    /// suite does not depend on the live SDK / license / open KB.
    /// </summary>
    public class PatternApplyService
    {
        // Pattern keys resolve through the installed-pattern registry (issue #260);
        // WorkWithPlus keeps its dedicated route and is always registered (alias WWP).
        public static readonly Guid WorkWithPlusPatternId = PatternRegistry.WorkWithPlusPatternId;

        internal sealed class WwpEnvironmentContext
        {
            public string InstallationPath { get; set; }
            public string GeneXusConfigPath { get; set; }
            public string UserAppDataPath { get; set; }
            public string EnvironmentConfigPath { get; set; }
            public string ConfigSource { get; set; }
            public bool EnvironmentConfigExists { get; set; }
            public bool? EnvironmentConfigWritable { get; set; }
            public string AccessError { get; set; }

            public JObject ToJson()
            {
                return new JObject
                {
                    ["installationPath"] = InstallationPath ?? "",
                    ["geneXusConfigPath"] = GeneXusConfigPath ?? "",
                    ["userAppDataPath"] = UserAppDataPath ?? "",
                    ["environmentConfigPath"] = EnvironmentConfigPath ?? "",
                    ["configSource"] = ConfigSource ?? "",
                    ["environmentConfigExists"] = EnvironmentConfigExists,
                    ["environmentConfigWritable"] = EnvironmentConfigWritable.HasValue
                        ? (JToken)EnvironmentConfigWritable.Value
                        : JValue.CreateNull(),
                    ["accessError"] = AccessError ?? ""
                };
            }
        }

        /// <summary>
        /// Outcome of the WorkWithPlus direct-attach attempt. A result object rather
        /// than a pile of out parameters because the caller has to act on three
        /// distinct pieces of state that a bool cannot carry: which pipeline stage
        /// failed, what the preflight already knew, and whether the attempt left an
        /// orphan <c>WorkWithPlus&lt;Parent&gt;</c> host behind (issue #330).
        /// </summary>
        internal sealed class WwpAttachResult
        {
            internal bool Attached;
            internal string HostName;
            internal string Template;
            /// <summary>How <see cref="Template"/> was chosen: caller, defaultEmpty, firstRegistered or unlisted.</summary>
            internal string TemplateSource;
            internal string ErrorMessage;
            /// <summary>Envelope code to surface; null falls back to <c>PatternNoOp</c>.</summary>
            internal string ErrorCode;
            /// <summary>Pipeline stage that failed, e.g. <c>createInstance</c>.</summary>
            internal string FailureStage;
            internal JObject EnvironmentContext;
            /// <summary>Preflight findings, so the response explains itself.</summary>
            internal JArray PreflightFindings;
            /// <summary>Name of an orphan host removed by the fail-closed cleanup.</summary>
            internal string RemovedOrphanHost;
            /// <summary>Set when an orphan host survived cleanup; the KB needs attention.</summary>
            internal string OrphanHostWarning;
            /// <summary>Set when the cleanup found no host left in the KB to remove.</summary>
            internal bool OrphanHostAbsent;
            /// <summary>Route that succeeded, when the official one did not.</summary>
            internal string DirectAttachRoute;
            /// <summary>Why the last-resort MSBuild task did not carry the attach.</summary>
            internal string LastResortDetail;
            /// <summary>
            /// Errors the WorkWithPlus package itself reported while validating the host
            /// (captured from its task logger). They name the real cause, e.g. a Design
            /// System that was never imported, which a template change cannot fix.
            /// </summary>
            internal List<string> PackageErrors;

            internal JObject ToJson()
            {
                var json = new JObject();
                // Only emit what actually happened — a null field in `extra` reads as a
                // claim the attach did not make.
                if (!string.IsNullOrEmpty(FailureStage)) json["failureStage"] = FailureStage;
                if (PreflightFindings != null && PreflightFindings.Count > 0) json["attachPreflight"] = PreflightFindings;
                if (!string.IsNullOrEmpty(LastResortDetail)) json["lastResortDetail"] = LastResortDetail;
                if (PackageErrors != null && PackageErrors.Count > 0) json["packageErrors"] = new JArray(PackageErrors);
                if (!string.IsNullOrEmpty(RemovedOrphanHost)) json["removedOrphanHost"] = RemovedOrphanHost;
                if (!string.IsNullOrEmpty(OrphanHostWarning)) json["orphanHostWarning"] = OrphanHostWarning;
                return json;
            }
        }

        private readonly ObjectService _objectService;
        private readonly IPatternEngineAdapter _engine;
        // Test seam: when set, used instead of _objectService.FindObject to resolve
        // the parent KBObject. The live path always uses _objectService.
        private readonly Func<string, KBObject> _findObjectOverride;
        // Installed patterns; null means "resolve lazily from the active installation".
        private PatternRegistry _registry;
        // Pattern instance discovery (instance type matching, child walk); lazily built.
        private PatternAnalysisService _analysis;

        public PatternApplyService(ObjectService objectService)
            : this(objectService, new ReflectionPatternEngineAdapter(), null)
        {
        }

        public PatternApplyService(ObjectService objectService, IPatternEngineAdapter engine)
            : this(objectService, engine, null)
        {
        }

        // Test ctor: lets unit tests bypass the live SDK FindObject lookup.
        internal PatternApplyService(ObjectService objectService, IPatternEngineAdapter engine, Func<string, KBObject> findObjectOverride)
            : this(objectService, engine, findObjectOverride, null, null)
        {
        }

        // Test ctor: also injects the pattern registry and the instance resolver.
        internal PatternApplyService(
            ObjectService objectService,
            IPatternEngineAdapter engine,
            Func<string, KBObject> findObjectOverride,
            PatternRegistry registry,
            PatternAnalysisService analysis = null)
        {
            _objectService = objectService;
            _engine = engine;
            _findObjectOverride = findObjectOverride;
            _registry = registry;
            _analysis = analysis;
        }

        internal PatternRegistry Registry => _registry ?? (_registry = PatternRegistry.Current);

        private PatternAnalysisService Analysis => _analysis ?? (_analysis = new PatternAnalysisService(_objectService, Registry));

        private bool TryResolvePattern(string patternKey, out PatternManifest manifest)
        {
            manifest = null;
            if (string.IsNullOrWhiteSpace(patternKey)) return false;
            return Registry.TryResolve(patternKey, out manifest);
        }

        // Manifest for an already resolved id; an unregistered GUID gets a bare entry.
        private PatternManifest ManifestFor(Guid patternId)
        {
            return Registry.FindById(patternId) ?? new PatternManifest { Id = patternId, Name = patternId.ToString() };
        }

        private KBObject ResolveObject(string objectName)
        {
            if (_findObjectOverride != null) return _findObjectOverride(objectName);
            return _objectService != null ? _objectService.FindObject(objectName) : null;
        }

        /// <summary>
        /// First-time apply of a pattern to a parent object.
        /// </summary>
        public string ApplyPattern(string objectName, string patternKey, JObject settings = null)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(objectName))
                    return McpResponse.Err(code: "MissingObjectName", message: "Object name is required.", hint: "Pass name=<KBObject name>.", target: objectName);
                if (string.IsNullOrWhiteSpace(patternKey))
                    return McpResponse.Err(code: "MissingPatternKey", message: "Pattern key is required.", hint: "Pass pattern='WorkWithPlus' or a known GUID.", target: objectName);

                // Recognize the embedded K2BTools designer before resolving a
                // pattern key or touching the pattern engine. Applying a pattern
                // here cannot create a PatternInstance, so return the same
                // structured unsupported/recovery envelope used by guarded edits.
                KBObject designerApplyCandidate = null;
                try { designerApplyCandidate = ResolveObject(objectName); } catch { /* preserve normal apply diagnostics */ }
                if (designerApplyCandidate != null
                    && K2bWebPanelDesignerService.TryRead(designerApplyCandidate, out var k2bApplyDesigner))
                {
                    return K2bWebPanelDesignerService.BuildEditRejectionResponse(
                        k2bApplyDesigner, designerApplyCandidate.Name, "PatternApply", "patternInstanceUnsupported");
                }

                if (!TryResolvePattern(patternKey, out PatternManifest pattern))
                    return PatternUnavailable(patternKey, "Unknown pattern key. Pass an installed pattern name (see availablePatterns), the alias 'WWP', or a pattern GUID.", Registry.Names());
                Guid patternId = pattern.Id;

                KBObject obj = designerApplyCandidate ?? ResolveObject(objectName);
                if (obj == null)
                {
                    // Reuse existing not-found shape (best-effort: tests may inject a null _objectService)
                    if (_objectService != null)
                        return HealingService.FormatNotFoundError(objectName, _objectService.GetLoadedIndexOrNull());
                    // no-nextStep: _objectService is null only in unit-test injection scenarios; HealingService.FormatNotFoundError carries nextSteps in the normal code path above.
                    return McpResponse.Err(code: "ObjectNotFound", message: "Object not found.", hint: "Verify the name with genexus_query.", target: objectName);
                }

                // Parent-type gate — extracted into TryBuildTypeGateRejection so
                // the rejection envelope shape is unit-testable without a real
                // KBObject. Live template enumeration stays in this scope because
                // it needs _objectService.
                if (pattern.IsWorkWithPlus)
                {
                    string parentType = obj.TypeDescriptor?.Name ?? "";
                    string callerTemplate = settings != null ? settings["template"]?.ToString() : null;
                    List<string> availableTemplates = null;
                    bool isWebPanelKind = IsWwpDirectAttachParentType(parentType);
                    if (isWebPanelKind)
                    {
                        try { availableTemplates = ListWwpWebTemplates(); } catch { /* best-effort */ }
                    }
                    // A GUID key still selects WorkWithPlus; the gate is keyed by name.
                    string gateKey = IsWwpKey(patternKey) ? patternKey : "WorkWithPlus";
                    string reject = TryBuildTypeGateRejection(obj.Name, gateKey, parentType, callerTemplate, availableTemplates);
                    if (reject != null) return reject;
                }
                else if (GetOwnedPatternInstance(obj, pattern) == null)
                {
                    string reject = TryBuildManifestTypeGateRejection(obj.Name, patternKey, pattern, obj.TypeDescriptor?.Name ?? "");
                    if (reject != null) return reject;
                }

                // IDE lock pre-check (parity with ReapplyPattern). The SDK
                // apply call deadlocks 10+ min when the GeneXus IDE holds the
                // object (or its pattern instance) open. Fail fast with a structured
                // IdeHoldsLock error instead of hanging the worker thread.
                string lockReject = TryBuildIdeLockRejection(obj, objectName, pattern);
                if (lockReject != null) return lockReject;

                return ApplyPatternToObject(obj, patternId, patternKey, settings, reapply: false);
            }
            catch (Exception ex)
            {
                Logger.Error("PatternApplyService.ApplyPattern failed: " + ex);
                return McpResponse.Err(code: "ApplyPatternFailed", message: ex.Message, hint: "Check the worker log for stack trace details.", target: objectName);
            }
        }

        /// <summary>
        /// Re-apply (regenerate) an existing pattern instance. <paramref name="objectName"/>
        /// may name the instance itself or its parent object. The pattern comes from the
        /// existing instance; a supplied <paramref name="patternKey"/> is only validated
        /// against it. There is no implicit WorkWithPlus default (issue #260).
        /// </summary>
        public string ReapplyPattern(string objectName, string patternKey, JObject settings = null)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(objectName))
                    return McpResponse.Err(code: "MissingObjectName", message: "Object name is required.", hint: "Pass name=<KBObject name>.", target: objectName);

                KBObject earlyReapplyDesigner = null;
                try { earlyReapplyDesigner = ResolveObject(objectName); } catch { /* preserve normal reapply diagnostics */ }
                if (earlyReapplyDesigner != null
                    && K2bWebPanelDesignerService.TryRead(earlyReapplyDesigner, out var earlyK2bDesigner))
                    return K2bWebPanelDesignerService.BuildEditRejectionResponse(
                        earlyK2bDesigner, earlyReapplyDesigner.Name, "PatternReapply", "patternInstanceUnsupported");

                PatternManifest requested = null;
                if (!string.IsNullOrWhiteSpace(patternKey) && !TryResolvePattern(patternKey, out requested))
                    return PatternUnavailable(patternKey, "Unknown pattern key. Pass an installed pattern name (see availablePatterns), the alias 'WWP', or a pattern GUID.", Registry.Names());

                KBObject obj = earlyReapplyDesigner ?? ResolveObject(objectName);
                if (obj == null)
                {
                    if (_objectService != null)
                        return HealingService.FormatNotFoundError(objectName, _objectService.GetLoadedIndexOrNull());
                    // no-nextStep: _objectService is null only in unit-test injection scenarios; HealingService.FormatNotFoundError carries nextSteps in the normal code path above.
                    return McpResponse.Err(code: "ObjectNotFound", message: "Object not found.", hint: "Verify the name with genexus_query.", target: objectName);
                }

                // The target may be the instance itself: reapply runs on its parent.
                KBObject parent = obj;
                var instances = new Dictionary<Guid, object>();
                var existing = new List<PatternManifest>();
                PatternManifest selfPattern = Analysis.MatchInstancePattern(obj);
                if (selfPattern != null)
                {
                    parent = PatternAnalysisService.ResolveInstanceParent(obj);
                    if (parent == null)
                        return McpResponse.Err(
                            code: "PatternInstanceParentNotFound",
                            message: "'" + obj.Name + "' is a " + selfPattern.Name + " instance, but its parent object could not be resolved.",
                            hint: "Pass the parent object's name instead of the instance name.",
                            nextSteps: new JArray(McpResponse.NextStep(
                                tool: "genexus_inspect",
                                args: new JObject { ["name"] = obj.Name },
                                why: "Inspect the instance to find the object it belongs to.")),
                            target: objectName,
                            extra: new JObject { ["objectName"] = obj.Name, ["pattern"] = selfPattern.Name });
                    existing.Add(selfPattern);
                    instances[selfPattern.Id] = obj;
                }
                else
                {
                    existing.AddRange(FindExistingPatterns(parent, requested, instances));
                }

                var decision = DecideReapplyPattern(requested, existing, targetIsInstance: selfPattern != null);
                // No instance of the requested pattern yet: historical contract, run the full
                // first-apply path (type gate, route gate, IDE lock).
                if (decision.Status == ReapplyDecisionStatus.FirstApply)
                    return ApplyPattern(parent.Name, patternKey, settings);
                if (decision.Status != ReapplyDecisionStatus.Proceed)
                    return BuildReapplyDecisionError(objectName, obj.Name, decision);

                PatternManifest pattern = decision.Pattern;
                instances.TryGetValue(pattern.Id, out object knownInstance);

                if (!pattern.IsWorkWithPlus)
                {
                    string routeReject = TryBuildRouteUnsupportedRejection(objectName, pattern, PatternRoute.Reapply);
                    if (routeReject != null) return routeReject;
                }

                // Friction 2026-05-25 item #6 — IDE lock pre-check. The GeneXus
                // IDE writes <KB>/Locks/<object-guid>.lock when it opens an
                // object. The reapply SDK call deadlocks for >10min when the
                // IDE holds the lock (UpdateParentObject contends on the same
                // KBObject handle). Fail fast with a structured error rather
                // than hanging the worker thread indefinitely.
                string lockReject = TryBuildIdeLockRejection(parent, ReferenceEquals(parent, obj) ? objectName : parent.Name, pattern);
                if (lockReject != null) return lockReject;

                // WorkWithPlus keeps its historical reapply route unchanged.
                if (pattern.IsWorkWithPlus)
                    return ApplyPatternToObject(parent, WorkWithPlusPatternId, "WorkWithPlus", settings, reapply: true);
                return ApplyPatternToObject(parent, pattern.Id, pattern.Name, settings, reapply: true, knownInstance: knownInstance);
            }
            catch (Exception ex)
            {
                Logger.Error("PatternApplyService.ReapplyPattern failed: " + ex);
                return McpResponse.Err(code: "ReapplyPatternFailed", message: ex.Message, hint: "Check the worker log for stack trace details.", target: objectName);
            }
        }

        // Patterns with an instance on the parent: registered-instance children plus the
        // engine's PatternInstance.Get(parent, id) for every registered (and requested)
        // pattern. The first instance object seen per pattern is recorded in `instances`.
        // Existing instance of the pattern on obj, ignoring an instance the SDK matched by name
        // that belongs to another object (WorkWithPlus keeps its historical lookup).
        private object GetOwnedPatternInstance(KBObject obj, PatternManifest pattern)
        {
            object instance = null;
            try { instance = _engine.GetPatternInstance(obj, pattern.Id); } catch { }
            return pattern.IsWorkWithPlus || PatternAnalysisService.InstanceBelongsTo(instance, obj) ? instance : null;
        }

        private List<PatternManifest> FindExistingPatterns(KBObject parent, PatternManifest requested, Dictionary<Guid, object> instances)
        {
            var found = new List<PatternManifest>();
            void Add(PatternManifest m, object instance)
            {
                if (m == null || instance == null) return;
                if (!found.Any(f => f.Id == m.Id)) found.Add(m);
                if (!instances.ContainsKey(m.Id)) instances[m.Id] = instance;
            }

            try
            {
                foreach (var match in Analysis.FindPatternInstances(parent))
                    Add(match.Pattern, match.Candidate.Source);
            }
            catch (Exception ex) { Logger.Debug("Reapply: instance child walk failed (best-effort): " + ex.Message); }

            // With a requested pattern only its instance matters (the child walk above still
            // lists the others); probing every registered pattern costs one SDK call each.
            var probe = requested != null ? new List<PatternManifest> { requested } : Registry.All.ToList();
            foreach (var m in probe)
            {
                try
                {
                    object instance = _engine.GetPatternInstance(parent, m.Id);
                    if (m.IsWorkWithPlus || PatternAnalysisService.InstanceBelongsTo(instance, parent)) Add(m, instance);
                }
                catch (Exception ex) { Logger.Debug("Reapply: GetPatternInstance(" + m.Name + ") failed (best-effort): " + ex.Message); }
            }
            return found;
        }

        internal enum ReapplyDecisionStatus { Proceed, FirstApply, NotFound, Ambiguous, Mismatch }

        internal sealed class ReapplyDecision
        {
            public ReapplyDecisionStatus Status { get; set; }
            public PatternManifest Pattern { get; set; }
            public PatternManifest Requested { get; set; }
            public IReadOnlyList<PatternManifest> Existing { get; set; } = new PatternManifest[0];
        }

        /// <summary>
        /// Pure reapply pattern choice (issue #260). <paramref name="existing"/> lists the
        /// patterns that already have an instance on the target. A requested pattern must be
        /// one of them; without a request exactly one must exist. Never defaults to WorkWithPlus.
        /// A requested pattern with no instance on a parent target keeps the historical
        /// reapply contract and runs as a first apply (<see cref="ReapplyDecisionStatus.FirstApply"/>);
        /// on an instance target it is a mismatch.
        /// </summary>
        internal static ReapplyDecision DecideReapplyPattern(PatternManifest requested, IReadOnlyList<PatternManifest> existing, bool targetIsInstance = false)
        {
            var distinct = new List<PatternManifest>();
            foreach (var m in existing ?? new PatternManifest[0])
            {
                if (m != null && !distinct.Any(d => d.Id == m.Id)) distinct.Add(m);
            }

            var decision = new ReapplyDecision { Requested = requested, Existing = distinct };
            if (requested != null)
            {
                var hit = distinct.FirstOrDefault(m => m.Id == requested.Id);
                if (hit != null) { decision.Status = ReapplyDecisionStatus.Proceed; decision.Pattern = hit; }
                else if (!targetIsInstance) { decision.Status = ReapplyDecisionStatus.FirstApply; decision.Pattern = requested; }
                else decision.Status = ReapplyDecisionStatus.Mismatch;
                return decision;
            }

            if (distinct.Count == 1) { decision.Status = ReapplyDecisionStatus.Proceed; decision.Pattern = distinct[0]; }
            else decision.Status = distinct.Count == 0 ? ReapplyDecisionStatus.NotFound : ReapplyDecisionStatus.Ambiguous;
            return decision;
        }

        internal string BuildReapplyDecisionError(string target, string objectName, ReapplyDecision decision)
        {
            var existingJson = new JArray(decision.Existing.Select(m => new JObject { ["pattern"] = m.Name, ["patternId"] = m.Id.ToString() }));
            switch (decision.Status)
            {
                case ReapplyDecisionStatus.Mismatch:
                    return McpResponse.Err(
                        code: "PatternMismatch",
                        message: "'" + objectName + "' has no " + decision.Requested.Name + " instance; it has " +
                                 string.Join(", ", decision.Existing.Select(m => m.Name)) + ".",
                        hint: "Reapply regenerates the existing instance's own pattern. Pass that pattern or omit 'pattern'.",
                        nextSteps: new JArray(McpResponse.NextStep(
                            tool: "genexus_apply_pattern",
                            args: new JObject { ["name"] = objectName, ["pattern"] = decision.Existing[0].Name, ["reapply"] = true },
                            why: "Reapply the pattern that owns the existing instance.")),
                        target: target,
                        extra: new JObject
                        {
                            ["objectName"] = objectName,
                            ["requestedPattern"] = decision.Requested.Name,
                            ["requestedPatternId"] = decision.Requested.Id.ToString(),
                            ["existingPatterns"] = existingJson
                        });
                case ReapplyDecisionStatus.Ambiguous:
                    return McpResponse.Err(
                        code: "PatternInstanceAmbiguous",
                        message: "'" + objectName + "' has instances of " + decision.Existing.Count + " patterns; name the pattern to reapply.",
                        hint: "Pass pattern=<one of candidates>.",
                        nextSteps: new JArray(McpResponse.NextStep(
                            tool: "genexus_apply_pattern",
                            args: new JObject { ["name"] = objectName, ["pattern"] = decision.Existing[0].Name, ["reapply"] = true },
                            why: "Reapply one named pattern.")),
                        target: target,
                        extra: new JObject { ["objectName"] = objectName, ["candidates"] = existingJson });
                default:
                    string patternName = decision.Requested?.Name;
                    var extra = new JObject { ["objectName"] = objectName, ["availablePatterns"] = new JArray(Registry.Names()) };
                    if (patternName != null) extra["requestedPattern"] = patternName;
                    return McpResponse.Err(
                        code: "PatternInstanceNotFound",
                        message: "'" + objectName + "' has no " + (patternName ?? "registered pattern") + " instance to reapply.",
                        hint: "Apply the pattern first (without reapply).",
                        nextSteps: new JArray(McpResponse.NextStep(
                            tool: "genexus_apply_pattern",
                            args: new JObject { ["name"] = objectName, ["pattern"] = patternName ?? "(pattern name)" },
                            why: "First apply creates the pattern instance.")),
                        target: target,
                        extra: extra);
            }
        }

        internal static bool IsWwpKey(string patternKey)
        {
            return string.Equals(patternKey?.Trim(), "WorkWithPlus", StringComparison.OrdinalIgnoreCase)
                || string.Equals(patternKey?.Trim(), "WWP", StringComparison.OrdinalIgnoreCase);
        }

        internal enum PatternRoute { FirstApply, Reapply }

        /// <summary>
        /// Which apply routes run for a pattern. WorkWithPlus has its dedicated, supported
        /// routes. Every other pattern goes through the generic pattern-engine route, gated by
        /// the two switches below.
        /// </summary>
        internal static class PatternRouteCapabilities
        {
            // ---------------------------------------------------------------------------
            // Generic (non-WorkWithPlus) pattern route switches - issue #260. An unsupported
            // route is rejected with PatternRouteUnsupported before any engine call.
            //
            // Live GX17 U4 + K2BTools 13.1 evidence (2026-09-22):
            // - First apply generates the instance and its objects (K2BEntityServices on a
            //   Transaction created WW<Trn>, <Trn>General, <Trn>Wrapper, export procedures).
            // - Reapply does NOT regenerate: the ApplyPattern(PatternInstance, ApplySettings)
            //   overload throws NRE headless, and ApplyPattern(KBObject, PatternDefinition) on
            //   an existing instance only re-saves the instance while every generated object
            //   keeps its previous version. Reporting that as applied would be a false
            //   success, so reapply stays unsupported until a regenerating route exists.
            //
            // SCOPE OF THAT EVIDENCE (issue #353). The measurement covers those TWO
            // overloads on that ONE combination. It is not a statement that the pattern
            // engine cannot regenerate - and the reason string below used to read exactly
            // that way, which made the diagnostic look like a dead end rather than two
            // exhausted entry points. A third entry point exists and is untested:
            //
            //   Artech.Packages.Patterns.PatternEngine.GenerateInstanceObjects(
            //       IPatternBuildProcess, PatternModel, PatternInstance,
            //       InstanceObjects, ApplySettings, ApplyResults) -> bool
            //
            // an internal static that this build never calls, reached by reflection like
            // the WorkWithPlus projection. All six arguments are obtainable from the
            // installed SDK - for standard WorkWith the build process comes from
            // Artech.Patterns.WorkWith.WorkWithPattern, which is public with a
            // parameterless constructor. Whether it works is UNKNOWN, and the strongest
            // argument against it is that it may be the very path that already throws.
            // It is recorded, not adopted: the switch below stays off until an
            // experiment on a disposable KB says otherwise, and no such fixture exists on
            // the machine this was written on. Evidence and member list:
            // docs/sdk-probe/workwith-regeneration-candidate.md
            // ---------------------------------------------------------------------------
            internal static bool GenericFirstApplySupported = true;
            internal static bool GenericReapplySupported = false;

            /// <summary>
            /// The regenerating entry point found by reading in #353, and never called.
            ///
            /// <para>
            /// Reported in the capability envelope as an <b>unverified lead</b>, not as a
            /// capability. The distinction is the whole point: a caller who sees this must
            /// be able to tell "no route exists" from "a route exists that nobody has run
            /// yet", because the two call for different next moves and only the first is
            /// a dead end.
            /// </para>
            /// </summary>
            internal const string UntestedRegenerationEntryPoint =
                "Artech.Packages.Patterns.PatternEngine.GenerateInstanceObjects";

            /// <summary>
            /// Whether the rejection reason should name the untested candidate.
            ///
            /// <para>
            /// True for the generic route only. WorkWithPlus returns supported before
            /// reaching here, so it keeps its own reason-free answer, and a first-apply
            /// rejection is about a different thing entirely and must not advertise a
            /// regeneration route.
            /// </para>
            /// </summary>
            internal static bool ShouldNameUntestedCandidate(PatternRoute route) =>
                route == PatternRoute.Reapply && !GenericReapplySupported;

            /// <summary>
            /// Whether a successful reapply regenerates the pattern's derived objects,
            /// as opposed to only re-saving the instance.
            ///
            /// <para>
            /// Issue #352: the diagnostic composed an <c>overrideConflict</c> finding
            /// whose remediation said reapply "regenerates the existing pattern
            /// instance" from a single constant, independent of what
            /// <see cref="IsSupported"/> reports for the same pattern and route. So for
            /// a pattern with reapply blocked, the response recommended the route it
            /// refused, and for a hypothetical supported-reapply-but-no-regeneration
            /// build it would still promise regeneration.
            /// </para>
            ///
            /// <para>
            /// This is false today on the evidence recorded above: the only reapply
            /// routes measured leave the generated objects at their previous version.
            /// WorkWithPlus is excluded because it has its own supported routes, and
            /// this switch only governs the generic pattern engine.
            /// </para>
            /// </summary>
            internal const bool ReapplyRegenerates = false;

            /// <summary>
            /// Whether a successful WorkWithPlus reapply regenerates the pattern's derived
            /// objects. <see cref="ReapplyRegenerates"/> describes the generic engine only,
            /// and WorkWithPlus returns early from <see cref="IsSupported"/> because it has
            /// its own supported routes, so its regeneration claim needs its own constant.
            ///
            /// <para>
            /// False. The first apply runs the engine and produces the generated objects,
            /// but for an existing instance <c>ApplyPatternToObject</c> skips the engine
            /// ("existing host detected - skipping engine reapply") and only re-projects
            /// the parent with <c>UpdateParentObject</c>. A reapply therefore returns
            /// <c>PatternApplied</c> while every derived object, such as the
            /// <c>LoadDVCombo&lt;Trn&gt;</c> helper, keeps the contract it had before the
            /// instance changed. The route that rebuilds them is
            /// <c>genexus_wwp action=regenerate_derived</c>.
            /// </para>
            /// </summary>
            internal const bool WwpReapplyRegenerates = false;

            /// <summary>
            /// Whether a reapply of <paramref name="pattern"/> regenerates the pattern's
            /// derived objects. The single source both the diagnose remediation and the
            /// capability report read, so a diagnostic cannot recommend a regeneration
            /// the capability section denies.
            /// </summary>
            internal static bool IsRegeneratingReapply(PatternManifest pattern) =>
                pattern != null && pattern.IsWorkWithPlus
                    ? WwpReapplyRegenerates
                    : ReapplyRegenerates;

            internal static bool IsSupported(PatternManifest pattern, PatternRoute route, out string reason)
            {
                reason = null;
                if (pattern != null && pattern.IsWorkWithPlus) return true;
                bool supported = route == PatternRoute.FirstApply ? GenericFirstApplySupported : GenericReapplySupported;
                if (supported) return true;

                // Issue #353. The claim is narrowed to what was measured - two named
                // overloads, on one recorded combination - and the untested third entry
                // point is named as a lead rather than folded into a blanket "the engine
                // cannot regenerate". A caller has to be able to tell two exhausted
                // options from an unmeasured one, because only the second is still worth
                // someone else's experiment.
                string what = (route == PatternRoute.FirstApply ? "First apply" : "Reapply")
                    + " of " + (pattern?.Name ?? "this pattern")
                    + " through the pattern engine's ApplyPattern overloads is not supported by this MCP build: "
                    + (route == PatternRoute.FirstApply
                        ? "first apply is disabled in this build"
                        : "measured on GX17 U4 + K2BTools 13.1, ApplyPattern(PatternInstance, ApplySettings) throws "
                          + "NullReferenceException headless and ApplyPattern(KBObject, PatternDefinition) re-saves the "
                          + "instance while every generated object keeps its previous version")
                    + ". Apply the pattern in the GeneXus IDE to regenerate the objects.";

                reason = what + (ShouldNameUntestedCandidate(route)
                    ? " This is a statement about those two overloads, not about the engine: " + UntestedRegenerationEntryPoint
                      + " is an entry point that this build does not call, and its effect on derived objects is unverified."
                      + " Re-measured on GX18 18.0.10.184260 without K2BTools: reapply still leaves every derived object at "
                      + "its previous version, so this refusal is confirmed rather than inherited. The NullReferenceException "
                      + "the no-settings path throws is this build's own null-ApplySettings artifact - supplying settings "
                      + "removes the throw but not the non-regeneration, which is why the route stays refused instead of "
                      + "being fixed."
                    : "");
                return false;
            }

            internal static JObject ToJson(PatternManifest pattern)
            {
                JObject Entry(PatternRoute route)
                {
                    bool ok = IsSupported(pattern, route, out string reason);
                    var e = new JObject { ["supported"] = ok };
                    if (!ok)
                    {
                        e["reason"] = reason;
                        // Reported beside the verdict rather than folded into it, so a
                        // client branching on `supported` is unaffected and a client
                        // reading further sees that the absence is scoped, not absolute.
                        if (ShouldNameUntestedCandidate(route))
                        {
                            e["unverifiedRegenerationCandidate"] = UntestedRegenerationEntryPoint;
                            e["candidateStatus"] = "found by reading the installed SDK; never called, effect on derived objects unmeasured";
                        }
                    }
                    return e;
                }
                return new JObject { ["firstApply"] = Entry(PatternRoute.FirstApply), ["reapply"] = Entry(PatternRoute.Reapply) };
            }
        }

        internal static string RouteName(PatternRoute route) => route == PatternRoute.FirstApply ? "firstApply" : "reapply";

        /// <summary>
        /// Guidance emitted when a pattern route (such as generic reapply) is unsupported.
        /// Qualifies that reapply remains unsupported on this route, existing property edits
        /// can persist the instance (structural authoring requires typed actions), but
        /// whether derived objects update is not measured by the edit call and recorded
        /// results vary by environment. If derived objects do not follow, applies via the IDE.
        /// </summary>
        internal const string RouteUnsupportedGuidance =
            "Reapply remains unsupported on this route. Read the existing instance with genexus_read part=PatternInstance. "
            + "Editing existing supported properties through genexus_edit part=PatternInstance is possible; structural authoring "
            + "needs the pattern's typed action. Whether derived objects update is not measured by the edit call and recorded "
            + "results vary by environment; if they do not follow, apply the pattern in the GeneXus IDE.";

        internal static string TryBuildRouteUnsupportedRejection(string target, PatternManifest pattern, PatternRoute route)
        {
            if (PatternRouteCapabilities.IsSupported(pattern, route, out string reason)) return null;
            return McpResponse.Err(
                code: "PatternRouteUnsupported",
                message: reason,
                // Issues #352, #478: distinguish reapply refusal from property editing.
                // Reapply remains unsupported. Editing existing supported properties can
                // persist the instance (structural authoring requires typed actions); whether
                // derived objects update is not measured by the edit call and recorded results
                // vary by environment. If derived objects do not follow, apply in the IDE.
                hint: RouteUnsupportedGuidance,
                nextSteps: new JArray(McpResponse.NextStep(
                    tool: "genexus_read",
                    args: new JObject { ["name"] = target, ["part"] = "PatternInstance" },
                    why: "Read the existing pattern instance instead of applying the pattern.")),
                target: target,
                extra: new JObject
                {
                    ["pattern"] = pattern.Name,
                    ["patternId"] = pattern.Id.ToString(),
                    ["route"] = RouteName(route)
                });
        }

        /// <summary>
        /// Manifest-driven parent-type gate for patterns other than WorkWithPlus (pure).
        /// Returns the rejection envelope or null. A pattern without an installed manifest
        /// (bare GUID) declares no parent types, so it is not gated.
        /// </summary>
        internal static string TryBuildManifestTypeGateRejection(string objName, string patternKey, PatternManifest pattern, string parentType)
        {
            if (pattern == null || pattern.IsWorkWithPlus || string.IsNullOrEmpty(pattern.ManifestPath)) return null;
            parentType = parentType ?? "";
            if (!pattern.IsParentless && pattern.AcceptsParentType(parentType)) return null;

            var extra = new JObject
            {
                ["patternKey"] = patternKey,
                ["pattern"] = pattern.Name,
                ["parentType"] = parentType,
                ["validParentTypes"] = new JArray(pattern.ParentObjectTypes.ToArray())
            };
            if (pattern.IsParentless)
            {
                return McpResponse.Err(
                    code: "PatternParentTypeMismatch",
                    message: pattern.Name + " has no parent object, so it cannot be applied to an object.",
                    hint: "Its single instance ('" + (pattern.FormatInstanceName(null) ?? pattern.Name) + "') is edited directly with genexus_read / genexus_edit part=PatternInstance.",
                    nextSteps: new JArray(McpResponse.NextStep(
                        tool: "genexus_read",
                        args: new JObject { ["name"] = pattern.FormatInstanceName(null) ?? pattern.Name, ["part"] = "PatternInstance" },
                        why: "Read the pattern's instance instead of applying it.")),
                    target: objName,
                    extra: extra);
            }

            string valid = string.Join(", ", pattern.ParentObjectTypes);
            return McpResponse.Err(
                code: "PatternParentTypeMismatch",
                message: pattern.Name + " cannot be applied to a " + (parentType.Length > 0 ? parentType : "object of unknown type") + ".",
                hint: "Apply " + pattern.Name + " only to: " + valid + ".",
                nextSteps: new JArray(McpResponse.NextStep(
                    tool: "genexus_apply_pattern",
                    args: new JObject { ["name"] = objName, ["pattern"] = patternKey },
                    why: "Call on a " + valid + " instead.")),
                target: objName,
                extra: extra);
        }

        /// <summary>
        /// Friction 2026-05-25 item #6 — returns a structured "IDE holds lock"
        /// rejection envelope when GeneXus IDE has the object open, otherwise
        /// null. Looks for <KB>/Locks/<guid>.lock for the parent AND its WWP
        /// host (WorkWithPlus&lt;Name&gt;). Best-effort: any I/O failure logs
        /// and returns null so the SDK call proceeds.
        /// </summary>
        internal string TryBuildIdeLockRejection(KBObject parent, string objectName, PatternManifest pattern = null)
        {
            try
            {
                string kbPath = null;
                try { kbPath = _objectService?.GetKbService()?.GetKbPath(); } catch { /* best-effort */ }
                if (string.IsNullOrWhiteSpace(kbPath)) return null;

                // GetKbPath returns either the .gkb file or the directory; normalise.
                string kbDir = System.IO.File.Exists(kbPath)
                    ? System.IO.Path.GetDirectoryName(kbPath)
                    : kbPath;
                if (string.IsNullOrWhiteSpace(kbDir)) return null;

                string locksDir = System.IO.Path.Combine(kbDir, "Locks");
                if (!System.IO.Directory.Exists(locksDir)) return null;

                var hits = new System.Collections.Generic.List<JObject>();
                void Probe(KBObject obj, string role)
                {
                    if (obj == null) return;
                    var guid = obj.Guid;
                    if (guid == Guid.Empty) return;
                    string lockFile = System.IO.Path.Combine(locksDir, guid.ToString("D") + ".lock");
                    if (!System.IO.File.Exists(lockFile)) return;

                    // Distinguish IDE locks from our own worker's locks. The
                    // worker writes its OWN .lock files when it opens objects;
                    // we should not refuse our own session. The IDE's lock
                    // files are typically created/touched at session start and
                    // remain throughout — the safest signal is "is the file
                    // currently held open by another process". F_OK is enough
                    // for now; refine via FileShare probe if false-positives
                    // appear.
                    hits.Add(new JObject
                    {
                        ["role"] = role,
                        ["object"] = obj.Name,
                        ["guid"] = guid.ToString("D"),
                        ["lockFile"] = lockFile,
                        ["lockedAtUtc"] = System.IO.File.GetLastWriteTimeUtc(lockFile).ToString("o")
                    });
                }

                Probe(parent, "parent");

                if (pattern == null || pattern.IsWorkWithPlus)
                {
                    // Probe the WWP host too: WorkWithPlus<Name> is the conventional naming.
                    if (_objectService != null && !string.IsNullOrEmpty(parent?.Name))
                    {
                        var hostName = "WorkWithPlus" + parent.Name;
                        var host = _objectService.FindObject(hostName);
                        Probe(host, "wwpHost");
                    }
                }
                else if (parent != null)
                {
                    // Other patterns: probe the instance the engine associates with the
                    // parent, else the object named by the manifest's instance template.
                    KBObject instanceObj = null;
                    try { instanceObj = _engine.GetPatternInstance(parent, pattern.Id) as KBObject; } catch { /* best-effort */ }
                    if (instanceObj == null && _objectService != null)
                    {
                        string instanceName = pattern.FormatInstanceName(parent.Name);
                        if (!string.IsNullOrWhiteSpace(instanceName)) instanceObj = _objectService.FindObject(instanceName);
                    }
                    Probe(instanceObj, "patternHost");
                }

                if (hits.Count == 0) return null;

                string lockMsg = "GeneXus IDE has " + (hits.Count == 1 ? "an object" : "objects") + " open that would deadlock the SDK reapply call. Close the tab(s) in the IDE (or save and switch to another object) and retry.";
                string lockHint = "Close '" + (string)hits[0]["object"] + "' (and any other listed object) in the GeneXus IDE before calling reapply. The MCP worker and the IDE cannot hold the same KBObject handle simultaneously.";
                var lockExtra = new JObject { ["lockedObjects"] = new JArray(hits) };
                var errEnv = JObject.Parse(McpResponse.Err(
                    code: "IdeHoldsLock",
                    message: lockMsg,
                    hint: lockHint,
                    nextSteps: new JArray(McpResponse.NextStep(
                        tool: "genexus_lifecycle",
                        args: new JObject { ["action"] = "status" },
                        why: "Check if the worker is still responsive after closing the IDE tab.")),
                    target: objectName,
                    extra: lockExtra));
                return errEnv.ToString(Newtonsoft.Json.Formatting.None);
            }
            catch (Exception ex)
            {
                Logger.Info("[IDE-LOCK-CHECK] best-effort failed: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Friction 2026-05-25 — invoke <c>DVelop.Patterns.WorkWithPlus.Helpers.PatternInstancePackageInterface.SetPatternApplyOnSave(host)</c>
        /// so the IDE's "Apply this pattern on save" checkbox stays on after a
        /// reapply (or after a user manually unchecked it in the IDE). Pure
        /// reflection over WWP types; best-effort — any miss is logged and
        /// returns false instead of throwing. Returns true when the SDK
        /// method was found and invoked without exception.
        /// </summary>
        internal bool TryEnableApplyOnSave(KBObject host)
        {
            return GxMcp.Worker.Helpers.WwpApplyOnSaveHelper.TryEnable(host);
        }

        internal string ApplyPatternToObject(KBObject obj, Guid patternId, string patternKey, JObject settings, bool reapply, string objectNameForResponse = null, object knownInstance = null)
        {
            if (obj != null && K2bWebPanelDesignerService.TryRead(obj, out var embeddedDesigner))
                return K2bWebPanelDesignerService.BuildEditRejectionResponse(
                    embeddedDesigner, obj.Name, reapply ? "PatternReapply" : "PatternApply", "patternInstanceUnsupported");

            // Route by pattern identity, not by the key spelling: every WorkWithPlus key
            // (name, alias, GUID) takes the WorkWithPlus route below.
            if (patternId != WorkWithPlusPatternId)
                return ApplyGenericPatternToObject(obj, ManifestFor(patternId), patternKey, settings, reapply, objectNameForResponse, knownInstance);

            var phaseTimer = System.Diagnostics.Stopwatch.StartNew();
            var phases = new System.Collections.Generic.List<string>();
            void Phase(string name) { phases.Add($"{name}={phaseTimer.ElapsedMilliseconds}ms"); phaseTimer.Restart(); }

            // Surfaced on the response when reapply projection runs long — agents
            // need a structured signal (not just a log line) to suggest closing
            // an IDE tab or retrying later. See SLOW_REAPPLY_THRESHOLD_MS.
            long projectionElapsedMs = 0;

            // F17 (perf-gated): SdkSurfaceProbe.Run walks every loaded SDK assembly,
            // dumps all public types/methods/properties and writes a multi-MB raw.json.
            // It used to run on EVERY apply (~5-15s of pure waste in production calls).
            // It's a debugging artifact — opt in with GX_MCP_SDK_PROBE=1 when you need
            // the dump, or call genexus_sdk_probe explicitly.
            if (_objectService != null
                && string.Equals(Environment.GetEnvironmentVariable("GX_MCP_SDK_PROBE"), "1", StringComparison.Ordinal))
            {
                try
                {
                    var fullProbe = SdkSurfaceProbe.Run(Environment.GetEnvironmentVariable("GX_MCP_SDK_PROBE_DIR"));
                    Logger.Info("[SDK-PROBE] Wrote SDK surface: " + fullProbe.RawJsonPath +
                        " (assemblies=" + fullProbe.AssembliesScanned +
                        ", types=" + fullProbe.TypesScanned +
                        ", generators=" + fullProbe.GeneratorCandidates + ")");
                }
                catch (Exception ex) { Logger.Debug("[SDK-PROBE] skipped: " + ex.Message); }
            }

            Phase("sdkProbeGated");
            // Probe the engine; if license/package is missing we degrade gracefully.
            object patternDefinition = _engine.GetPatternDefinition(patternId);
            if (patternDefinition == null)
            {
                return PatternUnavailable(patternKey, "WorkWithPlus pattern not loaded — check license / package install");
            }

            // An explicit template is honoured or refused, never swapped for another one.
            // Validated before anything touches the KB so a refusal changes nothing.
            string explicitTemplateRejection = TryResolveExplicitWwpTemplate(obj, settings, objectNameForResponse, out string explicitTemplate);
            if (explicitTemplateRejection != null) return explicitTemplateRejection;

            // Detect existing instance to decide between first-apply and re-apply.
            object existingInstance = _engine.GetPatternInstance(obj, patternId);

            // Stale-metadata guard: GetPatternInstance can return non-null even after
            // the user deleted the generated host (WorkWithPlus<Name>) in a prior
            // session — the SDK keeps the PatternInstance metadata on the parent.
            // Without this probe, reapply would skip the engine apply and produce a
            // minimalist PatternInstance (empty <table/>). Treat a missing host as
            // first-apply so the engine regenerates the family.
            bool staleInstanceRecovered = false;
            if (existingInstance != null && _objectService != null && !string.IsNullOrEmpty(obj?.Name))
            {
                try
                {
                    var wwpHostProbe = _objectService.FindObject("WorkWithPlus" + obj.Name);
                    if (wwpHostProbe == null)
                    {
                        Logger.Info("ApplyPattern: PatternInstance metadata present but WorkWithPlus" + obj.Name + " host missing — treating as first-apply (stale metadata recovery).");
                        existingInstance = null;
                        staleInstanceRecovered = true;
                    }
                }
                catch (Exception ex) { Logger.Debug("ApplyPattern: stale-host probe failed (best-effort): " + ex.Message); }
            }

            bool wasFirstApply = existingInstance == null;

            // The reapply route below only re-projects the existing instance, so a
            // requested template that differs from the persisted one would be silently
            // ignored. Refuse instead of reporting a template the instance does not use.
            if (explicitTemplate != null && existingInstance != null)
            {
                string currentInstanceXml = null;
                try { currentInstanceXml = Analysis.ReadPatternPartXmlFresh(obj, "PatternInstance", WorkWithPlusPatternId, out _, out _, out _); }
                catch (Exception ex) { Logger.Debug("Explicit template: fresh PatternInstance read failed: " + ex.Message); }
                if (!ExplicitTemplateMatches(currentInstanceXml, explicitTemplate))
                {
                    string changeTarget = objectNameForResponse ?? obj?.Name ?? "";
                    var changeExtra = new JObject { ["requestedTemplate"] = explicitTemplate };
                    string persistedTemplate = ReadPersistedWwpTemplate(currentInstanceXml);
                    if (persistedTemplate != null) changeExtra["persistedTemplate"] = persistedTemplate;
                    return McpResponse.Err(
                        code: "PatternTemplateChangeUnsupported",
                        message: "'" + changeTarget + "' already has a WorkWithPlus instance that does not use template '" + explicitTemplate +
                            "'. Changing the template of an existing instance is not supported by this route; no changes were applied.",
                        hint: "Edit the existing instance with the typed genexus_wwp actions, or apply the template to a fresh object.",
                        nextSteps: new JArray(McpResponse.NextStep(
                            tool: "genexus_read",
                            args: new JObject { ["name"] = changeTarget, ["part"] = "PatternInstance", ["limit"] = 0 },
                            why: "Read the existing instance and its template before choosing an edit.")),
                        target: changeTarget,
                        extra: changeExtra);
                }
            }

            PatternApplyResult result;
            try
            {
                // The SDK's `PatternEngine.ApplyPattern(PatternInstance, ApplySettings)`
                // overload may not be present on every GeneXus install (observed missing on
                // 18.0.7.179127). When that happens, fall back to the void overload — the
                // SDK detects the existing instance and re-applies. Wrap each reapply call
                // so the fallback is transparent to callers.
                if (existingInstance != null)
                {
                    // Existing host detected. The engine's reapply overload throws NRE
                    // on this install (needs services we can't provide). Skip it and
                    // project via UpdateParentObject below — that's the only generator
                    // step that matters anyway.
                    result = new PatternApplyResult();
                    wasFirstApply = false;
                    Logger.Info("ApplyPattern: existing host detected — skipping engine reapply (NRE-prone), will project via UpdateParentObject.");
                }
                else
                {
                    result = _engine.ApplyPattern(obj, patternDefinition, settings);
                    wasFirstApply = true;
                }
                Phase("engineApply");
                // The engine apply persisted the host's derived objects. targetName is
                // declared further down, so the equivalent in-scope expression is used
                // here; it is the same value targetName is assigned below.
                WriteService.NotePerTargetWrite(objectNameForResponse ?? obj?.Name ?? "");
            }
            catch (Exception ex)
            {
                string errName = objectNameForResponse ?? obj?.Name ?? "";
                Logger.Error("PatternEngine apply failed for '" + errName + "': " + ex);
                var errExtra = new JObject { ["patternKey"] = patternKey };

                // Issue #353 follow-up. A NullReferenceException here is not a missing or
                // unlicensed package. Measured on GX18 18.0.10.184260: reapply with no
                // settings object throws NRE from inside
                // PatternEngine.ApplyPattern(PatternInstance, ApplySettings), because this
                // path passes `null` for ApplySettings; supplying a real one
                // (IsFullGeneration + ForceSave) makes the same call return success. So the
                // NRE is this adapter's own null-settings artifact, and telling a caller to
                // go check licensing sends them after the wrong thing.
                //
                // What that success is worth is a separate, worse problem, and it is why the
                // route stays refused rather than "fixed": with settings supplied, the same
                // reapply returned PatternApplied while every derived object kept its
                // previous version, verified against a known pending difference and with an
                // instance-save control in the same run proving the measurement worked.
                // Removing the NRE would therefore trade a loud refusal for a silent no-op.
                bool nullSettingsNre = reapply && ex is NullReferenceException;
                if (nullSettingsNre)
                {
                    errExtra["cause"] = "nullApplySettings";
                    errExtra["note"] = "The SDK threw while this path passed a null ApplySettings. This is not a "
                        + "package or licensing problem. Supplying settings avoids the throw, but the call still does "
                        + "not regenerate the derived objects - so the reapply route stays refused. See "
                        + "docs/sdk-probe/workwith-regeneration-candidate.md";
                }

                return McpResponse.Err(
                    code: "PatternEngineApplyFailed",
                    message: ex.Message,
                    hint: nullSettingsNre
                        ? "This NullReferenceException comes from reapply being called without a settings object, not "
                          + "from the pattern package: the same call with settings supplied does not throw, but it also "
                          + "does not regenerate the pattern's objects. Headless reapply of a non-WorkWithPlus pattern "
                          + "remains unsupported - apply the pattern in the GeneXus IDE to regenerate."
                        : "Verify the pattern package is installed and the KB is open.",
                    nextSteps: new JArray(nullSettingsNre
                        ? McpResponse.NextStep(
                            tool: "genexus_edit",
                            args: new JObject
                            {
                                ["name"] = errName,
                                ["part"] = "PatternInstance",
                                ["mode"] = "full"
                            },
                            why: "Saving the instance is the route measured to regenerate the derived objects on GX18.")
                        : McpResponse.NextStep(
                            tool: "genexus_apply_pattern",
                            args: new JObject { ["name"] = errName, ["pattern"] = patternKey },
                            why: "Retry after verifying the pattern package and KB state.")),
                    target: errName,
                    extra: errExtra);
            }

            string targetName = objectNameForResponse ?? obj?.Name ?? "";

            // F17: when there's an existing PatternInstance host (re-apply case),
            // re-invoke UpdateParentObject so edits to the host's PatternInstance get
            // projected onto the parent's WebForm. We re-resolve the host KBObject
            // from disk first because the cached `existingInstance` may carry stale
            // PatternInstance state from before genexus_edit landed.
            if (existingInstance != null && _objectService != null)
            {
                KBObject reappliedHost = null;
                // Friction 2026-05-25 — projection step (`UpdateParentObject`)
                // can deadlock for 10+ minutes when the IDE has the host or
                // parent open in a tab. We can't safely abort an STA call,
                // but we CAN time it and surface elapsedMs in the response so
                // callers see how long it took. Combined with [APPLY-PATTERN]
                // log lines, anyone watching can identify hangs early.
                var projectionSw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    var freshHost = _objectService.FindObject("WorkWithPlus" + obj.Name);
                    if (freshHost != null)
                    {
                        TryInvokeBuildProcessUpdateParent(obj, freshHost);
                        reappliedHost = freshHost;
                    }
                    else if (existingInstance is KBObject existingHostObj)
                    {
                        TryInvokeBuildProcessUpdateParent(obj, existingHostObj);
                        reappliedHost = existingHostObj;
                    }
                }
                catch (Exception ex) { Logger.Info("Reapply UpdateParentObject best-effort: " + ex.Message); }
                projectionSw.Stop();
                projectionElapsedMs = projectionSw.ElapsedMilliseconds;
                if (projectionSw.ElapsedMilliseconds > 30000)
                {
                    // 30s threshold — IDE-hold-on-tab deadlocks were 10+ min.
                    // Clean reapply on a free object completes in ~1-3s. Log
                    // at warn so dev can correlate slow reapplies with IDE
                    // tab state. The .lock file pre-check (TryBuildIdeLockRejection)
                    // is a false-negative on per-tab opens — see project
                    // memory `feedback_mcp_friction_2026_05_25` item #6.
                    Logger.Warn("[APPLY-PATTERN] projection took " + projectionSw.ElapsedMilliseconds + "ms — likely IDE-tab-hold contention. Close any tab on '" + obj?.Name + "' or 'WorkWithPlus" + obj?.Name + "' if reapplies keep timing out.");
                }
                phases.Add($"projection={projectionSw.ElapsedMilliseconds}ms");

                // Friction 2026-05-25 — first-apply called SetPatternApplyOnSave
                // via PatternInstancePackageInterface so the IDE's "Apply this
                // pattern on save" checkbox lit up; reapply previously skipped
                // it, so a host whose checkbox was manually unchecked stayed
                // unchecked even after a successful reapply. Now always re-
                // assert the flag on reapply — best-effort, reflection-based,
                // matches the helper used in TryFirstApplyViaPackage above.
                if (reappliedHost != null)
                {
                    try { TryEnableApplyOnSave(reappliedHost); }
                    catch (Exception ex) { Logger.Info("Reapply SetPatternApplyOnSave best-effort: " + ex.Message); }
                }
            }

            // Compute the real generated-objects list. The adapter's GeneratedObjects
            // collection is normally empty (void overload returns no names), so we look
            // up the canonical WWP family by name pattern instead. Cheap: O(family_size)
            // FindObject lookups, vs O(model_size) for a pre/post diff. Also avoids the
            // race where the SDK registers new objects asynchronously after Invoke returns.
            var generated = LookupWwpFamilyByConvention(obj);
            Phase("lookupFamily");
            if (result?.GeneratedObjects != null)
            {
                foreach (var name in result.GeneratedObjects)
                {
                    if (!string.IsNullOrEmpty(name) && !generated.Contains(name))
                        generated.Add(name);
                }
            }

            // Keep the search index in sync with the generated family so the agent's next
            // list_objects/query reflects what the apply created. Without this, the host
            // and WW/Export* siblings remain invisible for minutes until a full reindex.
            // FindObject is O(1) on the typed index — avoid model.Objects.GetAll() loops
            // that would be O(generated × model_size) and torch large-KB performance.
            if (generated.Count > 0 && _objectService != null)
            {
                try
                {
                    var idx = _objectService.GetKbService()?.GetIndexCache();
                    if (idx != null)
                    {
                        foreach (var name in generated)
                        {
                            try
                            {
                                var o = _objectService.FindObject(name);
                                if (o != null) idx.UpdateEntry(o);
                            }
                            catch { /* per-name best-effort */ }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Debug("ApplyPattern: index UpdateEntry sweep skipped: " + ex.Message);
                }
            }
            Phase("indexUpdate");

            string parentTypeName = obj?.TypeDescriptor?.Name ?? "";
            string bindingMode = GetWwpBindingMode(parentTypeName);

            // Friction 2026-05-26 — apply_pattern reapply previously returned
            // status=Success even when the parent's Events-by-WorkWithPlus
            // generation produced src0265 ("Invalid attribute") / src0216
            // ("Visible invalid property") errors at save time (visible only
            // when the user tried to Ctrl+S in the IDE). Run the parent's
            // standard SDK validation here so any pre-existing references to
            // controls that the fresh PatternInstance no longer knows surface
            // in the response. Best-effort: if SdkDiagnosticsHelper throws
            // we still return Success — diagnostics aren't load-bearing.
            JArray patternValidationIssues = null;
            try
            {
                if (obj != null)
                {
                    var issues = GxMcp.Worker.Helpers.SdkDiagnosticsHelper.GetDiagnostics(obj);
                    if (issues != null && issues.Count > 0)
                    {
                        // Filter to ERROR-level diagnostics in pattern-generated
                        // events. We don't want to surface unrelated warnings
                        // or other-part issues here.
                        var filtered = new JArray();
                        foreach (var t in issues)
                        {
                            string sev = t["severity"]?.ToString();
                            string code = t["code"]?.ToString() ?? "";
                            // Surface known WWP-projection error codes plus
                            // any Error-severity issue on the parent's events.
                            if (string.Equals(sev, "Error", StringComparison.OrdinalIgnoreCase) ||
                                code.StartsWith("src0265", StringComparison.OrdinalIgnoreCase) ||
                                code.StartsWith("src0216", StringComparison.OrdinalIgnoreCase))
                            {
                                filtered.Add(t);
                            }
                        }
                        if (filtered.Count > 0) patternValidationIssues = filtered;
                    }
                }
            }
            catch (Exception ex) { Logger.Debug("ApplyPattern: post-projection validate best-effort: " + ex.Message); }
            Phase("postValidate");

            // v2.8.0: PartialFailure → "partial" status; Success → "ok".
            // All payload fields go under result.
            var patternResult = new JObject
            {
                ["parentType"] = parentTypeName,
                ["bindingMode"] = bindingMode,
                ["patternKey"] = patternKey,
                ["patternId"] = patternId.ToString(),
                ["wasFirstApply"] = wasFirstApply,
                ["generatedObjects"] = new JArray(generated),
                ["errors"] = new JArray(result?.Errors ?? Enumerable.Empty<string>())
            };
            // A first apply runs the engine. A reapply of an existing instance only re-projects the
            // parent, so a derived object (the LoadDVCombo helper, the WW family) is NOT rebuilt:
            // saying PatternApplied without this would read as "everything is consistent".
            patternResult["derivedObjectsRegenerated"] = wasFirstApply;
            if (!wasFirstApply)
                patternResult["derivedObjectsNote"] = "The existing instance was re-projected onto the parent only; its derived objects "
                    + "were not rebuilt. Use genexus_wwp action=regenerate_derived to rebuild them and check their call contract.";

            // Issue #359: say which expected family members were not observed.
            //
            // The reporter's ask was "generate the objects, or fail instead of
            // reporting PatternApplied". Failing hard is the wrong half of that
            // alternative here, and the reason is in the SDK comment above this
            // lookup: the generator registers new objects asynchronously after Invoke
            // returns, so a member missing at this instant may be seconds from
            // existing. Turning that race into a hard failure would reject applies
            // that succeed.
            //
            // What is not acceptable is a bare success that silently omits part of its
            // own result, because that reads identically whether the family was
            // generated, still pending, or never generated - and the first two are the
            // common cases while the third is the one worth surfacing. So the names go
            // in the response and the caller decides; nothing here is a verdict.
            var missingFamily = BuildMissingWwpFamily(obj?.Name, generated);
            if (missingFamily.Count > 0)
            {
                patternResult["expectedFamilyObjects"] =
                    new JArray(WwpFamilyNaming.ExpectedFamilyNames(obj?.Name));
                patternResult["familyMissing"] = new JArray(missingFamily);
                patternResult["familyComplete"] = false;
                patternResult["familyNote"] =
                    "The PatternInstance was attached, but these generated objects were not "
                    + "visible when the apply returned. WorkWithPlus registers them "
                    + "asynchronously, so this can mean 'still being generated' rather than "
                    + "'not generated'. Re-check with genexus_query on the name; if it is still "
                    + "absent after genexus_lifecycle action=index, the generator did not run and "
                    + "the IDE's Apply Pattern is the working route.";
            }
            JArray patternWarnings = null;
            if (patternValidationIssues != null)
            {
                patternWarnings = new JArray();
                foreach (var issue in patternValidationIssues) patternWarnings.Add(issue);
            }
            // Mutable response JObject for post-processing (slowReapply, NoOp, etc.).
            // Will be converted to canonical at the bottom of this method.
            var response = new JObject
            {
                // Internal tracking field (not emitted); converted to canonical McpResponse before return.
                ["_opStatus"] = patternValidationIssues != null ? "PartialFailure" : "Success",
                ["target"] = targetName,
                ["_result"] = patternResult
            };
            if (staleInstanceRecovered)
            {
                patternResult["staleInstanceRecovered"] = true;
                patternResult["staleInstanceHint"] = "PatternInstance metadata was present on the parent but the generated WorkWithPlus host was missing (typically from a prior delete). Engine apply was re-run as if this were a fresh apply so the family regenerates instead of producing an empty PatternInstance.";
            }
            if (patternValidationIssues != null)
            {
                patternResult["patternValidationIssues"] = patternValidationIssues;
                // hint surfaces in the partial warnings below
            }

            // Reapply projection-time surfacing. The STA-bound SDK call can't be
            // hard-aborted from another thread, but a structured signal lets the
            // agent decide to close the IDE tab / retry without re-reading logs.
            // Threshold matches the warn-log at the projection site.
            string projectionTimedOutCode = null;
            if (reapply && projectionElapsedMs > 30000)
            {
                patternResult["slowReapply"] = true;
                patternResult["projectionMs"] = projectionElapsedMs;
                patternResult["slowReapplyHint"] = $"Reapply projection took {projectionElapsedMs}ms (threshold 30000ms). " +
                                              $"The most common cause is the GeneXus IDE holding '{targetName}' or 'WorkWithPlus{targetName}' open in a tab — close it and retry. " +
                                              $"If no IDE is running, the SDK may be hung on a stale handle; restart the worker via genexus_worker_reload mode=hard.";

                long hardTimeoutMs = 300_000;
                try
                {
                    var raw = Environment.GetEnvironmentVariable("GENEXUS_MCP_REAPPLY_TIMEOUT_MS");
                    if (!string.IsNullOrWhiteSpace(raw) && long.TryParse(raw, out var parsed) && parsed > 0)
                    {
                        hardTimeoutMs = parsed;
                    }
                }
                catch { /* env read best-effort */ }
                if (projectionElapsedMs > hardTimeoutMs)
                {
                    projectionTimedOutCode = "ProjectionTimedOut";
                    patternResult["recoveryRequired"] = true;
                    patternResult["recoveryHint"] = $"Projection ran past the {hardTimeoutMs}ms hard-timeout. " +
                                                "The worker may hold stale SDK handles after this. " +
                                                "Call genexus_worker_reload mode=hard, or reconnect MCP via /mcp.";
                }
            }

            // Surface the WWP host (`WorkWithPlus<X>`) explicitly when present.
            string host = generated.FirstOrDefault(n => n.StartsWith("WorkWithPlus", StringComparison.Ordinal));
            if (host == null && obj != null && _objectService != null)
            {
                try
                {
                    var hostObj = _objectService.FindObject("WorkWithPlus" + obj.Name);
                    if (hostObj != null) host = hostObj.Name;
                }
                catch { /* lookup best-effort */ }
            }
            if (!string.IsNullOrEmpty(host)) patternResult["patternHost"] = host;

            // F23: surface available `WorkWithPlus for Web Template` names.
            if (IsWwpDirectAttachParentType(obj?.TypeDescriptor?.Name))
            {
                try
                {
                    var templates = ListWwpWebTemplates();
                    if (templates.Count > 0)
                    {
                        patternResult["availableTemplates"] = new JArray(templates);
                    }
                }
                catch { /* best-effort */ }
            }

            // No-op detection.
            bool isNoOp = false;
            if (generated.Count == 0 && host == null && obj != null)
            {
                bool targetHasPatternInstance = false;
                try
                {
                    foreach (var part in obj.Parts)
                    {
                        var p = part as KBObjectPart;
                        if (p == null) continue;
                        if (string.Equals(p.Name, "PatternInstance", StringComparison.OrdinalIgnoreCase) ||
                            p.GetType().Name.IndexOf("PatternInstance", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            targetHasPatternInstance = true;
                            break;
                        }
                    }
                }
                catch { /* best-effort */ }

                if (!targetHasPatternInstance)
                {
                    // explicitTemplate is already resolved to a registered template (or the
                    // call was refused above); null lets the attach pick the default.
                    WwpAttachResult attach;
                    try
                    {
                        attach = TryPackageInterfaceAttach(obj, explicitTemplate);
                    }
                    catch (Exception ex)
                    {
                        attach = new WwpAttachResult
                        {
                            FailureStage = "attach",
                            ErrorMessage = ex.GetType().Name + ": " + ex.Message
                        };
                    }
                    string createdHostName = attach.HostName;
                    string usedTemplate = attach.Template;

                    if (attach.Attached)
                    {
                        response["_opStatus"] = "Success";
                        patternResult["wasFirstApply"] = true;
                        patternResult["directAttach"] = true;
                        patternResult["directAttachRoute"] = attach.DirectAttachRoute ?? "PatternInstancePackageInterface";
                        patternResult["template"] = usedTemplate;
                        if (!string.IsNullOrEmpty(attach.TemplateSource)) patternResult["templateSource"] = attach.TemplateSource;
                        patternResult["directAttachNote"] = "Attached via the official WWP package API (CreatePatternInstanceWithTemplate + SetPatternApplyOnSave + ValidateAndSave + IPatternBuildProcess.UpdateParentObject). Host '" + createdHostName + "' is bound through the IDE's canonical lifecycle, so PatternInstance edits trigger regeneration on save.";
                        if (!string.IsNullOrEmpty(createdHostName))
                        {
                            patternResult["patternHost"] = createdHostName;
                            patternResult["generatedObjects"] = new JArray(createdHostName);
                            try
                            {
                                var idx = _objectService?.GetKbService()?.GetIndexCache();
                                if (idx != null)
                                {
                                    var hostObj = _objectService.FindObject(createdHostName);
                                    if (hostObj != null) idx.UpdateEntry(hostObj);
                                }
                            }
                            catch { }
                        }
                        // The association check inside the attach proves a host is bound, not
                        // that it uses the requested template. Confirm it from a fresh read and
                        // never report another template as success.
                        if (explicitTemplate != null)
                        {
                            string persistedXml = null;
                            try { persistedXml = Analysis.ReadPatternPartXmlFresh(obj, "PatternInstance", WorkWithPlusPatternId, out _, out _, out _); }
                            catch (Exception ex) { Logger.Warn("Explicit template: post-attach fresh read failed: " + ex.Message); }
                            if (!ExplicitTemplateMatches(persistedXml, explicitTemplate))
                            {
                                var notPersistedExtra = new JObject
                                {
                                    ["saved"] = true,
                                    ["requestedTemplate"] = explicitTemplate,
                                    ["patternHost"] = createdHostName
                                };
                                string persistedTemplate = ReadPersistedWwpTemplate(persistedXml);
                                if (persistedTemplate != null) notPersistedExtra["persistedTemplate"] = persistedTemplate;
                                return McpResponse.Err(
                                    code: "PatternTemplateNotPersisted",
                                    message: "The WorkWithPlus host '" + createdHostName + "' was attached, but a fresh read of the PatternInstance does not confirm template '" +
                                        explicitTemplate + "'.",
                                    hint: "The attach persisted. Read the PatternInstance before any retry; no other template is reported as success.",
                                    nextSteps: new JArray(McpResponse.NextStep(
                                        tool: "genexus_read",
                                        args: new JObject { ["name"] = targetName, ["part"] = "PatternInstance", ["limit"] = 0 },
                                        why: "Inspect the persisted instance and its template.")),
                                    target: targetName,
                                    extra: notPersistedExtra);
                            }
                        }
                    }
                    else
                    {
                        isNoOp = true;
                        if (attach.EnvironmentContext != null)
                            patternResult["wwpEnvironment"] = attach.EnvironmentContext;
                        // Name the stage that actually failed. The previous wording
                        // ("the fallback also failed: <last message>") made every
                        // failure mode read identically, which is why issue #330 could
                        // not be triaged from the tool's own output.
                        patternResult["failureStage"] = attach.FailureStage;
                        patternResult["noOpReason"] = BuildAttachNoOpReason(attach, parentTypeName);
                        patternResult["failureCode"] = attach.ErrorCode ?? "PatternNoOp";
                        foreach (var kv in attach.ToJson())
                        {
                            if (patternResult[kv.Key] == null) patternResult[kv.Key] = kv.Value;
                        }
                        patternResult["recommendation"] = BuildAttachRecommendation(attach, parentTypeName, targetName);

                        if (string.Equals(Environment.GetEnvironmentVariable("GX_MCP_SDK_PROBE"), "1", StringComparison.Ordinal))
                        {
                            try
                            {
                                var dump = DumpSdkSurface(patternDefinition, patternId);
                                string dumpPath = Path.Combine(Path.GetTempPath(), "gxmcp_pattern_probe.json");
                                File.WriteAllText(dumpPath, dump.ToString(Newtonsoft.Json.Formatting.Indented));
                                patternResult["sdkProbePath"] = dumpPath;

                                var fullProbe = SdkSurfaceProbe.Run(Environment.GetEnvironmentVariable("GX_MCP_SDK_PROBE_DIR"));
                                patternResult["sdkSurfaceProbe"] = new JObject
                                {
                                    ["rawJsonPath"] = fullProbe.RawJsonPath,
                                    ["indexMdPath"] = fullProbe.IndexMdPath,
                                    ["generatorsMdPath"] = fullProbe.GeneratorsMdPath,
                                    ["rawSizeBytes"] = fullProbe.RawSizeBytes,
                                    ["assembliesScanned"] = fullProbe.AssembliesScanned,
                                    ["typesScanned"] = fullProbe.TypesScanned,
                                    ["generatorCandidates"] = fullProbe.GeneratorCandidates,
                                    ["warnings"] = new JArray(fullProbe.Warnings)
                                };
                            }
                            catch (Exception ex) { patternResult["sdkProbeError"] = ex.Message; }
                        }
                    }
                }
            }

            Phase("tailEnvelope");
            Logger.Info("[ApplyPattern-PERF] target=" + targetName + " parent=" + parentTypeName + " phases=" + string.Join(",", phases));

            // v2.8.0: convert internal working status to canonical envelope.
            string internalStatus = response["_opStatus"]?.ToString() ?? "Success";
            string canonicalCode = projectionTimedOutCode ?? (isNoOp
                ? (patternResult["failureCode"]?.ToString() ?? "PatternNoOp")
                : "PatternApplied");

            string canonicalJson;
            if (isNoOp)
            {
                // NoOp: engine completed but nothing was generated — emit as error so the
                // agent gets actionable nextSteps rather than a misleading ok. The next
                // step follows the stage that failed: retrying the same call is only
                // correct for the stages where the caller supplied something wrong.
                canonicalJson = McpResponse.Err(
                    code: canonicalCode,
                    message: patternResult["noOpReason"]?.ToString() ?? "Pattern apply produced no generated objects.",
                    hint: patternResult["recommendation"]?.ToString() ?? "Apply WorkWithPlus to a Transaction or supply settings.template for a WebPanel.",
                    nextSteps: BuildAttachNextSteps(patternResult["failureStage"]?.ToString(), targetName, patternKey),
                    target: targetName,
                    extra: patternResult);
            }
            else if (string.Equals(internalStatus, "PartialFailure", StringComparison.OrdinalIgnoreCase))
            {
                // Partial: pattern applied but has validation issues.
                var partialWarnings = new JArray();
                if (patternValidationIssues != null)
                {
                    string validationHint = "Pattern apply persisted, but the parent's Events code references controls the fresh PatternInstance doesn't expose. The next IDE 'Ctrl+S' will fail with these errors. Edit the parent's Events to remove or rename the referenced controls (typically `GrpX.Visible = …` or similar) before saving in the IDE.";
                    partialWarnings.Add(new JObject { ["code"] = "PatternValidationIssues", ["message"] = validationHint, ["issues"] = patternValidationIssues });
                }
                canonicalJson = McpResponse.Partial(
                    target: targetName,
                    code: "PatternAppliedWithWarnings",
                    result: patternResult,
                    warnings: partialWarnings.Count > 0 ? partialWarnings : null);
            }
            else
            {
                canonicalJson = McpResponse.Ok(target: targetName, code: canonicalCode, result: patternResult);
            }

            // Re-attach SDK path tag and return.
            var canonicalObj = JObject.Parse(canonicalJson);
            GxMcp.Worker.Helpers.WriteResultMeta.TagSdkPath(canonicalObj, GxMcp.Worker.Helpers.WriteResultMeta.SdkPatternEngine);
            return canonicalObj.ToString(Newtonsoft.Json.Formatting.None);
        }

        /// <summary>
        /// Generic pattern-engine route for every pattern other than WorkWithPlus (issue #260):
        /// no WorkWithPlus projection, apply-on-save, template discovery, family lookup or
        /// package attach. First apply calls the engine and then requires the instance to
        /// exist; an existing instance is regenerated through the engine's reapply overload.
        /// </summary>
        private string ApplyGenericPatternToObject(KBObject obj, PatternManifest pattern, string patternKey, JObject settings, bool reapply, string objectNameForResponse, object knownInstance)
        {
            string targetName = objectNameForResponse ?? obj?.Name ?? "";
            string key = string.IsNullOrWhiteSpace(patternKey) ? pattern.Name : patternKey;

            object patternDefinition = _engine.GetPatternDefinition(pattern.Id);
            if (patternDefinition == null)
                return PatternUnavailable(key, pattern.Name + " pattern not loaded - check license / package install");

            object existingInstance = null;
            try
            {
                existingInstance = _engine.GetPatternInstance(obj, pattern.Id);
                if (!PatternAnalysisService.InstanceBelongsTo(existingInstance, obj)) existingInstance = null;
            }
            catch (Exception ex) { Logger.Debug("ApplyPattern: GetPatternInstance(" + pattern.Name + ") failed (best-effort): " + ex.Message); }
            if (existingInstance == null) existingInstance = knownInstance;

            var route = existingInstance != null ? PatternRoute.Reapply : PatternRoute.FirstApply;
            string routeReject = TryBuildRouteUnsupportedRejection(targetName, pattern, route);
            if (routeReject != null) return routeReject;

            PatternApplyResult result;
            bool wasFirstApply;
            try
            {
                if (existingInstance != null)
                {
                    result = TryReapplyWithFallback(existingInstance, obj, patternDefinition, settings, out wasFirstApply);
                }
                else
                {
                    result = _engine.ApplyPattern(obj, patternDefinition, settings);
                    wasFirstApply = true;
                }
            }
            catch (Exception ex)
            {
                Logger.Error("PatternEngine " + RouteName(route) + " failed for '" + targetName + "' (" + pattern.Name + "): " + ex);
                return McpResponse.Err(
                    code: "PatternEngineApplyFailed",
                    message: ex.Message,
                    hint: "Verify the " + pattern.Name + " package is installed and licensed and the KB is open.",
                    nextSteps: new JArray(McpResponse.NextStep(
                        tool: "genexus_apply_pattern",
                        args: new JObject { ["name"] = targetName, ["pattern"] = key, ["mode"] = "diagnose" },
                        why: "Run the read-only preflight to check the pattern and target state.")),
                    target: targetName,
                    extra: new JObject { ["patternKey"] = key, ["pattern"] = pattern.Name, ["route"] = RouteName(route) });
            }

            // Resolve through the typed registry path. A raw name lookup can find an
            // unrelated homonym and must not be treated as proof that apply succeeded.
            KBObject instanceObj = Analysis.ResolvePatternInstance(obj, pattern.Id, fresh: true, out _);
            string instanceName = instanceObj?.Name;

            if (instanceObj == null)
            {
                return McpResponse.Err(
                    code: "PatternNoOp",
                    message: "The pattern engine returned without creating a " + pattern.Name + " instance for '" + targetName + "'.",
                    hint: "This pattern may require the GeneXus IDE to create its first instance. Apply it in the IDE, then reapply or edit it here.",
                    nextSteps: new JArray(McpResponse.NextStep(
                        tool: "genexus_apply_pattern",
                        args: new JObject { ["name"] = targetName, ["pattern"] = key, ["mode"] = "diagnose" },
                        why: "Run the read-only preflight to check the target and pattern state.")),
                    target: targetName,
                    extra: new JObject { ["patternKey"] = key, ["pattern"] = pattern.Name, ["patternId"] = pattern.Id.ToString(), ["route"] = RouteName(route) });
            }

            var generated = new List<string>();
            if (!string.IsNullOrEmpty(instanceName)) generated.Add(instanceName);
            foreach (var name in result?.GeneratedObjects ?? Enumerable.Empty<string>())
            {
                if (!string.IsNullOrEmpty(name) && !generated.Contains(name, StringComparer.OrdinalIgnoreCase)) generated.Add(name);
            }

            // Keep the search index in sync with the instance and any reported objects.
            if (_objectService != null && generated.Count > 0)
            {
                try
                {
                    var idx = _objectService.GetKbService()?.GetIndexCache();
                    if (idx != null)
                    {
                        foreach (var name in generated)
                        {
                            try
                            {
                                var o = _objectService.FindObject(name);
                                if (o != null) idx.UpdateEntry(o);
                            }
                            catch { /* per-name best-effort */ }
                        }
                    }
                }
                catch (Exception ex) { Logger.Debug("ApplyPattern: index UpdateEntry sweep skipped: " + ex.Message); }
            }

            var patternResult = new JObject
            {
                ["parentType"] = obj?.TypeDescriptor?.Name ?? "",
                ["bindingMode"] = "pattern-engine",
                ["patternKey"] = key,
                ["patternName"] = pattern.Name,
                ["patternId"] = pattern.Id.ToString(),
                ["wasFirstApply"] = wasFirstApply,
                ["generatedObjects"] = new JArray(generated),
                ["errors"] = new JArray(result?.Errors ?? Enumerable.Empty<string>())
            };
            if (!string.IsNullOrEmpty(instanceName)) patternResult["patternHost"] = instanceName;

            var canonicalObj = JObject.Parse(McpResponse.Ok(target: targetName, code: "PatternApplied", result: patternResult));
            GxMcp.Worker.Helpers.WriteResultMeta.TagSdkPath(canonicalObj, GxMcp.Worker.Helpers.WriteResultMeta.SdkPatternEngine);
            return canonicalObj.ToString(Newtonsoft.Json.Formatting.None);
        }

        // F23: list all `WorkWithPlus for Web Template` KBObjects in the current KB —
        // used to surface options to the agent on apply_pattern responses.
        // Walking model.Objects.GetAll() is O(KB-size) and was clocking 10s+ on the
        // 50k-object KB. Templates rarely change at runtime, so memoize per KB for
        // v2.6.4 (#10): pure type-gate logic. Returns the rejection envelope
        // (serialized JSON) when the parent type / template combination is
        // ineligible for WorkWithPlus, or null if the apply may proceed.
        // - WorkWithPlus key only (other keys pass through unchanged)
        // - Transaction: always eligible (no template required)
        // - WebPanel/WebComponent/SDPanel: eligible; if callerTemplate provided AND
        //   availableTemplates is non-empty AND template not in list → reject
        // - Anything else → reject upfront (Procedure/SDT/Domain/etc.)
        // Extracted so the rejection contract is unit-testable without a live
        // KB or KBObject.
        internal static string TryBuildTypeGateRejection(
            string objName,
            string patternKey,
            string parentType,
            string callerTemplate,
            List<string> availableTemplates)
        {
            bool isWwp = string.Equals(patternKey, "WorkWithPlus", StringComparison.OrdinalIgnoreCase)
                      || string.Equals(patternKey, "WWP", StringComparison.OrdinalIgnoreCase);
            if (!isWwp) return null;

            if (parentType == null) parentType = "";
            bool isTransaction = string.Equals(parentType, "Transaction", StringComparison.OrdinalIgnoreCase);
            bool isWebPanelKind = IsWwpDirectAttachParentType(parentType);

            if (!isTransaction && !isWebPanelKind)
            {
                var rejExtra = new JObject
                {
                    ["patternKey"] = patternKey,
                    ["parentType"] = parentType,
                    ["validParentTypes"] = new JArray("Transaction", "WebPanel", "WebComponent", "SDPanel")
                };
                var rej = JObject.Parse(McpResponse.Err(
                    code: "PatternParentTypeMismatch",
                    message: $"WorkWithPlus cannot be applied to a {parentType}.",
                    hint: "Apply WorkWithPlus only to a Transaction (generates WW/View/Export family) or to a WebPanel/WebComponent/SDPanel (direct-attach with a Template; pass settings.template or let the MCP auto-discover one).",
                    nextSteps: new JArray(McpResponse.NextStep(
                        tool: "genexus_apply_pattern",
                        args: new JObject { ["name"] = objName, ["pattern"] = patternKey },
                        why: "Call on a Transaction, WebPanel, WebComponent or SDPanel instead.")),
                    target: objName,
                    extra: rejExtra));
                return rej.ToString(Newtonsoft.Json.Formatting.None);
            }

            if (isWebPanelKind
                && !string.IsNullOrEmpty(callerTemplate)
                && availableTemplates != null
                && availableTemplates.Count > 0
                && !availableTemplates.Any(t => string.Equals(t, callerTemplate, StringComparison.OrdinalIgnoreCase)))
            {
                var badExtra = new JObject
                {
                    ["patternKey"] = patternKey,
                    ["parentType"] = parentType,
                    ["availableTemplates"] = new JArray(availableTemplates)
                };
                var bad = JObject.Parse(McpResponse.Err(
                    code: "PatternTemplateNotFound",
                    message: $"Template '{callerTemplate}' is not a registered `WorkWithPlus for Web Template` in this KB.",
                    hint: "Pass settings.template equal to one of availableTemplates, or omit it to auto-discover.",
                    nextSteps: new JArray(McpResponse.NextStep(
                        tool: "genexus_apply_pattern",
                        args: new JObject { ["name"] = objName, ["pattern"] = patternKey, ["settings"] = new JObject { ["template"] = availableTemplates[0] } },
                        why: $"Retries with the first available template ({availableTemplates[0]}).")),
                    target: objName,
                    extra: badExtra));
                return bad.ToString(Newtonsoft.Json.Formatting.None);
            }

            return null;
        }

        internal static bool IsWwpDirectAttachParentType(string parentType)
        {
            return string.Equals(parentType, "WebPanel", StringComparison.OrdinalIgnoreCase)
                || string.Equals(parentType, "WebComponent", StringComparison.OrdinalIgnoreCase)
                || string.Equals(parentType, "SDPanel", StringComparison.OrdinalIgnoreCase);
        }

        internal static string GetWwpBindingMode(string parentType)
        {
            if (string.Equals(parentType, "Transaction", StringComparison.OrdinalIgnoreCase)) return "transaction-family";
            if (string.Equals(parentType, "WebPanel", StringComparison.OrdinalIgnoreCase)) return "webpanel-direct-attach";
            if (string.Equals(parentType, "WebComponent", StringComparison.OrdinalIgnoreCase)) return "webcomponent-direct-attach";
            if (string.Equals(parentType, "SDPanel", StringComparison.OrdinalIgnoreCase)) return "sdpanel-direct-attach";
            return "unknown";
        }

        // 60s — fresh enough to pick up new templates, cheap on the upfront-guard
        // path where every apply on a WebPanel hits this.
        private static readonly Dictionary<string, (DateTime expiresAt, List<string> names)> _templateCache
            = new Dictionary<string, (DateTime, List<string>)>(StringComparer.OrdinalIgnoreCase);
        private static readonly object _templateCacheLock = new object();
        private const int TemplateCacheTtlSeconds = 60;

        private List<string> ListWwpWebTemplates()
        {
            if (_objectService == null) return new List<string>();
            string kbKey = null;
            try { kbKey = _objectService.GetKbService()?.GetKB()?.Location ?? "(no-kb)"; } catch { kbKey = "(error)"; }
            lock (_templateCacheLock)
            {
                if (_templateCache.TryGetValue(kbKey, out var hit) && hit.expiresAt > DateTime.UtcNow)
                    return hit.names;
            }
            var names = new List<string>();
            // FAST PATH: walk the search index (in-memory, ~ms) instead of
            // model.Objects.GetAll() (~10s on 50k-object KBs). The index entries
            // carry Type, which is what we filter on. Fall back to SDK enumeration
            // only when the index isn't ready.
            try
            {
                var index = _objectService.GetIndex();
                if (index != null && index.Objects != null && index.Objects.Count > 0)
                {
                    foreach (var entry in index.FindByType("WorkWithPlus for Web Template"))
                    {
                        if (!string.IsNullOrEmpty(entry?.Name))
                            names.Add(entry.Name);
                    }
                }
                else
                {
                    var kb = _objectService.GetKbService()?.GetKB();
                    if (kb != null)
                    {
                        foreach (KBObject o in kb.DesignModel.Objects.GetAll())
                        {
                            if (o == null) continue;
                            if (string.Equals(o.TypeDescriptor?.Name, "WorkWithPlus for Web Template", StringComparison.OrdinalIgnoreCase))
                                names.Add(o.Name);
                        }
                    }
                }
            }
            catch { /* best-effort */ }
            names.Sort(StringComparer.Ordinal);
            lock (_templateCacheLock)
            {
                _templateCache[kbKey] = (DateTime.UtcNow.AddSeconds(TemplateCacheTtlSeconds), names);
            }
            return names;
        }

        /// <summary>The template WorkWithPlus documents for a blank direct attach (WWP/Resources/BaseXmlObjects.xml).</summary>
        internal const string DefaultWwpWebTemplate = "Empty";

        // Test seam: the registered `WorkWithPlus for Web Template` names. Unit tests run
        // without a KB; the live path reads them through ListWwpWebTemplates.
        internal Func<IReadOnlyList<string>> WwpTemplateLister { get; set; }

        private IReadOnlyList<string> RegisteredWwpWebTemplates()
        {
            if (WwpTemplateLister != null) return WwpTemplateLister() ?? new List<string>();
            return ListWwpWebTemplates();
        }

        /// <summary>
        /// Picks the `WorkWithPlus for Web Template` a direct attach uses. An explicit name
        /// must match a registered template (case-insensitive) and comes back with its
        /// registered spelling; it is never replaced by another template. The candidates are
        /// templates only: the standard ones (Selection, Transaction, View, Prompt,
        /// Association, ...) sit in a folder of the same name, and an untyped name lookup
        /// resolved that folder and fell through to an unrelated template, which
        /// ValidateAndSave then rejected. Without an explicit name the documented default
        /// <c>Empty</c> wins, then the first non-Popover template.
        /// </summary>
        internal static string SelectWwpWebTemplate(IEnumerable<string> registered, string preferred, out string source)
        {
            var names = (registered ?? Enumerable.Empty<string>()).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
            if (!string.IsNullOrWhiteSpace(preferred))
            {
                string wanted = preferred.Trim();
                string exact = names.FirstOrDefault(n => string.Equals(n, wanted, StringComparison.OrdinalIgnoreCase));
                source = exact != null ? "caller" : "notRegistered";
                return exact;
            }
            string empty = names.FirstOrDefault(n => string.Equals(n, DefaultWwpWebTemplate, StringComparison.OrdinalIgnoreCase));
            if (empty != null) { source = "defaultEmpty"; return empty; }
            string first = names.FirstOrDefault(n => !n.StartsWith("Popover", StringComparison.OrdinalIgnoreCase)) ?? names.FirstOrDefault();
            if (first != null) { source = "firstRegistered"; return first; }
            source = "unlisted";
            return DefaultWwpWebTemplate;
        }

        private string ResolveAvailableWwpTemplate(string preferred) => ResolveAvailableWwpTemplate(preferred, out _);

        // Resolves the template seeded into `<WPRoot Template="...">`. The validator rejects
        // the save when the name does not resolve to a real Template object in this KB.
        // Returns null for an explicit name that is not registered: the caller must refuse,
        // not attach some other template.
        internal string ResolveAvailableWwpTemplate(string preferred, out string source)
        {
            IReadOnlyList<string> registered;
            try { registered = RegisteredWwpWebTemplates(); }
            catch (Exception ex)
            {
                Logger.Debug("ResolveAvailableWwpTemplate: template listing failed: " + ex.Message);
                registered = new List<string>();
            }
            if (!string.IsNullOrWhiteSpace(preferred)
                || registered.Any(n => string.Equals(n, DefaultWwpWebTemplate, StringComparison.OrdinalIgnoreCase))
                || _objectService == null)
                return SelectWwpWebTemplate(registered, preferred, out source);

            // No explicit name and no `Empty` template: keep the established discovery order
            // (first non-Popover template in SDK enumeration order).
            try
            {
                var kb = _objectService.GetKbService()?.GetKB();
                if (kb != null)
                {
                    string firstNonPopover = null;
                    string anyTemplate = null;
                    foreach (KBObject o in kb.DesignModel.Objects.GetAll())
                    {
                        if (o == null) continue;
                        if (!string.Equals(o.TypeDescriptor?.Name, "WorkWithPlus for Web Template", StringComparison.OrdinalIgnoreCase)) continue;
                        if (anyTemplate == null) anyTemplate = o.Name;
                        if (!o.Name.StartsWith("Popover", StringComparison.OrdinalIgnoreCase)) { firstNonPopover = o.Name; break; }
                    }
                    string discovered = firstNonPopover ?? anyTemplate;
                    if (discovered != null) { source = "firstRegistered"; return discovered; }
                }
            }
            catch (Exception ex)
            {
                Logger.Debug("ResolveAvailableWwpTemplate failed: " + ex.Message);
            }
            return SelectWwpWebTemplate(registered, null, out source);
        }

        /// <summary>
        /// Why an explicit settings.template cannot be honoured, or null when it can. A web
        /// template belongs to the direct attach of a WebPanel, WebComponent or SDPanel; a
        /// Transaction generates the WorkWithPlus family and does not take one.
        /// </summary>
        internal static string ExplicitTemplateUnsupportedReason(string parentType, string requested)
        {
            if (string.IsNullOrWhiteSpace(requested))
                return "settings.template must be a non-empty template name.";
            if (!IsWwpDirectAttachParentType(parentType))
                return "settings.template applies only to a WebPanel, WebComponent or SDPanel direct attach; " +
                    (string.IsNullOrEmpty(parentType) ? "a target of unknown type" : "a " + parentType) + " does not take a web template.";
            return null;
        }

        /// <summary>
        /// Validates settings.template before any SDK work. Returns the refusal envelope, or
        /// null with <paramref name="explicitTemplate"/> set to the registered spelling (null
        /// when no template was requested).
        /// </summary>
        private string TryResolveExplicitWwpTemplate(KBObject obj, JObject settings, string objectNameForResponse, out string explicitTemplate)
        {
            explicitTemplate = null;
            JToken token = settings?["template"];
            if (token == null || token.Type == JTokenType.Null) return null;
            string target = objectNameForResponse ?? obj?.Name ?? "";
            string parentType = obj?.TypeDescriptor?.Name;
            string requested = token.Type == JTokenType.String ? (string)token : null;

            string unsupported = ExplicitTemplateUnsupportedReason(parentType, requested);
            if (unsupported != null)
            {
                var unsupportedExtra = new JObject { ["parentType"] = parentType ?? "" };
                if (requested != null) unsupportedExtra["requestedTemplate"] = requested;
                return McpResponse.Err(
                    code: "PatternTemplateUnsupported",
                    message: unsupported + " No changes were applied.",
                    hint: "Omit settings.template for a Transaction (it generates the WorkWithPlus family), or pass a registered template name on a WebPanel, WebComponent or SDPanel.",
                    target: target,
                    extra: unsupportedExtra);
            }

            explicitTemplate = ResolveAvailableWwpTemplate(requested, out _);
            if (!string.IsNullOrEmpty(explicitTemplate)) return null;

            var notFoundExtra = new JObject { ["parentType"] = parentType ?? "", ["requestedTemplate"] = requested };
            IReadOnlyList<string> available = null;
            try { available = RegisteredWwpWebTemplates(); } catch { /* the refusal stands without the list */ }
            if (available != null && available.Count > 0) notFoundExtra["availableTemplates"] = new JArray(available);
            return McpResponse.Err(
                code: "PatternTemplateNotFound",
                message: "Template '" + requested + "' is not a registered `WorkWithPlus for Web Template` in this KB. No changes were applied.",
                hint: "Pass settings.template equal to one of availableTemplates. The requested template is never replaced by another one.",
                target: target,
                extra: notFoundExtra);
        }

        /// <summary>The Template of the single WPRoot in a PatternInstance XML, or null.</summary>
        internal static string ReadPersistedWwpTemplate(string persistedXml)
        {
            if (string.IsNullOrWhiteSpace(persistedXml)) return null;
            try
            {
                var roots = XDocument.Parse(persistedXml).Descendants()
                    .Where(e => string.Equals(e.Name.LocalName, "WPRoot", StringComparison.OrdinalIgnoreCase)).ToList();
                return roots.Count == 1 ? (string)roots[0].Attribute("Template") : null;
            }
            catch (System.Xml.XmlException) { return null; }
        }

        internal static bool ExplicitTemplateMatches(string persistedXml, string requestedTemplate)
        {
            if (string.IsNullOrWhiteSpace(requestedTemplate)) return false;
            string persisted = ReadPersistedWwpTemplate(persistedXml);
            return persisted != null && string.Equals(persisted, requestedTemplate, StringComparison.OrdinalIgnoreCase);
        }

        internal static WwpEnvironmentContext InspectWwpEnvironment(
            string installationPath = null,
            Func<string, string> writeAccessProbe = null)
        {
            var context = new WwpEnvironmentContext();
            try
            {
                context.InstallationPath = string.IsNullOrWhiteSpace(installationPath)
                    ? ResolveGeneXusInstallationPath()
                    : Path.GetFullPath(installationPath);
                if (string.IsNullOrWhiteSpace(context.InstallationPath))
                {
                    context.AccessError = "GeneXus installation path could not be resolved from GX_PROGRAM_DIR, GX_PATH, or the loaded Artech SDK assembly.";
                    return context;
                }

                context.GeneXusConfigPath = Path.Combine(context.InstallationPath, "GeneXus.exe.config");
                string userAppDataPath = null;
                if (File.Exists(context.GeneXusConfigPath))
                {
                    var config = XDocument.Load(context.GeneXusConfigPath, LoadOptions.None);
                    var setting = config.Descendants("add")
                        .FirstOrDefault(x => string.Equals((string)x.Attribute("key"), "UserAppDataPath", StringComparison.OrdinalIgnoreCase));
                    userAppDataPath = (string)setting?.Attribute("value");
                    if (!string.IsNullOrWhiteSpace(userAppDataPath))
                    {
                        userAppDataPath = Environment.ExpandEnvironmentVariables(userAppDataPath.Trim());
                        if (!Path.IsPathRooted(userAppDataPath))
                            userAppDataPath = Path.GetFullPath(Path.Combine(context.InstallationPath, userAppDataPath));
                        context.ConfigSource = context.GeneXusConfigPath + " appSettings/UserAppDataPath";
                    }
                }

                if (string.IsNullOrWhiteSpace(userAppDataPath))
                {
                    userAppDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                    context.ConfigSource = "Windows ApplicationData fallback";
                }

                context.UserAppDataPath = userAppDataPath;
                string majorFolder = !string.IsNullOrWhiteSpace(Compatibility.DynamicSdkBridge.CurrentMajor)
                    ? Compatibility.DynamicSdkBridge.CurrentMajor
                    : "18";
                string envConfigPath = Path.Combine(userAppDataPath, "GeneXus", "GeneXus", majorFolder, "Environment.config");
                if (!File.Exists(envConfigPath))
                {
                    // Fallback to primary major "18" or search subdirectories in base GeneXus config
                    string primaryFallback = Path.Combine(userAppDataPath, "GeneXus", "GeneXus", "18", "Environment.config");
                    if (File.Exists(primaryFallback))
                    {
                        envConfigPath = primaryFallback;
                    }
                    else
                    {
                        string baseDir = Path.Combine(userAppDataPath, "GeneXus", "GeneXus");
                        if (Directory.Exists(baseDir))
                        {
                            try
                            {
                                var candidates = Directory.GetFiles(baseDir, "Environment.config", SearchOption.AllDirectories);
                                if (candidates.Length > 0)
                                {
                                    envConfigPath = candidates[0];
                                }
                            }
                            catch { }
                        }
                    }
                }
                context.EnvironmentConfigPath = envConfigPath;
                context.EnvironmentConfigExists = File.Exists(context.EnvironmentConfigPath);
                if (!context.EnvironmentConfigExists)
                {
                    context.EnvironmentConfigWritable = null;
                    return context;
                }

                string accessError = (writeAccessProbe ?? ProbeWriteAccess)(context.EnvironmentConfigPath);
                context.AccessError = accessError;
                context.EnvironmentConfigWritable = string.IsNullOrEmpty(accessError);
                return context;
            }
            catch (Exception ex)
            {
                context.AccessError = ex.ToString();
                context.EnvironmentConfigWritable = false;
                return context;
            }
        }

        private static string ResolveGeneXusInstallationPath() => GeneXusInstallPath.Resolve();


        private static string ProbeWriteAccess(string path)
        {
            try
            {
                using (new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) { }
                return null;
            }
            catch (Exception ex)
            {
                return ex.ToString();
            }
        }

        internal static MethodInfo ResolveWwpCreateCall(
            Type interfaceType,
            string parentType,
            object model,
            object parent,
            string template,
            out object[] arguments,
            out int byRefArgumentIndex,
            out string candidates,
            out string resolutionError)
        {
            bool webView = string.Equals(parentType, "WebPanel", StringComparison.OrdinalIgnoreCase)
                || string.Equals(parentType, "WebComponent", StringComparison.OrdinalIgnoreCase);
            if (webView)
            {
                var settingsTypes = interfaceType
                    .GetMethods(Compatibility.SdkMemberProbe.Static)
                    .Where(m => string.Equals(m.Name, "CreatePatternInstanceWithTemplate", StringComparison.Ordinal))
                    .Select(m => m.GetParameters())
                    .Where(p => p.Length == 5 && p[2].ParameterType.IsEnum && p[3].ParameterType == typeof(string) && p[4].ParameterType.IsByRef)
                    .Select(p => p[2].ParameterType)
                    .Distinct()
                    .ToList();
                if (settingsTypes.Count != 1 || !Enum.GetNames(settingsTypes[0]).Any(n => string.Equals(n, "Web", StringComparison.OrdinalIgnoreCase)))
                {
                    arguments = null;
                    byRefArgumentIndex = 4;
                    candidates = string.Join("; ", interfaceType
                        .GetMethods(Compatibility.SdkMemberProbe.Static)
                        .Where(m => string.Equals(m.Name, "CreatePatternInstanceWithTemplate", StringComparison.Ordinal))
                        .Select(FormatMethodSignature));
                    resolutionError = "The WWP package does not expose one unambiguous five-parameter SettingsView.Web overload.";
                    return null;
                }

                object web = Enum.Parse(settingsTypes[0], "Web", true);
                arguments = new[] { model, parent, web, template ?? string.Empty, null };
                byRefArgumentIndex = 4;
            }
            else
            {
                // The four-parameter overload delegates to SettingsView.NativeMobile
                // in WWP 16.1, so keep it only for SDPanel/native-mobile targets.
                arguments = new[] { model, parent, template ?? string.Empty, null };
                byRefArgumentIndex = 3;
            }

            return ResolveCompatibleStaticOverload(
                interfaceType,
                "CreatePatternInstanceWithTemplate",
                arguments,
                byRefArgumentIndex,
                typeof(bool),
                out candidates,
                out resolutionError);
        }

        // WWP has shipped multiple public overloads with this name. In U16, for
        // example, both of these are present:
        //   (KBModel, KBObject, string, out PatternInstance)
        //   (KBModel, KBObject, SettingsView, string, out PatternInstance)
        // Type.GetMethod(name) therefore throws AmbiguousMatchException before the
        // package code is ever reached. Resolve against the complete call shape and
        // prefer exact runtime-type matches; never pick an arbitrary tied overload.
        internal static MethodInfo ResolveCompatibleStaticOverload(
            Type declaringType,
            string methodName,
            object[] arguments,
            int byRefArgumentIndex,
            Type expectedReturnType,
            out string candidateSignatures,
            out string resolutionError)
        {
            candidateSignatures = "<none>";
            resolutionError = null;
            if (declaringType == null)
            {
                resolutionError = "Declaring type is null.";
                return null;
            }

            arguments = arguments ?? Array.Empty<object>();
            var candidates = declaringType
                .GetMethods(Compatibility.SdkMemberProbe.Static)
                .Where(m => string.Equals(m.Name, methodName, StringComparison.Ordinal))
                .OrderBy(m => m.MetadataToken)
                .ToList();
            candidateSignatures = candidates.Count == 0
                ? "<none>"
                : string.Join("; ", candidates.Select(FormatMethodSignature));

            var compatible = new List<Tuple<MethodInfo, int>>();
            foreach (var method in candidates)
            {
                if (expectedReturnType != null && method.ReturnType != expectedReturnType) continue;
                var parameters = method.GetParameters();
                if (parameters.Length != arguments.Length) continue;

                int score = 0;
                bool matches = true;
                for (int i = 0; i < parameters.Length; i++)
                {
                    bool shouldBeByRef = i == byRefArgumentIndex;
                    if (parameters[i].ParameterType.IsByRef != shouldBeByRef)
                    {
                        matches = false;
                        break;
                    }

                    var parameterType = parameters[i].ParameterType.IsByRef
                        ? parameters[i].ParameterType.GetElementType()
                        : parameters[i].ParameterType;
                    var argument = arguments[i];
                    if (argument == null)
                    {
                        if (!parameters[i].IsOut && parameterType.IsValueType && Nullable.GetUnderlyingType(parameterType) == null)
                        {
                            matches = false;
                            break;
                        }
                        continue;
                    }

                    var argumentType = argument.GetType();
                    if (parameterType == argumentType) score += 100;
                    else if (parameterType.IsAssignableFrom(argumentType)) score += parameterType == typeof(object) ? 1 : 10;
                    else
                    {
                        matches = false;
                        break;
                    }
                }

                if (matches) compatible.Add(Tuple.Create(method, score));
            }

            if (compatible.Count == 0)
            {
                resolutionError = "No compatible overload for the supplied argument types.";
                return null;
            }

            int bestScore = compatible.Max(x => x.Item2);
            var best = compatible.Where(x => x.Item2 == bestScore).Select(x => x.Item1).ToList();
            if (best.Count != 1)
            {
                resolutionError = "Multiple equally compatible overloads: " + string.Join("; ", best.Select(FormatMethodSignature));
                return null;
            }
            return best[0];
        }

        internal static string FormatMethodSignature(MethodInfo method)
        {
            if (method == null) return "<null>";
            return method.ReturnType.FullName + " " + method.DeclaringType.FullName + "." + method.Name + "(" +
                string.Join(", ", method.GetParameters().Select(p =>
                {
                    var type = p.ParameterType.IsByRef ? p.ParameterType.GetElementType() : p.ParameterType;
                    return (p.IsOut ? "out " : p.ParameterType.IsByRef ? "ref " : "") + type.FullName;
                })) + ")";
        }

        // DIRECT-ATTACH APPLY PATH for WebPanel/WebComponent/SDPanel targets. Two routes,
        // in this order:
        //
        //   1. OFFICIAL (TryOfficialAttach) — the IDE's canonical Right-click → Apply
        //      Pattern route: PatternInstancePackageInterface.CreatePatternInstanceWithTemplate
        //      + SetPatternApplyOnSave + ValidateAndSave, then the projection step
        //      (IPatternBuildProcess.UpdateParentObject) that writes the pattern onto the
        //      target's WebForm. The engine's own ApplyPattern is a silent no-op on these
        //      targets, so this is the only route that can work.
        //   2. LAST RESORT (WWP_ApplyTemplate MSBuild task) — kept only for a future SDK
        //      that relaxes its headless ctor requirement. See the method body.
        //
        // The template is the caller's hint via settings.template, else auto-discovered
        // among the registered `WorkWithPlus for Web Template` objects in this KB. Fails
        // closed on the WwpAttachPreflight and names the exact stage that failed, so the
        // caller can answer "why" instead of relaying the last line of a multi-step
        // pipeline (#330).
        internal WwpAttachResult TryPackageInterfaceAttach(KBObject parent, string preferredTemplate)
        {
            var result = TryOfficialAttach(parent, preferredTemplate);
            if (result.Attached) return result;

            // Last resort: the WWP_ApplyTemplate MSBuild task. This route is documented
            // as a dead end headlessly (docs/sdk-probe/wwp-projection-discovery.md, dead
            // end #5 — the task ctor needs an MSBuild engine host) and is kept only in
            // case a future SDK relaxes that. It used to run FIRST, ahead of the verified
            // route, and could leave a WorkWithPlus<X> host that the official create then
            // collided with — which is how a WebPanel apply ended in a false
            // "ValidateAndSave returned false" (issue #330). After the official route it
            // can no longer poison anything: the orphan host it may create is removed on
            // failure by the same fail-closed cleanup.
            if (string.Equals(parent?.TypeDescriptor?.Name, "WebPanel", StringComparison.Ordinal)
                && !string.IsNullOrEmpty(result.Template)
                && !string.Equals(result.FailureStage, "hostConflict", StringComparison.Ordinal))
            {
                var surface = WwpPackageSurface.Resolve();
                string taskHost = null;
                string taskError = null;
                List<string> packageErrors = null;
                bool taskSucceeded = surface.Assembly != null
                    && TryRunWwpApplyTemplateTask(surface.Assembly, parent, result.Template, out taskHost, out taskError, out packageErrors);
                // The package's own validation messages name the real cause; keep them
                // even when the task failed, so the response does not fall back to
                // generic template advice.
                if (packageErrors != null && packageErrors.Count > 0) result.PackageErrors = packageErrors;
                if (taskSucceeded)
                {
                    var taskHostObj = _objectService?.FindObject(taskHost);
                    string projectionFailure = null;
                    if (taskHostObj != null
                        && TryInvokeBuildProcessUpdateParent(parent, taskHostObj, out projectionFailure))
                    {
                        Logger.Info("WWP_ApplyTemplate last-resort attach succeeded: host='" + taskHost + "' parent='" + parent.Name + "'");
                        result.Attached = true;
                        result.FailureStage = null;
                        result.ErrorCode = null;
                        result.ErrorMessage = null;
                        result.PreflightFindings = result.PreflightFindings ?? new JArray();
                        result.DirectAttachRoute = "WWP_ApplyTemplate";
                        return result;
                    }
                    taskError = "the task reported success but the projection onto '" + parent.Name + "' did not complete"
                        + (string.IsNullOrEmpty(projectionFailure) ? "." : ": " + projectionFailure);
                }
                Logger.Info("WWP_ApplyTemplate last-resort failed: " + (taskError ?? "unknown"));
                result.FailureStage = result.FailureStage ?? "applyTemplateTask";
                if (taskError != null) result.LastResortDetail = taskError;
                // The task creates its host by convention before knowing whether the
                // projection will run, so clean up whatever it left behind.
                RemoveOrphanHost(parent, result);
            }

            return result;
        }

        /// <summary>
        /// The official IDE route — <c>PatternInstancePackageInterface</c> plus the
        /// <c>IPatternBuildProcess</c> projection. Every failure path here removes the
        /// host it created, so a caller can retry without inheriting an orphan.
        /// </summary>
        private WwpAttachResult TryOfficialAttach(KBObject parent, string preferredTemplate)
        {
            var result = new WwpAttachResult();
            if (parent == null) { result.ErrorMessage = "parent KBObject is null"; return result; }
            if (_objectService == null) { result.ErrorMessage = "ObjectService unavailable"; return result; }

            // Per-stage timing for the same reason the caller times its phases: an
            // attach that stalls for minutes looks identical to one that failed fast
            // unless the log says which stage consumed it.
            var stageTimer = System.Diagnostics.Stopwatch.StartNew();
            void Stage(string name)
            {
                Logger.Info("[APPLY-PATTERN-ATTACH] parent='" + parent.Name + "' stage=" + name + " elapsed=" + stageTimer.ElapsedMilliseconds + "ms");
                stageTimer.Restart();
            }

            try
            {
                var kb = _objectService.GetKbService()?.GetKB();
                if (kb == null) { result.ErrorMessage = "No KB open"; return result; }
                object model = kb.DesignModel;
                string parentType = parent.TypeDescriptor?.Name ?? "";

                var environment = InspectWwpEnvironment();
                result.EnvironmentContext = environment.ToJson();
                if (environment.EnvironmentConfigWritable == false)
                {
                    result.FailureStage = "environment";
                    result.ErrorCode = "PatternEnvironmentAccessDenied";
                    string identity;
                    try { identity = WindowsIdentity.GetCurrent()?.Name ?? Environment.UserName; }
                    catch { identity = Environment.UserName; }
                    result.ErrorMessage = environment.EnvironmentConfigExists
                        ? "WorkWithPlus resolves Environment.config from " + environment.ConfigSource +
                          ". The effective path '" + environment.EnvironmentConfigPath + "' exists but identity '" + identity +
                          "' cannot open it for write. The IDE can appear to work when it runs elevated under a different token. " +
                          "Grant Modify to the MCP identity (or a group it belongs to) on that file and keep the configured UserAppDataPath aligned with the IDE. Access probe: " +
                          environment.AccessError
                        : "The WorkWithPlus environment preflight could not resolve or inspect the effective Environment.config for identity '" +
                          identity + "'. Confirm the active GeneXus installation and its UserAppDataPath before applying. Preflight error: " +
                        environment.AccessError;
                    Logger.Warn(result.ErrorCode + ": " + result.ErrorMessage);
                    return result;
                }

                var surface = WwpPackageSurface.Resolve();
                if (surface.Assembly == null)
                {
                    result.FailureStage = "wwpAssembly";
                    result.ErrorCode = "PatternAttachPreflightFailed";
                    result.ErrorMessage = "The WorkWithPlus pattern package could not be loaded: " + surface.LoadError;
                    return result;
                }

                // The official route is authoritative and runs first. WWP_ApplyTemplate
                // is a last resort below, deliberately: it is documented as a dead end
                // headlessly (docs/sdk-probe/wwp-projection-discovery.md, dead end #5 —
                // the task ctor needs an MSBuild engine host), and when it does execute
                // it can leave a WorkWithPlus<X> host that the official create then
                // collides with. Running a known-fragile mutating route ahead of the
                // verified one cost WebPanel applies their PatternInstance binding (#330).
                result.Template = ResolveAvailableWwpTemplate(preferredTemplate, out var templateSource);
                result.TemplateSource = templateSource;
                if (string.IsNullOrEmpty(result.Template))
                {
                    result.FailureStage = "template";
                    result.ErrorCode = "PatternTemplateNotFound";
                    result.ErrorMessage = "Template '" + preferredTemplate + "' is not a registered `WorkWithPlus for Web Template` in this KB; no other template is substituted.";
                    return result;
                }
                var preflight = WwpAttachPreflight.Run(
                    surface.PackageInterfaceType,
                    surface.WorkWithPatternType,
                    parentType,
                    model,
                    parent,
                    result.Template);
                result.PreflightFindings = preflight.Findings;
                if (!preflight.CanAttach)
                {
                    result.FailureStage = preflight.BlockedStage;
                    result.ErrorCode = WwpAttachPreflight.BlockedCode;
                    result.ErrorMessage = preflight.Findings
                        .Select(f => f["detail"]?.ToString())
                        .FirstOrDefault(d => !string.IsNullOrEmpty(d))
                        ?? "The WorkWithPlus direct-attach preflight did not pass.";
                    return result;
                }

                var createMethod = preflight.CreateMethod;
                var setApplyMethod = preflight.SetApplyOnSaveMethod;
                var validateSaveMethod = preflight.ValidateAndSaveMethod;
                string usedTemplate = result.Template;
                string createCandidates = preflight.CreateCandidates ?? "<none>";

                // A host that already exists and that we did not create cannot be
                // adopted: the official create would fail on the name collision, and
                // ValidateAndSave would then run against somebody else's object. Report
                // the orphan instead of silently binding to it — the previous behaviour
                // adopted it, which is how a failed attach turned into a WebPanel that
                // renders but whose events never fire.
                var preExistingHost = _objectService.FindObject("WorkWithPlus" + parent.Name);
                if (preExistingHost != null)
                {
                    result.FailureStage = "hostConflict";
                    result.ErrorCode = "PatternHostAlreadyExists";
                    result.HostName = preExistingHost.Name;
                    result.ErrorMessage = "A '" + preExistingHost.Name + "' object already exists in this KB, so CreatePatternInstanceWithTemplate cannot create the " +
                        "pattern host and this call will not adopt an object it did not create. It is either an orphan left by a failed attach or a host whose " +
                        "PatternInstance is not bound to '" + parent.Name + "'.";
                    return result;
                }

                object[] args = (object[])preflight.CreateArguments.Clone();
                int createOutIndex = preflight.CreateByRefArgumentIndex;
                object createResult;
                bool createThrew = false;
                string createThrowMsg = null;
                try
                {
                    createResult = createMethod.Invoke(null, args);
                }
                catch (TargetInvocationException tie)
                {
                    var inner = tie.InnerException ?? tie;
                    createThrew = true;
                    createThrowMsg = inner.ToString();
                    createResult = null;
                }
                Stage("createInstance");
                bool createSaid = createResult is bool b && b;
                var hostObj = args[createOutIndex] as KBObject;
                if (hostObj == null)
                {
                    // Out-parameter null — check disk for the host (the API may have
                    // created+saved but returned false).
                    hostObj = _objectService.FindObject("WorkWithPlus" + parent.Name);
                }
                if (hostObj == null)
                {
                    result.FailureStage = "createInstance";
                    result.ErrorCode = "PatternAttachCreateFailed";
                    result.ErrorMessage = createThrew
                        ? "CreatePatternInstanceWithTemplate threw via " + FormatMethodSignature(createMethod) + ": " + createThrowMsg +
                          " Available overloads: " + createCandidates
                        : "CreatePatternInstanceWithTemplate returned " + (createSaid ? "true" : "false") +
                          " via " + FormatMethodSignature(createMethod) + " but host not present on disk (template='" + usedTemplate +
                          "'). Available overloads: " + createCandidates;
                    return result;
                }
                result.HostName = hostObj.Name;

                // Enable apply-on-save so future PatternInstance edits regenerate.
                try { setApplyMethod.Invoke(null, new object[] { hostObj }); }
                catch (Exception ex) { Logger.Info("SetPatternApplyOnSave best-effort: " + ex.Message); }

                // Final validate+save — triggers the engine generators.
                try
                {
                    var saveResult = validateSaveMethod.Invoke(null, new object[] { hostObj });
                    if (saveResult is bool sb && !sb)
                    {
                        result.FailureStage = "validateAndSave";
                        result.ErrorCode = "PatternAttachValidateFailed";
                        result.ErrorMessage = "ValidateAndSave returned false for host '" + result.HostName + "' (template='" + usedTemplate +
                            "'). The WorkWithPlus package created the host but rejected it during validation, so the " + parentType +
                            " was left without a bound pattern instance.";
                        RemoveOrphanHost(parent, result);
                        return result;
                    }
                }
                catch (TargetInvocationException tie)
                {
                    var inner = tie.InnerException ?? tie;
                    result.FailureStage = "validateAndSave";
                    result.ErrorCode = "PatternAttachValidateFailed";
                    result.ErrorMessage = "ValidateAndSave threw via " + FormatMethodSignature(validateSaveMethod) + ": " + inner.ToString();
                    RemoveOrphanHost(parent, result);
                    return result;
                }
                Stage("validateAndSave");

                // F17: Trigger the projection step that the IDE does on apply. Found via
                // SDK probe (docs/sdk-probe/) — the lifecycle is:
                //   Pattern (PatternDefinition).PatternImplementation
                //     → .GetBuildProcess() returns IPatternBuildProcess
                //     → .UpdateParentObject(parent, instance) projects PatternInstance
                //       onto the bound KBObject's WebForm.
                //
                // This is what the IDE calls internally. We invoke via reflection so we
                // don't add a hard dependency. A projection failure is NOT best-effort:
                // an unprojected host produces exactly the symptom in issue #330 — a
                // WebPanel that renders but whose controls never reach the server.
                if (!TryInvokeBuildProcessUpdateParent(parent, hostObj, out var projectionFailure))
                {
                    result.FailureStage = "projection";
                    result.ErrorCode = "PatternAttachProjectionFailed";
                    result.ErrorMessage = "The WorkWithPlus host was created and saved, but projecting it onto '" + parent.Name +
                        "' failed: " + (projectionFailure ?? "unknown projection failure") +
                        ". The host is an orphan until this is resolved; the " + parentType + " itself was not modified.";
                    RemoveOrphanHost(parent, result);
                    return result;
                }
                Stage("projection");

                // A generated WorkWithPlus<Parent> object is not sufficient proof:
                // the package can create the host and still fail to bind its
                // PatternInstance to the original WebPanel/WebComponent. Re-read via
                // the same SDK API used at the beginning of apply_pattern and only
                // report success when the association is visible.
                object attachedInstance;
                try
                {
                    attachedInstance = _engine.GetPatternInstance(parent, WorkWithPlusPatternId);
                }
                catch (Exception ex)
                {
                    result.FailureStage = "attachVerification";
                    result.ErrorCode = "PatternAttachVerifyFailed";
                    result.ErrorMessage = "Pattern host '" + result.HostName + "' was created, but post-attach PatternInstance verification threw: " + ex;
                    RemoveOrphanHost(parent, result);
                    return result;
                }
                if (attachedInstance == null)
                {
                    result.FailureStage = "attachVerification";
                    result.ErrorCode = "PatternAttachVerifyFailed";
                    result.ErrorMessage = "Pattern host '" + result.HostName + "' was created and saved via " + FormatMethodSignature(createMethod) +
                        ", but no WorkWithPlus PatternInstance is associated with parent '" + parent.Name +
                        "' after the call. Available overloads: " + createCandidates;
                    RemoveOrphanHost(parent, result);
                    return result;
                }

                Logger.Info("Package-interface attach succeeded: host='" + result.HostName + "' parent='" + parent.Name + "' template='" + usedTemplate + "'");
                result.Attached = true;
                // CreatePatternInstanceWithTemplate already saved the host, so this is a
                // confirmed write for the host object the caller did not name.
                WriteService.NotePerTargetWrite(result.HostName);
                return result;
            }
            catch (Exception ex)
            {
                result.FailureStage = result.FailureStage ?? "attach";
                result.ErrorMessage = ex.ToString();
                Logger.Warn("TryPackageInterfaceAttach unexpected: " + ex);
                return result;
            }
        }

        /// <summary>
        /// Fail-closed cleanup for a host this attach created but could not finish
        /// binding. Leaving it behind is not cosmetic: the next apply sees the host,
        /// takes the reapply route, skips the engine apply and projects an empty
        /// PatternInstance — a WebPanel that renders and does nothing.
        /// </summary>
        private void RemoveOrphanHost(KBObject parent, WwpAttachResult result)
        {
            string hostName = "WorkWithPlus" + parent?.Name;
            if (string.IsNullOrEmpty(hostName) || result == null) return;
            try
            {
                var host = _objectService?.FindObject(hostName);
                if (host == null)
                {
                    // Nothing to clean: the package did not keep the host. Record it so the
                    // response does not claim an orphan that could not be removed.
                    if (_objectService != null) result.OrphanHostAbsent = true;
                    return;
                }
                if (Compatibility.SdkDeletionAdapter.TryDeleteOrRemove(host))
                {
                    result.RemovedOrphanHost = hostName;
                    Logger.Info("Attach failed at stage '" + result.FailureStage + "' — removed the orphan host '" + hostName + "' so the next apply starts clean.");
                }
            }
            catch (Exception ex)
            {
                result.OrphanHostWarning = "A partially attached '" + hostName + "' host could not be removed (" + ex.Message +
                    "). Delete it with genexus_delete_object before retrying, otherwise the next apply takes the reapply route and leaves the " +
                    (parent?.TypeDescriptor?.Name ?? "target") + " with an unprojected pattern instance.";
                Logger.Warn("Attach cleanup of '" + hostName + "' failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Turn a failed attach into an instruction the caller can act on. Every
        /// branch is keyed by the stage that failed, because "try a different
        /// template" is useless advice for a package that does not expose the
        /// attachment API, and "update the package" is useless advice for a missing
        /// template object.
        /// </summary>
        internal static string BuildAttachRecommendation(WwpAttachResult attach, string parentTypeName, string targetName)
        {
            string host = "WorkWithPlus" + targetName;
            // Every preflight stage means the same thing to the caller: the installed
            // package does not expose the API this GeneXus major needs. Retrying with
            // a different template cannot help, so say that once instead of branching.
            if (string.Equals(attach.ErrorCode, WwpAttachPreflight.BlockedCode, StringComparison.Ordinal))
            {
                return "The installed WorkWithPlus package does not expose the API this GeneXus major needs for " + parentTypeName +
                    " targets (blocked at stage '" + attach.FailureStage + "'). Update the WorkWithPlus pattern package for the active GeneXus " +
                    "installation and rerun mode=diagnose, or apply WorkWithPlus to a Transaction, which the pattern engine generates natively. " +
                    "result.attachPreflight carries the resolution candidates that were rejected.";
            }

            switch (attach.FailureStage)
            {
                case "environment":
                    return "Grant Modify permission on the effective Environment.config to the non-elevated MCP identity, then run mode=diagnose again. Do not redirect UserAppDataPath away from the value configured for the GeneXus IDE.";
                case WwpAttachPreflight.StageTemplate:
                    return "Import or create a 'WorkWithPlus for Web Template' object in this KB, or set settings.template to one of patternResult.availableTemplates. " +
                        "A " + parentTypeName + " cannot receive WorkWithPlus without a template.";
                case "hostConflict":
                    return "'" + host + "' already exists and was not created by this call, so the attach will not adopt it. " +
                        "Delete it with genexus_delete_object name=" + host + " confirm=true, then retry; if it is a legitimate host whose PatternInstance is already bound, use reapply=true instead.";
                case "validateAndSave":
                    if (attach.PackageErrors != null && attach.PackageErrors.Count > 0)
                    {
                        string reported = "The WorkWithPlus package rejected '" + host + "' and reported: " + string.Join(" ", attach.PackageErrors) + " ";
                        if (PackageErrorsRequireDesignSystem(attach.PackageErrors))
                            return reported + "Import the WorkWithPlus Design System once in the GeneXus IDE (WorkWithPlus > for Web > Design System Wizard), then retry; " +
                                "a different template will not help. " + DescribeOrphanCleanup(attach, host);
                        return reported + "Address what the package reported, then retry. " + DescribeOrphanCleanup(attach, host);
                    }
                    return "The WorkWithPlus package created '" + host + "' but rejected it during validation. Try a different 'WorkWithPlus for Web Template' from patternResult.availableTemplates, " +
                        "or update the WorkWithPlus package. " + DescribeOrphanCleanup(attach, host);
                case "projection":
                    return "The host was created but the pattern could not be projected onto '" + targetName + "'. A host without a projection renders but never reaches the server. " +
                        DescribeOrphanCleanup(attach, host) +
                        " Rerun mode=diagnose and check the worker log for the [WWP-PROJECT] line that names the failing lifecycle callback.";
                default:
                    return IsWwpDirectAttachParentType(parentTypeName)
                        ? "Pass an explicit settings.template from patternResult.availableTemplates, or apply WorkWithPlus to a Transaction — the engine generates 'WW<Trn>' as a wired WWP screen."
                        : "Apply WorkWithPlus to a Transaction to generate the WWP family.";
            }
        }

        private static string DescribeOrphanCleanup(WwpAttachResult attach, string host)
        {
            if (!string.IsNullOrEmpty(attach.OrphanHostWarning)) return attach.OrphanHostWarning;
            if (!string.IsNullOrEmpty(attach.RemovedOrphanHost))
                return "The orphan host '" + attach.RemovedOrphanHost + "' was removed, so a retry starts clean.";
            if (attach.OrphanHostAbsent)
                return "No '" + host + "' host remains in the KB, so a retry starts clean.";
            return "The orphan host '" + host + "' could not be removed automatically; delete it with genexus_delete_object confirm=true before retrying.";
        }

        /// <summary>
        /// The WorkWithPlus package refuses the first web attach of a KB until its Design
        /// System has been imported through the IDE wizard. No template or package update
        /// fixes that, so it gets its own instruction.
        /// </summary>
        internal static bool PackageErrorsRequireDesignSystem(IEnumerable<string> packageErrors) =>
            packageErrors != null && packageErrors.Any(e => e != null && e.IndexOf("Design System", StringComparison.OrdinalIgnoreCase) >= 0);

        /// <summary>
        /// The failure sentence of a direct attach: the stage that failed, its error and,
        /// when the package reported them, the package's own validation messages.
        /// </summary>
        internal static string BuildAttachNoOpReason(WwpAttachResult attach, string parentTypeName)
        {
            string reason = "Engine ApplyPattern void overload no-op'd on a " + parentTypeName +
                " target, and the WorkWithPlus direct-attach route failed at stage '" +
                (attach?.FailureStage ?? "unknown") + "': " + (attach?.ErrorMessage ?? "unknown");
            if (attach?.PackageErrors != null && attach.PackageErrors.Count > 0)
                reason += " The WorkWithPlus package reported: " + string.Join(" ", attach.PackageErrors);
            return reason;
        }

        /// <summary>
        /// Next step for a failed attach, keyed by the stage that failed. Offering
        /// "retry with a different template" for a package that never exposed the
        /// attachment API is how issue #330 burned a day of blind retries.
        /// </summary>
        internal static JArray BuildAttachNextSteps(string failureStage, string targetName, string patternKey)
        {
            var steps = new JArray();
            switch (failureStage)
            {
                case "environment":
                    steps.Add(McpResponse.NextStep(
                        tool: "genexus_apply_pattern",
                        args: new JObject { ["name"] = targetName, ["pattern"] = patternKey, ["mode"] = "diagnose" },
                        why: "Rerun mode=diagnose after correcting the Environment.config ACL; it now reports the exact attach stage that blocked."));
                    break;
                case "hostConflict":
                    steps.Add(McpResponse.NextStep(
                        tool: "genexus_delete_object",
                        args: new JObject { ["name"] = "WorkWithPlus" + targetName, ["confirm"] = true },
                        why: "The pre-existing host blocks CreatePatternInstanceWithTemplate. Delete it first if it is an orphan."));
                    steps.Add(McpResponse.NextStep(
                        tool: "genexus_apply_pattern",
                        args: new JObject { ["name"] = targetName, ["pattern"] = patternKey, ["reapply"] = true },
                        why: "If that host is legitimate and its PatternInstance is already bound, reapply instead of deleting it."));
                    break;
                case WwpAttachPreflight.StageTemplate:
                    steps.Add(McpResponse.NextStep(
                        tool: "genexus_list_objects",
                        args: new JObject { ["typeFilter"] = "WorkWithPlus for Web Template" },
                        why: "A WebPanel cannot receive WorkWithPlus without a template object; list the ones registered in this KB to confirm."));
                    break;
                default:
                    steps.Add(McpResponse.NextStep(
                        tool: "genexus_apply_pattern",
                        args: new JObject { ["name"] = targetName, ["pattern"] = patternKey, ["mode"] = "diagnose" },
                        why: "mode=diagnose runs the same preflight the attach does and names the blocking stage with its resolution candidates."));
                    break;
            }
            if (failureStage != "hostConflict")
            {
                steps.Add(McpResponse.NextStep(
                    tool: "genexus_apply_pattern",
                    args: new JObject { ["name"] = targetName, ["pattern"] = patternKey, ["settings"] = new JObject { ["template"] = "<name from result.availableTemplates>" } },
                    why: "Retry pinned to a specific 'WorkWithPlus for Web Template' once the reported stage is addressed."));
            }            return steps;
        }

        // F17 / F18: delegates to the shared helper. Kept as a thin wrapper so the
        // apply_pattern → projection flow keeps its log context. The actual reflection
        // lives in WwpProjectionHelper so WriteService can call it too.
        internal void TryInvokeBuildProcessUpdateParent(KBObject parent, KBObject host)
        {
            TryInvokeBuildProcessUpdateParent(parent, host, out _);
        }

        // F17 / F18: delegates to the shared helper. The overload surfaces the
        // concrete reason the projection could not run, which the attach path needs
        // in order to fail closed instead of reporting a success it cannot back
        // (issue #330). Kept as a thin wrapper so the apply_pattern → projection
        // flow keeps its log context. The reflection lives in WwpProjectionHelper
        // so WriteService can call it too.
        internal bool TryInvokeBuildProcessUpdateParent(KBObject parent, KBObject host, out string failure)
        {
            bool ok = WwpProjectionHelper.TryProjectHostOntoParent(parent, host, out var result);
            failure = result?.Failure;
            if (!ok && string.IsNullOrEmpty(failure))
            {
                failure = "the WorkWithPlus build process did not complete its lifecycle on host '" + host?.Name + "'.";
            }
            return ok;
        }

        // Legacy implementation kept for reference; superseded by the call above.
        private void TryInvokeBuildProcessUpdateParent_Legacy(KBObject parent, KBObject host)
        {
            try
            {
                var wwpAsm = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => string.Equals(a.GetName().Name, "DVelop.Patterns.WorkWithPlus", StringComparison.OrdinalIgnoreCase));
                if (wwpAsm == null) { Logger.Debug("[BUILD-PROC] DVelop.Patterns.WorkWithPlus not loaded"); return; }

                var workWithPatternType = wwpAsm.GetType("DVelop.Patterns.WorkWithPlus.WorkWithPattern", false);
                if (workWithPatternType == null) { Logger.Debug("[BUILD-PROC] WorkWithPattern type not found"); return; }

                object impl;
                try { impl = Activator.CreateInstance(workWithPatternType); }
                catch (Exception ex) { Logger.Debug("[BUILD-PROC] WorkWithPattern ctor failed: " + ex.Message); return; }
                if (impl == null) { Logger.Debug("[BUILD-PROC] WorkWithPattern instance null"); return; }

                // PatternImplementation.Initialize() may be needed for the impl to be
                // functional. Best-effort call.
                try
                {
                    var initMethod = workWithPatternType.GetMethod("Initialize",
                        BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                    initMethod?.Invoke(impl, null);
                }
                catch (Exception ex) { Logger.Debug("[BUILD-PROC] Initialize skipped: " + ex.Message); }

                var getBuildProcess = workWithPatternType.GetMethod("GetBuildProcess",
                    BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (getBuildProcess == null) { Logger.Debug("[BUILD-PROC] GetBuildProcess() not found"); return; }

                object buildProcess = getBuildProcess.Invoke(impl, null);
                if (buildProcess == null) { Logger.Debug("[BUILD-PROC] GetBuildProcess returned null"); return; }

                var updateParent = buildProcess.GetType().GetMethod("UpdateParentObject",
                    BindingFlags.Public | BindingFlags.Instance);
                if (updateParent == null) { Logger.Debug("[BUILD-PROC] UpdateParentObject() not found on " + buildProcess.GetType().FullName); return; }

                Logger.Info("[BUILD-PROC] Invoking " + buildProcess.GetType().FullName + ".UpdateParentObject(parent=" + parent.Name + ", host=" + host.Name + ")");
                updateParent.Invoke(buildProcess, new object[] { parent, host });
                Logger.Info("[BUILD-PROC] UpdateParentObject returned successfully");

                // Save parent so projected changes persist. Use KBObjectSavePreferences
                // with ForceSave + SkipValidation because the projected WebForm can fail
                // WebPanel-level semantic validation while still being structurally
                // correct (same trick the IDE uses internally per WriteVisualPart).
                try
                {
                    var prefs = new global::Artech.Architecture.Common.Objects.KBObjectSavePreferences
                    {
                        ForceSave = true,
                        ForceSaveDefaultParts = true,
                        SkipValidation = true
                    };
                    parent.Save(prefs);
                    Logger.Info("[BUILD-PROC] Saved parent '" + parent.Name + "' (ForceSave+SkipValidation) after projection.");
                }
                catch (Exception saveEx)
                {
                    Logger.Info("[BUILD-PROC] ForceSave parent threw: " + saveEx.Message + " — falling back to EnsureSave(true).");
                    try { parent.EnsureSave(true); } catch (Exception ex2) { Logger.Info("[BUILD-PROC] EnsureSave fallback also failed: " + ex2.Message); }
                }
            }
            catch (TargetInvocationException tie)
            {
                var inner = tie.InnerException ?? tie;
                Logger.Warn("[BUILD-PROC] UpdateParentObject threw: " + inner.GetType().Name + ": " + inner.Message);
            }
            catch (Exception ex)
            {
                Logger.Warn("[BUILD-PROC] UpdateParentObject reflection failed: " + ex.Message);
            }
        }

        // Run the WWP_ApplyTemplate MSBuild task in-process. This is the IDE's exact
        // path for "Apply Template to WebPanel". The task takes WebPanelName +
        // TemplateName + KB as inputs and Execute() returns Boolean.
        //
        // MSBuild tasks normally need BuildEngine / Log infrastructure. We stub
        // BuildEngine with a NullBuildEngine impl so Execute() can call Log.* methods
        // without NPE, then invoke Execute reflectively.
        internal bool TryRunWwpApplyTemplateTask(Assembly wwpAsm, KBObject webPanel, string templateName, out string hostName, out string errorMessage)
            => TryRunWwpApplyTemplateTask(wwpAsm, webPanel, templateName, out hostName, out errorMessage, out _);

        internal bool TryRunWwpApplyTemplateTask(Assembly wwpAsm, KBObject webPanel, string templateName, out string hostName, out string errorMessage,
            out List<string> packageErrors)
        {
            hostName = null;
            errorMessage = null;
            packageErrors = null;
            var engine = new NullBuildEngine();
            try
            {
                return RunWwpApplyTemplateTask(wwpAsm, webPanel, templateName, engine, out hostName, out errorMessage);
            }
            finally
            {
                // The task reports why the package rejected the host only through its
                // logger; hand those errors back instead of leaving them in the log.
                if (engine.Errors.Count > 0)
                {
                    packageErrors = new List<string>(engine.Errors);
                    if (errorMessage != null) errorMessage += " Package reported: " + string.Join(" ", engine.Errors);
                }
            }
        }

        private bool RunWwpApplyTemplateTask(Assembly wwpAsm, KBObject webPanel, string templateName, NullBuildEngine engine, out string hostName, out string errorMessage)
        {
            hostName = null;
            errorMessage = null;
            try
            {
                var taskType = wwpAsm.GetType("DVelop.Patterns.WorkWithPlus.MSBuildTasks.WWP_ApplyTemplate", false);
                if (taskType == null) { errorMessage = "WWP_ApplyTemplate type not found"; return false; }

                object task;
                try { task = Activator.CreateInstance(taskType); }
                catch (Exception ex) { errorMessage = "WWP_ApplyTemplate ctor failed: " + ex.Message; return false; }

                var kb = _objectService.GetKbService()?.GetKB();
                if (kb == null) { errorMessage = "No KB open"; return false; }

                // Set the task inputs. KB property is the KB instance; WebPanelName +
                // TemplateName drive the apply target.
                taskType.GetProperty("KB")?.SetValue(task, kb);
                taskType.GetProperty("WebPanelName")?.SetValue(task, webPanel.Name);
                taskType.GetProperty("TemplateName")?.SetValue(task, templateName);
                taskType.GetProperty("WebPanelDescription")?.SetValue(task, webPanel.Description ?? webPanel.Name);
                taskType.GetProperty("AddWebPanelToListPrograms")?.SetValue(task, false);
                taskType.GetProperty("CaptureOutput")?.SetValue(task, true);

                // BuildEngine stub — Execute() uses Log methods which dereference BuildEngine.
                // The same instance collects the package's error events for the caller.
                try { taskType.GetProperty("BuildEngine")?.SetValue(task, engine); }
                catch (Exception ex) { Logger.Debug("BuildEngine stub set skipped: " + ex.Message); }

                var executeMethod = taskType.GetMethod("Execute", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (executeMethod == null) { errorMessage = "WWP_ApplyTemplate.Execute() not found"; return false; }

                bool ok;
                try { ok = (bool)executeMethod.Invoke(task, null); }
                catch (TargetInvocationException tie)
                {
                    var inner = tie.InnerException ?? tie;
                    errorMessage = "Execute threw: " + inner.GetType().Name + ": " + inner.Message;
                    return false;
                }

                if (!ok)
                {
                    var output = taskType.GetProperty("Output")?.GetValue(task) as string;
                    errorMessage = "Execute returned false. Output: " + (output ?? "<empty>");
                    return false;
                }

                // Look up the host the task created. Convention: WorkWithPlus<WebPanelName>.
                hostName = "WorkWithPlus" + webPanel.Name;
                return true;
            }
            catch (Exception ex)
            {
                errorMessage = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        // Stub IBuildEngine for the WWP MSBuild tasks, which reference
        // BuildEngine.LogMessage / LogError directly. Messages go to the worker log;
        // error events are also kept, because they carry the package's own reason
        // for rejecting a host (for example a Design System that was never imported).
        internal sealed class NullBuildEngine : Microsoft.Build.Framework.IBuildEngine
        {
            internal List<string> Errors { get; } = new List<string>();
            public bool ContinueOnError => true;
            public int LineNumberOfTaskNode => 0;
            public int ColumnNumberOfTaskNode => 0;
            public string ProjectFileOfTaskNode => string.Empty;
            public bool BuildProjectFile(string projectFileName, string[] targetNames, System.Collections.IDictionary globalProperties, System.Collections.IDictionary targetOutputs) => true;
            public void LogCustomEvent(Microsoft.Build.Framework.CustomBuildEventArgs e) { }
            public void LogErrorEvent(Microsoft.Build.Framework.BuildErrorEventArgs e)
            {
                Logger.Info("[WWP_TASK ERR] " + e?.Message);
                if (!string.IsNullOrWhiteSpace(e?.Message)) Errors.Add(e.Message.Trim());
            }
            public void LogMessageEvent(Microsoft.Build.Framework.BuildMessageEventArgs e) { Logger.Debug("[WWP_TASK] " + e.Message); }
            public void LogWarningEvent(Microsoft.Build.Framework.BuildWarningEventArgs e) { Logger.Info("[WWP_TASK WARN] " + e.Message); }
        }

        // [OBSOLETE — kept for reference] Direct-attach via private ctor produced ghost
        // hosts whose PatternInstance edits had no projection. Replaced by
        // TryPackageInterfaceAttach above (uses the official WWP package API).
        internal bool TryDirectAttachPatternInstance(KBObject parent, Guid patternId, out string hostName, out string errorMessage)
        {
            hostName = null;
            errorMessage = null;
            if (parent == null) { errorMessage = "parent KBObject is null"; return false; }

            try
            {
                var asm = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => string.Equals(a.GetName().Name, "Artech.Packages.Patterns", StringComparison.OrdinalIgnoreCase));
                if (asm == null) { errorMessage = "Artech.Packages.Patterns not loaded"; return false; }

                var piType = asm.GetType("Artech.Packages.Patterns.Objects.PatternInstance", false);
                if (piType == null) { errorMessage = "PatternInstance type not found"; return false; }

                // Locate the (KBObject, Guid) ctor — usually non-public.
                var ctor = piType.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .FirstOrDefault(c =>
                    {
                        var ps = c.GetParameters();
                        if (ps.Length != 2) return false;
                        return typeof(KBObject).IsAssignableFrom(ps[0].ParameterType)
                            && ps[1].ParameterType == typeof(Guid);
                    });
                if (ctor == null) { errorMessage = "PatternInstance(KBObject, Guid) ctor not found"; return false; }

                object instance;
                try
                {
                    instance = ctor.Invoke(new object[] { parent, patternId });
                }
                catch (TargetInvocationException tie)
                {
                    var inner = tie.InnerException ?? tie;
                    errorMessage = "ctor threw: " + inner.GetType().Name + ": " + inner.Message;
                    return false;
                }
                if (instance == null) { errorMessage = "ctor returned null"; return false; }

                // The instance is a KBObject. Give it a sensible name and Save() so it
                // lands in the KB. Convention matches what the engine produces for
                // Transaction targets: "WorkWithPlus<parentName>".
                string desiredName = "WorkWithPlus" + parent.Name;
                try
                {
                    var nameProp = piType.GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
                    if (nameProp != null && nameProp.CanWrite)
                    {
                        nameProp.SetValue(instance, desiredName);
                    }
                }
                catch (Exception ex) { Logger.Debug("DirectAttach: set Name skipped: " + ex.Message); }

                // Seed the PatternInstance part with a minimal valid Panel-mode XML.
                // Without this, Save() throws ValidationException("A validação de
                // WorkWithPlus instância falhou.") because WWP rejects an empty <instance/>.
                // The XML must declare type="Panel" so the engine generates the panel
                // family (PanelView, PanelTabTabular etc) rather than the Transaction one.
                try
                {
                    var parts = piType.GetProperty("Parts", BindingFlags.Public | BindingFlags.Instance)?.GetValue(instance) as System.Collections.IEnumerable;
                    KBObjectPart targetPart = null;
                    if (parts != null)
                    {
                        foreach (var p in parts)
                        {
                            var part = p as KBObjectPart;
                            if (part == null) continue;
                            if (string.Equals(part.Name, "PatternInstance", StringComparison.OrdinalIgnoreCase) ||
                                part.GetType().Name.IndexOf("PatternInstance", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                targetPart = part;
                                break;
                            }
                        }
                    }

                    if (targetPart != null)
                    {
                        // Mode/Dirty bookkeeping mirrors WriteService.ApplyPatternEnvelope —
                        // without this the SDK skips persisting the seeded XML.
                        WriteService.ForcePatternPartDirty(targetPart);

                        // Minimal valid seed: an `<instance type="WebPanel">` with a
                        // `<WPRoot Template="<resolved>">` host that has a `<table>` (required,
                        // CanModifyCollection=false in the schema) and a `<steps/>` slot.
                        // Validated against real samples in WWP/Resources/BaseXmlObjects.xml.
                        //
                        // Template name MUST resolve to a `WorkWithPlus for Web Template`
                        // KBObject in THIS KB — the validator rejects unknown names. We
                        // discover one at runtime instead of hard-coding "Empty" (which is
                        // a WWP-ship name that most real KBs don't register).
                        string template = ResolveAvailableWwpTemplate(null);
                        string seedXml =
                            "<instance type=\"" + (parent.TypeDescriptor?.Name == "SDPanel" ? "Panel" : "WebPanel") + "\">\n" +
                            "  <WPRoot Template=\"" + System.Security.SecurityElement.Escape(template) + "\" defaultTemplate=\"" + System.Security.SecurityElement.Escape(template) + "\" childrenOrderedList=\"7;66;TableMain\">\n" +
                            "    <table name=\"TableMain\" themeClass=\"TableMain\" defaultThemeClass=\"TableMain\" type=\"Responsive\" defaultType=\"Responsive\" childrenOrderedList=\"7;28;ErrorViewer\">\n" +
                            "      <errorViewer defaultThemeClass=\"ErrorViewer\" />\n" +
                            "    </table>\n" +
                            "    <steps />\n" +
                            "  </WPRoot>\n" +
                            "</instance>";
                        if (!WriteService.ApplyPatternDataFromXml(targetPart, seedXml))
                        {
                            Logger.Warn("DirectAttach: seed XML failed to apply via DeserializeDataFrom — Save will likely fail validation.");
                        }
                    }
                }
                catch (Exception seedEx)
                {
                    Logger.Debug("DirectAttach: seed step skipped: " + seedEx.Message);
                }

                // Save the new host. KBObject.Save() is in the inherited base class.
                var saveMethod = piType.GetMethod("Save", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (saveMethod == null) { errorMessage = "PatternInstance.Save() not found"; return false; }
                try
                {
                    saveMethod.Invoke(instance, null);
                }
                catch (TargetInvocationException tie)
                {
                    var inner = tie.InnerException ?? tie;
                    errorMessage = "Save threw: " + inner.GetType().Name + ": " + inner.Message;
                    return false;
                }

                hostName = desiredName;
                Logger.Info("PatternInstance direct-attach succeeded: host='" + desiredName + "' parent='" + parent.Name + "' (" + parent.TypeDescriptor?.Name + ")");
                return true;
            }
            catch (Exception ex)
            {
                errorMessage = ex.GetType().Name + ": " + ex.Message;
                Logger.Warn("DirectAttach unexpected failure: " + ex);
                return false;
            }
        }

        // Diagnostic dump of the SDK surface we can reach from the pattern definition.
        // Enabled by GX_MCP_PATTERN_PROBE=1 to keep the response clean by default. Lists
        // public static methods on PatternEngine, public instance methods on the
        // PatternDefinition runtime type, and public methods on PatternInstance — so we
        // can iterate toward a direct-attach API for the WebPanel-target case.
        private JObject DumpSdkSurface(object patternDefinition, Guid patternId)
        {
            var dump = new JObject();
            try
            {
                var asm = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => string.Equals(a.GetName().Name, "Artech.Packages.Patterns", StringComparison.OrdinalIgnoreCase));
                if (asm == null) { dump["error"] = "Artech.Packages.Patterns not loaded"; return dump; }

                var patternEngine = asm.GetType("Artech.Packages.Patterns.PatternEngine", false);
                var patternInstance = asm.GetType("Artech.Packages.Patterns.Objects.PatternInstance", false);

                dump["engineStatics"] = DumpMethods(patternEngine, Compatibility.SdkMemberProbe.Static);
                dump["instanceStatics"] = DumpMethods(patternInstance, Compatibility.SdkMemberProbe.Static);
                dump["instanceInstance"] = DumpMethods(patternInstance, BindingFlags.Public | BindingFlags.Instance);

                // F16: deep-probe DVelop.Patterns.WorkWithPlus — the WWP-specific impl
                // assembly. Also: Artech.Template.Helper which exposes template-apply
                // surface, plus any 'WorkWithPlus for Web Template' KBObject descriptor.
                // Hunting for: WorkWithPlusInstance / WorkWithPlusForWebTemplate /
                // anything with "ApplyTemplate" / "Initialize" / "Generate".
                var wwpAsm = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => string.Equals(a.GetName().Name, "DVelop.Patterns.WorkWithPlus", StringComparison.OrdinalIgnoreCase));
                if (wwpAsm == null)
                {
                    try
                    {
                        var gxPath = Environment.GetEnvironmentVariable("GX_PATH") ?? @"C:\Program Files (x86)\GeneXus\GeneXus18";
                        var wwpDllPath = Path.Combine(gxPath, "Packages", "Patterns", "WorkWithPlus", "DVelop.Patterns.WorkWithPlus.dll");
                        if (File.Exists(wwpDllPath)) wwpAsm = Assembly.LoadFrom(wwpDllPath);
                    }
                    catch (Exception ex) { dump["wwpAsmLoadErr"] = ex.Message; }
                }

                if (wwpAsm != null)
                {
                    var wwpTypes = new JArray();
                    foreach (var t in wwpAsm.GetTypes())
                    {
                        if (!t.IsPublic) continue;
                        if (t.Name.IndexOf("Template", StringComparison.OrdinalIgnoreCase) < 0 &&
                            t.Name.IndexOf("Instance", StringComparison.OrdinalIgnoreCase) < 0 &&
                            t.Name.IndexOf("Generator", StringComparison.OrdinalIgnoreCase) < 0 &&
                            t.Name.IndexOf("Helper", StringComparison.OrdinalIgnoreCase) < 0 &&
                            t.Name.IndexOf("Apply", StringComparison.OrdinalIgnoreCase) < 0 &&
                            t.Name.IndexOf("Engine", StringComparison.OrdinalIgnoreCase) < 0)
                            continue;
                        var entry = new JObject { ["fullName"] = t.FullName };
                        entry["publicStatic"] = DumpMethods(t, Compatibility.SdkMemberProbe.Static);
                        entry["publicInstance"] = DumpMethods(t, BindingFlags.Public | BindingFlags.Instance);
                        // Add properties for tasks (MSBuild tasks expose inputs as props).
                        var propArr = new JArray();
                        foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                        {
                            propArr.Add(p.Name + ":" + p.PropertyType.Name);
                        }
                        entry["publicProperties"] = propArr;
                        wwpTypes.Add(entry);
                    }
                    dump["wwpTypes"] = wwpTypes;
                }

                // Probe TemplateService / template-apply route in Artech.Template.Helper.
                var tmplAsm = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name?.IndexOf("Template", StringComparison.OrdinalIgnoreCase) >= 0);
                if (tmplAsm != null)
                {
                    dump["templateAsmName"] = tmplAsm.GetName().Name;
                    var tmplTypes = new JArray();
                    foreach (var t in tmplAsm.GetExportedTypes())
                    {
                        if (t.Name.IndexOf("Apply", StringComparison.OrdinalIgnoreCase) < 0 &&
                            t.Name.IndexOf("Service", StringComparison.OrdinalIgnoreCase) < 0 &&
                            t.Name.IndexOf("Template", StringComparison.OrdinalIgnoreCase) < 0)
                            continue;
                        tmplTypes.Add(new JObject
                        {
                            ["fullName"] = t.FullName,
                            ["methods"] = DumpMethods(t, Compatibility.SdkMemberProbe.StaticOrInstanceAnyVisibility)
                        });
                    }
                    dump["templateTypes"] = tmplTypes;
                }

                // List `WorkWithPlus for Web Template` KBObject descriptor — the type
                // GUID lets us read template content + understand the apply route.
                try
                {
                    var kb = _objectService?.GetKbService()?.GetKB();
                    if (kb != null)
                    {
                        var tmplInKb = new JArray();
                        foreach (KBObject o in kb.DesignModel.Objects.GetAll())
                        {
                            if (o == null) continue;
                            if (!string.Equals(o.TypeDescriptor?.Name, "WorkWithPlus for Web Template", StringComparison.OrdinalIgnoreCase)) continue;
                            tmplInKb.Add(new JObject
                            {
                                ["name"] = o.Name,
                                ["typeGuid"] = o.TypeDescriptor.Id.ToString(),
                                ["parts"] = new JArray(o.Parts.Cast<KBObjectPart>().Select(p => p.Name).ToArray())
                            });
                            if (tmplInKb.Count >= 3) break; // sample only
                        }
                        dump["wwpWebTemplatesInKb"] = tmplInKb;
                    }
                }
                catch (Exception ex) { dump["wwpWebTemplatesErr"] = ex.Message; }

                if (patternDefinition != null)
                {
                    var t = patternDefinition.GetType();
                    dump["patternDefinitionType"] = t.FullName;

                    // Resolve ParentTypes — the list of KBObject types WWP accepts as
                    // parent. This is the key piece: if WebPanel isn't in this list, the
                    // engine silently no-ops the apply for WebPanel targets.
                    try
                    {
                        var prop = t.GetProperty("ParentTypes", BindingFlags.Public | BindingFlags.Instance);
                        var val = prop?.GetValue(patternDefinition) as System.Collections.IEnumerable;
                        var arr = new JArray();
                        if (val != null) foreach (var item in val) arr.Add(item?.ToString() ?? "null");
                        dump["parentTypes"] = arr;
                    }
                    catch (Exception ex) { dump["parentTypesError"] = ex.Message; }

                    // Dump pattern Objects with their type-scope so we can see which ones
                    // target WebPanel specifically.
                    try
                    {
                        var prop = t.GetProperty("Objects", BindingFlags.Public | BindingFlags.Instance);
                        var val = prop?.GetValue(patternDefinition) as System.Collections.IEnumerable;
                        var arr = new JArray();
                        if (val != null)
                        {
                            foreach (var item in val)
                            {
                                if (item == null) continue;
                                var pt = item.GetType();
                                var entry = new JObject { ["type"] = pt.FullName };
                                foreach (var p in pt.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                                {
                                    if (p.GetIndexParameters().Length > 0) continue;
                                    try
                                    {
                                        var v = p.GetValue(item);
                                        if (v == null) { entry[p.Name] = null; continue; }
                                        if (v is string || v is int || v is bool || v is Guid)
                                            entry[p.Name] = v.ToString();
                                        else
                                            entry[p.Name] = v.GetType().Name;
                                    }
                                    catch { }
                                }
                                arr.Add(entry);
                            }
                        }
                        dump["patternObjects"] = arr;
                    }
                    catch { }

                    // Dump DefinitionFile path so we can read the raw XML manifest.
                    try
                    {
                        var defFile = t.GetProperty("DefinitionFile")?.GetValue(patternDefinition)?.ToString();
                        var basePath = t.GetProperty("DefinitionBasePath")?.GetValue(patternDefinition)?.ToString();
                        dump["definitionFile"] = defFile;
                        dump["definitionBasePath"] = basePath;
                    }
                    catch { }

                    // Probe Pattern.cs / PatternEngine internals — try to find a Pattern
                    // instance constructor or a private "create instance for type" helper.
                    try
                    {
                        var piType = asm.GetType("Artech.Packages.Patterns.Objects.PatternInstance");
                        if (piType != null)
                        {
                            var ctors = piType.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                            var arr = new JArray();
                            foreach (var c in ctors)
                            {
                                string sig = (c.IsPublic ? "public " : "private ") +
                                    "ctor(" + string.Join(",", c.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name)) + ")";
                                arr.Add(sig);
                            }
                            dump["patternInstanceCtors"] = arr;
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                dump["error"] = ex.GetType().Name + ": " + ex.Message;
            }
            return dump;
        }

        private static JArray DumpMethods(Type t, BindingFlags flags)
        {
            var arr = new JArray();
            if (t == null) return arr;
            try
            {
                foreach (var m in t.GetMethods(flags))
                {
                    if (m.IsSpecialName) continue; // skip property getters/setters
                    if (m.DeclaringType == typeof(object)) continue;
                    string sig = m.Name + "(" + string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name)) + ") -> " + m.ReturnType.Name;
                    arr.Add(sig);
                }
            }
            catch { }
            return arr;
        }

        // Probes the KB for the canonical WorkWithPlus family generated by a first-apply
        // on a Transaction: the host (`WorkWithPlus<X>`) plus the WW/View/Export* siblings.
        // We don't iterate the whole model — that's slow on large KBs (38k+ objects) and
        // races against async SDK persistence. Targeted FindObject lookups by name are
        // O(family_size) regardless of KB size.
        //
        // Naming reference (GeneXus 18 WWP default): `WorkWithPlus<X>` host, `WW<X>`
        // selection panel, `View<X>` detail, `ExportWW<X>`/`ExportReportWW<X>` exports,
        // `Prompt<X>` prompt. WebPanel targets don't generate siblings — they get the
        // host attached directly.
        /// <summary>
        /// Expected family members that were not among <paramref name="generated"/>.
        ///
        /// <para>
        /// Compares by name rather than by count: a caller that got four of the expected
        /// objects plus an unrelated one from the adapter must not read as complete, and
        /// a caller that got the right four under a different case must not read as
        /// incomplete.
        /// </para>
        /// </summary>
        private static List<string> BuildMissingWwpFamily(string baseName, List<string> generated)
        {
            var missing = new List<string>();
            if (string.IsNullOrEmpty(baseName) || generated == null) return missing;

            var present = new HashSet<string>(generated, StringComparer.OrdinalIgnoreCase);
            foreach (var expected in WwpFamilyNaming.ExpectedFamilyNames(baseName))
            {
                if (!present.Contains(expected)) missing.Add(expected);
            }
            return missing;
        }

        /// <summary>
        /// The generated WorkWithPlus family for <paramref name="parent"/>, by name.
        ///
        /// <para>
        /// Issue #359. This used to probe a hand-written <em>prefix</em> list -
        /// <c>WW&lt;Trn&gt;</c>, <c>View&lt;Trn&gt;</c>, <c>ExportWW&lt;Trn&gt;</c> - and
        /// WorkWithPlus generates a <em>suffix</em> family: <c>&lt;Trn&gt;WW</c>,
        /// <c>&lt;Trn&gt;WWDS</c>, <c>&lt;Trn&gt;WWExport</c>,
        /// <c>&lt;Trn&gt;WWGetFilterData</c>. Every family probe therefore missed, and
        /// because this same list drives the search-index registration just below, the
        /// generated objects were neither reported nor indexed: the apply returned
        /// <c>generatedObjects: ["WorkWithPlus&lt;Trn&gt;"]</c> and the four siblings
        /// read back as <c>ObjectNotFound</c> until something else reindexed the KB.
        /// </para>
        ///
        /// <para>
        /// The apply had in fact worked. Only the report - and the index - were wrong,
        /// which is why the failure looked like a generation gap and survived a reapply
        /// and a targeted build.
        /// </para>
        /// </summary>
        private List<string> LookupWwpFamilyByConvention(KBObject parent)
        {
            var found = new List<string>();
            if (parent == null || _objectService == null) return found;
            string baseName = parent.Name;
            if (string.IsNullOrEmpty(baseName)) return found;

            // Host first, then the suffix family, then the legacy prefix shapes. See
            // WwpFamilyNaming for the observed object names behind this order.
            foreach (var name in WwpFamilyNaming.FamilyCandidates(baseName))
            {
                try
                {
                    var o = _objectService.FindObject(name);
                    if (o != null && !string.IsNullOrEmpty(o.Name)
                        && !found.Contains(o.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        found.Add(o.Name);
                    }
                }
                catch { /* lookup best-effort */ }
            }
            return found;
        }

        private PatternApplyResult TryReapplyWithFallback(
            object existingInstance,
            KBObject parent,
            object patternDefinition,
            JObject settings,
            out bool wasFirstApply)
        {
            try
            {
                var result = _engine.ReapplyPattern(existingInstance, settings);
                wasFirstApply = false;
                return result;
            }
            catch (InvalidOperationException ex) when (
                ex.Message.IndexOf("ApplyPattern(PatternInstance, ApplySettings)", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                // SDK lacks the reapply overload on this install — replay the first-apply,
                // which the engine treats as a re-apply when an instance already exists.
                Logger.Info("PatternEngine reapply overload missing — falling back to first-apply overload.");
                if (settings != null && settings.Count > 0)
                {
                    Logger.Info("Reapply fallback: settings ignored on the void overload — defaults applied.");
                }
                var result = _engine.ApplyPattern(parent, patternDefinition, settings);
                wasFirstApply = false;
                return result;
            }
        }

        private static string PatternUnavailable(string patternKey, string message, IEnumerable<string> availablePatterns = null)
        {
            var j = new JObject
            {
                ["status"] = "pattern_unavailable",
                ["patternKey"] = patternKey,
                ["message"] = message
            };
            if (availablePatterns != null) j["availablePatterns"] = new JArray(availablePatterns);
            return j.ToString(Newtonsoft.Json.Formatting.None);
        }

        // ── Item 45: Pattern Diagnose (DryRun equivalent for genexus_apply_pattern) ─────
        // Read-only preflight: resolve target + pattern, then run every validation
        // gate that ApplyPattern would run, but return structured reasons instead of
        // mutating anything. No SDK apply is called.
        //
        // Returned reasons (one or more may fire):
        //   parentTypeMismatch   — target type is not supported by the pattern
        //   overrideConflict     — an existing PatternInstance host already exists
        //   templateInvalid      — caller-supplied template not found in KB
        //   missingRequiredAttribute — target is null / unresolvable
        //   ok                   — all checks pass; apply would proceed
        //
        // Each finding object: { reason, severity, detail, remediation }
        public string DiagnosePattern(string objectName, string patternKey, JObject settings = null)
        {
            try
            {
                var findings = new JArray();

                // ── 1. Parameter validation ──────────────────────────────────────
                if (string.IsNullOrWhiteSpace(objectName))
                {
                    findings.Add(Finding("missingRequiredAttribute", "critical",
                        "objectName is empty or null.",
                        "Pass name=<KBObject name>."));
                    return DiagnoseResponse(objectName, patternKey, findings);
                }
                if (string.IsNullOrWhiteSpace(patternKey))
                {
                    findings.Add(Finding("missingRequiredAttribute", "critical",
                        "pattern key is empty or null.",
                        "Pass pattern='WorkWithPlus' or a known GUID."));
                    return DiagnoseResponse(objectName, patternKey, findings);
                }

                // A K2BTools WebPanel Designer is an embedded designer object, not
                // an SDK pattern instance. Resolve that fact before pattern-key
                // lookup so `pattern: "K2BTools"` is diagnosed honestly instead
                // of becoming Unknown pattern key / WWPInstanceNotFound.
                KBObject designerCandidate = null;
                try { designerCandidate = ResolveObject(objectName); } catch { /* preserve normal pattern diagnostics */ }
                if (designerCandidate != null
                    && K2bWebPanelDesignerService.TryRead(designerCandidate, out var k2bDesigner))
                {
                    return K2bWebPanelDesignerService.BuildPatternDiagnosis(
                        objectName, patternKey, k2bDesigner, Registry.Names());
                }

                // ── 2. Pattern resolution ────────────────────────────────────────
                if (!TryResolvePattern(patternKey, out PatternManifest pattern))
                {
                    var unknown = Finding("templateInvalid", "critical",
                        $"Unknown pattern key '{patternKey}'. Not an installed pattern and not a valid GUID.",
                        "Use an installed pattern name (see availablePatterns), the alias 'WWP', or a raw pattern GUID.");
                    unknown["availablePatterns"] = new JArray(Registry.Names());
                    findings.Add(unknown);
                    return DiagnoseResponse(objectName, patternKey, findings);
                }
                JObject patternJson = pattern.ToJson();

                // ── 3. Engine / license availability ────────────────────────────
                object patternDef = null;
                try { patternDef = _engine.GetPatternDefinition(pattern.Id); } catch { }
                if (patternDef == null)
                {
                    findings.Add(pattern.IsWorkWithPlus
                        ? Finding("templateInvalid", "critical",
                            "Pattern engine returned null for the given pattern GUID — package probably not installed or license inactive.",
                            "Verify the WorkWithPlus package is present in GeneXus\\Packages\\Patterns\\WorkWithPlus\\. Check license activation.")
                        : Finding("templateInvalid", "critical",
                            "Pattern engine returned null for " + pattern.Name + " - the package is probably not installed or its license is inactive.",
                            "Verify the " + pattern.Name + " package is present under GeneXus\\Packages\\Patterns and licensed."));
                    return DiagnoseResponse(objectName, patternKey, findings, patternJson);
                }

                // ── 4. Object resolution ─────────────────────────────────────────
                KBObject obj = ResolveObject(objectName);
                if (obj == null)
                {
                    findings.Add(Finding("missingRequiredAttribute", "critical",
                        $"Object '{objectName}' not found in the KB.",
                        "Verify the name with genexus_query or genexus_list_objects."));
                    return DiagnoseResponse(objectName, patternKey, findings, patternJson);
                }

                // An instance target is diagnosed on its parent, the object apply/reapply acts on.
                if (!pattern.IsWorkWithPlus && Analysis.MatchInstancePattern(obj) != null)
                {
                    var owner = PatternAnalysisService.ResolveInstanceParent(obj);
                    if (owner != null) obj = owner;
                }

                return DiagnoseForObject(obj.Name, obj, obj.TypeDescriptor?.Name ?? "", patternKey, pattern, settings, findings);
            }
            catch (Exception ex)
            {
                Logger.Error("PatternApplyService.DiagnosePattern failed: " + ex);
                return McpResponse.Err(code: "DiagnosePatternFailed", message: ex.Message, hint: "Check the worker log for stack trace details.", nextSteps: new JArray(McpResponse.NextStep("genexus_apply_pattern", new JObject { ["name"] = objectName }, "Retry the apply directly if diagnosis is consistently failing.")), target: objectName);
            }
        }

        // Diagnose checks that need the resolved target. Split out so the checks are
        // unit-testable with a null KBObject (the fake engine never dereferences it).
        internal string DiagnoseForObject(string objectName, KBObject obj, string parentType, string patternKey, PatternManifest pattern, JObject settings, JArray findings)
        {
            {
                findings = findings ?? new JArray();
                parentType = parentType ?? "";
                bool isWwp = pattern.IsWorkWithPlus;
                Guid patternId = pattern.Id;

                // ── 5. Parent-type gate ──────────────────────────────────────────
                string callerTemplate = settings?["template"]?.ToString();
                List<string> availableTemplates = null;
                bool isWebPanelKind = isWwp && IsWwpDirectAttachParentType(parentType);
                if (isWebPanelKind)
                {
                    try { availableTemplates = ListWwpWebTemplates(); } catch { availableTemplates = new List<string>(); }
                }

                string typeGateReject = isWwp
                    ? TryBuildTypeGateRejection(objectName, IsWwpKey(patternKey) ? patternKey : "WorkWithPlus", parentType, callerTemplate, availableTemplates)
                    : null;
                // The apply refuses a template it cannot honour before touching the KB;
                // diagnose reports the same refusal instead of a green light.
                if (isWwp && typeGateReject == null && settings?["template"] != null && settings["template"].Type != JTokenType.Null)
                {
                    string templateUnsupported = ExplicitTemplateUnsupportedReason(parentType,
                        settings["template"].Type == JTokenType.String ? (string)settings["template"] : null);
                    if (templateUnsupported != null)
                    {
                        var unsupportedFinding = Finding("templateUnsupported", "critical", templateUnsupported,
                            "Omit settings.template for a Transaction, or pass a registered template name on a WebPanel, WebComponent or SDPanel.");
                        unsupportedFinding["stage"] = "template";
                        findings.Add(unsupportedFinding);
                    }
                }
                // The manifest's ParentObjects gate a first apply only: an existing instance is
                // regenerated through the reapply route whatever its parent type.
                if (!isWwp && GetOwnedPatternInstance(obj, pattern) == null)
                {
                    string manifestReject = TryBuildManifestTypeGateRejection(objectName, patternKey, pattern, parentType);
                    if (manifestReject != null)
                    {
                        var env = JObject.Parse(manifestReject);
                        findings.Add(Finding("parentTypeMismatch", "critical",
                            env["error"]?["message"]?.ToString() ?? (pattern.Name + " cannot be applied to this object."),
                            env["error"]?["hint"]?.ToString() ?? ("Apply " + pattern.Name + " only to a supported parent object type.")));
                    }
                }
                if (typeGateReject != null)
                {
                    var rejectEnv = JObject.Parse(typeGateReject);
                    bool isTemplateIssue = rejectEnv["error"]?.ToString()?.Contains("Template") == true
                                       || rejectEnv["error"]?.ToString()?.Contains("template") == true;
                    string reason = isTemplateIssue ? "templateInvalid" : "parentTypeMismatch";
                    string detail = rejectEnv["error"]?.ToString() ?? "Type gate rejected apply.";
                    string remediation = rejectEnv["hint"]?.ToString()
                        ?? (rejectEnv["availableTemplates"] != null
                            ? "Pass settings.template equal to one of availableTemplates: " + rejectEnv["availableTemplates"]
                            : "Apply WorkWithPlus only to Transaction, WebPanel, WebComponent or SDPanel.");
                    findings.Add(Finding(reason, "critical", detail, remediation));
                }

                // ── 6. Override / existing instance conflict ─────────────────────
                // The remediation has to name a route the same response can actually
                // run. Issue #352: the overrideConflict finding unconditionally told the
                // caller to "Call genexus_apply_pattern with reapply=true to
                // regenerate the existing pattern instance", while the routeUnsupported
                // finding emitted two lines below declared that reapply is unsupported
                // for this pattern. For a pattern with reapply blocked, the diagnostic
                // recommended precisely the route it refused.
                object existingInstance = GetOwnedPatternInstance(obj, pattern);
                var route = existingInstance != null ? PatternRoute.Reapply : PatternRoute.FirstApply;
                bool reapplySupported = PatternRouteCapabilities.IsSupported(pattern, PatternRoute.Reapply, out _);
                // WorkWithPlus has its own supported reapply route, which is the
                // apply-on-save path the package runs. The generic engine switch does
                // not describe it, so the regeneration claim has to come from the
                // pattern rather than from that switch alone.
                bool reapplyReconciles = PatternRouteCapabilities.IsRegeneratingReapply(pattern);
                if (existingInstance != null)
                {
                    // The remediation is the instruction; the detail is the evidence.
                    // Both are derived from the two capability answers above, so neither
                    // can name a route the other contradicts.
                    string notice = $"An existing PatternInstance for '{patternKey}' was found on '{objectName}'.";
                    string noOp = reapplyReconciles
                        ? " A first-apply will be a no-op; use reapply=true."
                        : " A first-apply will be a no-op. Reapply through this build does not regenerate the derived objects.";
                    string action = reapplySupported
                        ? (reapplyReconciles
                            ? "Call genexus_apply_pattern with reapply=true to regenerate the existing pattern instance."
                            : "Call genexus_apply_pattern with reapply=true to re-apply the existing instance; this does not regenerate the pattern's objects."
                              + (pattern != null && pattern.IsWorkWithPlus
                                  ? " To rebuild the derived objects of a WorkWithPlus instance use genexus_wwp action=regenerate_derived."
                                  : string.Empty))
                        : "Do not repeat apply headless: reapply for this pattern is not supported by this MCP build. Apply the pattern in the GeneXus IDE to regenerate the pattern's objects, then re-read the instance with genexus_read part=PatternInstance.";

                    var conflict = Finding("overrideConflict", reapplyReconciles ? "warn" : "info",
                        notice + noOp, action);
                    conflict["reapplyRecommended"] = reapplySupported;
                    conflict["regeneratesDerivedObjects"] = reapplyReconciles;
                    conflict["remediationRoute"] = reapplySupported ? "reapply" : "ide";
                    findings.Add(conflict);
                }

                // ── 6b. Route capability (patterns other than WorkWithPlus) ──────
                // Report the route that would actually run instead of claiming a clean
                // apply and then failing on an unsupported path.
                if (!PatternRouteCapabilities.IsSupported(pattern, route, out string routeReason))
                {
                    // Issues #352, #478: qualify the permitted edit and distinguish reapply
                    // refusal from property editing; derived-object update is not measured
                    // by the edit call and recorded results vary by environment. If derived
                    // objects do not follow, apply in the GeneXus IDE.
                    var routeFinding = Finding("routeUnsupported", "critical",
                        routeReason,
                        RouteUnsupportedGuidance);
                    routeFinding["route"] = RouteName(route);
                    routeFinding["regeneratesDerivedObjects"] = false;
                    findings.Add(routeFinding);
                }

                // ── 7. WWP package environment / ACL preflight ───────────────────
                // The package resolves Environment.config from GeneXus.exe.config's
                // UserAppDataPath, which can intentionally point outside the active
                // installation directory. First-attach writes that file. Validate the
                // exact effective path without changing its contents.
                if (patternId == WorkWithPlusPatternId && isWebPanelKind && existingInstance == null)
                {
                    var environment = InspectWwpEnvironment();
                    if (environment.EnvironmentConfigWritable == false)
                    {
                        string identity;
                        try { identity = WindowsIdentity.GetCurrent()?.Name ?? Environment.UserName; }
                        catch { identity = Environment.UserName; }
                        var accessFinding = Finding(
                            environment.EnvironmentConfigExists ? "environmentConfigAccessDenied" : "environmentConfigPreflightFailed",
                            "critical",
                            environment.EnvironmentConfigExists
                                ? "WorkWithPlus will use '" + environment.EnvironmentConfigPath + "' because " +
                                  environment.ConfigSource + ", but identity '" + identity + "' cannot open the existing file for write."
                                : "The WorkWithPlus environment preflight failed before it could inspect the effective Environment.config for identity '" + identity + "'.",
                            environment.EnvironmentConfigExists
                                ? "Grant Modify to the MCP identity (or one of its groups) on the effective Environment.config. Keep UserAppDataPath aligned with the IDE, then rerun mode=diagnose before apply."
                                : "Confirm the active GeneXus installation and GeneXus.exe.config UserAppDataPath, then rerun mode=diagnose. The complete preflight exception is included in environment.accessError.");
                        accessFinding["environment"] = environment.ToJson();
                        // Same stage vocabulary the attach reports, so a caller that
                        // reads a diagnose finding and an apply error correlates them.
                        accessFinding["stage"] = "environment";
                        findings.Add(accessFinding);
                    }
                    else if (environment.EnvironmentConfigExists && environment.EnvironmentConfigWritable == true)
                    {
                        var readyFinding = Finding(
                            "environmentConfigWritable",
                            "info",
                            "WorkWithPlus Environment.config preflight passed for '" + environment.EnvironmentConfigPath + "' (source: " + environment.ConfigSource + ").",
                            "No environment ACL change is required for first attach.");
                        readyFinding["environment"] = environment.ToJson();
                        findings.Add(readyFinding);
                    }
                    else
                    {
                        var unknownFinding = Finding(
                            "environmentConfigNotFound",
                            "warn",
                            "The effective WorkWithPlus Environment.config was not found at '" + (environment.EnvironmentConfigPath ?? "<unresolved>") + "'.",
                            "Confirm GeneXus.exe.config UserAppDataPath matches the IDE context and that the MCP identity can create files under that directory.");
                        unknownFinding["environment"] = environment.ToJson();
                        findings.Add(unknownFinding);
                    }
                }

                // ── 8. Missing required attribute: template for WebPanel targets ──
                if (isWebPanelKind && string.IsNullOrEmpty(callerTemplate) && typeGateReject == null)
                {
                    if (availableTemplates != null && availableTemplates.Count > 0)
                    {
                        findings.Add(Finding("missingRequiredAttribute", "info",
                            $"No settings.template supplied for {parentType} target. MCP will auto-discover one ({ResolveAvailableWwpTemplate(null)}).",
                            $"Pass settings.template explicitly to pin the template. Available: {string.Join(", ", availableTemplates)}."));
                    }
                    else
                    {
                        findings.Add(Finding("missingRequiredAttribute", "warn",
                            $"No settings.template supplied and no 'WorkWithPlus for Web Template' objects found in this KB.",
                            $"Import or create a 'WorkWithPlus for Web Template' object before applying to a {parentType}."));
                    }
                }

                // ── 9. Direct-attach preflight (the route that actually runs) ────
                // On a WebPanel/WebComponent/SDPanel the engine's ApplyPattern is a
                // documented silent no-op, so the PatternInstancePackageInterface
                // pipeline is the route the apply will take — and every stage of it
                // can fail. Diagnose used to skip all of it and still answer "All
                // pre-apply checks passed" for targets that could never apply
                // (issue #330). Run the same resolver the attach runs so the two
                // cannot disagree. Findings are additive: a broken Environment.config
                // ACL does not stop the caller learning that the package is also
                // missing the attach API, and fixing one at a time is what made this
                // expensive to triage in the first place.
                if (patternId == WorkWithPlusPatternId && isWebPanelKind
                    && existingInstance == null && typeGateReject == null)
                {
                    try
                    {
                        var surface = WwpPackageSurface.Resolve();
                        if (surface.Assembly == null)
                        {
                            var notLoaded = Finding("wwpPackageNotLoaded", "critical",
                                "The WorkWithPlus pattern package could not be loaded: " + surface.LoadError +
                                " The pattern engine reported the pattern as available, so this is a package-installation problem, not a licensing one.",
                                "Install or update the WorkWithPlus pattern package under the active GeneXus installation's " +
                                "Packages\\Patterns\\WorkWithPlus folder, or apply WorkWithPlus to a Transaction.");
                            notLoaded["stage"] = "wwpAssembly";
                            findings.Add(notLoaded);
                        }
                        else
                        {
                            var resolvedTemplate = ResolveAvailableWwpTemplate(callerTemplate);
                            if (string.IsNullOrEmpty(resolvedTemplate))
                            {
                                var templateMissing = Finding("templateNotFound", "critical",
                                    "Template '" + callerTemplate + "' is not a registered `WorkWithPlus for Web Template` in this KB; the apply will refuse it and substitute nothing.",
                                    "Pass settings.template equal to a registered template name.");
                                templateMissing["stage"] = "template";
                                findings.Add(templateMissing);
                            }
                            else
                            {
                            var preflight = WwpAttachPreflight.Run(
                                surface.PackageInterfaceType,
                                surface.WorkWithPatternType,
                                parentType,
                                _objectService?.GetKbService()?.GetKB()?.DesignModel,
                                obj,
                                resolvedTemplate);
                            foreach (var finding in preflight.Findings) findings.Add(finding);
                            if (preflight.CanAttach)
                            {
                                var attachReady = Finding("attachRouteReady", "info",
                                    "The WorkWithPlus direct-attach route resolves for this " + parentType + " target " +
                                    "(template '" + resolvedTemplate + "'). Applying will run CreatePatternInstanceWithTemplate → SetPatternApplyOnSave → ValidateAndSave → IPatternBuildProcess.UpdateParentObject, and will fail closed on any of those stages.",
                                    "Call genexus_apply_pattern to proceed. A failure will report the exact stage in result.failureStage.");
                                attachReady["stage"] = "attach";
                                attachReady["template"] = resolvedTemplate;
                                attachReady["createOverload"] = FormatMethodSignature(preflight.CreateMethod);
                                findings.Add(attachReady);
                            }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        // A preflight that cannot run is not a pass. Report it as
                        // critical rather than letting the caller act on a green
                        // diagnosis the attach will contradict.
                        findings.Add(Finding("attachPreflightFailed", "critical",
                            "The WorkWithPlus direct-attach preflight threw before it could complete: " + ex.GetType().Name + ": " + ex.Message,
                            "Check the worker log for the full trace, then rerun mode=diagnose."));
                    }
                }

                // ── 10. ok — all critical checks passed ─────────────────────────
                if (!findings.Any(f => f["severity"]?.ToString() == "critical"))
                {
                    findings.Add(Finding("ok", "info",
                        existingInstance != null
                            ? "All pre-apply checks passed. Remember to pass reapply=true (an instance already exists)."
                            : "All pre-apply checks passed. The pattern should apply cleanly.",
                        existingInstance != null
                            ? "Remember to pass reapply=true (an instance already exists)."
                            : "Call genexus_apply_pattern to proceed."));
                }

                return DiagnoseResponse(objectName, patternKey, findings, pattern.ToJson(),
                    isWwp ? null : PatternRouteCapabilities.ToJson(pattern));
            }
        }

        // ── helpers used only by DiagnosePattern ────────────────────────────────

        private static JObject Finding(string reason, string severity, string detail, string remediation)
        {
            return new JObject
            {
                ["reason"] = reason,
                ["severity"] = severity,
                ["detail"] = detail,
                ["remediation"] = remediation
            };
        }

        private static string DiagnoseResponse(string target, string patternKey, JArray findings, JObject pattern = null, JObject routeCapabilities = null)
        {
            var hasAnyOk = findings.Any(f => f["reason"]?.ToString() == "ok");
            var hasCritical = findings.Any(f => f["severity"]?.ToString() == "critical");
            string overallStatus = hasCritical ? "blocked" : (hasAnyOk ? "ok" : "warnings");
            var resp = new JObject
            {
                ["status"] = overallStatus,
                ["target"] = target ?? "",
                ["patternKey"] = patternKey ?? "",
                ["findings"] = findings
            };
            if (pattern != null) resp["pattern"] = pattern;
            if (routeCapabilities != null) resp["routeCapabilities"] = routeCapabilities;
            return resp.ToString(Newtonsoft.Json.Formatting.None);
        }
    }

}
