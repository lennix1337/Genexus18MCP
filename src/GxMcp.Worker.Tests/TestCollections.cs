using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using GxMcp.Worker.Services;
using Xunit;

// v2.6.9 — Worker tests share enough process-global state (Console.Error
// redirection in LoggerPhaseTagTests, the SDK's static type cache touched
// by PatternApplyServiceTests, the InProcessBuildEngine adapter, etc.) that
// cross-collection parallel execution intermittently surfaces a NRE in
// PatternApplyService.ApplyPatternToObject — different test classes racing
// on Console.Error swap during a Logger.Info call could leave a stale
// writer reference even though Logger wraps the write in try/catch (the
// NRE bubbles before the catch on some JIT paths). Pinning the whole
// assembly to serial execution adds ~5s to the suite (7s -> 12s) in
// exchange for deterministic green; xunit collection sharding alone was
// not enough because collections still run in parallel with each other.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]

namespace GxMcp.Worker.Tests
{
    // The collections below remain because they document intent at the class
    // level: with assembly-wide DisableTestParallelization the
    // DisableParallelization flags on the collections themselves are a no-op,
    // but the [Collection(...)] tags on the classes still group ownership.

    [CollectionDefinition("StderrCapture", DisableParallelization = true)]
    public class StderrCaptureCollection { }

    [CollectionDefinition("InProcessSdkReflection", DisableParallelization = true)]
    public class InProcessSdkReflectionCollection { }

    // BuildService starts background work and stores it in process-global registries.
    // Coverage instrumentation can keep that work alive into the next test even though
    // the suite itself is serial. Keep build-oriented test classes isolated from it.
    public abstract class BuildServiceTestBase : IDisposable
    {
        protected BuildServiceTestBase() => ResetBuildState();

        public void Dispose()
        {
            var timer = Stopwatch.StartNew();
            while (InFlight().Count > 0 && timer.Elapsed < TimeSpan.FromSeconds(3))
                Thread.Sleep(10);
            ResetBuildState();
        }

        private static void ResetBuildState()
        {
            BuildService.ResetBuildAdmissionForTest();
            Tasks().Clear();
            InFlight().Clear();
        }

        private static ConcurrentDictionary<string, BuildService.BuildTaskStatus> Tasks()
            => Registry("_tasks");

        private static ConcurrentDictionary<string, BuildService.BuildTaskStatus> InFlight()
            => Registry("_inFlightBuilds");

        private static ConcurrentDictionary<string, BuildService.BuildTaskStatus> Registry(string name)
        {
            var field = typeof(BuildService).GetField(name, BindingFlags.Static | BindingFlags.NonPublic);
            return (ConcurrentDictionary<string, BuildService.BuildTaskStatus>)field.GetValue(null);
        }
    }
}
