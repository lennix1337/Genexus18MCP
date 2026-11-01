using GxMcp.Gateway;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// The terse projection of a refused or unconfirmed genexus_properties set: the names of the changed parts reach the caller,
    /// the lines that differ come only with verbose_errors, and the code of an unreconciled object keeps its hint.
    /// </summary>
    public class PropertyRollbackProjectionTests
    {
        [Fact]
        public void TerseKeepsTheChangedPartNamesAndLeavesOutTheDifferingLines()
        {
            var input = FailedRollback();

            var trimmed = McpRouter.TrimErrorEnvelope(input, verbose: false);

            Assert.True(JToken.DeepEquals(input["changedParts"], trimmed["changedParts"]));
            Assert.Null(trimmed["changedLines"]);
        }

        [Fact]
        public void VerboseKeepsTheDifferingLines()
        {
            var input = FailedRollback();

            var verbose = McpRouter.TrimErrorEnvelope(input, verbose: true);

            Assert.True(JToken.DeepEquals(input["changedLines"], verbose["changedLines"]));
        }

        [Fact]
        public void TerseKeepsTheCodeAndHintOfAnUnreconciledObject()
        {
            var input = new JObject
            {
                ["code"] = "PropertyRollbackUnreconciled",
                ["message"] = "The last property write to this object was not confirmed.",
                ["hint"] = "Inspect the parts listed in changedParts, restore them, then retry.",
                ["changedParts"] = new JArray("WebForm"),
                ["changedLines"] = new JArray()
            };

            var trimmed = McpRouter.TrimErrorEnvelope(input, verbose: false);

            Assert.Equal("PropertyRollbackUnreconciled", (string)trimmed["code"]);
            Assert.Equal("Inspect the parts listed in changedParts, restore them, then retry.", (string)trimmed["hint"]);
            Assert.True(JToken.DeepEquals(new JArray("WebForm"), trimmed["changedParts"]));
        }

        private static JObject FailedRollback() => new JObject
        {
            ["code"] = "PropertyVerificationFailed",
            ["message"] = "The saved property 'MasterPage' could not be independently read back.",
            ["rollbackAttempted"] = true,
            ["rollbackVerified"] = false,
            ["propertiesRestored"] = new JArray("MasterPage"),
            ["propertiesNotRestored"] = new JArray(),
            ["changedParts"] = new JArray("Documentation", "WebForm"),
            ["changedLines"] = new JArray(new JObject { ["part"] = "WebForm", ["side"] = "before", ["line"] = "<table name=\"T\" />" })
        };
    }
}
