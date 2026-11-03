using System;
using System.Linq;
using Xunit;
using GxMcp.Worker.Helpers;

namespace GxMcp.Worker.Tests
{
    public class LayoutGotchaScannerTests
    {
        // FR#1 (friction-report 2026-05-19): gxButton custom OnClickEvent in Form type="html"
        // compiles clean but the HTML generator wires data-gx-evt=5 (Enter) regardless. Scanner
        // must surface a warning so the agent doesn't waste a build cycle discovering this.
        [Fact]
        public void Scan_GxButtonCustomEventInHtmlForm_EmitsWarning()
        {
            var xml = @"<GxMultiForm>
                <Form id=""1"" type=""html"">
                    <gxButton id=""BtnFoo"" caption=""Foo"" OnClickEvent=""'DoFoo'"" />
                </Form>
            </GxMultiForm>";
            var hits = LayoutGotchaScanner.Scan(xml, _ => null, _ => false);
            Assert.Single(hits);
            Assert.Equal("GotchaGxButtonHtmlFormCustomEvent", hits[0].Code);
            Assert.Equal("Warning", hits[0].Severity);
            Assert.Equal("BtnFoo", hits[0].ControlId);
            Assert.Contains("DoFoo", hits[0].Message);
            Assert.Contains("Enter", hits[0].Message);
            Assert.NotNull(hits[0].Workaround);
        }

        [Fact]
        public void Scan_GxButtonEnterEvent_NoWarning()
        {
            // Enter (and Cancel / Refresh) are the only events the generator wires, so they are
            // legitimate values — don't warn.
            var xml = @"<GxMultiForm>
                <Form id=""1"" type=""html"">
                    <gxButton id=""BtnConfirm"" caption=""Confirm"" eventGX=""'Enter'"" />
                </Form>
            </GxMultiForm>";
            var hits = LayoutGotchaScanner.Scan(xml, _ => null, _ => false);
            Assert.Empty(hits);
        }

        [Fact]
        public void Scan_GxButtonCustomEventInLayoutForm_NoWarning()
        {
            // Form type="layout" supports <action onClickEvent> with custom events. We only flag
            // gxButton inside html forms.
            var xml = @"<GxMultiForm>
                <Form id=""2"" type=""layout"">
                    <gxButton id=""BtnFoo"" caption=""Foo"" OnClickEvent=""'DoFoo'"" />
                </Form>
            </GxMultiForm>";
            var hits = LayoutGotchaScanner.Scan(xml, _ => null, _ => false);
            Assert.Empty(hits);
        }

        // FR#2 (revised 2026-05-19): gxAttribute ControlType="Radio Button" or "Combo Box" in
        // <Form type="html"> always renders disabled, regardless of variable name or ReadOnly /
        // Enabled attributes. Original hypothesis (variable-name shadow) was disproved by live
        // probe: renaming the bound variable did not change the rendering.
        [Fact]
        public void Scan_GxAttributeRadioInHtmlForm_EmitsWarning()
        {
            var xml = @"<GxMultiForm>
                <Form id=""1"" type=""html"">
                    <gxAttribute AttID=""var:24"" ControlType=""Radio Button"" ReadOnly=""False"" />
                </Form>
            </GxMultiForm>";
            var hits = LayoutGotchaScanner.Scan(xml, attId => attId == "var:24" ? "RespRegProf" : null);
            Assert.Single(hits);
            Assert.Equal("GotchaGxAttributeHtmlFormDiscreteReadOnly", hits[0].Code);
            Assert.Contains("RespRegProf", hits[0].Message);
            Assert.Contains("Radio Button", hits[0].Message);
            Assert.Contains("Form type=\"layout\"", hits[0].Workaround);
        }

        [Fact]
        public void Scan_GxAttributeComboInHtmlForm_EmitsWarning()
        {
            var xml = @"<GxMultiForm>
                <Form id=""1"" type=""html"">
                    <gxAttribute AttID=""var:5"" ControlType=""Combo Box"" />
                </Form>
            </GxMultiForm>";
            var hits = LayoutGotchaScanner.Scan(xml, _ => null);
            Assert.Single(hits);
            Assert.Equal("GotchaGxAttributeHtmlFormDiscreteReadOnly", hits[0].Code);
            Assert.Contains("Combo Box", hits[0].Message);
        }

        [Fact]
        public void Scan_GxAttributeRadioInLayoutForm_NoWarning()
        {
            // Form type="layout" supports editable Radio / Combo. Only html-form is broken.
            var xml = @"<GxMultiForm>
                <Form id=""2"" type=""layout"">
                    <gxAttribute AttID=""var:24"" ControlType=""Radio Button"" />
                </Form>
            </GxMultiForm>";
            var hits = LayoutGotchaScanner.Scan(xml, _ => null);
            Assert.Empty(hits);
        }

