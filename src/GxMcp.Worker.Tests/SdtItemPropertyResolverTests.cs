using System;
using System.Collections.Generic;
using System.Linq;
using GxMcp.Worker.Services.Structure;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class SdtItemPropertyResolverTests
    {
        private static SdtPropertyInfo Json() => FakeSdtStore.DefaultCatalog().First(p => p.NativeName == FakeSdtStore.Native);

        // ---- property names

        [Theory]
        [InlineData("idJsonInclude")]            // the bag key
        [InlineData("JsonNullSerialization")]    // the SDK typed constant
        [InlineData("Json Null Serialization")]  // the IDE name
        [InlineData("jsonnullserialization")]
        [InlineData("JSON NULL SERIALIZATION")]
        [InlineData("json_null_serialization")]
        [InlineData("JsonInclude")]              // the bag key without its id prefix
        [InlineData("  JsonNullSerialization  ")]
        public void EveryDocumentedSpellingResolvesTheSameProperty(string spelling)
        {
            var match = SdtItemPropertyResolver.Resolve(FakeSdtStore.DefaultCatalog(), spelling);
            Assert.NotNull(match.Info);
            Assert.Equal(FakeSdtStore.Native, match.Info.NativeName);
            Assert.Null(match.Ambiguous);
        }

        [Fact]
        public void AnExactSpellingBeatsALooserOneThatWouldAlsoMatch()
        {
            var catalog = new List<SdtPropertyInfo>
            {
                new SdtPropertyInfo { NativeName = "Required", DisplayName = "Required" },
                new SdtPropertyInfo { NativeName = "idRequired", DisplayName = "Required (service)" }
            };
            var match = SdtItemPropertyResolver.Resolve(catalog, "Required");
            Assert.Equal("Required", match.Info.NativeName);
        }

        [Fact]
        public void ASpellingThatFitsTwoPropertiesIsAmbiguousNotGuessed()
        {
            var catalog = new List<SdtPropertyInfo>
            {
                new SdtPropertyInfo { NativeName = "idA", DisplayName = "Same Name" },
                new SdtPropertyInfo { NativeName = "idB", DisplayName = "Same Name" }
            };
            var match = SdtItemPropertyResolver.Resolve(catalog, "same name");
            Assert.Null(match.Info);
            Assert.Equal(new[] { "idA", "idB" }, match.Ambiguous.Select(p => p.NativeName).OrderBy(n => n).ToArray());
        }

        [Fact]
        public void AnUnknownNameOffersCloseSpellingsInsteadOfGuessing()
        {
            var match = SdtItemPropertyResolver.Resolve(FakeSdtStore.DefaultCatalog(), "JsonNullSerialisation");
            Assert.Null(match.Info);
            Assert.Null(match.Ambiguous);
            Assert.Contains("JsonNullSerialization", match.Suggestions);
        }

        [Fact]
        public void ABlankNameMatchesNothing()
        {
            Assert.Null(SdtItemPropertyResolver.Resolve(FakeSdtStore.DefaultCatalog(), "  ").Info);
            Assert.Null(SdtItemPropertyResolver.Resolve(FakeSdtStore.DefaultCatalog(), null).Info);
        }

        [Fact]
        public void TheCanonicalNameIsTheSdkConstantElseTheCompactIdeName()
        {
            Assert.Equal("JsonNullSerialization", Json().CanonicalName);
            var noConstant = new SdtPropertyInfo { NativeName = "idX", DisplayName = "Some Display Name" };
            Assert.Equal("SomeDisplayName", noConstant.CanonicalName);
            Assert.Equal("idBare", new SdtPropertyInfo { NativeName = "idBare" }.CanonicalName);
        }

        // ---- structural properties

        [Theory]
        [InlineData("Name", true)]
        [InlineData("ATTCUSTOMTYPE", true)]
        [InlineData("idBasedOn", true)]
        [InlineData("AttCollection", true)]
        [InlineData("Length", true)]
        [InlineData("Decimals", true)]
        [InlineData("AttMaxLen", true)]      // the maximum length of a character member: the SDK also rewrites Length
        [InlineData("AttNumDim", true)]
        [InlineData("idJsonInclude", false)]
        [InlineData("idJsonName", false)]
        public void IdentityAndTypePropertiesAreStructural(string nativeName, bool structural)
        {
            Assert.Equal(structural, SdtItemPropertyResolver.IsStructural(new SdtPropertyInfo { NativeName = nativeName }));
        }

        // ---- enumerated values

        [Theory]
        [InlineData("idJsonEmpty", "idJsonEmpty")]    // the stored id
        [InlineData("IDJSONEMPTY", "idJsonEmpty")]
        [InlineData("Empty", "idJsonEmpty")]          // the typed enumeration member
        [InlineData("empty", "idJsonEmpty")]
        [InlineData("JsonNull", "idJsonJsonNull")]
        [InlineData("JSON null", "idJsonJsonNull")]   // the IDE name
        [InlineData("json_null", "idJsonJsonNull")]
        [InlineData("NoProperty", "idJsonNoProperty")]
        [InlineData("No Property", "idJsonNoProperty")]
        [InlineData("  no property ", "idJsonNoProperty")]
        public void AnEnumeratedValueResolvesFromAnyOfItsThreeSpellings(string requested, string expectedRaw)
        {
            object typed;
            string raw, error;
            Assert.True(SdtItemPropertyResolver.TryResolveValue(Json(), requested, out typed, out raw, out error), error);
            Assert.Equal(expectedRaw, raw);
            Assert.Equal(expectedRaw, typed);
        }

        [Fact]
        public void AnEnumeratedValueIsStoredAsTheObjectTheSdkListedNotAsItsText()
        {
            // An integer enumeration: the SDK lists 0, 1 and 2 as integers and the property bag must receive an integer.
            var info = new SdtPropertyInfo { NativeName = "AttNumDim", ClrType = typeof(int) };
            info.Options.Add(new SdtPropertyOption { Raw = "0", Label = "Scalar", Value = 0 });
            info.Options.Add(new SdtPropertyOption { Raw = "1", Label = "Vector", Value = 1 });
            object typed;
            string raw, error;

            Assert.True(SdtItemPropertyResolver.TryResolveValue(info, "vector", out typed, out raw, out error), error);

            Assert.IsType<int>(typed);
            Assert.Equal(1, typed);
            Assert.Equal("1", raw);
        }

        [Fact]
        public void AnEnumeratedValueFallsBackToItsTextWhenTheSdkListedNoObject()
        {
            var info = new SdtPropertyInfo { NativeName = "idX", ClrType = typeof(string) };
            info.Options.Add(new SdtPropertyOption { Raw = "idXOne", Member = "One" });
            object typed;
            string raw, error;

            Assert.True(SdtItemPropertyResolver.TryResolveValue(info, "One", out typed, out raw, out error), error);

            Assert.Equal("idXOne", typed);
        }

        [Fact]
        public void AnEnumeratedValueOutsideTheSetIsRefusedWithTheAllowedList()
        {
            object typed;
            string raw, error;
            Assert.False(SdtItemPropertyResolver.TryResolveValue(Json(), "Nothing", out typed, out raw, out error));
            Assert.Null(typed);
            Assert.Contains("Empty (idJsonEmpty)", error);
            Assert.Contains("NoProperty (idJsonNoProperty)", error);
        }

        [Fact]
        public void ATextValueIsKeptExactlyIncludingItsSpaces()
        {
            var info = new SdtPropertyInfo { NativeName = "idJsonName", ClrType = typeof(string) };
            object typed;
            string raw, error;
            Assert.True(SdtItemPropertyResolver.TryResolveValue(info, "  my name ", out typed, out raw, out error));
            Assert.Equal("  my name ", typed);
            Assert.Equal("  my name ", raw);
        }

        [Theory]
        [InlineData("true", true, "True")]
        [InlineData("False", false, "False")]
        [InlineData("1", true, "True")]
        [InlineData("no", false, "False")]
        public void ABooleanIsParsedAndStoredAsTrueOrFalse(string requested, bool expected, string expectedRaw)
        {
            var info = new SdtPropertyInfo { NativeName = "Required", ClrType = typeof(bool) };
            object typed;
            string raw, error;
            Assert.True(SdtItemPropertyResolver.TryResolveValue(info, requested, out typed, out raw, out error), error);
            Assert.Equal(expected, typed);
            Assert.Equal(expectedRaw, raw);
        }

        [Fact]
        public void ANonBooleanIsRefusedForABooleanProperty()
        {
            var info = new SdtPropertyInfo { NativeName = "Required", ClrType = typeof(bool) };
            object typed;
            string raw, error;
            Assert.False(SdtItemPropertyResolver.TryResolveValue(info, "maybe", out typed, out raw, out error));
            Assert.Contains("boolean", error);
        }

        [Fact]
        public void AnIntegerKeepsTheDeclaredClrTypeAndRefusesFractionsAndOverflow()
        {
            var info = new SdtPropertyInfo { NativeName = "Size", ClrType = typeof(int) };
            object typed;
            string raw, error;
            Assert.True(SdtItemPropertyResolver.TryResolveValue(info, "42", out typed, out raw, out error), error);
            Assert.IsType<int>(typed);
            Assert.Equal("42", raw);
            Assert.False(SdtItemPropertyResolver.TryResolveValue(info, "4.2", out typed, out raw, out error));
            Assert.False(SdtItemPropertyResolver.TryResolveValue(info, "99999999999", out typed, out raw, out error));
        }

        [Fact]
        public void ADecimalIsParsedWithTheInvariantCulture()
        {
            var info = new SdtPropertyInfo { NativeName = "Scale", ClrType = typeof(decimal) };
            object typed;
            string raw, error;
            Assert.True(SdtItemPropertyResolver.TryResolveValue(info, "1.5", out typed, out raw, out error), error);
            Assert.Equal(1.5m, typed);
            Assert.Equal("1.5", raw);
        }

        [Fact]
        public void ATypeThatIsNotScalarIsNotWritable()
        {
            var info = new SdtPropertyInfo { NativeName = "Opaque", ClrType = typeof(DateTime) };
            object typed;
            string raw, error;
            Assert.False(SdtItemPropertyResolver.TryResolveValue(info, "2026-01-01", out typed, out raw, out error));
            Assert.Contains("not writable", error);
        }

        [Fact]
        public void AMissingValueIsRefused()
        {
            object typed;
            string raw, error;
            Assert.False(SdtItemPropertyResolver.TryResolveValue(Json(), null, out typed, out raw, out error));
            Assert.Contains("value is required", error);
        }

        [Fact]
        public void ACLREnumerationIsWrittenAsItsEnumValue()
        {
            var info = new SdtPropertyInfo { NativeName = "Mode", ClrType = typeof(DayOfWeek) };
            info.Options.Add(new SdtPropertyOption { Raw = "Monday", Member = "Monday" });
            info.Options.Add(new SdtPropertyOption { Raw = "Friday", Member = "Friday" });
            object typed;
            string raw, error;
            Assert.True(SdtItemPropertyResolver.TryResolveValue(info, "friday", out typed, out raw, out error), error);
            Assert.Equal(DayOfWeek.Friday, typed);
            Assert.Equal("Friday", raw);
        }

        [Fact]
        public void TheCanonicalStringOfAValueIsCultureIndependent()
        {
            Assert.Equal("True", SdtItemPropertyResolver.RenderRaw(true));
            Assert.Equal("1.5", SdtItemPropertyResolver.RenderRaw(1.5m));
            Assert.Null(SdtItemPropertyResolver.RenderRaw(null));
            Assert.Equal("text", SdtItemPropertyResolver.RenderRaw("text"));
        }
    }
}
