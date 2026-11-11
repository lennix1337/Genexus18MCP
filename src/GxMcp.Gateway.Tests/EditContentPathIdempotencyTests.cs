using System;
using System.IO;
using System.Threading.Tasks;
using GxMcp.Gateway;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// The idempotency check hashes the request BEFORE the router turns <c>contentPath</c> into text. Without the file's
    /// fingerprint in the hash, the same key and the same path over a rewritten file would be taken for the request it
    /// already answered and replay a success that wrote the OLD text. With it, a rewritten file is a different request:
    /// the same key over different text is an <see cref="IdempotencyConflictException"/>, as it is for an inline content.
    /// </summary>
    public class EditContentPathIdempotencyTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "gxmcp-idem-" + Guid.NewGuid().ToString("N"));

        public EditContentPathIdempotencyTests() => Directory.CreateDirectory(_dir);

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        private static JObject Request(string path, string key, bool explicitNullContent = false)
        {
            var arguments = new JObject
            {
                ["name"] = "SampleDesignSystem",
                ["part"] = "Styles",
                ["mode"] = "full",
                ["contentPath"] = path,
                ["idempotencyKey"] = key
            };
            if (explicitNullContent) arguments["content"] = JValue.CreateNull();
            return new JObject { ["name"] = "genexus_edit", ["arguments"] = arguments };
        }

        private static Task<JObject> Succeeds(JObject request) => Task.FromResult(JObject.Parse("{\"isError\":false,\"data\":{\"id\":1}}"));

        [Fact]
        public async Task SameKeyAndPath_OverARewrittenFile_IsAConflict_NotAReplayOfTheOldWrite()
        {
            string path = Path.Combine(_dir, "styles.txt");
            File.WriteAllText(path, "first text");
            var middleware = new IdempotencyMiddleware(new IdempotencyCache(15, 1000), kbPath: "kb1");
            int calls = 0;

            await middleware.Invoke(Request(path, "file-key"), req => { calls++; return Succeeds(req); });
            File.WriteAllText(path, "second text");

            await Assert.ThrowsAsync<IdempotencyConflictException>(
                () => middleware.Invoke(Request(path, "file-key"), req => { calls++; return Succeeds(req); }));
            Assert.Equal(1, calls);
        }

        [Fact]
        public async Task SameKeyAndPath_WithAnExplicitNullContent_OverARewrittenFile_IsStillAConflict()
        {
            // `content: null` reaches the middleware as a JSON null, not as a missing key; the router reads the file for it
            // (Resolve treats it as absent), so the file's fingerprint must join the hash here too.
            string path = Path.Combine(_dir, "styles.txt");
            File.WriteAllText(path, "first text");
            var middleware = new IdempotencyMiddleware(new IdempotencyCache(15, 1000), kbPath: "kb1");
            int calls = 0;

            await middleware.Invoke(Request(path, "null-content-key", explicitNullContent: true), req => { calls++; return Succeeds(req); });
            File.WriteAllText(path, "second text");

            await Assert.ThrowsAsync<IdempotencyConflictException>(
                () => middleware.Invoke(Request(path, "null-content-key", explicitNullContent: true), req => { calls++; return Succeeds(req); }));
            Assert.Equal(1, calls);
        }

        [Fact]
        public async Task SameKeyAndPath_OverTheSameFile_ReplaysTheStoredResult()
        {
            string path = Path.Combine(_dir, "styles.txt");
            File.WriteAllText(path, "same text");
            var middleware = new IdempotencyMiddleware(new IdempotencyCache(15, 1000), kbPath: "kb1");
            int calls = 0;

            var first = await middleware.Invoke(Request(path, "file-key"), req => { calls++; return Succeeds(req); });
            var second = await middleware.Invoke(Request(path, "file-key"), req => { calls++; return Succeeds(req); });

            Assert.Equal(1, calls);
            Assert.Null(first["meta"]?["idempotent"]);
            Assert.True((bool)second["meta"]!["idempotent"]!);
        }

        [Fact]
        public async Task AFileTheFingerprintCannotReadDoesNotBreakTheKey()
        {
            // The router refuses the missing file later, with its own error; the idempotency check must not throw for it.
            string missing = Path.Combine(_dir, "absent.txt");
            var middleware = new IdempotencyMiddleware(new IdempotencyCache(15, 1000), kbPath: "kb1");
            int calls = 0;

            await middleware.Invoke(Request(missing, "file-key"), req => { calls++; return Succeeds(req); });

            Assert.Equal(1, calls);
        }

        [Fact]
        public async Task InlineContent_IsHashedAsBefore()
        {
            var middleware = new IdempotencyMiddleware(new IdempotencyCache(15, 1000), kbPath: "kb1");
            int calls = 0;
            var request = JObject.Parse(
                "{\"name\":\"genexus_edit\",\"arguments\":{\"name\":\"X\",\"part\":\"Source\",\"content\":\"<x/>\",\"idempotencyKey\":\"inline-key\"}}");

            await middleware.Invoke(request, req => { calls++; return Succeeds(req); });
            var replay = await middleware.Invoke(request, req => { calls++; return Succeeds(req); });

            Assert.Equal(1, calls);
            Assert.True((bool)replay["meta"]!["idempotent"]!);
        }
    }
}
