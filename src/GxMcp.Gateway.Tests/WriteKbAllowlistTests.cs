using GxMcp.Gateway;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// GXMCP_WRITE_KB_ALLOW: an operator who points the server at a shared or real KB can restrict
    /// every mutation to disposable KBs. A refused write must say so with wrote:false instead of
    /// leaving the caller to guess whether a probe touched the KB.
    /// </summary>
    public class WriteKbAllowlistTests
    {
        [Theory]
        [InlineData("C:\\KBs\\KBTeste", true)]
        [InlineData("c:/kbs/kbcreateteste749", true)]
        [InlineData("C:\\KBs\\KBTeste\\", true)]
        [InlineData("C:\\KBs\\Producao", false)]
        [InlineData("C:\\Other\\KBTeste", false)]
        [InlineData("", false)]
        public void Pattern_MatchesWholeKbPath_CaseAndSeparatorInsensitive(string path, bool allowed)
        {
            var list = WriteKbAllowlist.Parse("C:\\KBs\\*teste*");
            Assert.Equal(allowed, list.Allows(path));
        }

        [Fact]
        public void NoPattern_AllowsEverything_SoTheDefaultBehaviourIsUnchanged()
        {
            Assert.True(WriteKbAllowlist.Parse(null).Allows("D:\\Real\\KB"));
            Assert.True(WriteKbAllowlist.Parse("  ").Allows(""));
        }

        [Fact]
        public void SeveralPatterns_AreSeparatedBySemicolon()
        {
            var list = WriteKbAllowlist.Parse("C:\\KBs\\*teste*; D:\\Sandbox\\*");
            Assert.True(list.Allows("D:\\Sandbox\\Any"));
            Assert.False(list.Allows("D:\\Real\\KB"));
        }

        [Fact]
        public void Refusal_NamesTheKbAndStatesNothingWasWritten()
        {
            var envelope = WriteKbAllowlist.BuildRefusal("genexus_edit", "C:\\KBs\\Producao", "C:\\KBs\\*teste*");
            Assert.Equal("error", envelope["status"]?.ToString());
            Assert.Equal("WriteBlockedKbNotAllowed", envelope["code"]?.ToString());
            Assert.False(envelope["wrote"]!.Value<bool>());
            Assert.False(envelope["persisted"]!.Value<bool>());
            Assert.Contains("C:\\KBs\\Producao", envelope["message"]!.ToString());
        }
    }
}
