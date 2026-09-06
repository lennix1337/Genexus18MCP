using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class RequestIsolationTests
    {
        [Fact]
        public void CancellationRequiresOwningSessionAndExactIdType()
        {
            Assert.True(Program.RequestIdentityMatches("client-a", JToken.FromObject(1), "client-a", JToken.FromObject(1)));
            Assert.False(Program.RequestIdentityMatches("client-a", JToken.FromObject(1), "client-b", JToken.FromObject(1)));
            Assert.False(Program.RequestIdentityMatches("client-a", JToken.FromObject(1), "client-a", JToken.FromObject("1")));
        }

        [Fact]
        public void StdioDefaultsToOneExplicitScope()
        {
            Assert.True(Program.RequestIdentityMatches(null!, JToken.FromObject("request"), "stdio", JToken.FromObject("request")));
            Assert.False(Program.RequestIdentityMatches("stdio", JToken.FromObject("request"), "http-2", JToken.FromObject("request")));
        }

        [Fact]
        public void ProgressRoutingRestoresTheClientTokenWithoutChangingItsJsonType()
        {
            var workerFrame = JObject.Parse("""
                {"jsonrpc":"2.0","method":"notifications/progress","params":{"progressToken":"internal-operation","progress":50,"total":100}}
                """);

            var numericToken = JToken.FromObject(1);
            Assert.Equal(JTokenType.Integer, numericToken.Type);
            var numeric = Program.RewriteProgressTokenForClient(workerFrame, numericToken);
            Assert.Equal(JTokenType.Integer, numeric["params"]?["progressToken"]?.Type);
            Assert.Equal(1, numeric["params"]?["progressToken"]?.Value<int>());
            Assert.Equal("internal-operation", workerFrame["params"]?["progressToken"]?.ToString());

            var textual = Program.RewriteProgressTokenForClient(workerFrame, JToken.FromObject("1"));
            Assert.Equal(JTokenType.String, textual["params"]?["progressToken"]?.Type);
            Assert.Equal("1", textual["params"]?["progressToken"]?.ToString());
        }

        [Theory]
        [InlineData("stdio", true)]
        [InlineData("legacy-session", true)]
        [InlineData("http-modern", false)]
        [InlineData("", false)]
        public void ProgressRoutingRequiresAnOwningTransport(string sessionId, bool expected)
        {
            Assert.Equal(expected, Program.IsProgressSessionBound(sessionId));
        }

        [Fact]
        public void CancellationMatrixRejectsCrossSessionNullAndDifferentJsonTypes()
        {
            var numeric = JToken.FromObject(1);
            var textual = JToken.FromObject("1");

            Assert.True(Program.RequestIdentityMatches("client-a", numeric, "client-a", numeric));
            Assert.False(Program.RequestIdentityMatches("client-a", numeric, "client-a", textual));
            Assert.False(Program.RequestIdentityMatches("client-a", numeric, "client-b", numeric));
            Assert.False(Program.RequestIdentityMatches("client-a", numeric, "client-a", null));
            Assert.False(Program.RequestIdentityMatches("client-a", null, "client-a", numeric));

            // A duplicate cancellation can only find the same owning identity;
            // it cannot broaden the match to a second request that reused the id.
            Assert.True(Program.RequestIdentityMatches("client-a", numeric, "client-a", numeric));
            Assert.False(Program.RequestIdentityMatches("client-b", numeric, "client-a", numeric));
        }
    }
}
