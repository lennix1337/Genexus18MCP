using System;
using System.Reflection;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // Issue #24 (mode=patch) — PatchService keys success off the legacy _internalStatus
    // shape, but WriteService emits the canonical {status:"ok", code:...} envelope.
    // ParseWriteResult must bridge canonical -> _internalStatus so a clean write isn't
    // treated as a failure (which forced the fallback/rollback path and produced the
    // mangled {"message":"{"} envelope on inline native-code [! !] content).
    public class PatchParseWriteResultTests
    {
        // ParseWriteResult is private static — reach it via reflection (same assembly,
        // InternalsVisibleTo doesn't expose privates).
        private static JObject Parse(string json)
        {
            var m = typeof(PatchService).GetMethod("ParseWriteResult",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(m);
            return (JObject)m!.Invoke(null, new object[] { json })!;
        }

        [Theory]
        [InlineData("WriteApplied")]
        [InlineData("WriteNoChange")]
        public void Canonical_OkEnvelope_LiftsToSuccess(string code)
        {
            var jo = Parse(new JObject { ["status"] = "ok", ["code"] = code, ["target"] = "P" }.ToString());
            Assert.Equal("Success", (string)jo["_internalStatus"]);
        }

        [Fact]
        public void Canonical_PartialEnvelope_LiftsToSuccess()
        {
            var jo = Parse(new JObject { ["status"] = "partial", ["code"] = "WriteApplied" }.ToString());
            Assert.Equal("Success", (string)jo["_internalStatus"]);
        }

        [Fact]
        public void Canonical_ErrorEnvelope_LiftsToErrorAndMessage()
        {
            var jo = Parse(new JObject
            {
                ["status"] = "error",
                ["error"] = new JObject { ["code"] = "Boom", ["message"] = "it failed" }
            }.ToString());
            Assert.Equal("Error", (string)jo["_internalStatus"]);
            Assert.Equal("it failed", (string)jo["message"]);
        }

        [Fact]
        public void Legacy_SuccessEnvelope_StillRecognized()
        {
            // A not-yet-migrated caller may still emit status:"Success".
            var jo = Parse(new JObject { ["status"] = "Success" }.ToString());
            Assert.Equal("Success", (string)jo["_internalStatus"]);
        }

        [Fact]
        public void Unparseable_FallsBackToError()
        {
            var jo = Parse("{");
            Assert.Equal("Error", (string)jo["_internalStatus"]);
            Assert.Equal("{", (string)jo["message"]);
        }

        // Issue #542 (GX18 U16 proof): WriteService reports its verified Structure rollback
        // as plain extras of the canonical error envelope (error.rollbackAttempted,
        // error.rolledBack, error.rollbackError, error.unappliedMembers). ParseWriteResult
        // lifted only a fixed list of evidence names, so IsWriterRollbackReported never saw
        // the attempt and the patch receipt kept the generic "normalization difference"
        // message without unappliedMembers or rollback.source.
        [Fact]
        public void Canonical_ErrorEnvelope_LiftsTheWriterRollbackEvidence()
        {
            var jo = Parse(new JObject
            {
                ["status"] = "error",
                ["error"] = new JObject
                {
                    ["code"] = "WriteNotPersisted",
                    ["message"] = "not applied",
                    ["persistedVerified"] = false,
                    ["persistedVerifyError"] = "Structure DSL was saved but the post-Save re-read did not contain: codigo : VARCHAR(40).",
                    ["unappliedMembers"] = new JArray("codigo : VARCHAR(40)"),
                    ["rollbackAttempted"] = true,
                    ["rolledBack"] = true,
                    ["rollbackError"] = "cause"
                }
            }.ToString());

            Assert.True(jo["rollbackAttempted"]!.Value<bool>());
            Assert.True(jo["rolledBack"]!.Value<bool>());
            Assert.Equal("cause", (string)jo["rollbackError"]);
            Assert.Equal(new[] { "codigo : VARCHAR(40)" }, ((JArray)jo["unappliedMembers"]!).ToObject<string[]>());
            Assert.True(PatchService.IsWriterRollbackReported(jo));
        }

        [Fact]
        public void PatchService_UnpacksWritePayloadResult_ToExposeDerivedObjectRegeneration()
        {
            // Issue #453: PatchService must unpack writePayload["result"] so fields like
            // derivedObjectRegeneration, warning, resolvedObject are surfaced directly on resultObj
            // rather than being nested under result.result or dropped.
            string source = GxMcp.TestSupport.RepoSource.WithoutComments(
                "src", "GxMcp.Worker", "Services", "PatchService.cs");

            Assert.Contains("writePayload[\"result\"] is JObject innerWriteResult", source, StringComparison.Ordinal);
            Assert.Contains("resultObj[innerProp.Name] = innerProp.Value", source, StringComparison.Ordinal);
        }
    }
}
