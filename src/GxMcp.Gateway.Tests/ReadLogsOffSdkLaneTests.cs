using System;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// Issue #342, part one: <c>ReadLogs</c> is the route an agent reaches for when
    /// something has already gone wrong - a blocked SDK lane being the usual reason -
    /// yet it was dispatched through that very lane, so the one request that could
    /// explain the blockage waited behind the blockage.
    ///
    /// <para>
    /// The routing decision is the fix and it lives in the Worker, so these are
    /// source-shape assertions on the production whitelist. That is the honest limit:
    /// driving this needs a live Worker with a deliberately blocked STA thread, which
    /// this build cannot provide, and a fake would test the fake.
    /// </para>
    /// </summary>
    public class ReadLogsOffSdkLaneTests
    {
        private static string Dispatcher() => GxMcp.TestSupport.RepoSource.WithoutComments(
            "src", "GxMcp.Worker", "Services", "CommandDispatcher.cs");

        // Issue #854 moved the lane whitelist into CommandLaneClassifier; these two tests pin its source location, while CommandLaneClassifierEquivalenceTests pins the behavior.
        private static string Classifier() => GxMcp.TestSupport.RepoSource.WithoutComments(
            "src", "GxMcp.Worker", "Services", "CommandLaneClassifier.cs");

        [Fact]
        public void ReadLogs_Is_Whitelisted_For_The_Parallel_Dispatch_Path()
        {
            string source = Classifier();

            int whitelist = source.IndexOf(
                "method == \"object\" && action == \"ReadLogs\"", StringComparison.Ordinal);
            Assert.True(whitelist >= 0,
                "ReadLogs is not whitelisted, so a blocked SDK lane also blocks log diagnostics");

            // And the entry is inside IsThreadSafe, which is the method that decides the
            // lane. A match elsewhere in the file would not move it.
            int isThreadSafe = source.IndexOf("internal static bool IsThreadSafe(JObject request)", StringComparison.Ordinal);
            int methodEnd = isThreadSafe < 0 ? -1 : source.IndexOf("\n        }", isThreadSafe, StringComparison.Ordinal);
            Assert.True(isThreadSafe > 0 && methodEnd > isThreadSafe);
            Assert.True(whitelist > isThreadSafe && whitelist < methodEnd,
                "the ReadLogs whitelist entry is outside IsThreadSafe");

            // The dispatcher entry point the Worker loop calls must reach that method.
            string dispatcher = Dispatcher();
            int dispatcherIsThreadSafe = dispatcher.IndexOf("public bool IsThreadSafe(JObject request)", StringComparison.Ordinal);
            int nextMethod = dispatcher.IndexOf("public string Dispatch(", StringComparison.Ordinal);
            Assert.True(dispatcherIsThreadSafe > 0 && nextMethod > dispatcherIsThreadSafe);
            Assert.True(dispatcher.Substring(dispatcherIsThreadSafe, nextMethod - dispatcherIsThreadSafe)
                    .Contains("=> CommandLaneClassifier.IsThreadSafe(request);"),
                "CommandDispatcher.IsThreadSafe does not delegate to CommandLaneClassifier");
        }

        [Fact]
        public void The_Sdk_Lane_Probe_Is_Whitelisted_Too()
        {
            // Without this the gateway's second probe queues behind the blockage it
            // exists to detect, and the SDK dimension would read "unknown" forever - which
            // is a different way of not knowing anything.
            string source = Classifier();

            int whitelist = source.IndexOf(
                "method == \"system\" && action == \"GetSdkBusyStatus\"", StringComparison.Ordinal);
            Assert.True(whitelist >= 0,
                "GetSdkBusyStatus is not whitelisted, so the SDK-lane probe queues behind the blockage");
            int isThreadSafe = source.IndexOf("internal static bool IsThreadSafe(JObject request)", StringComparison.Ordinal);
            int methodEnd = isThreadSafe < 0 ? -1 : source.IndexOf("\n        }", isThreadSafe, StringComparison.Ordinal);
            Assert.True(isThreadSafe > 0 && methodEnd > isThreadSafe);
            Assert.True(whitelist > isThreadSafe && whitelist < methodEnd,
                "the GetSdkBusyStatus whitelist entry is outside IsThreadSafe");
        }

        [Fact]
        public void The_Sdk_Lane_Probe_Action_Is_Actually_Dispatched()
        {
            // The probe must resolve to the status, not to an unknown-action error -
            // otherwise the classifier only ever sees "unknown" and the whole distinction
            // is inert.
            string source = Dispatcher();

            Assert.Contains("[\"system\"] = Handle_System", source, StringComparison.Ordinal);
            Assert.Contains("Program.GetSdkBusyStatus()", source, StringComparison.Ordinal);
        }

        [Fact]
        public void The_Sdk_Status_Reports_Liveness_And_Not_Only_Busyness()
        {
            // "Active" alone cannot tell a slow build from a deadlocked call. The
            // progress fields are what make the distinction possible, so their absence
            // would leave the classifier with nothing to reason from.
            string program = GxMcp.TestSupport.RepoSource.WithoutComments(
                "src", "GxMcp.Worker", "Program.cs");

            int status = program.IndexOf("internal static JObject GetSdkBusyStatus", StringComparison.Ordinal);
            Assert.True(status > 0, "GetSdkBusyStatus not found");
            var window = program.Substring(status, Math.Min(2600, program.Length - status));

            Assert.Contains("sawProgress", window, StringComparison.Ordinal);
            Assert.Contains("lastProgressMs", window, StringComparison.Ordinal);
        }

        [Fact]
        public void Every_Progress_Emission_Records_Sdk_Liveness()
        {
            // The liveness signal has to come from somewhere real. ProgressEmitter is the
            // single point every progress notification passes through, so hooking it is
            // what makes "progressing" evidence rather than an assumption.
            string emitter = GxMcp.TestSupport.RepoSource.WithoutComments(
                "src", "GxMcp.Worker", "Helpers", "ProgressEmitter.cs");

            Assert.Contains("Program.NoteSdkProgress()", emitter, StringComparison.Ordinal);
        }

        [Fact]
        public void A_Stray_Progress_Emission_Cannot_Rescue_A_Later_Wedged_Command()
        {
            // If NoteSdkProgress recorded unconditionally, a progress line emitted between
            // two commands would make the *next* command look alive from its first
            // second, and a wedged SDK would be reported as progressing for as long as the
            // gate is.
            string program = GxMcp.TestSupport.RepoSource.WithoutComments(
                "src", "GxMcp.Worker", "Program.cs");

            int note = program.IndexOf("internal static void NoteSdkProgress()", StringComparison.Ordinal);
            Assert.True(note > 0, "NoteSdkProgress not found");
            var window = program.Substring(note, Math.Min(400, program.Length - note));

            Assert.Contains("if (_sdkBusy != 1) return;", window, StringComparison.Ordinal);
        }

        [Fact]
        public void Starting_A_Command_Resets_The_Liveness_Tick()
        {
            // Without the reset, a new command inherits the previous operation's last
            // progress timestamp and looks alive while it is already wedged.
            string program = GxMcp.TestSupport.RepoSource.WithoutComments(
                "src", "GxMcp.Worker", "Program.cs");

            int starts = CountOccurrences(program, "Interlocked.Exchange(ref _sdkLastProgressTicks, 0)");
            int busyMarks = CountOccurrences(program, "Interlocked.Exchange(ref _sdkBusySinceTicks, DateTime.UtcNow.Ticks)");

            Assert.Equal(busyMarks, starts);
            Assert.True(starts >= 2,
                "both the scheduled and the legacy command paths must reset the tick");
        }

        [Fact]
        public void The_Bounded_Read_Is_The_Only_Tail_Reader_In_The_Log_Route()
        {
            // The memory defect, pinned at its call site: a whole-file read here would
            // reintroduce it behind a passing unit test for LogTailReader.
            string objectService = GxMcp.TestSupport.RepoSource.WithoutComments(
                "src", "GxMcp.Worker", "Services", "ObjectService.cs");

            int read = objectService.IndexOf("public string ReadLogs(", StringComparison.Ordinal);
            Assert.True(read > 0, "ReadLogs not found");
            var window = ReadLogsBody(objectService, read);

            Assert.Contains("LogTailReader.Read(", window, StringComparison.Ordinal);
            // The old shape, asserted by mechanism rather than by a local variable's
            // name: reading the file line by line is exactly how the whole log ended up
            // in memory, and the bounded reader never does it.
            Assert.DoesNotContain("ReadLine(", window, StringComparison.Ordinal);
            Assert.DoesNotContain("StreamReader", window, StringComparison.Ordinal);
        }

        [Fact]
        public void The_Response_Reports_That_Filters_Saw_Only_The_Retained_Window()
        {
            // A bounded read can no longer see the whole log, so a caller must be able to
            // tell a complete answer from a tail. Without these fields a "since yesterday"
            // request silently returns a tail and reads as complete.
            string objectService = GxMcp.TestSupport.RepoSource.WithoutComments(
                "src", "GxMcp.Worker", "Services", "ObjectService.cs");

            int read = objectService.IndexOf("public string ReadLogs(", StringComparison.Ordinal);
            var window = ReadLogsBody(objectService, read);

            Assert.Contains("retainedLines", window, StringComparison.Ordinal);
            Assert.Contains("truncatedFromStart", window, StringComparison.Ordinal);
        }

        [Fact]
        public void A_Crash_Search_That_Missed_Inside_The_Window_Does_Not_Claim_No_Crash_Exists()
        {
            // "Not in the window we read" and "not in the log" are different answers, and
            // with a bounded read only the first is now possible.
            string objectService = GxMcp.TestSupport.RepoSource.WithoutComments(
                "src", "GxMcp.Worker", "Services", "ObjectService.cs");

            Assert.Contains("crashIndex < 0", objectService, StringComparison.Ordinal);
            int crash = objectService.IndexOf("result[\"crashLineIndex\"] = crashIndex;", StringComparison.Ordinal);
            Assert.True(crash > 0, "crash index reporting not found");
            var window = objectService.Substring(crash, Math.Min(1400, objectService.Length - crash));
            Assert.Contains("tailRead.Truncated", window, StringComparison.Ordinal);
        }

        /// <summary>
        /// The body of <c>ReadLogs</c>, bounded by the next method declaration. A fixed
        /// character window would be wrong in both directions - too small and a
        /// legitimate assertion cannot see its target, too large and it can match text
        /// belonging to a different method, which is how a source-shape guard ends up
        /// passing for the wrong reason.
        /// </summary>
        private static string ReadLogsBody(string source, int start)
        {
            const string signature = "public string ReadLogs(";
            int at = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(at >= 0, "ReadLogs not found");
            int end = source.IndexOf("\n        public ", at + signature.Length, StringComparison.Ordinal);
            Assert.True(end > at, "could not delimit ReadLogs");
            return source.Substring(at, end - at);
        }

        private static int CountOccurrences(string haystack, string needle)
        {
            int count = 0, at = 0;
            while ((at = haystack.IndexOf(needle, at, StringComparison.Ordinal)) >= 0)
            {
                count++;
                at += needle.Length;
            }
            return count;
        }
    }
}