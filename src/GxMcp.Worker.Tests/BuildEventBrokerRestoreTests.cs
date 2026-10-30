using System;
using System.Reflection;
using Artech.Architecture.Common.Events;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Services;
using Microsoft.Build.Framework;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // Artech.MsBuild.Common.ArtechTask.EventsSuspended is not a per-task option: its setter writes the
    // process-global EventsService.Events switch, and none of BuildOne, BuildAll or IdeWebBuildAndDeploy
    // resets it afterwards (SDK 18 task assemblies). In-process Build and Rebuild suspend SDK events for
    // the duration of the task; before this guard they also left the Worker's broker suspended for the rest
    // of its life, so every later SDK callback (pattern engine, isolated Events save preflight, the next
    // Build All's reported prior state) ran against a switch the build had flipped (#696).
    [Collection("InProcessSdkReflection")]
    public class BuildEventBrokerRestoreTests
    {
        private static readonly BindingFlags Private = BindingFlags.Static | BindingFlags.NonPublic;

        [Theory]
        [InlineData(false, "success")]
        [InlineData(true, "success")]
        [InlineData(false, "false")]
        [InlineData(true, "false")]
        [InlineData(false, "throws")]
        [InlineData(true, "throws")]
        [InlineData(false, "missing")]
        [InlineData(true, "missing")]
        public void BuildOne_keeps_events_suspended_for_the_task_and_restores_the_broker(bool originallySuspended, string outcome)
        {
            var broker = EventsService.Events;
            using (var fixture = new BrokerWritingTaskFixture("_typeBuildOne", originallySuspended, outcome == "missing"))
            {
                bool suspendedDuringTask = false;
                var originalFactory = Artech.Genexus.Common.Commands.BuildCommandFactory.Factory;
                BrokerWritingTask.ExecuteCallback = () => { suspendedDuringTask = broker.EventsSuspended; BuildReorgPolicyTests.AssertBuildFactoryRefusesReorg(); return Result(outcome); };

                bool built = (bool)typeof(InProcessBuildRunner).GetMethod("ExecuteBuildOne", Private)
                    .Invoke(null, new object[] { new object(), "SyntheticObject", new InProcessBuildEngine((line, error) => { }), false });

                Assert.Equal(outcome == "success", built);
                // Dispatch is unchanged: the task still runs with SDK events suspended.
                Assert.True(outcome == "missing" || suspendedDuringTask);
                // What changes: the switch the task flipped is put back, whatever the outcome.
                Assert.Equal(originallySuspended, broker.EventsSuspended);
                Assert.Same(originalFactory, Artech.Genexus.Common.Commands.BuildCommandFactory.Factory);
            }
        }

        [Theory]
        [InlineData(false, false, "success")]
        [InlineData(true, false, "success")]
        [InlineData(false, true, "success")]
        [InlineData(false, false, "false")]
        [InlineData(true, true, "false")]
        [InlineData(false, false, "throws")]
        [InlineData(true, false, "throws")]
        [InlineData(false, false, "missing")]
        [InlineData(true, true, "missing")]
        public void IdeWebBuildAndDeploy_keeps_events_suspended_for_the_task_and_restores_the_broker(bool originallySuspended, bool forceRebuild, string outcome)
        {
            var broker = EventsService.Events;
            using (var fixture = new BrokerWritingTaskFixture("_typeIdeWebBuildAndDeploy", originallySuspended, outcome == "missing"))
            {
                bool suspendedDuringTask = false;
                BrokerWritingTask.ExecuteCallback = () => { suspendedDuringTask = broker.EventsSuspended; Assert.True(BrokerWritingTask.Last.FailIfReorg); Assert.True(BrokerWritingTask.Last.DoNotExecuteReorg); return Result(outcome); };

                bool built = (bool)typeof(InProcessBuildRunner).GetMethod("ExecuteIdeWebBuildAndDeploy", Private)
                    .Invoke(null, new object[] { new object(), new InProcessBuildEngine((line, error) => { }), forceRebuild });

                Assert.Equal(outcome == "success", built);
                Assert.True(outcome == "missing" || suspendedDuringTask);
                Assert.Equal(originallySuspended, broker.EventsSuspended);
            }
        }

        private static object Result(string outcome)
        {
            if (outcome == "throws") throw new InvalidOperationException("synthetic build failure");
            return outcome == "success";
        }

        // Models the SDK contract the production code has to live with: the EventsSuspended setter is a
        // write to the global broker, exactly as ArtechTask implements it.
        internal sealed class BrokerWritingTask
        {
            public static Func<object> ExecuteCallback;
            public static BrokerWritingTask Last;
            public bool FailIfReorg { get; set; }
            public bool DoNotExecuteReorg { get; set; }
            public object KB { get; set; }
            public string ObjectName { get; set; }
            public bool ForceRebuild { get; set; }
            public bool BuildCalled { get; set; }
            public bool CompileMains { get; set; }
            public string Output { get; set; }
            public IBuildEngine BuildEngine { get; set; }
            public bool EventsSuspended
            {
                get { return EventsService.Events.EventsSuspended; }
                set { EventsService.Events.EventsSuspended = value; }
            }
            public object Execute() { Last = this; return ExecuteCallback(); }
        }

        // Same property surface, no Execute entry point.
        internal sealed class BrokerWritingTaskWithoutExecute
        {
            public object KB { get; set; }
            public string ObjectName { get; set; }
            public bool BuildCalled { get; set; }
            public string Output { get; set; }
            public IBuildEngine BuildEngine { get; set; }
            public bool EventsSuspended
            {
                get { return EventsService.Events.EventsSuspended; }
                set { EventsService.Events.EventsSuspended = value; }
            }
        }

        // The reflected task types and the SDK broker are process-global: both are restored together.
        internal sealed class BrokerWritingTaskFixture : IDisposable
        {
            private readonly FieldInfo _field;
            private readonly object _originalType;
            private readonly bool _originalSuspended;

            internal BrokerWritingTaskFixture(string typeField, bool suspended, bool withoutExecute)
            {
                _field = typeof(InProcessBuildRunner).GetField(typeField, Private);
                _originalType = _field.GetValue(null);
                _originalSuspended = EventsService.Events.EventsSuspended;
                _field.SetValue(null, withoutExecute ? typeof(BrokerWritingTaskWithoutExecute) : typeof(BrokerWritingTask));
                EventsService.Events.EventsSuspended = suspended;
            }

            public void Dispose()
            {
                EventsService.Events.EventsSuspended = _originalSuspended;
                _field.SetValue(null, _originalType);
                BrokerWritingTask.ExecuteCallback = null;
            }
        }
    }
}
