using GxMcp.Worker.Helpers;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class WebFormSchemaHintsTests
    {
        [Fact]
        public void LegacyObservedAttributes_AreAccepted()
        {
            const string xml =
                "<BODY><TABLE align=\"center\"><TR><TD vAlign=\"middle\">" +
                "<gxTextBlock Event=\"'Select'\" ReturnOnClick=\"true\" GxFormat=\"Text\" />" +
                "</TD></TR></TABLE></BODY>";

            var hits = WebFormSchemaHints.ScanForRejectedAttributes(xml);

            Assert.Empty(hits);
        }

        [Fact]
        public void UnknownAttribute_IsUnverified_NotGuaranteedSanitization()
        {
            const string xml = "<BODY><TABLE data-legacy-shape=\"keep\"><gxTextBlock /></TABLE></BODY>";

            var hit = Assert.Single(WebFormSchemaHints.ScanForRejectedAttributes(xml));

            Assert.Equal("data-legacy-shape", hit.Attribute);
            Assert.Contains("unverified", hit.Reason.ToLowerInvariant());
            Assert.DoesNotContain("will be sanitised", hit.Reason.ToLowerInvariant());
        }
    }
}
