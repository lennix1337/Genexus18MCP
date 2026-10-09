using System;
using GxMcp.TestSupport;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public sealed class NativeRecoveryFenceTests
    {
        [Fact]
        public void SdtAutomaticRecoveryRequiresAFreshOwnedVersionBeforeCopyingState()
        {
            string source = RepoSource.WithoutComments("src", "GxMcp.Worker", "Services", "SDTService.cs");
            string body = SourceAssert.MethodBody(source, "private bool RestoreFromRevision(");
            int read = body.IndexOf("FindObjectFreshByIdentity(seed)", StringComparison.Ordinal);
            int fence = body.IndexOf("string.Equals(ownedWriteVersion, WriteService.ComputeVersionToken(current)", StringComparison.Ordinal);
            int copy = body.IndexOf("CopyStructure(revision, current)", StringComparison.Ordinal);
            Assert.True(read >= 0 && read < fence && fence < copy);
            Assert.Contains("string.IsNullOrWhiteSpace(ownedWriteVersion)", body);
            Assert.DoesNotContain("?? seed", body);
        }

        [Fact]
        public void ExternalMethodRecoveryNeverSavesTheStalePreWriteOwner()
        {
            string source = RepoSource.WithoutComments("src", "GxMcp.Worker", "Services", "Structure", "AuthoringService.cs");
            string body = SourceAssert.MethodBody(source, "public string AddExternalMethod(");
            Assert.Contains("ownedWriteVersion = WriteService.ComputeVersionToken(obj)", body);
            Assert.Contains("RemoveOwnedExternalMethod(obj, added, ownedWriteVersion)", body);
            Assert.DoesNotContain("if (saveAttempted) obj.EnsureSave()", body);
            string recovery = SourceAssert.MethodBody(source, "private void RemoveOwnedExternalMethod(");
            Assert.Contains("FindObjectFreshByIdentity(owner)", recovery);
            Assert.Contains("string.Equals(ownedWriteVersion, WriteService.ComputeVersionToken(current)", recovery);
            Assert.Contains("current.EnsureSave()", recovery);
        }
    }
}
