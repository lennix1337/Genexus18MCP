#nullable enable
using System;
using System.IO;
using GxMcp.Common.SharedHost;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public sealed class SharedHostDiagnosticsRegistryTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "gx-diagnostics-" + Guid.NewGuid().ToString("N"));
        private const long StartTicks = 638000000000000000L;

        public SharedHostDiagnosticsRegistryTests() => Directory.CreateDirectory(_root);

        [Fact]
        public void MissingDirectory_IsAnEmptyRegistry()
        {
            Assert.Empty(SharedHostDiagnostics.ReadRegistryV2(new FixedProbe(ProcessProbeResult.NotRunning),
                Path.Combine(_root, "absent")));
        }

        [Theory]
        [InlineData("malformed")]
        [InlineData("unreadable")]
        [InlineData("unverified")]
        public void UncertainRegistry_DoesNotProduceAnExactCount(string failure)
        {
            string path = WriteRecord();
            if (failure == "malformed") File.WriteAllText(path, "{bad-json");
            using var held = failure == "unreadable"
                ? new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)
                : null;
            var process = failure == "unverified"
                ? ProcessProbeResult.RunningStartTimeUnreadable
                : ProcessProbeResult.Running(StartTicks);

            Assert.Throws<IOException>(() => SharedHostDiagnostics.ReadRegistryV2(new FixedProbe(process), _root));
        }

        [Fact]
        public void ReadableRecords_PreserveLiveAndStaleClassification()
        {
            WriteRecord();
            File.WriteAllText(Path.Combine(_root, "ignored.json.tmp-1"), "{bad-json");
            var live = Assert.Single(SharedHostDiagnostics.ReadRegistryV2(
                new FixedProbe(ProcessProbeResult.Running(StartTicks)), _root));
            Assert.Equal(RecordLiveness.Live, live.Liveness);
            Assert.Equal(102, live.Record.WorkerPid);
            var stale = Assert.Single(SharedHostDiagnostics.ReadRegistryV2(
                new FixedProbe(ProcessProbeResult.NotRunning), _root));
            Assert.Equal(RecordLiveness.Stale, stale.Liveness);
        }

        private string WriteRecord()
        {
            string path = Path.Combine(_root, "host.json");
            var record = new SharedHostRecord
            {
                KbKey = "test-key",
                HostPid = 101,
                HostStartTimeUtcTicks = StartTicks,
                WorkerPid = 102,
                State = SharedHostRecordState.Ready
            };
            File.WriteAllText(path, record.ToJson());
            return path;
        }

        private sealed class FixedProbe : IProcessProbe
        {
            private readonly ProcessProbeResult _result;
            internal FixedProbe(ProcessProbeResult result) => _result = result;
            public ProcessProbeResult Probe(int pid) => _result;
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
