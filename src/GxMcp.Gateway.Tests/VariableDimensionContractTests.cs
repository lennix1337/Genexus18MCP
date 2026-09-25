using System.Linq;
using GxMcp.Gateway.Routers;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public sealed class VariableDimensionContractTests
    {
        [Fact]
        public void Schema_ExposesSingleAndBatchDimensionFields()
        {
            var definitions = JArray.Parse(System.IO.File.ReadAllText(FindToolDefinitions()));
            var variable = definitions.First(x => x["name"]?.ToString() == "genexus_variable");
            var properties = variable["inputSchema"]?["properties"];
            var batchProperties = properties?["variables"]?["items"]?["properties"];

            Assert.NotNull(properties?["dimensions"]);
            Assert.NotNull(properties?["dimensionSizes"]);
            Assert.NotNull(batchProperties?["dimensions"]);
            Assert.NotNull(batchProperties?["dimensionSizes"]);
            Assert.Equal(1, properties["dimensions"]?["minimum"]?.ToObject<int>());
            Assert.Equal(2, properties["dimensions"]?["maximum"]?.ToObject<int>());
            Assert.Equal(1, properties["dimensionSizes"]?["minItems"]?.ToObject<int>());
            Assert.Equal(2, properties["dimensionSizes"]?["maxItems"]?.ToObject<int>());
        }

        [Fact]
        public void Validator_RejectsCollectionDimensionConflict()
        {
            var result = GatewayArgsValidator.Validate("genexus_variable", new JObject
            {
                ["action"] = "add",
                ["name"] = "MyProc",
                ["varName"] = "Values",
                ["dimensions"] = 1,
                ["dimensionSizes"] = new JArray(10),
                ["collection"] = true
            });

            Assert.False(result.Ok);
            Assert.Contains(result.Violations, v => v.Path == "collection");
        }

        [Fact]
        public void Validator_RejectsMismatchedAndMalformedBatchSizes()
        {
            var result = GatewayArgsValidator.Validate("genexus_variable", new JObject
            {
                ["action"] = "add",
                ["name"] = "MyProc",
                ["variables"] = new JArray(new JObject
                {
                    ["varName"] = "Values",
                    ["dimensions"] = 2,
                    ["dimensionSizes"] = new JArray(10)
                })
            });

            Assert.False(result.Ok);
            Assert.Contains(result.Violations, v => v.Path == "variables[0].dimensionSizes");

            var malformed = GatewayArgsValidator.Validate("genexus_variable", new JObject
            {
                ["action"] = "add",
                ["name"] = "MyProc",
                ["variables"] = new JArray(new JObject
                {
                    ["varName"] = "Values",
                    ["dimensions"] = 1,
                    ["dimensionSizes"] = "10"
                })
            });
            Assert.False(malformed.Ok);
        }

        [Fact]
        public void Validator_AcceptsAWellFormedVectorRequest()
        {
            var result = GatewayArgsValidator.Validate("genexus_variable", new JObject
            {
                ["action"] = "add",
                ["name"] = "MyProc",
                ["varName"] = "Values",
                ["dimensions"] = 1,
                ["dimensionSizes"] = new JArray(999)
            });

            Assert.True(result.Ok);
            Assert.Empty(result.Violations);
        }

        [Fact]
        public void Router_ForwardsSingleAndBatchDimensionArguments()
        {
            var single = JObject.FromObject(new OperationsRouter().ConvertToolCall(
                "genexus_variable",
                new JObject
                {
                    ["action"] = "modify",
                    ["name"] = "MyProc",
                    ["varName"] = "Values",
                    ["newTypeName"] = "Numeric(10)",
                    ["dimensions"] = 2,
                    ["dimensionSizes"] = new JArray(4, 5)
                })!);

            Assert.Equal(2, single["dimensions"]?.ToObject<int>());
            Assert.Equal(new[] { 4, 5 }, single["dimensionSizes"]?.ToObject<int[]>());

            var batch = JObject.FromObject(new OperationsRouter().ConvertToolCall(
                "genexus_variable",
                new JObject
                {
                    ["action"] = "add",
                    ["name"] = "MyProc",
                    ["variables"] = new JArray(new JObject
                    {
                        ["varName"] = "Values",
                        ["dimensions"] = 1,
                        ["dimensionSizes"] = new JArray(9)
                    })
                })!);
            Assert.Equal("AddVariable", batch["action"]?.ToString());
            Assert.Equal(1, batch["variables"]?[0]?["dimensions"]?.ToObject<int>());

            var businessComponent = JObject.FromObject(new OperationsRouter().ConvertToolCall(
                "genexus_variable",
                new JObject
                {
                    ["action"] = "add",
                    ["name"] = "MyProc",
                    ["varName"] = "OrderRecord",
                    ["objectType"] = "BusinessComponent",
                    ["objectName"] = "OrderRecord",
                    ["module"] = "Operations",
                    ["dimensions"] = 2,
                    ["dimensionSizes"] = new JArray(3, 4)
                })!);
            Assert.Equal(2, businessComponent["dimensions"]?.ToObject<int>());
            Assert.Equal(new[] { 3, 4 }, businessComponent["dimensionSizes"]?.ToObject<int[]>());
        }

        private static string FindToolDefinitions()
        {
            var directory = System.AppContext.BaseDirectory;
            for (int i = 0; i < 10; i++)
            {
                var candidate = System.IO.Path.Combine(directory, "src", "GxMcp.Gateway", "tool_definitions.json");
                if (System.IO.File.Exists(candidate)) return candidate;
                var parent = System.IO.Directory.GetParent(directory);
                if (parent == null) break;
                directory = parent.FullName;
            }
            throw new System.IO.FileNotFoundException("tool_definitions.json was not found.");
        }
    }
}
