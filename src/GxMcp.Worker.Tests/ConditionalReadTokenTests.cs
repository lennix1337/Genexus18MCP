using System;
using System.Collections.Generic;
using GxMcp.Worker.Helpers;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// Issue #357 — the conditional-read token contract.
    /// <para>
    /// These tests pin the one rule the feature rests on: a body may be omitted
    /// only when "unchanged" is provable. Every test below that produces a reason
    /// other than <c>matched</c> is a case where a full body MUST come back, so a
    /// regression shows up as a missing reason rather than a silently suppressed
    /// read.
    /// </para>
    /// </summary>
    public class ConditionalReadTokenTests
    {
        private static ConditionalReadToken.State ValidState() => new ConditionalReadToken.State
        {
            KbFingerprint = "kbhash",
            WorkerInstance = "worker-1",
            ModelVersion = "18.0.10",
            ObjectGuid = "0123456789abcdef0123456789abcdef",
            ObjectType = "Procedure",
            Part = "source",
            Offset = ConditionalReadToken.Unspecified,
            Limit = 0,
            Revision = string.Concat("638000000", "000000000", ":root"),
            BodyFingerprint = "bodyhash",
            VersionToken = string.Concat("638000000", "000000000", ":root:", "bodyhash"),
            TotalLines = 4200,
            TotalBytes = 262144,
            Truncated = false
        };

        [Fact]
        public void Issue_ThenParse_RoundTripsEveryBindingField()
        {
            var state = ValidState();
            string token = ConditionalReadToken.Issue(state);

            Assert.NotNull(token);
            Assert.StartsWith(ConditionalReadToken.Prefix, token);
            Assert.True(ConditionalReadToken.TryParse(token, out var parsed, out string reason));
            Assert.Equal(ConditionalReadToken.ReasonMatched, reason);
            Assert.Equal(state.KbFingerprint, parsed.KbFingerprint);
            Assert.Equal(state.WorkerInstance, parsed.WorkerInstance);
            Assert.Equal(state.ModelVersion, parsed.ModelVersion);
            Assert.Equal(state.ObjectGuid, parsed.ObjectGuid);
            Assert.Equal(state.ObjectType, parsed.ObjectType);
            Assert.Equal(state.Part, parsed.Part);
            Assert.Equal(state.Offset, parsed.Offset);
            Assert.Equal(state.Limit, parsed.Limit);
            Assert.Equal(state.Revision, parsed.Revision);
            Assert.Equal(state.BodyFingerprint, parsed.BodyFingerprint);
            Assert.Equal(state.VersionToken, parsed.VersionToken);
            Assert.Equal(state.TotalLines, parsed.TotalLines);
            Assert.Equal(state.TotalBytes, parsed.TotalBytes);
        }

        [Fact]
        public void Compare_Matched_WhenNothingChanged()
        {
            string token = ConditionalReadToken.Issue(ValidState());
            Assert.True(ConditionalReadToken.TryParse(token, out var parsed, out _));

            Assert.Equal(
                ConditionalReadToken.ReasonMatched,
                ConditionalReadToken.Compare(parsed, ValidState()));
        }

        [Fact]
        public void Compare_NotMatched_WhenRevisionAdvanced()
        {
            // The edit case. Same object, same part, same window: only the revision
            // moved, so the body must be retransmitted.
            string token = ConditionalReadToken.Issue(ValidState());
            ConditionalReadToken.TryParse(token, out var parsed, out _);

            var afterEdit = ValidState();
            afterEdit.Revision = "638000000000000123:root";
            afterEdit.BodyFingerprint = "changed";
            afterEdit.VersionToken = "638000000000000123:root:changed";

            Assert.Equal(
                ConditionalReadToken.ReasonRevisionAdvanced,
                ConditionalReadToken.Compare(parsed, afterEdit));
        }

        [Fact]
        public void Compare_NotMatched_WhenRevisionCannotBeEstablished()
        {
            // A missed or untrusted freshness signal must degrade to a full read.
            // Claiming notModified here is the exact failure the issue forbids.
            string token = ConditionalReadToken.Issue(ValidState());
            ConditionalReadToken.TryParse(token, out var parsed, out _);

            var noRevision = ValidState();
            noRevision.Revision = null;

            Assert.Equal(
                ConditionalReadToken.ReasonRevisionUnknown,
                ConditionalReadToken.Compare(parsed, noRevision));
        }

        [Theory]
        [InlineData("kb")]
        [InlineData("model")]
        [InlineData("worker")]
        [InlineData("guid")]
        [InlineData("type")]
        [InlineData("part")]
        [InlineData("window")]
        public void Compare_NotMatched_ForEveryBindingDimension(string dimension)
        {
            string token = ConditionalReadToken.Issue(ValidState());
            ConditionalReadToken.TryParse(token, out var parsed, out _);

            var drifted = ValidState();
            string expected;
            switch (dimension)
            {
                case "kb": drifted.KbFingerprint = "otherkb"; expected = ConditionalReadToken.ReasonKbChanged; break;
                case "model": drifted.ModelVersion = "17.0.1"; expected = ConditionalReadToken.ReasonModelChanged; break;
                case "worker": drifted.WorkerInstance = "worker-2"; expected = ConditionalReadToken.ReasonWorkerRestarted; break;
                case "guid": drifted.ObjectGuid = new string('a', 32); expected = ConditionalReadToken.ReasonObjectReplaced; break;
                case "type": drifted.ObjectType = "Transaction"; expected = ConditionalReadToken.ReasonObjectTypeChanged; break;
                case "part": drifted.Part = "rules"; expected = ConditionalReadToken.ReasonPartChanged; break;
                default: drifted.Limit = 200; expected = ConditionalReadToken.ReasonWindowChanged; break;
            }

            Assert.Equal(expected, ConditionalReadToken.Compare(parsed, drifted));
        }

        [Fact]
        public void Compare_NotMatched_ForOffsetChange()
        {
            string token = ConditionalReadToken.Issue(ValidState());
            ConditionalReadToken.TryParse(token, out var parsed, out _);

            var paged = ValidState();
            paged.Offset = 500;

            Assert.Equal(
                ConditionalReadToken.ReasonWindowChanged,
                ConditionalReadToken.Compare(parsed, paged));
        }

        [Fact]
        public void Compare_ReportsBindingMismatch_BeforeRevisionMismatch()
        {
            // Diagnosability contract: if both the binding and the revision moved,
            // the caller is told about the binding, because that is the fact they
            // can act on (they read the wrong object/part/window).
            string token = ConditionalReadToken.Issue(ValidState());
            ConditionalReadToken.TryParse(token, out var parsed, out _);

            var wrongEverything = ValidState();
            wrongEverything.Part = "rules";
            wrongEverything.Revision = "638000000000000999:root";

            Assert.Equal(
                ConditionalReadToken.ReasonPartChanged,
                ConditionalReadToken.Compare(parsed, wrongEverything));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("not-a-token")]
        [InlineData("grc1.@@@@not-base64@@@@")]
        [InlineData("grc1.")]
        [InlineData("grc0.eyJ2IjoxfQ")]
        public void TryParse_RejectsUnreadableTokens(string token)
        {
            Assert.False(ConditionalReadToken.TryParse(token, out var state, out string reason));
            Assert.Null(state);
            Assert.Equal(ConditionalReadToken.ReasonMalformed, reason);
        }

        [Fact]
        public void TryParse_RejectsOversizedToken()
        {
            string token = ConditionalReadToken.Prefix + new string('a', ConditionalReadToken.MaxTokenLength + 1);
            Assert.False(ConditionalReadToken.TryParse(token, out _, out string reason));
            Assert.Equal(ConditionalReadToken.ReasonMalformed, reason);
        }

        [Fact]
        public void TryParse_RejectsTokenMissingFreshnessFields()
        {
            // A token that lost its revision or body fingerprint cannot gate a
            // suppression. Hand-edited or truncated state must not become a licence
            // to omit a body.
            var noRevision = ValidState();
            noRevision.Revision = null;
            string token = ConditionalReadToken.Issue(noRevision);
            Assert.Null(token);

            var noFingerprint = ValidState();
            noFingerprint.BodyFingerprint = null;
            Assert.Null(ConditionalReadToken.Issue(noFingerprint));
        }

        [Fact]
        public void Issue_RejectsStateWithoutIdentity()
        {
            foreach (var mutate in new List<Action<ConditionalReadToken.State>>
            {
                s => s.KbFingerprint = null,
                s => s.WorkerInstance = null,
                s => s.ObjectGuid = null,
                s => s.Part = null
            })
            {
                var state = ValidState();
                mutate(state);
                Assert.Null(ConditionalReadToken.Issue(state));
            }
        }

        [Theory]
        [InlineData(null, null)]
        [InlineData("C:\\kb\\Alpha", "C:/KB/ALPHA")]
        [InlineData("C:\\kb\\Alpha\\", "C:/KB/ALPHA")]
        // A .gxw is the workspace file *inside* the KB directory, so it folds to
        // that directory — the same rule SourceSearchService.GetKbIdentity uses for
        // its search cursor. Agreeing with it is the point: two cursors that
        // disagree about "the same KB" would kill each other's tokens.
        [InlineData("C:\\kb\\Alpha.gxw", "C:/KB")]
        public void NormalizeKbPath_FoldsTheSamePhysicalKb_IntoOneValue(string left, string right)
        {
            // Case, trailing separators and the .gxw indirection must all resolve to
            // the same fingerprint, or a token minted by one alias silently dies on
            // the next call through another.
            string normalized = ConditionalReadToken.NormalizeKbPath(left);
            if (right == null)
            {
                Assert.Null(normalized);
                return;
            }
            Assert.Equal(ConditionalReadToken.NormalizeKbPath(right), normalized);
        }

        [Fact]
        public void NormalizeKbPath_DistinguishesDifferentKbs()
        {
            Assert.NotEqual(
                ConditionalReadToken.FingerprintKbPath("C:\\kb\\Alpha"),
                ConditionalReadToken.FingerprintKbPath("C:\\kb\\Beta"));
            Assert.Null(ConditionalReadToken.FingerprintKbPath(null));
            Assert.Null(ConditionalReadToken.FingerprintKbPath("  "));
        }

        [Theory]
        [InlineData(null, "source")]
        [InlineData("", "source")]
        [InlineData("  Source  ", "source")]
        [InlineData("RULES", "rules")]
        public void NormalizePart_IsCaseAndWhitespaceInsensitive(string input, string expected)
        {
            Assert.Equal(expected, ConditionalReadToken.NormalizePart(input));
        }

        [Fact]
        public void NormalizePage_MapsUnspecifiedToTheSameSentinelAsTheReadCache()
        {
            // -1 is BuildReadCacheKey's "unspecified" sentinel; the token must agree
            // or a default read and its token would disagree about the window.
            Assert.Equal(ConditionalReadToken.Unspecified, ConditionalReadToken.NormalizePage(null));
            Assert.Equal(0, ConditionalReadToken.NormalizePage(0));
            Assert.Equal(7, ConditionalReadToken.NormalizePage(7));
        }

        [Fact]
        public void DescribeReason_IsDefinedForEveryReasonCode()
        {
            // A silent fallback would leave a caller with a reason code and no
            // explanation, which is the worst outcome for a diagnostic field.
            var codes = new[]
            {
                ConditionalReadToken.ReasonMatched, ConditionalReadToken.ReasonAbsent,
                ConditionalReadToken.ReasonMalformed, ConditionalReadToken.ReasonKbChanged,
                ConditionalReadToken.ReasonModelChanged, ConditionalReadToken.ReasonWorkerRestarted,
                ConditionalReadToken.ReasonObjectReplaced, ConditionalReadToken.ReasonObjectTypeChanged,
                ConditionalReadToken.ReasonPartChanged, ConditionalReadToken.ReasonWindowChanged,
                ConditionalReadToken.ReasonRevisionAdvanced, ConditionalReadToken.ReasonRevisionUnknown,
                ConditionalReadToken.ReasonAuthoritativeRequired
            };
            foreach (var code in codes)
            {
                string described = ConditionalReadToken.DescribeReason(code);
                Assert.False(string.IsNullOrWhiteSpace(described), $"missing explanation for {code}");
                Assert.NotEqual("The body was returned unconditionally.", described);
            }
        }

        [Fact]
        public void Token_IsUrlSafe_SoItSurvivesEveryTransport()
        {
            // The token rides in JSON, MCP URIs and query strings. Standard base64
            // (+ and /) would break at least one of those.
            string token = ConditionalReadToken.Issue(ValidState());
            Assert.DoesNotContain("+", token);
            Assert.DoesNotContain("/", token);
            Assert.DoesNotContain("=", token);
        }
    }
}