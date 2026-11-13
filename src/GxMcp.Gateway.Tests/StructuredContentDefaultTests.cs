using System;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;
using Xunit.Abstractions;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// structuredContent duplicates every tool payload already present in content[0].text,
    /// so it is off by default. A tool that declares an outputSchema must still carry it
    /// (MCP spec), and both opt-in directions must work.
    /// </summary>
    [Collection("Gateway route state")]
    public class StructuredContentDefaultTests : IDisposable
    {
        private const string NoEnv = "GXMCP_NO_STRUCTURED_CONTENT";
        private const string EmitEnv = "GXMCP_EMIT_STRUCTURED_CONTENT";

        private readonly ITestOutputHelper _output;
        private readonly string? _previousNo = Environment.GetEnvironmentVariable(NoEnv);
        private readonly string? _previousEmit = Environment.GetEnvironmentVariable(EmitEnv);

        public StructuredContentDefaultTests(ITestOutputHelper output)
        {
            _output = output;
            SetEnv(null, null);
        }

        public void Dispose()
        {
            SetEnv(_previousNo, _previousEmit);
        }

        private static void SetEnv(string? no, string? emit)
        {
            Environment.SetEnvironmentVariable(NoEnv, no);
            Environment.SetEnvironmentVariable(EmitEnv, emit);
            Program.InvalidateEnvProbeCache();
        }

        private static JObject Build(JObject payload, string tool)
            => (JObject)Program.BuildToolTextResponse(new JValue("1"), payload, isError: false, toolName: tool)["result"]!;

        private static JObject SmallPayload() => new JObject { ["status"] = "ok", ["count"] = 1 };

        [Fact]
        public void ServerConfig_DefaultsToLeanResponses()
        {
            Assert.False(new ServerConfig().EmitStructuredContent);
        }

        [Fact]
        public void ToolWithoutOutputSchema_OmitsStructuredContentByDefault()
        {
            var result = Build(SmallPayload(), "genexus_read");

            Assert.Null(result["structuredContent"]);
            Assert.Equal("{\"status\":\"ok\",\"count\":1}", result["content"]![0]!["text"]!.ToString());
        }

        [Fact]
        public void ToolWithoutOutputSchema_EmitsStructuredContent_WhenEnabledByEnv()
        {
            SetEnv(null, "1");

            var result = Build(SmallPayload(), "genexus_read");

            Assert.Equal("ok", result["structuredContent"]?["status"]?.ToString());
        }

        [Fact]
        public void NoStructuredEnv_WinsOverEmitEnv()
        {
            SetEnv("1", "1");

            Assert.Null(Build(SmallPayload(), "genexus_read")["structuredContent"]);
        }

        [Fact]
        public void EveryToolDeclaringAnOutputSchema_GetsStructuredContent_RegardlessOfTheSetting()
        {
            var tools = (JArray)JObject.FromObject(McpRouter.Handle(new JObject
            {
                ["jsonrpc"] = "2.0", ["id"] = "1", ["method"] = "tools/list"
            })!)["tools"]!;
            var withSchema = tools.OfType<JObject>().Where(t => t["outputSchema"] != null)
                .Select(t => t["name"]!.ToString()).ToList();
            Assert.Contains("genexus_lifecycle", withSchema);

            foreach (string setting in new[] { null!, "1" })
            {
                SetEnv(setting, null); // default (lean) and GXMCP_NO_STRUCTURED_CONTENT=1
                foreach (string name in withSchema)
                    Assert.True(Build(SmallPayload(), name)["structuredContent"] is JObject,
                        name + " declares outputSchema and must carry structuredContent");

                var without = tools.OfType<JObject>().First(t => t["outputSchema"] == null)["name"]!.ToString();
                Assert.Null(Build(SmallPayload(), without)["structuredContent"]);
            }
        }

        [Fact]
        public void ErrorResults_NeverCarryStructuredContent()
        {
            SetEnv(null, "1");

            var result = (JObject)Program.BuildToolTextResponse(new JValue("1"),
                new JObject { ["code"] = "X" }, isError: true, toolName: "genexus_lifecycle")["result"]!;

            Assert.Null(result["structuredContent"]);
        }

        [Fact]
        public void PayloadBytes_WithAndWithoutStructuredContent()
        {
            var rows = new JArray();
            for (int i = 0; i < 25; i++)
                rows.Add(new JObject
                {
                    ["name"] = "TRN_CustomerInvoice" + i, ["type"] = "Transaction",
                    ["description"] = "Handles customer invoicing and line items for batch #" + i,
                    ["module"] = "Billing.Invoicing"
                });
            var bigRows = new JArray();
            for (int i = 0; i < 400; i++)
                bigRows.Add(new JObject
                {
                    ["name"] = "TRN_CustomerInvoice" + i, ["type"] = "Transaction",
                    ["description"] = "Handles customer invoicing, tax calculation and line items for invoice batch #" + i,
                    ["lastUpdate"] = "2026-09-02T20:00:00Z", ["module"] = "Billing.Invoicing"
                });
            var source = new StringBuilder();
            for (int i = 0; i < 120; i++)
                source.Append("For each Invoice\n    where InvoiceStatus = 'OPEN' and InvoiceId = &Id").Append(i).Append("\n    &Total += InvoiceAmount\nendfor\n");

            var cases = new (string Label, string Tool, JObject Payload)[]
            {
                ("status (whoami-sized)", "genexus_whoami", new JObject { ["status"] = "ok", ["kb"] = "KBTeste", ["version"] = "18" }),
                ("build report (real engine fixture)", "genexus_build", JObject.Parse(File.ReadAllText(FindUp("src", "GxMcp.Gateway.Tests", "Fixtures", "Engine", "rebuild_all_partial_kbtestefix16.json")))),
                ("query, 25 rows", "genexus_query", new JObject { ["status"] = "ok", ["count"] = rows.Count, ["objects"] = rows }),
                ("read source (~8 KB)", "genexus_read", new JObject { ["status"] = "ok", ["part"] = "Source", ["source"] = source.ToString() }),
                ("query, 400 rows", "genexus_query", new JObject { ["status"] = "ok", ["count"] = bigRows.Count, ["objects"] = bigRows }),
            };

            var table = new StringBuilder("\n| payload | bytes ON | bytes OFF | saved |\n|---|---:|---:|---:|\n");
            foreach (var (label, tool, payload) in cases)
            {
                SetEnv(null, "1");
                var on = Program.BuildToolTextResponse(new JValue("1"), (JObject)payload.DeepClone(), false, tool);
                SetEnv(null, null);
                var off = Program.BuildToolTextResponse(new JValue("1"), (JObject)payload.DeepClone(), false, tool);

                Assert.NotNull(on["result"]!["structuredContent"]);
                Assert.Null(off["result"]!["structuredContent"]);
                Assert.Equal(on["result"]!["content"]![0]!["text"]!.ToString(), off["result"]!["content"]![0]!["text"]!.ToString());

                int bytesOn = Encoding.UTF8.GetByteCount(on.ToString(Formatting.None));
                int bytesOff = Encoding.UTF8.GetByteCount(off.ToString(Formatting.None));
                Assert.True(bytesOff < bytesOn);
                table.Append($"| {label} | {bytesOn:N0} | {bytesOff:N0} | -{(1.0 - (double)bytesOff / bytesOn) * 100.0:F1}% |\n");
            }
            _output.WriteLine(table.ToString());
        }

        private static string FindUp(params string[] relativeSegments)
        {
            var dir = AppContext.BaseDirectory;
            for (int i = 0; i < 10; i++)
            {
                var candidate = Path.Combine(new[] { dir }.Concat(relativeSegments).ToArray());
                if (File.Exists(candidate)) return candidate;
                var parent = Directory.GetParent(dir);
                if (parent == null) break;
                dir = parent.FullName;
            }
            throw new FileNotFoundException("fixture not found: " + string.Join("/", relativeSegments));
        }
    }
}
