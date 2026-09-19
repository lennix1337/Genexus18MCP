using Newtonsoft.Json.Linq;
using Xunit;
using Services = GxMcp.Worker.Services;

namespace GxMcp.Worker.Tests
{
    public class PatchTextEditorTests
    {
        [Fact]
        public void TryReplace_ExactSingleMatch_ReplacesRequestedBlock()
        {
            string result = Services.PatchTextEditor.TryReplace(
                new[] { "before", "old one", "old two", "after" },
                new[] { "old one", "old two" },
                "new one\nnew two",
                1,
                out string status,
                out _,
                out int count);

            Assert.Equal("Applied", status);
            Assert.Equal(1, count);
            Assert.Equal("before\nnew one\nnew two\nafter", result);
        }

        [Fact]
        public void TryReplace_CompletePartWithEmptyContent_ReturnsEmptyResult()
        {
            string result = Services.PatchTextEditor.TryReplace(
                new[] { "old" }, new[] { "old" }, string.Empty, 1,
                out string status, out _, out int count);

            Assert.Equal("Applied", status);
            Assert.Equal(1, count);
            Assert.Equal(string.Empty, result);
        }

        [Fact]
        public void TryReplace_AmbiguousExactMatch_DoesNotProduceContent()
        {
            string result = Services.PatchTextEditor.TryReplace(
                new[] { "same", "same" }, new[] { "same" }, "new", 1,
                out string status, out string details, out int count);

            Assert.Equal("Ambiguous", status);
            Assert.Equal(2, count);
            Assert.Equal(string.Empty, result);
            Assert.Contains("replaceAll=true", details);
        }

        [Fact]
        public void TryReplace_ReplaceAll_ReplacesEveryExactOccurrence()
        {
            string result = Services.PatchTextEditor.TryReplace(
                new[] { "same", "middle", "same" }, new[] { "same" }, "new", 1,
                out string status, out _, out int count, replaceAll: true);

            Assert.Equal("Applied", status);
            Assert.Equal(2, count);
            Assert.Equal("new\nmiddle\nnew", result);
        }

        [Fact]
        public void TryReplace_FuzzyWhitespaceMatch_PreservesSurroundingLines()
        {
            string result = Services.PatchTextEditor.TryReplace(
                new[] { "before", "  if   value", "  endif", "after" },
                new[] { "if value", "endif" },
                "replacement",
                1,
                out string status,
                out _,
                out int count);

            Assert.Equal("Applied", status);
            Assert.Equal(1, count);
            Assert.Equal("before\nreplacement\nafter", result);
        }

        [Fact]
        public void TryInsertAfter_ExactAnchor_InsertsAfterCompleteBlock()
        {
            string result = Services.PatchTextEditor.TryInsertAfter(
                new[] { "one", "anchor", "two" }, new[] { "anchor" }, "inserted", 1,
                out string status, out _, out int count);

            Assert.Equal("Applied", status);
            Assert.Equal(1, count);
            Assert.Equal("one\nanchor\ninserted\ntwo", result);
        }

        [Fact]
        public void FindNearMatches_ReturnsHighestSimilarityFirst()
        {
            var matches = Services.PatchTextEditor.FindNearMatches(
                new[] { "alpha", "beta", "noise", "alpha", "other" },
                new[] { "alpha", "beta" },
                3);

            Assert.NotEmpty(matches);
            Assert.Equal(0, matches[0].StartLine);
            Assert.Equal(1d, matches[0].Similarity);
        }

        [Fact]
        public void Receipt_EmptyReplacement_ProvesOldContextAbsent()
        {
            var payload = new JObject();
            var verification = Services.TextPersistenceVerifier.Evaluate(
                requested: string.Empty,
                persisted: string.Empty,
                requestedMode: "exact",
                partName: "Source");

            bool verified = Services.PatchPersistenceReceipt.AttachVerification(
                payload,
                verification,
                requestedReplacement: string.Empty,
                originalContext: "old",
                savedSource: string.Empty,
                persistedSource: string.Empty,
                verifyMode: "exact",
                partName: "Source",
                matchCount: 1);

            Assert.True(verified);
            Assert.Equal(0, payload["persistedMatchCount"]?.Value<int>());
            Assert.False(payload["oldContentPresent"]?.Value<bool>());
            Assert.False(payload["verification"]?["replacementPresent"] == null);
            Assert.True(payload["verification"]?["replacementPresent"]?.Value<bool>());
        }

