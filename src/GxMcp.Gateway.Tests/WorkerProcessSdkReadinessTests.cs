using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class WorkerProcessSdkReadinessTests
    {
        private static WorkerProcess NewWorker() =>
            new WorkerProcess(new Configuration(), new KbHandle("test", "C:\\fake\\path"));

        [Fact]
        public void RpcError_DoesNotSignalSdkReady()
        {
            var worker = NewWorker();
            worker.HandleWorkerRpcResponseForTest("{\"jsonrpc\":\"2.0\",\"id\":\"1\",\"error\":{\"code\":-32000,\"message\":\"SDK failed\"}}");
            Assert.False(worker.IsSdkReady);
        }

        [Fact]
        public void ValidRpcSuccess_SignalsSdkReady()
        {
            var worker = NewWorker();
            worker.HandleWorkerRpcResponseForTest("{\"jsonrpc\":\"2.0\",\"id\":\"1\",\"result\":{\"ok\":true}}");
            Assert.True(worker.IsSdkReady);
        }

        [Fact]
        public void SdkReadyNotification_SignalsSdkReady()
        {
            var worker = NewWorker();
            worker.HandleWorkerRpcResponseForTest("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/worker/sdk_ready\"}");
            Assert.True(worker.IsSdkReady);
        }
    }
}
