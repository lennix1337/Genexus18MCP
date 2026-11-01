using System;
using GxMcp.TestSupport;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// The batch property save and the default check of the rollback, read from the production source. The batch saves through the one save
    /// seam, whose order and give-back PropertySaveSeamTests runs, so the batch only has to call that seam. The default check is a dynamic
    /// call, which PropertyDynamicCallsContractTests runs.
    /// </summary>
    public class PropertyWiringOrderContractTests
    {
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
        public void TheBatchSavesThroughTheSaveSeamAndDisposesNothingItself()
        {
            string batch = MemberText(PropertyService(), "public string SetProperties(");

            Assert.Contains("PropertyApplyOnSaveRule.Save(obj, controlName, properties.Properties().Select(p => p.Name), () =>", batch);
            Assert.DoesNotContain("PropertyApplyOnSaveRule.Begin(", batch);
            Assert.DoesNotContain("suppression?.Dispose();", batch);
        }

        [Fact]
        public void TheRollbackConfirmsEachPropertyByItsDefaultStateOnTheFreshObject()
        {
            string rollback = MemberText(PropertyService(), "private string RollbackPropertySnapshot(");

            Assert.Contains("name => restored != null && IsPropertyDefaultSafe(restored, rollback.NativeNames[name])", rollback);
        }
    }
}
