using Newtonsoft.Json.Linq;
using Xunit;
using GxMcp.Gateway;
using GxMcp.Gateway.Routers;

namespace GxMcp.Gateway.Tests
{
    public class EditTargetsContractTests
    {
        [Fact]
        public void Read_AcceptsSingularTarget()
        {
            var router = new ObjectRouter();
            var args = JObject.Parse("""{"name":"Customer","part":"Source"}""");

            var msg = router.ConvertToolCall("genexus_read", args);

            Assert.NotNull(msg);
        }

        [Fact]
        public void Read_AcceptsTargetsPlural()
        {
            var router = new ObjectRouter();
            var args = JObject.Parse("""{"targets":["A","B"],"part":"Source"}""");

            var msg = router.ConvertToolCall("genexus_read", args);

            Assert.NotNull(msg);
            var obj = JObject.FromObject(msg!);
            Assert.Equal("Batch", obj["module"]?.ToString());
            Assert.Equal("BatchRead", obj["action"]?.ToString());
        }

        [Fact]
        public void Read_RejectsBothTargetForms()
        {
            var router = new ObjectRouter();
            var args = JObject.Parse("""{"name":"Customer","targets":["A","B"]}""");

            var ex = Assert.Throws<UsageException>(() => router.ConvertToolCall("genexus_read", args));
            Assert.Contains("mutually exclusive", ex.Message);
            Assert.Equal("usage_error", ex.Code);
        }

        [Fact]
        public void Edit_RejectsBothTargetForms()
        {
            var router = new ObjectRouter();
            var args = JObject.Parse("""{"name":"A","targets":[{"name":"B","content":"y"}]}""");

            var ex = Assert.Throws<UsageException>(() => router.ConvertToolCall("genexus_edit", args));
            Assert.Contains("mutually exclusive", ex.Message);
            Assert.Equal("usage_error", ex.Code);
        }

        [Fact]
        public void Edit_AcceptsTargetsPlural_OfEditRequests()
        {
            var router = new ObjectRouter();
            var args = JObject.Parse("""{"targets":[{"name":"A","content":"x"},{"name":"B","content":"y"}],"rollbackOnFailure":false}""");

            var msg = router.ConvertToolCall("genexus_edit", args);

            Assert.NotNull(msg);
            var obj = JObject.FromObject(msg!);
            Assert.Equal("Batch", obj["module"]?.ToString());
            Assert.Equal("MultiEdit", obj["action"]?.ToString());
            Assert.False(obj["rollbackOnFailure"]?.ToObject<bool>());
        }

        [Fact]
        public void Edit_LegacyChangesArg_IsRejected()
        {
            var router = new ObjectRouter();
            var args = JObject.Parse("""{"name":"A","changes":[]}""");

            var ex = Assert.Throws<UsageException>(() => router.ConvertToolCall("genexus_edit", args));
            Assert.Equal("usage_error", ex.Code);
            Assert.Equal("argument 'changes' removed in v2.0.0; use 'targets' instead", ex.Message);
        }
    }
}
