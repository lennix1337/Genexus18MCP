using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class PatternSettingsCacheTests
    {
        [Theory]
        [InlineData("genexus_wwp", "{'action':'settings_templates'}")]
        [InlineData("genexus_wwp", "{'action':'settings_read','baseVersion':'previous'}")]
        [InlineData("genexus_wwp", "{'action':'settings_edit','dryRun':true}")]
        [InlineData("genexus_read", "{'name':'WorkWithPlus','type':'Pattern Settings'}")]
        [InlineData("genexus_read", "{'name':'PatternSettings:WorkWithPlus'}")]
        [InlineData("genexus_read", "{'guid':'settings-guid','part':'PatternSettings'}")]
        [InlineData("genexus_read", "{'guid':'settings-guid','parts':['Rules','PatternSettings']}")]
        [InlineData("genexus_read", "{'guid':'11111111-1111-1111-1111-111111111111','part':'Source'}")]
        [InlineData("genexus_read", "{'guid':'11111111-1111-1111-1111-111111111111'}")]
        [InlineData("genexus_read", "{'name':'11111111-1111-1111-1111-111111111111','part':'Source'}")]
        [InlineData("genexus_read", "{'name':'11111111-1111-1111-1111-111111111111'}")]
        [InlineData("genexus_read", "{'entityKey':'11111111-1111-1111-1111-111111111111-1','part':'Source'}")]
        public void EverySettingsObservationBypassesBothCacheKeyPaths(string tool, string json)
        {
            var args = JObject.Parse(json);
            Assert.True(Program.IsPatternSettingsObservation(tool, args));
            Assert.Null(Program.CreateSemanticCacheKey("sample", tool, args, false, false));
            Assert.Null(Program.CreateSemanticCacheKey("sample", tool, args, false, false, 1, "model", "environment"));
        }

        [Fact]
        public void OrdinarySourceReadsStillUseCache()
        {
            var args = JObject.Parse("{'name':'Sample','guid':'11111111-1111-1111-1111-111111111111','type':'Procedure','part':'Source'}");
            Assert.False(Program.IsPatternSettingsObservation("genexus_read", args));
            Assert.NotNull(Program.CreateSemanticCacheKey("sample", "genexus_read", args, false, false));
            Assert.NotNull(Program.CreateSemanticCacheKey("sample", "genexus_read", args, false, false, 1, null, null));
        }
    }
}
