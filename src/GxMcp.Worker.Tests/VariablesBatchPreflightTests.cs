using System;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public sealed class VariablesBatchPreflightTests
    {
        private sealed class Writer : ISdkObjectWriter
        {
            internal int Writes;
            internal bool RejectPreview;
            public string WriteObject(string target, JObject args)
            {
                if (args["dryRun"]?.Value<bool>() == true)
                    return RejectPreview ? "{status:'error',error:{code:'InvalidVariables'}}" : "{status:'ok'}";
                Writes++;
                return target == "Fail" ? "{status:'error',error:{code:'Failure'}}" : "{status:'ok'}";
            }
            public string ReadObjectSource(string target, string part) => "&Existing : Numeric(4)";
            public string ApplySemanticOps(JObject args) => throw new NotSupportedException();
            public string ApplyJsonPatch(JObject args) => throw new NotSupportedException();
            public string BulkWrite(JObject args) => throw new NotSupportedException();
        }

        [Fact]
        public void InvalidVariablesTargetBlocksEarlierSourceWrite()
        {
            var writer = new Writer { RejectPreview = true };
            var result = new MutationEngine(writer).Execute(new MutationRequest { Targets = new JArray(
                new JObject { ["target"] = "SourceFirst", ["part"] = "Source", ["content"] = "// must not save" },
                new JObject { ["target"] = "VariablesSecond", ["part"] = "Variables", ["content"] = "invalid" }) });
            Assert.False(result.Success);
            Assert.Equal("VariablesPreflightFailed", result.ErrorCode);
            Assert.Equal(0, writer.Writes);
        }

        [Fact]
        public void FailureNeverReplaysVariablesSnapshotOverPotentialConcurrentMetadata()
        {
            var writer = new Writer();
            var result = new MutationEngine(writer).Execute(new MutationRequest { RollbackOnFailure = true, Targets = new JArray(
                new JObject { ["target"] = "VariablesFirst", ["part"] = "Variables", ["content"] = "&Changed : Numeric(8)" },
                new JObject { ["target"] = "Fail", ["part"] = "Source", ["content"] = "// fail" }) });
            Assert.False(result.Success);
            Assert.False(result.RolledBack);
            Assert.Equal("indeterminate", result.RollbackOutcome);
            Assert.Equal(2, writer.Writes);
            Assert.Contains("text_snapshot_restore_refused", result.ResponseJson);
            Assert.False(JObject.Parse(result.ResponseJson)["rollback"]?["attempted"]?.Value<bool>());
        }
    }
}
