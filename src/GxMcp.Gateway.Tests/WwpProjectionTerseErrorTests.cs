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
    }
}
