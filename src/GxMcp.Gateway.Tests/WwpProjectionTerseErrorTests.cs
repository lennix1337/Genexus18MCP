using GxMcp.Gateway;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    // #638: the terse envelope of a refused add_grid_variable write (WwpProjectionNotConfirmed). It keeps the write receipt
    // the caller already reads; the WebForm projection that the refusal carries is added to it in the next change.
    public class WwpProjectionTerseErrorTests
    {
        private const string Refusal = @"{status:'error',target:'SyntheticGridHost',
            error:{code:'WwpProjectionNotConfirmed',message:'The projected parent WebForm holds 0 grid controls named Grid1 instead of one.',hint:'Inspect the worker diagnostics.'},
            exceptionType:'WwpTabException',failureTrace:'Synthetic worker trace',persisted:false,saved:false,partialPersistenceDetected:true,
            rollback:{performed:true,patternRestoredExactly:true,webFormRestoredExactly:true,applyOnSaveRestoredExactly:true,exact:true},
            webFormProjection:{confirmed:false,variable:'&SyntheticVariable',variableLayoutRef:null,requestedGridControlNames:['Grid1'],webFormGrids:[],message:'The projected parent WebForm holds 0 grid controls named Grid1 instead of one.'}}";

        [Fact]
        public void TheRefusalKeepsItsCodeMessageTargetAndWriteReceipt()
        {
            var output = McpRouter.TrimErrorEnvelope(JObject.Parse(Refusal), verbose: false);

            Assert.Equal("WwpProjectionNotConfirmed", (string?)output["code"]);
            Assert.Equal("The projected parent WebForm holds 0 grid controls named Grid1 instead of one.", (string?)output["message"]);
            Assert.Equal("SyntheticGridHost", (string?)output["target"]);
            Assert.False((bool)output["persisted"]!);
            Assert.False((bool)output["saved"]!);
            Assert.True((bool)output["partialPersistenceDetected"]!);
            Assert.True((bool)output["rollback"]!["exact"]!);
        }

        [Fact]
        public void TheRefusalCarriesTheWebFormProjectionItWasVerifiedAgainst()
        {
            var output = McpRouter.TrimErrorEnvelope(JObject.Parse(Refusal), verbose: false);

            var projection = Assert.IsType<JObject>(output["webFormProjection"]);
            Assert.False((bool)projection["confirmed"]!);
            Assert.Equal("&SyntheticVariable", (string?)projection["variable"]);
            Assert.Equal("Grid1", (string?)Assert.Single((JArray)projection["requestedGridControlNames"]!));
            Assert.Empty((JArray)projection["webFormGrids"]!);
            Assert.Equal("The projected parent WebForm holds 0 grid controls named Grid1 instead of one.", (string?)projection["message"]);
        }

        [Fact]
        public void TheRestOfTheTerseEnvelopeDoesNotChangeWithTheProjection()
        {
            var withoutProjection = JObject.Parse(Refusal);
            withoutProjection.Remove("webFormProjection");

            var output = McpRouter.TrimErrorEnvelope(JObject.Parse(Refusal), verbose: false);
            output.Remove("webFormProjection");

            Assert.True(JToken.DeepEquals(McpRouter.TrimErrorEnvelope(withoutProjection, verbose: false), output));
        }

        [Fact]
        public void AnErrorWithoutTheProjectionGainsNoField()
        {
            var input = JObject.Parse(@"{status:'error',error:{code:'WwpProjectionFailed',message:'The PatternInstance persisted, but the WorkWithPlus SDK did not project the parent WebForm.'}}");

            Assert.Null(McpRouter.TrimErrorEnvelope(input, verbose: false)["webFormProjection"]);
        }

        [Fact]
        public void AnotherCodeDoesNotGainTheProjection()
        {
            var input = JObject.Parse(@"{status:'error',error:{code:'WriteFailed',message:'m'},webFormProjection:{confirmed:false,message:'m'}}");

            Assert.Null(McpRouter.TrimErrorEnvelope(input, verbose: false)["webFormProjection"]);
        }

        [Fact]
        public void ALargeFormKeepsItsGridListCappedWithItsTotal()
        {
            var input = JObject.Parse(Refusal);
            input["webFormProjection"]!["webFormGrids"] = GridNames(60);

            var projection = (JObject)McpRouter.TrimErrorEnvelope(input, verbose: false)["webFormProjection"]!;

            Assert.Equal(50, ((JArray)projection["webFormGrids"]!).Count);
            Assert.Equal(60, (int)projection["webFormGridsTotal"]!);
            Assert.Equal("Grid0", (string?)((JArray)projection["webFormGrids"]!)[0]);
        }

        [Fact]
        public void AListOfExactlyTheLimitGainsNoTotal()
        {
            var input = JObject.Parse(Refusal);
            input["webFormProjection"]!["webFormGrids"] = GridNames(50);

            var projection = (JObject)McpRouter.TrimErrorEnvelope(input, verbose: false)["webFormProjection"]!;

            Assert.Equal(50, ((JArray)projection["webFormGrids"]!).Count);
            Assert.Null(projection["webFormGridsTotal"]);
        }

        [Fact]
        public void AListOneOverTheLimitIsCutToTheLimitWithItsTotal()
        {
            var input = JObject.Parse(Refusal);
            input["webFormProjection"]!["webFormGrids"] = GridNames(51);

            var projection = (JObject)McpRouter.TrimErrorEnvelope(input, verbose: false)["webFormProjection"]!;

            var kept = (JArray)projection["webFormGrids"]!;
            Assert.Equal(50, kept.Count);
            Assert.Equal("Grid49", (string?)kept[49]);
            Assert.Equal(51, (int)projection["webFormGridsTotal"]!);
        }

        [Fact]
        public void TheVerboseEnvelopeKeepsTheWholeProjection()
        {
            var input = JObject.Parse(Refusal);
            input["webFormProjection"]!["webFormGrids"] = GridNames(60);

            var projection = (JObject)McpRouter.TrimErrorEnvelope(input, verbose: true)["webFormProjection"]!;

            Assert.Equal(60, ((JArray)projection["webFormGrids"]!).Count);
            Assert.Null(projection["webFormGridsTotal"]);
        }

        private static JArray GridNames(int count)
        {
            var names = new JArray();
            for (int i = 0; i < count; i++) names.Add("Grid" + i);
            return names;
        }
    }
}
