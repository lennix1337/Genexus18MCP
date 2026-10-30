using System.Linq;
using GxMcp.Gateway;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class TerseErrorTests
    {
        // Keys of the PatternInstance write receipt that the terse projection keeps (McpRouter.TrimErrorEnvelope).
        private static readonly string[] PatternReceiptKeys =
        {
            "persistenceState", "commitCompleted", "snapshot", "stateRestoredExactly", "rollbackPerformed", "beforeHash",
            "projection", "restorationVerification", "hostHashBefore", "parentHashBefore", "transactionFinalizationError",
            "verificationError"
        };

        [Theory]
        [InlineData("partial", true)]
        [InlineData("unknown", null)]
        public void PatternReceiptRetainsPartialOrUnknownPersistence(string state, bool? persisted)
        {
            var input = new JObject
            {
                ["code"] = "PatternVerificationMismatch", ["message"] = "Read the saved state before retrying.",
                ["saved"] = true, ["persisted"] = persisted.HasValue ? new JValue(persisted.Value) : JValue.CreateNull(),
                ["persistenceState"] = state, ["persistedStateKnown"] = persisted.HasValue,
                ["snapshot"] = "snapshot-id", ["partialPersistenceDetected"] = persisted == true,
                ["rollback"] = new JObject { ["attempted"] = false, ["reason"] = "AtomicRollbackUnavailable" },
                ["stateRestoredExactly"] = false, ["commitCompleted"] = true,
                ["stack"] = "internal"
            };
            var trimmed = McpRouter.TrimErrorEnvelope(input, verbose: false);
            foreach (var property in input.Properties())
                if (property.Name != "stack") Assert.True(JToken.DeepEquals(property.Value, trimmed[property.Name]), property.Name);
            Assert.Null(trimmed["stack"]);
        }

        [Fact]
        public void TrimErrorEnvelope_PatternReceiptKeepsEveryReceiptKey()
        {
            var input = new JObject { ["code"] = "PatternVerificationUnavailable", ["message"] = "Read the saved state before retrying." };
            foreach (string key in PatternReceiptKeys)
                input[key] = "value-" + key;
            var trimmed = McpRouter.TrimErrorEnvelope(input, verbose: false);
            foreach (string key in PatternReceiptKeys)
                Assert.True(JToken.DeepEquals(input[key], trimmed[key]), key);
        }

        [Fact]
        public void TrimErrorEnvelope_PatternReceiptWithOnlyPersistenceStateKeepsEveryReceiptKey()
        {
            var keys = PatternReceiptKeys.Where(key => key != "commitCompleted").ToArray();
            var input = new JObject { ["code"] = "PatternVerificationUnavailable", ["message"] = "Read the saved state before retrying." };
            foreach (string key in keys)
                input[key] = "value-" + key;
            var trimmed = McpRouter.TrimErrorEnvelope(input, verbose: false);
            foreach (string key in keys)
                Assert.True(JToken.DeepEquals(input[key], trimmed[key]), key);
        }

        [Fact]
        public void TrimErrorEnvelope_UnrelatedErrorWithoutReceiptMarkersDropsReceiptKeys()
        {
            var keys = PatternReceiptKeys.Where(key => key != "commitCompleted" && key != "persistenceState").ToArray();
            var input = new JObject { ["code"] = "WriteNotPersisted", ["message"] = "Unrelated failure." };
            foreach (string key in keys)
                input[key] = "value-" + key;
            var trimmed = McpRouter.TrimErrorEnvelope(input, verbose: false);
            foreach (string key in keys)
                Assert.False(trimmed.ContainsKey(key), key);
        }

        // Patch, visual-write, WebForm and TransactionRecords errors report persistenceState too. They are not pattern
        // receipts: forwarding their snapshot (a local path) and hashes would widen their terse shape.
        [Theory]
        [InlineData("WriteNotPersisted", "CommittedUnverified")]
        [InlineData("WebFormWriteVerificationFailed", "Divergent")]
        [InlineData("CommitOutcomeUnknown", "Indeterminate")]
        public void TrimErrorEnvelope_OtherFamiliesThatReportPersistenceStateKeepTheirTerseShape(string code, string persistenceState)
        {
            var input = new JObject { ["code"] = code, ["message"] = "Unrelated failure.", ["persistenceState"] = persistenceState };
            foreach (string key in PatternReceiptKeys.Where(key => key != "persistenceState"))
                input[key] = key == "snapshot" ? new JObject { ["path"] = "C:\\kb\\.gx\\snapshots\\Sample.bak" } : (JToken)("value-" + key);
            var trimmed = McpRouter.TrimErrorEnvelope(input, verbose: false);
            foreach (string key in PatternReceiptKeys)
                Assert.False(trimmed.ContainsKey(key), key);
        }

        [Fact]
        public void TrimErrorEnvelope_PatternReceiptKeepsTheCauseOfEachSwallowedStep()
        {
            // The WorkWithPlus flow records the sanitized cause of a part save it swallowed, of a failed cache
            // invalidation and of a failed flush, and whether apply-on-save was switched back on.
            var input = JObject.Parse("{status:'error',error:{code:'PatternProjectionFailed',message:'The committed projection could not be confirmed.'},"
                + "commitCompleted:true,sdkSaveError:'part save refused',flushError:'flush refused',"
                + "readCacheInvalidationError:'cache refused',applyOnSaveReenabled:false}");
            var trimmed = McpRouter.TrimErrorEnvelope(input, verbose: false);
            foreach (string key in new[] { "sdkSaveError", "flushError", "readCacheInvalidationError", "applyOnSaveReenabled" })
                Assert.True(JToken.DeepEquals(input[key], trimmed[key]), key);
        }

        [Fact]
        public void TrimErrorEnvelope_RawSdkSaveErrorObjectOfTheGenericWriterIsNotForwarded()
        {
            // The generic writer keeps the raw exception text in an object; only the one-line sanitized string is terse-safe.
            var input = JObject.Parse("{status:'error',error:{code:'PatternVerificationMismatch',message:'mismatch',"
                + "sdkSaveError:{type:'System.Exception',message:'raw text',where:'resolvedPart.Save()'}},persistenceState:'partial'}");
            var trimmed = McpRouter.TrimErrorEnvelope(input, verbose: false);
            Assert.Equal("partial", (string)trimmed["persistenceState"]!);
            Assert.False(trimmed.ContainsKey("sdkSaveError"));
        }

        [Fact]
        public void TrimErrorEnvelope_PreservesIsolatedSavePinsAndRollbackEvidence()
        {
            var input = JObject.Parse("{code:'ObjectSaveIncomplete',message:'verification failed',requiredPins:['kb','version'],effectivePins:{kbIdentity:'opaque',version:'Development'},profileRevision:'abc',phase:'isolation-preflight',persistabilityVerified:false,isolation:{verified:true},writeAttempted:true,verificationCompleted:true,persistenceUncertain:false,transactionRolledBack:true,rollbackVerified:true,otherPartsIntact:true,otherObjectMetadataIntact:true}");
            var trimmed = McpRouter.TrimErrorEnvelope(input, verbose: false);
            foreach (var property in input.Properties())
                Assert.True(JToken.DeepEquals(property.Value, trimmed[property.Name]), property.Name);
        }

        [Fact]
        public void TrimErrorEnvelope_KeepsThePropertyNamesTheRollbackPutBack()
        {
            // genexus_properties set: a failed batch names the properties the rollback put back and the ones it could not.
            // Only names travel in these lists; no property value is in the envelope to project.
            var input = JObject.Parse(@"{
                ""code"":""PropertyVerificationFailed"",
                ""message"":""The saved property could not be confirmed. Snapshot restoration could not be verified; inspect the object before writing again."",
                ""rollbackAttempted"":true,""rollbackVerified"":true,
                ""propertiesRestored"":[""A""],""propertiesNotRestored"":[""B""]
            }");

            var trimmed = McpRouter.TrimErrorEnvelope(input, verbose: false);

            Assert.Equal(new[] { "A" }, trimmed["propertiesRestored"]!.ToObject<string[]>());
            Assert.Equal(new[] { "B" }, trimmed["propertiesNotRestored"]!.ToObject<string[]>());
        }

        [Fact]
        public void TrimErrorEnvelope_PreservesIsolatedSaveFailureDiagnostics()
        {
            // The envelope the Worker builds for a failed isolated Events save: the terse
            // projection keeps the first message line only, so the stage, the exception
            // and the SDK's own messages must survive as fields or the caller sees "Erro".
            var input = JObject.Parse(@"{
                ""code"":""ObjectSaveIncomplete"",
                ""message"":""Isolated Events save failed during part-save: src0059: Esperando 'EndFor'"",
                ""savePhase"":""part-save"",""verificationPhase"":""post-commit-verification"",
                ""diagnostics"":[{""code"":""src0294"",""message"":""Unknown function 'SetEmpty'"",""object"":""SamplePanel"",""part"":""Events"",""rawLine"":2,""column"":16}],
                ""exceptionType"":""System.Exception"",
                ""innerMessages"":[""inner""],""innerExceptionTypes"":[""System.ArgumentException""],
                ""sdkMessages"":[{""code"":""src0059"",""part"":""Events"",""rawLine"":12,""severity"":""Error"",""message"":""src0059: Esperando 'EndFor'""}],
                ""sourceValidationErrors"":[{""code"":""src0059"",""rawLine"":12,""message"":""src0059: Esperando 'EndFor'""}],
                ""originalError"":""Erro"",""sdkMessagesTruncated"":true,""sdkMessagesError"":""unavailable"",
                ""verificationFailure"":{""savePhase"":""post-commit-reread"",""exceptionType"":""System.Exception""},
                ""saveFailureMessage"":""Isolated Events save failed during part-save: x""
            }");

            var trimmed = McpRouter.TrimErrorEnvelope(input, verbose: false);

            foreach (var property in input.Properties().Where(p => p.Name != "message"))
                Assert.True(JToken.DeepEquals(property.Value, trimmed[property.Name]), property.Name);
            Assert.Equal("Isolated Events save failed during part-save: src0059: Esperando 'EndFor'", (string)trimmed["message"]!);
        }

        [Fact]
        public void TrimErrorEnvelope_PreservesPhysicalSaveAndTypedDiff()
        {
            var input = JObject.Parse("{code:'WriteNotPersisted',message:'mismatch',saved:true,sdkSaveCompleted:true,persistedStateKnown:true,persisted:false,objectSaved:true,partPersisted:false,requireObjectSave:true,saveContract:'transactional-object-save',metadataStampPersisted:true,versionToken:'35',postSaveVerification:{representation:'genexus_read',matches:false},mutation:{diff:{reason:'contentMismatch',firstDifferentLine:2,expectedLine:'a',readLine:'b'}},implicitLifecycleActions:[]}");
            var trimmed = McpRouter.TrimErrorEnvelope(input, verbose: false);
            foreach (string key in new[] { "saved", "sdkSaveCompleted", "persistedStateKnown", "persisted", "versionToken", "postSaveVerification", "mutation", "implicitLifecycleActions", "objectSaved", "partPersisted", "requireObjectSave", "saveContract", "metadataStampPersisted" })
                Assert.True(JToken.DeepEquals(input[key], trimmed[key]), key);
        }

        [Fact]
        public void TrimErrorEnvelope_DefaultDropsStackAndKeepsMessage()
        {
            // Friction 2026-05-25 item #5: `details` (and a handful of diagnostic
            // fields: verifyDiff, suggestion, persistedSnippet, requestedSnippet,
            // availableParts, part, objectName, objectType) is preserved by
            // TrimErrorEnvelope when present. The agent needs the SDK's actual
            // rejection diff to fix the next call. `stack` is still dropped (it's
            // engineer-internal — verbose=true is the escape hatch for it).
            var input = JObject.Parse(@"{
                ""code"":""internal"",""message"":""boom\nstack frame 1\nstack frame 2"",
                ""stack"":""..."",""details"":""verify-diff-payload"",""hint"":""try X""
            }");
            var trimmed = McpRouter.TrimErrorEnvelope(input, verbose: false);
            Assert.Equal("boom", (string)trimmed["message"]!);
            Assert.Equal("internal", (string)trimmed["code"]!);
            Assert.Equal("try X", (string)trimmed["hint"]!);
            Assert.False(trimmed.ContainsKey("stack"), "stack should be dropped — engineer-internal");
            Assert.Equal("verify-diff-payload", (string)trimmed["details"]!);
        }

        [Fact]
        public void TrimErrorEnvelope_KeepsSdkReasonBulletsUnderAColonHeader()
        {
            // The SDK refuses a delete with "header:\r\n* reason" (one bullet per object);
            // keeping only the header left DeleteFailed without any reason.
            var input = new JObject
            {
                ["code"] = "DeleteFailed",
                ["message"] = "Object(s) could not be deleted:"
                    + "\r\n* Attribute 'SampleId' is referenced at least by Index 'ISample'."
                    + "\r\n* Attribute 'SampleName' is referenced at least by Transaction 'Sample'."
            };
            var trimmed = McpRouter.TrimErrorEnvelope(input, verbose: false);
            Assert.Equal(
                "Object(s) could not be deleted: Attribute 'SampleId' is referenced at least by Index 'ISample'.; Attribute 'SampleName' is referenced at least by Transaction 'Sample'.",
                (string)trimmed["message"]!);
        }

        [Fact]
        public void TrimErrorEnvelope_ColonHeaderWithoutBulletsStillDropsTheRest()
        {
            var input = new JObject
            {
                ["code"] = "internal",
                ["message"] = "Unexpected failure:\n   at Sample.Method()\n* not a reason, after a stack frame"
            };
            var trimmed = McpRouter.TrimErrorEnvelope(input, verbose: false);
            Assert.Equal("Unexpected failure:", (string)trimmed["message"]!);
        }

        [Fact]
        public void TrimErrorEnvelope_WriteNotPersisted_PreservesPatchReceipt()
        {
            var input = JObject.Parse(@"{
                ""code"":""WriteNotPersisted"",""message"":""post-save mismatch"",
                ""saved"":true,""verified"":false,""requestedHash"":""req"",""persistedHash"":""disk"",
                ""persistedMatchCount"":1,""oldContentPresent"":true,""versionToken"":""123"",
                ""mismatchKind"":""normalization"",
                ""rollback"":{""requested"":true,""saved"":true,""verified"":true},""rolledBack"":true
            }");

            var trimmed = McpRouter.TrimErrorEnvelope(input, verbose: false);

            Assert.True(trimmed["saved"]!.ToObject<bool>());
            Assert.False(trimmed["verified"]!.ToObject<bool>());
            // Issue #547: the mismatch classification must survive the terse projection.
            Assert.Equal("normalization", trimmed["mismatchKind"]!.ToString());
            Assert.Equal("req", trimmed["requestedHash"]!.ToString());
            Assert.Equal("disk", trimmed["persistedHash"]!.ToString());
            Assert.Equal(1, trimmed["persistedMatchCount"]!.ToObject<int>());
            Assert.True(trimmed["oldContentPresent"]!.ToObject<bool>());
            Assert.True(trimmed["rollback"]!["verified"]!.ToObject<bool>());
            Assert.True(trimmed["rolledBack"]!.ToObject<bool>());
            Assert.Equal("123", trimmed["versionToken"]!.ToString());
        }

        // Issue #557 (GX18 U16 proof): the Worker labelled the explicit-normalized
        // Structure mismatch with error.mismatchKind, but the terse projection dropped
        // it, so the caller only saw the generic WriteNotPersisted.
        [Fact]
        public void TrimErrorEnvelope_WriteNotPersisted_PreservesMismatchKind()
        {
            var input = JObject.Parse(@"{
                ""code"":""WriteNotPersisted"",""message"":""post-save mismatch"",
                ""saved"":true,""verified"":false,""mismatchKind"":""sdk-generated-description""
            }");

            var trimmed = McpRouter.TrimErrorEnvelope(input, verbose: false);

            Assert.Equal("sdk-generated-description", trimmed["mismatchKind"]!.ToString());
        }

        // Issue #542 (GX18 U16 proof): the Structure writer's rollback evidence reached the
        // Worker envelope but the terse projection dropped unappliedMembers,
        // rollbackAttempted and rollbackError, so the caller saw rolledBack=true without
        // knowing which member the SDK refused or why a revert could not be verified.
        [Fact]
        public void TrimErrorEnvelope_WriteNotPersisted_PreservesStructureRollbackEvidence()
        {
            var input = JObject.Parse(@"{
                ""code"":""WriteNotPersisted"",""message"":""post-save mismatch"",
                ""persistedVerified"":false,""rolledBack"":false,
                ""unappliedMembers"":[""codigo : VARCHAR(40)""],
                ""rollbackAttempted"":true,""rollbackError"":""Rollback write failed: cause""
            }");

            var trimmed = McpRouter.TrimErrorEnvelope(input, verbose: false);

            Assert.Equal(new[] { "codigo : VARCHAR(40)" }, trimmed["unappliedMembers"]!.ToObject<string[]>());
            Assert.True(trimmed["rollbackAttempted"]!.ToObject<bool>());
            Assert.Equal("Rollback write failed: cause", trimmed["rollbackError"]!.ToString());
            Assert.False(trimmed["rolledBack"]!.ToObject<bool>());
        }

        [Fact]
        public void TrimErrorEnvelope_PatchSafetyEvidencePreservesAttemptAndReadFailure()
        {
            var input = JObject.Parse(@"
            {
                ""code"":""WriteVerificationUnavailable"",
                ""message"":""post-save read unavailable"",
                ""saveAttempted"":true,
                ""saved"":false,
                ""verified"":false,
                ""persisted"":false,
                ""verificationUnavailable"":true,
                ""postSaveVerification"":{""reReadConfirmed"":false,""reason"":""FreshReadUnavailable""},
                ""readCode"":""FreshReadUnavailable"",
                ""readCompleted"":false,
                ""readError"":""same in-memory object""
            }");

            var trimmed = McpRouter.TrimErrorEnvelope(input, verbose: false);

            Assert.True(trimmed["saveAttempted"]!.ToObject<bool>());
            Assert.False(trimmed["saved"]!.ToObject<bool>());
            Assert.True(trimmed["verificationUnavailable"]!.ToObject<bool>());
            Assert.Equal("FreshReadUnavailable", trimmed["postSaveVerification"]!["reason"]!.ToString());
            Assert.Equal("FreshReadUnavailable", trimmed["readCode"]!.ToString());
            Assert.False(trimmed["readCompleted"]!.ToObject<bool>());
            Assert.Equal("same in-memory object", trimmed["readError"]!.ToString());
        }

        [Fact]
        public void TrimErrorEnvelope_CommentOnlyFailure_PreservesTypedReceipt()
        {
            var input = JObject.Parse(@"{
                ""code"":""CommentOnlyWriteNotPersisted"",""message"":""comment mismatch"",
                ""saved"":true,""verified"":false,""commentOnly"":true,""commentStyle"":""line"",
                ""before"":""msg()"",""after"":""//msg()"",""matchedCount"":1,
                ""replacementPresent"":false,""reReadConfirmed"":true,""implicitOperations"":[]
            }");

            var trimmed = McpRouter.TrimErrorEnvelope(input, verbose: false);

            Assert.Equal("CommentOnlyWriteNotPersisted", trimmed["code"]!.ToString());
            Assert.True(trimmed["commentOnly"]!.ToObject<bool>());
            Assert.Equal("line", trimmed["commentStyle"]!.ToString());
            Assert.Equal("msg()", trimmed["before"]!.ToString());
            Assert.Equal("//msg()", trimmed["after"]!.ToString());
            Assert.Equal(1, trimmed["matchedCount"]!.ToObject<int>());
            Assert.False(trimmed["replacementPresent"]!.ToObject<bool>());
            Assert.True(trimmed["reReadConfirmed"]!.ToObject<bool>());
            Assert.Empty((JArray)trimmed["implicitOperations"]!);
        }

        [Fact]
        public void TrimErrorEnvelope_VerbosePassesThrough()
        {
            var input = JObject.Parse(@"{""message"":""x"",""stack"":""trace""}");
            var trimmed = McpRouter.TrimErrorEnvelope(input, verbose: true);
            Assert.Equal("trace", (string)trimmed["stack"]!);
        }

        [Fact]
        public void TrimErrorEnvelope_HandlesMissingFieldsGracefully()
        {
            var input = JObject.Parse(@"{}");
            var trimmed = McpRouter.TrimErrorEnvelope(input, verbose: false);
            Assert.Equal("Unknown error", (string)trimmed["message"]!);
            Assert.False(trimmed.ContainsKey("code"));
        }

        [Fact]
        public void TrimErrorEnvelope_CanonicalEnvelope_ResolvesNestedMessageNotBrace()
        {
            // Issue #24 regression: the v2.8.0 canonical envelope nests code/message/hint
            // under an `error` sub-object. The old code read top-level `error["message"]`
            // (null here) then fell back to `error["error"]?.ToString()` — the whole
            // sub-object serialized, whose first line is "{". Result: every validation
            // failure reached the client as {"message":"{"} with the real diagnostic lost.
            var input = JObject.Parse(@"{
                ""status"":""error"",
                ""target"":""ZTmpIssue24"",
                ""error"":{
                    ""code"":""TransactionFailed"",
                    ""message"":""SDK Save Exception for ZTmpIssue24: validation failed.\nsrc0053: '[' is invalid."",
                    ""hint"":""Build surfaces full diagnostics.""
                },
                ""part"":""Source"",
                ""persistedSnippet"":""// Procedure: ZTmpIssue24\n""
            }");
            var trimmed = McpRouter.TrimErrorEnvelope(input, verbose: false);

            Assert.Equal("SDK Save Exception for ZTmpIssue24: validation failed.", (string)trimmed["message"]!);
            Assert.NotEqual("{", (string)trimmed["message"]!);
            Assert.Equal("TransactionFailed", (string)trimmed["code"]!);
            Assert.Equal("Build surfaces full diagnostics.", (string)trimmed["hint"]!);
            // Top-level diagnostic fields still pass through.
            Assert.Equal("Source", (string)trimmed["part"]!);
            Assert.Equal("// Procedure: ZTmpIssue24\n", (string)trimmed["persistedSnippet"]!);
        }

        [Fact]
        public void TrimErrorEnvelope_LegacyBareStringError_StillResolves()
        {
            // Oldest shape: `error` is a bare string rather than a sub-object.
            var input = JObject.Parse(@"{""error"":""flat string failure""}");
            var trimmed = McpRouter.TrimErrorEnvelope(input, verbose: false);
            Assert.Equal("flat string failure", (string)trimmed["message"]!);
        }

        [Fact]
        public void AddLayoutFailureReceiptSurvivesTerseProjection()
        {
            var input = new JObject
            {
                ["code"] = "WwpLayoutNotVerified",
                ["message"] = "Inspect the saved PatternInstance before retrying.",
                ["commitCompleted"] = true,
                ["rollbackPerformed"] = false,
                ["restorationVerification"] = new JObject
                {
                    ["host"] = new JObject { ["equal"] = false }, ["parent"] = new JObject { ["equal"] = true }
                },
                ["projection"] = new JObject { ["webForm"] = false },
                ["snapshot"] = "snapshot-id"
            };
            var trimmed = McpRouter.TrimErrorEnvelope(input, verbose: false);
            foreach (string key in new[] { "commitCompleted", "rollbackPerformed", "restorationVerification", "projection", "snapshot" })
                Assert.True(JToken.DeepEquals(input[key], trimmed[key]), key);
        }
    }
}
