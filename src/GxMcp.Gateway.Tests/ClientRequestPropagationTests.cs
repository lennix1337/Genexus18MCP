using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public sealed class ClientRequestPropagationTests
    {
        [Fact]
        public void ExplicitClientRequestIdIsCopiedIntoWorkerParams()
        {
            var command = new JObject { ["module"] = "Object", ["action"] = "Write" };
            var args = new JObject { ["clientRequestId"] = "client-42" };

            Program.AttachClientRequestIdentity(command, args);

            Assert.Equal("client-42", command["params"]?["clientRequestId"]?.ToString());
        }

        [Fact]
        public void IdempotencyKeyProvidesWorkerIdentityWhenClientIdIsOmitted()
        {
            var command = JObject.Parse(@"{
                ""module"": ""Object"",
                ""params"": { ""name"": ""Customer"" }
            }");

            Program.AttachClientRequestIdentity(command, new JObject { ["idempotencyKey"] = "write-7" });

            Assert.Equal("write-7", command["params"]?["clientRequestId"]?.ToString());
        }

        [Fact]
        public void ExistingWorkerIdentityIsNeverOverwritten()
        {
            var command = JObject.Parse(@"{
                ""params"": { ""clientRequestId"": ""adapter-identity"" }
            }");

            Program.AttachClientRequestIdentity(command, new JObject { ["clientRequestId"] = "caller-identity" });

            Assert.Equal("adapter-identity", command["params"]?["clientRequestId"]?.ToString());
        }

        [Fact]
        public void MissingIdentityDoesNotCreateWorkerParams()
        {
            var command = new JObject { ["module"] = "Object" };

            Program.AttachClientRequestIdentity(command, new JObject { ["name"] = "Customer" });

            Assert.Null(command["params"]);
        }
    }
}
