using GxMcp.Worker.Models;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // The reply of a single set that the SDK verified names the property as the SDK holds it. A requested name that stands for
    // another SDK name is reported as asked, with the SDK name as nativeName and in nativeNames.
    public sealed class PropertyReplyNamesTests
    {
        [Fact]
        public void AVerifiedAliasReportsTheRequestedNameAndTheSdkNameAsNativeName()
        {
            string verified = McpResponse.Ok(target: "SynthPanel", code: "PropertyApplied",
                result: new JObject { ["property"] = "SHOWMASTERPAGE_POPUP", ["value"] = "True", ["persisted"] = "True", ["persistedVerified"] = true });

            JObject reply = JObject.Parse(PropertyService.ReportRequestedName(verified, "ShowMasterPageWhenPopUp", "SHOWMASTERPAGE_POPUP"));
            JObject result = (JObject)reply["result"];

            Assert.Equal("ok", reply["status"]?.ToString());
            Assert.Equal("PropertyApplied", reply["code"]?.ToString());
            Assert.Equal("ShowMasterPageWhenPopUp", result["property"]?.ToString());
            Assert.Equal("SHOWMASTERPAGE_POPUP", result["nativeName"]?.ToString());
            Assert.Equal("SHOWMASTERPAGE_POPUP", result["nativeNames"]?["ShowMasterPageWhenPopUp"]?.ToString());
            Assert.Equal("True", result["value"]?.ToString());
            Assert.Equal("True", result["persisted"]?.ToString());
            Assert.Equal(true, (bool?)result["persistedVerified"]);
        }

        [Fact]
        public void ASetUnderItsOwnNameLeavesTheReplyUnchanged()
        {
            string verified = McpResponse.Ok(target: "SynthPanel", code: "PropertyApplied",
                result: new JObject { ["property"] = "Description", ["value"] = "x" });

            Assert.Equal(verified, PropertyService.ReportRequestedName(verified, "Description", "Description"));
        }

        [Fact]
        public void AFailedVerificationLeavesTheErrorUnchanged()
        {
            string error = McpResponse.Err(code: "PropertyNotPersisted", message: "The saved value was not confirmed.", target: "SynthPanel");

            Assert.Equal(error, PropertyService.ReportRequestedName(error, "ShowMasterPageWhenPopUp", "SHOWMASTERPAGE_POPUP"));
        }
    }
}
