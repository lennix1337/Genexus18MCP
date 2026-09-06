using Microsoft.AspNetCore.Http;
using System.Collections.Generic;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// Unit tests for the /mcp HTTP auth primitives (Program.IsLoopbackBind,
    /// ConstantTimeEquals, IsHttpTokenValid). These guard the only auth boundary in
    /// front of a surface that grants full tool access (SDK writes, the gh shell-out,
    /// the AI-completion proxy that holds a live key), so a silent regression here —
    /// e.g. a branch that defaults to "allow" — must be caught by tests, not in prod.
    /// </summary>
    public class HttpTokenAuthTests
    {
        // ── IsLoopbackBind ───────────────────────────────────────────────────

        [Theory]
        [InlineData("127.0.0.1", true)]
        [InlineData("::1", true)]
        [InlineData("localhost", true)]
        [InlineData("LOCALHOST", true)]   // case-insensitive
        [InlineData(" localhost ", true)] // trimmed
        [InlineData("0.0.0.0", false)]
        [InlineData("192.168.1.10", false)]
        [InlineData("", false)]           // blank -> 0.0.0.0, not loopback
        [InlineData("   ", false)]
        [InlineData(null, false)]
        public void IsLoopbackBind_ClassifiesBindAddress(string? bind, bool expected)
        {
            Assert.Equal(expected, Program.IsLoopbackBind(bind!));
        }

        [Theory]
        [InlineData("localhost", "127.0.0.1", true)]
        [InlineData("127.0.0.1", "127.0.0.1", true)]
        [InlineData("[::1]", "::1", true)]
        [InlineData("::1", "::1", true)]
        [InlineData("evil.example", "127.0.0.1", false)]
        [InlineData("127.0.0.2", "127.0.0.1", false)]
        [InlineData("", "127.0.0.1", false)]
        public void IsLoopbackHostAllowed_RejectsDnsRebindingHost(string host, string bind, bool expected)
        {
            Assert.Equal(expected, Program.IsLoopbackHostAllowed(host, bind));
        }

        [Theory]
        [InlineData(null, true)]
        [InlineData("", true)]
        [InlineData("http://localhost:3000", true)]
        [InlineData("http://127.0.0.1:3000", true)]
        [InlineData("http://[::1]:3000", true)]
        [InlineData("https://evil.example", false)]
        [InlineData("not-an-origin", false)]
        public void IsOriginAllowed_UsesLoopbackAndExplicitAllowlist(string? origin, bool expected)
        {
            var config = new ServerConfig();
            Assert.Equal(expected, Program.IsOriginAllowed(origin, config));
        }

        [Fact]
        public void IsOriginAllowed_MatchesConfiguredOriginsCaseInsensitively()
        {
            var config = new ServerConfig
            {
                AllowedOrigins = new List<string> { "https://studio.example" }
            };

            Assert.True(Program.IsOriginAllowed("HTTPS://STUDIO.EXAMPLE", config));
            Assert.False(Program.IsOriginAllowed("https://other.example", config));
        }

        // ── ConstantTimeEquals ───────────────────────────────────────────────

        [Fact]
        public void ConstantTimeEquals_EqualStrings_True()
        {
            Assert.True(Program.ConstantTimeEquals("s3cr3t-token", "s3cr3t-token"));
        }

        [Theory]
        [InlineData("s3cr3t-token", "wrong-token")]      // same length, different content
        [InlineData("s3cr3t-token", "s3cr3t-token-xx")]  // different length
        [InlineData("s3cr3t-token", "")]                 // empty candidate
        [InlineData("", "s3cr3t-token")]                 // empty expected
        public void ConstantTimeEquals_Mismatches_False(string a, string b)
        {
            Assert.False(Program.ConstantTimeEquals(a, b));
        }

        [Theory]
        [InlineData(null, "x")]
        [InlineData("x", null)]
        [InlineData(null, null)]
        public void ConstantTimeEquals_Nulls_False(string? a, string? b)
        {
            Assert.False(Program.ConstantTimeEquals(a!, b!));
        }

        // ── IsHttpTokenValid ─────────────────────────────────────────────────

        private static HttpContext ContextWith(string? header, string? value)
        {
            var ctx = new DefaultHttpContext();
            if (header != null) ctx.Request.Headers[header] = value;
            return ctx;
        }

        [Fact]
        public void IsHttpTokenValid_BearerHeader_CorrectToken_True()
        {
            var ctx = ContextWith("Authorization", "Bearer the-token");
            Assert.True(Program.IsHttpTokenValid(ctx, "the-token"));
        }

        [Fact]
        public void IsHttpTokenValid_BearerHeader_CaseInsensitivePrefix_True()
        {
            var ctx = ContextWith("Authorization", "bearer the-token");
            Assert.True(Program.IsHttpTokenValid(ctx, "the-token"));
        }

        [Fact]
        public void IsHttpTokenValid_XGxmcpTokenHeader_CorrectToken_True()
        {
            var ctx = ContextWith("X-GXMCP-Token", "the-token");
            Assert.True(Program.IsHttpTokenValid(ctx, "the-token"));
        }

        [Fact]
        public void IsHttpTokenValid_WrongToken_False()
        {
            var ctx = ContextWith("Authorization", "Bearer not-the-token");
            Assert.False(Program.IsHttpTokenValid(ctx, "the-token"));
        }

        [Fact]
        public void IsHttpTokenValid_NoHeader_False()
        {
            var ctx = ContextWith(null, null);
            Assert.False(Program.IsHttpTokenValid(ctx, "the-token"));
        }

        [Fact]
        public void IsHttpTokenValid_EmptyBearerValue_False()
        {
            var ctx = ContextWith("Authorization", "Bearer ");
            Assert.False(Program.IsHttpTokenValid(ctx, "the-token"));
        }
    }
}