        [Fact]
        public void Scan_GxAttributeTextInputInHtmlForm_NoWarning()
        {
            // Default ControlType (text input) is unaffected — renders editable in html form.
            var xml = @"<GxMultiForm>
                <Form id=""1"" type=""html"">
                    <gxAttribute AttID=""var:9"" ReadOnly=""False"" />
                </Form>
            </GxMultiForm>";
            var hits = LayoutGotchaScanner.Scan(xml, _ => null);
            Assert.Empty(hits);
        }

        [Fact]
        public void Scan_EmptyOrInvalidXml_ReturnsEmpty()
        {
            Assert.Empty(LayoutGotchaScanner.Scan("", _ => null));
            Assert.Empty(LayoutGotchaScanner.Scan((string)null, _ => null));
            Assert.Empty(LayoutGotchaScanner.Scan("<not closed", _ => null));
        }

        // W5 phase 2 rules — structural / schema gotchas.

        [Fact]
        public void Scan_GxAttributeMissingDataField_EmitsWarning()
        {
            var xml = @"<GxMultiForm><Form id=""1"" type=""html"">
                <gxAttribute id=""Phantom"" />
            </Form></GxMultiForm>";
            var hits = LayoutGotchaScanner.Scan(xml, _ => null);
            Assert.Contains(hits, h => h.Code == "GotchaGxAttributeMissingDataField" && h.ControlId == "Phantom");
        }

        [Fact]
        public void Scan_UnknownControlType_EmitsWarning()
        {
            // "RadioButton" without the space is a common typo and the SDK silently falls back to Edit.
            var xml = @"<GxMultiForm><Form id=""1"" type=""html"">
                <gxAttribute AttID=""var:8"" ControlType=""RadioButton"" />
            </Form></GxMultiForm>";
            var hits = LayoutGotchaScanner.Scan(xml, _ => null);
            Assert.Contains(hits, h => h.Code == "GotchaUnknownControlType");
        }

        [Fact]
        public void Scan_RecognizedControlType_NoWarning()
        {
            // "Combo Box" with the space is the canonical form — no UnknownControlType gotcha.
            // (May still trigger the html-form discrete-readonly gotcha — that's a different code.)
            var xml = @"<GxMultiForm><Form id=""1"" type=""layout"">
                <gxAttribute AttID=""var:8"" ControlType=""Combo Box"" />
            </Form></GxMultiForm>";
            var hits = LayoutGotchaScanner.Scan(xml, _ => null);
            Assert.DoesNotContain(hits, h => h.Code == "GotchaUnknownControlType");
        }

        [Fact]
        public void Scan_WebComponentMissingObjectCall_EmitsWarning()
        {
            var xml = @"<GxMultiForm><Form id=""1"" type=""html"">
                <gxWebComponent id=""Header"" />
                <gxEmbeddedPage id=""Footer"" ObjectCall=""Footer.Create()"" />
            </Form></GxMultiForm>";
            var hits = LayoutGotchaScanner.Scan(xml, _ => null);
            Assert.Single(hits, h => h.Code == "GotchaWebComponentMissingObjectCall");
            Assert.Equal("Header", hits.First(h => h.Code == "GotchaWebComponentMissingObjectCall").ControlId);
        }

        [Fact]
        public void Scan_CellOutsideTable_EmitsWarning()
        {
            // <cell> directly under <body> without <table>...<row>...<cell> hierarchy
            var xml = @"<GxMultiForm><Form id=""1"" type=""html"">
                <body><cell id=""Orphan"">x</cell></body>
            </Form></GxMultiForm>";
            var hits = LayoutGotchaScanner.Scan(xml, _ => null);
            Assert.Contains(hits, h => h.Code == "GotchaCellOutsideTable");
        }

        [Fact]
        public void Scan_DuplicateControlName_EmitsWarning()
        {
            var xml = @"<GxMultiForm><Form id=""1"" type=""html"">
                <gxAttribute id=""Btn"" AttID=""var:8"" />
                <gxButton id=""Btn"" eventGX=""'Enter'"" />
            </Form></GxMultiForm>";
            var hits = LayoutGotchaScanner.Scan(xml, _ => null);
            Assert.Contains(hits, h => h.Code == "GotchaDuplicateControlName" && h.ControlId == "Btn");
        }

