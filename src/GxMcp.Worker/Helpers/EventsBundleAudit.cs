using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Artech.Architecture.Common.Objects;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Helpers
{
    // One audited SDK/WorkWithPlus bundle. The immutable audit input travels with the Worker as an embedded manifest;
    // editing its distributed copy cannot authorize an unknown SDK or a pending production save. A bundle is audited as a
    // whole: every installed assembly of the manifest must match its hash and every loaded SDK assembly must be in it.
    internal sealed class EventsBundleAudit
    {
        // The assembly whose exact bytes decide which manifest, if any, applies to a bundle that includes WorkWithPlus.
        internal const string TriggerAssembly = "DVelop.Patterns.WorkWithPlus";

        // A bundle audited without WorkWithPlus is selected by the exact bytes of the SDK core instead.
        internal const string CoreAssembly = "Artech.Architecture.Common";

        // The one scope a native verification may declare: its proof holds only for a Knowledge Base that has no pattern
        // instance (WorkWithPlus or any other pattern), so the save hooks of the pattern packages have nothing to apply.
        internal const string ScopeKbWithoutPatternInstances = "kb-without-pattern-instances";

        private readonly byte[] _manifestBytes;
        private readonly JObject _manifest;
        private readonly Dictionary<string, string> _criticalHashes;

        internal EventsBundleAudit(string manifestName, byte[] manifestBytes)
        {
            ManifestName = manifestName ?? throw new ArgumentNullException(nameof(manifestName));
            _manifestBytes = manifestBytes ?? throw new ArgumentNullException(nameof(manifestBytes));
            _manifest = JObject.Parse(Encoding.UTF8.GetString(_manifestBytes));
            BundleId = (string)_manifest["bundleId"];
            if (string.IsNullOrWhiteSpace(BundleId))
                throw new InvalidOperationException("Events audit manifest has no bundle id: " + manifestName);
            var critical = ((JArray)_manifest["criticalAssemblies"]).Values<string>().ToArray();
            _criticalHashes = ((JArray)_manifest["assemblies"])
                .Where(a => critical.Contains((string)a["name"]))
                .ToDictionary(a => (string)a["name"], a => (string)a["sha256"]);
        }

        internal static EventsBundleAudit FromEmbedded(string manifestName)
        {
            using (var stream = typeof(EventsBundleAudit).Assembly.GetManifestResourceStream(manifestName)
                ?? throw new InvalidOperationException("Embedded Events audit manifest unavailable: " + manifestName))
            using (var buffer = new MemoryStream())
            {
                stream.CopyTo(buffer);
                return new EventsBundleAudit(manifestName, buffer.ToArray());
            }
        }

        internal string ManifestName { get; }
        internal string BundleId { get; }
        internal JObject NativeVerification => (JObject)_manifest["nativeVerification"];

        // A copy: callers add and compare entries without touching the audit input.
        internal Dictionary<string, string> CriticalHashes => new Dictionary<string, string>(_criticalHashes);

        internal bool IncludesWorkWithPlus => _criticalHashes.ContainsKey(TriggerAssembly);

        // The assembly, and its audited hash, that make the complete audit of this manifest mandatory.
        internal string Trigger => IncludesWorkWithPlus ? TriggerAssembly : CoreAssembly;
        internal string TriggerHash => _criticalHashes[Trigger];

        // The exact bytes of the SDK core name one build of the SDK. A WorkWithPlus release is installed on more than one
        // build of it, so the WorkWithPlus alone cannot tell two bundles apart; the core can, and no two bundles that carry
        // the same WorkWithPlus share it (one build of the SDK may be audited with more than one WorkWithPlus release).
        internal string CoreHash => _criticalHashes[CoreAssembly];

        internal static bool MaySaveTarget(JObject verification, string name)
            => (string)verification?["status"] == "verified"
                || ((string)verification?["status"] == "pending"
                    && !string.IsNullOrWhiteSpace(name)
                    && string.Equals((string)verification["syntheticObject"], name, StringComparison.Ordinal));

        // The scope a native verification declares: the condition on the Knowledge Base under which its proof holds. Absent means
        // the proof covers the scenario it exercised wherever the bundle is loaded (the U16 manifest); an unknown scope is refused.
        internal static string ScopeOf(JObject verification)
        {
            JToken token = verification?["scope"];
            if (token == null || token.Type == JTokenType.Null) return null;
            string scope = token.Type == JTokenType.String ? (string)token : null;
            if (scope != ScopeKbWithoutPatternInstances)
                throw new InvalidOperationException("Unknown native verification scope: " + token.ToString(Formatting.None));
            return scope;
        }

        internal static void RequireExactHash(string expected, string actual, string name)
        {
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
                throw new InvalidOperationException("Unverified Events SDK bundle assembly: " + name);
        }

        // sdkRoot, baseDirectory and loadedAssemblies default to the real Worker process; they are parameters so each
        // refusal can be exercised on a temporary directory instead of only against an installed SDK.
        // countPatternInstances is asked only by a bundle whose native verification is scoped to Knowledge Bases without pattern
        // instances, and only after the bundle itself verified (it walks the whole model); it is null in the tests that do not need it.
        internal JObject Verify(string objectName, IDictionary<string, string> hashes,
            string sdkRoot = null, string baseDirectory = null, IEnumerable<Assembly> loadedAssemblies = null,
            Func<int> countPatternInstances = null)
        {
            if (!MaySaveTarget(NativeVerification, objectName))
                throw new InvalidOperationException("Complete " + BundleId
                    + " bundle native verification is pending; only its authorized synthetic object may be saved.");
            string scope = ScopeOf(NativeVerification);
            var distributed = Path.Combine(baseDirectory ?? AppDomain.CurrentDomain.BaseDirectory, ManifestName);
            // A missing or unreadable copy beside the Worker is a refusal of its own, not a preflight crash.
            string distributedHash;
            try { distributedHash = FileHash(distributed); }
            catch (IOException) { throw new InvalidOperationException("Unverified Events SDK bundle assembly: distributed audit manifest"); }
            catch (UnauthorizedAccessException) { throw new InvalidOperationException("Unverified Events SDK bundle assembly: distributed audit manifest"); }
            RequireExactHash(Hash(_manifestBytes), distributedHash, "distributed audit manifest");
            string root = sdkRoot ?? Path.GetDirectoryName(typeof(KBObject).Assembly.Location);
            string rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var entries = ((JArray)_manifest["assemblies"]).ToDictionary(a => (string)a["relativePath"], StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries.Values)
            {
                string path = Path.GetFullPath(Path.Combine(root, (string)entry["relativePath"]));
                if (!path.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Invalid relative Events SDK audit path.");
                string hash = FileHash(path);
                RequireExactHash((string)entry["sha256"], hash, (string)entry["name"]);
                if (_criticalHashes.ContainsKey((string)entry["name"])) hashes[(string)entry["name"]] = hash;
            }
            int loaded = 0;
            var unknown = new List<string>();
            foreach (Assembly assembly in loadedAssemblies ?? AppDomain.CurrentDomain.GetAssemblies())
            {
                string name = assembly.GetName().Name;
                if (!name.StartsWith("Artech.", StringComparison.OrdinalIgnoreCase)
                    && !name.StartsWith("GeneXus.", StringComparison.OrdinalIgnoreCase)
                    && !name.StartsWith("DVelop.", StringComparison.OrdinalIgnoreCase)) continue;
                string loadedPath = Path.GetFullPath(assembly.Location);
                if (!loadedPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)
                    || !entries.TryGetValue(loadedPath.Substring(rootPrefix.Length).Replace('\\', '/'), out JToken entry)
                    || !string.Equals((string)entry["name"], name, StringComparison.OrdinalIgnoreCase))
                {
                    unknown.Add(name);
                    continue;
                }
                RequireExactHash((string)entry["sha256"], FileHash(assembly.Location), name);
                loaded++;
            }
            // Every unknown loaded assembly is named at once, so one run shows the whole gap; the save is still refused.
            if (unknown.Count > 0)
                throw new InvalidOperationException("Unknown loaded Events SDK implementation: " + string.Join(", ", unknown));
            int? patternInstances = scope == ScopeKbWithoutPatternInstances ? RequireNoPatternInstances(countPatternInstances) : (int?)null;
            var report = new JObject
            {
                ["manifestSha256"] = Hash(_manifestBytes), ["installedAssembliesVerified"] = entries.Count,
                ["loadedAssembliesVerified"] = loaded, ["bundleId"] = BundleId,
                ["nativeVerification"] = NativeVerification.DeepClone()
            };
            if (patternInstances.HasValue) report["patternInstanceCount"] = patternInstances.Value;
            return report;
        }

        // Fail closed: a Knowledge Base whose pattern instances cannot be counted is treated like one that has them.
        private int RequireNoPatternInstances(Func<int> count)
        {
            int found;
            try { found = (count ?? throw new InvalidOperationException("No pattern instance counter was supplied.")).Invoke(); }
            catch (Exception ex) { throw Uncountable(ex); }
            if (found != 0)
                throw new EventsBundleScopeException("The " + BundleId + " bundle is verified only for Knowledge Bases without pattern instances;"
                    + " this Knowledge Base has " + found + ".");
            return found;
        }

        private EventsBundleScopeException Uncountable(Exception ex)
            => new EventsBundleScopeException("The " + BundleId + " bundle is verified only for Knowledge Bases without pattern instances,"
                + " and the instances of this one could not be counted (" + ex.GetType().Name + ").");

        private static string FileHash(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var sha = SHA256.Create()) return Hex(sha.ComputeHash(stream));
        }

        private static string Hash(byte[] bytes)
        {
            using (var sha = SHA256.Create()) return Hex(sha.ComputeHash(bytes));
        }

        private static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
    }

    // Every audited bundle the Worker knows. A new bundle is added here only together with its manifest, and the manifest
    // authorizes business objects only when its native verification is `verified` with the digest of a real proof.
    internal static class EventsBundleAudits
    {
        // GeneXus 18 U16 builds with WorkWithPlus 16.2.0.8386. One WorkWithPlus release is installed on every build of the SDK, byte for
        // byte, so each build is a bundle of its own, audited as a whole: one manifest per build, and a build with no manifest is refused.
        // A later WorkWithPlus release on an audited build is a bundle of its own as well (the build with that release): it has a manifest and
        // a native proof of its own, and the registry tells it from the earlier release by the exact bytes of the WorkWithPlus.
        internal static readonly EventsBundleAudit U16Build189158 = EventsBundleAudit.FromEmbedded("events-u16-wwp16.2-189158-audit.json");
        internal static readonly EventsBundleAudit U16Build189513 = EventsBundleAudit.FromEmbedded("events-u16-wwp16.2-189513-audit.json");
        internal static readonly EventsBundleAudit U16Build189552 = EventsBundleAudit.FromEmbedded("events-u16-wwp16.2-189552-audit.json");
        // GeneXus 18 U16 build 189595: the first audited one; its manifest keeps the name it was created with.
        internal static readonly EventsBundleAudit U16 = EventsBundleAudit.FromEmbedded("events-u16-wwp16.2-audit.json");
        internal static readonly EventsBundleAudit U16Build189647 = EventsBundleAudit.FromEmbedded("events-u16-wwp16.2-189647-audit.json");
        // GeneXus 18 U16 build 189647 with WorkWithPlus 16.2.1.8463, the release that replaced 16.2.0.8386 on that build.
        internal static readonly EventsBundleAudit U16Build189647Wwp1621 = EventsBundleAudit.FromEmbedded("events-u16-wwp16.2.1-189647-audit.json");
        internal static readonly EventsBundleAudit U11 = EventsBundleAudit.FromEmbedded("events-u11-wwp16.1-audit.json");
        internal static readonly EventsBundleAudit U10 = EventsBundleAudit.FromEmbedded("events-u10-audit.json");

        internal static IReadOnlyList<EventsBundleAudit> All { get; } = new[]
        {
            U16Build189158, U16Build189513, U16Build189552, U16, U16Build189647, U16Build189647Wwp1621, U11, U10
        };

        // The hashes that make the complete-bundle audit mandatory: the WorkWithPlus of a bundle that includes it, the SDK
        // core of one audited without it. Two bundles that carry the same WorkWithPlus share its hash.
        internal static IReadOnlyCollection<string> TriggerHashes
            => All.Select(a => a.TriggerHash).Distinct(StringComparer.Ordinal).ToArray();

        internal static IReadOnlyCollection<string> CriticalNames
            => All.SelectMany(a => a.CriticalHashes.Keys).Distinct(StringComparer.Ordinal).ToArray();

        // Picks the manifest by the exact bytes of the loaded WorkWithPlus and, because one WorkWithPlus release is installed on
        // more than one build of the SDK, by the exact bytes of the SDK core among the bundles that carry that WorkWithPlus; a
        // core that none of them audited selects nothing. Without WorkWithPlus, by the exact bytes of the SDK core among the
        // bundles audited without it. A loaded WorkWithPlus never selects a bundle audited without it. Selecting is never
        // authorizing: the chosen manifest still verifies every file it lists.
        internal static EventsBundleAudit Select(IDictionary<string, string> hashes)
        {
            string wwp = null;
            if (hashes != null && hashes.TryGetValue(EventsBundleAudit.TriggerAssembly, out wwp))
            {
                var sameWorkWithPlus = All.Where(a => a.IncludesWorkWithPlus && string.Equals(a.TriggerHash, wwp, StringComparison.Ordinal)).ToArray();
                if (sameWorkWithPlus.Length == 0) throw new InvalidOperationException("No complete bundle audit covers the loaded WorkWithPlus.");
                hashes.TryGetValue(EventsBundleAudit.CoreAssembly, out string loadedCore);
                return sameWorkWithPlus.FirstOrDefault(a => string.Equals(a.CoreHash, loadedCore, StringComparison.Ordinal))
                    ?? throw new InvalidOperationException("No complete bundle audit covers the loaded SDK core with the loaded WorkWithPlus (audited with it: "
                        + string.Join(", ", sameWorkWithPlus.Select(a => a.BundleId)) + ").");
            }
            return All.FirstOrDefault(a => !a.IncludesWorkWithPlus && hashes != null
                    && hashes.TryGetValue(a.Trigger, out string core) && string.Equals(a.TriggerHash, core, StringComparison.Ordinal))
                ?? throw new InvalidOperationException("No complete bundle audit covers the loaded SDK without WorkWithPlus.");
        }

        internal static JObject Verify(string objectName, IDictionary<string, string> hashes, Func<int> countPatternInstances = null)
            => Select(hashes).Verify(objectName, hashes, countPatternInstances: countPatternInstances);

        // The id of the manifest whose every critical assembly matches; a mixture of two bundles matches none.
        internal static string MatchComplete(IDictionary<string, string> hashes)
            => hashes == null ? null : All.FirstOrDefault(a => a.CriticalHashes.All(entry =>
                hashes.TryGetValue(entry.Key, out string hash) && string.Equals(hash, entry.Value, StringComparison.Ordinal)))?.BundleId;
    }

    // The audited bundle is intact but its proof does not cover this Knowledge Base (it has pattern instances, or they could not
    // be counted). A refusal of its own, so the remediation says what was proven instead of asking for a new bundle audit.
    internal sealed class EventsBundleScopeException : InvalidOperationException
    {
        internal EventsBundleScopeException(string message) : base(message) { }
    }
}
