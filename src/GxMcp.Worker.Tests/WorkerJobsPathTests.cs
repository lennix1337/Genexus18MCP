using System;
using System.IO;
using GxMcp.Worker;
using GxMcp.Worker.Helpers;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public sealed class WorkerJobsPathTests
    {
        [Fact]
        public void JobsPathPrefersJobsDirectoryAndLeavesStateRootIndependent()
        {
            string previousJobsDirectory = Environment.GetEnvironmentVariable("GXMCP_JOBS_DIR");
            string previousStateDirectory = Environment.GetEnvironmentVariable("GXMCP_STATE_DIR");
            string jobsRoot = Path.Combine(Path.GetTempPath(), "gxmcp-jobs-path-" + Guid.NewGuid().ToString("N"));
            string stateRoot = Path.Combine(Path.GetTempPath(), "gxmcp-state-path-" + Guid.NewGuid().ToString("N"));

            try
            {
                Environment.SetEnvironmentVariable("GXMCP_JOBS_DIR", jobsRoot);
                Environment.SetEnvironmentVariable("GXMCP_STATE_DIR", stateRoot);

                Assert.Equal(
                    Path.GetFullPath(Path.Combine(jobsRoot, "jobs.json")),
                    Path.GetFullPath(Program.ResolveJobsFilePath()));
                Assert.Equal(Path.GetFullPath(stateRoot), RuntimePaths.StateRoot);

                Environment.SetEnvironmentVariable("GXMCP_JOBS_DIR", null);
                Assert.Equal(
                    Path.GetFullPath(Path.Combine(stateRoot, "jobs.json")),
                    Path.GetFullPath(Program.ResolveJobsFilePath()));
            }
            finally
            {
                Environment.SetEnvironmentVariable("GXMCP_JOBS_DIR", previousJobsDirectory);
                Environment.SetEnvironmentVariable("GXMCP_STATE_DIR", previousStateDirectory);
            }
        }
    }
}
