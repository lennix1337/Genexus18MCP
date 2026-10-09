using GxMcp.TestSupport;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public sealed class MultipartDryRunTests
    {
        [Fact]
        public void DispatcherPassesTheRequestFlagToBatchService()
        {
            string source = RepoSource.WithoutComments("src", "GxMcp.Worker", "Services", "CommandDispatcher.cs");
            string body = SourceAssert.MethodBody(source, "private string Handle_Batch(");
            Assert.Contains("_batchService.BatchEdit(target, args?[\"changes\"] as JArray, args?[\"dryRun\"]?.ToObject<bool?>() ?? false)", body);
        }

        [Fact]
        public void RequestPreviewCannotEnterDirectSaveOrBeOverriddenByAnItem()
        {
            string source = RepoSource.WithoutComments("src", "GxMcp.Worker", "Services", "BatchService.cs");
            string body = SourceAssert.MethodBody(source, "public string BatchEdit(");
            Assert.Contains("bool dryRun = false", body);
            Assert.Contains("bool allDirect = !dryRun;", body);
            Assert.Equal(2, SourceAssert.Count(body, "dryRun || (change[\"dryRun\"]?.ToObject<bool?>() ?? false)"));
            Assert.Contains("null, itemDryRun, verifyRollback:", body);
            Assert.Contains("WriteObject(target, part, content, dryRun: itemDryRun)", body);
            Assert.Contains("BatchEditCompleted(target, count, results, sw, dryRun)", body);
        }
    }
}
