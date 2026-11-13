using System;
using System.IO;
using System.Net.Sockets;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class PortRecoveryTests
    {
        private const string Netstat = @"
  Proto  Local Address          Foreign Address        State           PID
  TCP    0.0.0.0:5000           0.0.0.0:0              LISTENING       111
  TCP    127.0.0.1:50001        0.0.0.0:0              LISTENING       222
  TCP    [::]:15000             [::]:0                 LISTENING       333
  TCP    [::1]:5000             [::]:0                 LISTENING       444
  TCP    10.0.0.5:6000          10.0.0.9:5000          ESTABLISHED     555
  TCP    127.0.0.1:5000         127.0.0.1:60000        ESTABLISHED     666
  UDP    0.0.0.0:5000           *:*                                    777
";

        [Fact]
        public void ParseListeningPids_MatchesLocalPortExactly()
        {
            var pids = Program.ParseListeningPidsForPort(Netstat, 5000);
            Assert.Equal(new[] { 111, 444 }, pids);
        }

        [Fact]
        public void ParseListeningPids_EmptyInput_ReturnsNone() =>
            Assert.Empty(Program.ParseListeningPidsForPort("", 5000));

        [Fact]
        public void AddressInUse_DetectedThroughInnerExceptions()
        {
            var inner = new SocketException((int)SocketError.AddressAlreadyInUse);
            Assert.True(Program.IsAddressInUseFailure(new IOException("bind", inner)));
            Assert.False(Program.IsAddressInUseFailure(new InvalidOperationException("boom")));
            Assert.False(Program.IsAddressInUseFailure(null));
        }
    }
}
