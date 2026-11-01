using GxMcp.Worker.Helpers;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// The property rollback compares the WebForm and Documentation parts after removing the representation noise the SDK adds
    /// (layout ids and empty default entries). Real differences in the text stay, and the other parts are not touched.
    /// </summary>
    public class PropertyPartNormalizerTests
    {
        [Fact]
        public void LayoutIdsOfAWebFormDoNotCountAsADifference()
        {
            string first = "<WebForm><table id=\"11111111-2222-3333-4444-555555555555\" name=\"TableMain\" /></WebForm>";
            string second = "<WebForm><table id=\"66666666-7777-8888-9999-aaaaaaaaaaaa\" name=\"TableMain\" /></WebForm>";

            Assert.Equal(
                PropertyPartNormalizer.Normalize("WebForm", first),
                PropertyPartNormalizer.Normalize("WebForm", second));
        }

        [Fact]
        public void AnIdAttributeIsIgnoredWhateverItsValue()
        {
            Assert.Equal(
                PropertyPartNormalizer.Normalize("WebForm", "<WebForm><table id=\"1\" name=\"T\" /></WebForm>"),
                PropertyPartNormalizer.Normalize("WebForm", "<WebForm><table id=\"2\" name=\"T\" /></WebForm>"));
        }

        [Fact]
        public void AGuidValueInAnyAttributeDoesNotCountAsADifference()
        {
            string first = "<WebForm><cell ref=\"11111111-2222-3333-4444-555555555555\" name=\"C\" /></WebForm>";
            string second = "<WebForm><cell ref=\"66666666-7777-8888-9999-aaaaaaaaaaaa\" name=\"C\" /></WebForm>";

            Assert.Equal(
                PropertyPartNormalizer.Normalize("WebForm", first),
                PropertyPartNormalizer.Normalize("WebForm", second));
        }

        [Fact]
        public void EmptyDefaultEntriesOfADocumentationPartDoNotCountAsADifference()
        {
            string withDefaults = "<Documentation><Entry /><Entry></Entry></Documentation>";
            string withoutDefaults = "<Documentation />";

            Assert.Equal(
                PropertyPartNormalizer.Normalize("Documentation", withoutDefaults),
                PropertyPartNormalizer.Normalize("Documentation", withDefaults));
        }

        [Fact]
        public void ARealTextDifferenceInAWebFormStillDiffers()
        {
            string first = "<WebForm><cell name=\"C\">Name</cell></WebForm>";
            string second = "<WebForm><cell name=\"C\">Other</cell></WebForm>";

            Assert.NotEqual(
                PropertyPartNormalizer.Normalize("WebForm", first),
                PropertyPartNormalizer.Normalize("WebForm", second));
        }

        [Fact]
        public void AnElementThatHasTextIsNotRemovedAsEmpty()
        {
            string normalized = PropertyPartNormalizer.Normalize("Documentation", "<Documentation><Note>kept</Note></Documentation>");

            Assert.Contains("<Note>kept</Note>", normalized);
        }

        [Fact]
        public void OtherPartsComeBackUnchanged()
        {
            string events = "<Events id=\"11111111-2222-3333-4444-555555555555\"><Entry /></Events>";

            Assert.Equal(events, PropertyPartNormalizer.Normalize("Events", events));
        }

        [Fact]
        public void TextThatIsNotWellFormedComesBackUnchanged()
        {
            string broken = "<WebForm><table id=\"1\"";

            Assert.Equal(broken, PropertyPartNormalizer.Normalize("WebForm", broken));
        }

        [Fact]
        public void TheNormalizedPartsAreTheWebFormAndTheDocumentationOnly()
        {
            Assert.True(PropertyPartNormalizer.IsNormalized("WebForm"));
            Assert.True(PropertyPartNormalizer.IsNormalized("documentation"));
            Assert.False(PropertyPartNormalizer.IsNormalized("Variables"));
            Assert.False(PropertyPartNormalizer.IsNormalized("Events"));
        }
    }
}
