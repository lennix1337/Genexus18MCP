using System;
using System.Dynamic;
using System.Collections.Generic;
using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public sealed class PropertyServiceResolverTests
    {
        [Fact]
        public void ReadOnlyDescriptorIsRejectedBeforeAnySave()
        {
            dynamic container = Container(
                Property("Caption", "old", typeof(string), readOnly: true));

            string result = PropertyService.ValidatePropertyWrite(container, "Caption", "new");

            Assert.StartsWith("PropertyReadOnly", result, StringComparison.Ordinal);
        }

        [Fact]
        public void InvalidTypedValueIsRejectedBeforeAnySave()
        {
            dynamic container = Container(
                Property("Visible", true, typeof(bool), readOnly: false));

            string result = PropertyService.ValidatePropertyWrite(container, "Visible", "maybe");

            Assert.StartsWith("InvalidPropertyValue", result, StringComparison.Ordinal);
        }

        [Fact]
        public void ValidTypedValueAndUnknownDescriptorAreDistinguished()
        {
            dynamic container = Container(
                Property("Visible", true, typeof(bool), readOnly: false));

            Assert.Null(PropertyService.ValidatePropertyWrite(container, "Visible", "false"));
            Assert.StartsWith("PropertyNotFound", PropertyService.ValidatePropertyWrite(container, "Missing", "x"), StringComparison.Ordinal);
        }

        private static ExpandoObject Container(params ExpandoObject[] properties)
        {
            dynamic result = new ExpandoObject();
            result.Properties = properties;
            return result;
        }

        private static ExpandoObject Property(string name, object value, Type type, bool readOnly)
        {
            dynamic result = new ExpandoObject();
            dynamic definition = new ExpandoObject();
            definition.Type = type;
            definition.ReadOnly = readOnly;
            result.Name = name;
            result.Value = value;
            result.Definition = definition;
            return result;
        }
    }
}
