using System;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public sealed class WorkerTelemetryTests
    {
        [Fact]
        public void ComputeQueueWaitMs_ReportsElapsedTransportTime()
        {
            var queued = new DateTimeOffset(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);
            var now = queued.UtcDateTime.AddMilliseconds(137);

            long elapsed = GxMcp.Worker.Program.ComputeQueueWaitMs(
                new JValue(queued.ToString("O")), now);

            Assert.Equal(137L, elapsed);
        }

        [Theory]
        [InlineData("not-a-timestamp")]
        [InlineData("")]
        public void ComputeQueueWaitMs_InvalidTimestampFailsClosed(string value)
        {
            long elapsed = GxMcp.Worker.Program.ComputeQueueWaitMs(
                new JValue(value), DateTime.UtcNow);

            Assert.Equal(0L, elapsed);
        }

        [Fact]
        public void ComputeQueueWaitMs_DoesNotReportFutureTime()
        {
            var now = new DateTime(2026, 9, 5, 12, 0, 0, DateTimeKind.Utc);
            var future = now.AddSeconds(2).ToString("O");

            Assert.Equal(0L, GxMcp.Worker.Program.ComputeQueueWaitMs(new JValue(future), now));
        }
    }
}
