using Xunit;
using GxMcp.Worker.Services;

namespace GxMcp.Worker.Tests
{
    // issue #117 — Domain assignment on Attributes/Domains.
    // Setting Domain / DomainBasedOn / BasedOn routes to DomainBasedOn rather than
    // a scalar string in the property bag.
    public class DomainPropertyMappingTests
    {
        [Theory]
        [InlineData("Domain", true)]
        [InlineData("domain", true)]
        [InlineData("DomainBasedOn", true)]
        [InlineData("domainbasedon", true)]
        [InlineData("BasedOn", true)]
        [InlineData("basedon", true)]
        [InlineData("DomainDefinition", true)]
        // The SDK name of an Attribute's "Based on" property; without it the write fell
        // into the scalar setter and reported PropertyApplied without persisting.
        [InlineData("idBasedOn", true)]
        [InlineData("IDBASEDON", true)]
        [InlineData(" idBasedOn ", true)]
        [InlineData("Description", false)]
        [InlineData("Type", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void IsDomainPropertyName_RecognizesAliases(string name, bool expected)
        {
            Assert.Equal(expected, PropertyService.IsDomainPropertyName(name));
        }

        [Theory]
        [InlineData("Domain", "DomainBasedOn", true)]
        [InlineData("BasedOn", "Domain", true)]
        [InlineData("DomainBasedOn", "DomainBasedOn", true)]
        // idBasedOn carries the opaque BasedOnReference type name, so it never stands in
        // for (or is stood in by) the domain-name entry on reads.
        [InlineData("Domain", "idBasedOn", false)]
        [InlineData("idBasedOn", "DomainBasedOn", false)]
        [InlineData("Domain", "Description", false)]
        public void IsDomainReadAlias_ExcludesIdBasedOn(string requested, string candidate, bool expected)
        {
            Assert.Equal(expected, PropertyService.IsDomainReadAlias(requested, candidate));
        }

        [Theory]
        [InlineData("SampleDomain", "SampleDomain")]
        [InlineData("Domain:SampleDomain", "SampleDomain")]
        [InlineData("domain:SampleDomain", "SampleDomain")]
        [InlineData("  Domain: SampleDomain ", "SampleDomain")]
        // Other qualifiers are preserved so they fail as DomainNotFound rather than
        // resolving to a different object.
        [InlineData("Attribute:SampleAttribute", "Attribute:SampleAttribute")]
        [InlineData("", "")]
        [InlineData(null, null)]
        public void StripDomainQualifier_RemovesOnlyDomainPrefix(string value, string expected)
        {
            Assert.Equal(expected, PropertyService.StripDomainQualifier(value));
        }

        // Source-shape guard: the write and the post-save check need the live SDK, so
        // what a unit test can pin is that both sides normalize the qualifier (write
        // without it cannot resolve; verify without it reports a false
        // PropertyNotPersisted) and that both get loops use the idBasedOn-excluding alias.
        [Fact]
        public void DomainQualifierAndReadAlias_AreWiredIntoWriteVerifyAndGet()
        {
            string src = GxMcp.TestSupport.RepoSource.Read("src", "GxMcp.Worker", "Services", "PropertyService.cs");

            Assert.Contains("new Artech.Architecture.Common.Objects.QualifiedName(StripDomainQualifier(rawValue).Trim())", src);
            Assert.Contains("requested = StripDomainQualifier(requested);", src);
            // Both get paths read through FindListedItem, which applies the idBasedOn-excluding alias once for every name.
            Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(src, @"IsDomainReadAlias\(requested, n\)").Count);
            Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(src, @"FindListedItem\(props, (targetPropName|req), officialAliases\)").Count);
            Assert.DoesNotContain("IsDomainPropertyName(targetPropName) && IsDomainPropertyName(n)", src);
            Assert.DoesNotContain("IsDomainPropertyName(req) && IsDomainPropertyName(n)", src);
        }
    }
}
