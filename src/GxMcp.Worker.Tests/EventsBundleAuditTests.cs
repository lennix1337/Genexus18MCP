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
            Assert.All(U16Builds, audit => Assert.Null(audit.NativeVerification["scope"]));
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
            // Five GeneXus 18 U16 builds (one manifest each), U11 and U10.
            Assert.Equal(7, EventsBundleAudits.All.Count);
            Assert.Equal(5, U16Builds.Length);
            Assert.Equal(EventsBundleAudits.All.Count, EventsBundleAudits.All.Select(a => a.BundleId).Distinct().Count());
            Assert.Equal(EventsBundleAudits.All.Count, EventsBundleAudits.All.Select(a => a.ManifestName).Distinct().Count());
            foreach (var audit in EventsBundleAudits.All)
            {
                Assert.Matches(audit.IncludesWorkWithPlus ? @"^18\.0\.\d+\.\d+\+wwp\d+(\.\d+)+$" : @"^18\.0\.\d+\.\d+$", audit.BundleId);
                Assert.Equal(ExpectedCriticalOf(audit).OrderBy(n => n, StringComparer.Ordinal), audit.CriticalHashes.Keys.OrderBy(n => n, StringComparer.Ordinal));
                Assert.All(audit.CriticalHashes.Values, hash => Assert.Matches(Sha256Hex, hash));
            }
            // The SDK core names one build of the SDK: no two audited bundles share it, which is what lets it tell apart two
            // bundles that carry the same WorkWithPlus.
            var cores = EventsBundleAudits.All.Select(a => a.CoreHash).ToArray();
            Assert.Equal(cores.Length, cores.Distinct().Count());
            // The Artech assemblies of a bundle are those of its SDK build: none belongs to two bundles. Only a WorkWithPlus
            // assembly may be shared (one release installed on two SDK builds), and then only between bundles that carry it.
            var shared = EventsBundleAudits.All
                .SelectMany(a => a.CriticalHashes.Select(entry => new { Bundle = a, entry.Key, entry.Value }))
                .GroupBy(item => item.Value).Where(group => group.Count() > 1).ToArray();
            Assert.All(shared, group =>
            {
                Assert.All(group, item => Assert.StartsWith("DVelop.", item.Key, StringComparison.Ordinal));
                Assert.All(group, item => Assert.True(item.Bundle.IncludesWorkWithPlus, item.Bundle.BundleId));
            });
            var trigger = EventsBundleAudits.All.Select(a => a.CriticalHashes[a.Trigger]).Distinct().ToArray();
            Assert.Equal(trigger.OrderBy(h => h), EventsBundleAudits.TriggerHashes.OrderBy(h => h));
        }

        // The audited GeneXus 18 U16 builds, oldest first: one bundle (and one manifest) each, all with the same WorkWithPlus release.
        private static EventsBundleAudit[] U16Builds => new[]
        {
            EventsBundleAudits.U16Build189158, EventsBundleAudits.U16Build189513, EventsBundleAudits.U16Build189552,
            EventsBundleAudits.U16, EventsBundleAudits.U16Build189647
        };

        private static EventsBundleAudit U16BuildOf(string build)
            => EventsBundleAudits.All.Single(audit => audit.BundleId == "18.0.16." + build + "+wwp16.2.0.8386");

        [Fact]
        public void TheSameWorkWithPlusOnEveryAuditedU16Build_IsToldApartByTheExactSdkCoreBytes()
        {
            var builds = U16Builds;
            // The premise: one WorkWithPlus release, byte for byte, on five builds of the GeneXus 18 U16 SDK.
            Assert.Equal(new[]
            {
                "18.0.16.189158+wwp16.2.0.8386", "18.0.16.189513+wwp16.2.0.8386", "18.0.16.189552+wwp16.2.0.8386",
                "18.0.16.189595+wwp16.2.0.8386", "18.0.16.189647+wwp16.2.0.8386"
            }, builds.Select(a => a.BundleId));
            Assert.Single(builds.Select(a => a.TriggerHash).Distinct());
            Assert.Equal(builds.Length, builds.Select(a => a.CoreHash).Distinct().Count());

            // Each build selects its own manifest, whatever the order of the registry.
            foreach (var audit in builds)
            {
                Assert.Same(audit, EventsBundleAudits.Select(new Dictionary<string, string>(audit.CriticalHashes)));
                Assert.Equal(audit.BundleId, EventsSaveIsolation.MatchAuditedBuild(new Dictionary<string, string>(audit.CriticalHashes)));
            }
        }

        [Theory]
        [InlineData("189158", "a25625dec745f34114adebd66619ad9f964bf55910a7ae96881d686babe6e4e6")]
        [InlineData("189513", "0f27f6f1e9ef24f392ba5df35e5225b04b6963cae000a4886872e874b9594cfc")]
        [InlineData("189552", "61186b156df3a515c3057bb6f182206ea3280a34739491b955eae7ca010e70cd")]
        [InlineData("189595", "978fab2afad4daaa28aecffd5cd327367b1bb9c0a8f027cfda74911a294eac12")]
        [InlineData("189647", "160b59afdab14c160711de46f60e6644a2ec88f420a1cf2b86d97219fa3464c6")]
        public void TheSdkCoreOfEveryAuditedU16Build_IsTheOneOfItsVendorInstallationArchive(string build, string core)
        {
            // The SHA-256 of Artech.Architecture.Common.dll as the vendor's installation archive of that build carries it: the identity of the
            // build, pinned here independently of the manifest so that editing a manifest cannot move a build onto another one.
            var audit = U16BuildOf(build);
            Assert.Equal(core, audit.CoreHash);
            Assert.Equal(core, audit.CriticalHashes[EventsBundleAudit.CoreAssembly]);
            Assert.Same(audit, EventsBundleAudits.Select(new Dictionary<string, string>(audit.CriticalHashes)));
        }

        [Fact]
        public void TheSharedWorkWithPlusNeverSelectsABundleWhoseSdkCoreIsNotTheLoadedOne()
        {
            var newer = EventsBundleAudits.U16Build189647;
            var older = EventsBundleAudits.U16;

            // An SDK core that no bundle carrying this WorkWithPlus audited selects nothing, and the refusal names everything that was audited.
            var unknownCore = new Dictionary<string, string>(newer.CriticalHashes) { [EventsBundleAudit.CoreAssembly] = new string('0', 64) };
            var ex = Assert.Throws<InvalidOperationException>(() => EventsBundleAudits.Select(unknownCore));
            Assert.Contains("No complete bundle audit covers the loaded SDK core with the loaded WorkWithPlus", ex.Message);
            Assert.All(U16Builds, audit => Assert.Contains(audit.BundleId, ex.Message));
            Assert.DoesNotContain(EventsBundleAudits.U11.BundleId, ex.Message);
            Assert.Throws<InvalidOperationException>(() => EventsBundleAudits.Verify("Synthetic01", unknownCore));
            Assert.Null(EventsSaveIsolation.MatchAuditedBuild(unknownCore));

            // Exact bytes only: a core that differs from the audited one in its last character, or only in letter case, is another core.
            string audited = newer.CoreHash;
            var lastCharChanged = new Dictionary<string, string>(newer.CriticalHashes)
            {
                [EventsBundleAudit.CoreAssembly] = audited.Substring(0, audited.Length - 1) + (audited[audited.Length - 1] == '0' ? '1' : '0')
            };
            Assert.Throws<InvalidOperationException>(() => EventsBundleAudits.Select(lastCharChanged));
            var upperCase = new Dictionary<string, string>(newer.CriticalHashes) { [EventsBundleAudit.CoreAssembly] = audited.ToUpperInvariant() };
            Assert.NotEqual(audited, upperCase[EventsBundleAudit.CoreAssembly]);
            Assert.Throws<InvalidOperationException>(() => EventsBundleAudits.Select(upperCase));

            // No core at all is the same refusal, never the first bundle that has the WorkWithPlus.
            var noCore = new Dictionary<string, string>(newer.CriticalHashes);
            noCore.Remove(EventsBundleAudit.CoreAssembly);
            Assert.Throws<InvalidOperationException>(() => EventsBundleAudits.Select(noCore));

            // The core of the other bundle that has this WorkWithPlus selects that bundle, not this one: the bundle is then
            // verified as a whole, so a mixture of the two builds is refused file by file and never matched as an audited build.
            var mixed = new Dictionary<string, string>(newer.CriticalHashes) { [EventsBundleAudit.CoreAssembly] = older.CoreHash };
            Assert.Same(older, EventsBundleAudits.Select(mixed));
            Assert.Null(EventsSaveIsolation.MatchAuditedBuild(mixed));
            // The same holds for every ordered pair of audited builds: the core picks the manifest, never the other assemblies.
            foreach (var a in U16Builds)
                foreach (var b in U16Builds.Where(other => !ReferenceEquals(other, a)))
                {
                    var mixture = new Dictionary<string, string>(a.CriticalHashes) { [EventsBundleAudit.CoreAssembly] = b.CoreHash };
                    Assert.Same(b, EventsBundleAudits.Select(mixture));
                    Assert.Null(EventsSaveIsolation.MatchAuditedBuild(mixture));
                }

            // A WorkWithPlus that no bundle audited selects nothing, with any core.
            var otherWwp = new Dictionary<string, string>(newer.CriticalHashes) { [EventsBundleAudit.TriggerAssembly] = new string('e', 64) };
            ex = Assert.Throws<InvalidOperationException>(() => EventsBundleAudits.Select(otherWwp));
            Assert.Contains("covers the loaded WorkWithPlus", ex.Message);
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
        public void TheCompleteMatch_KeepsEveryPairOfSdkBuildsOfOneWorkWithPlusApart_AndNoMixtureOfThemMatches()
        {
            var builds = U16Builds;
            foreach (var first in builds)
                foreach (var second in builds.Where(other => !ReferenceEquals(other, first)))
                {
                    var older = first.CriticalHashes;
                    var newer = second.CriticalHashes;
                    var differing = ExpectedCritical.Where(name => older[name] != newer[name]).ToArray();
                    // Every SDK assembly differs between two builds; the WorkWithPlus ones are the same bytes.
                    Assert.Equal(new[] { "Artech.Architecture.Common", "Artech.Common.Properties", "Artech.Genexus.Common", "Artech.Packages.Patterns", "Artech.Udm.Framework" },
                        differing.OrderBy(name => name, StringComparer.Ordinal));
                    Assert.All(ExpectedCritical.Except(differing), name => Assert.StartsWith("DVelop.", name, StringComparison.Ordinal));

                    foreach (string name in differing)
                    {
                        // One assembly of the other build is a mixed bundle, whichever build is the base.
                        var olderWithOne = new Dictionary<string, string>(older) { [name] = newer[name] };
                        Assert.Null(EventsSaveIsolation.MatchAuditedBuild(olderWithOne));
                    }
                }
            foreach (var audit in builds)
                foreach (string name in ExpectedCritical)
                {
                    var changed = new Dictionary<string, string>(audit.CriticalHashes) { [name] = new string('0', 64) };
                    Assert.Null(EventsSaveIsolation.MatchAuditedBuild(changed));
                    var missing = new Dictionary<string, string>(audit.CriticalHashes);
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
        public void TheRegistryChoosesTheManifestOnlyByTheExactWorkWithPlusBytes_AndTheExactCoreBytesAmongThoseThatShareThem()
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

        [Theory]
        [InlineData("189158")]
        [InlineData("189513")]
        [InlineData("189552")]
        [InlineData("189647")]
        public void TheU16BuildManifest_IsVerifiedWithTheDigestOfItsProof_AndSaysWhatItDoesNotCover(string build)
        {
            // The proof of each build ran in a disposable Knowledge Base that has a pattern instance, with WorkWithPlus loaded and a
            // synthetic WebPanel that is not itself a persisted host: the manifest declares no scope (the proof is not conditioned on the
            // Knowledge Base) and states the scenario it did not exercise instead of claiming the host proof of build 189595.
            var audit = U16BuildOf(build);
            var proof = audit.NativeVerification;

            Assert.Equal("verified", (string)proof["status"]);
            Assert.Null(proof["scope"]);
            Assert.Equal("synthetic-webpanel-in-kb-with-pattern-instances-and-loaded-workwithplus", (string)proof["scenario"]);
            Assert.Matches(Sha256Hex, (string)proof["proofEvidenceSha256"]);
            Assert.True(EventsBundleAudit.MaySaveTarget(proof, "AnyBusinessObject"));
            Assert.Contains("persisted WorkWithPlus host", string.Join(" ", ((JArray)proof["notCovered"]).Values<string>()));
            // Each build was proven on its own: no two builds share the digest of a proof.
            Assert.Equal(U16Builds.Length, U16Builds.Select(a => (string)a.NativeVerification["proofEvidenceSha256"]).Distinct().Count());
        }

        [Theory]
        [InlineData("189158")]
        [InlineData("189513")]
        [InlineData("189552")]
        [InlineData("189647")]
        public void TheU16BuildRuntimeInspection_IsConsistentWithItself_AndComparesTheSameSaveHooksAsTheControl(string build)
        {
            var audit = U16BuildOf(build);
            var inspection = (JObject)ManifestOf(audit)["runtimeInspection"];

            // The nine critical assemblies at least; the audit also read every other assembly that subscribes to a Save topic.
            Assert.True(inspection.Value<int>("assemblies") >= 9);
            Assert.Equal(0, inspection.Value<int>("bodyErrors"));
            Assert.Equal(0, inspection.Value<int>("decodeErrors"));
            Assert.True(inspection.Value<bool>("coreProtectedBodiesRequireNativeProof"));
            // The whole-bundle inventory of subscriptions is part of the audit: the builds subscribe to the same topics, assembly by assembly.
            var whole = (JObject)inspection["wholeBundle"];
            int codeAssemblies = ((JArray)ManifestOf(audit)["assemblies"])
                .Count(entry => !((string)entry["relativePath"]).EndsWith(".resources.dll", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(codeAssemblies, whole.Value<int>("codeAssemblies"));
            Assert.Equal(0, whole.Value<int>("assembliesWhoseSubscriptionsDiffer"));
            Assert.True(whole.Value<int>("assembliesSubscribingToSaveTopics") > 0);
            // Every save hook that was compared is identical, by IL and by what it refers to: a difference would have to be listed.
            int compared = inspection.Value<int>("saveHooksCompared");
            Assert.True(compared > 0);
            Assert.Equal(compared, inspection.Value<int>("identicalIl"));
            Assert.Equal(compared, inspection.Value<int>("identicalReferenceSequences"));
            Assert.True(inspection.Value<int>("methodsWithIdenticalIl") <= inspection.Value<int>("methodsCompared"));
            Assert.NotEmpty((JArray)inspection["differences"]);
        }

        [Fact]
        public void TheAuditedU16Builds_WereComparedOnTheSameSaveHooks()
        {
            // The same 49 assemblies and the same save hooks were compared on every build, so what the proofs ran against is comparable.
            var inspections = new[] { "189158", "189513", "189552", "189647" }
                .Select(build => (JObject)ManifestOf(U16BuildOf(build))["runtimeInspection"]).ToArray();
            Assert.Single(inspections.Select(i => i.Value<int>("saveHooksCompared")).Distinct());
            Assert.Single(inspections.Select(i => i.Value<int>("assemblies")).Distinct());
            Assert.Single(inspections.Select(i => i["wholeBundle"].Value<int>("eventSubscriptions")).Distinct());
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
