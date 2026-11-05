using System;
using GxMcp.Worker.Helpers;
using System.Reflection;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class AttributeTypeApplierTests
    {
        // Fake attribute exposing the surface AttributeTypeApplier writes to.
        // Mirrors the public properties of Artech.Genexus.Common.Objects.Attribute that we touch.
        public class FakeAttribute
        {
            public string Type { get; set; }
            public int Length { get; set; }
            public int Decimals { get; set; }
            public object DomainBasedOn { get; set; }
        }

        private sealed class FailingLengthAttribute
        {
            public string Type { get; set; }
            public int Length
            {
                get { return 0; }
                set { throw new InvalidOperationException("length is read-only"); }
            }
        }

        [Fact]
        public void TryParse_RecognizesPrimitiveWithLength()
        {
            var spec = AttributeTypeApplier.Parse("Character(40)");
            Assert.True(spec.Recognized);
            Assert.Equal("Character", spec.CanonicalType);
            Assert.Equal(40, spec.Length);
            Assert.Null(spec.Decimals);
            Assert.Null(spec.DomainName);
        }

        [Fact]
        public void TryParse_RecognizesNumericWithDecimals()
        {
            var spec = AttributeTypeApplier.Parse("Numeric(18,4)");
            Assert.True(spec.Recognized);
            Assert.Equal("Numeric", spec.CanonicalType);
            Assert.Equal(18, spec.Length);
            Assert.Equal(4, spec.Decimals);
        }

        [Fact]
        public void TryParse_RecognizesBareTypes()
        {
            Assert.Equal("DateTime", AttributeTypeApplier.Parse("DateTime").CanonicalType);
            Assert.Equal("Date", AttributeTypeApplier.Parse("Date").CanonicalType);
            Assert.Equal("Boolean", AttributeTypeApplier.Parse("Boolean").CanonicalType);
            Assert.Equal("Boolean", AttributeTypeApplier.Parse("Bool").CanonicalType);
        }

        [Fact]
        public void TryParse_StripsSerializerCommentTail()
        {
            // The DSL serializer writes "UserLogin (Character)" — the trailing (Character)
            // is documentation. Parse must strip it and treat the head as a domain name.
            var spec = AttributeTypeApplier.Parse("UserLogin (Character)");
            Assert.True(spec.Recognized);
            Assert.Equal("DomainReference", spec.CanonicalType);
            Assert.Equal("UserLogin", spec.DomainName);
        }

        [Fact]
        public void TryParse_RecognizesAmpersandDomainReference()
        {
            // Same convention as VariableTypeResolver: &Foo → DomainReference
            var spec = AttributeTypeApplier.Parse("&UserLogin");
            Assert.True(spec.Recognized);
            Assert.Equal("DomainReference", spec.CanonicalType);
            Assert.Equal("UserLogin", spec.DomainName);
        }

        [Fact]
        public void TryParse_UnrecognizedNonPrimitive_TreatedAsDomainCandidate()
        {
            // A bare identifier that isn't a primitive (e.g., "UserLogin", "AutoNum18") is a
            // domain reference candidate — the parser flags it as such; resolution against the
            // KB Domain.Get is the caller's responsibility.
            var spec = AttributeTypeApplier.Parse("AutoNum18");
            Assert.True(spec.Recognized);
            Assert.Equal("DomainReference", spec.CanonicalType);
            Assert.Equal("AutoNum18", spec.DomainName);
        }

        [Fact]
        public void TryParse_EmptyOrUnknown_NotRecognized()
        {
            Assert.False(AttributeTypeApplier.Parse(null).Recognized);
            Assert.False(AttributeTypeApplier.Parse("").Recognized);
            Assert.False(AttributeTypeApplier.Parse("Unknown").Recognized);
            // Pure punctuation
            Assert.False(AttributeTypeApplier.Parse("(40)").Recognized);
        }

        [Fact]
        public void ApplyPrimitive_SetsTypeLengthDecimalsOnFakeAttribute()
        {
            var fake = new FakeAttribute();
            bool applied = AttributeTypeApplier.ApplyPrimitive(fake, "Numeric", 18, 4);
            Assert.True(applied);
            Assert.Equal("NUMERIC", fake.Type);
            Assert.Equal(18, fake.Length);
            Assert.Equal(4, fake.Decimals);
        }

        [Fact]
        public void ApplyPrimitive_CharacterMapsToCHARACTER()
        {
            var fake = new FakeAttribute();
            AttributeTypeApplier.ApplyPrimitive(fake, "Character", 40, null);
            Assert.Equal("CHARACTER", fake.Type);
            Assert.Equal(40, fake.Length);
        }

        [Fact]
        public void ApplyPrimitive_DateTimeMapsToDATETIME()
        {
            var fake = new FakeAttribute();
            AttributeTypeApplier.ApplyPrimitive(fake, "DateTime", null, null);
            Assert.Equal("DATETIME", fake.Type);
        }

        [Fact]
        public void TryApplyType_MapsPropertyValueToTypedAttributeSurface()
        {
            var fake = new FakeAttribute { Type = "NUMERIC" };

            bool applied = AttributeTypeApplier.TryApplyType(fake, "Character", out string error);

            Assert.True(applied, error);
            Assert.Equal("CHARACTER", fake.Type);
        }

        [Fact]
        public void ApplyPrimitive_ReturnsFalse_WhenRequestedLengthCannotBeApplied()
        {
            var fake = new FailingLengthAttribute { Type = "ORIGINAL" };

            bool applied = AttributeTypeApplier.ApplyPrimitive(fake, "Character", 40, null);

            Assert.False(applied);
            Assert.Equal("ORIGINAL", fake.Type);
        }

        // Same GetByName shape as the SDK's IKBModelObjects: (string module, Guid? type, string name).
        public class FakeKbObjects
        {
            public string LastName;
            public System.Collections.Generic.IEnumerable<object> GetByName(string module, Guid? type, string name)
            {
                LastName = name;
                return new object[] { "match:" + name };
            }
        }

        public interface IFakeKbObjects
        {
            System.Collections.Generic.IEnumerable<object> GetByName(string module, Guid? type, string name);
        }

        private sealed class ExplicitFakeKbObjects : IFakeKbObjects
        {
            System.Collections.Generic.IEnumerable<object> IFakeKbObjects.GetByName(string module, Guid? type, string name)
                => new object[] { "explicit:" + name };
        }

        [Fact]
        public void DomainLookupCallsTheSdkGetByNameShape()
        {
            var objects = new FakeKbObjects();
            var matches = AttributeTypeApplier.InvokeGetByName(objects, "SampleDomain");
            Assert.NotNull(matches);
            Assert.Equal("SampleDomain", objects.LastName);
            Assert.Contains("match:SampleDomain", System.Linq.Enumerable.Cast<object>(matches));
        }

        [Fact]
        public void DomainLookupFindsAnExplicitInterfaceImplementation()
        {
            var matches = AttributeTypeApplier.InvokeGetByName(new ExplicitFakeKbObjects(), "SampleDomain");
            Assert.NotNull(matches);
            Assert.Contains("explicit:SampleDomain", System.Linq.Enumerable.Cast<object>(matches));
        }

        [Fact]
        public void DomainLookupOnNullObjectsIsNull()
        {
            Assert.Null(AttributeTypeApplier.InvokeGetByName(null, "SampleDomain"));
        }

        public enum FakeDbType { NUMERIC = 4, CHARACTER = 5 }

        // Mirrors the Artech SDTItem hierarchy, where a derived class shadows Type.
        public class FakeSdtItemBase
        {
            public object Type { get; set; }
        }

        public sealed class FakeSdtItem : FakeSdtItemBase
        {
            public new FakeDbType Type { get; set; } = FakeDbType.NUMERIC;
        }

        [Fact]
        public void SetMemberType_writes_the_shadowing_Type_property_of_an_sdt_member()
        {
            var item = new FakeSdtItem();

            AttributeTypeApplier.SetMemberType(item, FakeDbType.CHARACTER);

            Assert.Equal(FakeDbType.CHARACTER, item.Type);
            Assert.Null(((FakeSdtItemBase)item).Type);
        }

        [Fact]
        public void SetMemberType_ignores_null_target_and_null_type()
        {
            var item = new FakeSdtItem();

            AttributeTypeApplier.SetMemberType(null, FakeDbType.CHARACTER);
            AttributeTypeApplier.SetMemberType(item, null);

            Assert.Equal(FakeDbType.NUMERIC, item.Type);
        }
    }
}
