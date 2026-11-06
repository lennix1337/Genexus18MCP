using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// Issue #886: a successful type change answers with the code "VariableRenamed";
    /// the payload must say it was a retype and that the persisted object was read back.
    /// </summary>
    public class RetypeResultStateTests
    {
        [Fact]
        public void ARetypeResultStatesThatItWasRetypedAndConfirmed()
        {
            var payload = WriteService.MarkRetypeConfirmed(new JObject { ["persistedType"] = "Date" });

            Assert.True((bool)payload["retyped"]);
            Assert.Equal("confirmed", (string)payload["persistedState"]);
            Assert.Equal("Date", (string)payload["persistedType"]);
        }
    }
}
