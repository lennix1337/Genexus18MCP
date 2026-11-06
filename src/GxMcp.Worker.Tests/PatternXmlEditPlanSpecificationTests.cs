using System.Collections.Generic;
using System.Linq;
using GxMcp.Worker.Helpers;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // A leaf added by a raw PatternInstance edit may carry the attributes the installed pattern
    // specification declares for its type, not only the ones in the static allow-list. A header
    // button of a MasterPage needs imageClass, buttonClass, ControlType and the like, which the
    // static list never named.
    public class PatternXmlEditPlanSpecificationTests
    {
        private sealed class FakeSpec : IPatternSpecView
        {
            private readonly Dictionary<string, string[]> _attributes = new Dictionary<string, string[]>
            {
                ["Instance"] = new[] { "Template" },
                ["Root"] = new[] { "Template", "childrenOrderedList" },
                ["Table"] = new[] { "name", "type", "numberOfColumns", "childrenOrderedList" },
                ["TextBlock"] = new[] { "controlName", "caption", "mergeWithPrevious" },
                ["UserAction"] = new[]
                {
                    "name", "caption", "gxobject", "imageClass", "buttonClass", "ControlType", "imageType",
                    "popupWidth", "flexGrow", "defaultCaption"
                }
            };

            private readonly Dictionary<string, string> _children = new Dictionary<string, string>
            {
                ["Instance/WPRoot"] = "Root",
                ["Root/table"] = "Table",
                ["Table/table"] = "Table",
                ["Table/userAction"] = "UserAction",
                ["Table/textBlock"] = "TextBlock"
            };

            public string RootType() => "Instance";

            public string ChildType(string parentType, string childName) =>
                _children.TryGetValue(parentType + "/" + childName, out string type) ? type : null;

            public IReadOnlyCollection<string> AttributeNames(string typeName) =>
                _attributes.TryGetValue(typeName, out string[] names) ? new HashSet<string>(names) : null;
        }

        // Derived from WWP 11.1.38 WorkWithPlusInstance.xml: WPRoot -> WPTable -> WPTable/control.
        private static string Selection(string extra) =>
            "<instance><WPRoot Template='Selection'><table name='Main'>" + extra + "</table></WPRoot></instance>";

        [Fact]
        public void Selection_NestedTableWithTypedChildren_IsAcceptedBySpecification()
        {
            var plan = PatternXmlEditPlan.Create(Selection(""), Selection("<table name='Rows' numberOfColumns='2'><textBlock controlName='Label' caption='Show' mergeWithPrevious='True'/></table>"), false, new FakeSpec());
            Assert.Null(plan.ErrorCode);
            Assert.Equal("table", (string)Assert.Single(plan.Changes)["element"]);
        }

        [Fact]
        public void Selection_ChildAbsentFromSpecification_IsRefused()
        {
            var plan = PatternXmlEditPlan.Create(Selection(""), Selection("<table name='Rows'><variable name='Missing'/></table>"), false, new FakeSpec());
            Assert.Equal("PatternStructureChangeUnsupported", plan.ErrorCode);
            Assert.Contains("not declared", plan.Error);
        }

        [Fact]
        public void Selection_ExplicitRow_IsRefusedWithSupportedShape()
        {
            var plan = PatternXmlEditPlan.Create(Selection(""), Selection("<table name='Rows'><row/></table>"), false, new FakeSpec());
            Assert.Equal("PatternStructureChangeUnsupported", plan.ErrorCode);
            Assert.Contains("Selection", plan.Error);
            Assert.Contains("row", plan.Error);
            Assert.Contains("numberOfColumns", plan.Error);
        }

        private const string Existing = "<userAction name=\"NotificationsWC\" caption=\"Notifications\" />";

        private static string Doc(string extra) =>
            "<instance><WPRoot Template=\"Empty\"><table name=\"TableMain\" type=\"Responsive\">"
            + "<table name=\"TableHeader\" type=\"Flex\"><table name=\"TableUserRole\" type=\"Flex\">"
            + Existing + extra
            + "</table></table></table></WPRoot></instance>";

        private const string RichAction = "<userAction name=\"Empresa\" caption=\"Empresa\" imageClass=\"fas fa-building\""
            + " buttonClass=\"Header\" ControlType=\"DropDownComponent\" imageType=\"Font icon\" popupWidth=\"360\" flexGrow=\"0\" />";

        [Fact]
        public void WithoutSpecification_AttributesOutsideTheStaticListAreRefused()
        {
            var plan = PatternXmlEditPlan.Create(Doc(string.Empty), Doc(RichAction));

            Assert.Equal("PatternStructureChangeUnsupported", plan.ErrorCode);
            Assert.Contains("Attribute 'imageClass' is not permitted on a <userAction>", plan.Error);
        }

        [Fact]
        public void WithSpecification_DeclaredAttributesArePermitted()
        {
            var plan = PatternXmlEditPlan.Create(Doc(string.Empty), Doc(RichAction), false, new FakeSpec());

            Assert.Null(plan.ErrorCode);
            var change = Assert.Single(plan.Changes);
            Assert.Equal("Insert", (string)change["operation"]);
            Assert.Equal("userAction", (string)change["element"]);
            Assert.Equal("Empresa", (string)change["identity"]);
        }

        [Fact]
        public void WithSpecification_AnAttributeItDoesNotDeclareIsStillRefused()
        {
            var plan = PatternXmlEditPlan.Create(Doc(string.Empty),
                Doc("<userAction name=\"Empresa\" caption=\"Empresa\" bogusAttribute=\"x\" />"), false, new FakeSpec());

            Assert.Equal("PatternStructureChangeUnsupported", plan.ErrorCode);
            Assert.Contains("Attribute 'bogusAttribute' is not permitted", plan.Error);
            Assert.Contains("imageClass", plan.Error);
        }

        [Fact]
        public void WithSpecification_SdkOwnedMetadataIsRefusedEvenWhenDeclared()
        {
            var plan = PatternXmlEditPlan.Create(Doc(string.Empty),
                Doc("<userAction name=\"Empresa\" caption=\"Empresa\" defaultCaption=\"Empresa\" />"), false, new FakeSpec());

            Assert.Equal("PatternStructureChangeUnsupported", plan.ErrorCode);
            Assert.Contains("Attribute 'defaultCaption' is not permitted", plan.Error);
            Assert.DoesNotContain("defaultCaption,", plan.Error);
        }

        [Fact]
        public void WithSpecification_AContainerTheSpecificationCannotPlaceKeepsTheStaticList()
        {
            // Same document, but the specification does not know the 'WPRoot' element.
            var plan = PatternXmlEditPlan.Create(Doc(string.Empty), Doc(RichAction), false, new UnplacedSpec());

            Assert.Equal("PatternStructureChangeUnsupported", plan.ErrorCode);
            Assert.Contains("Attribute 'imageClass' is not permitted", plan.Error);
        }

        [Fact]
        public void WithSpecification_AStaticListAttributeNeedsNoDeclaration()
        {
            var plan = PatternXmlEditPlan.Create(Doc(string.Empty),
                Doc("<userAction name=\"Empresa\" caption=\"Empresa\" visible=\"False\" />"), false, new UnplacedSpec());

            Assert.Null(plan.ErrorCode);
            Assert.Single(plan.Changes);
        }

        [Fact]
        public void DeclaredAttributesDoNotWidenAnotherElementKind()
        {
            // A textBlock is not a userAction: the userAction attributes stay refused on it.
            var plan = PatternXmlEditPlan.Create(Doc(string.Empty),
                Doc("<textBlock controlName=\"Aviso\" caption=\"Aviso\" imageClass=\"x\" />"), false, new FakeSpec());

            Assert.Equal("PatternStructureChangeUnsupported", plan.ErrorCode);
            Assert.Contains("Attribute 'imageClass' is not permitted on a <textBlock>", plan.Error);
        }

        [Fact]
        public void TheRulesTableWidensOnlyWithDeclaredNames()
        {
            var element = System.Xml.Linq.XElement.Parse("<userAction name=\"A\" imageClass=\"c\" />");

            Assert.Equal("imageClass", StructuralLayoutRules.FirstDisallowedAttribute("userAction", element));
            Assert.Null(StructuralLayoutRules.FirstDisallowedAttribute("userAction", element, new[] { "imageClass" }));
            Assert.Equal("imageClass", StructuralLayoutRules.FirstDisallowedAttribute("userAction", element, new[] { "caption" }));
            Assert.True(StructuralLayoutRules.IsSdkOwnedAttribute("defaultImageClass"));
            Assert.True(StructuralLayoutRules.IsSdkOwnedAttribute("templateName"));
            Assert.True(StructuralLayoutRules.IsSdkOwnedAttribute("childrenOrderedList"));
            Assert.False(StructuralLayoutRules.IsSdkOwnedAttribute("imageClass"));
        }

        private sealed class UnplacedSpec : IPatternSpecView
        {
            public string RootType() => "Instance";
            public string ChildType(string parentType, string childName) => null;
            public IReadOnlyCollection<string> AttributeNames(string typeName) => new HashSet<string> { "imageClass" };
        }
    }
}
