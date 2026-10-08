using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class WwpEmptyNativeCommandTests
    {
        [Fact]
        public void NativeCommandsCreateTheMissingTableAndActionTogether()
        {
            var part = new Part();
            var main = part.RootElement.Children.CreateChildElement("table");
            main.Attributes.SetPropertyValue("name", "TableMain");
            part.RootElement.Children.Items.Add(main);
            JObject args = JObject.Parse("{actionName:'Confirm',caption:'Confirm'}");
            Assert.Null(WwpActionService.ApplyNativeFormUserAction(part, args)["error"]);
            var actions = main.Children.Items.Single();
            Assert.Equal("TableActions", actions.Attributes.GetPropertyValueString("name"));
            Assert.Equal("Confirm", actions.Children.Items.Single().Attributes.GetPropertyValueString("name"));
            Assert.Equal(1, part.Updates);
        }

        [Fact]
        public void NativeCommandsWriteVariableLengthsAndReadOnlyWithTheirClrTypes()
        {
            var part = new Part();
            var content = part.RootElement.Children.CreateChildElement("table");
            content.Attributes.SetPropertyValue("name", "TableContent");
            part.RootElement.Children.Items.Add(content);
            JObject args = JObject.Parse("{containerName:'TableContent',columns:[{variable:'Code',basicType:'Character',length:13,readOnly:false}]}");
            Assert.Null(WwpActionService.ValidateVariableGridArgs(args, out var columns));
            Assert.Null(WwpActionService.ApplyNativeVariableGrid(part, args, columns)["error"]);
            var grid = content.Children.Items.Single();
            var variable = grid.Children.Items.Single();
            Assert.IsType<int>(variable.Attributes.GetPropertyValue("basicCLength"));
            Assert.Equal(13, variable.Attributes.GetPropertyValue("basicCLength"));
            Assert.IsType<bool>(variable.Attributes.GetPropertyValue("readOnly"));
            Assert.Equal(false, variable.Attributes.GetPropertyValue("readOnly"));
            Assert.Null(grid.Attributes.GetPropertyValue("SDTCollection"));
            Assert.Equal(1, part.Updates);
        }

        internal sealed class Part
        {
            public Element RootElement { get; } = new Element("instance");
            public int Updates { get; private set; }
            public void ExecuteUpdate(string label, Action mutation) { Updates++; mutation(); }
        }

        internal sealed class Element
        {
            public Element(string type) { Type = type; }
            public string Type { get; }
            public Attributes Attributes { get; } = new Attributes();
            public Children Children { get; } = new Children();
            public string ToXmlString() => new XElement(Type).ToString();
        }

        internal sealed class Attributes
        {
            private readonly Dictionary<string, object> _values = new Dictionary<string, object>();
            public object GetPropertyValue(string key) => _values.TryGetValue(key, out object value) ? value : null;
            public string GetPropertyValueString(string key) => GetPropertyValue(key)?.ToString();
            public void SetPropertyValue(string key, object value) { _values[key] = value; }
        }

        internal sealed class Children : IEnumerable
        {
            public List<Element> Items { get; } = new List<Element>();
            public Element CreateChildElement(string type) => new Element(type);
            public IEnumerator GetEnumerator() => Items.GetEnumerator();
        }
    }
}

namespace Artech.Packages.Patterns.Objects
{
    internal sealed class AddElementCommand
    {
        private readonly GxMcp.Worker.Tests.WwpEmptyNativeCommandTests.Element _parent;
        private readonly GxMcp.Worker.Tests.WwpEmptyNativeCommandTests.Element _child;
        public AddElementCommand(object parent, object child)
        {
            _parent = (GxMcp.Worker.Tests.WwpEmptyNativeCommandTests.Element)parent;
            _child = (GxMcp.Worker.Tests.WwpEmptyNativeCommandTests.Element)child;
        }
        public bool IsSafeToExecute() => true;
        public void Execute() => _parent.Children.Items.Add(_child);
    }
}