        [Fact]
        public void Receipt_OldContextStillPresent_IsReported()
        {
            var payload = new JObject();
            var verification = Services.TextPersistenceVerifier.Evaluate(
                requested: "old",
                persisted: "old",
                requestedMode: "exact",
                partName: "Source");

            bool verified = Services.PatchPersistenceReceipt.AttachVerification(
                payload, verification, "old", "old", "old", "old", "exact", "Source", 1);

            Assert.True(verified);
            Assert.Equal(1, payload["persistedMatchCount"]?.Value<int>());
            Assert.True(payload["oldContentPresent"]?.Value<bool>());
        }

        [Fact]
        public void Receipt_MarkVerified_RemovesContradictoryLegacyDiagnostics()
        {
            var payload = new JObject
            {
                ["error"] = "legacy mismatch",
                ["mutation"] = new JObject(),
                ["verificationWarning"] = "legacy warning"
            };

            Services.PatchPersistenceReceipt.MarkVerified(payload, saved: true);

            Assert.Null(payload["error"]);
            Assert.Null(payload["mutation"]);
            Assert.Null(payload["verificationWarning"]);
            Assert.Equal("Applied", payload["code"]?.ToString());
            Assert.True(payload["saved"]?.Value<bool>());
            Assert.True(payload["verified"]?.Value<bool>());
        }

        [Fact]
        public void Receipt_MarkNotPersisted_SeparatesSaveFromVerification()
        {
            var payload = new JObject();

            Services.PatchPersistenceReceipt.MarkNotPersisted(payload, saved: true, verifyError: "different");

            Assert.Equal("WriteNotPersisted", payload["code"]?.ToString());
            Assert.False(payload["saved"]?.Value<bool>());
            Assert.True(payload["saveAttempted"]?.Value<bool>());
            Assert.False(payload["persisted"]?.Value<bool>());
            Assert.False(payload["verified"]?.Value<bool>());
            Assert.Equal("different", payload["persistedVerifyError"]?.ToString());
        }

        [Fact]
        public void Receipt_VerificationUnavailableDoesNotExposeRequestedTextAsSaved()
        {
            var payload = new JObject();
            Services.PatchPersistenceReceipt.AttachContentEvidence(
                payload, requestedSource: "new", savedSource: "new", reReadSource: null);

            Services.PatchPersistenceReceipt.MarkVerificationUnavailable(
                payload, saveAttempted: true, reason: "FreshReadUnavailable");

            Assert.Equal("WriteVerificationUnavailable", payload["code"]?.ToString());
            Assert.False(payload["saved"]?.Value<bool>());
            Assert.True(payload["saveAttempted"]?.Value<bool>());
            Assert.False(payload["persisted"]?.Value<bool>());
            Assert.Null(payload["content"]?["saved"]?.Value<string>());
            Assert.Null(payload["content"]?["reRead"]?.Value<string>());
            Assert.Equal("FreshReadUnavailable", payload["persistedVerifyError"]?.ToString());
        }

        [Fact]
        public void Receipt_RollbackIncludesHashesAndVerificationState()
        {
            var verification = Services.TextPersistenceVerifier.Evaluate("snapshot", "snapshot", "exact", "Source");

            JObject rollback = Services.PatchPersistenceReceipt.BuildRollback(true, verification, null);

            Assert.True(rollback["saved"]?.Value<bool>());
            Assert.True(rollback["verified"]?.Value<bool>());
            Assert.False(string.IsNullOrWhiteSpace(rollback["requestedHash"]?.ToString()));
            Assert.Equal(rollback["requestedHash"]?.ToString(), rollback["persistedHash"]?.ToString());
            Assert.Equal(JTokenType.Null, rollback["error"]?.Type);
        }

        // ── Issue #205: patch.scope — bounded search region ──────────────────────

        private static Services.PatchTextEditor.ScopeSlice ResolveScope(
            string[] source, string start, string end, out string code)
        {
            return Services.PatchTextEditor.ResolveScope(
                source,
                Services.PatchTextEditor.SplitAnchorLines(start),
                end == null ? null : Services.PatchTextEditor.SplitAnchorLines(end),
                out code,
                out _);
        }

        [Fact]
        public void ResolveScope_AnchorsDelimitRegionAndStayOutsideIt()
        {
            var source = new[] { "Case BancoCodigo = \"033\"", "  A", "  B", "Case BancoCodigo = \"237\"" };

            var slice = ResolveScope(source, "Case BancoCodigo = \"033\"", "Case BancoCodigo = \"237\"", out string code);

            Assert.Null(code);
            Assert.Equal(1, slice.StartLine);         // after the start anchor's last line
            Assert.Equal(3, slice.EndLineExclusive);  // before the end anchor's first line
            Assert.False(slice.EndsAtEof);
        }

