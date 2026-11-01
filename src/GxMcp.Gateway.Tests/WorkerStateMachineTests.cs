using System;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class WorkerStateMachineTests
    {
        [Fact]
        public void InitialState_IsUnstarted_WithNoStopReason()
        {
            var sm = new WorkerStateMachine();
            Assert.Equal(WorkerLifecycleState.Unstarted, sm.CurrentState);
            Assert.Equal(WorkerStopReason.None, sm.StopReason);
            Assert.False(sm.IsStarting);
            Assert.False(sm.IsOperational);
            Assert.False(sm.IsStoppingOrStopped);
        }

        [Fact]
        public void NormalLifecycle_TransitionsFollowSingleSourceOfTruth()
        {
            var sm = new WorkerStateMachine();

            sm.TransitionTo(WorkerLifecycleState.Starting);
            Assert.True(sm.IsStarting);
            Assert.False(sm.IsOperational);

            sm.TransitionTo(WorkerLifecycleState.PipeConnecting);
            Assert.True(sm.IsStarting);

            sm.TransitionTo(WorkerLifecycleState.SdkInitializing);
            Assert.True(sm.IsStarting);

            sm.TransitionTo(WorkerLifecycleState.Ready);
            Assert.False(sm.IsStarting);
            Assert.True(sm.IsOperational);

            sm.TransitionTo(WorkerLifecycleState.Busy);
            Assert.True(sm.IsOperational);

            sm.TransitionTo(WorkerLifecycleState.Ready);
            Assert.True(sm.IsOperational);

            sm.TransitionTo(WorkerLifecycleState.Stopping, WorkerStopReason.IdleTimeout);
            Assert.True(sm.IsStoppingOrStopped);
            Assert.False(sm.IsOperational);
            Assert.Equal(WorkerStopReason.IdleTimeout, sm.StopReason);

            sm.TransitionTo(WorkerLifecycleState.Exited);
            Assert.Equal(WorkerLifecycleState.Exited, sm.CurrentState);
            Assert.True(sm.IsStoppingOrStopped);
        }

        [Fact]
        public void TerminalState_PreventsOperationalReentry_AllowsDispose()
        {
            var sm = new WorkerStateMachine();
            sm.TransitionTo(WorkerLifecycleState.Exited, WorkerStopReason.GatewayShutdown);

            sm.TransitionTo(WorkerLifecycleState.Ready);
            Assert.Equal(WorkerLifecycleState.Exited, sm.CurrentState);

            sm.TransitionTo(WorkerLifecycleState.Disposed);
            Assert.Equal(WorkerLifecycleState.Disposed, sm.CurrentState);

            sm.TransitionTo(WorkerLifecycleState.Starting);
            Assert.Equal(WorkerLifecycleState.Disposed, sm.CurrentState);
        }

        [Fact]
        public void SupervisionTracker_TracksInFlight_AndWedgedDetection()
        {
            var tracker = new WorkerSupervisionTracker();
            Assert.Equal(0, tracker.InFlightCount);
            Assert.False(tracker.HasWedgedCommand(TimeSpan.FromMinutes(15), out _));

            tracker.TrackInFlightStart("cmd-1", DateTime.UtcNow.AddMinutes(-20));
            Assert.Equal(1, tracker.InFlightCount);
            Assert.True(tracker.HasWedgedCommand(TimeSpan.FromMinutes(15), out var age));
            Assert.True(age.TotalMinutes >= 15);

            Assert.True(tracker.CompleteInFlight("cmd-1"));
            Assert.Equal(0, tracker.InFlightCount);
            Assert.False(tracker.HasWedgedCommand(TimeSpan.FromMinutes(15), out _));
        }

        [Fact]
        public void SupervisionTracker_ShouldStopForIdle_GuardsActiveConditions()
        {
            var tracker = new WorkerSupervisionTracker();
            var idle = TimeSpan.FromMinutes(10);
            var grace = TimeSpan.FromSeconds(90);

            tracker.LastActivityUtc = DateTime.UtcNow.AddMinutes(-15);
            Assert.True(tracker.ShouldStopForIdle(idle, queuedCommands: 0, inFlightCommands: 0, isStarting: false, grace));

            // In-flight commands block idle reap
            Assert.False(tracker.ShouldStopForIdle(idle, queuedCommands: 0, inFlightCommands: 1, isStarting: false, grace));

            // Starting state blocks idle reap
            Assert.False(tracker.ShouldStopForIdle(idle, queuedCommands: 0, inFlightCommands: 0, isStarting: true, grace));

            // Fresh background activity blocks idle reap
            tracker.MarkBuildActive(DateTime.UtcNow.AddSeconds(-30));
            Assert.False(tracker.ShouldStopForIdle(idle, queuedCommands: 0, inFlightCommands: 0, isStarting: false, grace));
        }

        [Fact]
        public void WorkerProcess_IntegratesStateMachine_AndSupervisionTracker()
        {
            var worker = new WorkerProcess(new Configuration(), new KbHandle("test", "C:\\fake"));
            Assert.NotNull(worker.StateMachine);
            Assert.NotNull(worker.SupervisionTracker);
            Assert.Equal(WorkerLifecycleState.Unstarted, worker.State);

            worker.StopProcessForTest(WorkerStopReason.GatewayShutdown);
            Assert.True(worker.IsStopping);
            Assert.Equal(WorkerLifecycleState.Exited, worker.State);
            Assert.Equal(WorkerStopReason.GatewayShutdown, worker.StateMachine.StopReason);
        }
    }
}
