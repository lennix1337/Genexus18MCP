using GxMcp.Gateway;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// A style write or an XPZ import that the SDK refuses because a gx-image() points at an Image object nobody has must
    /// keep, through the terse error projection, the list of those names: it is the one thing the caller can act on.
    /// </summary>
    public class TerseStyleRefusalTests
    {
        // The envelope WriteService.CreateThemeWriteError returns (code, message and hint under error; the style details,
        // the part and the SDK messages beside it).
        private const string StyleRefusal =
            "{\"status\":\"error\",\"target\":\"SynthBrandDS\"," +
            "\"error\":{\"code\":\"ThemeStyleUnresolvedReferences\"," +
            "\"message\":\"Theme/StyleSheet write refused: the style source refers to Image objects that do not exist in the Knowledge Base.\"," +
            "\"hint\":\"Create or import the Image objects listed in unresolvedImageReferences, then repeat the write. Nothing was changed.\"}," +
            "\"part\":\"Styles\"," +
            "\"details\":\"The style source refers to 2 Image objects that do not exist in this Knowledge Base: SynthLogo, SynthWallpaper.\"," +
            "\"unresolvedImageReferences\":[\"SynthLogo\",\"SynthWallpaper\"]," +
            "\"sdkMessages\":[{\"level\":\"Error\",\"text\":\"synthetic SDK message\"}]," +
            "\"persisted\":false}";

        [Fact]
        public void Terse_style_refusal_keeps_the_unresolved_image_names_and_the_sdk_messages()
        {
            var trimmed = McpRouter.TrimErrorEnvelope(JObject.Parse(StyleRefusal), verbose: false);

            Assert.Equal("ThemeStyleUnresolvedReferences", trimmed["code"]?.ToString());
            Assert.Equal("Styles", trimmed["part"]?.ToString());
            Assert.Contains("SynthLogo, SynthWallpaper", trimmed["details"]?.ToString());
            Assert.Equal(new[] { "SynthLogo", "SynthWallpaper" }, ((JArray)trimmed["unresolvedImageReferences"]!).ToObject<string[]>());
            Assert.Equal("synthetic SDK message", trimmed["sdkMessages"]?[0]?["text"]?.ToString());
            Assert.False(trimmed["persisted"]?.ToObject<bool>());
        }

        // The envelope TransferService.ImportDeclined returns when the package refers to Image objects nobody has.
        private const string ImportRefusal =
            "{\"status\":\"error\"," +
            "\"error\":{\"code\":\"TransferImportDeclined\"," +
            "\"message\":\"GeneXus ImportFile returned false; import was not confirmed.\"," +
            "\"hint\":\"The package refers, through gx-image(), to Image objects that are neither in the package nor in the Knowledge Base (SynthLogo; see unresolvedImageReferences).\"}," +
            "\"unresolvedImageReferences\":[\"SynthLogo\"],\"imported\":false}";

        [Fact]
        public void Terse_declined_import_keeps_the_unresolved_image_names()
        {
            var trimmed = McpRouter.TrimErrorEnvelope(JObject.Parse(ImportRefusal), verbose: false);

            Assert.Equal("TransferImportDeclined", trimmed["code"]?.ToString());
            Assert.Equal(new[] { "SynthLogo" }, ((JArray)trimmed["unresolvedImageReferences"]!).ToObject<string[]>());
        }
    }
}
