using System;
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
    }
}