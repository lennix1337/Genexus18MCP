using System.Security.Cryptography;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class SelfUpdaterChecksumTests
    {
        private static readonly byte[] Zip = { 1, 2, 3, 4 };

        private static string Hex(byte[] b) => System.Convert.ToHexString(SHA256.HashData(b));

        [Fact]
        public void MissingChecksum_RefusesToStage() =>
            Assert.False(SelfUpdater.ChecksumAllowsStaging(null, Zip, "9.9.9"));

        [Fact]
        public void MismatchedChecksum_RefusesToStage() =>
            Assert.False(SelfUpdater.ChecksumAllowsStaging(Hex(new byte[] { 9 }), Zip, "9.9.9"));

        [Fact]
        public void MatchingChecksum_Allows() =>
            Assert.True(SelfUpdater.ChecksumAllowsStaging(Hex(Zip).ToLowerInvariant(), Zip, "9.9.9"));
    }
}
