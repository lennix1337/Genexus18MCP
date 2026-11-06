using System;
using System.IO;
using System.Linq;
using GxMcp.TestSupport;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// Credential redaction existed five times: three <c>LogValue</c> copies in the
    /// Gateway and two in the Worker. Four were byte-identical. The fifth,
    /// <c>SharedWorkerHost.RedactDiagnostic</c>, was not - it had already added
    /// <c>connectionstring</c> to the key list.
    ///
    /// That divergence is the whole argument for this file. Four of the five
    /// masking paths logged a connection string only in part, because the one
    /// author who thought of connection strings updated the copy in front of them.
    /// These tests hold the single rule and assert the drift cannot come back.
    ///
    /// The Worker holds the same helper under the same name; the assemblies cannot
    /// share a type, so <see cref="TheTwoAssemblyCopiesCarryTheSamePattern"/>
    /// compares the literals.
    /// </summary>
    public class LogRedactionSingleSourceTests
    {
        [Fact]
        public void ThePatternExistsOncePerAssembly()
        {
            // The needle is the declaration, not the key list. The key list is
            // expected to change whenever a credential shape is added - that is
            // the point of the rule - and pinning it made this test fail for a
            // correct change, which is how a real leak would get waved through.
            // One "internal const string Pattern" per assembly is the invariant the
            // name actually claims.
            const string declaration = "internal const string Pattern =";

            Assert.Equal(1, CountOccurrences(AllSource("GxMcp.Gateway"), declaration));
            Assert.Equal(1, CountOccurrences(AllSource("GxMcp.Worker"), declaration));
        }

        [Fact]
        public void EveryCallSiteGoesThroughTheHelper()
        {
            // Three in the Gateway. If one grew its own inline Regex.Replace again,
            // "one rule" would quietly become two.
            foreach (string file in new[]
            {
                RepoSource.PathOf("src", "GxMcp.Gateway", "KbImportHelper.cs"),
                RepoSource.PathOf("src", "GxMcp.Gateway", "MacroSuggestionService.cs"),
                RepoSource.PathOf("src", "GxMcp.Gateway", "Program.Http.cs"),
            })
            {
                string src = File.ReadAllText(file);
                Assert.Contains("LogRedaction.Redact(value)", src);
                Assert.DoesNotContain("password|passwd|pass|token|secret", src);
            }
        }

        [Fact]
        public void AConnectionStringIsMaskedWhereItWasNotBefore()
        {
            // The regression this consolidation exists to remove. Only
            // SharedWorkerHost.RedactDiagnostic knew about "connectionstring", so
            // on the other four paths this value logged its secret intact.
            string masked = LogRedaction.Redact("connectionstring=hunter2");

            Assert.DoesNotContain("hunter2", masked);
            Assert.Contains("connectionstring=", masked);
            Assert.Contains("<redacted>", masked);
        }

        [Fact]
        public void TheServerAndPortOfAConnectionStringAreAlsoHidden()
        {
            // Previously visible on four of the five paths, which is where a KB
            // alias and an internal host name ended up in the log.
            string masked = LogRedaction.Redact("connectionstring=Server=db.internal");

            Assert.DoesNotContain("db.internal", masked);
        }

        [Theory]
        [InlineData("Password=hunter2")]
        [InlineData("password: hunter2")]
        [InlineData("passwd=hunter2")]
        [InlineData("token=hunter2")]
        [InlineData("secret=hunter2")]
        [InlineData("api_key=hunter2")]
        [InlineData("authorization=Bearer hunter2")]
        [InlineData("credential=hunter2")]
        [InlineData("connectionstring=hunter2")]
        public void EveryRecognisedKeyIsMasked(string input)
        {
            Assert.DoesNotContain("hunter2", LogRedaction.Redact(input));
        }

        [Fact]
        public void APwdDelimitedConnectionStringNoLongerLeaksItsTail()
        {
            // This used to be a KNOWN GAP recorded here rather than folded into the
            // consolidation that created this file: the unmasked value pattern stops
            // at a delimiter, so a semicolon-delimited connection string leaked
            // everything after its first ";", including "Pwd=" - the standard ADO.NET
            // alias for "Password". The pattern now recognises the short alias, so the
            // tail is masked with the head. Before this change the masked output ended
            // at "<redacted>;Pwd=SENTINEL-CREDENTIAL".
            Assert.Equal(
                "connectionstring=<redacted>;Pwd=<redacted>",
                LogRedaction.Redact("connectionstring=Server=db;Pwd=SENTINEL-CREDENTIAL"));
        }

        [Fact]
        public void ThePwdKeyIsRecognisedOnItsOwn()
        {
            // Renamed from ThePwdKeyIsStillNotRecognisedOnItsOwn when the pattern
            // started recognising it. "Pwd" is the standard ADO.NET alias for
            // "Password", so leaving it unmatched logged a real connection string
            // credential intact - and the name documented the leak.
            Assert.Equal("pwd=<redacted>", LogRedaction.Redact("pwd=SENTINEL-CREDENTIAL"));
        }

        [Fact]
        public void BenignTextIsLeftAlone()
        {
            // Over-redaction costs diagnostics, so the rule has to be narrow enough
            // that ordinary log lines survive readable.
            foreach (string benign in new[] { "user=alice", "target=Foo", @"kb=C:\kb\MyKb" })
                Assert.Equal(benign, LogRedaction.Redact(benign));
        }

        [Fact]
        public void NullAndEmptyAreHandledWithoutThrowing()
        {
            // This runs on the logging path: throwing here would replace the log
            // line with the failure to log it.
            Assert.Equal(string.Empty, LogRedaction.Redact(null!));
            Assert.Equal(string.Empty, LogRedaction.Redact(string.Empty));
        }

        [Fact]
        public void TheKeyAndSeparatorSurviveSoTheLineStaysReadable()
        {
            Assert.Equal("login=alice password=<redacted>", LogRedaction.Redact("login=alice password=hunter2"));
        }

        [Fact]
        public void TheTwoAssemblyCopiesCarryTheSamePattern()
        {
            // The two assemblies cannot share a type. They can still be asserted
            // equal - which is the check the five divergent copies never had.
            string? gwPattern = ExtractPattern(RepoSource.Read("src", "GxMcp.Gateway", "Helpers", "LogRedaction.cs"));
            string? wkPattern =ExtractPattern(RepoSource.Read("src", "GxMcp.Worker", "Helpers", "LogRedaction.cs"));

            Assert.NotNull(gwPattern);
            Assert.NotNull(wkPattern);
            Assert.Equal(wkPattern, gwPattern);
        }

        private static string? ExtractPattern(string helperSource)
        {
            const string marker = "internal const string Pattern =";
            int at = helperSource.IndexOf(marker, StringComparison.Ordinal);
            if (at < 0) return null;

            int start = helperSource.IndexOf("@\"", at, StringComparison.Ordinal);
            int end = helperSource.IndexOf("\";", start, StringComparison.Ordinal);
            return helperSource.Substring(start, end + 2 - start);
        }

        private static int CountOccurrences(string haystack, string needle)
        {
            int count = 0, index = 0;
            while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += needle.Length;
            }
            return count;
        }

        /// <summary>
        /// Every production source file of one assembly, concatenated. Bin and obj
        /// are excluded so a stale build artefact cannot satisfy or break the count.
        /// </summary>
        private static string AllSource(string projectName)
        {
            var dir = new DirectoryInfo(RepoSource.DirectoryOf("src", projectName));
            if (dir == null)
            {
                throw new DirectoryNotFoundException(
                    "Could not locate src/" + projectName
                    + " starting from " + AppDomain.CurrentDomain.BaseDirectory);
            }

            var builder = new System.Text.StringBuilder();
            foreach (string file in Directory.GetFiles(dir.FullName, "*.cs", SearchOption.AllDirectories)
                .Where(f => f.IndexOf(@"\bin\", StringComparison.OrdinalIgnoreCase) < 0
                         && f.IndexOf(@"\obj\", StringComparison.OrdinalIgnoreCase) < 0)
                .OrderBy(f => f, StringComparer.Ordinal))
            {
                builder.Append(File.ReadAllText(file));
            }
            return builder.ToString();
        }

            }
}