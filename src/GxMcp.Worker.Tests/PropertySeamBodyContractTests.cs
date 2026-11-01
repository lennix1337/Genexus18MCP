using System;
using GxMcp.TestSupport;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // The SDK-facing defaults of the PropertyService seams. Production answers them from the knowledge base, which a unit
    // test cannot reach, so these guards fix the expression each default evaluates: a default replaced by a constant, or by
    // one of its arguments, changes the text and fails here. The behavioural effect of each default is checked live.
    public sealed class PropertySeamBodyContractTests
    {
        private static readonly string Source = RepoSource
            .WithoutComments(RepoSource.Read("src", "GxMcp.Worker", "Services", "PropertyService.cs"))
            .Replace("\r\n", "\n");

        [Fact]
        public void TheFreshTargetIsReadIndependentlyOfTheObjectItWasGiven()
        {
            Assert.Contains("_objectService.FindObjectFreshByIdentity(obj)", Member("protected internal virtual KBObject FindFreshTarget(KBObject obj)"));
        }

        [Fact]
        public void TheStoredPropertyIsReadFromTheObjectBeingChecked()
        {
            Assert.Contains("ReadPropertyValueAt(obj, nativeName, controlName)", Member("protected internal virtual string ReadStoredProperty("));
        }

        [Fact]
        public void TheTypeOfAnObjectIsTheIdentifierOfItsDescriptor()
        {
            Assert.Contains("obj?.TypeDescriptor?.Id", Member("private static Guid ObjectTypeOf(KBObject obj)"));
        }

        [Fact]
        public void TheTargetFoundByNameIsTheObjectServiceLookup()
        {
            Assert.Contains("_objectService.FindObject(target, typeFilter)", Member("protected internal virtual KBObject FindTarget("));
        }

        [Fact]
        public void TheTypeOfTheTargetIsTheTypeOfItsObject()
        {
            Assert.Contains("ObjectTypeOf(obj)", Member("protected internal virtual Guid TargetTypeOf("));
        }

        // The member's text: from its declaration to the next blank line.
        private static string Member(string declaration)
        {
            int start = Source.IndexOf(declaration, StringComparison.Ordinal);
            Assert.True(start >= 0, "declaration not found: " + declaration);
            int end = Source.IndexOf("\n\n", start, StringComparison.Ordinal);
            return end < 0 ? Source.Substring(start) : Source.Substring(start, end - start);
        }
    }
}
