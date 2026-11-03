using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Xunit;
using GxMcp.Gateway;

namespace GxMcp.Gateway.Tests
{
    // #830 item 2: a failed build's status/result must stay readable at any size, and the full log
    // must be pageable by line. These tests pin the page contract and the path binding (#656).
    public sealed class LifecycleLogPageTests : IDisposable
    {
        private const string TaskId = "abcdef12";
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "gxmcp-830-log-" + Guid.NewGuid().ToString("N"));

        public LifecycleLogPageTests() => Directory.CreateDirectory(_directory);

        public void Dispose()
        {
            try { Directory.Delete(_directory, true); } catch { }
        }

        private static JobEntry Job(string status = "failed", string? workerTaskId = TaskId)
            => new JobEntry { Id = Guid.NewGuid().ToString("N"), Kind = "lifecycle/build", Status = status, WorkerTaskId = workerTaskId };

        private string WriteLog(string fileName, int lines)
        {
            string path = Path.Combine(_directory, fileName);
            File.WriteAllText(path, string.Join("\r\n", Enumerable.Range(0, lines).Select(i => "line " + i)));
            return path;
        }

        private static JObject Args(params (string Name, JToken Value)[] values)
        {
            var args = new JObject();
            foreach (var (name, value) in values) args[name] = value;
            return args;
        }

        [Fact]
        public void FullLog_PagesByLineWithTotalsAndNextOffset()
        {
            string path = WriteLog("build-" + TaskId + ".log", 250);
            var result = new JObject { ["FullLogPath"] = path };

            var page = LifecycleLogPage.Build(Job(), result, Args(("log_offset", 0), ("log_limit", 100)));

            Assert.True(page["available"]!.Value<bool>());
            Assert.Equal("fullLog", page["source"]!.ToString());
            Assert.Equal(250, page["totalLines"]!.Value<int>());
            Assert.Equal(100, page["returned"]!.Value<int>());
            Assert.Equal(100, page["nextOffset"]!.Value<int>());
            Assert.Equal("line 0", page["lines"]![0]!.ToString());
        }

        [Fact]
        public void FullLog_LastPageHasNoNextOffset()
        {
            string path = WriteLog("build-" + TaskId + ".log", 250);
            var result = new JObject { ["FullLogPath"] = path };

            var page = LifecycleLogPage.Build(Job(), result, Args(("log_offset", 200), ("log_limit", 100)));

            Assert.Equal(50, page["returned"]!.Value<int>());
            Assert.Equal(JTokenType.Null, page["nextOffset"]!.Type);
            Assert.Equal("line 200", page["lines"]![0]!.ToString());
            Assert.Equal("line 249", page["lines"]![49]!.ToString());
        }

        [Fact]
        public void FullLog_OffsetPastTheEnd_ReturnsEmptyPageWithTotals()
        {
            string path = WriteLog("build-" + TaskId + ".log", 3);

            var page = LifecycleLogPage.Build(Job(), new JObject { ["FullLogPath"] = path }, Args(("log_offset", 10)));

            Assert.Equal(0, page["returned"]!.Value<int>());
            Assert.Equal(3, page["totalLines"]!.Value<int>());
            Assert.Equal(JTokenType.Null, page["nextOffset"]!.Type);
        }

        [Fact]
        public void FullLog_LimitIsClampedToMaximum()
        {
            string path = WriteLog("build-" + TaskId + ".log", 500);

            var page = LifecycleLogPage.Build(Job(), new JObject { ["FullLogPath"] = path }, Args(("log_limit", 100000)));

            Assert.Equal(LifecycleLogPage.MaxLimit, page["returned"]!.Value<int>());
            Assert.Equal(LifecycleLogPage.MaxLimit, page["limit"]!.Value<int>());
        }

        [Fact]
        public void FullLog_LongLineIsCapped()
        {
            string path = Path.Combine(_directory, "build-" + TaskId + ".log");
            File.WriteAllText(path, new string('x', 5000));

            var page = LifecycleLogPage.Build(Job(), new JObject { ["FullLogPath"] = path }, Args(("log_limit", 1)));

            string line = page["lines"]![0]!.ToString();
            Assert.True(line.Length <= LifecycleResponseShaper.LineCap + 32, "line length " + line.Length);
            Assert.Contains("[+4700 chars]", line);
        }

        // Another task's log must never be opened through a stored path (#656).
        [Fact]
        public void LogOfAnotherTask_IsNotReadEvenWhenItExists()
        {
            string foreign = WriteLog("build-ffffffff.log", 5);

            var page = LifecycleLogPage.Build(
                Job(),
                new JObject { ["FullLogPath"] = foreign, ["TailLines"] = new JArray("own tail") },
                Args());

            Assert.Equal("tailLines", page["source"]!.ToString());
            Assert.Equal("own tail", page["lines"]![0]!.ToString());
            Assert.Equal(1, page["totalLines"]!.Value<int>());
        }

