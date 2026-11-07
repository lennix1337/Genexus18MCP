using System;
using System.Collections.Generic;
using System.Linq;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// EnumValues of a Domain through genexus_properties. <c>get</c> lists the property as writable; the write used to answer
    /// PropertyNotFound because the SDK keeps the enumerated values in the property bag, not among the descriptors. The adapter
    /// reads, validates, applies and verifies one canonical JSON list. The doubles stand for a Domain that the SDK would give.
    /// </summary>
    public class DomainEnumValuesPropertyTests
    {
        public sealed class EnumValueDouble
        {
            public string Name { get; set; }
            public string Value { get; set; }
            public string Description { get; set; }
        }

        public sealed class PropertyDouble
        {
            public string Name { get; set; }
        }

        // A Domain with its enumerated values listed the way the SDK lists them: a collection on the object itself.
        public sealed class EnumeratedDomainDouble : IDomainEnumValuesTarget
        {
            public List<EnumValueDouble> EnumValues { get; } = new List<EnumValueDouble>();
            public List<PropertyDouble> Properties { get; } = new List<PropertyDouble> { new PropertyDouble { Name = "Description" } };
            public bool Dirty { get; set; }
        }

        // An object that is not a Domain and still lists enumerated values (an Attribute, a Variable).
        public sealed class NotADomainDouble
        {
            public List<EnumValueDouble> EnumValues { get; } = new List<EnumValueDouble> { new EnumValueDouble { Name = "Active", Value = "A", Description = "Ativo" } };
            public List<PropertyDouble> Properties { get; } = new List<PropertyDouble> { new PropertyDouble { Name = "Description" } };
        }

        private static EnumeratedDomainDouble Domain(params (string name, string value, string description)[] values)
        {
            var domain = new EnumeratedDomainDouble();
            foreach (var v in values)
                domain.EnumValues.Add(new EnumValueDouble { Name = v.name, Value = v.value, Description = v.description });
            return domain;
        }

        private const string TwoValues =
            "[{\"name\":\"Active\",\"value\":\"A\",\"description\":\"Situação ativa\"},{\"name\":\"Blocked\",\"value\":\"B\",\"description\":\"Situação bloqueada\"}]";

        // ---------- which property and which object ----------

        [Theory]
        [InlineData("EnumValues", true)]
        [InlineData("enumvalues", true)]
        [InlineData("  EnumValues  ", true)]
        [InlineData("Values", false)]
        [InlineData("ControlValues", false)]
        [InlineData("Description", false)]
        [InlineData(null, false)]
        public void IsName_IsTheEnumValuesPropertyOnly(string name, bool expected)
            => Assert.Equal(expected, DomainEnumValuesProperty.IsName(name));

        [Fact]
        public void Targets_TheEnumValuesOfADomain_WithOrWithoutValues()
        {
            Assert.True(DomainEnumValuesProperty.Targets(Domain(("Active", "A", "Ativo")), "EnumValues"));
            Assert.True(DomainEnumValuesProperty.Targets(new EnumeratedDomainDouble(), "EnumValues"));
        }

        [Fact]
        public void Targets_NotAnotherPropertyAndNotAnObjectThatIsNotADomain()
        {
            Assert.False(DomainEnumValuesProperty.Targets(Domain(("Active", "A", "Ativo")), "Description"));
            Assert.False(DomainEnumValuesProperty.Targets(new NotADomainDouble(), "EnumValues"));
            Assert.False(DomainEnumValuesProperty.Targets(new object(), "EnumValues"));
        }

        [Fact]
        public void Applies_ADomainThatListsEnumeratedValues()
            => Assert.True(DomainEnumValuesProperty.Applies(Domain(("Active", "A", "Ativo")), "EnumValues"));

        [Fact]
        public void Applies_NotToADomainWithoutEnumeratedValues()
            => Assert.False(DomainEnumValuesProperty.Applies(new EnumeratedDomainDouble(), "EnumValues"));

        [Fact]
        public void Applies_NotToAnObjectThatIsNotADomain()
            => Assert.False(DomainEnumValuesProperty.Applies(new NotADomainDouble(), "EnumValues"));

        [Fact]
        public void Applies_NotToAnotherPropertyOfTheDomain()
            => Assert.False(DomainEnumValuesProperty.Applies(Domain(("Active", "A", "Ativo")), "Description"));

        // ---------- reading ----------

        [Fact]
        public void Read_IsTheCanonicalJsonListOfTheStoredValues()
        {
            string text = DomainEnumValuesProperty.Read(Domain(("Active", "A", "Situação ativa"), ("Blocked", "B", "Situação bloqueada")));
            Assert.Equal(TwoValues, text);
        }

        [Fact]
        public void Read_IsAnEmptyListWhenTheDomainHoldsNone()
        {
            // A list the SDK emptied must read as a list, so the check after the save sees a mismatch and not an unreadable value.
            Assert.Equal("[]", DomainEnumValuesProperty.Read(new EnumeratedDomainDouble()));
        }

        [Fact]
        public void Read_IsNullForAnObjectThatIsNotADomain()
        {
            Assert.Null(DomainEnumValuesProperty.Read(new NotADomainDouble()));
            Assert.Null(DomainEnumValuesProperty.Read(new object()));
        }

        [Fact]
        public void ReadPropertyString_ReadsTheEnumeratedValuesOfADomain()
        {
            // The batch write reads the previous value, the verification reads the saved one and the rollback restores the
            // previous one, all through this read: without the Domain branch the generic lookup finds no descriptor.
            string text = PropertyService.ReadPropertyString(Domain(("Active", "A", "Situação ativa"), ("Blocked", "B", "Situação bloqueada")), "EnumValues");
            Assert.Equal(TwoValues, text);
        }

        [Fact]
        public void ReadPropertyString_ADomainEmptiedBySdkReadsAsAnEmptyList()
            => Assert.Equal("[]", PropertyService.ReadPropertyString(new EnumeratedDomainDouble(), "EnumValues"));

        [Fact]
        public void SerializeSelectedProperties_TheRecoveryReadFindsTheEnumeratedValues()
        {
            // reconcileTimedOutWrite names the properties it must confirm; a name it cannot find leaves the write fence in place.
            var result = PropertyService.SerializeSelectedProperties(Domain(("Active", "A", "Situação ativa"), ("Blocked", "B", "Situação bloqueada")), null, new[] { "EnumValues" });

            var item = Assert.Single((Newtonsoft.Json.Linq.JArray)result["properties"]);
            Assert.Equal("EnumValues", item["name"].ToString());
            Assert.Equal(TwoValues, item["value"].ToString());
            Assert.False((bool)item["readOnly"]);
        }

        [Fact]
        public void SerializeSelectedProperties_AnObjectThatIsNotADomainStillHasNoSuchEntry()
        {
            var result = PropertyService.SerializeSelectedProperties(new NotADomainDouble(), null, new[] { "EnumValues" });
            Assert.Empty((Newtonsoft.Json.Linq.JArray)result["properties"]);
        }

        // ---------- refusing a list ----------

        [Theory]
        [InlineData("", "a JSON list")]
        [InlineData("   ", "a JSON list")]
        [InlineData("not json", "not valid JSON")]
        [InlineData("[] x", "not valid JSON")]
        [InlineData("[{\"name\":\"A\",\"value\":\"1\"}] []", "not valid JSON")]
        [InlineData("{\"name\":\"A\"}", "a JSON list is required")]
        [InlineData("\"A\"", "a JSON list is required")]
        [InlineData("[]", "the list is empty")]
        [InlineData("[1]", "item 1 is not an object")]
        [InlineData("[{\"value\":\"A\"}]", "item 1 has no name")]
        [InlineData("[{\"name\":\"\",\"value\":\"A\"}]", "item 1 has no name")]
        [InlineData("[{\"name\":{\"x\":1},\"value\":\"A\"}]", "item 1 has no name")]
        // The SDK keeps the previous list for a name that is not an identifier: a space, a hyphen, a dot, an accented letter,
        // a leading digit or a leading underscore.
        [InlineData("[{\"name\":\"Active One\",\"value\":\"A\"}]", "holds only letters, digits and underscores")]
        [InlineData("[{\"name\":\"Active-1\",\"value\":\"A\"}]", "holds only letters, digits and underscores")]
        [InlineData("[{\"name\":\"Active.1\",\"value\":\"A\"}]", "holds only letters, digits and underscores")]
        [InlineData("[{\"name\":\"Ação\",\"value\":\"A\"}]", "holds only letters, digits and underscores")]
        [InlineData("[{\"name\":\"1Active\",\"value\":\"A\"}]", "starts with a letter")]
        [InlineData("[{\"name\":\"_Active\",\"value\":\"A\"}]", "starts with a letter")]
        [InlineData("[{\"name\":\"Active\"}]", "item 1 ('Active') has no value")]
        [InlineData("[{\"name\":\"Active\",\"value\":null}]", "item 1 ('Active') has no value")]
        [InlineData("[{\"name\":\"Active\",\"value\":[\"A\"]}]", "item 1 ('Active') has no value")]
        [InlineData("[{\"name\":\"Active\",\"value\":\"A\"},{\"name\":\"Active\",\"value\":\"B\"}]", "the name 'Active' appears more than once")]
        // The SDK compares names and descriptions without regard to case and drops the list when two match.
        [InlineData("[{\"name\":\"Active\",\"value\":\"A\"},{\"name\":\"active\",\"value\":\"B\"}]", "the name 'active' appears more than once")]
        [InlineData("[{\"name\":\"Active\",\"value\":\"A\"},{\"name\":\"Blocked\",\"value\":\"A\"}]", "the value 'A' appears more than once")]
        [InlineData("[{\"name\":\"Active\",\"value\":\"A\",\"description\":\"Same\"},{\"name\":\"Blocked\",\"value\":\"B\",\"description\":\"Same\"}]", "the description 'Same' appears more than once")]
        [InlineData("[{\"name\":\"Active\",\"value\":\"A\",\"description\":\"Same\"},{\"name\":\"Blocked\",\"value\":\"B\",\"description\":\"same\"}]", "the description 'same' appears more than once")]
        // An empty description takes the name; another item already described with that name collides with it.
        [InlineData("[{\"name\":\"Active\",\"value\":\"A\"},{\"name\":\"Blocked\",\"value\":\"B\",\"description\":\"Active\"}]", "the description 'Active' appears more than once")]
        public void Parse_RefusesAListTheSdkWouldDrop(string raw, string reason)
        {
            string refusal = DomainEnumValuesProperty.Parse(raw, out var specs);

            Assert.NotNull(refusal);
            Assert.Null(specs);
            Assert.StartsWith("InvalidPropertyValue: EnumValues: ", refusal);
            Assert.Contains(reason, refusal);
            Assert.Equal("InvalidPropertyValue", PropertyService.ValidationErrorCode(refusal));
        }

        [Fact]
        public void Parse_TakesTheListAsItIs_AndDefaultsAnEmptyDescriptionToTheName()
        {
            string refusal = DomainEnumValuesProperty.Parse(
                "[{\"name\":\"Active\",\"value\":\"A\",\"description\":\"Situação ativa\"},{\"name\":\"Blocked\",\"value\":1,\"description\":\"\"},{\"name\":\"Late\",\"value\":\"L\"}]",
                out var specs);

            Assert.Null(refusal);
            Assert.Equal(3, specs.Count);
            Assert.Equal("Situação ativa", specs[0].Description);
            Assert.Equal("1", specs[1].Value);
            Assert.Equal("Blocked", specs[1].Description);
            Assert.Equal("Late", specs[2].Description);
        }

        [Fact]
        public void Parse_AnIdentifierNameIsAccepted()
        {
            Assert.Null(DomainEnumValuesProperty.Parse("[{\"name\":\"Active_1\",\"value\":\"A\"},{\"name\":\"A2\",\"value\":\"B\"},{\"name\":\"" + new string('N', 60) + "\",\"value\":\"C\"}]", out var specs));
            Assert.Equal(3, specs.Count);
        }

        [Fact]
        public void Parse_ValuesThatDifferOnlyInCaseAreDistinct()
        {
            // Unlike names and descriptions, the SDK keeps two values that differ only in case.
            Assert.Null(DomainEnumValuesProperty.Parse("[{\"name\":\"Lower\",\"value\":\"a\"},{\"name\":\"Upper\",\"value\":\"A\"}]", out var specs));
            Assert.Equal(2, specs.Count);
        }

        [Fact]
        public void Parse_ATextThatLooksLikeADateStaysTheText()
        {
            // The default JSON reader turns "2026-01-01T10:00:00" into a date and writes it back in the machine's format.
            Assert.Null(DomainEnumValuesProperty.Parse(
                "[{\"name\":\"Start\",\"value\":\"2026-01-01T10:00:00\",\"description\":\"2026-01-01T10:00:00Z\"}]", out var specs));
            Assert.Equal("2026-01-01T10:00:00", specs[0].Value);
            Assert.Equal("2026-01-01T10:00:00Z", specs[0].Description);
        }

        [Fact]
        public void Parse_ANumberKeepsItsDigits()
        {
            Assert.Null(DomainEnumValuesProperty.Parse(
                "[{\"name\":\"Rate\",\"value\":1.50,\"description\":\"Rate\"},{\"name\":\"Big\",\"value\":12345678901234567890,\"description\":\"Big\"},{\"name\":\"One\",\"value\":1,\"description\":\"One\"}]", out var specs));
            Assert.Equal("1.50", specs[0].Value);
            Assert.Equal("12345678901234567890", specs[1].Value);
            Assert.Equal("1", specs[2].Value);
        }

        // ---------- the one canonical text ----------

        [Fact]
        public void Canonical_OfARequest_IsWhatReadReturnsForTheSameValues()
        {
            // The verification compares the request with what the SDK holds after the save: both sides must be the same text.
            var domain = Domain(("Active", "A", "Situação ativa"), ("Blocked", "B", "Situação bloqueada"));
            Assert.Null(DomainEnumValuesProperty.Parse(TwoValues, out var specs));

            Assert.Equal(DomainEnumValuesProperty.Read(domain), DomainEnumValuesProperty.Canonical(specs));
        }

        [Fact]
        public void Matches_IgnoresFormattingAndAnOmittedDescription()
        {
            string pretty = "[\n  { \"name\": \"Active\", \"value\": \"A\" },\n  { \"name\": \"Blocked\", \"value\": \"B\", \"description\": \"Bloqueado\" }\n]";
            string persisted = "[{\"name\":\"Active\",\"value\":\"A\",\"description\":\"Active\"},{\"name\":\"Blocked\",\"value\":\"B\",\"description\":\"Bloqueado\"}]";

            Assert.True(DomainEnumValuesProperty.Matches(pretty, persisted));
        }

        [Fact]
        public void Matches_ACaseOnlyChangeOfADescriptionIsAMismatch()
        {
            // The comparison of the other properties is case-insensitive; a Domain description fixed from "Estado de conservação"
            // to "Estado de Conservação" must not be reported as saved when the SDK kept the old text.
            string requested = "[{\"name\":\"Home\",\"value\":\"H\",\"description\":\"Estado de Conservação\"}]";
            string persisted = "[{\"name\":\"Home\",\"value\":\"H\",\"description\":\"Estado de conservação\"}]";

            Assert.False(DomainEnumValuesProperty.Matches(requested, persisted));
        }

        [Fact]
        public void Matches_ADroppedValueIsAMismatch()
        {
            string persisted = "[{\"name\":\"Active\",\"value\":\"A\",\"description\":\"Situação ativa\"}]";
            Assert.False(DomainEnumValuesProperty.Matches(TwoValues, persisted));
        }

        [Fact]
        public void Matches_AListTheSdkEmptiedIsAMismatch()
            => Assert.False(DomainEnumValuesProperty.Matches(TwoValues, "[]"));

        [Fact]
        public void Matches_ARequestThatCouldNotBeWrittenNeverMatches()
            => Assert.False(DomainEnumValuesProperty.Matches("not json", "[]"));

        [Fact]
        public void PersistedValueMatches_TheEnumeratedValuesAreComparedExactly_TheOtherPropertiesWithoutCase()
        {
            string requested = "[{\"name\":\"Home\",\"value\":\"H\",\"description\":\"Estado de Conservação\"}]";
            string persisted = "[{\"name\":\"Home\",\"value\":\"H\",\"description\":\"Estado de conservação\"}]";

            Assert.False(PropertyService.PersistedValueMatches("EnumValues", true, requested, persisted));
            Assert.True(PropertyService.PersistedValueMatches("Description", false, "Estado de Conservação", "Estado de conservação"));
        }

        // ---------- the property paths ----------

        [Fact]
        public void ValidatePropertyWrite_ADomainListIsValidatedAsAList()
        {
            Assert.Null(PropertyService.ValidatePropertyWrite(Domain(("Active", "A", "Ativo")), "EnumValues", TwoValues));

            string refusal = PropertyService.ValidatePropertyWrite(Domain(("Active", "A", "Ativo")), "EnumValues", "not json");
            Assert.NotNull(refusal);
            Assert.Equal("InvalidPropertyValue", PropertyService.ValidationErrorCode(refusal));
        }

        [Fact]
        public void ValidatePropertyWrite_ADomainWithoutEnumeratedValuesStillAnswersPropertyNotFound()
        {
            string refusal = PropertyService.ValidatePropertyWrite(new EnumeratedDomainDouble(), "EnumValues", TwoValues);

            Assert.NotNull(refusal);
            Assert.Equal("PropertyNotFound", PropertyService.ValidationErrorCode(refusal));
        }

        [Fact]
        public void ValidatePropertyWrite_AnObjectThatIsNotADomainStillAnswersPropertyNotFound()
        {
            string refusal = PropertyService.ValidatePropertyWrite(new NotADomainDouble(), "EnumValues", TwoValues);

            Assert.NotNull(refusal);
            Assert.Equal("PropertyNotFound", PropertyService.ValidationErrorCode(refusal));
        }

        [Fact]
        public void ApplyPropertyValue_AnInvalidListIsRefusedBeforeAnythingIsWritten()
        {
            var domain = Domain(("Active", "A", "Ativo"));

            var ex = Assert.Throws<InvalidOperationException>(() => PropertyService.ApplyPropertyValue(domain, "EnumValues", "[]", null, null));

            Assert.StartsWith("InvalidPropertyValue: EnumValues: ", ex.Message);
            Assert.False(domain.Dirty);
            Assert.Single(domain.EnumValues);
        }

        [Fact]
        public void ApplyPropertyValue_GoesThroughTheAdapter_EvenForADomainTheSdkEmptied()
        {
            // The rollback of a write puts the previous list back through this path; the Domain may hold none by then. The
            // generic path would push the JSON text into the property bag as a plain value.
            var domain = new EnumeratedDomainDouble();

            var ex = Assert.Throws<InvalidOperationException>(() => PropertyService.ApplyPropertyValue(domain, "EnumValues", TwoValues, null, null));

            Assert.StartsWith("Could not write EnumValues", ex.Message);
            Assert.False(domain.Dirty);
        }

        [Fact]
        public void ApplyPropertyValue_ForAControl_TheAdapterIsNotUsed()
        {
            var domain = Domain(("Active", "A", "Ativo"));

            var ex = Assert.ThrowsAny<Exception>(() => PropertyService.ApplyPropertyValue(domain, "EnumValues", TwoValues, "SomeControl", null));

            Assert.DoesNotContain("Could not write EnumValues", ex.Message);
        }

        // ---------- applying ----------

        [Fact]
        public void Apply_HandsTheParsedValuesToTheSdkHelper_AndMarksTheObjectChanged()
        {
            var domain = Domain(("Active", "A", "Ativo"));
            IList<DomainEnumValueSpec> received = null;

            DomainEnumValuesProperty.Apply(domain, TwoValues, (container, specs) => { received = specs; return specs.Count; });

            Assert.True(domain.Dirty);
            Assert.Equal(new[] { "Active", "Blocked" }, received.Select(s => s.Name));
            Assert.Equal(new[] { "A", "B" }, received.Select(s => s.Value));
            Assert.Equal(new[] { "Situação ativa", "Situação bloqueada" }, received.Select(s => s.Description));
        }

        [Fact]
        public void Apply_TheSdkBuildsFewerValuesThanSent_Fails()
        {
            var domain = Domain(("Active", "A", "Ativo"));

            var ex = Assert.Throws<InvalidOperationException>(() => DomainEnumValuesProperty.Apply(domain, TwoValues, (container, specs) => 1));

            Assert.Contains("built 1 of 2", ex.Message);
            Assert.False(domain.Dirty);
        }

        [Fact]
        public void Apply_TheSdkHelperIsNotResolvable_Fails()
        {
            var domain = Domain(("Active", "A", "Ativo"));

            var ex = Assert.Throws<InvalidOperationException>(() => DomainEnumValuesProperty.Apply(domain, TwoValues, (container, specs) => -1));

            Assert.Contains("not resolvable", ex.Message);
            Assert.False(domain.Dirty);
        }

        [Fact]
        public void Apply_ARefusedListNeverReachesTheSdkHelper()
        {
            bool called = false;

            Assert.Throws<InvalidOperationException>(() => DomainEnumValuesProperty.Apply(Domain(("Active", "A", "Ativo")), "[]", (container, specs) => { called = true; return 0; }));

            Assert.False(called);
        }

        [Fact]
        public void Apply_OnAnObjectThatIsNotADomain_IsRefused()
        {
            var ex = Assert.Throws<InvalidOperationException>(() => DomainEnumValuesProperty.Apply(new NotADomainDouble(), TwoValues, (container, specs) => specs.Count));
            Assert.Contains("only be written on a Domain", ex.Message);
        }
    }
}
