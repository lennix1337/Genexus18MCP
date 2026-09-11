using System.Collections.Generic;
using GxMcp.Worker.Helpers;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class DomainPropertyApplierTests
    {
        // Fake Domain mirroring the public surface DomainPropertyApplier writes to.
        // Type:string mirrors the test-fake path (eDBType enum on the real SDK).
        public class FakeDomain
        {
            public string Name { get; set; }
            public string Type { get; set; }
            public int Length { get; set; }
            public int Decimals { get; set; }
            public bool Signed { get; set; }
            public object DomainBasedOn { get; set; }
        }

        public class FakeEnumValue
        {
            public string Name { get; set; }
            public string Value { get; set; }
            public string Description { get; set; }
        }

        public class FakeEnumerableDomain
        {
            public List<FakeEnumValue> EnumValues { get; } = new List<FakeEnumValue>();
        }

        [Fact]
        public void ApplyPrimitive_SetsTypeLengthDecimalsSigned()
        {
            var d = new FakeDomain();
            bool ok = DomainPropertyApplier.ApplyPrimitive(d, "Character", length: 10, decimals: null, signed: null);
            Assert.True(ok);
            Assert.Equal("CHARACTER", d.Type);
            Assert.Equal(10, d.Length);
        }

        [Fact]
        public void ApplyPrimitive_NumericWithDecimalsAndSigned()
        {
            var d = new FakeDomain();
            bool ok = DomainPropertyApplier.ApplyPrimitive(d, "Numeric", length: 12, decimals: 2, signed: true);
            Assert.True(ok);
            Assert.Equal("NUMERIC", d.Type);
            Assert.Equal(12, d.Length);
            Assert.Equal(2, d.Decimals);
            Assert.True(d.Signed);
        }

        [Fact]
        public void ApplyPrimitive_UnknownType_ReturnsFalse()
        {
            var d = new FakeDomain();
            Assert.False(DomainPropertyApplier.ApplyPrimitive(d, "Nope", null, null, null));
        }

        [Fact]
        public void ApplyDomainBasedOn_SetsProperty()
        {
            var d = new FakeDomain();
            var basedOn = new FakeDomain { Type = "CHARACTER", Length = 20 };
            Assert.True(DomainPropertyApplier.ApplyDomainBasedOn(d, basedOn));
            Assert.Same(basedOn, d.DomainBasedOn);
        }

        [Fact]
        public void ApplyEnumValues_OnFakeWithoutSdk_ReturnsNegative()
        {
            // Without the real Artech.Genexus.Common.CustomTypes.EnumValues type loaded,
            // ApplyEnumValues cannot resolve the helper and must report a hard failure
            // so the caller can surface enumError. The Domain itself doesn't have a
            // direct EnumValues property on this fake.
            var d = new FakeDomain();
            var values = new List<DomainEnumValueSpec>
            {
                new DomainEnumValueSpec { Name = "Active",  Value = "\"A\"" },
                new DomainEnumValueSpec { Name = "Blocked", Value = "\"B\"" }
            };
            int applied = DomainPropertyApplier.ApplyEnumValues(d, values);
            Assert.Equal(-1, applied);
        }

        public class FakeItem
        {
            public string Name { get; set; }
            public string Type { get; set; }
            public int Length { get; set; }
            public int Decimals { get; set; }
            public bool Signed { get; set; }
            public object DomainBasedOn { get; set; }
            public object AttributeBasedOn { get; set; }
        }

        public class FakeAttribute
        {
            public string Name { get; set; }
            public string Type { get; set; }
        }

        [Fact]
        public void ApplyAttributeBasedOn_SetsProperty_AndGetsName()
        {
            var item = new FakeItem();
            var attr = new FakeAttribute { Name = "CardapioID", Type = "NUMERIC" };
            Assert.True(DomainPropertyApplier.ApplyAttributeBasedOn(item, attr));
            Assert.Same(attr, item.AttributeBasedOn);
            Assert.Equal("CardapioID", DomainPropertyApplier.GetAttributeBasedOnName(item));
        }

        [Fact]
        public void ApplyDomainBasedOn_SetsProperty_AndGetsName()
        {
            var item = new FakeItem();
            var dom = new FakeDomain { Name = "FakeDomain", Type = "VARCHAR" };
            Assert.True(DomainPropertyApplier.ApplyDomainBasedOn(item, dom));
            Assert.Same(dom, item.DomainBasedOn);
            Assert.Equal("FakeDomain", DomainPropertyApplier.GetDomainBasedOnName(item));
        }

        [Fact]
        public void ClearAttributeAndDomainBasedOn_ClearsProperties()
        {
            var item = new FakeItem
            {
                AttributeBasedOn = new FakeAttribute { Name = "CardapioID" },
                DomainBasedOn = new FakeDomain { Type = "VARCHAR" }
            };
            Assert.True(DomainPropertyApplier.ClearAttributeBasedOn(item));
            Assert.Null(item.AttributeBasedOn);
            Assert.True(DomainPropertyApplier.ClearDomainBasedOn(item));
            Assert.Null(item.DomainBasedOn);
        }

        [Fact]
        public void ApplyEnumValues_EmptyList_ReturnsZero()
        {
            var d = new FakeDomain();
            Assert.Equal(0, DomainPropertyApplier.ApplyEnumValues(d, new List<DomainEnumValueSpec>()));
        }

        [Fact]
        public void ReadEnumValues_AcceptsDirectlyEnumerableSdkShape()
        {
            var d = new FakeEnumerableDomain();
            d.EnumValues.Add(new FakeEnumValue
            {
                Name = "Queued",
                Value = "1",
                Description = "Waiting"
            });

            var values = DomainPropertyApplier.ReadEnumValues(d);

            var value = Assert.Single(values);
            Assert.Equal("Queued", (string)value["name"]);
            Assert.Equal("1", (string)value["value"]);
            Assert.Equal("Waiting", (string)value["description"]);
        }
    }
}
