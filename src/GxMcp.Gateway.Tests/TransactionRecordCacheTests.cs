using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class TransactionRecordCacheTests
    {
        [Theory]
        [InlineData("records_query", false, false)]
        [InlineData("records_insert", false, true)]
        [InlineData("records_update", false, true)]
        [InlineData("records_insert", true, false)]
        [InlineData("records_update", true, false)]
        [InlineData("RECORDS_UPDATE", false, true)]
        public void RecordsUseLiveDispatchAndCorrectMutationPolicy(string action, bool dryRun, bool writes)
        {
            var args = new JObject { ["action"] = action, ["dryRun"] = dryRun };
            Assert.Equal(writes, Program.IsMutatingTool("genexus_db", args));
            Assert.Equal(!writes, OperationClassifier.IsReadOnly("genexus_db", args));
            Assert.Null(Program.CreateSemanticCacheKey("sample", "genexus_db", args, writes, false));
        }

        [Theory]
        [InlineData("records_insert")]
        [InlineData("records_update")]
        public void OmittedDryRunDefaultsToFreshPreview(string action)
        {
            var args = new JObject { ["action"] = action };
            Assert.False(Program.IsMutatingTool("genexus_db", args));
            Assert.Null(Program.CreateSemanticCacheKey("sample", "genexus_db", args, false, false));
        }

        [Theory]
        [InlineData("records_query")]
        [InlineData("records_insert")]
        [InlineData("records_update")]
        public void ExistingEmptyOrSuccessfulEnvelopeCannotSkipDispatch(string action)
        {
            var args = new JObject { ["action"] = action, ["dryRun"] = false };
            var cache = new SemanticCacheStore();
            string oldKey = "sample|genexus_db:" + args.ToString(Newtonsoft.Json.Formatting.None);
            cache.Set(oldKey, new JObject { ["persisted"] = true, ["records"] = new JArray() });
            int dispatches = 0;
            for (int i = 0; i < 2; i++)
            {
                var key = Program.CreateSemanticCacheKey("sample", "genexus_db", args,
                    Program.IsMutatingTool("genexus_db", args), false);
                if (key == null || !cache.TryGet(key, out _)) dispatches++;
            }
            Assert.Equal(2, dispatches);
        }

        [Fact]
        public void TimedOutWriteNeverClaimsNonPersistenceOrSafeRetry()
        {
            var payload = new JObject { ["retriable"] = true };
            Program.MarkRecordWriteOutcomeUnknown(payload);
            Assert.False(payload["retriable"]!.Value<bool>());
            Assert.False(payload["retrySafe"]!.Value<bool>());
            Assert.Equal(JTokenType.Null, payload["persisted"]!.Type);
            Assert.Equal("Indeterminate", payload["commitState"]!.Value<string>());
        }

        [Fact]
        public void ExistingSourceCachePolicyIsPreserved()
        {
            var args = new JObject { ["name"] = "SampleProcedure", ["part"] = "Source" };
            Assert.Equal("sample|genexus_read:" + args.ToString(Newtonsoft.Json.Formatting.None),
                Program.CreateSemanticCacheKey("sample", "genexus_read", args, false, false));
        }
    }
}