        [Fact]
        public void MissingFullLog_FallsBackToKeptTailAndSaysSo()
        {
            var result = new JObject
            {
                ["FullLogPath"] = Path.Combine(_directory, "build-" + TaskId + ".log"),
                ["TailLines"] = new JArray("t0", "t1", "t2")
            };

            var page = LifecycleLogPage.Build(Job(), result, Args(("log_offset", 1)));

            Assert.Equal("tailLines", page["source"]!.ToString());
            Assert.False(page["fullLogAvailable"]!.Value<bool>());
            Assert.Equal("t1", page["lines"]![0]!.ToString());
            Assert.Equal(3, page["totalLines"]!.Value<int>());
        }

        [Fact]
        public void NoStoredLog_IsReportedAsUnavailable()
        {
            var page = LifecycleLogPage.Build(Job(), new JObject { ["Status"] = "Failed" }, Args());

            Assert.False(page["available"]!.Value<bool>());
            Assert.Contains("No log", page["reason"]!.ToString());
        }

        [Fact]
        public void RunningTask_HasNoLogYet()
        {
            var page = LifecycleLogPage.Build(Job(status: "running"), new JObject(), Args());

            Assert.False(page["available"]!.Value<bool>());
            Assert.Contains("not finished", page["reason"]!.ToString());
        }

        [Theory]
        [InlineData("-1")]
        [InlineData("\"abc\"")]
        public void InvalidOffset_IsRefusedWithAReason(string rawValue)
        {
            var args = JObject.Parse("{\"log_offset\":" + rawValue + "}");

            var page = LifecycleLogPage.Build(Job(), new JObject { ["TailLines"] = new JArray("x") }, args);

            Assert.False(page["available"]!.Value<bool>());
            Assert.Contains("log_offset", page["reason"]!.ToString());
        }

        [Fact]
        public void ZeroLimit_IsRefusedWithAReason()
        {
            var page = LifecycleLogPage.Build(Job(), new JObject { ["TailLines"] = new JArray("x") }, Args(("log_limit", 0)));

            Assert.False(page["available"]!.Value<bool>());
            Assert.Contains("log_limit", page["reason"]!.ToString());
        }

        // #830 item 2: the shaped error list is what a caller reads when the raw log is cut off.
        [Fact]
        public void CompactErrors_EachLineCapped_TotalCountKept()
        {
            var errors = new JArray();
            errors.Add(new string('e', 2000));
            for (int i = 1; i < 12; i++) errors.Add("short error " + i);
            var raw = new JObject
            {
                ["Status"] = "Failed",
                ["ErrorCount"] = 40,
                ["Errors"] = errors,
                ["TailLines"] = new JArray(new string('t', 5000))
            };

            var compact = LifecycleResponseShaper.CompactObject(raw);

            Assert.Equal(40, compact["errorCount"]!.Value<int>());
            Assert.Equal(40, compact["totalErrors"]!.Value<int>());
            Assert.Equal(10, compact["shownErrors"]!.Value<int>());
            Assert.Equal(30, compact["omittedErrors"]!.Value<int>());
            var first = compact["errors"]![0]!.ToString();
            Assert.True(first.Length <= LifecycleResponseShaper.LineCap + 32, "error length " + first.Length);
            Assert.StartsWith(new string('e', LifecycleResponseShaper.LineCap), first);
            Assert.Null(compact["TailLines"]);
        }

        [Fact]
        public void CompactErrors_ErrorBlockComesBeforeTheWarningsAndSummary()
        {
            var raw = new JObject
            {
                ["Status"] = "Failed",
                ["ErrorCount"] = 2,
                ["Errors"] = new JArray("a(1,1): error CS0103: x", "b(2,2): error CS0103: y"),
                ["Warnings"] = new JArray()
            };

            var names = LifecycleResponseShaper.CompactObject(raw).Properties().Select(p => p.Name).ToList();

            Assert.True(names.IndexOf("errors") < names.IndexOf("warnings"));
            Assert.True(names.IndexOf("errors") < names.IndexOf("summary"));
            Assert.True(names.IndexOf("errorCount") < 3);
        }

        [Fact]
        public void IsBuildTaskPayload_RecognisesBuildShapesOnly()
        {
            Assert.True(LifecycleResponseShaper.IsBuildTaskPayload(new JObject { ["Status"] = "Failed", ["ErrorCount"] = 1 }));
            Assert.True(LifecycleResponseShaper.IsBuildTaskPayload(new JObject { ["errorCount"] = 0, ["errors"] = new JArray() }));
            Assert.False(LifecycleResponseShaper.IsBuildTaskPayload(new JObject { ["status"] = "ok", ["persisted"] = false }));
            Assert.False(LifecycleResponseShaper.IsBuildTaskPayload(null));
        }
    }
}
