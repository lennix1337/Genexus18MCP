using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using GxMcp.Common.SharedHost;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // The broker's registry v2 record writer must replace the record in one step. With File.Replace a Gateway that
    // read the record during a write found it absent or unreadable, and ConnectV2 refused a concurrent open with
    // KB_WORKER_OWNED_ELSEWHERE / liveness-unverified (Requirement 2 criterion 2, SharedHostProcessHarnessTests).
    public sealed class SharedHostFileRecordWriterTests
    {
        [Fact]
        public void A_concurrent_reader_never_sees_the_record_absent_or_unreadable_while_the_broker_rewrites_it()
        {
            string root = Path.Combine(Path.GetTempPath(), "gxmcp-recordwriter-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                string path = Path.Combine(root, new string('a', 64) + ".json");
                var writer = new SharedHostFileRecordWriter(path);
                var record = new SharedHostRecord
                {
                    KbKey = new string('a', 64),
                    HostPid = 1234,
                    HostStartTimeUtcTicks = 5678,
                    PipeName = "gxmcp-test",
                    Generation = 1,
                    State = SharedHostRecordState.Ready,
                    UpdatedUtc = DateTime.UtcNow
                };
                writer.Write(record);

                int reads = 0, writes = 0;
                string firstFailure = null;
                using (var stop = new CancellationTokenSource())
                {
                    var writing = Task.Run(() =>
                    {
                        while (!stop.IsCancellationRequested)
                        {
                            record.Generation++;
                            writer.Write(record);
                            writes++;
                        }
                    });
                    var clock = Stopwatch.StartNew();
                    while (clock.ElapsedMilliseconds < 1000)
                    {
                        var read = RecordReadResult.ReadFile(path);
                        reads++;
                        if (read.Status != RecordReadStatus.Parsed && firstFailure == null)
                            firstFailure = "read " + reads + ": " + read.Status + " " + read.Error;
                    }
                    stop.Cancel();
                    writing.Wait();
                }

                Assert.True(writes > 50 && reads > 50, "too little overlap: writes=" + writes + " reads=" + reads);
                Assert.True(firstFailure == null, firstFailure + " (writes=" + writes + ", reads=" + reads + ")");
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
