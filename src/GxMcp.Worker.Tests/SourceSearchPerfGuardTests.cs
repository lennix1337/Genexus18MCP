using System;
using System.Reflection;
using System.Text;
using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // PERFORMANCE (perf round 3): the search_source regex scan routes patterns
    // through one of two paths — a fast single-pass rx.Matches(src) loop, or the
    // legacy per-line Split + IsMatch loop. Correctness of the fast path depends
    // entirely on the two static guards that decide the routing:
    //   - HasLineAnchors:  ^ $ \A \Z \z force the per-line loop (they anchor per
    //     line, not per file — only the per-line loop preserves that semantic).
    //   - MayMatchAcrossLines: patterns whose atoms can match a newline could
    //     match across line boundaries, where the single-pass path would report
    //     different results than the per-line loop.
    // Both guards are deliberately conservative: a false positive only costs
    // speed (falls back to the slower per-line loop), never correctness. These
    // tests pin the routing so a future regex-atom edit can't silently change
    // search results.
    public class SourceSearchPerfGuardTests
    {
        private static bool Guard(string method, string pattern)
        {
            var m = typeof(SourceSearchService).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(m); // guard method must exist
            return (bool)m.Invoke(null, new object[] { pattern });
        }

        // ---- HasLineAnchors -------------------------------------------------

        [Theory]
        [InlineData("^parm", true)]
        [InlineData("parm$", true)]
        [InlineData("^parm$", true)]
        [InlineData("\\Aparm", true)]
        [InlineData("parm\\Z", true)]
        [InlineData("parm\\z", true)]
        [InlineData("parm", false)]
        [InlineData("\\bparm\\b", false)]
        public void HasLineAnchors_DetectsAnchors(string pattern, bool expected)
        {
            Assert.Equal(expected, Guard("HasLineAnchors", pattern));
        }

        [Fact]
        public void HasLineAnchors_EscapedCaretAndDollar_AreLiterals()
        {
            // Escaped ^ and $ are literal characters, not anchors.
            Assert.False(Guard("HasLineAnchors", "a\\^b"));
            Assert.False(Guard("HasLineAnchors", "a\\$b"));
        }

        [Fact]
        public void HasLineAnchors_CaretInsideCharClass_IsNotAnchor()
        {
            // [^a] is a negated class, not a line anchor; [a^] has a literal caret.
            Assert.False(Guard("HasLineAnchors", "[^a]"));
            Assert.False(Guard("HasLineAnchors", "[a^]b"));
        }

        // ---- MayMatchAcrossLines ---------------------------------------------

        [Theory]
        [InlineData("parm", false)]
        [InlineData("\\bparm\\b", false)]
        [InlineData("^parm$", false)]   // anchor-routed anyway; guard itself is false
        [InlineData("[a-z]+", false)]
        [InlineData("\\d+", false)]
        [InlineData("\\w+", false)]
        public void MayMatchAcrossLines_PlainPatterns_AreSingleLine(string pattern, bool expected)
        {
            Assert.Equal(expected, Guard("MayMatchAcrossLines", pattern));
        }

        [Theory]
        [InlineData("\\s+", true)]      // whitespace incl. newline
        [InlineData("a\\s*b", true)]
        [InlineData("\\n", true)]       // explicit newline escape
        [InlineData("\\r", true)]
        [InlineData("\\v", true)]
        [InlineData("\\f", true)]
        [InlineData("\\D+", true)]      // non-digit matches newline
        [InlineData("\\W+", true)]      // non-word matches newline
        [InlineData("[\\s\\S]*", true)]
        [InlineData("[\\x00-\\xff]", true)] // range covering 0x0A
        [InlineData("foo\\cJbar", true)]      // \cJ = LF control char
        [InlineData("foo\\cMbar", true)]      // \cM = CR control char
        public void MayMatchAcrossLines_NewlineCapableAtoms_Flagged(string pattern, bool expected)
        {
            Assert.Equal(expected, Guard("MayMatchAcrossLines", pattern));
        }

        [Fact]
        public void MayMatchAcrossLines_NegatedClass_Flagged()
        {
            // [^...] matches anything except the listed chars — includes newline.
            Assert.True(Guard("MayMatchAcrossLines", "[^a]"));
        }

        [Fact]
        public void MayMatchAcrossLines_InlineSingleline_Flagged()
        {
            // (?s) makes '.' match newlines.
            Assert.True(Guard("MayMatchAcrossLines", "(?s)if\\s*\\(.*\\)"));
        }

        [Theory]
        [InlineData("(?is)foo.bar")]
        [InlineData("(?si)foo.bar")]
        [InlineData("(?ms)foo.bar")]
        [InlineData("(?i-s:foo.bar)")]
        public void MayMatchAcrossLines_CombinedInlineOptions_Flagged(string pattern)
        {
            // Review nit: only (?s) at position i+2 was detected; combined option
            // groups like (?is)/(?si)/(?ms) still activate Singleline and must
            // route to the per-line loop too.
            Assert.True(Guard("MayMatchAcrossLines", pattern));
        }

        [Fact]
        public void MayMatchAcrossLines_LiteralNewlineInPattern_Flagged()
        {
            Assert.True(Guard("MayMatchAcrossLines", "foo\nbar"));
            Assert.True(Guard("MayMatchAcrossLines", "foo\r\nbar"));
        }

        [Fact]
        public void MayMatchAcrossLines_PlainEscapes_NotFlagged()
        {
            // Escapes that cannot match a newline stay on the fast path.
            Assert.False(Guard("MayMatchAcrossLines", "\\*"));
            Assert.False(Guard("MayMatchAcrossLines", "a\\.b"));
            Assert.False(Guard("MayMatchAcrossLines", "\\d{3}"));
        }

        // ---- compiled-regex cache (perf round 4) ------------------------------

        // net48 JITs a Compiled regex on EVERY construction (~15ms floor); the
        // cache must return the SAME instance for repeated (pattern, options)
        // so repeated searches skip recompilation. Different options (case
        // sensitivity) must be a different cache key.
        private static object GetCached(string pattern, bool caseSensitive)
        {
            var m = typeof(SourceSearchService).GetMethod("GetCachedRegex", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(m);
            var opts = System.Text.RegularExpressions.RegexOptions.Compiled;
            if (!caseSensitive) opts |= System.Text.RegularExpressions.RegexOptions.IgnoreCase;
            return m.Invoke(null, new object[] { pattern, opts });
        }

        [Fact]
        public void GetCachedRegex_SamePattern_SameInstance()
        {
            var a = GetCached("parm", caseSensitive: false);
            var b = GetCached("parm", caseSensitive: false);
            Assert.Same(a, b); // no re-JIT on the second call
        }

        [Fact]
        public void GetCachedRegex_CaseSensitivity_SeparateEntries()
        {
            var a = GetCached("parm", caseSensitive: true);
            var b = GetCached("parm", caseSensitive: false);
            Assert.NotSame(a, b);
        }

        [Fact]
        public void GetCachedRegex_DistinctPatterns_SeparateEntries()
        {
            var a = GetCached("parm", caseSensitive: false);
            var b = GetCached("other", caseSensitive: false);
            Assert.NotSame(a, b);
        }

        // ---- bounded match timeout (plan 068) --------------------------------

        [Fact]
        public void GetCachedRegex_CarriesBoundedMatchTimeout()
        {
            var rx = (System.Text.RegularExpressions.Regex)GetCached("parm", caseSensitive: false);
            Assert.True(rx.MatchTimeout > TimeSpan.Zero);
            Assert.True(rx.MatchTimeout <= TimeSpan.FromSeconds(3));
        }

        [Fact]
        public void BoundedRegex_ThrowsMatchTimeout_OnCatastrophicInput()
        {
            // Plan 068: the 2s per-match timeout is what keeps a pathological LLM-supplied
            // pattern from hanging the worker's single STA thread (net48 default match
            // timeout is infinite). Pin the guarantee directly at the regex level: a
            // catastrophic pattern against a long run must throw RegexMatchTimeoutException
            // — the exact exception SearchCore's catch maps to the PatternTimeout envelope
            // (live-verified against a real KB: PatternTimeout at 2.0s, STA responsive).
            // Pattern choice: "(a|aa)+$" partitions the run into 1-or-2-char chunks
            // (Fibonacci blow-up) — pathological on every engine; "(a+)++$" is only
            // pathological on .NET Framework and "(a|a)+$" is merely quadratic.
            var rx = (System.Text.RegularExpressions.Regex)GetCached("(a|aa)+$", caseSensitive: false);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            Assert.Throws<System.Text.RegularExpressions.RegexMatchTimeoutException>(() =>
                rx.IsMatch(new string('a', 20000) + "b"));
            sw.Stop();
            // Timeout fires around the 2s bound, not after minutes of backtracking.
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15), $"regex took {sw.Elapsed}");
        }

        [Fact]
        public void FastRegexPath_EmitsOneHitPerMatchingLine()
        {
            int lastLine = -1;

            Assert.True(SourceSearchService.ShouldEmitRegexHitLine(3, ref lastLine));
            Assert.False(SourceSearchService.ShouldEmitRegexHitLine(3, ref lastLine));
            Assert.True(SourceSearchService.ShouldEmitRegexHitLine(4, ref lastLine));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ResumeCursor_RoundTripsEntrySkipAndPhase(bool metadata)
        {
            string cursor = SourceSearchService.BuildResumeCursor(7, 11, metadata);

            Assert.True(SourceSearchService.TryParseResumeCursor(
                cursor, out int entry, out int skipped, out bool parsedMetadata));
            Assert.Equal(7, entry);
            Assert.Equal(11, skipped);
            Assert.Equal(metadata, parsedMetadata);
        }

        [Fact]
        public void BoundResumeCursor_RejectsDifferentQueryScopeIndexOrKb()
        {
            string cursor = SourceSearchService.BuildResumeCursor(
                4, 2, metadata: false,
                queryFingerprint: "query-a",
                scopeFingerprint: "scope-a",
                indexGeneration: "index-a",
                kbIdentity: "kb-a");

            Assert.True(SourceSearchService.TryParseResumeCursor(
                cursor, "query-a", "scope-a", "index-a", "kb-a",
                out int entry, out int skipped, out bool metadata));
            Assert.Equal(4, entry);
            Assert.Equal(2, skipped);
            Assert.False(metadata);

            Assert.False(SourceSearchService.TryParseResumeCursor(
                cursor, "query-b", "scope-a", "index-a", "kb-a", out _, out _, out _));
            Assert.False(SourceSearchService.TryParseResumeCursor(
                cursor, "query-a", "scope-b", "index-a", "kb-a", out _, out _, out _));
            Assert.False(SourceSearchService.TryParseResumeCursor(
                cursor, "query-a", "scope-a", "index-b", "kb-a", out _, out _, out _));
            Assert.False(SourceSearchService.TryParseResumeCursor(
                cursor, "query-a", "scope-a", "index-a", "kb-b", out _, out _, out _));
        }

        [Theory]
        [InlineData("")]
        [InlineData("not-a-cursor")]
        public void ResumeCursor_RejectsMalformedTokens(string cursor)
        {
            Assert.False(SourceSearchService.TryParseResumeCursor(
                cursor, out _, out _, out _));
        }

        [Fact]
        public void ResumeCursor_RejectsUnknownPhase()
        {
            string cursor = Convert.ToBase64String(Encoding.UTF8.GetBytes(
                    "{\"v\":1,\"entry\":7,\"skip\":0,\"phase\":\"future\"}"))
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');

            Assert.False(SourceSearchService.TryParseResumeCursor(
                cursor, out _, out _, out _));
        }
    }
}