        [Fact]
        public void ResolveScope_WithoutEndAnchor_RunsToEof()
        {
            var source = new[] { "anchor", "A", "B" };

            var slice = ResolveScope(source, "anchor", null, out string code);

            Assert.Null(code);
            Assert.Equal(1, slice.StartLine);
            Assert.Equal(source.Length, slice.EndLineExclusive);
            Assert.True(slice.EndsAtEof);
        }

        [Fact]
        public void ResolveScope_EmptyRegionBetweenAdjacentAnchors_IsValid()
        {
            var source = new[] { "anchor", "end" };

            var slice = ResolveScope(source, "anchor", "end", out string code);

            Assert.Null(code);
            Assert.Equal(1, slice.StartLine);
            Assert.Equal(1, slice.EndLineExclusive);
        }

        [Fact]
        public void ResolveScope_AmbiguousAnchor_IsReported()
        {
            var source = new[] { "same", "A", "same", "B" };

            var slice = ResolveScope(source, "same", null, out string code);

            Assert.Null(slice);
            Assert.Equal("ScopeAnchorAmbiguous", code);
        }

        [Fact]
        public void ResolveScope_MissingAnchor_IsReported()
        {
            var slice = ResolveScope(new[] { "A", "B" }, "nope", null, out string code);

            Assert.Null(slice);
            Assert.Equal("ScopeAnchorNotFound", code);
        }

        [Fact]
        public void ResolveScope_MidLineAnchor_IsNotComparable()
        {
            // The anchor exists as text but does not occupy complete lines.
            var slice = ResolveScope(new[] { "  Case X = 1 // Banco B", "B" }, "Case X = 1", null, out string code);

            Assert.Null(slice);
            Assert.Equal("ScopeAnchorNotComparable", code);
        }

        [Fact]
        public void ResolveScope_AnchorNotFoundWhenOnlyIndentationDiffers()
        {
            // Only CRLF/LF are normalized: a tab/space difference is NOT a match.
            var slice = ResolveScope(new[] { "\tCase X = 1", "B" }, "  Case X = 1", null, out string code);

            Assert.Null(slice);
            Assert.Equal("ScopeAnchorNotFound", code);
        }

        [Fact]
        public void ResolveScope_TrailingLineBreakIsTolerated()
        {
            // A caller pasting the anchor line out of a read output includes its terminator;
            // that terminator is not an extra anchor line.
            var slice = ResolveScope(new[] { "anchor", "A" }, "anchor\r\n", null, out string code);

            Assert.Null(code);
            Assert.Equal(1, slice.StartLine);

            var multiLine = ResolveScope(new[] { "a", "b", "c" }, "a\nb\n", null, out string multiCode);
            Assert.Null(multiCode);
            Assert.Equal(2, multiLine.StartLine);
        }

        [Fact]
        public void ResolveScope_BlankLineAnchorStillCountsAsALine()
        {
            // "a\n\n" is the line `a` followed by one EMPTY line: two anchor lines, not one.
            var slice = ResolveScope(new[] { "a", "", "tail" }, "a\n\n", null, out string code);

            Assert.Null(code);
            Assert.Equal(2, slice.StartLine);
        }

        [Fact]
        public void ResolveScope_EndAnchorIsSearchedOnlyAfterStart()
        {
            // `B` appears before the start anchor too; only the suffix occurrence counts.
            var source = new[] { "B", "start", "A", "B" };

            var slice = ResolveScope(source, "start", "B", out string code);

            Assert.Null(code);
            Assert.Equal(2, slice.StartLine);
            Assert.Equal(3, slice.EndLineExclusive);
        }

        [Fact]
        public void ReplaceWithinScope_OnlyTheScopedBranchChanges()
        {
            // Two equivalent branches; the intended change targets the third line block only.
            var source = new[]
            {
                "if Banco = \"A\"",
                "  msg(\"x\")",
                "end",
                "if Banco = \"B\"",
                "  msg(\"x\")",
                "end"
            };
            var slice = ResolveScope(source, "if Banco = \"B\"", null, out string code);
            Assert.Null(code);

            var outcome = Services.PatchTextEditor.ReplaceWithinScope(
                source, slice.StartLine, slice.EndLineExclusive, new[] { "  msg(\"x\")" }, "  msg(\"y\")", 1, false);

            Assert.Equal("Applied", outcome.Status);
            Assert.Equal(1, outcome.MatchCount);
            Assert.Single(outcome.Matches);
            Assert.Equal(4, outcome.Matches[0].StartLine);          // original line numbering
            Assert.Equal(5, outcome.Matches[0].EndLineExclusive);
            Assert.Equal(0, outcome.Matches[0].StartColumn);
            var lines = outcome.UpdatedSource.Split('\n');
            Assert.Equal("  msg(\"x\")", lines[1]);                 // Banco A untouched
            Assert.Equal("  msg(\"y\")", lines[4]);                 // Banco B changed
        }

