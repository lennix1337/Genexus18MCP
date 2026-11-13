using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using GxMcp.Gateway;
using Newtonsoft.Json.Linq;
using Xunit;
using Xunit.Abstractions;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// Opt-in KB history: the capture decision runs on fake Worker receipts, the committer runs a
    /// real git in a temp directory. Nothing here touches the project repository or the user's git
    /// configuration (the recorder passes its own commit identity per call).
    /// </summary>
    [Collection("KbHistory")]
    public sealed class KbHistoryTests : IDisposable
    {
        private readonly ITestOutputHelper _output;
        private readonly string _temp = Path.Combine(Path.GetTempPath(), "gxmcp-kbhistory-" + Guid.NewGuid().ToString("N"));
        private readonly string _kb;
        private readonly string _root;
        private readonly List<string> _log = new List<string>();

        public KbHistoryTests(ITestOutputHelper output)
        {
            _output = output;
            _kb = Path.Combine(_temp, "KB");
            _root = Path.Combine(_temp, "history");
            Directory.CreateDirectory(_kb);
        }

        public void Dispose()
        {
            try
            {
                foreach (string file in Directory.EnumerateFiles(_temp, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(_temp, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static string Sha(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

        private static JObject EditArgs(string name = "MyProc", string mode = "full")
            => new JObject { ["name"] = name, ["type"] = "Procedure", ["part"] = "Source", ["mode"] = mode, ["content"] = "x" };

        private static JObject Receipt(string source, Action<JObject>? tweak = null)
        {
            var receipt = new JObject
            {
                ["status"] = "ok",
                ["code"] = "WriteApplied",
                ["target"] = "MyProc",
                ["part"] = "Source",
                ["source"] = source,
                ["persistedHash"] = Sha(source),
                ["persistedState"] = "confirmed",
                ["persisted"] = true,
                ["verified"] = true,
                ["changed"] = true,
                ["postSaveVerification"] = new JObject { ["reReadConfirmed"] = true },
                ["correlationId"] = "corr-123"
            };
            tweak?.Invoke(receipt);
            return receipt;
        }

        private static JObject DeleteReceipt(Action<JObject>? tweak = null)
        {
            var receipt = new JObject
            {
                ["status"] = "ok",
                ["code"] = "ObjectDeleted",
                ["target"] = "MyProc",
                ["result"] = new JObject
                {
                    ["deleted"] = "MyProc", ["type"] = "Procedure", ["persisted"] = true, ["rereadConfirmed"] = true
                }
            };
            tweak?.Invoke(receipt);
            return receipt;
        }

        private KbHistoryJob? Capture(JObject args, JObject payload, string tool = "genexus_edit")
            => KbHistoryCapture.TryCreate(tool, args, payload, _kb, "Demo", "session-1");

        private KbHistoryRecorder NewRecorder(string git = "git", int capacity = KbHistoryRecorder.DefaultCapacity)
            => new KbHistoryRecorder(_root, git, line => { lock (_log) _log.Add(line); }, capacity);

        private KbHistoryJob WriteJob(string text, string name = "MyProc", string? hash = null)
            => Capture(EditArgs(name), Receipt(text, r => { r["target"] = name; if (hash != null) r["persistedHash"] = hash; }))!;

        private string Repo()
            => Directory.EnumerateDirectories(_root).Single(d => Directory.Exists(Path.Combine(d, ".git")));

        private static string Git(string repo, params string[] args)
        {
            var start = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            start.ArgumentList.Add("-C");
            start.ArgumentList.Add(repo);
            foreach (string a in args) start.ArgumentList.Add(a);
            start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
            using var p = Process.Start(start)!;
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return output.TrimEnd('\n');
        }

        // ---- capture ----------------------------------------------------------------------

        [Fact]
        public void ConfirmedEditReceiptBecomesAWriteJob()
        {
            KbHistoryJob? job = Capture(EditArgs(), Receipt("line1\nline2"));

            Assert.NotNull(job);
            Assert.Equal(KbHistoryJobKind.Write, job!.Kind);
            Assert.Equal("Procedure", job.ObjectType);
            Assert.Equal("MyProc", job.ObjectName);
            Assert.Equal("Source", job.Part);
            Assert.Equal("line1\nline2", job.Content);
            Assert.Equal("full", job.Action);
            Assert.Equal("session-1", job.SessionId);
            Assert.Equal("corr-123", job.OperationId);
        }

        [Fact]
        public void ReceiptNestedUnderResultIsRecognised()
        {
            var nested = new JObject { ["status"] = "success", ["result"] = Receipt("a") };
            ((JObject)nested["result"]!).Remove("status");

            Assert.NotNull(Capture(EditArgs(mode: "patch"), nested));
        }

        public static IEnumerable<object[]> UnconfirmedReceipts()
        {
            yield return new object[] { "error status", (Action<JObject>)(r => r["status"] = "error") };
            yield return new object[] { "error object", (Action<JObject>)(r => r["error"] = new JObject { ["code"] = "X" }) };
            yield return new object[] { "dry-run code", (Action<JObject>)(r => r["code"] = "WriteDryRun") };
            yield return new object[] { "dryRun flag", (Action<JObject>)(r => r["dryRun"] = true) };
            yield return new object[] { "no change", (Action<JObject>)(r => r["code"] = "WriteNoChange") };
            yield return new object[] { "changed false", (Action<JObject>)(r => r["changed"] = false) };
            yield return new object[] { "state unknown", (Action<JObject>)(r => r["persistedState"] = "unknown") };
            yield return new object[] { "state not_persisted", (Action<JObject>)(r => r["persistedState"] = "not_persisted") };
            yield return new object[] { "not persisted", (Action<JObject>)(r => r["persisted"] = false) };
            yield return new object[] { "no re-read", (Action<JObject>)(r => r["postSaveVerification"] = new JObject { ["reReadConfirmed"] = false }) };
            yield return new object[] { "no re-read block", (Action<JObject>)(r => r.Remove("postSaveVerification")) };
            yield return new object[] { "no source", (Action<JObject>)(r => r.Remove("source")) };
        }

        [Theory]
        [MemberData(nameof(UnconfirmedReceipts))]
        public void NoJobWhenPersistenceIsNotConfirmed(string _, Action<JObject> tweak)
        {
            Assert.Null(Capture(EditArgs(), Receipt("text", tweak)));
        }

        [Fact]
        public void NoJobForDryRunRequestEvenWithAConfirmedLookingReceipt()
        {
            var args = EditArgs();
            args["dryRun"] = true;
            Assert.Null(Capture(args, Receipt("text")));
        }

        [Theory]
        [InlineData("parts")]
        [InlineData("targets")]
        [InlineData("changeSet")]
        public void NoJobForMultiTargetEdits(string field)
        {
            var args = EditArgs();
            args[field] = new JArray();
            Assert.Null(Capture(args, Receipt("text")));
        }

        [Theory]
        [InlineData("..")]
        [InlineData("a/b")]
        [InlineData("a\\b")]
        [InlineData("")]
        public void NoJobForUnsafeObjectNames(string name)
        {
            Assert.Null(Capture(EditArgs(name), Receipt("text", r => r["target"] = name)));
        }

        [Fact]
        public void NoJobForToolsOutsideTheHistoryScope()
        {
            Assert.Null(Capture(EditArgs(), Receipt("text"), tool: "genexus_read"));
            Assert.Null(Capture(EditArgs(), Receipt("text"), tool: "genexus_variable"));
        }

        [Fact]
        public void ConfirmedDeleteBecomesADeleteJob()
        {
            var args = new JObject { ["name"] = "MyProc", ["type"] = "Procedure", ["confirm"] = true };
            KbHistoryJob? job = Capture(args, DeleteReceipt(), "genexus_delete_object");

            Assert.NotNull(job);
            Assert.Equal(KbHistoryJobKind.Delete, job!.Kind);
            Assert.Equal("Procedure", job.ObjectType);
            Assert.Equal("MyProc", job.ObjectName);
            Assert.Null(job.Part);
        }

        [Fact]
        public void NoDeleteJobForDryRunUnconfirmedOrUnpersistedDeletes()
        {
            var confirmed = new JObject { ["name"] = "MyProc", ["type"] = "Procedure", ["confirm"] = true };
            var dryRun = (JObject)confirmed.DeepClone();
            dryRun["dryRun"] = true;
            var noConfirm = new JObject { ["name"] = "MyProc", ["type"] = "Procedure" };

            Assert.Null(Capture(dryRun, DeleteReceipt(), "genexus_delete_object"));
            Assert.Null(Capture(noConfirm, DeleteReceipt(), "genexus_delete_object"));
            Assert.Null(Capture(confirmed, DeleteReceipt(r => ((JObject)r["result"]!)["persisted"] = false), "genexus_delete_object"));
            Assert.Null(Capture(confirmed, DeleteReceipt(r => ((JObject)r["result"]!)["rereadConfirmed"] = false), "genexus_delete_object"));
            Assert.Null(Capture(confirmed, DeleteReceipt(r => r["code"] = "DeleteNotPersisted"), "genexus_delete_object"));
        }

        // ---- committer (real git) -----------------------------------------------------------

        [Fact]
        public async Task ConfirmedWriteCreatesOneCommitWithPathContentAndMessage()
        {
            var recorder = NewRecorder();
            var args = EditArgs();
            args["clientRequestId"] = "req token=abc123";
            Assert.True(recorder.TryEnqueue(Capture(args, Receipt("Do\r\n  Msg('hi')\r\nEndDo\r\n"))!));

            await recorder.StopAsync(TimeSpan.FromSeconds(60));

            string repo = Repo();
            Assert.Equal(1, recorder.Committed);
            Assert.Equal("Do\r\n  Msg('hi')\r\nEndDo\r\n", File.ReadAllText(Path.Combine(repo, "Procedure", "MyProc", "Source.gx")));
            Assert.Equal("genexus_edit full Procedure MyProc (Source)", Git(repo, "log", "-1", "--format=%s"));
            string body = Git(repo, "log", "-1", "--format=%b");
            Assert.Contains("KB: Demo", body);
            Assert.Contains("Session: session-1", body);
            Assert.Contains("Operation: corr-123", body);
            Assert.DoesNotContain("abc123", body);
            Assert.Equal("GeneXus MCP", Git(repo, "log", "-1", "--format=%an"));
            Assert.Equal("Procedure/MyProc/Source.gx", Git(repo, "show", "--name-only", "--format=", "HEAD"));
            Assert.Empty(Directory.GetFiles(_kb, "*", SearchOption.AllDirectories));
            Assert.StartsWith(Path.GetFullPath(_root), repo);
        }

        [Fact]
        public async Task HistoryShowsEachChangeAndSkipsIdenticalText()
        {
            var recorder = NewRecorder();
            recorder.TryEnqueue(WriteJob("one\n"));
            recorder.TryEnqueue(WriteJob("one\ntwo\n"));
            recorder.TryEnqueue(WriteJob("one\ntwo\n"));

            await recorder.StopAsync(TimeSpan.FromSeconds(60));

            string repo = Repo();
            Assert.Equal(2, recorder.Committed);
            Assert.Equal(1, recorder.Unchanged);
            Assert.Equal(2, Git(repo, "log", "--format=%s", "--", "Procedure/MyProc/Source.gx").Split('\n').Length);
            Assert.Contains("+two", Git(repo, "log", "-1", "-p", "--format=", "--", "Procedure/MyProc/Source.gx"));
        }

        [Fact]
        public async Task ConfirmedDeleteCommitsTheRemoval()
        {
            var recorder = NewRecorder();
            recorder.TryEnqueue(WriteJob("body\n"));
            var args = new JObject { ["name"] = "MyProc", ["type"] = "Procedure", ["confirm"] = true };
            recorder.TryEnqueue(Capture(args, DeleteReceipt(), "genexus_delete_object")!);

            await recorder.StopAsync(TimeSpan.FromSeconds(60));

            string repo = Repo();
            Assert.Equal(2, recorder.Committed);
            Assert.False(Directory.Exists(Path.Combine(repo, "Procedure", "MyProc")));
            Assert.Equal("genexus_delete_object delete Procedure MyProc", Git(repo, "log", "-1", "--format=%s"));
            Assert.Equal("D\tProcedure/MyProc/Source.gx", Git(repo, "show", "--name-status", "--format=", "HEAD"));
        }

        [Fact]
        public async Task TextThatDoesNotMatchTheReportedHashIsNotRecorded()
        {
            var recorder = NewRecorder();
            recorder.TryEnqueue(WriteJob("real text", hash: Sha("something else")));

            await recorder.StopAsync(TimeSpan.FromSeconds(60));

            Assert.Equal(0, recorder.Committed);
            Assert.Equal(1, recorder.Skipped);
            Assert.False(File.Exists(Path.Combine(Repo(), "Procedure", "MyProc", "Source.gx")));
        }

        [Fact]
        public async Task QueueOverflowDropsTheOldestAndKeepsTheNewestState()
        {
            var recorder = NewRecorder(capacity: 2);
            const int total = 40;
            for (int i = 1; i <= total; i++)
                Assert.True(recorder.TryEnqueue(WriteJob("version " + i + "\n")));

            await recorder.StopAsync(TimeSpan.FromSeconds(120));

            string repo = Repo();
            Assert.Equal(total, recorder.Enqueued);
            Assert.True(recorder.Dropped > 0, "a burst larger than the queue must drop entries");
            Assert.Equal(total, recorder.Committed + recorder.Dropped + recorder.Unchanged + recorder.Failed + recorder.Abandoned + recorder.Skipped);
            Assert.Equal("version " + total + "\n", File.ReadAllText(Path.Combine(repo, "Procedure", "MyProc", "Source.gx")));
            Assert.Contains(_log, line => line.Contains("queue full"));
        }

        [Fact]
        public async Task ShutdownWithNoDrainTimeCancelsPendingJobsCleanly()
        {
            var recorder = NewRecorder(capacity: 64);
            for (int i = 0; i < 30; i++) recorder.TryEnqueue(WriteJob("v" + i + "\n"));

            var sw = Stopwatch.StartNew();
            await recorder.StopAsync(TimeSpan.Zero);
            sw.Stop();

            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15), "cancel must not wait for the whole backlog");
            Assert.Equal(30, recorder.Enqueued);
            Assert.Equal(30, recorder.Committed + recorder.Abandoned + recorder.Dropped + recorder.Unchanged + recorder.Failed + recorder.Skipped);
            Assert.True(recorder.Abandoned > 0);
            Assert.False(recorder.TryEnqueue(WriteJob("late")) && recorder.Enqueued > 30);
        }

        [Fact]
        public async Task ShutdownDrainsPendingJobsWhenGivenTime()
        {
            var recorder = NewRecorder();
            for (int i = 0; i < 3; i++) recorder.TryEnqueue(WriteJob("v" + i + "\n", name: "Obj" + i));

            await recorder.StopAsync(TimeSpan.FromSeconds(60));

            Assert.Equal(3, recorder.Committed);
            Assert.Equal(0, recorder.Abandoned);
        }

        [Fact]
        public async Task MissingGitDisablesTheFeatureWithOneLogLine()
        {
            var recorder = NewRecorder(git: Path.Combine(_temp, "no-such-git.exe"));
            recorder.TryEnqueue(WriteJob("a"));
            recorder.TryEnqueue(WriteJob("b"));

            await recorder.StopAsync(TimeSpan.FromSeconds(30));

            Assert.True(recorder.IsDisabled);
            Assert.Equal(1, _log.Count(line => line.Contains("disabled for this process")));
            Assert.False(recorder.TryEnqueue(WriteJob("c")));
            Assert.Equal(0, recorder.Committed);
            Assert.False(Directory.Exists(_root) && Directory.EnumerateFileSystemEntries(_root).Any());
        }

        [Fact]
        public async Task HistoryLocationInsideTheKbFolderDisablesTheFeature()
        {
            var recorder = new KbHistoryRecorder(Path.Combine(_kb, "history"), "git", line => { lock (_log) _log.Add(line); });
            recorder.TryEnqueue(WriteJob("a"));

            await recorder.StopAsync(TimeSpan.FromSeconds(30));

            Assert.True(recorder.IsDisabled, string.Join(" | ", _log));
            Assert.Contains(_log, line => line.Contains("inside the Knowledge Base folder"));
            Assert.Empty(Directory.GetFileSystemEntries(_kb));
        }

        [Fact]
        public void FeatureIsOffUnlessTheEnvironmentEnablesIt()
        {
            string? previous = Environment.GetEnvironmentVariable(KbHistoryRecorder.EnabledVariable);
            try
            {
                Environment.SetEnvironmentVariable(KbHistoryRecorder.EnabledVariable, null);
                Assert.Null(KbHistoryRecorder.CreateFromEnvironment(_ => { }));
                Environment.SetEnvironmentVariable(KbHistoryRecorder.EnabledVariable, "0");
                Assert.Null(KbHistoryRecorder.CreateFromEnvironment(_ => { }));
            }
            finally
            {
                Environment.SetEnvironmentVariable(KbHistoryRecorder.EnabledVariable, previous);
            }
        }

        // ---- request path -------------------------------------------------------------------

        private static Program.ToolCallResponseContext Context(string tool, JObject args, string kbPath, string alias)
            => new Program.ToolCallResponseContext
            {
                ToolName = tool,
                ToolArgs = args,
                LcAction = null,
                KbScope = alias,
                RecoveryReads = new List<(string Target, string Part, RecoveryRequirement? Observed)>(),
                PropertiesRecoveryRead = false,
                CacheKey = null,
                IsLiveTool = false,
                CacheEpochAtDispatch = 0,
                CacheRevisionAtDispatch = 0,
                KbPath = kbPath,
                KbAlias = alias,
                SessionId = "session-1"
            };

        [Fact]
        public async Task ToolResponseIsShapedAndReturnedWhileTheCommitHappensInTheBackground()
        {
            var recorder = NewRecorder();
            var previous = Program._kbHistory;
            Program._kbHistory = recorder;
            JObject toolResult;
            try
            {
                var envelope = new JObject { ["jsonrpc"] = "2.0", ["result"] = Receipt(new string('x', 30000) + "\n") };
                toolResult = Program.ShapeWorkerToolResponse(envelope, Context("genexus_edit", EditArgs(), _kb, "Demo"));
                Assert.Equal(1, recorder.Enqueued);
            }
            finally
            {
                Program._kbHistory = previous;
            }

            Assert.NotNull(toolResult["content"]);
            await recorder.StopAsync(TimeSpan.FromSeconds(60));
            string repo = Repo();
            Assert.Equal(new string('x', 30000) + "\n", File.ReadAllText(Path.Combine(repo, "Procedure", "MyProc", "Source.gx")));
        }

        [Fact]
        public void FeatureOffAddsNoWorkToTheRequestPath()
        {
            Assert.Null(Program._kbHistory);
            var envelope = new JObject { ["jsonrpc"] = "2.0", ["result"] = Receipt("text") };

            Program.ShapeWorkerToolResponse(envelope, Context("genexus_edit", EditArgs(), _kb, "Demo"));

            Assert.False(Directory.Exists(_root));
        }

        [Fact]
        public void DryRunAndFailedCallsEnqueueNothingThroughTheRequestHook()
        {
            var recorder = NewRecorder(git: Path.Combine(_temp, "never-started"));
            var dry = EditArgs();
            dry["dryRun"] = true;

            Program.TryRecordKbHistory(recorder, Context("genexus_edit", dry, _kb, "Demo"), Receipt("text"));
            Program.TryRecordKbHistory(recorder, Context("genexus_edit", EditArgs(), _kb, "Demo"),
                Receipt("text", r => { r["status"] = "error"; r["error"] = new JObject { ["code"] = "WriteNotPersisted" }; }));
            Program.TryRecordKbHistory(recorder, Context("genexus_read", EditArgs(), _kb, "Demo"), Receipt("text"));

            Assert.Equal(0, recorder.Enqueued);
            Assert.Equal(2, recorder.Skipped);
        }

        [Fact]
        public async Task MeasuresPerCommitCostAndWholeResponseOverhead()
        {
            var recorder = NewRecorder(capacity: 4096);
            recorder.TryEnqueue(WriteJob("warm\n", name: "Warm"));
            while (recorder.Committed < 1) await Task.Delay(20); // repository init + first commit excluded below

            const int commits = 20;
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < commits; i++) recorder.TryEnqueue(WriteJob("line " + i + "\n" + new string('c', 20_000), name: "Obj" + (i % 5)));
            while (recorder.Committed < 1 + commits) await Task.Delay(5);
            sw.Stop();
            _output.WriteLine($"background commit (write file + git add + git commit, 20 KB part): {sw.Elapsed.TotalMilliseconds / commits:F1} ms per commit (avg of {commits})");

            // The same Worker envelope shaped with the feature off and on: the difference is the request-path cost.
            string source = new string('s', 100_000);
            var ctx = Context("genexus_edit", EditArgs(), _kb, "Demo");
            double Measure(KbHistoryRecorder? rec)
            {
                var previous = Program._kbHistory;
                Program._kbHistory = rec;
                try
                {
                    for (int i = 0; i < 20; i++) Program.ShapeWorkerToolResponse(new JObject { ["result"] = Receipt(source) }, ctx);
                    var watch = Stopwatch.StartNew();
                    for (int i = 0; i < 200; i++) Program.ShapeWorkerToolResponse(new JObject { ["result"] = Receipt(source) }, ctx);
                    return watch.Elapsed.TotalMilliseconds / 200;
                }
                finally { Program._kbHistory = previous; }
            }
            double off = Measure(null);
            double on = Measure(recorder);
            _output.WriteLine($"ShapeWorkerToolResponse (100 KB receipt): off {off:F3} ms, on {on:F3} ms, added {(on - off) * 1000:F0} us per call");
            await recorder.StopAsync(TimeSpan.Zero);
        }

        [Fact]
        public void RequestPathCostIsAnEnqueue()
        {
            var recorder = NewRecorder(git: Path.Combine(_temp, "never-started"), capacity: 4096);
            string source = new string('s', 100_000);
            var args = EditArgs();
            var receipt = Receipt(source);
            var ctx = Context("genexus_edit", args, _kb, "Demo");
            Program.TryRecordKbHistory(recorder, ctx, receipt); // warm-up (JIT, classifier tables)

            const int iterations = 1000;
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++) Program.TryRecordKbHistory(recorder, ctx, receipt);
            sw.Stop();

            double microseconds = sw.Elapsed.TotalMilliseconds * 1000 / iterations;
            _output.WriteLine($"capture+enqueue of a 100 KB receipt: {microseconds:F1} us per call (avg of {iterations})");
            Assert.True(microseconds < 2000, "request-path hook must stay far below one millisecond-scale cost");
        }
    }
}
