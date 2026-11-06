using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Artech.Architecture.Common.Objects;
using GxMcp.Worker.Helpers;
using Newtonsoft.Json.Linq;
using Xunit;
using Xunit.Abstractions;

namespace GxMcp.Worker.Tests
{
    // The audited-bundle registry and the complete-bundle verification, exercised on plain files: no Knowledge Base and no
    // installed SDK is needed to prove each refusal, so a regression fails here and not only against a live KB.
    public sealed class EventsBundleAuditTests : IDisposable
    {
        private static readonly string[] ExpectedCritical =
        {
            "Artech.Architecture.Common", "Artech.Genexus.Common", "Artech.Udm.Framework", "Artech.Common.Properties",
            "Artech.Packages.Patterns", "DVelop.Patterns.WorkWithPlus", "DVelop.Extensions.WWPPackagesCommon",
            "DVelop.Extensions.UserControls", "DVelop.Extensions.WWPPackages"
        };

        // A bundle audited without WorkWithPlus has the five SDK assemblies only.
        private static IEnumerable<string> ExpectedCriticalOf(EventsBundleAudit audit)
            => audit.IncludesWorkWithPlus ? ExpectedCritical : ExpectedCritical.Where(name => name.StartsWith("Artech.", StringComparison.Ordinal));

        private static readonly Regex Sha256Hex = new Regex("^[0-9a-f]{64}$", RegexOptions.Compiled);

        private readonly string _dir = Path.Combine(Path.GetTempPath(), "gxmcp-bundle-audit-" + Guid.NewGuid().ToString("N"));

        private readonly ITestOutputHelper _output;

        public EventsBundleAuditTests(ITestOutputHelper output)
        {
            _output = output;
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        }