        [Fact]
        public void ReplaceWithinScope_FindOutsideScopeDoesNotMatch()
        {
            var source = new[] { "target", "start", "other" };
            var slice = ResolveScope(source, "start", null, out _);

            var outcome = Services.PatchTextEditor.ReplaceWithinScope(
                source, slice.StartLine, slice.EndLineExclusive, new[] { "target" }, "new", 1, false);

            Assert.Equal("NoMatch", outcome.Status);
            Assert.Null(outcome.UpdatedSource);
            Assert.Empty(outcome.Matches);
        }

        [Fact]
        public void ReplaceWithinScope_ReplaceAllAppliesOnlyInsideTheScope()
        {
            var source = new[] { "same", "start", "same", "same" };
            var slice = ResolveScope(source, "start", null, out _);

            var outcome = Services.PatchTextEditor.ReplaceWithinScope(
                source, slice.StartLine, slice.EndLineExclusive, new[] { "same" }, "new", 1, true);

            Assert.Equal("Applied", outcome.Status);
            Assert.Equal(2, outcome.MatchCount);
            Assert.Equal(2, outcome.Matches.Count);
            Assert.Equal(new[] { 2, 3 }, outcome.Matches.ConvertAll(m => m.StartLine).ToArray());
            var lines = outcome.UpdatedSource.Split('\n');
            Assert.Equal("same", lines[0]);   // outside the scope
            Assert.Equal("start", lines[1]);
            Assert.Equal("new", lines[2]);
            Assert.Equal("new", lines[3]);
        }

        [Fact]
        public void ReplaceWithinScope_NormalizedStrategyReportsOriginalLines()
        {
            // The source line differs from `find` in internal whitespace, so the exact path
            // misses and the fuzzy (whitespace-normalized) strategy applies — it must report
            // the ORIGINAL line numbers, not slice-relative ones.
            var source = new[] { "start", "\tmsg(\"x\",  y)", "end" };
            var slice = ResolveScope(source, "start", "end", out _);

            var outcome = Services.PatchTextEditor.ReplaceWithinScope(
                source, slice.StartLine, slice.EndLineExclusive, new[] { "msg(\"x\", y)" }, "\tmsg(\"x\", y)", 1, false);

            Assert.Equal("Applied", outcome.Status);
            Assert.Single(outcome.Matches);
            Assert.Equal("fuzzy", outcome.Matches[0].Strategy);
            Assert.Equal(1, outcome.Matches[0].StartLine);
            Assert.Equal(2, outcome.Matches[0].EndLineExclusive);
            Assert.Equal(0, outcome.Matches[0].StartColumn);
            Assert.Equal("\tmsg(\"x\", y)", outcome.UpdatedSource.Split('\n')[1]);
            Assert.Equal("start", outcome.UpdatedSource.Split('\n')[0]);
        }

        [Fact]
        public void ReplaceWithinScope_ReportsMatchColumnForMidLineFind()
        {
            var source = new[] { "start", "\t\tmsg(\"x\");", "end" };
            var slice = ResolveScope(source, "start", "end", out _);

            var outcome = Services.PatchTextEditor.ReplaceWithinScope(
                source, slice.StartLine, slice.EndLineExclusive, new[] { "msg(\"x\");" }, "msg(\"y\");", 1, false);

            Assert.Equal("Applied", outcome.Status);
            Assert.Equal(2, outcome.Matches[0].StartColumn);
        }

        [Fact]
        public void ReplaceWithinScope_WhitespaceNormalizedStrategyReportsOriginalLines()
        {
            // The context splits its lines in a different place than the source, so no line
            // window matches fuzzy; only the whitespace-collapsed comparison does.
            var source = new[] { "start", "x y", "z", "end" };
            var slice = ResolveScope(source, "start", "end", out _);

            var outcome = Services.PatchTextEditor.ReplaceWithinScope(
                source, slice.StartLine, slice.EndLineExclusive, new[] { "x", "y z" }, "new", 1, false);

            Assert.Equal("Applied", outcome.Status);
            Assert.Equal("whitespace-normalized", outcome.Matches[0].Strategy);
            Assert.Equal(1, outcome.Matches[0].StartLine);         // original line numbering
            Assert.Equal(3, outcome.Matches[0].EndLineExclusive);
            Assert.Equal("start\nnew\nend", outcome.UpdatedSource);
        }

