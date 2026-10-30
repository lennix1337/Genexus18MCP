using System;
using System.Collections.Generic;
using System.Linq;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // The isolated Events save control flow, driven with synthetic steps. No SDK
    // object, Knowledge Base or transaction exists here: the flow owns ordering,
    // rollback and what is reported, and that is exactly what these tests pin.
    public sealed class IsolatedEventsSaveFlowTests
    {
        internal sealed class FakeTextPosition
        {
            public int Line { get; set; }
            public int Char { get; set; }
        }

        // The shape of the SDK's IPosition when it is not a "TextPosition".
        internal sealed class FakePosition
        {
            public int Line { get; set; }
            public int Column { get; set; }
            public string FullName { get; set; }
            public int ObjectId { get; set; }
        }

        internal sealed class FakeSdkMessage
        {
            public string Text { get; set; }
            public string ErrorCode { get; set; }
            public string Level { get; set; }
            public object Position { get; set; }
            public List<object> Positions { get; set; }

            // The SDK's messages render as their text, which is what GetSdkMessages joins.
            public override string ToString() => Text;
        }

        // Shaped like Artech.Common.Diagnostics.ValidationException: the bare "Erro" is its
        // Message, the validation messages are on Output, and FullMessage renders them as text.
        internal sealed class FakeValidationException : Exception
        {
            public FakeValidationException(string message, IEnumerable<FakeSdkMessage> output, string fullMessage = null)
                : base(message)
            {
                Output = output == null ? null : new List<FakeSdkMessage>(output);
                FullMessage = fullMessage;
            }

            public List<FakeSdkMessage> Output { get; }
            public string FullMessage { get; }
        }

        // Shaped like an SDK part for the two things the diagnostics read: a
        // Messages collection and a Save() that fails with the bare "Erro".
        internal sealed class FakeSdkPart
        {
            public List<FakeSdkMessage> Messages { get; } = new List<FakeSdkMessage>();
            public void Save() => throw new Exception("Erro");
        }

        internal sealed class RecordingSteps : IIsolatedEventsSaveSteps
        {
            private readonly JObject _receipt;
            private readonly IsolatedEventsSaveTrace _trace;
            internal readonly List<string> Calls = new List<string>();
            internal readonly List<string> PhaseAtCall = new List<string>();
            internal readonly Dictionary<string, Exception> Faults = new Dictionary<string, Exception>();
            internal readonly Dictionary<string, Action> Behaviors = new Dictionary<string, Action>();
            internal readonly List<KeyValuePair<string, object>> Sources = new List<KeyValuePair<string, object>>();
            internal Exception SourcesFault;

            internal RecordingSteps(JObject receipt, IsolatedEventsSaveTrace trace)
            {
                _receipt = receipt;
                _trace = trace;
            }

            public string OriginalToken { get; set; } = "token-1";
            internal string FreshToken { get; set; } = "token-1";
            public bool WorkerRestartRequired { get; set; }
            public bool Verified { get; set; } = true;
            public string UnconfirmedMessage { get; set; }
            internal bool? VerifiedCommitted;

            private void Step(string name)
            {
                Calls.Add(name);
                PhaseAtCall.Add(_trace.Phase);
                if (Behaviors.TryGetValue(name, out var behavior)) behavior();
                if (Faults.TryGetValue(name, out var fault)) throw fault;
            }

            public void ReadOriginal() => Step("ReadOriginal");
            public void CaptureBaseline() => Step("CaptureBaseline");
            public void BeginTransaction() => Step("BeginTransaction");
            public string ReadFreshInTransaction() { Step("ReadFreshInTransaction"); return FreshToken; }
            public void RecheckTarget() => Step("RecheckTarget");
            public void AssignSource() { Step("AssignSource"); _receipt["writeAttempted"] = true; }
            public void SavePart() => Step("SavePart");
            public void SaveObject() => Step("SaveObject");
            public void EndSaveIsolation() => Step("EndSaveIsolation");
            public void VerifyPreCommit() => Step("VerifyPreCommit");
            public void Commit() => Step("Commit");
            public void Rollback() => Step("Rollback");
            public void EndTransaction() => Step("EndTransaction");
            public void RereadAfterCommit() => Step("RereadAfterCommit");
            public void VerifyAfterCommit(bool committed) { VerifiedCommitted = committed; Step("VerifyAfterCommit"); }
            public void Dispose() { }

            public IEnumerable<KeyValuePair<string, object>> SdkMessageSources()
            {
                Calls.Add("SdkMessageSources");
                PhaseAtCall.Add(_trace.Phase);
                if (SourcesFault != null) throw SourcesFault;
                return Sources;
            }
        }

        internal sealed class Gate
        {
            internal int Acquired;
            internal int Released;
            internal IDisposable Acquire() { Acquired++; return new Releaser(this); }
            private sealed class Releaser : IDisposable
            {
                private readonly Gate _gate;
                internal Releaser(Gate gate) { _gate = gate; }
                public void Dispose() { _gate.Released++; }
            }
        }

        private sealed class Harness
        {
            internal readonly JObject Receipt = new JObject();
            internal readonly IsolatedEventsSaveTrace Trace;
            internal readonly RecordingSteps Steps;
            internal readonly Gate Gate = new Gate();

            internal Harness()
            {
                Trace = new IsolatedEventsSaveTrace(Receipt, "SamplePanel");
                Steps = new RecordingSteps(Receipt, Trace);
            }

            internal IsolatedEventsSaveOutcome Run(string baseVersion = "token-1")
                => IsolatedEventsSaveFlow.Run(Steps, Trace, baseVersion, Gate.Acquire);
        }

        private static readonly string[] HappyPath =
        {
            "ReadOriginal", "CaptureBaseline", "BeginTransaction", "ReadFreshInTransaction", "RecheckTarget",
            "AssignSource", "SavePart", "SaveObject", "EndSaveIsolation", "VerifyPreCommit", "Commit",
            "EndTransaction", "RereadAfterCommit", "VerifyAfterCommit"
        };

        [Fact]
        public void HappyPath_RunsEveryStepOnceInOrder_AndCommits()
        {
            var h = new Harness();

            var outcome = h.Run();

            Assert.Equal(IsolatedEventsSaveOutcomeKind.Verified, outcome.Kind);
            Assert.Equal(HappyPath, h.Steps.Calls);
            Assert.True(h.Steps.VerifiedCommitted);
            Assert.Equal(1, h.Gate.Acquired);
            Assert.Equal(1, h.Gate.Released);
            Assert.Null(h.Receipt["transactionRolledBack"]);
        }

        [Fact]
        public void HappyPath_AnnouncesEachStageBeforeItRuns_AndEndsCompleted()
        {
            var h = new Harness();

            h.Run();

            var expected = new Dictionary<string, string>
            {
                ["ReadOriginal"] = "fresh-read",
                ["CaptureBaseline"] = "baseline-capture",
                ["BeginTransaction"] = "baseline-capture",
                ["ReadFreshInTransaction"] = "transaction-fresh-read",
                ["RecheckTarget"] = "target-recheck",
                ["AssignSource"] = "source-assignment",
                ["SavePart"] = "part-save",
                ["SaveObject"] = "object-save",
                ["EndSaveIsolation"] = "object-save",
                ["VerifyPreCommit"] = "pre-commit-verification",
                ["Commit"] = "commit",
                ["EndTransaction"] = "post-commit-reread",
                ["RereadAfterCommit"] = "post-commit-reread",
                ["VerifyAfterCommit"] = "post-commit-verification"
            };
            for (int i = 0; i < h.Steps.Calls.Count; i++)
                Assert.Equal(expected[h.Steps.Calls[i]], h.Steps.PhaseAtCall[i]);
            Assert.Equal("completed", h.Receipt["savePhase"]?.ToString());
            Assert.Null(h.Receipt["verificationPhase"]);
        }

        public static IEnumerable<object[]> FailingSteps => new[]
        {
            // step that throws, phase it must be reported under, whether the flow handles it inside the transaction
            new object[] { "ReadOriginal", "fresh-read", false },
            new object[] { "CaptureBaseline", "baseline-capture", false },
            new object[] { "BeginTransaction", "baseline-capture", false },
            new object[] { "ReadFreshInTransaction", "transaction-fresh-read", true },
            new object[] { "RecheckTarget", "target-recheck", true },
            new object[] { "AssignSource", "source-assignment", true },
            new object[] { "SavePart", "part-save", true },
            new object[] { "SaveObject", "object-save", true },
            new object[] { "EndSaveIsolation", "object-save", true },
            new object[] { "VerifyPreCommit", "pre-commit-verification", true },
            new object[] { "Commit", "commit", true },
            new object[] { "EndTransaction", "post-commit-reread", false },
            new object[] { "RereadAfterCommit", "post-commit-reread", false },
            new object[] { "VerifyAfterCommit", "post-commit-verification", false }
        };

        [Theory]
        [MemberData(nameof(FailingSteps))]
        public void FailureAtAnyStep_IsReportedUnderThatStage_WithTheTypedDiagnostics(string failingStep, string phase, bool insideTransaction)
        {
            var h = new Harness { };
            h.Steps.Verified = false;
            h.Steps.Faults[failingStep] = new InvalidOperationException("synthetic cause");

            var outcome = h.Run();

            Assert.Equal(insideTransaction ? IsolatedEventsSaveOutcomeKind.NotConfirmed : IsolatedEventsSaveOutcomeKind.Interrupted, outcome.Kind);
            Assert.Equal(phase, outcome.Phase);
            Assert.Equal(phase, h.Receipt["savePhase"]?.ToString());
            Assert.Equal(typeof(InvalidOperationException).FullName, h.Receipt["exceptionType"]?.ToString());
            Assert.Contains(phase, outcome.Message);
            Assert.Contains("synthetic cause", outcome.Message);
            Assert.NotNull(h.Receipt["innerMessages"]);
            Assert.NotNull(h.Receipt["diagnostics"]);
            Assert.NotNull(h.Receipt["sdkMessages"]);
            Assert.NotNull(h.Receipt["sourceValidationErrors"]);
            if (insideTransaction)
            {
                Assert.True(h.Receipt["transactionRolledBack"]?.Value<bool>());
                Assert.Contains("Rollback", h.Steps.Calls);
            }
            Assert.Equal(1, h.Gate.Released);
        }

        [Fact]
        public void SdkSaveRaisingBareErro_ReportsTheSdkCause_AndKeepsTheOriginalError()
        {
            var h = new Harness();
            h.Steps.Verified = false;
            var part = new FakeSdkPart();
            part.Messages.Add(new FakeSdkMessage
            {
                Text = "src0059: Esperando 'EndFor'",
                Position = new FakeTextPosition { Line = 12, Char = 4 }
            });
            h.Steps.Sources.Add(new KeyValuePair<string, object>("Events", part));
            h.Steps.Behaviors["SavePart"] = part.Save;

            var outcome = h.Run();

            Assert.Equal(IsolatedEventsSaveOutcomeKind.NotConfirmed, outcome.Kind);
            Assert.Equal("Isolated Events save failed during part-save: src0059: Esperando 'EndFor' (line 12, column 4)", outcome.Message);
            Assert.Equal("part-save", h.Receipt["savePhase"]?.ToString());
            Assert.Equal("Erro", h.Receipt["originalError"]?.ToString());
            Assert.Equal(typeof(Exception).FullName, h.Receipt["exceptionType"]?.ToString());
            var sdk = (JObject)((JArray)h.Receipt["sdkMessages"]).Single();
            Assert.Equal("src0059", sdk["code"]?.ToString());
            Assert.Equal("Events", sdk["part"]?.ToString());
            Assert.Equal(12, sdk["rawLine"]?.Value<int>());
            Assert.Equal(4, sdk["column"]?.Value<int>());
            Assert.Equal("Error", sdk["severity"]?.ToString());
            Assert.Equal("src0059: Esperando 'EndFor'", sdk["message"]?.ToString());
            var sourceErrors = (JArray)h.Receipt["sourceValidationErrors"];
            Assert.Single(sourceErrors);
            Assert.Equal("src0059", sourceErrors[0]["code"]?.ToString());
            Assert.True(h.Receipt["transactionRolledBack"]?.Value<bool>());
            Assert.DoesNotContain("Commit", h.Steps.Calls);
            Assert.Equal(1, h.Steps.Calls.Count(c => c == "SavePart"));
            Assert.Equal(0, h.Steps.Calls.Count(c => c == "SaveObject"));
        }

        [Fact]
        public void PartSaveRaisingAValidationExceptionWithOutput_ReturnsTheDiagnosticsOfTheRealFailure()
        {
            // What the real SDK does for an unknown function in the Events source: the part save
            // throws a ValidationException whose Message is the bare "Erro" and whose Output
            // carries one error per call, with the code apart from the text and a position.
            var h = new Harness();
            h.Steps.Verified = false;
            h.Steps.Faults["SavePart"] = new FakeValidationException("Erro", new[]
            {
                new FakeSdkMessage { ErrorCode = "src0294", Text = "Unknown function 'SetEmpty'", Position = new FakePosition { Line = 2, Column = 16 } },
                new FakeSdkMessage { ErrorCode = "src0294", Text = "Unknown function 'SetEmpty'", Position = new FakePosition { Line = 3, Column = 16 } },
                new FakeSdkMessage { ErrorCode = "src0294", Text = "Unknown function 'SetEmpty'", Position = new FakePosition { Line = 4, Column = 16 } }
            });

            var outcome = h.Run();

            Assert.Equal(IsolatedEventsSaveOutcomeKind.NotConfirmed, outcome.Kind);
            Assert.Equal(
                "Isolated Events save failed during part-save: src0294: Unknown function 'SetEmpty' (line 2, column 16)"
                + " | src0294: Unknown function 'SetEmpty' (line 3, column 16)"
                + " | src0294: Unknown function 'SetEmpty' (line 4, column 16)",
                outcome.Message);
            Assert.Equal("Erro", h.Receipt["originalError"]?.ToString());
            Assert.Equal(typeof(FakeValidationException).FullName, h.Receipt["exceptionType"]?.ToString());
            var diagnostics = (JArray)h.Receipt["diagnostics"];
            Assert.Equal(3, diagnostics.Count);
            Assert.Equal(new[] { 2, 3, 4 }, diagnostics.Select(d => d["rawLine"].Value<int>()).ToArray());
            Assert.All(diagnostics, d => Assert.Null(d["line"]));
            Assert.All(diagnostics, d =>
            {
                Assert.Equal("src0294", d["code"]?.ToString());
                Assert.Equal("Unknown function 'SetEmpty'", d["message"]?.ToString());
                Assert.Equal("SamplePanel", d["object"]?.ToString());
                Assert.Equal("Events", d["part"]?.ToString());
                Assert.Equal(16, d["column"]?.Value<int>());
            });
            Assert.Equal(3, ((JArray)h.Receipt["sourceValidationErrors"]).Count);
            Assert.Equal(3, ((JArray)h.Receipt["sdkMessages"]).Count);
            Assert.True(h.Receipt["transactionRolledBack"]?.Value<bool>());
            Assert.DoesNotContain("Commit", h.Steps.Calls);
        }

        [Fact]
        public void ObjectSaveRaisingAValidationExceptionWithOutput_AttributesTheMessagesToTheObject()
        {
            var h = new Harness();
            h.Steps.Verified = false;
            h.Steps.Faults["SaveObject"] = new FakeValidationException("Erro", new[]
            {
                new FakeSdkMessage { ErrorCode = "spc0001", Text = "Rule is not valid" }
            });

            h.Run();

            var diagnostic = (JObject)((JArray)h.Receipt["diagnostics"]).Single();
            Assert.Equal("object-save", h.Receipt["savePhase"]?.ToString());
            Assert.Equal("Object", diagnostic["part"]?.ToString());
            Assert.Null(diagnostic["rawLine"]);
            Assert.Empty((JArray)h.Receipt["sourceValidationErrors"]);
        }

        [Fact]
        public void ObjectSaveRaisingBareErro_IsAttributedToTheObjectSave_NotThePartSave()
        {
            var h = new Harness();
            h.Steps.Verified = false;
            h.Steps.Faults["SaveObject"] = new Exception("Erro");

            var outcome = h.Run();

            Assert.Equal("object-save", h.Receipt["savePhase"]?.ToString());
            Assert.Equal(1, h.Steps.Calls.Count(c => c == "SavePart"));
            Assert.DoesNotContain("Commit", h.Steps.Calls);
            // No SDK message and no inner cause: the report is still not the bare "Erro".
            Assert.StartsWith("Isolated Events save failed during object-save: ", outcome.Message);
            Assert.NotEqual("Erro", outcome.Message);
            Assert.Contains("generic error", outcome.Message);
            Assert.Equal("Erro", h.Receipt["originalError"]?.ToString());
            Assert.Empty((JArray)h.Receipt["sdkMessages"]);
        }

        [Fact]
        public void SdkMessagesAreReadBeforeTheRollback()
        {
            var h = new Harness();
            h.Steps.Verified = false;
            h.Steps.Faults["SaveObject"] = new Exception("Erro");

            h.Run();

            Assert.True(h.Steps.Calls.IndexOf("SdkMessageSources") >= 0);
            Assert.True(h.Steps.Calls.IndexOf("SdkMessageSources") < h.Steps.Calls.IndexOf("Rollback"));
        }

        [Fact]
        public void InformativeInnerException_ReplacesABareOuterErro()
        {
            var h = new Harness();
            h.Steps.Verified = false;
            h.Steps.Faults["SavePart"] = new InvalidOperationException("Erro", new ArgumentException("Events part rejected the assignment"));

            var outcome = h.Run();

            Assert.Equal("Isolated Events save failed during part-save: Events part rejected the assignment", outcome.Message);
            Assert.Equal(new[] { "Events part rejected the assignment" }, h.Receipt["innerMessages"].ToObject<string[]>());
            Assert.Equal(new[] { typeof(ArgumentException).FullName }, h.Receipt["innerExceptionTypes"].ToObject<string[]>());
            Assert.Equal("Erro", h.Receipt["originalError"]?.ToString());
        }

        [Fact]
        public void NonBareExceptionMessage_IsKeptAndNotReportedAsOriginal()
        {
            var h = new Harness();
            h.Steps.Verified = false;
            h.Steps.Faults["VerifyPreCommit"] = new InvalidOperationException("The pre-commit SDK object state did not match the requested isolated change.");

            var outcome = h.Run();

            Assert.Equal("Isolated Events save failed during pre-commit-verification: The pre-commit SDK object state did not match the requested isolated change.", outcome.Message);
            Assert.Null(h.Receipt["originalError"]);
            Assert.DoesNotContain("Commit", h.Steps.Calls);
        }

        [Fact]
        public void PhaseIsFrozenAtTheFailure_AndVerificationAfterTheRollbackIsReportedSeparately()
        {
            var h = new Harness();
            h.Steps.Verified = false;
            h.Steps.Faults["SaveObject"] = new Exception("Erro");

            var outcome = h.Run();

            Assert.Equal("object-save", h.Receipt["savePhase"]?.ToString());
            Assert.Equal("post-commit-verification", h.Receipt["verificationPhase"]?.ToString());
            Assert.Equal("object-save", outcome.Phase);
            Assert.Null(h.Receipt["verificationFailure"]);
        }

        [Fact]
        public void FailureWhileVerifyingAfterTheRollback_DoesNotOverwriteTheSaveFailure()
        {
            var h = new Harness();
            h.Steps.Verified = false;
            h.Steps.Faults["SavePart"] = new InvalidOperationException("part save refused");
            h.Steps.Faults["RereadAfterCommit"] = new InvalidOperationException("reread refused");

            var outcome = h.Run();

            Assert.Equal(IsolatedEventsSaveOutcomeKind.Interrupted, outcome.Kind);
            Assert.Equal("part-save", h.Receipt["savePhase"]?.ToString());
            Assert.Equal("part-save", outcome.Phase);
            Assert.Equal("post-commit-reread", h.Receipt["verificationPhase"]?.ToString());
            Assert.Equal("Isolated Events save failed during part-save: part save refused", h.Receipt["saveFailureMessage"]?.ToString());
            var nested = (JObject)h.Receipt["verificationFailure"];
            Assert.Equal("post-commit-reread", nested["savePhase"]?.ToString());
            Assert.Equal(typeof(InvalidOperationException).FullName, nested["exceptionType"]?.ToString());
            Assert.Equal(typeof(InvalidOperationException).FullName, h.Receipt["exceptionType"]?.ToString());
            Assert.Contains("interrupted during post-commit-reread", outcome.Message);
            Assert.Contains("reread refused", outcome.Message);
        }

        [Fact]
        public void SdkMessageCollectionFailure_DoesNotMaskTheOriginalFailure()
        {
            var h = new Harness();
            h.Steps.Verified = false;
            h.Steps.Faults["SavePart"] = new InvalidOperationException("part save refused");
            h.Steps.SourcesFault = new InvalidOperationException("messages unavailable");

            var outcome = h.Run();

            Assert.Equal("Isolated Events save failed during part-save: part save refused", outcome.Message);
            Assert.Equal("messages unavailable", h.Receipt["sdkMessagesError"]?.ToString());
            Assert.True(h.Receipt["transactionRolledBack"]?.Value<bool>());
        }

        [Fact]
        public void RollbackFailure_IsRecordedSanitized_AndVerificationStillRuns()
        {
            var h = new Harness();
            h.Steps.Verified = false;
            h.Steps.Faults["SaveObject"] = new Exception("Erro");
            h.Steps.Faults["Rollback"] = new InvalidOperationException(@"rollback refused for C:\Users\Jane Doe\kb\data.db");

            var outcome = h.Run();

            Assert.Equal(IsolatedEventsSaveOutcomeKind.NotConfirmed, outcome.Kind);
            Assert.Equal("rollback refused for <path>", h.Receipt["rollbackError"]?.ToString());
            Assert.Null(h.Receipt["transactionRolledBack"]);
            Assert.Contains("VerifyAfterCommit", h.Steps.Calls);
        }

        [Fact]
        public void UnconfirmedPersistenceWithoutAnException_ReportsTheDefaultInstructionAtTheVerification()
        {
            var h = new Harness();
            h.Steps.Verified = false;

            var outcome = h.Run();

            Assert.Equal(IsolatedEventsSaveOutcomeKind.NotConfirmed, outcome.Kind);
            Assert.Equal(IsolatedEventsSaveFlow.NotConfirmedMessage, outcome.Message);
            Assert.Equal("post-commit-verification", outcome.Phase);
            Assert.Equal("post-commit-verification", h.Receipt["savePhase"]?.ToString());
            Assert.Null(h.Receipt["exceptionType"]);
            Assert.True(h.Steps.VerifiedCommitted);
            Assert.Contains("Commit", h.Steps.Calls);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("token-2")]
        public void BaseVersionMismatchBeforeTheTransaction_ConflictsWithoutBaselineOrWrite(string baseVersion)
        {
            var h = new Harness();

            var outcome = h.Run(baseVersion);

            Assert.Equal(IsolatedEventsSaveOutcomeKind.VersionConflict, outcome.Kind);
            Assert.Equal("The Events version changed; no save was attempted.", outcome.Message);
            Assert.Equal("token-1", outcome.CurrentVersion);
            Assert.Equal("version-conflict-check", outcome.Phase);
            Assert.Equal(new[] { "ReadOriginal" }, h.Steps.Calls);
            Assert.Equal(1, h.Gate.Released);
        }

        [Fact]
        public void TokenChangedAtTheTransactionBoundary_RollsBackAndNeverAssignsTheSource()
        {
            var h = new Harness();
            h.Steps.FreshToken = "token-2";

            var outcome = h.Run();

            Assert.Equal(IsolatedEventsSaveOutcomeKind.VersionConflict, outcome.Kind);
            Assert.Equal("The Events version changed at the transaction boundary; no save was attempted.", outcome.Message);
            Assert.Equal("token-2", outcome.CurrentVersion);
            Assert.Equal("version-conflict-check", outcome.Phase);
            // The transaction is closed inside the write gate, before the conflict is reported.
            Assert.Equal(new[] { "ReadOriginal", "CaptureBaseline", "BeginTransaction", "ReadFreshInTransaction", "Rollback", "EndTransaction" }, h.Steps.Calls);
            Assert.Equal(1, h.Gate.Released);
        }

        [Fact]
        public void TransactionDisposeFailingAtTheConflictBoundary_IsAnInterruptionNotAConflict()
        {
            var h = new Harness();
            h.Steps.FreshToken = "token-2";
            h.Steps.Faults["EndTransaction"] = new InvalidOperationException("dispose refused");

            var outcome = h.Run();

            Assert.Equal(IsolatedEventsSaveOutcomeKind.Interrupted, outcome.Kind);
            Assert.Contains("dispose refused", outcome.Message);
            Assert.DoesNotContain("AssignSource", h.Steps.Calls);
        }

        [Fact]
        public void RollbackFailureAtTheConflictBoundary_IsAFailureOfTheVersionCheck()
        {
            var h = new Harness();
            h.Steps.FreshToken = "token-2";
            h.Steps.Verified = false;
            h.Steps.Faults["Rollback"] = new InvalidOperationException("rollback refused");

            var outcome = h.Run();

            Assert.Equal("version-conflict-check", h.Receipt["savePhase"]?.ToString());
            Assert.Equal(IsolatedEventsSaveOutcomeKind.NotConfirmed, outcome.Kind);
            Assert.DoesNotContain("AssignSource", h.Steps.Calls);
        }

        [Fact]
        public void InterruptionAfterTheCommit_ReportsUncertainPersistenceWithoutCompensation()
        {
            var h = new Harness();
            h.Steps.WorkerRestartRequired = true;
            h.Steps.Faults["RereadAfterCommit"] = new InvalidOperationException("fresh read failed");

            var outcome = h.Run();

            Assert.Equal(IsolatedEventsSaveOutcomeKind.Interrupted, outcome.Kind);
            Assert.Equal("SDK persistence or verification was interrupted during post-commit-reread: fresh read failed. No compensating write was performed.", outcome.Message);
            Assert.True(h.Receipt["objectSaved"]?.Value<bool>());
            Assert.False(h.Receipt["verificationCompleted"]?.Value<bool>());
            Assert.True(h.Receipt["persistenceUncertain"]?.Value<bool>());
            Assert.Equal(JTokenType.Null, h.Receipt["partPersisted"].Type);
            Assert.True(h.Receipt["workerRestartRequired"]?.Value<bool>());
            Assert.DoesNotContain("VerifyAfterCommit", h.Steps.Calls);
        }

        [Fact]
        public void InterruptionBeforeAnyWrite_DoesNotClaimUncertainPersistence()
        {
            var h = new Harness();
            h.Receipt["writeAttempted"] = false;
            h.Steps.Faults["BeginTransaction"] = new Exception("no transaction");

            var outcome = h.Run();

            Assert.Equal(IsolatedEventsSaveOutcomeKind.Interrupted, outcome.Kind);
            Assert.False(h.Receipt["objectSaved"]?.Value<bool>());
            Assert.False(h.Receipt["persistenceUncertain"]?.Value<bool>());
            Assert.DoesNotContain("AssignSource", h.Steps.Calls);
        }

        [Fact]
        public void WriteGateFailure_IsReportedAsAnInterruptionBeforeAnyStep()
        {
            var h = new Harness();

            var outcome = IsolatedEventsSaveFlow.Run(h.Steps, h.Trace, "token-1",
                () => throw new InvalidOperationException("gate unavailable"));

            Assert.Equal(IsolatedEventsSaveOutcomeKind.Interrupted, outcome.Kind);
            Assert.Contains("gate unavailable", outcome.Message);
            Assert.Equal("preflight", outcome.Phase);
            Assert.DoesNotContain("ReadOriginal", h.Steps.Calls);
        }

        [Fact]
        public void WriteGate_IsReleasedWhenTheSaveFails()
        {
            var h = new Harness();
            h.Steps.Verified = false;
            h.Steps.Faults["SavePart"] = new Exception("Erro");

            h.Run();

            Assert.Equal(1, h.Gate.Acquired);
            Assert.Equal(1, h.Gate.Released);
        }

        [Fact]
        public void FakeSdkPartMirrorsTheShapeTheDiagnosticsAndTheNormalWritePathRead()
        {
            var part = new FakeSdkPart();
            part.Messages.Add(new FakeSdkMessage { Text = "src0059: Esperando 'EndFor'" });

            // The normal write path reads messages through this extension; the isolated
            // path must see the same text from the same kind of object.
            Assert.Contains("src0059", part.GetSdkMessages());
            var failure = IsolatedEventsSaveDiagnostics.Describe(new Exception("Erro"), "part-save",
                () => new[] { new KeyValuePair<string, object>("Events", part) });
            Assert.Contains("src0059", failure.Detail);
        }
    }
}
