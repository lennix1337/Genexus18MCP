using System;
using System.IO;
using System.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    // Mirror of GxMcp.Worker.Tests.LiveKbFactAttribute — same env-var contract
    // (GXMCP_TEST_KB to opt in; optional GXMCP_REQUIRE_WWP=1 for WWP-licensed
    // suites). Lives in this project so Gateway E2E tests can be skipped by
    // default on CI without referencing the worker test assembly.
    public sealed class LiveKbFactAttribute : FactAttribute
    {
        public LiveKbFactAttribute(bool requiresWWP = false, bool requiresNavigation = false)
        {
            string kb = Environment.GetEnvironmentVariable("GXMCP_TEST_KB");
            if (string.IsNullOrEmpty(kb))
            {
                Skip = "GXMCP_TEST_KB env var not set — set to a KB folder path to run live E2E tests.";
                return;
            }
            if (requiresWWP)
            {
                string wwp = Environment.GetEnvironmentVariable("GXMCP_REQUIRE_WWP");
                if (string.IsNullOrEmpty(wwp) || wwp == "0")
                {
                    Skip = "GXMCP_REQUIRE_WWP not set — set to 1 to run WorkWithPlus-licensed E2E tests.";
                }
                return;
            }
            if (requiresNavigation && !HasGeneratedNavigationReport(kb))
            {
                Skip = "GXMCP_TEST_KB has no generated procedure navigation report — run specification/build first to exercise navigation E2E coverage.";
            }
        }

        private static bool HasGeneratedNavigationReport(string kbPath)
        {
            try
            {
                return Directory.EnumerateDirectories(kbPath, "GXSPC*", SearchOption.TopDirectoryOnly)
                    .SelectMany(spec => Directory.EnumerateDirectories(spec, "GEN*", SearchOption.TopDirectoryOnly))
                    .Select(gen => Path.Combine(gen, "NVG"))
                    .Where(Directory.Exists)
                    .SelectMany(nvg => Directory.EnumerateFiles(nvg, "*.xml", SearchOption.TopDirectoryOnly))
                    .Any(file => !string.Equals(Path.GetFileName(file), "SubTypes.xml", StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                return false;
            }
        }
    }
}
