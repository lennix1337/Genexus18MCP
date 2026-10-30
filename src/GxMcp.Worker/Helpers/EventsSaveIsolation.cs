using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using Artech.Architecture.Common.Objects;
using Artech.Genexus.Common.Objects;
using Artech.Genexus.Common.Parts;
using Artech.Packages.Patterns.Objects;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Helpers
{
    internal static class EventsSaveIsolation
    {
        // Only the inspected U16 implementation is eligible. Broker suppression
        // cannot disable virtual methods or native Entity.BeforeSave delegates.
        private static readonly Dictionary<string, string> AuditedAssemblies = new Dictionary<string, string>
        {
            ["Artech.Architecture.Common"] = "5de4b9fa160b2aca8fa08be8e070870c00286415d7ecdfad816aa5bdc406f283",
            ["Artech.Genexus.Common"] = "9e1faca2f240141794c6627f22e71e4d8acd8f1a8a9daaea3b2d4254515e95f0",
            ["Artech.Udm.Framework"] = "ba47b86818aff828a5172b44c0ea83353b4a565fec076105480a995535bc9251",
            ["Artech.Common.Properties"] = "615ca8e522142748feb4a467081efdcd57d962ad71382dc2d77e2639f91a990a",
            ["Artech.Packages.Patterns"] = "36f99d7d76c3c7b54dded1249d09a02234834c9d38483c68afc9d79bd8538ace",
            ["DVelop.Patterns.WorkWithPlus"] = "16e15bcb8eaef779abd5b8ef9d33c724f58406979496612a253473688b52fb02"
        };
        private static readonly Dictionary<string, string> UpdatedU16Assemblies = new Dictionary<string, string>
        {
            ["Artech.Architecture.Common"] = "978fab2afad4daaa28aecffd5cd327367b1bb9c0a8f027cfda74911a294eac12",
            ["Artech.Genexus.Common"] = "cb7676857b75b817f5717edb37c4e19fbc06b2bb07feed10e64d74cb9bb159da",
            ["Artech.Udm.Framework"] = "9ba54b1a27a320c10cefab53a204b9e9ed9b027f6f025b7e0122224b5a71e86c",
            ["Artech.Common.Properties"] = "89cf957262dd5eee40d76b281abc5bb978881bb99959578d11a912fc7c069ae6",
            ["Artech.Packages.Patterns"] = "1f5baabe79d15279f0e1759053b512cc880cf695553459dd860a01120896aba8",
            ["DVelop.Patterns.WorkWithPlus"] = "16e15bcb8eaef779abd5b8ef9d33c724f58406979496612a253473688b52fb02"
        };
        private static readonly HashSet<Type> Parts = new HashSet<Type>
        {
            typeof(EventsPart), typeof(WebFormPart), typeof(VariablesPart), typeof(RulesPart),
            typeof(ConditionsPart), typeof(HelpPart), typeof(DocumentationPart),
            typeof(PatternVirtualPart)
        };

        internal static JObject Preflight(KBObject obj)
        {
            // Both checks share one count: it walks the whole model, and only a bundle scoped to Knowledge Bases without pattern
            // instances asks for it. The lambda reads obj only when asked, after CheckTarget has refused a null target.
            var count = Memoize(() => CountPatternInstances(obj.KB));
            CheckTarget(obj, count);
            var fresh = Fresh(obj.KB, obj.Guid);
            var report = CheckTarget(fresh, count);
            report["brokers"] = SdkEventSuppressionScope.VerifyBroker(fresh.KB);
            report["freshInstanceVerified"] = true;
            report["verified"] = true;
            return report;
        }

        // The ids MatchAuditedBuild can return. A refusal lists them so the bundle that is loaded can be
        // compared with the ones that were audited, without a version-number allowlist.
        internal static IEnumerable<string> AuditedBundleIds
            => new[] { "18.0.16.189550", "18.0.16.189595" }.Concat(EventsBundleAudits.All.Select(audit => audit.BundleId));

        // The pattern instances of a model: the objects the save hooks of the pattern packages apply anything to. A bundle whose proof
        // is scoped to Knowledge Bases without them is valid only while this is zero; the whole model is counted, never a sample.
        internal static int CountPatternInstances(KnowledgeBase kb)
            => CountPatternInstances(kb.DesignModel.Objects.Cast<KBObject>());

        // An instance is recognized by its class and, independently, by its type name: either is enough, so a model whose objects
        // materialize as another class (or a type an SDK renamed) is still counted and never trusted to have none.
        internal static int CountPatternInstances(IEnumerable<KBObject> objects, Func<KBObject, string> typeName = null)
        {
            typeName = typeName ?? (o => o?.TypeDescriptor?.Name);
            return (objects ?? Enumerable.Empty<KBObject>())
                .Count(o => o != null && (o is PatternInstance || string.Equals(typeName(o), PatternInstanceTypeName, StringComparison.OrdinalIgnoreCase)));
        }

        private const string PatternInstanceTypeName = "PatternInstance";

        private static Func<int> Memoize(Func<int> compute)
        {
            bool done = false;
            int value = 0;
            return () =>
            {
                if (!done) { value = compute(); done = true; }
                return value;
            };
        }

        internal static JObject CheckTarget(KBObject obj, Func<int> patternInstanceCount = null)
        {
            if (obj == null || obj.GetType() != typeof(WebPanel))
                throw new EventsIsolationRefusalException(EventsIsolationFindings.TargetTypeUnsupported(obj?.GetType().FullName));
            var kb = obj.KB;
            var countInstances = patternInstanceCount ?? (() => CountPatternInstances(kb));
            var callbacks = new JArray();
            // Independent prerequisites are all evaluated before refusing, each phase guarded, so fixing the pins is not
            // how the next blocker (the SDK bundle) is first discovered and an I/O failure cannot hide the pins finding.
            // The SDK, the environment and the file system enter only through the probe.
            var evaluation = EventsIsolationFindings.Evaluate(new IsolationProbe
            {
                ExpectedPath = Environment.GetEnvironmentVariable(WriteDestinationGuard.PathVariable),
                ExpectedVersion = Environment.GetEnvironmentVariable(WriteDestinationGuard.VersionVariable),
                ReadDestination = () =>
                {
                    var active = KBVersion.GetActive(kb);
                    return new IsolationDestination
                    {
                        Path = kb.Location, VersionName = active?.Name, Frozen = active == null ? (bool?)null : active.IsFrozen
                    };
                },
                RequiredAssemblies = AuditedAssemblies.Keys.ToArray(),
                OptionalAssembly = "DVelop.Patterns.WorkWithPlus",
                LoadedLocations = name => AppDomain.CurrentDomain.GetAssemblies()
                    .Where(a => a.GetName().Name == name).Select(a => a.Location).ToArray(),
                HashFile = path => Hash(File.ReadAllBytes(path)),
                FileVersion = FileVersion,
                CompleteBundleTriggerHashes = EventsBundleAudits.TriggerHashes,
                VerifyCompleteBundle = hashes => EventsBundleAudits.Verify(obj.Name, hashes, countInstances),
                MatchAuditedBuild = MatchAuditedBuild,
                AuditedBundleIds = AuditedBundleIds,
                PartTypes = () => obj.Parts.Cast<KBObjectPart>().Select(p => p.GetType()).ToArray(),
                IsAllowedPart = Parts.Contains,
                RequiredPart = typeof(EventsPart),
                InspectCallbacks = () =>
                {
                    InspectDirectCallbacks(obj, callbacks);
                    foreach (KBObjectPart part in obj.Parts) InspectDirectCallbacks(part, callbacks);
                }
            });
            if (evaluation.Findings.Count > 0) throw new EventsIsolationRefusalException(evaluation.Findings);
            var version = evaluation.Destination;
            var partTypes = evaluation.PartTypes;
            string auditedBuild = evaluation.AuditedBuild;
            JObject report = new JObject();
            report["directCallbacks"] = callbacks;
            report["sdkFingerprintVerified"] = true;
            report["auditedSdkBuild"] = auditedBuild;
            if (evaluation.BundleAudit != null) report["completeBundleAudit"] = evaluation.BundleAudit;
            report["destinationPinsVerified"] = true;
            AppendPinEvidence(report);
            report["effectiveVersion"] = version.VersionName;
            report["effectiveKbIdentity"] = ContentHash(Path.GetFullPath(kb.Location).TrimEnd('\\', '/').ToUpperInvariant());
            report["objectGuid"] = obj.Guid.ToString();
            report["parts"] = new JArray(partTypes.Select(p => p.FullName));
            report["patternPart"] = partTypes.Contains(typeof(PatternVirtualPart));
            report["skipApplyPatternSupported"] = true;
            report["wwpAssemblyLoaded"] = AppDomain.CurrentDomain.GetAssemblies()
                .Any(a => a.GetName().Name == "DVelop.Patterns.WorkWithPlus");
            report["scope"] = "audited-u16-webpanel-with-pattern-save-isolation";
            return report;
        }

        internal static bool IsAllowedPartType(Type type) => Parts.Contains(type);
        internal static string MatchAuditedBuild(IDictionary<string, string> hashes)
        {
            // The extra critical assemblies are added to the hashes only by a complete-bundle verification, so their
            // presence selects the complete path; a mixture of two audited bundles matches none of them.
            if (hashes != null && EventsBundleAudits.CriticalNames.Except(AuditedAssemblies.Keys).Any(hashes.ContainsKey))
                return EventsBundleAudits.MatchComplete(hashes);
            if (Matches(AuditedAssemblies, hashes)) return "18.0.16.189550";
            if (Matches(UpdatedU16Assemblies, hashes)) return "18.0.16.189595";
            return null;
        }

        private static bool Matches(Dictionary<string, string> expected, IDictionary<string, string> actual)
            => actual != null && expected.All(entry =>
                entry.Key == "DVelop.Patterns.WorkWithPlus" && !actual.ContainsKey(entry.Key)
                || actual.TryGetValue(entry.Key, out string hash) && string.Equals(hash, entry.Value, StringComparison.Ordinal));

        internal static JObject DescribeConfiguredPins()
        {
            var report = WriteDestinationGuard.DescribePins(
                Environment.GetEnvironmentVariable(WriteDestinationGuard.PathVariable),
                Environment.GetEnvironmentVariable(WriteDestinationGuard.VersionVariable));
            report["phase"] = "isolation-preflight";
            return report;
        }

        // The pin state the Worker actually has, in the shape a refusal reports it. An eligible preview carries it too,
        // so that "the pins are applied" is something the report shows (state of each pin variable, identity hash and
        // native-config revision) and not only something inferred from the absence of a refusal.
        internal static void AppendPinEvidence(JObject report)
        {
            var pins = DescribeConfiguredPins();
            foreach (string key in new[] { "effectivePins", "profileRevision", "profileRevisionSource" })
                report[key] = pins[key];
        }

        // What a refused preflight reports, for the preview and for the real save alike. blocker and
        // reason keep their names and meaning (the first finding); reasonCode and blockers add every
        // independent finding with its remediation, so a client reading only status/code/blocker is unaffected.
        internal static JObject DescribeRefusal(Exception ex)
        {
            IReadOnlyList<EventsIsolationFinding> findings = (ex as EventsIsolationRefusalException)?.Findings;
            if (findings == null || findings.Count == 0) findings = new[] { EventsIsolationFindings.Unclassified(ex) };
            var report = DescribeConfiguredPins();
            report["verified"] = false;
            report["blocker"] = "ObjectSaveIsolationUnverified";
            report["reason"] = findings[0].Message;
            report["reasonCode"] = findings[0].Code;
            report["blockers"] = new JArray(findings.Select(f => f.ToJson()));
            return report;
        }
        internal static bool IsAllowedPartTypeName(string fullName)
            => Parts.Any(type => type.FullName == fullName);

        internal static bool IsExpectedPatternProjection(ObjectMoveSnapshot.Comparison comparison)
        {
            // Part names alone are not evidence of a legitimate projection: the
            // same names can contain arbitrary authored changes. Until the SDK
            // exposes a canonical projection diff, fail closed and require the
            // complete snapshot to remain unchanged.
            return comparison == null || comparison.Equal;
        }

        internal static void InspectDirectCallbacks(object instance, JArray report)
        {
            for (Type type = instance.GetType(); type != null; type = type.BaseType)
            {
                foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly))
                {
                    if (!typeof(Delegate).IsAssignableFrom(field.FieldType)) continue;
                    var value = field.GetValue(instance) as Delegate;
                    if (value == null) continue;
                    foreach (var callback in value.GetInvocationList())
                    {
                        string declaringAssembly = callback.Method.DeclaringType?.Assembly.GetName().Name;
                        string targetAssembly = callback.Target?.GetType().Assembly.GetName().Name;
                        string handler = callback.Method.DeclaringType?.FullName + "." + callback.Method.Name;
                        if (declaringAssembly == null || !AuditedAssemblies.ContainsKey(declaringAssembly)
                            || (targetAssembly != null && !AuditedAssemblies.ContainsKey(targetAssembly)))
                            throw new InvalidOperationException("An unverified direct SDK callback is attached to "
                                + instance.GetType().FullName + ": " + handler + " (target assembly: " + targetAssembly + ").");
                        report.Add(new JObject { ["ownerType"] = instance.GetType().FullName, ["handler"] = handler });
                    }
                }
            }
        }

        internal static KBObject Fresh(KnowledgeBase kb, Guid guid)
        {
            var seed = kb.DesignModel.Objects.Get(guid) ?? throw new InvalidOperationException("Object not found by GUID.");
            var cache = kb.DesignModel.Objects as IKBModelObjectsCacheConfiguration
                ?? throw new InvalidOperationException("SDK public object-cache configuration is unavailable.");
            cache.Invalidate(seed);
            cache.RemoveFromCaches(seed);
            var fresh = kb.DesignModel.Objects.Get(seed.Key) ?? throw new InvalidOperationException("SDK fresh object lookup failed.");
            if (fresh.Guid != guid || ReferenceEquals(seed, fresh))
                throw new InvalidOperationException("SDK did not return a new instance of the requested GUID after cache removal.");
            return fresh;
        }

        internal static string Source(KBObject obj)
        {
            var part = GxMcp.Worker.Structure.PartAccessor.GetPart(obj, "Events") as ISource;
            return part?.Source ?? string.Empty;
        }

        internal static string Token(KBObject obj)
            => WriteService.ComputeContentVersionToken(obj, Source(obj));

        internal static string ContentHash(string value)
        {
            return SdkReflection.Sha256Hex(value);
        }

        internal static bool SourceEquivalent(string left, string right)
            => NormalizeSource(left) == NormalizeSource(right);

        private static string NormalizeSource(string value)
            => (value ?? string.Empty).Replace("\r\n", "\n").Replace("\r", "\n").TrimEnd('\n');

        internal static string Placement(KBObject obj) => obj.Parent?.Guid.ToString() ?? "root";

        internal static Dictionary<Guid, EventsInventoryEntry> Inventory(KnowledgeBase kb)
        {
            var ids = kb.DesignModel.Objects.Cast<KBObject>().Select(o => o.Guid).ToArray();
            var result = new Dictionary<Guid, EventsInventoryEntry>();
            foreach (Guid guid in ids)
            {
                var obj = Fresh(kb, guid);
                result.Add(guid, new EventsInventoryEntry(
                    obj.VersionId,
                    SdkTimestampNormalizer.NormalizeUtc(obj.LastUpdate).Ticks,
                    obj.Parent?.Guid.ToString(),
                    obj.Name,
                    Convert.ToString(obj.TypeDescriptor.Id, CultureInfo.InvariantCulture),
                    obj.TypeDescriptor.Name,
                    LastModifiedBy(obj)));
            }
            return result;
        }

        // The author is diagnostic only: it is read apart so that an object that cannot name one is still inventoried.
        private static string LastModifiedBy(KBObject obj)
        {
            try { return obj.UserName; }
            catch { return null; }
        }

        internal static JArray ChangedOthers(Dictionary<Guid, string> before, Dictionary<Guid, string> after, Guid target)
            => new JArray(before.Keys.Concat(after.Keys).Distinct().Where(g => g != target
                && (!before.ContainsKey(g) || !after.ContainsKey(g) || before[g] != after[g])).Select(g => g.ToString()));

        private static string Hash(byte[] value)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(value)).Replace("-", "").ToLowerInvariant();
        }

        // Diagnostic only: the assembly file version printed beside a hash prefix, never used to decide.
        private static string FileVersion(string path)
        {
            try { return System.Diagnostics.FileVersionInfo.GetVersionInfo(path).FileVersion; }
            catch { return null; }
        }
    }
}
