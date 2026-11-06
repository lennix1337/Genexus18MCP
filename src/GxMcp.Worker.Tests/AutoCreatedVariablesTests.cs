using System.Collections.Generic;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// Issue #889: a Source save declares undeclared &amp;variables on its own, and the response said nothing.
    /// </summary>
    public class AutoCreatedVariablesTests
    {
        private static IReadOnlyList<VariableInjector.InjectedVariable> Created(params string[] names)
        {
            var list = new List<VariableInjector.InjectedVariable>();
            foreach (var n in names)
                list.Add(new VariableInjector.InjectedVariable { Name = n, Type = "VARCHAR", Length = 100, Decimals = 0 });
            return list;
        }

        [Fact]
        public void TheWriteResultListsEachVariableTheSaveDeclared()
        {
            var result = new JObject { ["sdkSaveCompleted"] = true };

            WritePolicy.AddAutoCreatedVariables(result, Created("Hoy", "Total"));

            var arr = (JArray)result["autoCreatedVariables"];
            Assert.Equal(2, arr.Count);
            Assert.Equal("&Hoy", (string)arr[0]["name"]);
            Assert.Equal("VARCHAR", (string)arr[0]["type"]);
            Assert.Equal(100, (int)arr[0]["length"]);
            Assert.Contains("&Hoy, &Total", (string)((JArray)result["warnings"])[0]);
        }

        [Fact]
        public void ASaveThatDeclaredNothingLeavesTheResultUntouched()
        {
            var result = new JObject { ["sdkSaveCompleted"] = true };

            WritePolicy.AddAutoCreatedVariables(result, Created());
            WritePolicy.AddAutoCreatedVariables(result, null);

            Assert.Null(result["autoCreatedVariables"]);
            Assert.Null(result["warnings"]);
        }

        [Fact]
        public void TheWarningFollowsTheShapeOfTheWarningsAlreadyInTheResult()
        {
            var structured = new JObject { ["warnings"] = new JArray(new JObject { ["code"] = "LintX", ["message"] = "m" }) };
            var plain = new JObject { ["warnings"] = new JArray("validation was bypassed") };

            WritePolicy.AddAutoCreatedVariables(structured, Created("A"));
            WritePolicy.AddAutoCreatedVariables(plain, Created("A"));

            Assert.Equal("AutoCreatedVariables", (string)structured["warnings"][1]["code"]);
            Assert.Equal(JTokenType.String, plain["warnings"][1].Type);
        }
    }
}
