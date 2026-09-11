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
    }
}
