using GxMcp.Gateway;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class TerseErrorTests
    {
        [Fact]
        public void TrimErrorEnvelope_PreservesIsolatedSavePinsAndRollbackEvidence()
        {
            var input = JObject.Parse("{code:'ObjectSaveIncomplete',message:'verification failed',requiredPins:['kb','version'],effectivePins:{kbIdentity:'opaque',version:'Development'},profileRevision:'abc',phase:'isolation-preflight',persistabilityVerified:false,isolation:{verified:true},writeAttempted:true,verificationCompleted:true,persistenceUncertain:false,transactionRolledBack:true,rollbackVerified:true,otherPartsIntact:true,otherObjectMetadataIntact:true}");
            var trimmed = McpRouter.TrimErrorEnvelope(input, verbose: false);
            foreach (var property in input.Properties())
                Assert.True(JToken.DeepEquals(property.Value, trimmed[property.Name]), property.Name);
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
    }
}
