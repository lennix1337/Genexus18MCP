using System;
using System.IO;
using System.Linq;
using System.Reflection;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // Wave-3: BlameService — porcelain parser + KbNotInGit envelope.
    // Tests target the pure helpers + the path that doesn't need a real
    // git binary. End-to-end git integration is left to the live worker.
    public class BlameServiceTests : IDisposable
    {
        private readonly string _tempDir;

        public BlameServiceTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "gxmcp-blame-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
        }

        // Stand-in for an opened GeneXus KB — same trick used in TimeTravelServiceTests.
        public class FakeKb
        {
            public string Location { get; set; }
        }

        private KbService BuildKbServiceWithPath(string path)
        {
            var indexCache = new IndexCacheService();
            var kb = new KbService(indexCache);
            var fld = typeof(KbService).GetField("_kb", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(fld);
            fld.SetValue(kb, new FakeKb { Location = path });
            return kb;
        }

        [Fact]
        public void ParsePorcelain_TwoCommits_ReturnsBothEntries()
        {
            string sample =
                "abcdef0123456789abcdef0123456789abcdef01 1 1 2\n" +
                "author Alice\n" +
                "author-mail <alice@example.com>\n" +
                "author-time 1700000000\n" +
                "summary First commit\n" +
                "\tline one\n" +
                "1234567890abcdef1234567890abcdef12345678 2 2 1\n" +
                "author Bob\n" +
                "author-mail <bob@example.com>\n" +
                "author-time 1700001000\n" +
                "summary Second commit\n" +
                "\tline two\n";

            // ParsePorcelain is private static; exercise via reflection.
            var mi = typeof(BlameService).GetMethod(
                "ParsePorcelain",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.NotNull(mi);
            var entries = (System.Collections.IEnumerable)mi!.Invoke(null, new object[] { sample })!;
            var list = entries.Cast<object>().ToList();
            Assert.Equal(2, list.Count);

            string FieldOf(object o, string n) =>
                o.GetType().GetField(n, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)
                 ?.GetValue(o)?.ToString();
            Assert.Equal("Alice", FieldOf(list[0], "Author"));
            Assert.Equal("Bob", FieldOf(list[1], "Author"));
            Assert.Equal("line one", FieldOf(list[0], "LineContent"));
            Assert.Equal("First commit", FieldOf(list[0], "Summary"));
        }

        [Fact]
        public void Blame_NoKb_ReturnsNoKbError()
        {
            var svc = new BlameService(kbService: null, objectService: null);
            var json = JObject.Parse(svc.Blame(new BlameService.BlameRequest { Name = "Foo" }));
            Assert.Equal("NoKb", (string)json["code"]);
        }

        [Fact]
        public void Blame_MissingNameAndFilePath_ReturnsMissingArgs()
        {
            var svc = new BlameService(kbService: null, objectService: null);
            var json = JObject.Parse(svc.Blame(new BlameService.BlameRequest()));
            Assert.Equal("missingArgs", (string)json["code"]);
        }

        // TECHDEBT-02: KbNotInGit/PartNotTracked/PathOutsideRepo/FileNotFound/GitFailed
        // used to be hand-rolled {error,code} JObjects; they now route through the
        // canonical McpResponse.Err envelope (status:"error" + nested error.code).
        // NoKb/missingArgs above are untouched — they're read via top-level `code`
        // by the pre-existing tests, so normalizing them would be an observable
        // shape change (see plans/README.md TECHDEBT-02 entry).
        [Fact]
        public void Blame_KbDirWithoutGit_ReturnsCanonicalKbNotInGitError()
        {
            var kbSvc = BuildKbServiceWithPath(_tempDir);
            // TEMP may itself be inside a checkout. This case tests the no-repository
            // envelope; real filesystem discovery is exercised separately below.
            var svc = new BlameService(kbSvc, objectService: null, findGitRoot: path =>
            {
                Assert.Equal(_tempDir, path);
                return null;
            });
            var json = JObject.Parse(svc.Blame(new BlameService.BlameRequest { Name = "Foo" }));
            Assert.Equal("error", (string)json["status"]);
            Assert.Equal("KbNotInGit", (string)json["error"]["code"]);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void FindGitRoot_FindsNearestMarkerRegardlessOfTempAncestors(bool worktreeFile)
        {
            var repository = Path.Combine(_tempDir, "repository");
            var nested = Path.Combine(repository, "nested");
            var child = Path.Combine(nested, "child");
            Directory.CreateDirectory(child);
            Directory.CreateDirectory(Path.Combine(repository, ".git"));
            if (worktreeFile)
                File.WriteAllText(Path.Combine(nested, ".git"), "gitdir: synthetic");
            else
                Directory.CreateDirectory(Path.Combine(nested, ".git"));

            Assert.Equal(nested, BlameService.FindGitRoot(child));
            Assert.Equal(repository, BlameService.FindGitRoot(repository));
        }
    }
}
