using System.Linq;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class DataSelectorReadServiceTests
    {
        [Fact]
        public void BuildResponse_PreservesOrderedDefinitionAndMetadata()
        {
            var snapshot = CompleteSnapshot();

            JObject result = DataSelectorReadService.BuildResponse(snapshot, new[]
            {
                "parameters", "conditions", "orders", "definedBy", "baseTransaction", "baseTable", "structure"
            });

            Assert.Equal("SelectorTeste", (string)result["name"]);
            Assert.Equal("DataSelector", (string)result["type"]);
            Assert.True((bool)result["persisted"]);
            Assert.True((bool)result["readOnly"]);
            Assert.Equal("token-123", (string)result["versionToken"]);
            Assert.Empty((JArray)result["implicitOperations"]);

            JArray parameters = (JArray)result["parameters"];
            Assert.Equal(new[] { "ClienteId", "DataInicial" }, parameters.Select(p => (string)p["name"]));
            Assert.Equal(new[] { 1, 2 }, parameters.Select(p => (int)p["ordinal"]));
            Assert.All(parameters, p => Assert.Equal("in", (string)p["direction"]));

            JArray conditions = (JArray)result["conditions"];
            Assert.Equal("ClienteId = &ClienteId", (string)conditions[0]["expression"]);
            Assert.Equal("PedidoData >= &DataInicial and not PedidoCancelado", (string)conditions[1]["expression"]);

            JObject order = (JObject)result["orders"][0];
            Assert.Equal("PedidoData DESCENDING WHEN &MaisRecent", (string)order["expression"]);
            Assert.Equal("Descending", (string)order["direction"]);
            Assert.Equal("&MaisRecent", (string)order["condition"]);
            Assert.Equal("PedidoData", (string)order["items"][0]["name"]);

            Assert.Equal(new[] { "PedidoId", "ClienteId" }, result["definedBy"].Select(v => (string)v));
            Assert.Equal(new[] { "PedidoId", "ClienteId", "PedidoData", "ClienteNome" },
                result["structure"]["attributes"].Select(v => (string)v));
            Assert.Equal("defined by PedidoId, ClienteId", (string)result["structure"]["expression"]);
            Assert.Equal("semanticProjection", (string)result["structure"]["expressionKind"]);
            Assert.Equal(2, result["structure"]["parameters"].Count());
            Assert.Equal(2, result["structure"]["conditions"].Count());
            Assert.Equal("Pedido", (string)result["baseTransaction"]["name"]);
            Assert.Equal("PEDIDO", (string)result["baseTable"]["physicalName"]);
            Assert.Equal("IX_PEDIDO_DATA", (string)result["baseTable"]["indexes"][0]["name"]);
            Assert.Empty((JArray)result["unsupportedParts"]);
        }

        [Fact]
        public void BuildResponse_ReportsUnavailableSdkPartsInsteadOfEmptyCollections()
        {
            JObject result = DataSelectorReadService.BuildResponse(
                CompleteSnapshot(),
                new[] { "projection", "joins", "unknownPart" });

            Assert.Null(result["projection"]);
            Assert.Null(result["joins"]);
            JArray unsupported = (JArray)result["unsupportedParts"];
            Assert.Equal(new[] { "projection", "joins", "unknownpart" },
                unsupported.Select(v => (string)v["part"]));
            Assert.All(unsupported, item => Assert.False(string.IsNullOrWhiteSpace((string)item["reason"])));
        }

        [Fact]
        public void BuildResponse_IsDeterministicAndDoesNotAddUnrequestedDefinitionParts()
        {
            var snapshot = CompleteSnapshot();

            string first = DataSelectorReadService.BuildResponse(snapshot, new[] { "conditions" }).ToString();
            string second = DataSelectorReadService.BuildResponse(snapshot, new[] { "conditions" }).ToString();
            JObject parsed = JObject.Parse(first);

            Assert.Equal(first, second);
            Assert.NotNull(parsed["conditions"]);
            Assert.Null(parsed["parameters"]);
            Assert.Null(parsed["orders"]);
            Assert.Null(parsed["baseTable"]);
        }

        [Fact]
        public void BuildResponse_DoesNotTrimPersistedConditionWhitespace()
        {
            var snapshot = CompleteSnapshot();
            snapshot.Conditions[0].Expression = "ClienteId = &ClienteId\r\n";

            JObject result = DataSelectorReadService.BuildResponse(snapshot, new[] { "conditions" });

            Assert.Equal("ClienteId = &ClienteId\r\n", (string)result["conditions"][0]["expression"]);
        }

        [Fact]
        public void BuildResponse_ExplainsAmbiguousBaseResolution()
        {
            var snapshot = CompleteSnapshot();
            snapshot.BaseTable = null;
            snapshot.BaseTransaction = string.Empty;
            snapshot.BaseResolution = "More than one table contains every referenced attribute.";

            JObject result = DataSelectorReadService.BuildResponse(snapshot, new[] { "baseTable", "baseTransaction" });

            Assert.False((bool)result["baseTable"]["resolved"]);
            Assert.Contains("More than one table", (string)result["baseTable"]["reason"]);
            Assert.False((bool)result["baseTransaction"]["resolved"]);
        }

        private static DataSelectorReadService.Snapshot CompleteSnapshot()
        {
            var snapshot = new DataSelectorReadService.Snapshot
            {
                Name = "SelectorTeste",
                VersionToken = string.Concat("token", "-123"),
                StructureExpression = "defined by PedidoId, ClienteId",
                BaseTransaction = "Pedido",
                BaseResolution = "Resolved uniquely from complete referenced-attribute coverage.",
                BaseTable = new DataSelectorReadService.TableSnapshot { Name = "PEDIDO" }
            };
            snapshot.Parameters.Add(new DataSelectorReadService.ParameterSnapshot
            {
                Name = "ClienteId", Type = "DClienteId", ContentKind = "variable", Ordinal = 1
            });
            snapshot.Parameters.Add(new DataSelectorReadService.ParameterSnapshot
            {
                Name = "DataInicial", Type = "Date", ContentKind = "variable", Ordinal = 2
            });
            snapshot.Conditions.Add(new DataSelectorReadService.ExpressionSnapshot
            {
                Expression = "ClienteId = &ClienteId", Ordinal = 1
            });
            snapshot.Conditions.Add(new DataSelectorReadService.ExpressionSnapshot
            {
                Expression = "PedidoData >= &DataInicial and not PedidoCancelado", Ordinal = 2
            });
            var order = new DataSelectorReadService.OrderSnapshot
            {
                Expression = "PedidoData DESCENDING WHEN &MaisRecent",
                Direction = "Descending",
                Condition = "&MaisRecent",
                Ordinal = 1
            };
            order.Items.Add(new DataSelectorReadService.OrderMemberSnapshot
            {
                Name = "PedidoData", Direction = "Descending"
            });
            snapshot.Orders.Add(order);
            snapshot.DefinedBy.AddRange(new[] { "PedidoId", "ClienteId" });
            // Deliberately represents attributes from a base and an extended table.
            snapshot.ReferencedAttributes.AddRange(new[] { "PedidoId", "ClienteId", "PedidoData", "ClienteNome" });
            var index = new DataSelectorReadService.IndexSnapshot { Name = "IX_PEDIDO_DATA", Type = "User" };
            index.Attributes.Add(new DataSelectorReadService.IndexAttributeSnapshot
            {
                Name = "PedidoData", Direction = "Descending"
            });
            snapshot.BaseTable.Indexes.Add(index);
            return snapshot;
        }
    }
}