        private static string Sha(string text)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "").ToLowerInvariant();
        }

        private static string FileSha(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        private sealed class Fixture
        {
            internal string Root, Base;
            internal EventsBundleAudit Audit;
            internal string OneFile => Path.Combine(Root, "Artech.Fake.One.dll");
            internal string DistributedManifest => Path.Combine(Base, "fake-audit.json");
        }

        // A tiny installation (two "assemblies"), its manifest and the distributed copy of that manifest.
        private Fixture NewFixture(string nativeVerificationJson, Action<JObject> tweak = null)
        {
            string id = Guid.NewGuid().ToString("N");
            var fixture = new Fixture { Root = Path.Combine(_dir, "sdk-" + id), Base = Path.Combine(_dir, "base-" + id) };
            Directory.CreateDirectory(Path.Combine(fixture.Root, "Packages"));
            Directory.CreateDirectory(fixture.Base);
            File.WriteAllText(fixture.OneFile, "one");
            File.WriteAllText(Path.Combine(fixture.Root, "Packages", "DVelop.Fake.Wwp.dll"), "wwp");
            var manifest = new JObject
            {
                ["schemaVersion"] = 1,
                ["bundleId"] = "fake+bundle",
                ["criticalAssemblies"] = new JArray("Artech.Fake.One", "DVelop.Fake.Wwp"),
                ["assemblies"] = new JArray(
                    new JObject { ["name"] = "Artech.Fake.One", ["relativePath"] = "Artech.Fake.One.dll", ["sha256"] = Sha("one") },
                    new JObject { ["name"] = "DVelop.Fake.Wwp", ["relativePath"] = "Packages/DVelop.Fake.Wwp.dll", ["sha256"] = Sha("wwp") }),
                ["nativeVerification"] = JObject.Parse(nativeVerificationJson)
            };
            tweak?.Invoke(manifest);
            byte[] bytes = Encoding.UTF8.GetBytes(manifest.ToString());
            File.WriteAllBytes(fixture.DistributedManifest, bytes);
            fixture.Audit = new EventsBundleAudit("fake-audit.json", bytes);
            return fixture;
        }

        private static JObject Verify(Fixture f, string objectName, IDictionary<string, string> hashes = null, IEnumerable<Assembly> loaded = null,
            Func<int> count = null)
            => f.Audit.Verify(objectName, hashes ?? new Dictionary<string, string>(), f.Root, f.Base, loaded ?? new Assembly[0], count);

        private const string Verified = "{status:'verified'}";
        private const string Pending = "{status:'pending',syntheticObject:'Synthetic01'}";
        private const string ScopedVerified = "{status:'verified',scope:'kb-without-pattern-instances'}";
        private const string ScopedPending = "{status:'pending',syntheticObject:'Synthetic01',scope:'kb-without-pattern-instances'}";

        [Fact]
        public void Verify_AcceptsAnInstallationWhoseEveryAssemblyMatches_AndReportsTheCriticalHashes()
        {
            var f = NewFixture(Verified);
            var hashes = new Dictionary<string, string>();

            var report = Verify(f, "AnyObject", hashes);

            Assert.Equal("fake+bundle", report["bundleId"].ToString());
            Assert.Equal(2, report["installedAssembliesVerified"].Value<int>());
            Assert.Equal(0, report["loadedAssembliesVerified"].Value<int>());
            Assert.Equal(Sha("one"), hashes["Artech.Fake.One"]);
            Assert.Equal(Sha("wwp"), hashes["DVelop.Fake.Wwp"]);
        }

        [Fact]
        public void Verify_RefusesAChangedInstalledAssembly_ByName()
        {
            var f = NewFixture(Verified);
            File.WriteAllText(f.OneFile, "changed");

            var ex = Assert.Throws<InvalidOperationException>(() => Verify(f, "AnyObject"));

            Assert.Equal("Unverified Events SDK bundle assembly: Artech.Fake.One", ex.Message);
        }

        [Fact]
        public void Verify_RefusesAMissingInstalledAssembly_NeverTreatsItAsOptional()
        {
            var f = NewFixture(Verified);
            File.Delete(f.OneFile);

            Assert.ThrowsAny<IOException>(() => Verify(f, "AnyObject"));
        }

        [Fact]
        public void Verify_RefusesADistributedManifestThatIsNotTheEmbeddedOne()
        {
            var f = NewFixture(Verified);
            File.WriteAllText(f.DistributedManifest, "{\"edited\":true}");

            var ex = Assert.Throws<InvalidOperationException>(() => Verify(f, "AnyObject"));

            Assert.Equal("Unverified Events SDK bundle assembly: distributed audit manifest", ex.Message);
        }

        [Fact]
        public void Verify_RefusesAMissingDistributedManifest_AsARefusalNotAPreflightCrash()
        {
            var f = NewFixture(Verified);
            File.Delete(f.DistributedManifest);

            var ex = Assert.Throws<InvalidOperationException>(() => Verify(f, "AnyObject"));

            Assert.Equal("Unverified Events SDK bundle assembly: distributed audit manifest", ex.Message);
        }

        [Theory]
        [InlineData("../outside.dll")]
        [InlineData("..\\outside.dll")]
        [InlineData("Packages/../../outside.dll")]
        public void Verify_RefusesAManifestPathThatLeavesTheInstallation(string relativePath)
        {
            var f = NewFixture(Verified, m => ((JObject)((JArray)m["assemblies"])[0])["relativePath"] = relativePath);

            var ex = Assert.Throws<InvalidOperationException>(() => Verify(f, "AnyObject"));

            Assert.Equal("Invalid relative Events SDK audit path.", ex.Message);
        }

        [Fact]
        public void Verify_RefusesALoadedSdkAssemblyThatTheManifestDoesNotList()
        {
            var f = NewFixture(Verified);
            var realSdkAssembly = typeof(KBObject).Assembly;   // loaded from the real installation, not from the fake root

            var ex = Assert.Throws<InvalidOperationException>(() => Verify(f, "AnyObject", null, new[] { realSdkAssembly }));

            Assert.StartsWith("Unknown loaded Events SDK implementation: Artech.", ex.Message);
        }

        [Fact]
        public void Verify_NamesEveryUnknownLoadedSdkAssemblyAtOnce_AndStillRefuses()
        {
            var f = NewFixture(Verified);
            // Two real SDK assemblies, loaded from the real installation and so listed by neither the fake root nor its manifest.
            var first = typeof(KBObject).Assembly;
            var second = typeof(Artech.Genexus.Common.Objects.WebPanel).Assembly;
            Assert.NotEqual(first.GetName().Name, second.GetName().Name);

            var ex = Assert.Throws<InvalidOperationException>(() => Verify(f, "AnyObject", null, new[] { first, second }));

            Assert.Equal("Unknown loaded Events SDK implementation: " + first.GetName().Name + ", " + second.GetName().Name, ex.Message);
        }

        [Fact]
        public void Verify_RefusesAListedPathWhoseNameIsNotTheLoadedAssemblyName()
        {
            // The entry points at the real installed file with its real hash, under another name: path and hash both pass,
            // so only the name check stops a manifest from vouching for an assembly under a name it was not audited with.
            var real = typeof(KBObject).Assembly;
            string root = Path.GetDirectoryName(real.Location);
            string realHash = FileSha(real.Location);
            var manifest = new JObject
            {
                ["schemaVersion"] = 1,
                ["bundleId"] = "fake+bundle",
                ["criticalAssemblies"] = new JArray("Artech.Renamed"),
                ["assemblies"] = new JArray(new JObject
                {
                    ["name"] = "Artech.Renamed", ["relativePath"] = Path.GetFileName(real.Location), ["sha256"] = realHash
                }),
                ["nativeVerification"] = JObject.Parse(Verified)
            };
            string baseDirectory = Path.Combine(_dir, "base-renamed");
            Directory.CreateDirectory(baseDirectory);
            byte[] bytes = Encoding.UTF8.GetBytes(manifest.ToString());
            File.WriteAllBytes(Path.Combine(baseDirectory, "fake-audit.json"), bytes);
            var audit = new EventsBundleAudit("fake-audit.json", bytes);

            var ex = Assert.Throws<InvalidOperationException>(
                () => audit.Verify("AnyObject", new Dictionary<string, string>(), root, baseDirectory, new[] { real }));

            Assert.Equal("Unknown loaded Events SDK implementation: " + real.GetName().Name, ex.Message);
        }

        [Fact]
        public void TheManifestOfThisInstallation_VerifiesAgainstTheInstalledFiles()
        {
            // When the SDK these tests run against is one of the audited bundles, its manifest must describe that very
            // installation, file by file. Any other SDK build (U12, U15, another U16 build) has no manifest and proves nothing here.
            // The test project copies the SDK assemblies it uses into its own output folder, so neither Location nor CodeBase
            // is the installation; GX_PATH, the variable the build and the gate set, is. Without it there is nothing to compare.
            string installedRoot = Environment.GetEnvironmentVariable("GX_PATH");
            if (string.IsNullOrWhiteSpace(installedRoot) || !File.Exists(Path.Combine(installedRoot, "Artech.Architecture.Common.dll")))
            {
                _output.WriteLine("NOT APPLICABLE: GX_PATH does not point to a GeneXus installation, so no manifest was compared.");
                return;
            }
            string coreHash = FileSha(Path.Combine(installedRoot, "Artech.Architecture.Common.dll"));
            var audit = EventsBundleAudits.All.FirstOrDefault(a => a.CriticalHashes["Artech.Architecture.Common"] == coreHash);
            if (audit == null)
            {
                _output.WriteLine("NOT APPLICABLE: the SDK of this run is not an audited bundle, so no manifest was compared.");
                return;
            }
            string baseDirectory = Path.Combine(_dir, "base-installed");
            Directory.CreateDirectory(baseDirectory);
            File.Copy(Path.Combine(FindRepositoryRoot(), "config", audit.ManifestName), Path.Combine(baseDirectory, audit.ManifestName));
            string name = (string)audit.NativeVerification["syntheticObject"] ?? "AnyObject";
            var hashes = new Dictionary<string, string>();

            // The count is the scope's concern, tested on its own below; this test is about the files of the installation.
            var report = audit.Verify(name, hashes, installedRoot, baseDirectory, new Assembly[0], () => 0);

            Assert.Equal(audit.BundleId, report["bundleId"].ToString());
            Assert.True(report["installedAssembliesVerified"].Value<int>() > 300);
            Assert.Equal(audit.CriticalHashes, hashes);
            _output.WriteLine("VERIFIED: manifest " + audit.BundleId + " matches " + report["installedAssembliesVerified"] + " installed files of " + installedRoot);
        }

        [Fact]
        public void PendingProof_AllowsOnlyTheAuthorizedSyntheticObject()
        {
            var f = NewFixture(Pending);

            Assert.Equal("fake+bundle", Verify(f, "Synthetic01")["bundleId"].ToString());
            foreach (string name in new[] { "BusinessObject", "synthetic01", "Synthetic01 ", "", null })
            {
                var ex = Assert.Throws<InvalidOperationException>(() => Verify(f, name));
                Assert.Contains("native verification is pending", ex.Message);
                Assert.Contains("fake+bundle", ex.Message);
            }
        }

        [Fact]
        public void PendingProof_RefusesBeforeTouchingTheInstallation()
        {
            // The refusal for a business object must not depend on, or reveal, what is installed.
            var f = NewFixture(Pending);
            File.WriteAllText(f.OneFile, "changed");

            var ex = Assert.Throws<InvalidOperationException>(() => Verify(f, "BusinessObject"));

            Assert.Contains("native verification is pending", ex.Message);
        }

        [Fact]
        public void UnknownOrMissingStatus_NeverAuthorizesAnObject()
        {
            foreach (string json in new[] { "{status:'unknown'}", "{status:'Verified'}", "{}", "{status:'pending'}" })
            {
                var f = NewFixture(json);
                Assert.Throws<InvalidOperationException>(() => Verify(f, "BusinessObject"));
            }
        }

        [Fact]
        public void ScopedProof_AcceptsAKnowledgeBaseWithoutPatternInstances_AndReportsTheCount()
        {
            var f = NewFixture(ScopedVerified);

            var report = Verify(f, "AnyObject", count: () => 0);

            Assert.Equal(0, report["patternInstanceCount"].Value<int>());
            Assert.Equal("kb-without-pattern-instances", (string)report["nativeVerification"]["scope"]);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(3)]
        [InlineData(30000)]
        public void ScopedProof_RefusesAKnowledgeBaseWithPatternInstances_AndSaysHowMany(int found)
        {
            var f = NewFixture(ScopedVerified);

            var ex = Assert.Throws<EventsBundleScopeException>(() => Verify(f, "AnyObject", count: () => found));

            Assert.Equal("The fake+bundle bundle is verified only for Knowledge Bases without pattern instances; this Knowledge Base has "
                + found + ".", ex.Message);
        }

        [Fact]
        public void ScopedProof_FailsClosedWhenThePatternInstancesCannotBeCounted_WithoutLeakingTheCause()
        {
            var f = NewFixture(ScopedVerified);

            var ex = Assert.Throws<EventsBundleScopeException>(
                () => Verify(f, "AnyObject", count: () => throw new IOException(@"C:\Secret\Kb\model.dat is locked")));

            Assert.Contains("could not be counted (IOException)", ex.Message);
            Assert.DoesNotContain("Secret", ex.Message);
            // No counter at all is the same refusal, never a pass.
            Assert.Throws<EventsBundleScopeException>(() => Verify(f, "AnyObject", count: null));
        }

        [Fact]
        public void ScopedPendingGrant_StillRefusesAKnowledgeBaseWithPatternInstances_AndABusinessObject()
        {
            var f = NewFixture(ScopedPending);

            Assert.Equal(0, Verify(f, "Synthetic01", count: () => 0)["patternInstanceCount"].Value<int>());
            Assert.Throws<EventsBundleScopeException>(() => Verify(f, "Synthetic01", count: () => 2));
            var pending = Assert.Throws<InvalidOperationException>(() => Verify(f, "BusinessObject", count: () => 0));
            Assert.Contains("native verification is pending", pending.Message);
        }

        [Theory]
        [InlineData("{status:'verified',scope:'whatever'}")]
        [InlineData("{status:'verified',scope:''}")]
        [InlineData("{status:'verified',scope:7}")]
        [InlineData("{status:'verified',scope:{}}")]
        public void AnUnknownScope_IsRefusedBeforeAnythingIsCounted(string json)
        {
            var f = NewFixture(json);
            int asked = 0;

            var ex = Assert.Throws<InvalidOperationException>(() => Verify(f, "AnyObject", count: () => { asked++; return 0; }));

            Assert.StartsWith("Unknown native verification scope:", ex.Message);
            Assert.Equal(0, asked);
        }

        [Fact]
        public void AProofWithoutAScope_NeverCountsAnything()
        {
            // The U16 proof covers its scenario wherever the bundle is loaded: it neither pays for the count nor depends on it.
            var f = NewFixture(Verified);
            int asked = 0;

            var report = Verify(f, "AnyObject", count: () => { asked++; return 5; });

            Assert.Equal(0, asked);
            Assert.Null(report["patternInstanceCount"]);
        }

        [Fact]
        public void TheCount_IsAskedOnlyAfterTheBundleItselfVerified()
        {
            // Counting walks the whole model: a bundle that does not verify is refused before that cost.
            var f = NewFixture(ScopedVerified);
            File.WriteAllText(f.OneFile, "changed");
            int asked = 0;

            var ex = Assert.Throws<InvalidOperationException>(() => Verify(f, "AnyObject", count: () => { asked++; return 0; }));

            Assert.Equal("Unverified Events SDK bundle assembly: Artech.Fake.One", ex.Message);
            Assert.Equal(0, asked);
        }

        [Fact]
        public void TheU11Manifest_IsScopedToKnowledgeBasesWithoutPatternInstances_AndTheU16ManifestIsNot()
        {
            Assert.Equal(EventsBundleAudit.ScopeKbWithoutPatternInstances, (string)EventsBundleAudits.U11.NativeVerification["scope"]);
            Assert.Equal(EventsBundleAudit.ScopeKbWithoutPatternInstances, (string)EventsBundleAudits.U10.NativeVerification["scope"]);
            Assert.Null(EventsBundleAudits.U16.NativeVerification["scope"]);
            foreach (var audit in EventsBundleAudits.All)
                Assert.Contains(EventsBundleAudit.ScopeOf(audit.NativeVerification), new[] { null, EventsBundleAudit.ScopeKbWithoutPatternInstances });
        }

        [Fact]
        public void CountPatternInstances_CountsOnlyPatternInstanceObjects()
        {
            KBObject Make(Type type) => (KBObject)FormatterServices.GetUninitializedObject(type);
            var webPanelA = Make(typeof(Artech.Genexus.Common.Objects.WebPanel));
            var webPanelB = Make(typeof(Artech.Genexus.Common.Objects.WebPanel));
            var transaction = Make(typeof(Artech.Genexus.Common.Objects.Transaction));
            var procedure = Make(typeof(Artech.Genexus.Common.Objects.Procedure));
            var instanceA = Make(typeof(Artech.Packages.Patterns.Objects.PatternInstance));
            var instanceB = Make(typeof(Artech.Packages.Patterns.Objects.PatternInstance));
            // The type name each object reports: webPanelB stands for an instance that materialized as another class.
            string Name(KBObject o) => ReferenceEquals(o, webPanelB) ? "patterninstance"
                : ((object)o).GetType() == typeof(Artech.Packages.Patterns.Objects.PatternInstance) ? "PatternInstance" : "Other";
            var others = new[] { webPanelA, transaction, procedure };

            Assert.Equal(2, EventsSaveIsolation.CountPatternInstances(others.Concat(new[] { instanceA, instanceB }), Name));
            Assert.Equal(3, EventsSaveIsolation.CountPatternInstances(others.Concat(new[] { instanceA, instanceB, webPanelB }), Name));
            Assert.Equal(0, EventsSaveIsolation.CountPatternInstances(others, Name));
            Assert.Equal(0, EventsSaveIsolation.CountPatternInstances(new KBObject[0], Name));
            // Each recognition works without the other: the class when the name is something else, the name when the class is.
            Assert.Equal(1, EventsSaveIsolation.CountPatternInstances(new[] { instanceA }, o => "Renamed"));
            Assert.Equal(1, EventsSaveIsolation.CountPatternInstances(new[] { webPanelA }, o => "PatternInstance"));
        }

        [Fact]
        public void CountPatternInstances_FailsClosedWhenAnObjectCannotBeInspected()
        {
            KBObject Make(Type type) => (KBObject)FormatterServices.GetUninitializedObject(type);
            var webPanel = Make(typeof(Artech.Genexus.Common.Objects.WebPanel));

            Assert.Throws<InvalidOperationException>(
                () => EventsSaveIsolation.CountPatternInstances(new[] { webPanel }, o => throw new InvalidOperationException("type lookup failed")));
        }

        [Fact]
        public void TheEmbeddedManifests_AreWellFormedAndDoNotOverlap()
        {
            Assert.Equal(3, EventsBundleAudits.All.Count);
            Assert.Equal(EventsBundleAudits.All.Count, EventsBundleAudits.All.Select(a => a.BundleId).Distinct().Count());
            Assert.Equal(EventsBundleAudits.All.Count, EventsBundleAudits.All.Select(a => a.ManifestName).Distinct().Count());
            foreach (var audit in EventsBundleAudits.All)
            {
                Assert.Matches(audit.IncludesWorkWithPlus ? @"^18\.0\.\d+\.\d+\+wwp\d+(\.\d+)+$" : @"^18\.0\.\d+\.\d+$", audit.BundleId);
                Assert.Equal(ExpectedCriticalOf(audit).OrderBy(n => n, StringComparer.Ordinal), audit.CriticalHashes.Keys.OrderBy(n => n, StringComparer.Ordinal));
                Assert.All(audit.CriticalHashes.Values, hash => Assert.Matches(Sha256Hex, hash));
            }
            var trigger = EventsBundleAudits.All.Select(a => a.CriticalHashes[a.Trigger]).ToArray();
            Assert.Equal(trigger.Length, trigger.Distinct().Count());
            // No critical assembly hash belongs to two audited bundles: a hash identifies one bundle.
            var allCritical = EventsBundleAudits.All.SelectMany(a => a.CriticalHashes.Values).ToArray();
            Assert.Equal(allCritical.Length, allCritical.Distinct().Count());
            Assert.Equal(trigger.OrderBy(h => h), EventsBundleAudits.TriggerHashes.OrderBy(h => h));
        }

        [Fact]
        public void TheEmbeddedManifests_ListEachAssemblyOnceWithAValidHashAndASafeRelativePath()
        {
            foreach (var audit in EventsBundleAudits.All)
            {
                var manifest = ManifestOf(audit);
                var entries = ((JArray)manifest["assemblies"]).Cast<JObject>().ToArray();
                Assert.NotEmpty(entries);
                Assert.Equal(entries.Length, entries.Select(e => (string)e["relativePath"]).Distinct(StringComparer.OrdinalIgnoreCase).Count());
                foreach (var entry in entries)
                {
                    string relative = (string)entry["relativePath"];
                    Assert.False(string.IsNullOrWhiteSpace((string)entry["name"]));
                    Assert.Matches(Sha256Hex, (string)entry["sha256"]);
                    Assert.False(Path.IsPathRooted(relative), relative);
                    Assert.DoesNotContain("..", relative);
                    Assert.DoesNotContain("\\", relative);
                    Assert.EndsWith(".dll", relative, StringComparison.OrdinalIgnoreCase);
                }
                foreach (string critical in ExpectedCriticalOf(audit))
                    Assert.Single(entries, e => (string)e["name"] == critical);
            }
        }

        [Fact]
        public void TheEmbeddedManifests_AreTheBytesOfTheRepositoryFilesThatAreDistributedBesideTheWorker()
        {
            string config = Path.Combine(FindRepositoryRoot(), "config");
            foreach (var audit in EventsBundleAudits.All)
            {
                byte[] file = File.ReadAllBytes(Path.Combine(config, audit.ManifestName));
                var embedded = typeof(EventsBundleAudit).Assembly.GetManifestResourceStream(audit.ManifestName);
                Assert.NotNull(embedded);
                using (embedded)
                using (var buffer = new MemoryStream())
                {
                    embedded.CopyTo(buffer);
                    Assert.Equal(file, buffer.ToArray());
                }
            }
        }

        [Fact]
        public void TheNativeVerificationOfEveryManifest_IsEitherAPendingSyntheticOnlyGrantOrACompleteProof()
        {
            foreach (var audit in EventsBundleAudits.All)
            {
                var proof = audit.NativeVerification;
                string status = (string)proof["status"];
                Assert.Contains(status, new[] { "pending", "verified" });
                if (status == "pending")
                {
                    Assert.False(string.IsNullOrWhiteSpace((string)proof["syntheticObject"]));
                    Assert.False(EventsBundleAudit.MaySaveTarget(proof, "BusinessObject"));
                    continue;
                }
                // `verified` is only ever written together with the digest of a real native proof and every item of it.
                Assert.Matches(Sha256Hex, (string)proof["proofEvidenceSha256"]);
                foreach (string item in new[]
                {
                    "previewNoMutation", "objectSaveVerified", "idempotentSaveVerified", "staleTokenRejected", "atomicRollbackVerified",
                    "canonicalPartsAndOtherObjectMetadataPreserved", "nativeWorkerTimeoutResultConsulted", "syntheticObjectsRemoved"
                })
                    Assert.True(proof[item]?.Value<bool>() == true, audit.BundleId + " / " + item);
                Assert.Equal(0, proof["automaticSaveRetries"].Value<int>());
                Assert.Empty((JArray)proof["implicitOperations"]);
            }
        }

        [Fact]
        public void TheU16FacadeKeepsItsIdAndHashes()
        {
            Assert.Equal(EventsU16BundleAudit.BundleId, EventsBundleAudits.U16.BundleId);
            Assert.Equal(EventsBundleAudits.U16.CriticalHashes, EventsU16BundleAudit.CriticalHashes);
        }

        [Fact]
        public void TheCompleteMatch_PicksTheBundleOfTheExactCriticalHashes_AndNoMixture()
        {
            var u16 = EventsBundleAudits.U16.CriticalHashes;
            var u11 = EventsBundleAudits.U11.CriticalHashes;
            Assert.Equal(EventsBundleAudits.U16.BundleId, EventsSaveIsolation.MatchAuditedBuild(new Dictionary<string, string>(u16)));
            Assert.Equal(EventsBundleAudits.U11.BundleId, EventsSaveIsolation.MatchAuditedBuild(new Dictionary<string, string>(u11)));

            foreach (string name in ExpectedCritical)
            {
                var mixed = new Dictionary<string, string>(u16);
                mixed[name] = u11[name];
                Assert.Null(EventsSaveIsolation.MatchAuditedBuild(mixed));
                var other = new Dictionary<string, string>(u11);
                other[name] = u16[name];
                Assert.Null(EventsSaveIsolation.MatchAuditedBuild(other));
                var changed = new Dictionary<string, string>(u11);
                changed[name] = new string('0', 64);
                Assert.Null(EventsSaveIsolation.MatchAuditedBuild(changed));
                var missing = new Dictionary<string, string>(u11);
                missing.Remove(name);
                Assert.Null(EventsSaveIsolation.MatchAuditedBuild(missing));
            }
        }

        [Fact]
        public void WithoutWorkWithPlus_TheRegistryChoosesTheBundleByTheExactCoreBytes_AndALoadedWorkWithPlusNeverSelectsIt()
        {
            var u10 = EventsBundleAudits.U10.CriticalHashes;
            Assert.False(EventsBundleAudits.U10.IncludesWorkWithPlus);
            Assert.Equal(EventsBundleAudit.CoreAssembly, EventsBundleAudits.U10.Trigger);
            Assert.Contains(u10[EventsBundleAudit.CoreAssembly], EventsBundleAudits.TriggerHashes);

            // Selected by its core, the bundle is then verified as a whole: a pending proof refuses a business object.
            if ((string)EventsBundleAudits.U10.NativeVerification["status"] == "pending")
            {
                var pending = Assert.Throws<InvalidOperationException>(() => EventsBundleAudits.Verify("RecordsBusinessObject", new Dictionary<string, string>(u10)));
                Assert.Contains(EventsBundleAudits.U10.BundleId + " bundle native verification is pending", pending.Message);
            }

            var withWwp = new Dictionary<string, string>(u10) { [EventsBundleAudit.TriggerAssembly] = new string('e', 64) };
            var ex = Assert.Throws<InvalidOperationException>(() => EventsBundleAudits.Verify("McpU10AuditProbe01", withWwp));
            Assert.Contains("covers the loaded WorkWithPlus", ex.Message);
            Assert.Null(EventsSaveIsolation.MatchAuditedBuild(withWwp));

            var otherCore = new Dictionary<string, string>(u10) { [EventsBundleAudit.CoreAssembly] = new string('0', 64) };
            ex = Assert.Throws<InvalidOperationException>(() => EventsBundleAudits.Verify("McpU10AuditProbe01", otherCore));
            Assert.Contains("covers the loaded SDK without WorkWithPlus", ex.Message);
        }

        [Fact]
        public void TheCompleteMatch_RecognizesTheBundleWithoutWorkWithPlus_OnlyWithEveryCriticalHash()
        {
            var u10 = EventsBundleAudits.U10.CriticalHashes;
            Assert.Equal(EventsBundleAudits.U10.BundleId, EventsSaveIsolation.MatchAuditedBuild(new Dictionary<string, string>(u10)));
            foreach (string name in u10.Keys)
            {
                var changed = new Dictionary<string, string>(u10) { [name] = new string('0', 64) };
                Assert.Null(EventsSaveIsolation.MatchAuditedBuild(changed));
                var missing = new Dictionary<string, string>(u10);
                missing.Remove(name);
                Assert.Null(EventsSaveIsolation.MatchAuditedBuild(missing));
            }
        }

        [Fact]
        public void TheRegistryChoosesTheManifestOnlyByTheExactWorkWithPlusBytes()
        {
            var unknown = new Dictionary<string, string> { [EventsBundleAudit.TriggerAssembly] = new string('f', 64) };
            var ex = Assert.Throws<InvalidOperationException>(() => EventsBundleAudits.Verify("Synthetic01", unknown));
            Assert.Contains("No complete bundle audit covers", ex.Message);
            Assert.Throws<InvalidOperationException>(() => EventsBundleAudits.Verify("Synthetic01", new Dictionary<string, string>()));
            Assert.Throws<InvalidOperationException>(() => EventsBundleAudits.Verify("Synthetic01", null));
        }

        [Fact]
        public void TheU11Candidate_NeverAuthorizesABusinessObjectWithoutAProof()
        {
            // Whatever the state of the proof, the business objects of a Knowledge Base are not saved through the U11 bundle
            // unless its native verification is complete (TheNativeVerificationOfEveryManifest... checks that completeness).
            var u11 = EventsBundleAudits.U11;
            if ((string)u11.NativeVerification["status"] == "pending")
            {
                var ex = Assert.Throws<InvalidOperationException>(() => u11.Verify("RecordsBusinessObject", new Dictionary<string, string>()));
                Assert.Contains("native verification is pending", ex.Message);
            }
            else
            {
                Assert.Equal("verified", (string)u11.NativeVerification["status"]);
            }
        }

        [Fact]
        public void TheU11Manifest_IsVerifiedWithTheDigestOfItsProof_AndNoLongerRefusesABusinessObject()
        {
            // The native proof ran on 28/10 in a Knowledge Base without pattern instances; the scope it carries is what keeps the
            // bundle from being trusted anywhere else (the count above refuses a Knowledge Base that has any).
            var proof = EventsBundleAudits.U11.NativeVerification;

            Assert.Equal("verified", (string)proof["status"]);
            Assert.Equal(EventsBundleAudit.ScopeKbWithoutPatternInstances, (string)proof["scope"]);
            Assert.Equal("synthetic-webpanel-in-kb-without-pattern-instances", (string)proof["scenario"]);
            Assert.Matches(Sha256Hex, (string)proof["proofEvidenceSha256"]);
            Assert.True(EventsBundleAudit.MaySaveTarget(proof, "AnyBusinessObject"));
        }

        private static JObject ManifestOf(EventsBundleAudit audit)
        {
            using (var stream = typeof(EventsBundleAudit).Assembly.GetManifestResourceStream(audit.ManifestName))
            using (var reader = new StreamReader(stream, Encoding.UTF8))
                return JObject.Parse(reader.ReadToEnd());
        }

        private static string FindRepositoryRoot()
        {
            DirectoryInfo directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Genexus18MCP.sln")))
                directory = directory.Parent;
            Assert.NotNull(directory);
            return directory.FullName;
        }
    }
}
