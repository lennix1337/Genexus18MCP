using System;
using System.Collections.Generic;
using System.Linq;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// One independent reason the isolated Events save refuses before any write.
    /// Message keeps the sentence the preflight has always thrown; Code names it for
    /// callers, Remediation says what to change and who owns the change, and Evidence
    /// carries sanitized facts only (identity hashes, version names, assembly hash
    /// prefixes), never a path, a credential or an environment value.
    /// </summary>
    internal sealed class EventsIsolationFinding
    {
        internal EventsIsolationFinding(string code, string layer, string message, string remediation, JObject evidence = null)
        {
            Code = code;
            Layer = layer;
            Message = message;
            Remediation = remediation;
            Evidence = evidence;
        }

        internal string Code { get; }
        internal string Layer { get; }
        internal string Message { get; }
        internal string Remediation { get; }
        internal JObject Evidence { get; }

        internal JObject ToJson()
        {
            var json = new JObject
            {
                ["code"] = Code, ["layer"] = Layer, ["message"] = Message, ["remediation"] = Remediation
            };
            if (Evidence != null && Evidence.HasValues) json["evidence"] = Evidence.DeepClone();
            return json;
        }
    }

    /// <summary>
    /// Thrown by the preflight with every independent finding it could establish, in the
    /// order the checks run. The message is the first finding's, so callers that only read
    /// the message keep their behavior; callers that read the findings see the next blocker
    /// before a fix for the first one has been deployed.
    /// </summary>
    internal sealed class EventsIsolationRefusalException : InvalidOperationException
    {
        internal EventsIsolationRefusalException(IReadOnlyList<EventsIsolationFinding> findings)
            : base(findings == null || findings.Count == 0
                ? "Isolated Events save preflight refused."
                : findings[0].Message)
        {
            Findings = findings ?? Array.Empty<EventsIsolationFinding>();
        }

        internal EventsIsolationRefusalException(EventsIsolationFinding finding)
            : this(new[] { finding })
        {
        }

        internal IReadOnlyList<EventsIsolationFinding> Findings { get; }
    }

    /// <summary>
    /// Pure evaluation of the isolation prerequisites. Nothing here touches the SDK, the
    /// environment or the file system, so each refusal is testable from plain inputs and
    /// the same decision serves the preview and the real save.
    /// </summary>
    internal static class EventsIsolationFindings
    {
        internal const string PinsLayer = "pins";
        internal const string DestinationLayer = "destination";
        internal const string BundleLayer = "sdk-bundle";
        internal const string ObjectLayer = "object";
        internal const string CallbackLayer = "callbacks";
        internal const string PreflightLayer = "preflight";

        internal const string PinsSentence = "Isolated Events save requires both profile-owned KB and version pins.";

        private const string PinsRemediation =
            "The Gateway owner sets GXMCP_EXPECTED_KB_PATH and GXMCP_EXPECTED_KB_VERSION in the environment that launches this "
            + "Gateway (for example the env block of the MCP client's server entry), after confirming them with the SDK "
            + "(genexus_whoami for the KB, genexus_kb_version action=list for the active version), and restarts the Gateway so its "
            + "Worker inherits them. The MCP never derives them from the request or from the KB it opened, and Build, reorg or "
            + "retrying the edit does not supply them.";

        private const string BundleRemediation =
            "Pins confirm the destination; they do not make an SDK/WorkWithPlus bundle safe. This save is available only for a "
            + "bundle that was audited as a whole (docs/events-object-save-isolation.md), and there is no allowlist by version "
            + "number. Until this exact bundle is audited the save stays refused; do not retry it or use another write mode to bypass it.";

        private const string ScopeRemediation =
            "The native proof of this bundle covers only Knowledge Bases that have no pattern instances (WorkWithPlus or any other "
            + "pattern). The save stays refused for a Knowledge Base that has them, or whose instances cannot be counted, because that "
            + "scenario was not proven. Do not retry it or use another write mode to bypass it; a proof that covers pattern instances "
            + "is needed first (docs/events-object-save-isolation.md).";

        /// <summary>Zero findings when the pins and the opened destination agree; otherwise exactly one.</summary>
        internal static IReadOnlyList<EventsIsolationFinding> EvaluateDestination(
            string expectedPath, string expectedVersion, string actualPath, string actualVersion, bool? frozen)
        {
            string code = WriteDestinationGuard.Classify(
                expectedPath, expectedVersion, actualPath, actualVersion, frozen, opening: false, requirePins: true);
            if (code == null) return Array.Empty<EventsIsolationFinding>();

            var evidence = new JObject();
            string message;
            string remediation;
            string layer = PinsLayer;
            switch (code)
            {
                case WriteDestinationGuard.PinsMissingCode:
                    message = PinsSentence;
                    remediation = PinsRemediation;
                    evidence["missing"] = new JArray(WriteDestinationGuard.PathVariable, WriteDestinationGuard.VersionVariable);
                    break;
                case "WriteDestinationConfigurationIncomplete":
                    string missing = string.IsNullOrWhiteSpace(expectedPath)
                        ? WriteDestinationGuard.PathVariable : WriteDestinationGuard.VersionVariable;
                    message = PinsSentence + " " + missing + " is missing.";
                    remediation = PinsRemediation;
                    evidence["missing"] = new JArray(missing);
                    break;
                case "WriteDestinationKbMismatch":
                    layer = DestinationLayer;
                    message = "The opened KB is not the profile-pinned write destination; no operation was dispatched.";
                    remediation = "Compare the profile pin with the KB this Gateway opened. Nothing is activated or changed automatically; the profile owner corrects the pin or opens the right KB.";
                    evidence["expectedKbIdentity"] = WriteDestinationGuard.KbIdentity(expectedPath);
                    evidence["actualKbIdentity"] = WriteDestinationGuard.KbIdentity(actualPath);
                    break;
                case "WriteDestinationVersionMismatch":
                    layer = DestinationLayer;
                    message = "The KB's active version is not the profile-pinned version; no operation was dispatched.";
                    remediation = "Restore the pinned version explicitly (genexus_kb_version action=set_active with targetVersion equal to the pin and autoUpdate=false) or have the profile owner correct the pin. Nothing was activated.";
                    evidence["expectedVersion"] = expectedVersion;
                    evidence["actualVersion"] = actualVersion;
                    break;
                default:
                    layer = DestinationLayer;
                    message = "The pinned KB version is frozen or its writability could not be confirmed; no operation was dispatched.";
                    remediation = "Pin a writable version, or confirm the active version with genexus_kb_version action=list. A frozen version is never written.";
                    evidence["version"] = actualVersion;
                    evidence["isFrozen"] = frozen.HasValue ? (JToken)frozen.Value : JValue.CreateNull();
                    break;
            }
            return new[] { new EventsIsolationFinding(code, layer, message, remediation, evidence) };
        }

        internal static EventsIsolationFinding TargetTypeUnsupported(string objectType)
            => new EventsIsolationFinding(
                "IsolationTargetTypeUnsupported", ObjectLayer,
                "Isolated Events save supports only the exact audited SDK WebPanel type.",
                "Only an exact WebPanel carries an audited Events save contract; other types keep their own, unisolated write path.",
                new JObject { ["objectType"] = objectType });

        /// <summary>Null when the loaded bundle is one of the audited ones.</summary>
        internal static EventsIsolationFinding EvaluateBundle(
            IDictionary<string, string> loadedHashes,
            IDictionary<string, string> fileVersions,
            IReadOnlyCollection<string> missingAssemblies,
            IReadOnlyCollection<string> ambiguousAssemblies,
            string auditedBuild,
            IEnumerable<string> auditedBundleIds)
        {
            if (missingAssemblies != null && missingAssemblies.Count > 0)
                return new EventsIsolationFinding(
                    "SdkBundleAssemblyMissing", BundleLayer,
                    "Unverified SDK implementation: " + missingAssemblies.First(),
                    "An assembly the audit requires is not loaded in this Worker, so the bundle cannot be verified. " + BundleRemediation,
                    new JObject { ["missingAssemblies"] = new JArray(missingAssemblies) });
            if (ambiguousAssemblies != null && ambiguousAssemblies.Count > 0)
                return new EventsIsolationFinding(
                    "SdkBundleAssemblyAmbiguous", BundleLayer,
                    "Unverified SDK implementation: more than one loaded assembly is named " + ambiguousAssemblies.First(),
                    "Two implementations of the same assembly are loaded, which is a mixed bundle. " + BundleRemediation,
                    new JObject { ["ambiguousAssemblies"] = new JArray(ambiguousAssemblies) });
            if (auditedBuild != null) return null;

            var loaded = new JObject();
            foreach (var pair in (loadedHashes ?? new Dictionary<string, string>()).OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                string version = null;
                fileVersions?.TryGetValue(pair.Key, out version);
                loaded[pair.Key] = new JObject
                {
                    ["sha256Prefix"] = pair.Value != null && pair.Value.Length >= 12 ? pair.Value.Substring(0, 12) : pair.Value,
                    ["fileVersion"] = version
                };
            }
            return new EventsIsolationFinding(
                "SdkBundleNotAudited", BundleLayer,
                "Unverified SDK implementation bundle; isolated Events save requires a complete audited SDK bundle (evidence.auditedBundles lists them).",
                BundleRemediation,
                new JObject
                {
                    ["loadedBundle"] = loaded,
                    ["auditedBundles"] = new JArray((auditedBundleIds ?? Array.Empty<string>()).ToArray())
                });
        }

        internal static EventsIsolationFinding BundleAuditFailed(string message)
            => new EventsIsolationFinding(
                "SdkBundleAuditFailed", BundleLayer,
                string.IsNullOrWhiteSpace(message) ? "The complete SDK bundle audit failed." : IsolatedEventsSaveDiagnostics.Sanitize(message),
                BundleRemediation);

        internal static EventsIsolationFinding BundleScopeNotCovered(string message)
            => new EventsIsolationFinding(
                "PatternInstancesNotVerified", BundleLayer,
                string.IsNullOrWhiteSpace(message) ? "The audited bundle is not verified for this Knowledge Base." : IsolatedEventsSaveDiagnostics.Sanitize(message),
                ScopeRemediation);

        internal static EventsIsolationFinding UnverifiedParts(string message)
            => new EventsIsolationFinding(
                "UnverifiedObjectPart", ObjectLayer, message,
                "Every part of the object must be one of the part types audited for the Events save; an unknown or extension part may run its own save handlers.");

        internal static EventsIsolationFinding UnverifiedCallback(string message)
            => new EventsIsolationFinding(
                "UnverifiedSdkCallback", CallbackLayer, IsolatedEventsSaveDiagnostics.Sanitize(message),
                "A callback from an assembly outside the audited bundle is attached directly to the object or one of its parts; it is never invoked or suppressed by the MCP.");

        /// <summary>
        /// Any other preflight failure is named, never swallowed. The message goes through the same sanitizer as the
        /// save diagnostics (local paths, connection parts and tokens are withheld), because an I/O failure while
        /// hashing an SDK file would otherwise carry a path into the response.
        /// </summary>
        internal static EventsIsolationFinding Unclassified(Exception ex, string layer = PreflightLayer)
            => new EventsIsolationFinding(
                "IsolationPreflightFailed", layer,
                string.IsNullOrWhiteSpace(ex?.Message) ? "The isolation preflight failed." : IsolatedEventsSaveDiagnostics.Sanitize(ex.Message),
                "The preflight could not complete; nothing was written. Inspect the Worker log for this request (the response's correlationId, when it carries one) before retrying.",
                new JObject { ["exceptionType"] = ex?.GetType().Name });

        /// <summary>The KB version could not be read, so the destination cannot be confirmed (fail closed).</summary>
        internal static EventsIsolationFinding DestinationUnreadable(Exception ex)
            => new EventsIsolationFinding(
                "WriteDestinationUnreadable", DestinationLayer,
                "The active KB version could not be read, so the configured write destination could not be confirmed.",
                "Check that the KB is open and its versions are listable (genexus_kb_version action=list); nothing was written.",
                new JObject { ["exceptionType"] = ex?.GetType().Name });

        private static bool IsBlank(string value) => string.IsNullOrWhiteSpace(value);

        /// <summary>
        /// Evaluates every independent prerequisite of the isolated save, in the order the checks run, and never throws:
        /// a failure in one phase becomes a finding and the remaining phases still run, so the first fix is not how the
        /// next blocker is discovered and an unexpected exception can no longer hide the pins finding that the parent
        /// code would have reported first. Everything that touches the SDK, the environment or the file system comes in
        /// through <paramref name="probe"/>, which is what makes each branch testable without a Knowledge Base.
        /// </summary>
        internal static IsolationEvaluation Evaluate(IsolationProbe probe)
        {
            var result = new IsolationEvaluation();

            // 1. Destination: the pins, and what the SDK reports about the opened KB.
            IsolationDestination destination = new IsolationDestination();
            Exception destinationError = null;
            try { destination = probe.ReadDestination() ?? destination; }
            catch (Exception ex) { destinationError = ex; }
            result.Destination = destination;
            bool pinsComplete = !IsBlank(probe.ExpectedPath) && !IsBlank(probe.ExpectedVersion);
            if (destinationError != null && pinsComplete)
                result.Findings.Add(DestinationUnreadable(destinationError));
            else
                // With a pin missing the verdict does not depend on the actual values.
                result.Findings.AddRange(EvaluateDestination(
                    probe.ExpectedPath, probe.ExpectedVersion, destination.Path, destination.VersionName, destination.Frozen));

            // 2. The loaded SDK/WorkWithPlus bundle.
            try
            {
                var hashes = new Dictionary<string, string>();
                var versions = new Dictionary<string, string>();
                var missing = new List<string>();
                var ambiguous = new List<string>();
                foreach (string name in probe.RequiredAssemblies)
                {
                    IReadOnlyList<string> locations = probe.LoadedLocations(name);
                    // WorkWithPlus is loaded only when the target has that package installed/attached. If loaded, its
                    // exact bytes are still mandatory; an unknown loaded implementation is never trusted.
                    if (locations.Count == 0)
                    {
                        if (name != probe.OptionalAssembly) missing.Add(name);
                        continue;
                    }
                    if (locations.Count > 1) { ambiguous.Add(name); continue; }
                    hashes[name] = probe.HashFile(locations[0]);
                    versions[name] = probe.FileVersion(locations[0]);
                }
                // An incomplete or ambiguous bundle is neither audited as a whole nor matched against the audited ones: a
                // partial set is reported as what is missing, never as a near match.
                EventsIsolationFinding bundleFinding = null;
                bool complete = missing.Count == 0 && ambiguous.Count == 0;
                var triggers = probe.CompleteBundleTriggerHashes;
                // A trigger is the WorkWithPlus of a bundle audited with it, or the SDK core of one audited without it.
                if (complete && triggers != null && hashes.Values.Any(triggers.Contains))
                {
                    try { result.BundleAudit = probe.VerifyCompleteBundle(hashes); }
                    catch (EventsBundleScopeException ex) { bundleFinding = BundleScopeNotCovered(ex.Message); }
                    catch (InvalidOperationException ex) { bundleFinding = BundleAuditFailed(ex.Message); }
                    catch (Exception ex)
                    {
                        bundleFinding = new EventsIsolationFinding(
                            "IsolationPreflightFailed", BundleLayer,
                            "SDK bundle verification failed (" + ex.GetType().Name + "): " + IsolatedEventsSaveDiagnostics.Sanitize(ex.Message),
                            BundleRemediation);
                    }
                }
                result.AuditedBuild = complete && bundleFinding == null ? probe.MatchAuditedBuild(hashes) : null;
                bundleFinding = bundleFinding ?? EvaluateBundle(
                    hashes, versions, missing, ambiguous, result.AuditedBuild, probe.AuditedBundleIds);
                if (bundleFinding != null) result.Findings.Add(bundleFinding);
            }
            catch (Exception ex) { result.Findings.Add(Unclassified(ex, BundleLayer)); }

            // 3. The object's parts.
            try
            {
                IReadOnlyList<Type> types = probe.PartTypes();
                result.PartTypes = types;
                var unverified = types.Where(t => !probe.IsAllowedPart(t)).Select(t => t.FullName).ToArray();
                if (unverified.Length > 0)
                    result.Findings.Add(UnverifiedParts("The object contains an unverified SDK or extension part: " + string.Join(", ", unverified)));
                else if (!types.Contains(probe.RequiredPart))
                    result.Findings.Add(UnverifiedParts("The object has no Events part, so there is no Events source to save."));
            }
            catch (Exception ex) { result.Findings.Add(Unclassified(ex, ObjectLayer)); }

            // 4. Direct callbacks attached to the object or its parts (read, never invoked).
            try { probe.InspectCallbacks(); }
            catch (InvalidOperationException ex) { result.Findings.Add(UnverifiedCallback(ex.Message)); }
            catch (Exception ex) { result.Findings.Add(Unclassified(ex, CallbackLayer)); }

            return result;
        }
    }

    /// <summary>What the SDK says about the opened destination.</summary>
    internal sealed class IsolationDestination
    {
        internal string Path { get; set; }
        internal string VersionName { get; set; }
        internal bool? Frozen { get; set; }
    }

    /// <summary>
    /// The inputs of <see cref="EventsIsolationFindings.Evaluate"/>. In production each member is bound to the SDK, the
    /// process environment or the file system by <c>EventsSaveIsolation.CheckTarget</c>; tests bind them to plain fakes.
    /// </summary>
    internal sealed class IsolationProbe
    {
        internal string ExpectedPath { get; set; }
        internal string ExpectedVersion { get; set; }
        internal Func<IsolationDestination> ReadDestination { get; set; }
        internal IReadOnlyList<string> RequiredAssemblies { get; set; }
        internal string OptionalAssembly { get; set; }
        internal Func<string, IReadOnlyList<string>> LoadedLocations { get; set; }
        internal Func<string, string> HashFile { get; set; }
        internal Func<string, string> FileVersion { get; set; }
        /// <summary>The hashes of the optional assembly that make a complete-bundle audit mandatory; null when there is none.</summary>
        internal IReadOnlyCollection<string> CompleteBundleTriggerHashes { get; set; }
        internal Func<IDictionary<string, string>, JObject> VerifyCompleteBundle { get; set; }
        internal Func<IDictionary<string, string>, string> MatchAuditedBuild { get; set; }
        internal IEnumerable<string> AuditedBundleIds { get; set; }
        internal Func<IReadOnlyList<Type>> PartTypes { get; set; }
        internal Func<Type, bool> IsAllowedPart { get; set; }
        internal Type RequiredPart { get; set; }
        internal Action InspectCallbacks { get; set; }
    }

    /// <summary>The findings in the order the checks run, and the facts the eligible report needs.</summary>
    internal sealed class IsolationEvaluation
    {
        internal List<EventsIsolationFinding> Findings { get; } = new List<EventsIsolationFinding>();
        internal IsolationDestination Destination { get; set; } = new IsolationDestination();
        internal string AuditedBuild { get; set; }
        internal JObject BundleAudit { get; set; }
        internal IReadOnlyList<Type> PartTypes { get; set; } = Array.Empty<Type>();
    }
}
