using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using GxMcp.Gateway;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;
using Xunit.Abstractions;

namespace GxMcp.Gateway.Tests
{
    public class PayloadSerializationBenchmarkTests
    {
        private readonly ITestOutputHelper _output;

        public PayloadSerializationBenchmarkTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void Benchmark_PayloadSerialization_And_StructuredContent()
        {
            // Create a realistic 100KB payload (e.g. list_objects or query result)
            var rows = new JArray();
            for (int i = 0; i < 400; i++)
            {
                rows.Add(new JObject
                {
                    ["name"] = "TRN_CustomerInvoice" + i,
                    ["type"] = "Transaction",
                    ["description"] = "Handles customer invoicing, tax calculation and line items for invoice batch #" + i,
                    ["lastUpdate"] = "2026-09-02T20:00:00Z",
                    ["module"] = "Billing.Invoicing"
                });
            }
            var payload = new JObject
            {
                ["status"] = "ok",
                ["count"] = rows.Count,
                ["objects"] = rows
            };

            // Measure Payload Byte Size:
            // 1. With structuredContent
            Environment.SetEnvironmentVariable("GXMCP_NO_STRUCTURED_CONTENT", null);
            Environment.SetEnvironmentVariable("GXMCP_EMIT_STRUCTURED_CONTENT", "1");
            Environment.SetEnvironmentVariable("GXMCP_TERSE", null);
            Program.InvalidateEnvProbeCache();
            var respWithStructured = Program.BuildToolTextResponse(new JValue("1"), payload, false, "genexus_query");
            string jsonWithStructured = respWithStructured.ToString(Formatting.None);
            int bytesWithStructured = Encoding.UTF8.GetByteCount(jsonWithStructured);

            // 2. Without structuredContent
            Environment.SetEnvironmentVariable("GXMCP_NO_STRUCTURED_CONTENT", "1");
            Program.InvalidateEnvProbeCache();
            var respWithoutStructured = Program.BuildToolTextResponse(new JValue("1"), payload, false, "genexus_query");
            string jsonWithoutStructured = respWithoutStructured.ToString(Formatting.None);
            int bytesWithoutStructured = Encoding.UTF8.GetByteCount(jsonWithoutStructured);

            // 3. Terse (without structuredContent + no legal actions/tokens)
            Environment.SetEnvironmentVariable("GXMCP_TERSE", "1");
            Program.InvalidateEnvProbeCache();
            var respTerse = Program.BuildToolTextResponse(new JValue("1"), payload, false, "genexus_query");
            string jsonTerse = respTerse.ToString(Formatting.None);
            int bytesTerse = Encoding.UTF8.GetByteCount(jsonTerse);

            // Reset env
            Environment.SetEnvironmentVariable("GXMCP_NO_STRUCTURED_CONTENT", null);
            Environment.SetEnvironmentVariable("GXMCP_EMIT_STRUCTURED_CONTENT", null);
            Environment.SetEnvironmentVariable("GXMCP_TERSE", null);
            Program.InvalidateEnvProbeCache();

            // Benchmark Direct Stream Writing vs ToString
            int iters = 1000;
            var ms = new MemoryStream();
            var writer = new StreamWriter(ms, Encoding.UTF8);

            // ANTES: rpc.ToString() then WriteLine
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            int gen0Start = GC.CollectionCount(0);
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < iters; i++)
            {
                ms.SetLength(0);
                string str = payload.ToString(Formatting.None);
                writer.WriteLine(str);
                writer.Flush();
            }
            sw.Stop();
            int gen0ToString = GC.CollectionCount(0) - gen0Start;
            double toStringUs = (sw.Elapsed.TotalMilliseconds * 1000.0) / iters;

            // DEPOIS: payload.WriteTo(jsonWriter) directly to stream
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            gen0Start = GC.CollectionCount(0);
            sw.Restart();
            for (int i = 0; i < iters; i++)
            {
                ms.SetLength(0);
                using (var jw = new JsonTextWriter(writer) { CloseOutput = false })
                {
                    payload.WriteTo(jw);
                }
                writer.WriteLine();
                writer.Flush();
            }
            sw.Stop();
            int gen0Stream = GC.CollectionCount(0) - gen0Start;
            double streamUs = (sw.Elapsed.TotalMilliseconds * 1000.0) / iters;

            string report = $@"
=== PAYLOAD_AND_SERIALIZATION_BENCHMARK ===
1. PAYLOAD SIZE COMPARISON:
   - Full with structuredContent: {bytesWithStructured:N0} bytes (100%)
   - Without structuredContent:    {bytesWithoutStructured:N0} bytes ({(double)bytesWithoutStructured / bytesWithStructured * 100.0:F1}%) -> ECONOMIA DE {bytesWithStructured - bytesWithoutStructured:N0} bytes (-{(1.0 - (double)bytesWithoutStructured / bytesWithStructured) * 100.0:F1}%)
   - Terse Mode:                   {bytesTerse:N0} bytes ({(double)bytesTerse / bytesWithStructured * 100.0:F1}%) -> ECONOMIA DE {bytesWithStructured - bytesTerse:N0} bytes (-{(1.0 - (double)bytesTerse / bytesWithStructured) * 100.0:F1}%)

2. PIPE SERIALIZATION SPEED & ALLOCATION (1,000 ops on ~70KB JSON):
   - ANTES (rpc.ToString() -> WriteLine):   {toStringUs:F2} us/op, Gen0 Collections: {gen0ToString}
   - DEPOIS (Direct WriteTo(JsonTextWriter)): {streamUs:F2} us/op, Gen0 Collections: {gen0Stream}
===========================================";
            _output.WriteLine(report);
            Console.WriteLine(report);
        }
    }
}
