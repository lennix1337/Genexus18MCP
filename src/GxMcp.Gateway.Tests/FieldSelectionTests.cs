using Newtonsoft.Json.Linq;
using Xunit;
using GxMcp.Gateway.Routers;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// Task 5.1 — genexus_read parts= field-selection routing contract.
    /// Verifies that the Gateway ObjectRouter converts the parts array into the
    /// correct worker message (module=Read, action=ExtractParts) and that the
    /// legacy single-part path is preserved when parts is absent/empty.
    /// </summary>
    public class FieldSelectionTests
    {
        private readonly ObjectRouter _router = new ObjectRouter();

        // ── parts= present ────────────────────────────────────────────────────

        [Fact]
        public void Parts_NonEmpty_RoutesToExtractParts()
        {
            var args = JObject.Parse(
                "{\"name\":\"Customer\",\"parts\":[\"Variables\"],\"type\":\"Transaction\"}");
            var msg = _router.ConvertToolCall("genexus_read", args);
            Assert.NotNull(msg);
            var obj = JObject.FromObject(msg!);

            Assert.Equal("Read", obj["module"]?.ToString());
            Assert.Equal("ExtractParts", obj["action"]?.ToString());
            Assert.Equal("Customer", obj["target"]?.ToString());
            Assert.Equal("Transaction", obj["type"]?.ToString());

            var partsArr = obj["parts"] as JArray;
            Assert.NotNull(partsArr);
            Assert.Single(partsArr!);
            Assert.Equal("Variables", partsArr![0]?.ToString());
        }

        [Fact]
        public void Parts_WithOffsetAndLimit_ForwardsThePagingWindow()
        {
            var args = JObject.Parse(
                "{\"name\":\"Customer\",\"parts\":[\"Events\"],\"offset\":200,\"limit\":0}");
            var obj = JObject.FromObject(_router.ConvertToolCall("genexus_read", args)!);

            Assert.Equal("ExtractParts", obj["action"]?.ToString());
            Assert.Equal(200, obj["offset"]?.ToObject<int?>());
            Assert.Equal(0, obj["limit"]?.ToObject<int?>());
        }

        [Fact]
        public void Parts_MultipleParts_AllPassedThrough()
        {
            var args = JObject.Parse(
                "{\"name\":\"InvoiceProc\",\"parts\":[\"Source\",\"Variables\",\"Rules\"]}");
            var msg = _router.ConvertToolCall("genexus_read", args);
            var obj = JObject.FromObject(msg!);

            Assert.Equal("ExtractParts", obj["action"]?.ToString());
            var partsArr = obj["parts"] as JArray;
            Assert.NotNull(partsArr);
            Assert.Equal(3, partsArr!.Count);
        }

        // ── parts= absent → legacy single-part path ────────────────────────

        [Fact]
        public void NoParts_RoutesToExtractSource()
        {
            var args = JObject.Parse(
                "{\"name\":\"Customer\",\"part\":\"Source\"}");
            var msg = _router.ConvertToolCall("genexus_read", args);
            Assert.NotNull(msg);
            var obj = JObject.FromObject(msg!);

            Assert.Equal("Read", obj["module"]?.ToString());
            Assert.Equal("ExtractSource", obj["action"]?.ToString());
            Assert.Equal("Customer", obj["target"]?.ToString());
        }

        [Fact]
        public void EmptyPartsArray_RoutesToExtractFullObject()
        {
            var args = JObject.Parse(
                "{\"name\":\"Customer\",\"parts\":[]}");
            var msg = _router.ConvertToolCall("genexus_read", args);
            var obj = JObject.FromObject(msg!);

            // Empty array means "no part selection" → fall back to SOTA 1-roundtrip full object
            Assert.Equal("ExtractFullObject", obj["action"]?.ToString());
        }

        [Theory]
        [InlineData("all")]
        [InlineData("full")]
        [InlineData("summary")]
        [InlineData("360")]
        public void PartAliases_RouteToExtractFullObject(string partAlias)
        {
            var args = new JObject { ["name"] = "Customer", ["part"] = partAlias };
            var msg = _router.ConvertToolCall("genexus_read", args);
            Assert.NotNull(msg);
            var obj = JObject.FromObject(msg!);

            Assert.Equal("Read", obj["module"]?.ToString());
            Assert.Equal("ExtractFullObject", obj["action"]?.ToString());
            Assert.Equal("Customer", obj["target"]?.ToString());
        }

        [Fact]
        public void NullParts_RoutesToExtractFullObject()
        {
            var args = JObject.Parse(
                "{\"name\":\"Customer\"}");
            var msg = _router.ConvertToolCall("genexus_read", args);
            var obj = JObject.FromObject(msg!);

            Assert.Equal("ExtractFullObject", obj["action"]?.ToString());
        }

        // ── parts= + targets= should preserve field selection ────────────────

        [Fact]
        public void Targets_WithoutParts_RoutesToBatchRead()
        {
            var args = JObject.Parse(
                "{\"targets\":[\"Proc1\",\"Proc2\"]}");
            var msg = _router.ConvertToolCall("genexus_read", args);
            var obj = JObject.FromObject(msg!);

            Assert.Equal("Batch", obj["module"]?.ToString());
            Assert.Equal("BatchRead", obj["action"]?.ToString());
        }

        [Fact]
        public void Targets_WithParts_ForwardsRequestedPartsToBatchRead()
        {
            var args = JObject.Parse(
                "{\"targets\":[\"Proc1\",\"Proc2\"],\"parts\":[\"Variables\"]}");
            var msg = _router.ConvertToolCall("genexus_read", args);
            var obj = JObject.FromObject(msg!);

            Assert.Equal("Batch", obj["module"]?.ToString());
            Assert.Equal("BatchRead", obj["action"]?.ToString());
            Assert.Equal("Variables", obj["parts"]?[0]?.ToString());
        }
    }
}
