using GxMcp.TestSupport;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// The public object_atomic route (genexus_create action=object_atomic). A save that only writes the properties of an existing
    /// object suppresses the pattern on save; any save that writes variables, rules or source runs as before, and so does a
    /// creation. The decision is the production rule, called here with a parsed spec.
    /// </summary>
    public class AtomicCreatePropertyOnlySaveTests
    {
        private static AtomicCreateService.ParsedSpec SpecWithProperties()
        {
            var spec = new AtomicCreateService.ParsedSpec { Name = "SynthWC", Type = "WebPanel" };
            spec.Properties["MasterPage"] = "SynthMaster";
            return spec;
        }

        [Fact]
        public void APropertyOnlySaveOfAnExistingObjectIsSuppressed()
        {
            Assert.True(AtomicCreateService.IsPropertyOnlySave(true, SpecWithProperties()));
        }

        [Fact]
        public void ACreationIsNotSuppressed()
        {
            Assert.False(AtomicCreateService.IsPropertyOnlySave(false, SpecWithProperties()));
        }

        [Fact]
        public void ASaveThatAlsoWritesVariablesRulesOrSourceIsNotSuppressed()
        {
            var withVariables = SpecWithProperties();
            withVariables.Variables.Add(new JObject { ["varName"] = "&SynthCount", ["typeName"] = "Numeric" });
            var withRules = SpecWithProperties();
            withRules.RulesText = "Event 'Start'";
            var withSource = SpecWithProperties();
            withSource.Source = "&SynthCount = 1";

            Assert.False(AtomicCreateService.IsPropertyOnlySave(true, withVariables));
            Assert.False(AtomicCreateService.IsPropertyOnlySave(true, withRules));
            Assert.False(AtomicCreateService.IsPropertyOnlySave(true, withSource));
        }

        [Fact]
        public void ASaveWithoutPropertiesIsNotSuppressed()
        {
            Assert.False(AtomicCreateService.IsPropertyOnlySave(true, new AtomicCreateService.ParsedSpec { Name = "SynthWC" }));
        }

        [Fact]
        public void TheProductionSaveDecidesThroughTheRule()
        {
            string src = RepoSource.WithoutComments(RepoSource.Read("src", "GxMcp.Worker", "Services", "AtomicCreateService.cs"));

            Assert.Equal(1, SourceAssert.Count(src, "using (IDisposable suppression = IsPropertyOnlySave(exists, spec)"));
            Assert.Equal(1, SourceAssert.Count(src, "PropertyApplyOnSaveRule.Begin(obj, null, spec.Properties.Properties().Select(p => p.Name))"));
        }
    }
}
