using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Newtonsoft.Json.Linq;
using Xunit;
using static GxMcp.Gateway.Tests.RecoveryReadFixtures;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// A fence-covered <c>genexus_read</c> carries the Worker's pre-cut certificate, and the
    /// Gateway decides on it before the wire guards cut or replace the part. Synthetic
    /// throughout: the Worker payload is shaped by hand and run through the real
    /// <c>TruncateResponseIfNeeded</c> and <see cref="ResponseSizeGuard"/>.
    /// </summary>
    public class RecoveryReadEmbeddedCertificateTests
    {
        private static JObject WorkerRead(string text, int lines, JObject? certificate, int offset = 0, bool paginated = false)
        {
            var payload = new JObject
            {
                ["name"] = ObjectName,
                ["part"] = PartName,
                ["source"] = text,
                ["offset"] = offset,
                ["limit"] = lines,
                ["totalLines"] = lines,
                ["totalBytes"] = Encoding.UTF8.GetByteCount(text),
                ["truncated"] = paginated,
                ["isTruncatedByWorker"] = paginated,
                ["explicitFullRead"] = true,
                ["versionToken"] = "r1:root:" + Sha256(text)
            };
            if (certificate != null) payload["recoveryCertificate"] = certificate;
            return payload;
        }

        private static JToken ThroughWireGuards(JObject workerPayload, JObject readArgs)
        {
            var truncate = typeof(Program).GetMethod("TruncateResponseIfNeeded", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(truncate);
            var shaped = (JToken)truncate!.Invoke(null, new object?[] { workerPayload.DeepClone(), "genexus_read" })!;
            return shaped is JObject shapedObject
                ? new ResponseSizeGuard().Apply(shapedObject, "genexus_read", readArgs).result
                : shaped;
        }

        private static JObject ReadArgs(string name = ObjectName, string? part = PartName)
        {
            var args = new JObject { ["name"] = name, ["limit"] = 0 };
            if (part != null) args["part"] = part;
            return args;
        }

        /// <summary>The decision <c>genexus_read</c> takes: certificate captured first, then the wire guards.</summary>
        private static int Decide(MutationRecoveryRegistry registry, JObject readArgs, JObject workerPayload,
            Func<JObject, JObject?>? afterObserve = null)
        {
            var covered = Program.FindCoveredRecoveryReads(registry, Kb, readArgs);
            JObject? certificate = Program.ExtractRecoveryCertificate(workerPayload);
            var final = ThroughWireGuards(workerPayload, readArgs);
            afterObserve?.Invoke(workerPayload);
            return Program.ConfirmRecoveryReadWithCertificate(registry, Kb, final, certificate, covered);
        }

        private static MutationRecoveryRegistry Fenced(string op = "op-1", string name = ObjectName, string part = PartName)
        {
            var registry = new MutationRecoveryRegistry();
            registry.RequireRead(Kb, name, part, op);
            return registry;
        }

        // ---- the part the Gateway has to cut ---------------------------------------

        [Theory]
        [InlineData(250000, 5000)]
        [InlineData(400000, 8000)]
        public void PartCutByTheGatewayIsClearedByItsCertificateAndOnlyByIt(int chars, int lines)
        {
            string text = SyntheticPart(chars, lines);
            var args = ReadArgs();

            // Without the certificate the cut read is not evidence: the old limit holds.
            var withoutCertificate = Fenced();
            Assert.Equal(0, Decide(withoutCertificate, args, WorkerRead(text, lines, certificate: null)));
            Assert.Equal(1, withoutCertificate.Count);

            var withCertificate = Fenced();
            var worker = WorkerRead(text, lines, Certificate(text, lines));
            Assert.True(((JObject)ThroughWireGuards(worker, args))["truncatedByGateway"]!.Value<bool>());

            Assert.Equal(1, Decide(withCertificate, args, worker));
            Assert.Equal(0, withCertificate.Count);
        }

        [Fact]
        public void PartBelowTheGatewayBudgetIsClearedOnceNotTwice()
        {
            string text = SyntheticPart(81500, 2306);
            var registry = Fenced();
            var args = ReadArgs();

            Assert.Equal(1, Decide(registry, args, WorkerRead(text, 2306, Certificate(text, 2306))));
            Assert.Equal(0, registry.Count);
        }

        [Fact]
        public void MultiByteSourceThatTheSizeGuardReplacesNeverClears()
        {
            // ~150k characters (inside the Gateway's own 200k budget) that encode to more
            // than the guard's byte cap: the caller would receive only the sentinel, so
            // only the typed action may clear.
            string text = string.Concat(System.Linq.Enumerable.Repeat(new string('日', 100) + "\n", 1500));
            var args = ReadArgs();
            var worker = WorkerRead(text, 1500, Certificate(text, 1500));
            var final = ThroughWireGuards(worker, args);
            Assert.True(Program.IsSizeGuardSentinel(final), "fixture must trip the size guard");

            var registry = Fenced();
            Assert.Equal(0, Decide(registry, args, worker));
            Assert.Equal(1, registry.Count);
        }

        [Fact]
        public void SentinelNeverClearsEvenIfItCarriesTheCertifiedVersion()
        {
            // The real sentinel has no versionToken, so the version guard alone would also
            // refuse it. This one is built to pass that guard, so only the explicit sentinel
            // check can: a response the caller cannot act on is not a recovery read.
            string text = SyntheticPart(3000, 60);
            var certificate = Certificate(text, 60);
            var sentinel = new JObject
            {
                ["versionToken"] = certificate["versionToken"],
                ["_meta"] = new JObject { ["truncated"] = new JObject { ["reason"] = "response_exceeded_cap" } }
            };
            var registry = Fenced();
            var covered = Program.FindCoveredRecoveryReads(registry, Kb, ReadArgs());

            Assert.True(Program.IsSizeGuardSentinel(sentinel));
            Assert.Equal(0, Program.ConfirmRecoveryReadWithCertificate(registry, Kb, sentinel, certificate, covered));
            Assert.Equal(1, registry.Count);
        }

        // ---- coverage and version ---------------------------------------------------

        [Fact]
        public void PaginatedReadWithAnIncompleteCertificateNeverClears()
        {
            string text = SyntheticPart(250000, 5000);
            var incomplete = Certificate(text, 5000).With("complete", false).With("coverage", "sdk-partial");
            incomplete["reason"] = "window-does-not-cover-part";
            var registry = Fenced();
            var args = ReadArgs();
            args["offset"] = 100;
            args.Remove("limit");

            Assert.Equal(0, Decide(registry, args, WorkerRead(text, 5000, incomplete, offset: 100, paginated: true)));
            Assert.Equal(1, registry.Count);
        }

        [Fact]
        public void ReadThatRacedAWriteIsVetoedEvenIfThePageLooksComplete()
        {
            string text = SyntheticPart(3000, 60);
            var raced = Certificate(text, 60).With("complete", false);
            raced["reason"] = "changed-during-read";
            var registry = Fenced();

            // The page itself passes the old check; the Worker's own evidence outranks it.
            Assert.True(Program.IsCompleteMutationRecoveryRead(WorkerRead(text, 60, null)));
            Assert.Equal(0, Decide(registry, ReadArgs(), WorkerRead(text, 60, raced)));
            Assert.Equal(1, registry.Count);
        }

        [Fact]
        public void CertificateOfAnotherVersionThanThePageTheCallerReceivedNeverClears()
        {
            // The page came from a Worker cache the fresh certification read has outdated:
            // the caller would edit from a versionToken that is not the one certified.
            string text = SyntheticPart(250000, 5000);
            var worker = WorkerRead(text, 5000, Certificate(text, 5000, revision: "r9:root"));
            var registry = Fenced();

            Assert.Equal(0, Decide(registry, ReadArgs(), worker));
            Assert.Equal(1, registry.Count);

            // Same version on both sides clears (the case this guard must not break).
            var current = Fenced();
            Assert.Equal(1, Decide(current, ReadArgs(), WorkerRead(text, 5000, Certificate(text, 5000))));
        }

        [Fact]
        public void CompleteCertificateOfAnotherVersionThanThePageVetoesTheCompletePageToo()
        {
            // The page is complete and would clear the fence on its own; the fresh certificate
            // says the part is at another version, so the page the caller holds is outdated.
            string text = SyntheticPart(3000, 60);
            var certificate = Certificate(text, 60, revision: "r9:root");
            var registry = Fenced();
            var page = ThroughWireGuards(WorkerRead(text, 60, null), ReadArgs());
            Assert.True(Program.IsCompleteMutationRecoveryRead(page));

            Assert.Equal(0, Program.ConfirmRecoveryReadWithCertificate(
                registry, Kb, page, certificate, Program.FindCoveredRecoveryReads(registry, Kb, ReadArgs())));
            Assert.Equal(1, registry.Count);

            // Without a certificate the page decides, as before.
            Assert.Equal(1, Program.ConfirmRecoveryReadWithCertificate(
                registry, Kb, page, certificate: null, Program.FindCoveredRecoveryReads(registry, Kb, ReadArgs())));
        }

        [Fact]
        public void PageWithoutAVersionTokenGivesTheCallerNothingToEditFromSoTheCertificateDoesNotClear()
        {
            string text = SyntheticPart(250000, 5000);
            var certificate = Certificate(text, 5000);
            var worker = WorkerRead(text, 5000, certificate);
            worker.Remove("versionToken");
            var registry = Fenced();
            var args = ReadArgs();
            var page = ThroughWireGuards(worker, args);

            Assert.Equal(0, Program.ConfirmRecoveryReadWithCertificate(
                registry, Kb, page, certificate, Program.FindCoveredRecoveryReads(registry, Kb, args)));
            Assert.Equal(1, registry.Count);
        }

        [Fact]
        public void ReadThatRacedAWriteIsVetoedEvenWhenAnEarlierRefusalHidesTheReason()
        {
            string text = SyntheticPart(3000, 60);
            var raced = Certificate(text, 60).With("complete", false).With("stable", false);
            raced["reason"] = "window-does-not-cover-part";
            var registry = Fenced();
            var page = ThroughWireGuards(WorkerRead(text, 60, null), ReadArgs());

            Assert.Equal(0, Program.ConfirmRecoveryReadWithCertificate(
                registry, Kb, page, raced, Program.FindCoveredRecoveryReads(registry, Kb, ReadArgs())));
            Assert.Equal(1, registry.Count);
        }

        [Fact]
        public void CoveredReadDescribesEachFenceAndWhatTheVersionSaysAboutTheTimedOutWrite()
        {
            string text = SyntheticPart(250000, 5000);
            var certificate = Certificate(text, 5000);
            var registry = new MutationRecoveryRegistry();
            registry.RequireRead(Kb, ObjectName, PartName, "op-landed", null, null, null, null,
                "r2:root:" + Sha256("before the write"));
            registry.RequireRead(Kb, "OtherPanel", PartName, "op-other");
            var worker = WorkerRead(text, 5000, certificate);
            var args = ReadArgs();
            var covered = Program.FindCoveredRecoveryReads(registry, Kb, args);
            var final = ThroughWireGuards(worker, args);

            Program.ConfirmRecoveryReadWithCertificate(registry, Kb, final, certificate, covered);
            var described = Program.DescribeRecoveryFences(registry, final, certificate, covered);

            var fence = Assert.Single(described);
            Assert.Equal("op-landed", fence["operationId"]!.ToString());
            Assert.True(fence["cleared"]!.Value<bool>());
            Assert.Equal("changed", fence["timedOutWrite"]!.ToString());
            Assert.True(fence["certificateComplete"]!.Value<bool>());
            Assert.Equal(5000, fence["totalLines"]!.Value<int>());
            Assert.Equal(Sha256(text), fence["hash"]!.ToString());
            Assert.Equal(1, registry.Count); // the other object's fence is untouched
        }

        [Fact]
        public void FenceThatStaysIsDescribedAsNotCleared()
        {
            string text = SyntheticPart(3000, 60);
            var registry = Fenced();
            var args = ReadArgs();
            args["offset"] = 10;
            var covered = Program.FindCoveredRecoveryReads(registry, Kb, args);
            var final = ThroughWireGuards(WorkerRead(text, 60, null, offset: 10, paginated: true), args);

            var described = Program.DescribeRecoveryFences(registry, final, null, covered);

            Assert.False(Assert.Single(described)["cleared"]!.Value<bool>());
        }

        // ---- scopes -----------------------------------------------------------------

        [Fact]
        public void FolderAliasIsAnEquivalentScopeUnlessAnotherKbOwnsIt()
        {
            string path = @"C:\KBs\SyntheticKb";
            var none = Array.Empty<(string Alias, string? Path)>();

            Assert.Equal(new[] { "handle", "synthetickb" }, Program.EquivalentKbScopes("handle", path + "\\", none));
            // The KB's own alias already is the folder name: nothing to add.
            Assert.Equal(new[] { "synthetickb" }, Program.EquivalentKbScopes("synthetickb", path, none));
            // Another KB (another path) owns that alias: its fences must never be taken as this KB's.
            var collision = new[] { ("SyntheticKb", (string?)@"C:\Other\Place") };
            Assert.Equal(new[] { "handle" }, Program.EquivalentKbScopes("handle", path, collision));
            // The same KB known under the folder alias is not a collision.
            var sameKb = new[] { ("synthetickb", (string?)path) };
            Assert.Equal(new[] { "handle", "synthetickb" }, Program.EquivalentKbScopes("handle", path, sameKb));
            Assert.Empty(Program.EquivalentKbScopes(null, null, none));
            Assert.Equal(new[] { "handle" }, Program.EquivalentKbScopes("handle", null, none));
        }

        [Fact]
        public void CoveredReadsAreFoundUnderEveryEquivalentScope()
        {
            var registry = new MutationRecoveryRegistry();
            registry.RequireRead("folder-alias", ObjectName, PartName, "op-legacy");

            Assert.Empty(Program.FindCoveredRecoveryReads(registry, "handle-alias", ReadArgs()));
            var covered = Program.FindCoveredRecoveryReads(registry, new[] { "handle-alias", "folder-alias" }, ReadArgs());

            Assert.Equal("op-legacy", Assert.Single(covered).Observed!.OperationId);
        }

        [Fact]
        public void RegistryTellsAClearedFenceFromOneThatWasRearmed()
        {
            var registry = new MutationRecoveryRegistry();
            registry.RequireRead(Kb, ObjectName, PartName, "op-old");
            Assert.True(registry.TryGet(Kb, ObjectName, PartName, out var observed));
            Assert.True(registry.IsStillFenced(observed));

            registry.RequireRead(Kb, ObjectName, PartName, "op-new");
            Assert.False(registry.IsStillFenced(observed));

            Assert.True(registry.TryGet(Kb, ObjectName, PartName, out var current));
            Assert.True(registry.ConfirmRead(Kb, ObjectName, PartName, current));
            Assert.False(registry.IsStillFenced(current));
        }

        [Fact]
        public void CertificateWithAHashThatDoesNotMatchItsVersionNeverClears()
        {
            string text = SyntheticPart(250000, 5000);
            var forged = Certificate(text, 5000).With("sha256", Sha256("other bytes"));
            var registry = Fenced();

            Assert.Equal(0, Decide(registry, ReadArgs(), WorkerRead(text, 5000, forged)));
            Assert.Equal(1, registry.Count);
        }

        [Fact]
        public void FenceRearmedAfterTheReadWasObservedIsNotClearedByItsCertificate()
        {
            string text = SyntheticPart(250000, 5000);
            var registry = Fenced("op-old");
            var args = ReadArgs();
            var worker = WorkerRead(text, 5000, Certificate(text, 5000));

            int cleared = Decide(registry, args, worker, _ =>
            {
                // A second timeout re-arms the part between observing the fence and deciding.
                registry.RequireRead(Kb, ObjectName, PartName, "op-new");
                return null;
            });

            Assert.Equal(0, cleared);
            Assert.True(registry.TryGet(Kb, ObjectName, PartName, out var survivor));
            Assert.Equal("op-new", survivor.OperationId);
        }

        // ---- only the matching fence ------------------------------------------------

        [Fact]
        public void CertificateClearsOnlyTheFenceOfTheObjectPartAndIdentityItDescribes()
        {
            string text = SyntheticPart(250000, 5000);
            var registry = Fenced("op-target");
            registry.RequireRead(Kb, "OtherPanel", PartName, "op-other-object");
            registry.RequireRead(Kb, ObjectName, "Rules", "op-other-part");
            registry.RequireRead(Kb, ObjectName, PartName, "op-other-type", null, null, "Procedure", null, null);

            Assert.Equal(1, Decide(registry, ReadArgs(), WorkerRead(text, 5000, Certificate(text, 5000))));

            Assert.Equal(3, registry.Count);
            Assert.False(registry.TryGet(Kb, ObjectName, PartName, out var named) && named.OperationId == "op-target");
            Assert.True(registry.TryGet(Kb, "OtherPanel", PartName, out _));
            Assert.True(registry.TryGet(Kb, ObjectName, "Rules", out _));
        }

        [Fact]
        public void CertificateOfAnotherObjectClearsNothing()
        {
            string text = SyntheticPart(250000, 5000);
            var otherObject = Certificate(text, 5000, name: "OtherPanel", guid: "11111111-2222-3333-4444-555555555555");
            var registry = Fenced();

            Assert.Equal(0, Decide(registry, ReadArgs(), WorkerRead(text, 5000, otherObject)));
            Assert.Equal(1, registry.Count);
        }

        [Fact]
        public void ReadWithNoFenceInScopeNeverConsultsTheCertificate()
        {
            var registry = new MutationRecoveryRegistry();
            string text = SyntheticPart(3000, 60);

            Assert.Equal(0, Decide(registry, ReadArgs(), WorkerRead(text, 60, Certificate(text, 60))));
        }

        // ---- plumbing ---------------------------------------------------------------

        [Fact]
        public void ConditionalReadThatOmittedTheBodyIsNeverEvidence()
        {
            var notModified = new JObject
            {
                ["notModified"] = true, ["part"] = PartName, ["versionToken"] = "r1:abc",
                ["offset"] = 0, ["truncated"] = false
            };

            Assert.False(Program.IsCompleteMutationRecoveryRead(notModified));
            var registry = Fenced();
            Assert.Equal(0, Program.ConfirmRecoveryReadWithCertificate(
                registry, Kb, notModified, certificate: null,
                Program.FindCoveredRecoveryReads(registry, Kb, ReadArgs())));
            Assert.Equal(1, registry.Count);
        }

        [Fact]
        public void RecoveryFlagsReachTheWorkerUnderTheNamesItReads()
        {
            // The routed command is nested whole under `params`, which is what the Worker
            // receives as its arguments; a flag under any other spelling is never read.
            var routed = new JObject { ["module"] = "Read", ["action"] = "ExtractSource", ["target"] = ObjectName,
                ["requireAuthoritativeRead"] = false };

            Program.ApplyRecoveryReadFlags(routed);
            var rpc = Program.BuildWorkerRpcRequest(routed, "request-1");

            var workerArgs = (JObject)rpc["params"]!;
            Assert.True(workerArgs["requireAuthoritativeRead"]!.Value<bool>());
            Assert.True(workerArgs["certifyRecovery"]!.Value<bool>());
            Assert.Null(workerArgs["_requireAuthoritativeRead"]);
            Assert.Null(workerArgs["_certifyRecovery"]);
        }

        [Fact]
        public void CertificateIsTakenFromTheWorkerPayloadBeforeAnyGuardCouldDropIt()
        {
            string text = SyntheticPart(250000, 5000);
            var worker = WorkerRead(text, 5000, Certificate(text, 5000));

            Assert.NotNull(Program.ExtractRecoveryCertificate(worker));
            // Taken out of the payload: it must not add bytes to a response near the budget.
            Assert.Null(worker["recoveryCertificate"]);
            Assert.Null(Program.ExtractRecoveryCertificate(WorkerRead(text, 5000, null)));
            Assert.Null(Program.ExtractRecoveryCertificate(new JValue("text")));
            Assert.Null(Program.ExtractRecoveryCertificate(null));
        }

        [Fact]
        public void BlockedEnvelopeNamesTheCertifiedRecoveryAsANextStepForLargeParts()
        {
            var registry = new MutationRecoveryRegistry();
            registry.RequireRead(Kb, ObjectName, PartName, "op-1", ObjectGuid, null, "WebPanel", null, null);
            Assert.True(registry.TryGet(Kb, ObjectName, PartName, out var fence));

            var envelope = MutationRecoveryRegistry.BuildBlockedEnvelope(fence);

            var error = (JObject)envelope["error"]!;
            Assert.Equal("PostTimeoutReadRequired", error["code"]!.ToString());
            Assert.Contains("recovery_read_complete", error["hint"]!.ToString());
            var steps = (JArray)error["nextSteps"]!;
            Assert.Equal(3, steps.Count);
            Assert.Equal("genexus_read", steps[0]["tool"]!.ToString());
            var recover = steps[1];
            Assert.Equal("genexus_connection_recover", recover["tool"]!.ToString());
            var args = (JObject)recover["args"]!;
            Assert.Equal("recovery_read_complete", args["action"]!.ToString());
            Assert.Equal(Kb, args["kb"]!.ToString());
            Assert.Equal(ObjectName, args["name"]!.ToString());
            Assert.Equal(PartName, args["part"]!.ToString());
            Assert.Equal("op-1", args["operationId"]!.ToString());
            Assert.Equal(ObjectGuid, args["guid"]!.ToString());
            Assert.Equal("WebPanel", args["type"]!.ToString());
            // Identity the fence did not record is absent, not null: the argument guard
            // would reject a null string.
            Assert.Null(args["entityKey"]);
            Assert.Null(args["path"]);
            // Inspect first: clearing the fence does not say the timed-out write landed.
            Assert.True(args["dryRun"]!.Value<bool>());
            Assert.Contains("timedOutWrite", recover["why"]!.ToString());
            // The suggested call is one the published schema accepts as it stands.
            var validation = GatewayArgsValidator.Validate("genexus_connection_recover", (JObject)args.DeepClone());
            Assert.True(validation.Ok, string.Join("; ", validation.Violations.ConvertAll(v => v.Path + " " + v.Actual)));
            var clear = steps[2];
            Assert.Equal("genexus_connection_recover", clear["tool"]!.ToString());
            var clearArgs = (JObject)clear["args"]!;
            Assert.False(clearArgs["dryRun"]!.Value<bool>());
            Assert.Equal(ObjectName, clearArgs["name"]!.ToString());
            Assert.Equal(ObjectGuid, clearArgs["guid"]!.ToString());
            Assert.Equal(PartName, clearArgs["part"]!.ToString());
            Assert.Equal("op-1", clearArgs["operationId"]!.ToString());
            Assert.Contains("does not", clear["why"]!.ToString());
            var clearValidation = GatewayArgsValidator.Validate("genexus_connection_recover", (JObject)clearArgs.DeepClone());
            Assert.True(clearValidation.Ok, string.Join("; ", clearValidation.Violations.ConvertAll(v => v.Path + " " + v.Actual)));
        }

        [Fact]
        public void PropertiesAndKbLevelFencesDoNotAdvertiseTheTextRecovery()
        {
            var registry = new MutationRecoveryRegistry();
            registry.RequireRead(Kb, ObjectName, "Properties", "op-props");
            registry.RequireRead(Kb, MutationRecoveryRegistry.KbRecoveryTarget, MutationRecoveryRegistry.KbRecoveryPart, "op-kb");
            Assert.True(registry.TryGet(Kb, ObjectName, "Properties", out var props));
            Assert.True(registry.TryGet(Kb, MutationRecoveryRegistry.KbRecoveryTarget, MutationRecoveryRegistry.KbRecoveryPart, out var kb));

            Assert.Single((JArray)MutationRecoveryRegistry.BuildBlockedEnvelope(props)["error"]!["nextSteps"]!);
            Assert.Empty((JArray)MutationRecoveryRegistry.BuildBlockedEnvelope(kb)["error"]!["nextSteps"]!);
        }
    }
}
