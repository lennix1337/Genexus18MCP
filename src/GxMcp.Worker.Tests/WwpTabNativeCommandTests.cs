using System;
using System.Linq;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;
using Element = GxMcp.Worker.Tests.WwpEmptyNativeCommandTests.Element;

namespace GxMcp.Worker.Tests
{
    // Issue #552: the native add_tab path against the same fake element tree used by
    // WwpEmptyNativeCommandTests. It proves the command sequence, not the WorkWithPlus
    // package: whether the DVelop pattern accepts a synthesized tabs/tab in a Transaction
    // form is still unverified on a real KB.
    public class WwpTabNativeCommandTests
    {
        private static TabPart TransactionForm(out Element content, out Element attributes)
        {
            var part = new TabPart();
            Element transaction = part.RootElement.Children.CreateChildElement("transaction");
            part.RootElement.Children.Items.Add(transaction);
            Element main = transaction.Children.CreateChildElement("table");
            main.Attributes.SetPropertyValue("name", "TableMain");
            transaction.Children.Items.Add(main);
            content = main.Children.CreateChildElement("table");
            content.Attributes.SetPropertyValue("name", "TableContent");
            main.Children.Items.Add(content);
            attributes = content.Children.CreateChildElement("table");
            attributes.Attributes.SetPropertyValue("name", "TableAttributes");
            content.Children.Items.Add(attributes);
            foreach (string name in new[] { "A", "B" })
            {
                Element attribute = attributes.Children.CreateChildElement("attribute");
                attribute.Attributes.SetPropertyValue("attribute", "adbb33c9-0906-4971-833c-998de27e0676-" + name);
                attributes.Children.Items.Add(attribute);
            }
            return part;
        }

        private static JObject AddTab(params JObject[] children) => new JObject
        {
            ["controlName"] = "TabX", ["title"] = "X", ["children"] = new JArray(children)
        };

        [Fact]
        public void NativeAddTab_SynthesizesTabsInsideTheUpdateAndMovesTheAttribute()
        {
            TabPart part = TransactionForm(out Element content, out Element attributes);
            Element existing = attributes.Children.Items[0];

            JObject result = WwpActionService.ApplyNativeTabMutation(part, "add_tab",
                AddTab(new JObject { ["type"] = "attribute", ["attribute"] = "A", ["description"] = "Renamed" }));

            Assert.Null(result["error"]);
            Assert.Equal(1, part.Updates);
            Assert.False(part.TabsPresentWhenUpdateStarted, "the tabs container must be created inside ExecuteUpdate");
            Element tabs = content.Children.Items.Single(e => e.Type == "tabs");
            Element tab = tabs.Children.Items.Single();
            Assert.Equal("tab", tab.Type);
            Assert.Equal("TabX", tab.Attributes.GetPropertyValueString("ControlName"));
            Element table = tab.Children.Items.Single();
            Assert.Equal("Responsive", table.Attributes.GetPropertyValueString("type"));
            Assert.Same(existing, table.Children.Items.Single());
            Assert.Equal("Renamed", existing.Attributes.GetPropertyValueString("description"));
            Assert.Equal(new[] { "adbb33c9-0906-4971-833c-998de27e0676-B" },
                attributes.Children.Items.Select(e => e.Attributes.GetPropertyValueString("attribute")));
        }

        [Fact]
        public void NativeAddTab_RefusesAnArbitraryTable()
        {
            var part = new TabPart();
            Element selection = part.RootElement.Children.CreateChildElement("selection");
            part.RootElement.Children.Items.Add(selection);
            Element filters = selection.Children.CreateChildElement("table");
            filters.Attributes.SetPropertyValue("name", "TableFilters");
            selection.Children.Items.Add(filters);

            JObject result = WwpActionService.ApplyNativeTabMutation(part, "add_tab", AddTab());

            Assert.Equal("TabsContainerNotFound", (string)result["code"]);
            Assert.Equal(0, part.Updates);
            Assert.Empty(filters.Children.Items);
        }

        [Fact]
        public void NativeAddTab_AttributeRequestedTwice_IsRefused()
        {
            TabPart part = TransactionForm(out _, out _);
            var ex = Assert.ThrowsAny<Exception>(() => WwpActionService.ApplyNativeTabMutation(part, "add_tab",
                AddTab(new JObject { ["type"] = "attribute", ["attribute"] = "A" }, new JObject { ["type"] = "attribute", ["attribute"] = "A" })));
            Assert.Contains("requested more than once", (ex.InnerException ?? ex).Message);
        }

        internal sealed class TabPart
        {
            public Element RootElement { get; } = new Element("instance");
            public int Updates { get; private set; }
            public bool TabsPresentWhenUpdateStarted { get; private set; }
            public void ExecuteUpdate(string label, Action mutation)
            {
                Updates++;
                TabsPresentWhenUpdateStarted = ContainsTabs(RootElement);
                mutation();
            }
            private static bool ContainsTabs(Element element) =>
                element.Type == "tabs" || element.Children.Items.Any(ContainsTabs);
        }
    }
}

namespace Artech.Packages.Patterns.Objects
{
    internal sealed class InsertElementCommand
    {
        private readonly GxMcp.Worker.Tests.WwpEmptyNativeCommandTests.Element _parent;
        private readonly GxMcp.Worker.Tests.WwpEmptyNativeCommandTests.Element _child;
        private readonly int _index;
        public InsertElementCommand(object parent, object child, int index)
        {
            _parent = (GxMcp.Worker.Tests.WwpEmptyNativeCommandTests.Element)parent;
            _child = (GxMcp.Worker.Tests.WwpEmptyNativeCommandTests.Element)child;
            _index = index;
        }
        public bool IsSafeToExecute() => true;
        public void Execute() => _parent.Children.Items.Insert(Math.Min(_index, _parent.Children.Items.Count), _child);
    }

    // Children.MoveElement of the SDK: a position outside the list is ignored.
    internal sealed class MoveElementCommand
    {
        private readonly GxMcp.Worker.Tests.WwpEmptyNativeCommandTests.Element _parent;
        private readonly GxMcp.Worker.Tests.WwpEmptyNativeCommandTests.Element _child;
        private readonly int _newPosition;
        public MoveElementCommand(object parent, object child, int oldPosition, int newPosition)
        {
            _parent = (GxMcp.Worker.Tests.WwpEmptyNativeCommandTests.Element)parent;
            _child = (GxMcp.Worker.Tests.WwpEmptyNativeCommandTests.Element)child;
            _newPosition = newPosition;
        }
        public void Execute()
        {
            if (_newPosition < 0 || _newPosition > _parent.Children.Items.Count - 1) return;
            _parent.Children.Items.Remove(_child);
            _parent.Children.Items.Insert(_newPosition, _child);
        }
    }

    internal sealed class RemoveElementCommand
    {
        private readonly GxMcp.Worker.Tests.WwpEmptyNativeCommandTests.Element _parent;
        private readonly GxMcp.Worker.Tests.WwpEmptyNativeCommandTests.Element _child;
        public RemoveElementCommand(object parent, object child, int index)
        {
            _parent = (GxMcp.Worker.Tests.WwpEmptyNativeCommandTests.Element)parent;
            _child = (GxMcp.Worker.Tests.WwpEmptyNativeCommandTests.Element)child;
        }
        public bool IsSafeToExecute() => true;
        public void Execute() => _parent.Children.Items.Remove(_child);
    }
}