        // Issue #830: a grid's Conditions are its ControlWhere property; a <condition> child or attribute is not part of
        // the layout format and the SDK drops it, so the write does not keep those Conditions.
        [Theory]
        [InlineData("grid", "<condition>NomeCliente like '%' + &amp;Filtro.Trim() + '%'</condition>", "<condition> child")]
        [InlineData("simplegrid", "<Conditions>NomeCliente like 'A%'</Conditions>", "<Conditions> child")]
        [InlineData("grid", "<where>NomeCliente like 'A%'</where>", "<where> child")]
        public void Scan_GridConditionChildInLayoutForm_EmitsWarningThatNamesControlWhere(string grid, string child, string shape)
        {
            var xml = @"<GxMultiForm><Form id=""1"" type=""layout""><detail><layout id=""L""><table id=""T"" controlName=""MainTable"">
                <row id=""R""><cell id=""C""><" + grid + @" id=""9c1f2a3b-4d5e-4f60-8a7b-0c1d2e3f4a5b"" controlName=""GridProva"">" + child + @"
                    <table id=""G"" controlName=""GridTable"" /></" + grid + @"></cell></row>
            </table></layout></detail></Form></GxMultiForm>";

            var hits = LayoutGotchaScanner.Scan(xml, _ => null);

            var hit = Assert.Single(hits, h => h.Code == GotchaCodes.GotchaGridConditionIgnored);
            Assert.Equal("Warning", hit.Severity);
            Assert.Equal(grid, hit.Element);
            Assert.Equal("GridProva", hit.ControlId);
            Assert.Contains(shape, hit.Message);
            Assert.Equal("genexus://kb/tool-help/gotchas/GotchaGridConditionIgnored", hit.DocUrl);
            // The same advice for both grids: both list ControlWhere, and nothing proves the Conditions part belongs to a grid.
            Assert.Contains("ControlWhere", hit.Workaround);
            Assert.Contains("expectedVersion", hit.Workaround);
            Assert.DoesNotContain("part=Conditions", hit.Workaround);
            Assert.DoesNotContain("classic", hit.Workaround);
            // The message says what the SDK does, not what the receipt of a failed write reads like.
            Assert.Contains("the SDK drops it", hit.Message);
            Assert.DoesNotContain("reports success", hit.Message);
        }

        [Theory]
        [InlineData("condition")]
        [InlineData("Conditions")]
        public void Scan_GridConditionAttributeInLayoutForm_EmitsWarning(string attribute)
        {
            var xml = @"<GxMultiForm><Form id=""1"" type=""layout""><detail><layout id=""L"">
                <grid id=""G1"" " + attribute + @"=""NomeCliente like 'A%'"" />
            </layout></detail></Form></GxMultiForm>";

            var hits = LayoutGotchaScanner.Scan(xml, _ => null);

            var hit = Assert.Single(hits, h => h.Code == GotchaCodes.GotchaGridConditionIgnored);
            Assert.Contains("'" + attribute + "' attribute", hit.Message);
            Assert.Equal("G1", hit.ControlId); // no controlName: the id is the only handle
        }

        [Fact]
        public void Scan_GridWithoutConditionCarrier_HasNoGridConditionWarning()
        {
            var xml = @"<GxMultiForm><Form id=""1"" type=""layout""><detail><layout id=""L"">
                <grid id=""G1"" controlName=""GridProva"" PATTERN_ELEMENT_CUSTOM_PROPERTIES=""x""><table id=""T"" controlName=""GridTable"" /></grid>
                <simplegrid id=""G2"" controlName=""Grid""><item id=""I1"" attribute=""att:1"" /></simplegrid>
            </layout></detail></Form></GxMultiForm>";

            Assert.DoesNotContain(LayoutGotchaScanner.Scan(xml, _ => null), h => h.Code == GotchaCodes.GotchaGridConditionIgnored);
        }

        [Fact]
        public void Scan_ConditionOnSomethingThatIsNotAGrid_HasNoGridConditionWarning()
        {
            var xml = @"<GxMultiForm><Form id=""1"" type=""layout""><detail><layout id=""L"">
                <table id=""T"" controlName=""MainTable""><condition>x</condition></table>
                <data id=""D"" attribute=""att:1"" where=""x"" />
            </layout></detail></Form></GxMultiForm>";

            Assert.DoesNotContain(LayoutGotchaScanner.Scan(xml, _ => null), h => h.Code == GotchaCodes.GotchaGridConditionIgnored);
        }

        [Fact]
        public void Scan_GridConditionInAnHtmlForm_IsNotJudgedByTheLayoutRule()
        {
            var xml = @"<GxMultiForm><Form id=""1"" type=""html"">
                <grid id=""G1""><condition>x</condition></grid>
            </Form></GxMultiForm>";

            Assert.DoesNotContain(LayoutGotchaScanner.Scan(xml, _ => null), h => h.Code == GotchaCodes.GotchaGridConditionIgnored);
        }

        [Fact]
        public void Scan_GridConditionInTheLayoutFormOfADocumentThatAlsoHasAnHtmlForm_StillWarns()
        {
            var xml = @"<GxMultiForm>
                <Form id=""1"" type=""html""><grid id=""H1""><condition>x</condition></grid></Form>
                <Form id=""2"" type=""layout""><detail><layout id=""L""><grid id=""G1"" controlName=""GridLayout""><condition>y</condition></grid></layout></detail></Form>
            </GxMultiForm>";

            var hit = Assert.Single(LayoutGotchaScanner.Scan(xml, _ => null), h => h.Code == GotchaCodes.GotchaGridConditionIgnored);
            Assert.Equal("GridLayout", hit.ControlId);
        }
    }
}
