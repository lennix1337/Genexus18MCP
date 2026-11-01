using GxMcp.TestSupport;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// The object_atomic save that only writes the properties of an existing object is a property write, so it suppresses the
    /// pattern on save the same way the property write does. The decision is PropertyApplyOnSaveRule's; these tests pin that
    /// the save of the atomic path calls it, and that the save with variables, rules or source is left as it was.
    /// </summary>
    public class AtomicAuthoringSaveContractTests
    {
        [Fact]
        public void ThePropertyOnlySaveOfAnExistingObjectSuppressesThePatternOnSave()
        {
            string src = RepoSource.WithoutComments(RepoSource.Read("src", "GxMcp.Worker", "Services", "AtomicAuthoringService.cs"));

            Assert.Equal(1, SourceAssert.Count(src, "PropertyApplyOnSaveRule.Begin(obj, null,"));
            Assert.Equal(1, SourceAssert.Count(src, "bool propertyOnlySave = existing != null"));
            Assert.Equal(1, SourceAssert.Count(src, "obj.EnsureSave(check: false);"));
        }
    }
}
