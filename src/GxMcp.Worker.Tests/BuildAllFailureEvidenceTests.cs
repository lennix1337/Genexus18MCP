using System;
using System.Reflection;
using Artech.Architecture.Common.Events;
using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    [Collection("InProcessSdkReflection")]
    public class BuildAllFailureEvidenceTests
    {
        [Theory]
        [InlineData(false, "false", null, "did not report success")]
        [InlineData(true, "false", null, "did not report success")]
        [InlineData(false, "null", null, "did not report success")]
        [InlineData(true, "null", null, "did not report success")]
        [InlineData(false, "invalid", null, "did not report success")]
        [InlineData(true, "invalid", null, "did not report success")]
        [InlineData(false, "exception", null, "synthetic root cause")]
        [InlineData(true, "exception", null, "synthetic root cause")]
        [InlineData(false, "constructor", typeof(BuildAllTaskFixture.ConstructorFailureTask), "synthetic root cause")]
        [InlineData(true, "constructor", typeof(BuildAllTaskFixture.ConstructorFailureTask), "synthetic root cause")]
        [InlineData(false, "missing", typeof(BuildAllTaskFixture.MissingExecuteTask), "Execute method not found")]
        [InlineData(true, "missing", typeof(BuildAllTaskFixture.MissingExecuteTask), "Execute method not found")]
        public void Failures_reach_the_build_log_and_restore_broker(bool originallySuspended, string failure, Type taskType, string diagnostic)
        {
            var broker = EventsService.Events;
            var status = new BuildService.BuildTaskStatus { Action = "BuildAll" };
            var service = new BuildService();
            var handleLine = typeof(BuildService).GetMethod("HandleLine", BindingFlags.Instance | BindingFlags.NonPublic);
            int calls = 0;
            using (var task = new BuildAllTaskFixture(originallySuspended, taskType))
            {
                BuildAllTaskFixture.EventTask.ExecuteCallback = () =>
                {
                    calls++;
                    Assert.False(broker.EventsSuspended);
                    broker.EventsSuspended = true;
                    return FailureResult(failure);
                };
                Assert.False(task.Execute((line, error) => handleLine.Invoke(service, new object[] { status, line, error })));
                Assert.Equal(taskType == null ? 1 : 0, calls);
                Assert.Equal(originallySuspended, broker.EventsSuspended);
                Assert.Contains(diagnostic, status.FullOutput.ToString());
                Assert.Equal(1, status.ErrorCount);
                Assert.Contains(status.Errors, line => line.Contains(diagnostic));
            }
        }

        private static object FailureResult(string failure)
        {
            switch (failure)
            {
                case "exception": throw new TargetInvocationException(new InvalidOperationException("synthetic root cause"));
                case "null": return null;
                case "invalid": return "unexpected";
                default: return false;
            }
        }
    }
}
