using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // The whole orchestration of the isolated-save preflight (every phase, their order, the gating between them and what
    // happens when a phase fails) runs here on fakes. Each probe member is a spy or a stub: nothing opens a KB, loads the
    // SDK or reads a file, so a regression in the orchestration fails here instead of only against a real Knowledge Base.
    public sealed class EventsIsolationEvaluationTests
    {
        private sealed class EventsPartStub { }
        private sealed class AllowedPartStub { }
        private sealed class ExtensionPartStub { }

        private sealed class Spy
        {
            public int Hashed, Verified, Matched, Inspected;
            public readonly List<string> HashedLocations = new List<string>();
        }

        private static IsolationProbe Probe(Spy spy, Action<IsolationProbe> tweak = null)
        {
            var probe = new IsolationProbe
            {
                ExpectedPath = @"C:\KB\Synthetic",
                ExpectedVersion = "SyntheticVersion",
                ReadDestination = () => new IsolationDestination { Path = @"C:\KB\Synthetic", VersionName = "SyntheticVersion", Frozen = false },
                RequiredAssemblies = new[] { "Core.A", "Core.B", "Optional.Wwp" },
                OptionalAssembly = "Optional.Wwp",
                LoadedLocations = name => name == "Optional.Wwp" ? new string[0] : new[] { @"C:\sdk\" + name + ".dll" },
                HashFile = path => { spy.Hashed++; spy.HashedLocations.Add(path); return path.Contains("Optional") ? "trigger" : "hash:" + path; },
                FileVersion = path => "1.0.0",
                CompleteBundleTriggerHashes = new[] { "trigger" },
                VerifyCompleteBundle = hashes => { spy.Verified++; return new JObject { ["verified"] = true }; },
                MatchAuditedBuild = hashes => { spy.Matched++; return "audited-build"; },
                AuditedBundleIds = new[] { "id1", "id2" },
                PartTypes = () => new[] { typeof(EventsPartStub), typeof(AllowedPartStub) },
                IsAllowedPart = type => type == typeof(EventsPartStub) || type == typeof(AllowedPartStub),
                RequiredPart = typeof(EventsPartStub),
                InspectCallbacks = () => spy.Inspected++
            };
            tweak?.Invoke(probe);
            return probe;
        }

        private static string[] Codes(IsolationEvaluation evaluation) => evaluation.Findings.Select(f => f.Code).ToArray();

        [Fact]
        public void EverythingEligible_RaisesNoFindingAndCarriesTheFactsTheReportNeeds()
        {
            var spy = new Spy();

            var result = EventsIsolationFindings.Evaluate(Probe(spy));

            Assert.Empty(result.Findings);
            Assert.Equal("audited-build", result.AuditedBuild);
            Assert.Null(result.BundleAudit);
            Assert.Equal("SyntheticVersion", result.Destination.VersionName);
            Assert.Equal(new[] { typeof(EventsPartStub), typeof(AllowedPartStub) }, result.PartTypes.ToArray());
            Assert.Equal(2, spy.Hashed);   // the two core assemblies; the optional one is not loaded
            Assert.Equal(0, spy.Verified);
            Assert.Equal(1, spy.Matched);
            Assert.Equal(1, spy.Inspected);
        }

        [Fact]
        public void TheOptionalAssemblyWithTheTriggerHash_RunsTheCompleteBundleAuditExactlyOnce()
        {
            var spy = new Spy();

            var result = EventsIsolationFindings.Evaluate(Probe(spy, p =>
                p.LoadedLocations = name => new[] { @"C:\sdk\" + name + ".dll" }));

            Assert.Empty(result.Findings);
            Assert.Equal(1, spy.Verified);
            Assert.True(result.BundleAudit["verified"].Value<bool>());
            Assert.Equal(1, spy.Matched);
        }

        [Fact]
        public void ACoreAssemblyWithTheTriggerHash_RunsTheCompleteAuditWithoutTheOptionalAssembly()
        {
            // A bundle audited without WorkWithPlus is selected by its SDK core: with the optional assembly not loaded, the
            // complete audit still runs, and a failure of that audit is a refusal, never a match.
            var spy = new Spy();

            var result = EventsIsolationFindings.Evaluate(Probe(spy, p =>
                p.HashFile = path => { spy.Hashed++; return path.Contains("Core.A") ? "trigger" : "hash:" + path; }));

            Assert.Empty(result.Findings);
            Assert.Equal(1, spy.Verified);
            Assert.Equal(1, spy.Matched);

            var refused = EventsIsolationFindings.Evaluate(Probe(spy = new Spy(), p =>
            {
                p.HashFile = path => { spy.Hashed++; return path.Contains("Core.A") ? "trigger" : "hash:" + path; };
                p.VerifyCompleteBundle = hashes => { spy.Verified++; throw new InvalidOperationException("Unknown loaded Events SDK implementation: Core.C"); };
            }));

            Assert.Equal(new[] { "SdkBundleAuditFailed" }, Codes(refused));
            Assert.Equal(0, spy.Matched);
            Assert.Null(refused.AuditedBuild);
        }

        [Fact]
        public void TheOptionalAssemblyWithAnotherHash_SkipsTheCompleteAuditAndIsNotAudited()
        {
            var spy = new Spy();

            var result = EventsIsolationFindings.Evaluate(Probe(spy, p =>
            {
                p.LoadedLocations = name => new[] { @"C:\sdk\" + name + ".dll" };
                p.HashFile = path => { spy.Hashed++; return path.Contains("Optional") ? "another" : "hash:" + path; };
                p.MatchAuditedBuild = hashes => { spy.Matched++; return hashes.Values.Contains("another") ? null : "audited-build"; };
            }));

            Assert.Equal(new[] { "SdkBundleNotAudited" }, Codes(result));
            Assert.Equal(0, spy.Verified);
        }

        [Fact]
        public void FindingsComeInTheOrderTheChecksRun_AndTheFirstIsThePinsFinding()
        {
            var spy = new Spy();

            var result = EventsIsolationFindings.Evaluate(Probe(spy, p =>
            {
                p.ExpectedPath = null;
                p.ExpectedVersion = null;
                p.MatchAuditedBuild = hashes => null;
                p.PartTypes = () => new[] { typeof(EventsPartStub), typeof(ExtensionPartStub) };
                p.InspectCallbacks = () => throw new InvalidOperationException("An unverified direct SDK callback is attached.");
            }));

            Assert.Equal(
                new[] { WriteDestinationGuard.PinsMissingCode, "SdkBundleNotAudited", "UnverifiedObjectPart", "UnverifiedSdkCallback" },
                Codes(result));
            Assert.Equal(
                new[] { EventsIsolationFindings.PinsLayer, EventsIsolationFindings.BundleLayer, EventsIsolationFindings.ObjectLayer, EventsIsolationFindings.CallbackLayer },
                result.Findings.Select(f => f.Layer).ToArray());
            Assert.Equal(EventsIsolationFindings.PinsSentence, new EventsIsolationRefusalException(result.Findings).Message);
            Assert.Contains(typeof(ExtensionPartStub).FullName, result.Findings[2].Message);
        }

        [Fact]
        public void ADivergentDestinationWithEverythingElseEligible_IsTheOnlyFinding()
        {
            var result = EventsIsolationFindings.Evaluate(Probe(new Spy(), p =>
                p.ReadDestination = () => new IsolationDestination { Path = @"C:\KB\Other", VersionName = "SyntheticVersion", Frozen = false }));

            Assert.Equal(new[] { "WriteDestinationKbMismatch" }, Codes(result));
        }

        [Fact]
        public void AMissingAssembly_SkipsTheCompleteAuditAndTheBuildMatch()
        {
            var spy = new Spy();

            var result = EventsIsolationFindings.Evaluate(Probe(spy, p =>
                p.LoadedLocations = name => name == "Core.B" || name == "Optional.Wwp" ? new string[0] : new[] { @"C:\sdk\" + name + ".dll" }));

            Assert.Equal(new[] { "SdkBundleAssemblyMissing" }, Codes(result));
            Assert.Equal("Unverified SDK implementation: Core.B", result.Findings[0].Message);
            Assert.Equal(0, spy.Verified);
            Assert.Equal(0, spy.Matched);
            Assert.Equal(1, spy.Hashed);
        }

        [Fact]
        public void TwoImplementationsOfOneAssembly_AreAMixedBundleNeverHashedNorMatched()
        {
            var spy = new Spy();

            var result = EventsIsolationFindings.Evaluate(Probe(spy, p =>
                p.LoadedLocations = name => name == "Core.A" ? new[] { @"C:\sdk\one\Core.A.dll", @"C:\sdk\two\Core.A.dll" }
                    : name == "Optional.Wwp" ? new string[0] : new[] { @"C:\sdk\" + name + ".dll" }));

            Assert.Equal(new[] { "SdkBundleAssemblyAmbiguous" }, Codes(result));
            Assert.DoesNotContain(spy.HashedLocations, location => location.Contains("Core.A"));
            Assert.Equal(0, spy.Verified);
            Assert.Equal(0, spy.Matched);
        }

        [Fact]
        public void ACompleteAuditThatRefuses_BecomesAFindingAndSkipsTheBuildMatch()
        {
            var spy = new Spy();

            var result = EventsIsolationFindings.Evaluate(Probe(spy, p =>
            {
                p.LoadedLocations = name => new[] { @"C:\sdk\" + name + ".dll" };
                p.VerifyCompleteBundle = hashes => throw new InvalidOperationException("Unverified Events SDK bundle assembly: Core.A");
            }));

            Assert.Equal(new[] { "SdkBundleAuditFailed" }, Codes(result));
            Assert.Equal("Unverified Events SDK bundle assembly: Core.A", result.Findings[0].Message);
            Assert.Equal(0, spy.Matched);
        }

        [Fact]
        public void ABundleVerifiedOnlyForKnowledgeBasesWithoutPatternInstances_IsAFindingOfItsOwn()
        {
            var spy = new Spy();
            const string message = "The 18.0.11.54344+wwp16.1.2.8167 bundle is verified only for Knowledge Bases without pattern instances; this Knowledge Base has 2.";

            var result = EventsIsolationFindings.Evaluate(Probe(spy, p =>
            {
                p.LoadedLocations = name => new[] { @"C:\sdk\" + name + ".dll" };
                p.VerifyCompleteBundle = hashes => throw new EventsBundleScopeException(message);
            }));

            Assert.Equal(new[] { "PatternInstancesNotVerified" }, Codes(result));
            Assert.Equal(message, result.Findings[0].Message);
            Assert.Equal(EventsIsolationFindings.BundleLayer, result.Findings[0].Layer);
            Assert.Contains("covers only Knowledge Bases that have no pattern instances", result.Findings[0].Remediation);
            Assert.Equal(0, spy.Matched);
        }

        [Fact]
        public void AnIoFailureWhileHashing_DoesNotHideThePinsFinding_AndDoesNotLeakThePath()
        {
            var result = EventsIsolationFindings.Evaluate(Probe(new Spy(), p =>
            {
                p.ExpectedPath = null;
                p.ExpectedVersion = null;
                p.HashFile = path => throw new IOException(@"Could not open C:\Secret\Dir\Core.A.dll");
            }));

            Assert.Equal(new[] { WriteDestinationGuard.PinsMissingCode, "IsolationPreflightFailed" }, Codes(result));
            Assert.Equal(EventsIsolationFindings.BundleLayer, result.Findings[1].Layer);
            Assert.DoesNotContain("Secret", result.Findings[1].ToJson().ToString());
            Assert.Equal("IOException", result.Findings[1].Evidence["exceptionType"].ToString());
            Assert.Equal(EventsIsolationFindings.PinsSentence, new EventsIsolationRefusalException(result.Findings).Message);
        }

        [Fact]
        public void AnIoFailureFromTheCompleteAudit_IsNotMistakenForAnAuditVerdict()
        {
            var result = EventsIsolationFindings.Evaluate(Probe(new Spy(), p =>
            {
                p.LoadedLocations = name => new[] { @"C:\sdk\" + name + ".dll" };
                p.VerifyCompleteBundle = hashes => throw new IOException("sharing violation");
            }));

            Assert.Equal(new[] { "IsolationPreflightFailed" }, Codes(result));
        }

        [Fact]
        public void AnUnreadableDestination_FailsClosedWhenThePinsAreComplete()
        {
            var result = EventsIsolationFindings.Evaluate(Probe(new Spy(), p =>
                p.ReadDestination = () => throw new InvalidOperationException("no active version")));

            Assert.Equal(new[] { "WriteDestinationUnreadable" }, Codes(result));
            Assert.Equal(EventsIsolationFindings.DestinationLayer, result.Findings[0].Layer);
        }

        [Fact]
        public void AnUnreadableDestination_DoesNotChangeTheVerdictWhenAPinIsMissing()
        {
            var result = EventsIsolationFindings.Evaluate(Probe(new Spy(), p =>
            {
                p.ExpectedVersion = null;
                p.ReadDestination = () => throw new InvalidOperationException("no active version");
            }));

            Assert.Equal(new[] { "WriteDestinationConfigurationIncomplete" }, Codes(result));
        }

        [Fact]
        public void PartsPhase_NamesAnUnverifiedPart_AndAMissingEventsPartSeparately()
        {
            var extension = EventsIsolationFindings.Evaluate(Probe(new Spy(), p =>
                p.PartTypes = () => new[] { typeof(EventsPartStub), typeof(ExtensionPartStub) }));
            var noEvents = EventsIsolationFindings.Evaluate(Probe(new Spy(), p =>
                p.PartTypes = () => new[] { typeof(AllowedPartStub) }));

            Assert.Equal(new[] { "UnverifiedObjectPart" }, Codes(extension));
            Assert.Contains(typeof(ExtensionPartStub).FullName, extension.Findings[0].Message);
            Assert.Equal(new[] { "UnverifiedObjectPart" }, Codes(noEvents));
            Assert.Contains("no Events part", noEvents.Findings[0].Message);
        }

        [Fact]
        public void PartsPhase_AFailureToEnumerateThePartsIsAFindingNotAnEscape()
        {
            var result = EventsIsolationFindings.Evaluate(Probe(new Spy(), p =>
                p.PartTypes = () => throw new NullReferenceException()));

            Assert.Equal(new[] { "IsolationPreflightFailed" }, Codes(result));
            Assert.Equal(EventsIsolationFindings.ObjectLayer, result.Findings[0].Layer);
        }

        [Fact]
        public void CallbackPhase_AnUnverifiedCallbackIsNamed_AndAnyOtherFailureIsUnclassified()
        {
            var named = EventsIsolationFindings.Evaluate(Probe(new Spy(), p =>
                p.InspectCallbacks = () => throw new InvalidOperationException(@"An unverified direct SDK callback is attached to X at C:\Secret\x.dll")));
            var other = EventsIsolationFindings.Evaluate(Probe(new Spy(), p =>
                p.InspectCallbacks = () => throw new TargetInvocationException(new Exception("inner"))));

            Assert.Equal(new[] { "UnverifiedSdkCallback" }, Codes(named));
            Assert.DoesNotContain("Secret", named.Findings[0].Message);
            Assert.Equal(new[] { "IsolationPreflightFailed" }, Codes(other));
            Assert.Equal(EventsIsolationFindings.CallbackLayer, other.Findings[0].Layer);
        }

        [Fact]
        public void ACompleteBundleVerification_ThatThrowsAnUnexpectedException_BecomesIsolationPreflightFailed()
        {
            var result = EventsIsolationFindings.Evaluate(Probe(new Spy(), p =>
            {
                p.LoadedLocations = name => new[] { @"C:\sdk\" + name + ".dll" };
                p.VerifyCompleteBundle = hashes => throw new System.IO.IOException("Disk read failed");
            }));

            Assert.Contains("IsolationPreflightFailed", Codes(result));
            Assert.Contains("IOException", result.Findings.First(f => f.Code == "IsolationPreflightFailed").Message);
        }

        [Fact]
        public void AnEvaluationNeverThrows_EvenWhenEveryProbeFails()
        {
            var result = EventsIsolationFindings.Evaluate(Probe(new Spy(), p =>
            {
                p.ReadDestination = () => throw new InvalidOperationException("a");
                p.LoadedLocations = name => throw new InvalidOperationException("b");
                p.PartTypes = () => throw new InvalidOperationException("c");
                p.InspectCallbacks = () => throw new Exception("d");
            }));

            Assert.Equal(
                new[] { "WriteDestinationUnreadable", "IsolationPreflightFailed", "IsolationPreflightFailed", "IsolationPreflightFailed" },
                Codes(result));
        }

        [Fact]
        public void ARefusalFromAnUnexpectedException_WithholdsTheLocalPath()
        {
            var report = EventsSaveIsolation.DescribeRefusal(new IOException(@"Could not open C:\Secret\Dir\x.dll"));

            Assert.DoesNotContain("Secret", report.ToString());
            Assert.Equal("IsolationPreflightFailed", report["reasonCode"].ToString());
        }

        [Fact]
        public void TheListedAuditedBundleIds_AreExactlyTheIdsTheMatcherCanReturn()
        {
            // The ids shown in a refusal are hard-coded beside MatchAuditedBuild; this keeps them from drifting apart.
            var sets = new[] { "AuditedAssemblies", "UpdatedU16Assemblies" }
                .Select(field => new Dictionary<string, string>((Dictionary<string, string>)typeof(EventsSaveIsolation)
                    .GetField(field, BindingFlags.Static | BindingFlags.NonPublic).GetValue(null)))
                .Concat(EventsBundleAudits.All.Select(audit => audit.CriticalHashes))
                .ToArray();

            var returned = sets.Select(EventsSaveIsolation.MatchAuditedBuild).ToArray();

            Assert.All(returned, id => Assert.NotNull(id));
            Assert.Equal(returned.OrderBy(id => id), EventsSaveIsolation.AuditedBundleIds.OrderBy(id => id));
        }
    }
}
