using System;
using GxMcp.TestSupport;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// The order of the batch property save and the default check of the rollback, read from the production source. A batch write
    /// needs a live object to run, so these tests pin the calls their semantics depend on inside the member that makes them. The
    /// verification rules themselves are run by PropertyVerificationSeamTests.
    /// </summary>
    public class PropertyWiringOrderContractTests
    {
        private const string BeginCall = "suppression = PropertyApplyOnSaveRule.Begin(obj, controlName, properties.Properties().Select(p => p.Name));";
        private const string SaveCall = "obj.EnsureSave();";
        private const string DisposeCall = "suppression?.Dispose();";

        private static string PropertyService()
            => RepoSource.WithoutComments(RepoSource.Read("src", "GxMcp.Worker", "Services", "PropertyService.cs"));

        // The text of one member: from its signature to the next member of the same class (members are indented by eight spaces).
        private static string MemberText(string src, string signature)
        {
            int start = src.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(start >= 0, "member not found: " + signature);
            int end = src.Length;
            foreach (string marker in new[] { "\n        private ", "\n        public ", "\n        internal ", "\n        protected " })
            {
                int at = src.IndexOf(marker, start + signature.Length, StringComparison.Ordinal);
                if (at >= 0 && at < end) end = at;
            }
            return src.Substring(start, end - start);
        }

        [Fact]
        public void TheBatchStartsTheSuppressionBeforeItsSave()
        {
            string batch = MemberText(PropertyService(), "public string SetProperties(");

            int begin = batch.IndexOf(BeginCall, StringComparison.Ordinal);
            int save = batch.IndexOf(SaveCall, StringComparison.Ordinal);

            Assert.True(begin >= 0, "the batch does not start the suppression");
            Assert.True(save > begin, "the batch saves the object before the suppression starts");
        }

        [Fact]
        public void TheBatchDisposesTheSuppressionInAFinallyAfterItsSave()
        {
            string batch = MemberText(PropertyService(), "public string SetProperties(");

            int save = batch.IndexOf(SaveCall, StringComparison.Ordinal);
            int finallyAt = batch.IndexOf("finally", Math.Max(save, 0), StringComparison.Ordinal);
            int dispose = batch.IndexOf(DisposeCall, StringComparison.Ordinal);

            Assert.True(save >= 0, "the batch does not save");
            Assert.True(finallyAt > save, "the batch has no finally after its save");
            Assert.True(dispose > finallyAt, "the suppression is not disposed in that finally");
        }

        [Fact]
        public void TheRollbackConfirmsEachPropertyByItsDefaultStateOnTheFreshObject()
        {
            string rollback = MemberText(PropertyService(), "private string RollbackPropertySnapshot(");

            Assert.Contains("name => restored != null && IsPropertyDefaultSafe(restored, rollback.NativeNames[name])", rollback);
        }
    }
}