        [Fact]
        public void ReplaceWithinScope_NormalizedStrategyCannotEscapeTheBoundary()
        {
            // The whitespace-collapsed match exists only OUTSIDE the scope: the slice is taken
            // before every strategy runs, so the normalized path cannot reach across it.
            var source = new[] { "x y", "z", "start", "other" };
            var slice = ResolveScope(source, "start", null, out _);

            var outcome = Services.PatchTextEditor.ReplaceWithinScope(
                source, slice.StartLine, slice.EndLineExclusive, new[] { "x", "y z" }, "new", 1, false);

            Assert.Equal("NoMatch", outcome.Status);
            Assert.Null(outcome.UpdatedSource);
        }

        // Issue #206 rule 1 (validate must not change which match the current algorithms
        // select): the indentation-only path runs the whole-part slice through the same
        // pipeline, so its verdict and output must equal the unscoped path's for every
        // strategy, including the ambiguous and no-match ones.
        [Theory]
        [InlineData("before\nold one\nold two\nafter", "old one\nold two", "new one\nnew two", 1, false)]
        [InlineData("old\nold\nold", "old", "new", 3, false)]
        [InlineData("old\nold\nold", "old", "new", 1, false)]
        [InlineData("old\nold\nold", "old", "new", 1, true)]
        [InlineData("  if (x) {\n    DoOld();\n  }", "if (x) {\n\tDoOld();\n}", "new body", 1, false)]
        [InlineData("x y\nz", "x\ny z", "new", 1, false)]
        [InlineData("nothing here", "absent", "new", 1, false)]
        public void ReplaceWithinScope_WholePartSliceMatchesUnscopedBehaviour(
            string sourceText, string contextText, string content, int expectedCount, bool replaceAll)
        {
            var source = sourceText.Split('\n');
            var context = contextText.Split('\n');

            string unscoped = Services.PatchTextEditor.TryReplace(
                source, context, content, expectedCount, out string unscopedStatus, out string unscopedDetails, out int unscopedCount, replaceAll);
            var scoped = Services.PatchTextEditor.ReplaceWithinScope(
                source, 0, source.Length, context, content, expectedCount, replaceAll);

            Assert.Equal(unscopedStatus, scoped.Status);
            Assert.Equal(unscopedCount, scoped.MatchCount);
            // A failure reports an empty string unscoped and null scoped; both mean "nothing to write".
            Assert.Equal(unscoped ?? string.Empty, scoped.UpdatedSource ?? string.Empty);
            // The success detail is empty on both paths; the Ambiguous/NoMatch details differ by
            // design (the scoped text names the region), so only a non-empty check is shared.
            Assert.Equal(unscopedStatus == "Applied", string.IsNullOrEmpty(scoped.Details));
        }

        [Fact]
        public void ReplaceWithinScope_WholePartSliceMatchesUnscopedBehaviour_ExactBlock()
        {
            var source = new[] { "before", "old one", "old two", "after" };

            var outcome = Services.PatchTextEditor.ReplaceWithinScope(
                source, 0, source.Length, new[] { "old one", "old two" }, "new one\nnew two", 1, false);

            Assert.Equal("Applied", outcome.Status);
            Assert.Equal("before\nnew one\nnew two\nafter", outcome.UpdatedSource);
            Assert.Equal(1, outcome.Matches[0].StartLine);
            Assert.Equal(3, outcome.Matches[0].EndLineExclusive);
        }

        [Fact]
        public void Receipt_DivergentFreshRead_DoesNotClaimConfirmation()
        {
            var payload = new JObject();
            var verification = Services.TextPersistenceVerifier.Evaluate("new", "old", "exact", "Source");

            bool verified = Services.PatchPersistenceReceipt.AttachVerification(
                payload, verification, "new", "old", "new", "old", "exact", "Source", 1);

            Assert.False(verified);
            Assert.False(payload["reReadConfirmed"]?.Value<bool>());
            Assert.False(payload["verification"]?["reReadConfirmed"]?.Value<bool>());
            Assert.True(payload["verification"]?["readCompleted"]?.Value<bool>());
            Assert.Equal("fresh-sdk-read", payload["verification"]?["source"]?.ToString());
        }
    }
}
