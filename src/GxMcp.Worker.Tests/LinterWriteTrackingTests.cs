using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Xml;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class LinterWriteTrackingTests
    {
        [Fact]
        public void BatchVariableRemovalResponse_MarksDirtyAndUpdatesTimestamp()
        {
            string target = "LinterBatch_" + Guid.NewGuid().ToString("N");
            var since = DateTime.UtcNow.AddSeconds(-1);

            WriteService.MarkDirtyIfSuccess(
                "{\"status\":\"ok\",\"code\":\"AttributeRemoved\"}",
                target);

            Assert.True(WriteService.WasTargetWrittenSince(target, since));
            Assert.Contains(target.ToLowerInvariant(), EditDirtyTracker.GetDirty(null));
        }

        [Fact]
        public void BatchVariableNoChangeResponse_DoesNotMarkDirty()
        {
            string target = "LinterNoop_" + Guid.NewGuid().ToString("N");

            WriteService.MarkDirtyIfSuccess(
                "{\"status\":\"ok\",\"code\":\"WriteNoChange\"}",
                target);

            Assert.False(WriteService.WasTargetWrittenSince(target, DateTime.UtcNow.AddSeconds(-1)));
            Assert.DoesNotContain(target, EditDirtyTracker.GetDirty(null));
        }

        [Fact]
        public void LinterFix_ResolvesLegacyUnusedVariableSnippetWhenSymbolIsAbsent()
        {
            var issue = JObject.Parse("{\"code\":\"GX008\",\"snippet\":\"&UnusedVar\"}");

            Assert.Equal("&UnusedVar", LinterService.ResolveFixSymbol(issue));
        }

        [Fact]
        public void WebFormOnlyReference_IsNotAnUnusedVariable()
        {
            var visualPart = new VariableReferenceDouble("OnlyInWebForm");

            var unused = LinterService.FindUnusedVariableNames(
                new[] { "OnlyInWebForm", "Unused" },
                new[] { "Event Start\n" },
                new[] { visualPart.ReadNames() });

            Assert.Equal(new[] { "Unused" }, unused);
            Assert.True(visualPart.ReadCalled);
        }

        [Fact]
        public void WebFormReferenceRead_DoesNotChangePartLastModification()
        {
            Type webFormType = Type.GetType(
                "Artech.Genexus.Common.Parts.WebFormPart, Artech.Genexus.Common")
                ?? throw new InvalidOperationException("GeneXus WebFormPart type is unavailable.");
            var part = FormatterServices.GetUninitializedObject(webFormType);
            var document = new XmlDocument();
            document.LoadXml("<BODY><gxTextBlock /></BODY>");
            webFormType.GetField("m_Document", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(part, document);
            PropertyInfo lastModification = webFormType.GetProperty(
                "LastModification", BindingFlags.Instance | BindingFlags.NonPublic)!;
            FieldInfo fixPending = webFormType.GetField(
                "m_FixPending", BindingFlags.Instance | BindingFlags.NonPublic)!;
            MethodInfo getReferencedVariables = webFormType.GetMethod("GetReferencedVariables")!;
            int before = (int)lastModification.GetValue(part, null)!;
            bool pendingBefore = (bool)fixPending.GetValue(part)!;

            _ = LinterService.ReadVisualVariableNames(() =>
                ((IEnumerable)getReferencedVariables.Invoke(part, null)!)
                    .Cast<object>()
                    .Select(reference => reference.GetType().GetProperty("Name")?.GetValue(reference, null)?.ToString()))
                .ToList();

            Assert.Equal("<BODY><gxTextBlock /></BODY>", document.OuterXml);
            Assert.Equal(before, (int)lastModification.GetValue(part, null)!);
            Assert.Equal(pendingBefore, (bool)fixPending.GetValue(part)!);
        }

        [Fact]
        public void IncompleteWebFormReferenceRead_SuppressesUnsafeUnusedFinding()
        {
            var unused = LinterService.FindUnusedVariableNames(
                new[] { "MaybeInWebForm" },
                new[] { "Event Start\n" },
                new[] { LinterService.ReadVisualVariableNames(() => throw new InvalidOperationException("unsupported")) });

            Assert.Empty(unused);
        }

        [Fact]
        public void LinterFixDryRun_ReturnsExplicitErrorWithoutReadingOrWriting()
        {
            var service = new LinterService(null, null);
            string target = "LinterDryRun_" + Guid.NewGuid().ToString("N");

            var response = JObject.Parse(service.LintAndFix(target, dryRun: true));

            Assert.Equal("error", response["status"]?.ToString());
            Assert.Equal("LinterFixDryRunUnsupported", response["error"]?["code"]?.ToString());
            Assert.Contains("fix=true", response["error"]?["message"]?.ToString() ?? "");
            Assert.Contains("dryRun=true", response["error"]?["message"]?.ToString() ?? "");
            Assert.DoesNotContain(target.ToLowerInvariant(), EditDirtyTracker.GetDirty(null));
        }

        private sealed class VariableReferenceDouble
        {
            private readonly string _name;

            public VariableReferenceDouble(string name)
            {
                _name = name;
            }

            public bool ReadCalled { get; private set; }

            public IEnumerable<string> ReadNames()
            {
                ReadCalled = true;
                yield return _name;
            }
        }
    }
}
