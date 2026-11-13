using System;
using System.Net.Http;
using System.Threading.Tasks;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    // Legacy stdio->HTTP proxy hardening and stdio shutdown drain.
    public class ProxyHardeningTests
    {
        [Fact]
        public void ProxyRequestTimeout_ExceedsServerLongPollCap()
        {
            // A timeout below the server's long-poll made the proxy abort and re-send a call
            // the master was still executing.
            Assert.True(Program.ProxyRequestTimeout > TimeSpan.FromSeconds(McpRouter.MaxLongPollSeconds));
        }

        [Fact]
        public void ProxyIsConnectFailure_OnlyForConnectionEstablishment()
        {
            Assert.True(Program.ProxyIsConnectFailure(new HttpRequestException(HttpRequestError.ConnectionError, "refused")));
            Assert.False(Program.ProxyIsConnectFailure(new TaskCanceledException("timeout")));
            Assert.False(Program.ProxyIsConnectFailure(new Exception("Empty response from master")));
            Assert.False(Program.ProxyIsConnectFailure(new HttpRequestException(HttpRequestError.ResponseEnded, "reset")));
        }

        [Fact]
        public void ApplyProxyAuthHeader_ForwardsTokenWhenSet()
        {
            string? prior = Environment.GetEnvironmentVariable("GXMCP_HTTP_TOKEN");
            try
            {
                Environment.SetEnvironmentVariable("GXMCP_HTTP_TOKEN", "s3cret");
                using var client = new HttpClient();
                Program.ApplyProxyAuthHeader(client.DefaultRequestHeaders);
                Assert.True(client.DefaultRequestHeaders.TryGetValues("X-GXMCP-Token", out var values));
                Assert.Equal("s3cret", Assert.Single(values));

                Environment.SetEnvironmentVariable("GXMCP_HTTP_TOKEN", null);
                using var bare = new HttpClient();
                Program.ApplyProxyAuthHeader(bare.DefaultRequestHeaders);
                Assert.False(bare.DefaultRequestHeaders.Contains("X-GXMCP-Token"));
            }
            finally { Environment.SetEnvironmentVariable("GXMCP_HTTP_TOKEN", prior); }
        }

        [Fact]
        public async Task DrainStdioInFlight_WaitsForInFlightRequestThenReturns()
        {
            System.Threading.Interlocked.Increment(ref Program._stdioInFlight);
            try
            {
                var drain = Program.DrainStdioInFlightAsync(TimeSpan.FromSeconds(5));
                await Task.Delay(150);
                Assert.False(drain.IsCompleted);
                System.Threading.Interlocked.Decrement(ref Program._stdioInFlight);
                Assert.True(await drain);
            }
            finally
            {
                if (Program._stdioInFlight > 0) System.Threading.Interlocked.Decrement(ref Program._stdioInFlight);
            }
        }

        [Fact]
        public async Task DrainStdioInFlight_GivesUpAfterTimeout()
        {
            System.Threading.Interlocked.Increment(ref Program._stdioInFlight);
            try { Assert.False(await Program.DrainStdioInFlightAsync(TimeSpan.FromMilliseconds(120))); }
            finally { System.Threading.Interlocked.Decrement(ref Program._stdioInFlight); }
        }
    }
}
