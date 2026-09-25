using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // Issue #313: K2BTools WebPanel Designer objects have embedded designer
    // metadata rather than an SDK PatternInstance.
    public sealed class K2bWebPanelDesignerServiceTests
    {
        private static (string WebForm, string Events) ReadFixture()
        {
            string path = Path.Combine(
                TestFixtures.FindRepoRoot(),
                "src", "GxMcp.Worker.Tests", "Fixtures", "K2bWebPanelDesigner.fixture.xml");
            var document = XDocument.Parse(File.ReadAllText(path));
            return (
                document.Root?.Element("WebForm")?.Value ?? string.Empty,
                document.Root?.Element("Events")?.Value ?? string.Empty);
        }

        [Fact]
        public void RecognizesEmbeddedDesignerAndDecodesGridMetadata()
        {
            var fixture = ReadFixture();
            var model = K2bWebPanelDesignerService.Analyze(fixture.WebForm, fixture.Events);

            Assert.True(model.Recognized);
            Assert.Equal(1, model.CustomPropertyMarkerCount);
            Assert.Equal(2, model.EditorMarkerCount);
            var grid = Assert.Single(model.Grids);
            Assert.Equal("Grid", grid.Name);
            Assert.Equal("Customer", grid.BaseTable);
            Assert.Null(grid.BaseTableId); // the fixture supplies a table name, not an id
            Assert.Equal("multi", grid.SelectionMode);
            var key = Assert.Single(grid.KeyAttributes);
            Assert.Equal("att:5477", (string)key["attribute"]);
            Assert.Equal("5477", (string)key["id"]);
            Assert.Equal(3, grid.Columns.Count);
            Assert.Equal("att:5477", grid.Columns[1].Attribute);
            Assert.True(grid.Columns[1].IsKey);
            Assert.Equal("Active", (string)grid.ControlWhere.Tokens[0]["name"]);
            Assert.Equal("Id", (string)grid.ControlOrder.Tokens[0]["name"]);

            JObject json = model.ToJson();
            Assert.Equal("K2BToolsWebPanelDesigner", (string)json["designer"]);
            Assert.False((bool)json["patternInstancePresent"]);
            Assert.Equal(JTokenType.Null, json["patternInstance"]?.Type);
            Assert.False((bool)json["regenerationSupported"]);

            var metadataResponse = JObject.Parse(K2bWebPanelDesignerService.BuildMetadataResponse(null, model));
            Assert.Equal("ok", (string)metadataResponse["status"]);
            Assert.Equal("K2bWebPanelDesignerMetadataRead", (string)metadataResponse["code"]);
            Assert.Equal("K2BToolsWebPanelDesigner", (string)metadataResponse["result"]?["designer"]);
        }

        [Fact]
        public void ProtectedEditorBlockRangesAreExactAndDoNotCoverUserCompletionBlock()
        {
            var fixture = ReadFixture();
            var model = K2bWebPanelDesignerService.Analyze(fixture.WebForm, fixture.Events);
            var block = Assert.Single(model.EditorBlocks, x => x.IsProtected);
            var user = Assert.Single(model.EditorBlocks, x => !x.IsProtected);

            Assert.Contains("Do Not Change", block.Marker);
            Assert.Equal(fixture.Events.Substring(block.StartOffset, block.EndOffsetExclusive - block.StartOffset), block.Text);
            Assert.Equal(2, block.StartLine);
            Assert.Equal(1, block.StartColumn);
            Assert.Equal(5, user.StartLine);
            Assert.True(block.EndOffsetExclusive <= user.StartOffset);
            Assert.Contains("UpdateSelection", block.Text);
            Assert.DoesNotContain("U_UserCompleted", block.Text);

            var rejection = JObject.Parse(K2bWebPanelDesignerService.BuildEditRejectionResponse(
                model, "SelectionPopup", "Events", "protectedEditorBlockChanged"));
            Assert.Equal("K2BProtectedEditorBlock", (string)rejection["error"]?["code"]);
            Assert.Single((JArray)rejection["error"]?["protectedRanges"]);
            Assert.Equal("utf16-character", (string)rejection["error"]?["protectedRanges"]?[0]?["offsetUnit"]);
            Assert.False((bool)rejection["error"]?["regenerationClaimed"]);
            Assert.NotNull(rejection["error"]?["recoveryPath"]);

            var patternRejection = JObject.Parse(K2bWebPanelDesignerService.BuildEditRejectionResponse(
                model, "SelectionPopup", "PatternApply", "patternInstanceUnsupported"));
            Assert.Equal("K2BWebPanelDesignerUnsupported", (string)patternRejection["error"]?["code"]);
            Assert.Equal(JTokenType.Null, patternRejection["error"]?["patternInstance"]?.Type);

            var range = (JObject)K2bWebPanelDesignerService.ProtectedRanges(model)[0];
            Assert.Equal(block.StartOffset, (int)range["startOffset"]);
            Assert.Equal(block.EndOffsetExclusive, (int)range["endOffsetExclusive"]);
            Assert.Equal("utf16-character", (string)range["offsetUnit"]);
        }

        [Fact]
        public void UserCompletionEditIsSafeButGeneratedBlockEditIsRejected()
        {
            var fixture = ReadFixture();
            var model = K2bWebPanelDesignerService.Analyze(fixture.WebForm, fixture.Events);

            string userEdit = fixture.Events.Replace("U_UserCompleted", "U_UserCompletedAfterDesignerSave");
            Assert.False(K2bWebPanelDesignerService.ShouldRejectEdit(model, "Events", userEdit, out string safeReason));
            Assert.Null(safeReason);

            string generatedEdit = fixture.Events.Replace("UpdateSelection(Grid)", "UpdateSelection(Grid, true)");
            Assert.True(K2bWebPanelDesignerService.ShouldRejectEdit(model, "Events", generatedEdit, out string blockedReason));
            Assert.Equal("protectedEditorBlockChanged", blockedReason);
        }

        [Fact]
        public void GridMetadataEditIsRejectedWithoutClaimingRegeneration()
        {
            var fixture = ReadFixture();
            var model = K2bWebPanelDesignerService.Analyze(fixture.WebForm, fixture.Events);
            string changed = fixture.WebForm.Replace("Customer", "Supplier");

            Assert.True(K2bWebPanelDesignerService.ShouldRejectEdit(model, "WebForm", changed, out string reason));
            Assert.Equal("designerGridMetadataChanged", reason);
        }

        [Fact]
        public void DisplayOnlyColumnTitleEditIsAllowedBecauseGeneratedMethodsAreIndependent()
        {
            var fixture = ReadFixture();
            var model = K2bWebPanelDesignerService.Analyze(fixture.WebForm, fixture.Events);
            // Only the display title of a non-key column changes: the generated
            // "Do Not Change" methods consume the key, not the caption.
            string changed = fixture.WebForm.Replace("=Customer.Name", "=Customer.FullName");

            Assert.False(K2bWebPanelDesignerService.ShouldRejectEdit(model, "WebForm", changed, out string reason));
            Assert.Null(reason);

            var impact = K2bWebPanelDesignerService.ClassifyGridEdit(model, changed);
            Assert.True(impact.Compared);
            Assert.False(impact.RequiresRegeneration);
            Assert.Contains(impact.DisplayOnlyChanges, x => x.Contains("titleExpression"));
            Assert.Empty(impact.RegenerationReasons);
        }

        [Fact]
        public void KeyAttributeChangeIsRefusedAsRegenerationDependent()
        {
            var fixture = ReadFixture();
            var model = K2bWebPanelDesignerService.Analyze(fixture.WebForm, fixture.Events);
            // The selection checkbox is bound to var:82; retargeting the key
            // changes what the generated UpdateSelection(Grid) block must assign.
            string changed = fixture.WebForm.Replace("att:5477", "att:9999");

            Assert.True(K2bWebPanelDesignerService.ShouldRejectEdit(model, "WebForm", changed, out string reason));
            Assert.Equal("designerGridMetadataChanged", reason);

            var impact = K2bWebPanelDesignerService.ClassifyGridEdit(model, changed);
            Assert.True(impact.RequiresRegeneration);
            Assert.Contains("keyAttributesChanged", impact.RegenerationReasons);
        }

        [Fact]
        public void SelectionModeChangeIsRefusedAsRegenerationDependent()
        {
            var fixture = ReadFixture();
            var model = K2bWebPanelDesignerService.Analyze(fixture.WebForm, fixture.Events);
            string changed = fixture.WebForm.Replace("Multi", "Single");

            var impact = K2bWebPanelDesignerService.ClassifyGridEdit(model, changed);
            Assert.True(impact.RequiresRegeneration);
            Assert.Contains("selectionModeChanged", impact.RegenerationReasons);
        }

        [Fact]
        public void UnparsableGridEditFailsClosedAsRegenerationRequired()
        {
            var fixture = ReadFixture();
            var model = K2bWebPanelDesignerService.Analyze(fixture.WebForm, fixture.Events);

            var impact = K2bWebPanelDesignerService.ClassifyGridEdit(model, "this is not xml");
            Assert.False(impact.Compared);
            Assert.True(impact.RequiresRegeneration);
            Assert.Contains("gridComparisonUnavailable", impact.RegenerationReasons);
        }

        [Fact]
        public void GeneratedMethodDependencyIsDerivedFromRealEventsSource()
        {
            var fixture = ReadFixture();
            var model = K2bWebPanelDesignerService.Analyze(fixture.WebForm, fixture.Events);
            var dependency = K2bWebPanelDesignerService.BuildGeneratedMethodDependency(model);

            Assert.True((bool)dependency["hasGeneratedBlocks"]);
            // The protected block really does call UpdateSelection(Grid).
            Assert.True((bool)dependency["referencesGridControls"]);
            Assert.Contains("Grid", dependency["gridControlNames"].Select(x => (string)x));
            Assert.Contains("5477", dependency["keyAttributeIds"].Select(x => (string)x));
        }

        [Fact]
        public void RejectionResponseExplainsRegenerationImpactAndSafeSubset()
        {
            var fixture = ReadFixture();
            var model = K2bWebPanelDesignerService.Analyze(fixture.WebForm, fixture.Events);
            string changed = fixture.WebForm.Replace("Customer", "Supplier");
            var impact = K2bWebPanelDesignerService.ClassifyGridEdit(model, changed);

            var rejection = JObject.Parse(K2bWebPanelDesignerService.BuildEditRejectionResponse(
                model, "SelectionPopup", "WebForm", "designerGridMetadataChanged", impact));

            Assert.False((bool)rejection["error"]?["persisted"]);
            Assert.True((bool)rejection["error"]?["regenerationImpact"]?["requiresRegeneration"]);
            Assert.Contains("baseTableChanged", rejection["error"]["regenerationImpact"]["regenerationReasons"].Select(x => (string)x));
            Assert.NotEmpty(rejection["error"]["displayOnlyAspects"]);
            Assert.NotNull(rejection["error"]["generatedMethodDependency"]);
        }

        [Fact]
        public void MetadataResponseAdvertisesDisplayOnlySubsetAndFailsClosedContract()
        {
            var fixture = ReadFixture();
            var model = K2bWebPanelDesignerService.Analyze(fixture.WebForm, fixture.Events);
            var parsed = JObject.Parse(K2bWebPanelDesignerService.BuildMetadataResponse(null, model));
            var result = (JObject)(parsed["result"] ?? parsed);

            Assert.True((bool)result["displayOnlyGridEditsSupported"]);
            Assert.Contains("columnTitleExpression", result["displayOnlyAspects"].Select(x => (string)x));
            Assert.Contains("baseTable", result["regenerationRequiredAspects"].Select(x => (string)x));
            Assert.True((bool)result["regenerationImpactContract"]?["failsClosed"]);
            Assert.False((bool)result["regenerationSupported"]);
        }

        [Fact]
        public void AddingADisplayColumnIsAllowedBecauseTheKeySelectionContractIsUnchanged()
        {
            var fixture = ReadFixture();
            var model = K2bWebPanelDesignerService.Analyze(fixture.WebForm, fixture.Events);
            string changed = fixture.WebForm.Replace(
                "<item attribute=\"att:5478\" titleExp=\"=Customer.Name\" />",
                "<item attribute=\"att:5478\" titleExp=\"=Customer.Name\" />\n      <item attribute=\"att:5479\" titleExp=\"=Customer.Email\" />");
            Assert.NotEqual(fixture.WebForm, changed);

            var impact = K2bWebPanelDesignerService.ClassifyGridEdit(model, changed);
            Assert.True(impact.Compared);
            Assert.False(impact.RequiresRegeneration);
            Assert.Contains(impact.DisplayOnlyChanges, x => x.Contains("columns"));

            Assert.False(K2bWebPanelDesignerService.ShouldRejectEdit(model, "WebForm", changed, out string reason));
            Assert.Null(reason);
        }

        [Fact]
        public void RemovingADisplayColumnIsAllowedButRemovingTheKeyColumnIsNot()
        {
            var fixture = ReadFixture();
            var model = K2bWebPanelDesignerService.Analyze(fixture.WebForm, fixture.Events);

            string removedDisplay = fixture.WebForm.Replace(
                "\n      <item attribute=\"att:5478\" titleExp=\"=Customer.Name\" />", string.Empty);
            var displayImpact = K2bWebPanelDesignerService.ClassifyGridEdit(model, removedDisplay);
            Assert.False(displayImpact.RequiresRegeneration);

            string removedKey = fixture.WebForm.Replace(
                "\n      <item attribute=\"att:5477\" titleExp=\"=Customer.CustomerId\" key=\"true\" />", string.Empty);
            var keyImpact = K2bWebPanelDesignerService.ClassifyGridEdit(model, removedKey);
            Assert.True(keyImpact.RequiresRegeneration);
            Assert.Contains(keyImpact.RegenerationReasons, r => r == "keyAttributesChanged" || r == "columnSetChanged");
        }

        [Fact]
        public void AddingASecondKeyColumnIsRefusedAsRegenerationDependent()
        {
            var fixture = ReadFixture();
            var model = K2bWebPanelDesignerService.Analyze(fixture.WebForm, fixture.Events);
            string changed = fixture.WebForm.Replace(
                "</simplegrid>",
                "<item attribute=\"att:5480\" key=\"true\" />\n    </simplegrid>");

            var impact = K2bWebPanelDesignerService.ClassifyGridEdit(model, changed);
            Assert.True(impact.RequiresRegeneration);
            Assert.Contains("keyAttributesChanged", impact.RegenerationReasons);
        }

        [Fact]
        public void ReorderingTheKeyRelativeToTheSelectionColumnIsRefused()
        {
            var fixture = ReadFixture();
            var model = K2bWebPanelDesignerService.Analyze(fixture.WebForm, fixture.Events);
            // Swap the order of the selection and key columns: the generated
            // UpdateSelection(Grid) block is materialized against that contract.
            string selection = "<item attribute=\"var:82\" selection=\"true\" />";
            string key = "<item attribute=\"att:5477\" titleExp=\"=Customer.CustomerId\" key=\"true\" />";
            string changed = fixture.WebForm
                .Replace(selection + "\n      " + key, key + "\n      " + selection);
            Assert.NotEqual(fixture.WebForm, changed);

            var impact = K2bWebPanelDesignerService.ClassifyGridEdit(model, changed);
            Assert.True(impact.RequiresRegeneration);
        }

        [Fact]
        public void OrdinaryWebFormWithoutK2bMarkersIsNotRecognized()
        {
            var model = K2bWebPanelDesignerService.Analyze(
                "<GxMultiForm><form><simplegrid id=\"ordinary\" /></form></GxMultiForm>",
                "Event Ordinary\n  // user code\nend");

            Assert.False(model.Recognized);
            Assert.Empty(model.Grids);
            Assert.Empty(model.EditorBlocks);
        }

        [Fact]
        public void GenericPatternCustomPropertiesDoNotAloneImpersonateK2bDesigner()
        {
            var model = K2bWebPanelDesignerService.Analyze(
                "<GxMultiForm><form><simplegrid id=\"ordinary\" PATTERN_ELEMENT_CUSTOM_PROPERTIES=\"&lt;Properties&gt;&lt;Property&gt;&lt;Name&gt;Class&lt;/Name&gt;&lt;Value&gt;Grid&lt;/Value&gt;&lt;/Property&gt;&lt;/Properties&gt;\" /></form></GxMultiForm>",
                "Event Ordinary\n  // user code\nend");

            Assert.False(model.Recognized);
            Assert.Empty(model.Grids);
        }

        [Fact]
        public void PatternDiagnosisIsExplicitlyUnsupportedAndContainsRecoveryEvidence()
        {
            var fixture = ReadFixture();
            var model = K2bWebPanelDesignerService.Analyze(fixture.WebForm, fixture.Events);
            var response = JObject.Parse(K2bWebPanelDesignerService.BuildPatternDiagnosis(
                "SelectionPopup", "K2BTools", model, new[] { "WorkWithPlus", "K2BEntityServices" }));

            Assert.Equal("blocked", (string)response["status"]);
            Assert.Equal("unsupported", (string)response["outcome"]);
            Assert.False((bool)response["patternInstancePresent"]);
            Assert.Equal(JTokenType.Null, response["patternInstance"]?.Type);
            Assert.False((bool)response["editSupported"]);
            Assert.False((bool)response["regenerationSupported"]);
            Assert.NotNull(response["protectedRanges"]);
            Assert.NotNull(response["recoveryPath"]);
            Assert.DoesNotContain("templateInvalid", response.ToString());
            Assert.DoesNotContain("WWPInstanceNotFound", response.ToString());
            Assert.Contains("GeneXus IDE", (string)response["recoveryPath"]["tool"]);
        }
    }
}
