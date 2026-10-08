using System;
using System.Linq;
using GxMcp.TestSupport;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// Six operations refuse a stale WorkWithPlus PatternInstance the same way, and
    /// each wrote the whole envelope out.
    ///
    /// What made the copies worth removing is the <c>extra</c>, not the code: its two
    /// fields are the entire retry contract, because a caller re-runs with
    /// <c>expectedVersion</c> and compares against <c>currentVersion</c>. Six
    /// hand-written copies of a contract read programmatically is six chances for one
    /// to drop a field or rename it, and the result is a caller who can do nothing
    /// but re-read the whole pattern.
    ///
    /// What was deliberately <em>not</em> merged is the comparison that triggers it.
    /// <c>add_grid_attribute</c> requires a version and refuses up front with
    /// <c>ExpectedVersionRequired</c>, so its check is unconditional; the other four
    /// treat an absent version as "no check asked for". That is a stricter contract,
    /// not an oversight, and it is pinned here so a future edit does not unify them.
    ///
    /// The read-resolve-compare prologue around these calls also stays inline in each
    /// partial, by the decision recorded in <c>WwpActionService.Grid.cs</c> - it is a
    /// declaration block whose locals are read through ~40 mutation lines. These
    /// assert that decision still holds, so it is not undone by accident either.
    ///
    /// The six operations are listed as plain pairs and iterated rather than passed
    /// through a theory: the cases are six near-identical instances of one contract,
    /// so the useful failure text is "which file", and a loop can put that in the
    /// assertion message directly.
    /// </summary>
    public class WwpStaleObjectEnvelopeTests
    {
        /// <summary>
        /// One entry per refusing operation: the file it lives in, and the message it
        /// must keep. The messages are all different on purpose - each names what that
        /// operation did not do, which is what tells an operator which stage refused -
        /// so losing one to a shared default would make the refusal less useful than
        /// the copies were.
        /// </summary>
        private static readonly (string File, string Message)[] Operations =
        {
            ("WwpActionService.cs", "The WorkWithPlus PatternInstance changed after the caller's read; no action mutation was applied."),
            ("WwpActionService.FormActions.cs", "The WorkWithPlus PatternInstance changed after the caller's read/dry-run; no form action was changed."),
            ("WwpActionService.Grid.cs", "The PatternInstance changed after the caller's read/dry-run; no grid attribute was changed."),
            ("WwpActionService.Tables.cs", "The WorkWithPlus PatternInstance changed after the caller's read/dry-run; no table type mutation was applied."),
            ("WwpActionService.Tabs.cs", "The PatternInstance changed after the caller's read/dry-run; no tab mutation was applied."),
            ("WwpActionService.WebComponentReplacement.cs", "The PatternInstance changed after the caller's read/dry-run; no replacement was applied."),
        };

        /// <summary>
        /// The envelope carries the code, the caller's own message, the target, and
        /// the two version fields a retry needs.
        /// </summary>
        [Fact]
        public void TheRefusalCarriesTheRetryContract()
        {
            foreach (var (file, message) in Operations)
            {
                var refusal = JObject.Parse(
                    WwpActionService.BuildWwpStaleObject("MyWwp", "v1", "v2", message));
                var error = (JObject)refusal["error"];

                Assert.True(error != null, file + ": no error object");
                Assert.Equal("StaleObject", error["code"].Value<string>());
                Assert.True(message == error["message"].Value<string>(),
                    file + ": message was rewritten as [" + error["message"]?.Value<string>() + "]");
                Assert.Equal("MyWwp", refusal["target"].Value<string>());

                // The version fields sit at the envelope's TOP level, not inside
                // "error". `Err` merges its `extra:` argument into the envelope rather
                // than into the error object, so a caller reading the retry contract
                // has to look here. Asserted with the location spelled out because a
                // reader looking in the obvious place gets null and no explanation.
                Assert.Equal("v1", refusal["expectedVersion"].Value<string>());
                Assert.Equal("v2", refusal["currentVersion"].Value<string>());
                Assert.True(error["extra"] == null,
                    file + ": the versions moved inside error, which is a contract change");

                // And the envelope says nothing else, so a field added later is
                // deliberate rather than an accident in one of the copies.
                var fields = refusal.Properties().Select(p => p.Name)
                    .OrderBy(n => n, StringComparer.Ordinal).ToArray();
                Assert.True(fields.Length == 5
                    && fields[0] == "currentVersion" && fields[1] == "error"
                    && fields[2] == "expectedVersion" && fields[3] == "status"
                    && fields[4] == "target",
                    file + ": envelope fields changed to [" + string.Join(", ", fields) + "]");
            }
        }

        /// <summary>
        /// The two versions are reported as given - not normalised, not swapped, and
        /// not defaulted when absent.
        /// </summary>
        [Fact]
        public void TheVersionsAreReportedAsGivenEvenWhenAbsent()
        {
            // A caller that sent no version gets both fields present and null rather
            // than one of them missing: the refusal is still a refusal, and a client
            // reading the contract should not have to special-case its absence.
            var refusal = JObject.Parse(WwpActionService.BuildWwpStaleObject("MyWwp", null, "v2", "m"));

            // Both keys are present with a null value rather than one being missing:
            // the refusal is still a refusal, and a client reading the contract should
            // not have to special-case the absence of the caller's version.
            Assert.True(refusal.ContainsKey("expectedVersion"));
            Assert.Equal(JTokenType.Null, refusal["expectedVersion"].Type);
            Assert.Equal("v2", refusal["currentVersion"].Value<string>());

            // And with both absent the target still says what moved.
            var bare = JObject.Parse(WwpActionService.BuildWwpStaleObject("MyWwp", null, null, "m"));
            Assert.Equal("MyWwp", bare["target"].Value<string>());
        }

        /// <summary>
        /// One envelope, six callers. Asserted per file so a new caller cannot be added
        /// and an old one left inline.
        /// </summary>
        [Fact]
        public void EveryRefusingOperationRoutesThroughTheOneEnvelope()
        {
            foreach (var (file, _) in Operations)
            {
                string source = SourceAssert.NormaliseNewlines(RepoSource.WithoutComments(Read(file)));

                Assert.True(SourceAssert.Count(source, "BuildWwpStaleObject(target,") == 1, file + ": call sites");

                // The router file is the one that holds the shared envelope, so the code
                // literal legitimately still appears there; nowhere else may it.
                if (file != "WwpActionService.cs")
                {
                    Assert.True(SourceAssert.Count(source, "code: \"StaleObject\"") == 0, file + ": envelope left inline");
                }
            }

            string router = SourceAssert.NormaliseNewlines(RepoSource.WithoutComments(Read("WwpActionService.cs")));
            Assert.Equal(1, SourceAssert.Count(router, "internal static string BuildWwpStaleObject("));

            // Scoped to the helper's own body: the router file sets currentVersion in
            // two other, unrelated places, so a file-wide count would not be counting
            // this envelope at all.
            string envelope = SourceAssert.MethodBody(router, "internal static string BuildWwpStaleObject(");
            Assert.Equal(1, SourceAssert.Count(envelope, "code: \"StaleObject\""));
            Assert.Equal(1, SourceAssert.Count(envelope, "[\"expectedVersion\"] = expectedVersion,"));
            Assert.Equal(1, SourceAssert.Count(envelope, "[\"currentVersion\"] = currentVersion"));
        }

        /// <summary>
        /// The comparison that triggers the refusal stays at each call site, and the
        /// difference between grid and the rest is preserved.
        /// </summary>
        [Fact]
        public void TheTriggeringComparisonStaysPerSiteAndKeepsItsDifference()
        {
            // Grid requires a version, so it compares unconditionally. It validated
            // that up front with ExpectedVersionRequired, which is what makes the
            // unconditional compare safe rather than a bug.
            string grid = SourceAssert.NormaliseNewlines(RepoSource.WithoutComments(Read("WwpActionService.Grid.cs")));
            Assert.Equal(1, SourceAssert.Count(grid, "if (!string.Equals(expectedVersion, currentVersion, StringComparison.Ordinal))"));
            Assert.Equal(0, SourceAssert.Count(grid, "if (!string.IsNullOrWhiteSpace(expectedVersion)"));
            Assert.Equal(1, SourceAssert.Count(grid, "code: \"ExpectedVersionRequired\""));

            // The other four treat an absent version as "no check asked for".
            foreach (string file in new[]
            {
                "WwpActionService.Tables.cs",
                "WwpActionService.Tabs.cs",
                "WwpActionService.FormActions.cs",
                "WwpActionService.WebComponentReplacement.cs",
            })
            {
                string source = SourceAssert.NormaliseNewlines(RepoSource.WithoutComments(Read(file)));
                Assert.True(SourceAssert.Count(source, "if (!string.IsNullOrWhiteSpace(expectedVersion)") == 1, file);
                Assert.True(SourceAssert.Count(source, "code: \"ExpectedVersionRequired\"") == 0, file);
            }

            // And the router's own check is a third shape - a named predicate rather
            // than an inline compare - so it is pinned as such.
            string router = SourceAssert.NormaliseNewlines(RepoSource.WithoutComments(Read("WwpActionService.cs")));
            Assert.Equal(1, SourceAssert.Count(router, "if (!IsExpectedVersion(expectedVersion, versionToken))"));
        }

        /// <summary>
        /// The inline prologue stays inline, by the decision its own NOTE records.
        ///
        /// Asserted so the decision is a deliberate, visible state rather than
        /// something a future reader re-litigates from scratch - or undoes because the
        /// refusal beside it now looks shared.
        /// </summary>
        [Fact]
        public void TheReadResolveComparePrologueIsStillInlineByDecision()
        {
            foreach (string file in new[]
            {
                "WwpActionService.Grid.cs",
                "WwpActionService.Tables.cs",
                "WwpActionService.Tabs.cs",
                "WwpActionService.FormActions.cs",
                "WwpActionService.WebComponentReplacement.cs",
            })
            {
                string source = SourceAssert.NormaliseNewlines(RepoSource.WithoutComments(Read(file)));

                Assert.True(SourceAssert.Count(source, "_patterns.ReadPatternPartXml(lockedTarget, \"PatternInstance\"") == 1, file + ": read site");
                Assert.True(SourceAssert.Count(source, "lock (WriteService.AcquirePerTargetLock(target))") == 1, file + ": lock");
                int expectedRefusals = file == "WwpActionService.FormActions.cs" ? 2 : 1;
                Assert.True(SourceAssert.Count(source, "return BuildWwpInstanceNotResolvable(target);") == expectedRefusals, file + ": unresolvable refusal");
            }

            Assert.Contains("It is deliberately NOT single-sourced.", Read("WwpActionService.Grid.cs"));
            Assert.Contains("BuildWwpInstanceNotResolvable", Read("WwpActionService.cs"));
        }

        /// <summary>
        /// A resolved owner must be used, rather than captured into an unused local.
        /// Form actions retain it so the part lookup and Save use the same instance.
        /// </summary>
        [Fact]
        public void NoPathCapturesAResolvedObjectItDoesNotRead()
        {
            foreach (string file in new[]
            {
                "WwpActionService.Grid.cs",
                "WwpActionService.Tables.cs",
                "WwpActionService.Tabs.cs",
                "WwpActionService.FormActions.cs",
                "WwpActionService.WebComponentReplacement.cs",
            })
            {
                string source = SourceAssert.NormaliseNewlines(RepoSource.WithoutComments(Read(file)));
                Assert.True(source.IndexOf("resolvedCurrentObject", StringComparison.Ordinal) < 0, file + ": unused local is back");
                string ownerOutput = file == "WwpActionService.FormActions.cs"
                    ? "out currentInstance, out KBObjectPart currentPart)" : "out _, out KBObjectPart currentPart)";
                Assert.True(SourceAssert.Count(source, ownerOutput) == 1, file + ": envelope owner");
            }
        }

        [Theory]
        [InlineData("WwpActionService.FormActions.cs", "private string RunFormUserActionOperation(")]
        [InlineData("WwpActionService.AddGrid.cs", "private string RunAddGridOperation(")]
        public void PartLookupAndSaveRetainTheSameResolvedOwner(string file, string method)
        {
            string source = SourceAssert.NormaliseNewlines(RepoSource.WithoutComments(Read(file)));
            string body = SourceAssert.MethodBody(source, method);
            int read = body.IndexOf("_patterns.ReadPatternPartXml(lockedTarget,", StringComparison.Ordinal);
            int guard = body.IndexOf("if (currentInstance == null || string.IsNullOrWhiteSpace(currentXml))", StringComparison.Ordinal);
            int lookup = body.IndexOf("_patterns.BuildPatternPartEnvelope(currentInstance,", StringComparison.Ordinal);
            Assert.True(read >= 0 && guard > read && lookup > guard, file + ": resolved owner must be guarded before part lookup");
            Assert.Contains("out currentInstance, out KBObjectPart currentPart)", body);
            Assert.Contains("SaveNativePattern(currentInstance, currentPart)", body);
        }

        private static string Read(string file)
        {
            return RepoSource.Read("src", "GxMcp.Worker", "Services", file);
        }

        /// <summary>
        /// The text of one member, from its declaration to its closing brace.
        /// </summary>

    }
}
