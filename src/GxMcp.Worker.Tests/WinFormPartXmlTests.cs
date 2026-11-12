using System;
using GxMcp.Worker.Helpers;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // issue #963: the WinForm part is read and written as the SDK's own Part XML
    // (Document/Form/Canvas/Elements), the same shape an XPZ export carries.
    public class WinFormPartXmlTests
    {
        private const string PartGuid = "4c28dfb9-f83b-46f0-9cf3-f7e090b525d5";

        private static string TextForm(string elements = "<Label><Properties /></Label>") =>
            "<Part type=\"" + PartGuid + "\"><Document><Form><Type>Text</Type><Class>Text</Class>"
            + "<ClassGuid>70027999-f0dc-4cf4-9373-8298a4f2dec1</ClassGuid><SizeUnit>ch</SizeUnit>"
            + "<Info><Properties /></Info><Canvas><Elements>" + elements + "</Elements></Canvas></Form></Document></Part>";

        [Fact]
        public void Validate_AcceptsAPartRootAndReturnsItUnchangedInMeaning()
        {
            string xml = WinFormPartXml.Validate(TextForm());

            Assert.Contains("<Document>", xml);
            Assert.Contains("<Type>Text</Type>", xml);
        }

        [Fact]
        public void Validate_WrapsABareDocumentRootInAPart()
        {
            string xml = WinFormPartXml.Validate("<Document><Form><Type>Windows</Type></Form></Document>");

            Assert.StartsWith("<Part type=\"" + PartGuid + "\"", xml);
            Assert.Contains("<Type>Windows</Type>", xml);
        }

        [Theory]
        [InlineData("not xml at all")]
        [InlineData("<Other><Document><Form><Type>Text</Type></Form></Document></Other>")]
        [InlineData("<Part type=\"11111111-2222-3333-4444-555555555555\"><Document><Form><Type>Text</Type></Form></Document></Part>")]
        [InlineData("<Part type=\"4c28dfb9-f83b-46f0-9cf3-f7e090b525d5\"><Properties /></Part>")]
        [InlineData("<Part><Document /></Part>")]
        [InlineData("<Part><Document><Form><Class>Text</Class></Form></Document></Part>")]
        [InlineData("<Part><Document><Form><Type>NoSuchFormType</Type></Form></Document></Part>")]
        [InlineData("<Part><Document><Form><Type>Text</Type><ClassGuid>not-a-guid</ClassGuid></Form></Document></Part>")]
        public void Validate_RejectsInputTheSdkDeserializerWouldIgnoreSilently(string input)
        {
            Assert.Throws<FormatException>(() => WinFormPartXml.Validate(input));
        }

        [Fact]
        public void CountElements_CountsEveryControlUnderEveryElementsContainer()
        {
            string xml = TextForm("<Label><Properties /></Label><Group><Elements><Label><Properties /></Label></Elements></Group>");

            Assert.Equal(3, WinFormPartXml.CountElements(xml));
        }

        [Fact]
        public void CountElements_IsZeroForAFormWithoutCanvas()
        {
            Assert.Equal(0, WinFormPartXml.CountElements("<Part><Document><Form><Type>Text</Type></Form></Document></Part>"));
        }

        [Fact]
        public void TakeCanonicalContent_ReturnsTheStagedFormAndRemovesItFromTheResponse()
        {
            var response = Newtonsoft.Json.Linq.JObject.Parse("{\"result\":{\"part\":\"WinForm\",\"canonicalContent\":\"<Part />\"}}");

            Assert.Equal("<Part />", WinFormPartXml.TakeCanonicalContent(response));
            Assert.Null(response["result"]["canonicalContent"]);
            Assert.Equal("WinForm", (string)response["result"]["part"]);
        }

        [Fact]
        public void TakeCanonicalContent_IsNullWhenTheResponseCarriesNone()
        {
            Assert.Null(WinFormPartXml.TakeCanonicalContent(Newtonsoft.Json.Linq.JObject.Parse("{\"result\":{}}")));
            Assert.Null(WinFormPartXml.TakeCanonicalContent(Newtonsoft.Json.Linq.JObject.Parse("{}")));
        }
    }
}
