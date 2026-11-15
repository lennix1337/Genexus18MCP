using System;
using System.Collections.Generic;
using System.Linq;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // The isolated Events save refuses before any write when its prerequisites are not met. These tests
    // drive the pure evaluation behind that refusal: every case is a plain input, so nothing can open a KB,
    // call Save or reach an implicit operation, and every refusal is named, sanitized and actionable.
    public sealed class EventsIsolationFindingsTests
    {
        private const string PathPin = @"C:\KB\Synthetic";
        private const string VersionPin = "SyntheticVersion";

        private static IReadOnlyList<EventsIsolationFinding> Destination(
            string pathPin, string versionPin, string actualPath = PathPin, string actualVersion = VersionPin, bool? frozen = false)
            => EventsIsolationFindings.EvaluateDestination(pathPin, versionPin, actualPath, actualVersion, frozen);

        [Theory]
        [InlineData(null, null)]
        [InlineData("", "  ")]
        [InlineData("   ", null)]
        public void NeitherPin_IsAPinsMissingRefusal_NotAnInferredIdentity(string path, string version)
        {
            // The opened KB and its active version are right there; the preflight must still refuse,
            // because identity comes from the profile owner and never from the thing being written.
            var finding = Assert.Single(Destination(path, version));

            Assert.Equal(WriteDestinationGuard.PinsMissingCode, finding.Code);
            Assert.Equal(EventsIsolationFindings.PinsLayer, finding.Layer);
            Assert.Equal(EventsIsolationFindings.PinsSentence, finding.Message);
            Assert.Equal(
                new[] { WriteDestinationGuard.PathVariable, WriteDestinationGuard.VersionVariable },
                finding.Evidence["missing"].Values<string>().ToArray());
            Assert.Contains("environment that launches this Gateway", finding.Remediation);
            Assert.Contains("never derives", finding.Remediation);
        }

        [Fact]
        public void OnlyThePath_NamesTheMissingVersionPin()
        {
            var finding = Assert.Single(Destination(PathPin, null));

            Assert.Equal("WriteDestinationConfigurationIncomplete", finding.Code);
            Assert.Equal(new[] { WriteDestinationGuard.VersionVariable }, finding.Evidence["missing"].Values<string>().ToArray());
            Assert.Contains(WriteDestinationGuard.VersionVariable, finding.Message);
        }

        [Fact]
        public void OnlyTheVersion_NamesTheMissingPathPin()
        {
            var finding = Assert.Single(Destination(null, VersionPin));

            Assert.Equal("WriteDestinationConfigurationIncomplete", finding.Code);
            Assert.Equal(new[] { WriteDestinationGuard.PathVariable }, finding.Evidence["missing"].Values<string>().ToArray());
            Assert.Contains(WriteDestinationGuard.PathVariable, finding.Message);
        }

        [Fact]
        public void DivergentPath_IsAKbMismatchReportedByIdentityNeverByPath()
        {
            var finding = Assert.Single(Destination(@"C:\KB\Other", VersionPin));

            Assert.Equal("WriteDestinationKbMismatch", finding.Code);
            Assert.Equal(EventsIsolationFindings.DestinationLayer, finding.Layer);
            string expected = finding.Evidence["expectedKbIdentity"].ToString();
            string actual = finding.Evidence["actualKbIdentity"].ToString();
            Assert.Equal(64, expected.Length);
            Assert.NotEqual(expected, actual);
            string serialized = finding.ToJson().ToString();
            Assert.DoesNotContain("Synthetic", serialized);
            Assert.DoesNotContain(@"C:\KB", serialized.Replace("\\\\", "\\"));
        }

        [Fact]
        public void SamePathWithDifferentCaseAndSeparators_IsTheSameDestination()
            => Assert.Empty(Destination(@"c:\kb\synthetic\", VersionPin, actualPath: @"C:/KB/Synthetic"));

        [Fact]
        public void DivergentVersion_IsAVersionMismatchNamingBothVersions()
        {
            var finding = Assert.Single(Destination(PathPin, VersionPin, actualVersion: "OtherVersion"));

            Assert.Equal("WriteDestinationVersionMismatch", finding.Code);
            Assert.Equal(VersionPin, finding.Evidence["expectedVersion"].ToString());
            Assert.Equal("OtherVersion", finding.Evidence["actualVersion"].ToString());
            Assert.Contains("autoUpdate=false", finding.Remediation);
        }

        [Fact]
        public void ContradictoryPins_ReportTheDestinationBeforeTheVersion()
        {
            // A pin that names another KB and another version is contradictory with the opened KB on both
            // axes; the refusal is the destination, the first fact that makes every later one moot.
            var finding = Assert.Single(Destination(@"C:\KB\Other", "OtherVersion"));

            Assert.Equal("WriteDestinationKbMismatch", finding.Code);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(null)]
        public void FrozenOrUnknownWritability_IsRefused(bool? frozen)
        {
            var finding = Assert.Single(Destination(PathPin, VersionPin, frozen: frozen));

            Assert.Equal("WriteDestinationVersionNotWritable", finding.Code);
        }

        [Fact]
        public void MatchingPinsAndWritableVersion_RaiseNoFinding()
            => Assert.Empty(Destination(PathPin, VersionPin));

        private static Dictionary<string, string> Hashes(string value) => new Dictionary<string, string>
        {
            ["Artech.Architecture.Common"] = value + "1" + new string('0', 52),
            ["Artech.Genexus.Common"] = value + "2" + new string('0', 52),
            ["Artech.Udm.Framework"] = value + "3" + new string('0', 52),
        };

        [Fact]
        public void AuditedBundle_RaisesNoFinding()
            => Assert.Null(EventsIsolationFindings.EvaluateBundle(
                Hashes("a"), null, new string[0], new string[0], "18.0.16.189595", EventsSaveIsolation.AuditedBundleIds));

        [Fact]
        public void UnknownBundle_IsNamedWithHashPrefixesAndTheAuditedIds_NotAVersionAllowlist()
        {
            var versions = new Dictionary<string, string> { ["Artech.Genexus.Common"] = "18.0.11.54344" };

            var finding = EventsIsolationFindings.EvaluateBundle(
                Hashes("b"), versions, new string[0], new string[0], null, EventsSaveIsolation.AuditedBundleIds);

            Assert.NotNull(finding);
            Assert.Equal("SdkBundleNotAudited", finding.Code);
            Assert.Equal(EventsIsolationFindings.BundleLayer, finding.Layer);
            Assert.Equal("Unverified SDK implementation bundle; isolated Events save requires a complete audited SDK bundle (evidence.auditedBundles lists them).", finding.Message);
            Assert.Equal(12, finding.Evidence.SelectToken("loadedBundle['Artech.Genexus.Common'].sha256Prefix").ToString().Length);
            Assert.Equal("18.0.11.54344", finding.Evidence.SelectToken("loadedBundle['Artech.Genexus.Common'].fileVersion").ToString());
            var audited = finding.Evidence["auditedBundles"].Values<string>().ToArray();
            Assert.Contains("18.0.16.189550", audited);
            Assert.Contains("18.0.16.189595", audited);
            Assert.Contains(EventsU16BundleAudit.BundleId, audited);
            Assert.Contains(EventsBundleAudits.U16Build189158.BundleId, audited);
            Assert.Contains(EventsBundleAudits.U16Build189513.BundleId, audited);
            Assert.Contains(EventsBundleAudits.U16Build189552.BundleId, audited);
            Assert.Contains(EventsBundleAudits.U16Build189647.BundleId, audited);
            Assert.Contains(EventsBundleAudits.U11.BundleId, audited);
            Assert.Contains("do not make an SDK/WorkWithPlus bundle safe", finding.Remediation);
            Assert.Contains("no allowlist by version number", finding.Remediation);
            Assert.DoesNotContain(new string('0', 52), finding.ToJson().ToString());
        }

        [Fact]
        public void MissingAssembly_IsRefusedByName()
        {
            var finding = EventsIsolationFindings.EvaluateBundle(
                Hashes("a"), null, new[] { "Artech.Packages.Patterns" }, new string[0], null, EventsSaveIsolation.AuditedBundleIds);

            Assert.Equal("SdkBundleAssemblyMissing", finding.Code);
            Assert.Equal("Unverified SDK implementation: Artech.Packages.Patterns", finding.Message);
        }

        [Fact]
        public void MixedImplementationsOfOneAssembly_AreRefused()
        {
            var finding = EventsIsolationFindings.EvaluateBundle(
                Hashes("a"), null, new string[0], new[] { "Artech.Genexus.Common" }, "18.0.16.189595", EventsSaveIsolation.AuditedBundleIds);

            Assert.Equal("SdkBundleAssemblyAmbiguous", finding.Code);
            Assert.Contains("Artech.Genexus.Common", finding.Message);
        }

        [Fact]
        public void TheRealU16UpdatedBundleIsStillTheOnlyU16BundleThatMatches()
        {
            // The audited sets are exact hash sets: one changed assembly is a mixed bundle, not a near match.
            var updated = new Dictionary<string, string>((Dictionary<string, string>)typeof(EventsSaveIsolation)
                .GetField("UpdatedU16Assemblies", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic).GetValue(null));
            Assert.Equal("18.0.16.189595", EventsSaveIsolation.MatchAuditedBuild(updated));
            updated["Artech.Genexus.Common"] = "b" + new string('0', 63);
            Assert.Null(EventsSaveIsolation.MatchAuditedBuild(updated));
        }

        [Fact]
        public void TheU11SdkBundleIsNotAnyOfTheU16Bundles()
        {
            // First 16 hex digits of the SHA-256 of four Artech assemblies of the GeneXus 18 U11 (18.0.11) installation, as
            // the Worker logs them at startup. Matching is by full content hash, so a prefix can only ever be a negative proof.
            // The U11 bundle has its own manifest (EventsBundleAuditTests); it is not a U16 build.
            string[] u11Prefixes = { "b3f467e31d207ca4", "3c041fe183cc5b58", "5a5f45793e0293c2", "777b9dba655a5336" };
            var auditedHashes = new[] { "AuditedAssemblies", "UpdatedU16Assemblies" }
                .SelectMany(field => ((Dictionary<string, string>)typeof(EventsSaveIsolation)
                    .GetField(field, System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic).GetValue(null)).Values)
                .Concat(EventsU16BundleAudit.CriticalHashes.Values).ToArray();

            foreach (string prefix in u11Prefixes)
                Assert.DoesNotContain(auditedHashes, hash => hash.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Refusal_KeepsTheFirstFindingAsMessageAndListsEveryIndependentBlocker()
        {
            var pins = Destination(null, null).Single();
            var bundle = EventsIsolationFindings.EvaluateBundle(
                Hashes("b"), null, new string[0], new string[0], null, EventsSaveIsolation.AuditedBundleIds);
            var refusal = new EventsIsolationRefusalException(new[] { pins, bundle });

            var report = EventsSaveIsolation.DescribeRefusal(refusal);

            Assert.Equal(refusal.Message, report["reason"].ToString());
            Assert.Equal(EventsIsolationFindings.PinsSentence, report["reason"].ToString());
            Assert.Equal("ObjectSaveIsolationUnverified", report["blocker"].ToString());
            Assert.Equal(WriteDestinationGuard.PinsMissingCode, report["reasonCode"].ToString());
            Assert.False(report["verified"].Value<bool>());
            var codes = ((JArray)report["blockers"]).Select(b => b["code"].ToString()).ToArray();
            Assert.Equal(new[] { WriteDestinationGuard.PinsMissingCode, "SdkBundleNotAudited" }, codes);
            Assert.All((JArray)report["blockers"], b => Assert.False(string.IsNullOrWhiteSpace(b["remediation"]?.ToString())));
        }

        [Fact]
        public void Refusal_StillCarriesThePinDiagnosticsAndStatesNothingWasWritten()
        {
            var report = EventsSaveIsolation.DescribeRefusal(new EventsIsolationRefusalException(Destination(null, null)));

            Assert.Equal("isolation-preflight", report["phase"].ToString());
            Assert.Equal(2, ((JArray)report["requiredPins"]).Count);
            Assert.False(report["persisted"].Value<bool>());
            Assert.False(report["saved"].Value<bool>());
            Assert.False(report["writeAttempted"].Value<bool>());
            Assert.Empty((JArray)report["implicitOperations"]);
        }

        [Fact]
        public void AnyOtherPreflightFailure_IsNamedNotSwallowed()
        {
            var report = EventsSaveIsolation.DescribeRefusal(new InvalidOperationException("SDK fresh object lookup failed."));

            Assert.Equal("SDK fresh object lookup failed.", report["reason"].ToString());
            Assert.Equal("IsolationPreflightFailed", report["reasonCode"].ToString());
            var only = Assert.Single((JArray)report["blockers"]);
            Assert.Equal("InvalidOperationException", only["evidence"]["exceptionType"].ToString());
        }

        [Fact]
        public void DescribePins_SaysWhereEachPinCameFromWithoutRepeatingItsValue()
        {
            var report = WriteDestinationGuard.DescribePins(PathPin, null);

            Assert.Equal("process-environment", report.SelectToken("effectivePins.source").ToString());
            Assert.Equal("present", report.SelectToken("effectivePins.variables.GXMCP_EXPECTED_KB_PATH").ToString());
            Assert.Equal("absent", report.SelectToken("effectivePins.variables.GXMCP_EXPECTED_KB_VERSION").ToString());
            Assert.DoesNotContain("Synthetic", report.ToString());
            Assert.Equal("blank", WriteDestinationGuard.DescribePins("  ", null).SelectToken("effectivePins.variables.GXMCP_EXPECTED_KB_PATH").ToString());
        }

        [Fact]
        public void DescribePins_KeepsTheGatewayConfigRevisionApartFromTheEnvironmentTheyCameFrom()
        {
            var report = WriteDestinationGuard.DescribePins(null, null);

            // profileRevision is the Gateway configuration file; the pins are process environment variables.
            Assert.Equal("native-config", report["profileRevisionSource"].ToString());
            Assert.Equal("process-environment", report.SelectToken("effectivePins.source").ToString());
        }

        [Fact]
        public void EligibleReport_CarriesTheSamePinEvidenceAsARefusal()
        {
            var report = new JObject { ["verified"] = true };

            EventsSaveIsolation.AppendPinEvidence(report);

            Assert.Equal("native-config", report["profileRevisionSource"].ToString());
            Assert.Equal("process-environment", report.SelectToken("effectivePins.source").ToString());
            Assert.True(report["verified"].Value<bool>());
            // Same keys a refusal reports, so a client reads applied and refused pins the same way.
            var refusal = EventsSaveIsolation.DescribeRefusal(new InvalidOperationException("x"));
            foreach (string key in new[] { "effectivePins", "profileRevision", "profileRevisionSource" })
            {
                Assert.NotNull(report[key]);
                Assert.NotNull(refusal[key]);
            }
        }

        [Fact]
        public void GuardClassification_IsTheSingleDecisionBehindBothThePlainGuardAndTheIsolationRefusal()
        {
            // Without requirePins the plain guard keeps its documented behavior: neither pin set means no guard.
            Assert.Null(WriteDestinationGuard.Classify(null, null, PathPin, VersionPin, false, opening: false));
            Assert.Null(WriteDestinationGuard.Check(null, null, PathPin, VersionPin, false, false));
            Assert.Equal(WriteDestinationGuard.PinsMissingCode,
                WriteDestinationGuard.Classify(null, null, PathPin, VersionPin, false, opening: false, requirePins: true));
        }
    }
}
