using System;
using System.Collections.Generic;
using Artech.Architecture.Common.Objects;
using Artech.Genexus.Common.CustomTypes;
using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// A WebPanel's MasterPage is a WebPanelReference. It used to be read as the CLR type name
    /// (ToString of the reference) and any name written to it was rejected up front by the
    /// scalar converter. Reads render the referenced object's name; writes accept a name.
    /// </summary>
    public class PropertyWebPanelReferenceTests
    {
        public class FakeReference
        {
            private readonly string _name;
            public FakeReference(string name) { _name = name; }
            public string GetName(KBModel model) => _name;
            public override string ToString() => "Fake.Namespace.FakeReference";
        }

        public class FakeDefinition { public Type Type { get; set; } public bool ReadOnly { get; set; } }

    // Exposes GetName(KBModel) like a reference, but is not reference-shaped: its value
    // must keep rendering through ToString.
    public class ValueWithGetName
    {
        private readonly string _value;
        public ValueWithGetName(string value) { _value = value; }
        public string GetName(KBModel model) => "resolved-name";
        public override string ToString() => _value;
    }

    // Reference-shaped, but its GetName fails. Rendering must fall back rather than
    // reporting no value.
    public class ThrowingReference
    {
        public string GetName(KBModel model) => throw new InvalidOperationException("no name");
        public override string ToString() => "ThrowingReference(value)";
    }

        public class FakeProperty
        {
            public string Name { get; set; }
            public FakeDefinition Definition { get; set; }
            public object Value { get; set; }
        }

        public class FakeContainer
        {
            public List<FakeProperty> Properties { get; } = new List<FakeProperty>();
        }

        [Fact]
        public void Render_ReferenceExposingGetName_YieldsReferencedName()
        {
            Assert.Equal("SampleMasterPage", PropertyService.RenderPropertyValue(new FakeReference("SampleMasterPage"), null));
        }

        [Fact]
        public void Render_EmptyReference_YieldsEmptyString()
        {
            Assert.Equal("", PropertyService.RenderPropertyValue(new FakeReference(null), null));
        }

        [Fact]
        public void Render_NoneRef_YieldsEmptyString_NotTheTypeName()
        {
            Assert.Equal("", PropertyService.RenderPropertyValue(WebPanelReference.NoneRef, null));
        }

        [Fact]
        public void Render_OtherValues_KeepToString()
        {
            Assert.Equal("5", PropertyService.RenderPropertyValue(5, null));
            Assert.Equal("abc", PropertyService.RenderPropertyValue("abc", null));
            Assert.Equal("", PropertyService.RenderPropertyValue(null, null));
        }

        // A non-reference value that happens to expose GetName(KBModel) must keep its own
        // rendering. Resolving every property's GetName changed how unrelated types display,
        // which is wider than this fix needs.
        [Fact]
        public void Render_NonReferenceValueWithGetName_KeepsItsOwnValue()
        {
            Assert.Equal("own-value", PropertyService.RenderPropertyValue(new ValueWithGetName("own-value"), null));
        }

        // A reference-shaped value whose GetName throws must not render as empty: an empty
        // read is indistinguishable from a wiped property and trips the safety net.
        [Fact]
        public void Render_ReferenceWhoseGetNameThrows_DoesNotBecomeEmpty()
        {
            string rendered = PropertyService.RenderPropertyValue(new ThrowingReference(), null);

            Assert.NotEqual("", rendered);
            Assert.Equal(new ThrowingReference().ToString(), rendered);
        }

        [Fact]
        public void IsReferenceShapedType_CoversReferencesAndNothingElse()
        {
            Assert.True(PropertyService.IsReferenceShapedType(typeof(WebPanelReference)));
            Assert.True(PropertyService.IsReferenceShapedType(typeof(FakeReference)));
            Assert.False(PropertyService.IsReferenceShapedType(typeof(ValueWithGetName)));
            Assert.False(PropertyService.IsReferenceShapedType(typeof(string)));
            Assert.False(PropertyService.IsReferenceShapedType(typeof(int)));
            Assert.False(PropertyService.IsReferenceShapedType(null));
        }

        [Fact]
        public void IsWebPanelReferenceType_OnlyForReferenceType()
        {
            Assert.True(PropertyService.IsWebPanelReferenceType(typeof(WebPanelReference)));
            Assert.False(PropertyService.IsWebPanelReferenceType(typeof(string)));
            Assert.False(PropertyService.IsWebPanelReferenceType(null));
        }

        [Fact]
        public void ValidatePropertyWrite_AcceptsAnObjectNameForAWebPanelReference()
        {
            var container = new FakeContainer();
            container.Properties.Add(new FakeProperty
            {
                Name = "MasterPage",
                Definition = new FakeDefinition { Type = typeof(WebPanelReference) },
                Value = WebPanelReference.NoneRef
            });

            Assert.Null(PropertyService.ValidatePropertyWrite(container, "MasterPage", "SampleMasterPage"));
            Assert.Null(PropertyService.ValidatePropertyWrite(container, "MasterPage", ""));
        }

        [Fact]
        public void ValidatePropertyWrite_StillRejectsBadScalarForOtherTypes()
        {
            var container = new FakeContainer();
            container.Properties.Add(new FakeProperty
            {
                Name = "Count",
                Definition = new FakeDefinition { Type = typeof(int) },
                Value = 1
            });

            Assert.StartsWith("InvalidPropertyValue", PropertyService.ValidatePropertyWrite(container, "Count", "abc"));
        }

        [Fact]
        public void SelectedRecoveryRead_UsesTypedValuesAndOnlyRequestedProperties()
        {
            var container = new FakeContainer();
            container.Properties.Add(new FakeProperty { Name = "IsMain", Value = true,
                Definition = new FakeDefinition { Type = typeof(bool) } });
            container.Properties.Add(new FakeProperty { Name = "MasterPage", Value = new FakeReference("SampleMasterPage"),
                Definition = new FakeDefinition { Type = typeof(WebPanelReference) } });
            container.Properties.Add(new FakeProperty { Name = "Unrelated", Value = "private" });

            var result = PropertyService.SerializeSelectedProperties(container, null,
                new[] { "MainProgram", "MasterPage" }, webPanel: true);
            var properties = (Newtonsoft.Json.Linq.JArray)result["properties"];

            Assert.Equal(2, properties.Count);
            Assert.Equal("MainProgram", properties[0]["name"]?.ToString());
            Assert.Equal("IsMain", properties[0]["nativeName"]?.ToString());
            Assert.Equal("True", properties[0]["value"]?.ToString());
            Assert.Equal("SampleMasterPage", properties[1]["value"]?.ToString());
        }
    }
}
