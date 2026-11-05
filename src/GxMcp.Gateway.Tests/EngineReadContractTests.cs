using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class EngineReadContractTests
    {
        private static JObject CompletePage() => new JObject
        {
            ["part"] = "Source", ["source"] = "one\r\ntwo", ["isBase64"] = false,
            ["isEmpty"] = false, ["truncated"] = false, ["isTruncatedByWorker"] = false,
            ["offset"] = 0, ["limit"] = 2, ["totalLines"] = 2, ["totalBytes"] = 8,
            ["versionToken"] = "636911179160000000:root:" + new string('a', 64),
        };

        [Fact]
        public void CandidateShapesACompletePageWithoutClaimingSdkObservation()
        {
            var plan = new EngineReadPlan("Alpha", "Source", null, null);
            Assert.True(EngineReadContract.TryShape(plan, CompletePage(), new JObject(), out var payload, out var error), error);
            Assert.Equal("one\r\ntwo", payload!["source"]!.Value<string>());
            Assert.Equal("engine-object-state", payload["readFreshness"]!["origin"]!.Value<string>());
            Assert.False(payload["readFreshness"]!["sdkCacheRefreshConfirmed"]!.Value<bool>());
        }

        [Theory]
        [InlineData("{\"limit\":1}")]
        [InlineData("{\"limit\":2,\"offset\":1}")]
        [InlineData("{\"limit\":0}")]
        public void CandidateRefusesAnAnswerThatDoesNotHonorTheRequestedWindow(string args)
        {
            var request = JObject.Parse(args);
            request["name"] = "Alpha";
            request["part"] = "Source";
            Assert.Null(EngineReadContract.TryPlan(request, out var plan));
            Assert.False(EngineReadContract.TryShape(plan!, CompletePage(), new JObject(), out var payload, out var error));
            Assert.Null(payload);
            Assert.False(string.IsNullOrWhiteSpace(error));
        }

        [Theory]
        [InlineData("{\"offset\":1}")]
        [InlineData("{\"isEmpty\":true}")]
        [InlineData("{\"explicitFullRead\":true}")]
        [InlineData("{\"offset\":2147483648}")]
        [InlineData("{\"totalLines\":9223372036854775808}")]
        [InlineData("{\"truncated\":true,\"isTruncatedByWorker\":true,\"suggestedNextOffset\":3,\"suggestedNextLimit\":200}")]
        [InlineData("{\"limit\":0,\"truncated\":true,\"isTruncatedByWorker\":true,\"suggestedNextOffset\":0,\"suggestedNextLimit\":200}")]
        public void CandidateRefusesIncoherentOrOutOfRangeCounts(string changes)
        {
            var page = CompletePage();
            page.Merge(JObject.Parse(changes));
            Assert.False(EngineReadContract.TryShape(new EngineReadPlan("Alpha", "Source", null, null),
                page, new JObject(), out var payload, out var error));
            Assert.Null(payload);
            Assert.False(string.IsNullOrWhiteSpace(error));
        }

        [Theory]
        [InlineData("offset")]
        [InlineData("limit")]
        public void CandidateRefusesOversizedRequestCountsWithoutThrowing(string field)
        {
            var args = JObject.Parse("{\"name\":\"Alpha\",\"part\":\"Source\",\"" + field + "\":9223372036854775808}");
            Assert.NotNull(EngineReadContract.TryPlan(args, out var plan));
            Assert.Null(plan);
        }

        [Fact]
        public void CandidateAcceptsTheSdkLimitZeroClampedOffset()
        {
            var page = CompletePage();
            page["source"] = "";
            page["offset"] = 2;
            page["limit"] = 0;
            page["explicitFullRead"] = true;
            page["isEmpty"] = true; // SDK describes the returned page, not the full part.
            Assert.True(EngineReadContract.TryShape(new EngineReadPlan("Alpha", "Source", 9, 0),
                page, new JObject(), out _, out var error), error);
        }

        [Theory]
        [InlineData("Source")]
        [InlineData("Events")]
        [InlineData("Rules")]
        [InlineData("Conditions")]
        public void TryPlanAdmitsSupportedParts(string part)
        {
            var args = new JObject { ["name"] = "MyObject", ["part"] = part };
            string? error = EngineReadContract.TryPlan(args, out var plan);
            Assert.Null(error);
            Assert.NotNull(plan);
            Assert.Equal(part, plan.Part);
        }

        [Theory]
        [InlineData("Variables")]
        [InlineData("Structure")]
        [InlineData("PatternInstance")]
        [InlineData("WebForm")]
        public void TryPlanDefersComplexPartsToSdk(string part)
        {
            var args = new JObject { ["name"] = "MyObject", ["part"] = part };
            string? error = EngineReadContract.TryPlan(args, out var plan);
            Assert.NotNull(error);
            Assert.Null(plan);
        }
    }
}
