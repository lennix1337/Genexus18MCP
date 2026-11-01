using GxMcp.TestSupport;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// An argument the caller did not send and an argument the caller sent as
    /// <c>"x": null</c> are the same request, and for a <c>JObject</c> built from JSON
    /// they are not distinguishable by a null test.
    ///
    /// A property that is absent reads back as C# <c>null</c>; a property written as
    /// explicit null reads back as a <c>JValue</c> of type <see cref="JTokenType.Null"/>,
    /// which is a live object. So <c>args["x"] != null</c> takes its "the caller
    /// supplied this" branch for an explicit null, and the test that usually follows -
    /// <c>is not JArray</c>, or <c>Type != JTokenType.Object</c> - is also true of that
    /// null. The guard then reports a malformed argument the caller never sent.
    ///
    /// Callers do send them that way: an LLM composing a call from a tool schema
    /// routinely serialises every declared property and leaves the unused ones null, so
    /// issue #331 was <c>genexus_properties action=get name=...</c> refusing every
    /// single-object read with <c>InvalidBatchTargets</c> and a message about a
    /// <c>targets</c> the request never contained. Its hint named the exact form the
    /// caller had used.
    ///
    /// These pin the four places that got it wrong, by their observable effect rather
    /// than by the guard's shape - a caller's outcome, not an implementation detail.
    /// </summary>
    public class ExplicitNullArgumentTests
    {
        private static JObject Args(string json) => JObject.Parse(json);

        [Fact]
        public void AnExplicitNullIsNotASuppliedArgument()
        {
            Assert.False(JsonUtil.IsSupplied(Args("{}")["targets"]));
            Assert.False(JsonUtil.IsSupplied(Args("{\"targets\":null}")["targets"]));

            // And the three real shapes are all supplied.
            Assert.True(JsonUtil.IsSupplied(Args("{\"targets\":[]}")["targets"]));
            Assert.True(JsonUtil.IsSupplied(Args("{\"targets\":\"Home\"}")["targets"]));
            Assert.True(JsonUtil.IsSupplied(Args("{\"name\":\"Home\"}")["name"]));

            // The C# null token is not a JToken at all, and must read as absent.
            Assert.False(JsonUtil.IsSupplied((JToken)null));
        }

        /// <summary>
        /// The predicate is the whole point, so it is pinned against its own failure
        /// mode: dropping the type test reintroduces every bug below at once.
        /// </summary>
        [Fact]
        public void TheTypeTestIsWhatSeparatesNullFromAbsent()
        {
            JToken explicitNull = Args("{\"targets\":null}")["targets"];

            // The trap, stated so the reason this predicate exists is visible: the token
            // is not null, so a null test alone cannot exclude it.
            Assert.True(explicitNull != null);
            Assert.Equal(JTokenType.Null, explicitNull.Type);
        }

        /// <summary>
        /// Issue #331: a single-object properties read with <c>targets</c> absent, or
        /// present as null, must not be read as a batch.
        /// </summary>
        [Fact]
        public void APropertiesGetWithNameOnlyIsNotABatchRequest()
        {
            foreach (string json in new[]
            {
                "{\"action\":\"get\",\"name\":\"Home\",\"type\":\"WebPanel\"}",
                "{\"action\":\"get\",\"name\":\"Home\",\"type\":\"WebPanel\",\"targets\":null}",
                "{\"action\":\"get\",\"name\":\"Home\",\"propertyName\":\"Name\",\"targets\":null}",
            })
            {
                JObject args = Args(json);
                Assert.False(JsonUtil.IsSupplied(args["targets"]));
                Assert.False(string.IsNullOrWhiteSpace(args["name"]?.ToString()));
            }
        }

        /// <summary>
        /// A real batch still is one, so the fix cannot have been to ignore the argument.
        /// </summary>
        [Fact]
        public void ARealBatchIsStillDetected()
        {
            JObject args = Args("{\"action\":\"get\",\"targets\":[{\"name\":\"Home\"}]}");

            Assert.True(JsonUtil.IsSupplied(args["targets"]));
            Assert.IsType<JArray>(args["targets"]);
            Assert.Empty(args["name"]?.ToString() ?? string.Empty);
        }

        /// <summary>
        /// <c>genexus_edit_and_build</c> used to enter its patch-normalisation branch for
        /// an explicit <c>"patch": null</c>, set <c>mode=patch</c>, and then match neither
        /// the object nor the string shape - selecting patch mode for a request carrying
        /// no patch. Exercised through the guard's own rule, since
        /// <c>EditAndBuildOrchestrator</c> needs a live Worker to run end to end.
        /// </summary>
        [Fact]
        public void AnExplicitNullPatchDoesNotSelectPatchMode()
        {
            JObject args = Args("{\"name\":\"Home\",\"patch\":null}");

            // What the old guard evaluated: true, and mode became "patch".
            // What it should evaluate: false, and the branch is not entered.
            Assert.False(JsonUtil.IsSupplied(args["patch"]));
        }

        /// <summary>
        /// The atomic-authoring plan described what the caller sent. With an explicit
        /// <c>"rules": null</c> it reported a rule set that was never provided.
        /// </summary>
        [Fact]
        public void APlanDoesNotReportAnArgumentTheCallerSentAsNull()
        {
            Assert.False(JsonUtil.IsSupplied(Args("{\"rules\":null}")["rules"]));
            Assert.False(JsonUtil.IsSupplied(Args("{}")["rules"]));
            Assert.True(JsonUtil.IsSupplied(Args("{\"rules\":[]}")["rules"]));
        }

        /// <summary>
        /// The tests above pin the predicate. On their own they would not notice if a
        /// call site went back to a bare null test - every one of them calls
        /// <c>IsSupplied</c> directly, so reverting the dispatcher or the helper leaves
        /// them all green while the bug returns. That is the shape of a guard that only
        /// looks like coverage, and it is why the four sites are pinned here too.
        ///
        /// Structural, and honestly so: it reads the production source rather than
        /// calling these paths, because the sites sit behind services needing a live
        /// Worker. What it protects is that the fixed sites stay fixed; it says nothing
        /// about their behaviour, which the tests above cover at the predicate and which
        /// a live smoke would be needed to cover end to end.
        /// </summary>
        [Fact]
        public void EachFixedCallSiteStillUsesThePredicate()
        {
            string dispatcher = RepoSource.WithoutComments(RepoSource.Read("src", "GxMcp.Worker", "Services", "CommandDispatcher.cs"));
            string theme = RepoSource.WithoutComments(RepoSource.Read("src", "GxMcp.Worker", "Helpers", "ThemeStyleEditHelper.cs"));
            string editBuild = RepoSource.WithoutComments(RepoSource.Read("src", "GxMcp.Worker", "Services", "EditAndBuildOrchestrator.cs"));
            string atomic = RepoSource.WithoutComments(RepoSource.Read("src", "GxMcp.Worker", "Services", "AtomicAuthoringService.cs"));

            // The fixed form is present at each site.
            Assert.Equal(1, SourceAssert.Count(dispatcher, "JsonUtil.IsSupplied(args?[\"targets\"])"));
            Assert.Equal(1, SourceAssert.Count(theme, "JsonUtil.IsSupplied(request[\"properties\"])"));
            Assert.Equal(1, SourceAssert.Count(editBuild, "JsonUtil.IsSupplied(args[\"patch\"])"));
            Assert.Equal(1, SourceAssert.Count(atomic, "JsonUtil.IsSupplied(args?[\"rules\"])"));
            Assert.Equal(1, SourceAssert.Count(atomic, "JsonUtil.IsSupplied(args?[\"source\"])"));

            // And the form that caused the reports is gone from each of them.
            Assert.DoesNotContain("args?[\"targets\"] != null", dispatcher);
            Assert.DoesNotContain("request[\"properties\"] != null", theme);
            Assert.DoesNotContain("args[\"patch\"] != null", editBuild);
            Assert.DoesNotContain("args?[\"rules\"] != null", atomic);
            Assert.DoesNotContain("args?[\"source\"] != null", atomic);
        }
    }
}
