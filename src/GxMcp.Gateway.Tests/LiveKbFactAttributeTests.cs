using System;
using System.IO;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class LiveKbFactAttributeTests
    {
        [Theory]
        [InlineData(false, "GXMCP_TEAMDEV_PENDING_NAME")]
        [InlineData(true, "GXMCP_DSO_NAME")]
        public void Fixture_requirement_is_a_discovery_skip(bool designSystem, string pendingVariable)
        {
            const string kbVariable = "GXMCP_TEST_KB";
            string? previousKb = Environment.GetEnvironmentVariable(kbVariable);
            string? previousPending = Environment.GetEnvironmentVariable(pendingVariable);
            string tempKb = Path.Combine(Path.GetTempPath(), "gxmcp-live-attribute-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempKb);
            try
            {
                Environment.SetEnvironmentVariable(kbVariable, tempKb);
                Environment.SetEnvironmentVariable(pendingVariable, null);
                var attribute = new LiveKbFactAttribute(
                    requiresTeamDevelopmentFixture: !designSystem,
                    requiresDesignSystemFixture: designSystem);
                Assert.Contains(pendingVariable, attribute.Skip);
            }
            finally
            {
                Environment.SetEnvironmentVariable(kbVariable, previousKb);
                Environment.SetEnvironmentVariable(pendingVariable, previousPending);
                try { Directory.Delete(tempKb, recursive: true); } catch { }
            }
        }
    }
}
