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
        private static readonly Guid ProcedureType = new Guid("84a12160-f59b-4ad7-a683-ea4481ac23e9");
        private static readonly Guid PanelType = new Guid("11111111-1111-1111-1111-111111111111");
        private const string WebFormPart = "d24a58ad-57ba-41b7-9e6e-eaca3543c778";

        private static string RawPackage(string xml)
        {
            string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".xpz");
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(archive.CreateEntry("export.xml").Open()))
                writer.Write(xml);
            return path;
        }

        private static Tuple<string, bool> ResolveRawType(Guid type)
        {
            if (type == ProcedureType) return Tuple.Create("Procedure", false);
            if (type == PanelType) return Tuple.Create("WebPanel", true);
            return null;
        }

        private static readonly string Form = $"<Part type='{WebFormPart}'><Source><![CDATA[<GxMultiForm />]]></Source></Part>";

        // Each shape comes from a real package: GAM_Web-Administration.xpz (a Folder and a WebPanel
        // share a name), Carmine.xpz (theme classes share a name, not a guid), GAM_Lib_Delete.xpz
        // (actions only) and Finance.xpz (a WebForm part with Properties only).
        public static TheoryData<string, string, int> AcceptedPackages => new TheoryData<string, string, int>
        {
            { "two procedures", Objects($"<Object name='A' type='{ProcedureType}'/><Object name='B' type='{ProcedureType}'/>"), 0 },
            { "unknown type without WebForm", Objects($"<Object name='A' type='{Guid.NewGuid()}'/>"), 0 },
            { "same name, different types", Objects($"<Object name='Dashboard' type='{ProcedureType}'/><Object name='Dashboard' type='{PanelType}'>{Form}</Object>"), 1 },
            { "same name, different guids", Objects($"<Object guid='{Guid.NewGuid()}' name='Text' type='{ProcedureType}'/><Object guid='{Guid.NewGuid()}' name='Text' type='{ProcedureType}'/>"), 0 },
            { "actions only", "<ExportFile><KMW /><Source /><Actions><Action type='delete' /></Actions></ExportFile>", 0 },
            { "WebForm without source", Objects($"<Object name='Panel' type='{PanelType}'/><Object name='Quote' type='{PanelType}'><Part type='{WebFormPart}'><Properties /></Part></Object>"), 0 },
        };

        public static TheoryData<string, string> RejectedPackages
        {
            get
            {
                var guid = Guid.NewGuid();
                return new TheoryData<string, string>
                {
                    { "WebForm source that is not a form", Objects($"<Object name='A' type='{ProcedureType}'/><Object name='Panel' type='{PanelType}'><Part type='{WebFormPart}'><Source>not a form</Source></Part></Object>") },
                    { "WebForm of an unknown type", Objects($"<Object name='A' type='{Guid.NewGuid()}'>{Form}</Object>") },
                    { "same name twice under one type", Objects($"<Object name='A' type='{ProcedureType}'/><Object name='A' type='{ProcedureType}'/>") },
                    { "same guid twice", Objects($"<Object guid='{guid}' name='A' type='{ProcedureType}'/><Object guid='{guid}' name='B' type='{ProcedureType}'/>") },
                    { "two WebForms for one name", Objects($"<Object guid='{Guid.NewGuid()}' name='Panel' type='{PanelType}'>{Form}</Object><Object guid='{Guid.NewGuid()}' name='Panel' type='{PanelType}'>{Form}</Object>") },
                };
            }
        }

        private static string Objects(string objects) => "<ExportFile><Objects>" + objects + "</Objects></ExportFile>";

        private static TransferService.ImportFidelityPlan Capture(string xml)
        {
            string path = RawPackage(xml);
            try { return TransferService.CaptureImportFidelity(path, ResolveRawType, (name, type) => null); }
            finally { File.Delete(path); }
        }

        [Theory]
        [MemberData(nameof(AcceptedPackages))]
        public void CaptureImportFidelity_AcceptsRealPackageShapes(string shape, string xml, int webForms)
        {
            Assert.True(Capture(xml).Items.Count == webForms, shape);
        }

        [Theory]
        [MemberData(nameof(RejectedPackages))]
        public void CaptureImportFidelity_RejectsAmbiguousPackages(string shape, string xml)
        {
            Assert.True(Assert.Throws<InvalidDataException>(() => Capture(xml)) != null, shape);
        }

        [Fact]
        public void CaptureImportFidelity_PreservesRawBaselineAndExistingSnapshot()
        {
            var path = RawPackage(Objects($"<Object name='Panel' type='{PanelType}'><Part type='{WebFormPart}'><Source><![CDATA[<GxMultiForm><BODY /></GxMultiForm>]]></Source></Part></Object>"));
            try
            {
                var plan = TransferService.CaptureImportFidelity(path, ResolveRawType, (name, type) => Tuple.Create("before"));
                var item = Assert.Single(plan.Items);
                Assert.Equal("<GxMultiForm><BODY /></GxMultiForm>", item.ExpectedXml);
                Assert.True(item.ExistingBefore);
                Assert.Equal("before", item.BeforeXml);
            }
            finally { File.Delete(path); }
        }

        private sealed class FakeExportItem
        {
            public string ObjectName { get; set; }
            public string DisplayName { get; set; }
            public string Guid { get; set; }
            public string BaseOperation { get; set; }
            public string Status { get; set; }
            public int DescriptorReads { get; private set; }
            public int ObjectReads { get; private set; }
            public object TypeDescriptor { get { DescriptorReads++; throw new InvalidOperationException(); } }
            public object Object { get { ObjectReads++; throw new InvalidOperationException(); } }
        }

        [Fact]
        public void DescribeExportItem_DoesNotEvaluateMutatingSdkGetters()
        {
            var item = new FakeExportItem { ObjectName = "Sample" };
            var result = TransferService.DescribeExportItem(item);
            Assert.Equal("Sample", result["name"]?.ToString());
            Assert.Equal(0, item.ObjectReads);
            Assert.Equal(0, item.DescriptorReads);
        }

        [Fact]
        public void ImportFidelityFailure_ReportsNoImport()
        {
            var diagnostics = new JObject { ["sdkDiagnostics"] = new JArray() };
            var result = JObject.Parse(TransferService.BuildImportFidelityFailure("unreadable", diagnostics));

            Assert.Equal("error", result["status"]?.ToString());
            Assert.Equal("TransferImportVerificationUnavailable", result["error"]?["code"]?.ToString());
            Assert.False(result["imported"].Value<bool>());
            Assert.Equal("unchanged", result["persistedState"]?.ToString());
            Assert.Null(result["error"]["reconciliationRequired"]);
            Assert.Contains("before retrying", result["error"]["hint"].ToString());
            Assert.Contains("ImportFile was not called", result["error"]["message"].ToString());
        }

        [Fact]
        public void ImportPreflight_NeverPreparesSdkObjectsBeforeImportFile()
        {
            // PrepareImport constructs target objects, so a preflight failure after it could leave
            // empty stubs in the KB while reporting that nothing was imported.
            string source = GxMcp.TestSupport.RepoSource.WithoutComments("src", "GxMcp.Worker", "Services", "TransferService.cs");
            Assert.DoesNotContain("PrepareImport", source);
            Assert.Contains("fidelityPlan = CaptureImportFidelity(file);", source);
            Assert.Contains("return BuildImportFidelityFailure(ex.Message, ImportDiagnostics(diagnostics, imported: false));", source);
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
