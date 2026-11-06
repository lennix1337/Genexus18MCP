using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class TransferImportTests
    {
        private sealed class FakeExportItem
        {
            public string ObjectName { get; set; }
            public string DisplayName { get; set; }
            public string Guid { get; set; }
            public string BaseOperation { get; set; }
            public string Status { get; set; }
        }

        [Fact]
        public void ImportFidelityFailure_AfterPreparation_RequiresReconciliationOfTargets()
        {
            var targets = JArray.Parse("[{\"name\":\"P360CPACBUGuardar\",\"type\":\"Procedure\"},{\"name\":\"PreCBUItmUlt\",\"type\":\"Procedure\"}]");
            var result = JObject.Parse(TransferService.ImportFidelityFailure(new InvalidDataException("unreadable"), new JObject(), true, targets));
            string text = result.ToString();
            Assert.Contains("TransferImportVerificationUnavailable", text);
            Assert.Contains("\"persistedState\": \"unknown\"", text);
            Assert.Contains("\"reconciliationRequired\": true", text);
            Assert.Equal(2, result.SelectTokens("$..reconciliationCalls[*]").Count());
            Assert.Equal("P360CPACBUGuardar", result.SelectToken("$..reconciliationCalls[0].args.name").ToString());
            Assert.Equal("PreCBUItmUlt", result.SelectToken("$..reconciliationCalls[1].args.name").ToString());
            Assert.All(result.SelectTokens("$..reconciliationCalls[*].tool"), call => Assert.Equal("genexus_inspect", call.ToString()));
            Assert.DoesNotContain("no import was attempted", text);
        }

        [Fact]
        public void ImportFidelityFailure_BeforePreparation_DoesNotClaimUncertainPreparation()
        {
            var result = JObject.Parse(TransferService.ImportFidelityFailure(new InvalidDataException("unreadable"), new JObject(), false, new JArray()));
            Assert.Contains("no import was attempted", result.ToString());
            Assert.Null(result.SelectToken("$..persistedState"));
        }

        [Fact]
        public void CaptureImportFidelity_MarksPreparationBeforeSdkCallAndReportsFailure()
        {
            string source = GxMcp.TestSupport.RepoSource.WithoutComments("src", "GxMcp.Worker", "Services", "TransferService.cs");
            Assert.Contains("preparationStarted();\n            var prepared = svc.PrepareImport", source.Replace("\r\n", "\n"));
            Assert.Contains("ImportFidelityFailure(ex, ImportDiagnostics(diagnostics, imported: false), preparationStarted, targets)", source);
            Assert.Contains("targets.Add(new JObject", source);
        }

        [Fact]
        public void DescribeExportItem_ProjectsStableSdkMetadataWithoutCallingObject()
        {
            var descriptor = TransferService.DescribeExportItem(new FakeExportItem
            {
                ObjectName = "SamplePanel",
                DisplayName = "Sample panel",
                Guid = "9b7e0f20-39a2-45a4-9b21-000000000001",
                BaseOperation = "Insert",
                Status = "Ready"
            });

            Assert.Equal("SamplePanel", descriptor["name"]?.ToString());
            Assert.Equal("Sample panel", descriptor["displayName"]?.ToString());
            Assert.Equal("Insert", descriptor["baseOperation"]?.ToString());
            Assert.True(descriptor["identityAvailable"]?.ToObject<bool>() ?? false);
        }

        [Fact]
        public void ImportPreview_WithConfirmButNoDryRunFalse_SaysNothingWasImportedAndHowToImport()
        {
            var result = new JObject();
            TransferService.AddImportPreviewGuidance(result, JObject.Parse("{\"action\":\"import\",\"file\":\"C:/x/Sample.xpz\",\"confirm\":true}"));

            Assert.True(result["previewOnly"].Value<bool>());
            Assert.False(result["imported"].Value<bool>());
            Assert.Contains("dryRun=false", result["hint"].ToString());
            Assert.Contains("confirm=true alone does NOT import", result["warning"].ToString());
            Assert.False(result["nextAction"]["args"]["dryRun"].Value<bool>());
            Assert.True(result["nextAction"]["args"]["confirm"].Value<bool>());
            Assert.Equal("C:/x/Sample.xpz", result["nextAction"]["args"]["file"].ToString());
        }

        [Fact]
        public void ImportPreview_PlainPreview_HasHintButNoConfirmWarning()
        {
            var result = new JObject();
            TransferService.AddImportPreviewGuidance(result, JObject.Parse("{\"action\":\"import\",\"file\":\"C:/x/Sample.xpz\",\"dryRun\":true}"));

            Assert.True(result["previewOnly"].Value<bool>());
            Assert.Contains("dryRun=false", result["hint"].ToString());
            Assert.Null(result["warning"]);
        }

        [Fact]
        public void InspectPreviewPath_AttachesTheGuidance()
        {
            // Inspect needs a live SDK service, so pin the wiring at source level.
            string source = GxMcp.TestSupport.RepoSource.WithoutComments(
                "src", "GxMcp.Worker", "Services", "TransferService.cs");

            Assert.Contains("if (isDryRunImport) AddImportPreviewGuidance(result, args);", source, StringComparison.Ordinal);
        }

        [Fact]
        public void SilentImportOptions_DefaultsToLosslessOverwrite()
        {
            Assert.Equal("Overwrite", TransferService.ResolveImportThemeBehavior(new JObject()));
            Assert.Equal("UseFromExport", TransferService.ResolveImportClassConflicts(new JObject()));

            // ImportOptions properties are backed by the GeneXus property
            // context; reading them in this isolated net48 test attempts to
            // load the IDE configuration and throws. The effective values are
            // verified by the real-KB import test, while these assertions keep
            // the silent-options factory covered without requiring a KB.
            Assert.NotNull(TransferService.SilentImportOptions(new JObject()));
        }

        [Fact]
        public void SilentImportOptions_AllowsExplicitIncrementalThemeIntegration()
        {
            Assert.Equal(
                "IncrementalIntegration",
                TransferService.ResolveImportThemeBehavior(
                    JObject.Parse("{\"themeImportBehavior\":\"IncrementalIntegration\"}")));
        }

        [Fact]
        public void SilentImportOptions_RejectsUnknownExplicitValues()
        {
            Assert.Throws<ArgumentException>(() =>
                TransferService.SilentImportOptions(
                    JObject.Parse("{\"classConflicts\":\"IgnoreEverything\"}")));
        }

        [Fact]
        public void ReadExportWebForms_UsesRawSourceFromXpz()
        {
            string path = Path.Combine(Path.GetTempPath(), "gxmcp-transfer-" + Guid.NewGuid().ToString("N") + ".xpz");
            try
            {
                using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
                using (var writer = new StreamWriter(archive.CreateEntry("export.xml").Open()))
                {
                    writer.Write("<ExportFile><Objects>"
                        + "<Object name=\"SampleWebPanel\"><Part><Source><![CDATA["
                        + "<GxMultiForm><gxAttribute GxWidth=\"30chr\" GxHeight=\"1row\" />"
                        + "</GxMultiForm>]]></Source></Part></Object>"
                        + "</Objects></ExportFile>");
                }

                var forms = TransferService.ReadExportWebForms(path);
                Assert.True(forms.ContainsKey("SampleWebPanel"));
                Assert.Contains("GxWidth=\"30chr\"", forms["SampleWebPanel"]);
                Assert.Contains("GxHeight=\"1row\"", forms["SampleWebPanel"]);
            }
            finally
            {
                try { File.Delete(path); } catch { }
            }
        }

        [Theory]
        [InlineData("Artech.Genexus.Common.Parts.ReportPart")]
        [InlineData("Artech.Genexus.Common.Parts.LayoutPart")]
        // The exclusion matches on the runtime type name, so it has to hold for the
        // naming variants GeneXus emits for a report part, not only the two canonical
        // spellings. `ReportLayoutPart` is the shape a Procedure's print layout
        // carries when it sits under a report namespace.
        [InlineData("Artech.Genexus.Common.Parts.Report.ReportPart")]
        [InlineData("Artech.Genexus.Common.Parts.Report.ReportLayoutPart")]
        public void IsWebFormFidelityCandidate_SkipsProcedurePrintLayout(string partClass)
        {
            Assert.False(TransferService.IsWebFormFidelityCandidate(partClass));
        }

        [Theory]
        [InlineData("Artech.Genexus.Common.Parts.WebFormPart")]
        [InlineData("Artech.Genexus.Common.Parts.WebForm.WebFormPart")]
        // A WebPanel is not a WebForm, but its visual part is still a WebForm payload
        // the fidelity check must keep guarding. If the exclusion ever widened to this
        // shape, issue #102's guard would go silently dead for WebPanels.
        [InlineData("Artech.Genexus.Common.Parts.WebPanel.WebPanelPart")]
        public void IsWebFormFidelityCandidate_KeepsRealWebFormParts(string partClass)
        {
            Assert.True(TransferService.IsWebFormFidelityCandidate(partClass));
        }

        [Fact]
        public void IsWebFormFidelityCandidate_UnknownPartTypeStaysCandidate()
        {
            Assert.True(TransferService.IsWebFormFidelityCandidate(null));
        }

        [Fact]
        public void ReadExportWebForms_CandidateWithoutSourcePayloadHasNoBaseline()
        {
            // The invariant the Procedure exclusion must not weaken: a WebForm candidate
            // with no raw payload has nothing to verify against, so the fidelity check
            // refuses the import instead of trusting the SDK projection. Pinned here on
            // the same package shape that motivated the exclusion - a Transaction whose
            // WebForm part carries a <Properties> child and no <Source> CDATA.
            string path = Path.Combine(Path.GetTempPath(), "gxmcp-transfer-nopayload-" + Guid.NewGuid().ToString("N") + ".xpz");
            try
            {
                using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
                using (var writer = new StreamWriter(archive.CreateEntry("export.xml").Open()))
                {
                    writer.Write("<ExportFile><Objects>"
                        + "<Object name=\"SampleTransaction\">"
                        + "<Part type=\"00000000-0000-0000-0000-000000000000\"><Properties /></Part>"
                        + "</Object></Objects></ExportFile>");
                }

                Assert.False(TransferService.ReadExportWebForms(path)
                    .TryGetValue("SampleTransaction", out string _));
            }
            finally
            {
                try { File.Delete(path); } catch { }
            }
        }

        [Fact]
        public void ReadExportWebForms_HandlesNamespacesAndXmlDeclaration()
        {
            string path = Path.Combine(Path.GetTempPath(), "gxmcp-transfer-ns-" + Guid.NewGuid().ToString("N") + ".xpz");
            try
            {
                using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
                using (var writer = new StreamWriter(archive.CreateEntry("namespaced.xml").Open()))
                {
                    writer.Write("<?xml version=\"1.0\"?><gx:ExportFile xmlns:gx=\"urn:test\"><gx:Object name=\"NamespacedPanel\"><gx:Part><gx:Source><![CDATA[<?xml version=\"1.0\"?><gx:GxMultiForm xmlns:gx=\"urn:test\"><gx:gxAttribute GxWidth=\"30chr\" /></gx:GxMultiForm>]]></gx:Source></gx:Part></gx:Object></gx:ExportFile>");
                }

                var forms = TransferService.ReadExportWebForms(path);
                Assert.True(forms.ContainsKey("NamespacedPanel"));
                Assert.Contains("GxWidth=\"30chr\"", forms["NamespacedPanel"]);
            }
            finally
            {
                try { File.Delete(path); } catch { }
            }
        }
    }
}
