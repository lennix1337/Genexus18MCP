using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // The tests below set GXMCP_EXPECTED_KB_PATH and GXMCP_EXPECTED_KB_VERSION, which are
    // process-wide. The assembly already runs serially; the collection documents the intent.
    [CollectionDefinition("CommandLaneClassifierEnvironment", DisableParallelization = true)]
    public sealed class CommandLaneClassifierEnvironmentCollection { }

    /// <summary>
    /// Pins the lane decision of <c>CommandDispatcher.IsThreadSafe(JObject)</c> before it is
    /// extracted into <c>CommandLaneClassifier</c> (issue #854, Requirements 3.8, 10.1, 13.9).
    /// Every row is evaluated under each write-destination pin state, because a configured
    /// pin restricts the parallel lane to the observations that <c>WriteDestinationGuard</c>
    /// lets bypass its fence.
    /// </summary>
    [Collection("CommandLaneClassifierEnvironment")]
    public sealed class CommandLaneClassifierEquivalenceTests
    {
        public enum PinState { None, Blank, PathOnly, VersionOnly, Both }

        private const string NullRequest = "<null>";
        private const string PinnedPath = @"C:\KBs\KbA";
        private const string PinnedVersion = "Development";

        // The two entry points under test: the dispatcher method the Worker loop calls, and
        // the SDK-free classifier the shared-host broker calls. Both must agree on every row.
        private static readonly Func<JObject, bool> DispatcherEntryPoint = request =>
            ((CommandDispatcher)FormatterServices.GetUninitializedObject(typeof(CommandDispatcher))).IsThreadSafe(request);

        private static readonly Func<JObject, bool> ClassifierEntryPoint = CommandLaneClassifier.IsThreadSafe;

        // Columns: request JSON, lane without pins, lane with a configured pin (either variable).
        internal static readonly (string Request, bool Unpinned, bool Pinned)[] Table =
        {
            // ping and health: always parallel unpinned; health is an observation only for GetReport.
            (@"{""method"":""ping""}", true, true),
            (@"{""method"":""ping"",""action"":""Anything""}", true, true),
            (@"{""method"":""PING""}", true, true),
            (@"{""method"":""Ping"",""action"":""Run""}", true, true),
            (@"{""method"":""health""}", true, false),
            (@"{""method"":""health"",""action"":""GetReport""}", true, true),
            (@"{""method"":""health"",""action"":""getreport""}", true, false),
            (@"{""method"":""Health"",""action"":""GetReport""}", true, true),
            (@"{""method"":""health"",""action"":""Unknown""}", true, false),

            // kbversion: only ActiveModel is parallel; the pin observation also depends on params.action.
            (@"{""method"":""kbversion"",""action"":""ActiveModel""}", true, true),
            (@"{""method"":""kbversion"",""action"":""activemodel""}", true, true),
            (@"{""method"":""KbVersion"",""action"":""ActiveModel""}", true, true),
            (@"{""method"":""kbversion"",""action"":""ActiveModel"",""params"":{""action"":""set_active""}}", true, true),
            (@"{""method"":""kbversion"",""action"":""activemodel"",""params"":{""action"":""set_active""}}", true, false),
            (@"{""method"":""kbversion"",""action"":""activemodel"",""params"":{""action"":""LIST""}}", true, true),
            (@"{""method"":""kbversion"",""action"":""activemodel"",""params"":""not-an-object""}", true, true),
            (@"{""method"":""kbversion"",""action"":""Run""}", false, false),
            (@"{""method"":""kbversion"",""action"":""Run"",""params"":{""action"":""list""}}", false, false),
            (@"{""method"":""kbversion""}", false, false),

            // search: parallel unless SearchSource (case-insensitive); pinned only Query.
            (@"{""method"":""search"",""action"":""Query""}", true, true),
            (@"{""method"":""search"",""action"":""query""}", true, false),
            (@"{""method"":""Search"",""action"":""Query""}", true, true),
            (@"{""method"":""search""}", true, false),
            (@"{""method"":""search"",""action"":""Unknown""}", true, false),
            (@"{""method"":""search"",""action"":""SearchSource""}", false, false),
            (@"{""method"":""search"",""action"":""searchsource""}", false, false),
            (@"{""method"":""search"",""action"":""SEARCHSOURCE""}", false, false),

            // list: every action parallel unpinned; pinned only Objects.
            (@"{""method"":""list"",""action"":""Objects""}", true, true),
            (@"{""method"":""LIST"",""action"":""Objects""}", true, true),
            (@"{""method"":""list"",""action"":""objects""}", true, false),
            (@"{""method"":""list""}", true, false),
            (@"{""method"":""list"",""action"":""Unknown""}", true, false),

            // control: only Cancel, case-sensitive.
            (@"{""method"":""control"",""action"":""Cancel""}", true, true),
            (@"{""method"":""Control"",""action"":""Cancel""}", true, true),
            (@"{""method"":""control"",""action"":""cancel""}", false, false),
            (@"{""method"":""control"",""action"":""Pause""}", false, false),
            (@"{""method"":""control""}", false, false),

            // kb: the three in-memory index reads, case-sensitive. Open is an observation but stays on STA.
            (@"{""method"":""kb"",""action"":""GetIndexStatus""}", true, true),
            (@"{""method"":""kb"",""action"":""GetIndexState""}", true, true),
            (@"{""method"":""kb"",""action"":""GetNameTypeMap""}", true, true),
            (@"{""method"":""KB"",""action"":""GetIndexStatus""}", true, true),
            (@"{""method"":""kb"",""action"":""getindexstatus""}", false, false),
            (@"{""method"":""kb"",""action"":""getindexstate""}", false, false),
            (@"{""method"":""kb"",""action"":""getnametypemap""}", false, false),
            (@"{""method"":""kb"",""action"":""Open""}", false, false),
            (@"{""method"":""kb"",""action"":""ListEnvironments""}", false, false),
            (@"{""method"":""kb"",""action"":""GetActiveEnvironment""}", false, false),
            (@"{""method"":""kb""}", false, false),

            // doctor: parallel except WwpTemplates (case-insensitive).
            (@"{""method"":""doctor""}", true, true),
            (@"{""method"":""doctor"",""action"":""Run""}", true, true),
            (@"{""method"":""Doctor"",""action"":""Report""}", true, true),
            (@"{""method"":""doctor"",""action"":""WwpTemplates""}", false, false),
            (@"{""method"":""doctor"",""action"":""wwptemplates""}", false, false),

            // build: only Status and Result, case-sensitive.
            (@"{""method"":""build"",""action"":""Status""}", true, true),
            (@"{""method"":""build"",""action"":""Result""}", true, true),
            (@"{""method"":""Build"",""action"":""Status""}", true, true),
            (@"{""method"":""build"",""action"":""status""}", false, false),
            (@"{""method"":""build"",""action"":""result""}", false, false),
            (@"{""method"":""build"",""action"":""Run""}", false, false),
            (@"{""method"":""build""}", false, false),

            // object: only ReadLogs, case-sensitive. Read is an observation but stays on STA.
            (@"{""method"":""object"",""action"":""ReadLogs""}", true, true),
            (@"{""method"":""Object"",""action"":""ReadLogs""}", true, true),
            (@"{""method"":""object"",""action"":""readlogs""}", false, false),
            (@"{""method"":""object"",""action"":""Read""}", false, false),

            // system: GetSdkBusyStatus is parallel, but it is not a pin observation.
            (@"{""method"":""system"",""action"":""GetSdkBusyStatus""}", true, false),
            (@"{""method"":""System"",""action"":""GetSdkBusyStatus""}", true, false),
            (@"{""method"":""system"",""action"":""getsdkbusystatus""}", false, false),
            (@"{""method"":""system""}", false, false),

            // Observations and writes that touch the SDK stay on STA in both states.
            (@"{""method"":""read"",""action"":""ExtractSource""}", false, false),
            (@"{""method"":""read"",""action"":""ExtractFullObject""}", false, false),
            (@"{""method"":""write"",""action"":""Source""}", false, false),
            (@"{""method"":""write"",""action"":""Source"",""params"":{""dryRun"":true}}", false, false),
            (@"{""method"":""batch"",""action"":""Run""}", false, false),
            (@"{""method"":""layout"",""action"":""GetTree""}", false, false),
            (@"{""method"":""module"",""action"":""Run""}", false, false),
            (@"{""method"":""sdkprobe"",""action"":""Read""}", false, false),
            (@"{""method"":""unknown"",""action"":""Cancel""}", false, false),

            // Missing, empty, or non-string method and the null request.
            (@"{}", false, false),
            (@"{""action"":""Cancel""}", false, false),
            (@"{""method"":"""",""action"":""Cancel""}", false, false),
            (@"{""method"":null,""action"":""Cancel""}", false, false),
            (@"{""method"":123}", false, false),
            (NullRequest, false, false),
        };

        public static IEnumerable<object[]> Cases()
        {
            foreach (PinState pins in Enum.GetValues(typeof(PinState)))
                foreach (var row in Table)
                    yield return new object[] { pins, row.Request, IsConfigured(pins) ? row.Pinned : row.Unpinned };
        }

        [Theory]
        [MemberData(nameof(Cases))]
        public void Dispatcher_lane_decision_matches_the_pinned_table(PinState pins, string request, bool expected)
        {
            using (new PinScope(pins))
            {
                Assert.Equal(expected, DispatcherEntryPoint(Parse(request)));
            }
        }

        [Theory]
        [MemberData(nameof(Cases))]
        public void Classifier_lane_decision_matches_the_pinned_table(PinState pins, string request, bool expected)
        {
            using (new PinScope(pins))
            {
                Assert.Equal(expected, ClassifierEntryPoint(Parse(request)));
            }
        }

        [Theory]
        [MemberData(nameof(Cases))]
        public void Dispatcher_and_classifier_give_identical_lane_decisions(PinState pins, string request, bool expected)
        {
            _ = expected;
            using (new PinScope(pins))
            {
                Assert.Equal(DispatcherEntryPoint(Parse(request)), ClassifierEntryPoint(Parse(request)));
            }
        }

        [Fact]
        public void Table_covers_both_lanes_in_both_pin_states()
        {
            int unpinnedSafe = 0, pinnedSafe = 0;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in Table)
            {
                Assert.True(seen.Add(row.Request), "Duplicate row: " + row.Request);
                if (row.Unpinned) unpinnedSafe++;
                if (row.Pinned) pinnedSafe++;
                // A pin only narrows the parallel lane; it never widens it.
                Assert.False(row.Pinned && !row.Unpinned, "Pinned lane wider than unpinned: " + row.Request);
            }
            Assert.InRange(unpinnedSafe, 1, Table.Length - 1);
            Assert.InRange(pinnedSafe, 1, unpinnedSafe - 1);
        }

        private static JObject Parse(string request)
            => request == NullRequest ? null : JObject.Parse(request);

        private static bool IsConfigured(PinState pins)
            => pins == PinState.PathOnly || pins == PinState.VersionOnly || pins == PinState.Both;

        private sealed class PinScope : IDisposable
        {
            private readonly string _path = Environment.GetEnvironmentVariable(WriteDestinationGuard.PathVariable);
            private readonly string _version = Environment.GetEnvironmentVariable(WriteDestinationGuard.VersionVariable);

            internal PinScope(PinState pins)
            {
                string path = null, version = null;
                switch (pins)
                {
                    case PinState.Blank: path = "   "; version = "\t"; break;
                    case PinState.PathOnly: path = PinnedPath; break;
                    case PinState.VersionOnly: version = PinnedVersion; break;
                    case PinState.Both: path = PinnedPath; version = PinnedVersion; break;
                }
                Environment.SetEnvironmentVariable(WriteDestinationGuard.PathVariable, path);
                Environment.SetEnvironmentVariable(WriteDestinationGuard.VersionVariable, version);
                Assert.Equal(IsConfigured(pins), WriteDestinationGuard.IsConfigured);
            }

            public void Dispose()
            {
                Environment.SetEnvironmentVariable(WriteDestinationGuard.PathVariable, _path);
                Environment.SetEnvironmentVariable(WriteDestinationGuard.VersionVariable, _version);
            }
        }
    }
}
