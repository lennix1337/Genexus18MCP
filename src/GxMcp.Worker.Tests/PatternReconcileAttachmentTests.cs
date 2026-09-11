using Xunit;

namespace GxMcp.Worker.Tests
{
    // Raw XML edits must never infer SDK-owned metadata.
    public class PatternReconcileAttachmentTests
    {
        [Fact]
        public void WriteService_UsesSamePurePlanBeforePreviewAndSave()
        {
            // Definition + call sites now live in split partial files (plan 007).
            System.IO.DirectoryInfo dir = new System.IO.DirectoryInfo(System.AppDomain.CurrentDomain.BaseDirectory);
            string servicesDir = null;
            while (dir != null)
            {
                string candidate = System.IO.Path.Combine(dir.FullName, "src", "GxMcp.Worker", "Services");
                if (System.IO.Directory.Exists(candidate)) { servicesDir = candidate; break; }
                dir = dir.Parent;
            }
            string writeSrc = System.IO.File.ReadAllText(System.IO.Path.Combine(servicesDir, "WriteService.VisualWrite.cs"))
                + System.IO.File.ReadAllText(System.IO.Path.Combine(servicesDir, "WriteService.PatternWrite.cs"));

            Assert.Contains("PatternXmlEditPlan.Create(currentXml, xml)", writeSrc);
            Assert.Contains("normalizedInput = plan.Xml;", writeSrc);
            Assert.DoesNotContain("PatternChildOrderReconciler.Reconcile", writeSrc);
            Assert.DoesNotContain("AttachReconcileReport", writeSrc);
            Assert.True(writeSrc.IndexOf("if (plan.IsNoChange)") < writeSrc.IndexOf("normalizedInput = plan.Xml;"));
            Assert.True(writeSrc.IndexOf("if (plan.ErrorCode != null)") < writeSrc.IndexOf("ApplyPatternEnvelope(resolvedPart, envelope, normalizedInput)"));
        }
    }
}
