using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// Issue #886: a batch add over a variable that already exists reports WriteNoChange;
    /// the result has to say that add does not change types and where to go instead.
    /// </summary>
    public class ExistingVariableHintTests
    {
        [Fact]
        public void ABatchThatFoundExistingVariablesPointsToModify()
        {
            var outcomes = new JArray(new JObject { ["name"] = "Dt", ["itemStatus"] = "Exists" });

            var result = WriteService.BuildVariableBatchResult(0, 1, 0, outcomes);

            Assert.Contains("genexus_modify_variable", (string)result["hint"]);
            Assert.Equal(1, (int)result["counts"]["existed"]);
        }

        [Fact]
        public void ABatchWithNoExistingVariableCarriesNoHint()
        {
            var result = WriteService.BuildVariableBatchResult(2, 0, 0, new JArray());

            Assert.Null(result["hint"]);
        }
    }
}
