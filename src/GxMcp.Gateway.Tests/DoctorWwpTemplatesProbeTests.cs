using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using GxMcp.Gateway;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    // Shares the Gateway's pending-request registry with the worker-lifecycle tests, so counts stay exact.
    [Collection("WorkerLifecycleSerial")]
    public class DoctorWwpTemplatesProbeTests
    {
        private const string SynthTemplateName = "SynthTemplate";

        // The command envelope the Worker writes for the WwpTemplates action (DoctorService.WwpTemplatesResponse).
        private static JObject OkEnvelope(int read, int invalid) => new JObject
        {
            ["status"] = "ok",
            ["code"] = "DoctorWwpTemplatesOk",
            ["result"] = new JObject
            {
                ["available"] = true,
                ["read"] = read,
                ["invalid"] = invalid,
                ["unreadable"] = 0,
                ["listLimit"] = 5,
                ["listed"] = new JArray(new JObject { ["name"] = SynthTemplateName, ["problem"] = "emptyType" }),
                ["severity"] = "high",
                ["hint"] = "Correct each listed template with genexus_properties set."
            }
        };

        // The reply frame the Worker writes: the JSON-RPC envelope whose result is the command envelope.
        private static string ReplyFrame(string id, JObject commandEnvelope) => new JObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["result"] = commandEnvelope
        }.ToString(Formatting.None);

        // Answers a probe the way the Worker does: the frame goes through the production reply router.
        private static Func<JObject, Task> RouteReply(Func<string, string> frameFor) => rpc =>
        {
            Program.RouteWorkerReplyForTest(frameFor(rpc["id"]!.ToString()));
            return Task.CompletedTask;
        };

        [Fact]
        public async Task Probe_without_an_open_kb_answers_KbNotOpen()
        {
            var block = await Program.ProbeDoctorWwpTemplatesAsync(null);

            Assert.False(block["available"]!.Value<bool>());
            Assert.Equal("KbNotOpen", block["reason"]!.ToString());
        }

        [Fact]
        public async Task Probe_with_a_session_kb_and_no_worker_pool_answers_WorkerNotRunning()
        {
            // With nothing serving the session's KB, the probe gives up; it does not start a Worker.
            var field = typeof(Program).GetField("_currentKb", BindingFlags.NonPublic | BindingFlags.Static);
            var currentKb = (AsyncLocal<KbHandle?>)field!.GetValue(null)!;
            currentKb.Value = new KbHandle("synthkb", "C:\\SynthKb");
            try
            {
                var block = await Program.ProbeDoctorWwpTemplatesAsync(null);

                Assert.False(block["available"]!.Value<bool>());
                Assert.Equal("WorkerNotRunning", block["reason"]!.ToString());
            }
            finally
            {
                currentKb.Value = null;
            }
        }

        [Fact]
        public void Probe_waits_at_most_four_seconds()
        {
            Assert.Equal(TimeSpan.FromSeconds(4), Program.DoctorWwpTemplatesBudget);
        }

        [Fact]
        public async Task The_command_is_the_template_action_with_a_busy_wait_below_the_budget()
        {
            JObject? sent = null;

            await Program.ExchangeDoctorWwpTemplatesAsync(
                rpc =>
                {
                    sent = rpc;
                    return RouteReply(id => ReplyFrame(id, OkEnvelope(read: 0, invalid: 0)))(rpc);
                },
                Program.DoctorWwpTemplatesBudget,
                sessionId: null);

            Assert.Equal("Doctor", sent!["method"]!.ToString());
            Assert.Equal("WwpTemplates", sent["params"]!["action"]!.ToString());
            int busyWait = sent["params"]!["busyWaitMs"]!.Value<int>();
            Assert.True(busyWait > 0 && busyWait < Program.DoctorWwpTemplatesBudget.TotalMilliseconds);
        }

        [Fact]
        public async Task A_reply_routed_by_the_reply_router_becomes_the_template_block()
        {
            var block = await Program.ExchangeDoctorWwpTemplatesAsync(
                RouteReply(id => ReplyFrame(id, OkEnvelope(read: 3, invalid: 1))),
                TimeSpan.FromSeconds(5),
                sessionId: null);

            Assert.True(block["available"]!.Value<bool>());
            Assert.Equal(3, block["read"]!.Value<int>());
            Assert.Equal(1, block["invalid"]!.Value<int>());
            Assert.Equal("high", block["severity"]!.ToString());
            Assert.Single((JArray)block["listed"]!);
            Assert.Equal(0, Program.PendingRequestCountForTest);
        }

        [Fact]
        public async Task A_busy_worker_refusal_answers_WorkerBusy()
        {
            var refusal = new JObject
            {
                ["status"] = "error",
                ["error"] = new JObject { ["code"] = "WorkerBusy", ["message"] = "The Worker SDK thread is busy." }
            };

            var block = await Program.ExchangeDoctorWwpTemplatesAsync(
                RouteReply(id => ReplyFrame(id, refusal)),
                TimeSpan.FromSeconds(5),
                sessionId: null);

            Assert.Equal("WorkerBusy", block["reason"]!.ToString());
        }

        [Fact]
        public async Task A_command_error_keeps_its_code()
        {
            var error = new JObject
            {
                ["status"] = "error",
                ["error"] = new JObject { ["code"] = "KbOpenFailed", ["message"] = "The KB could not be read." }
            };

            var block = await Program.ExchangeDoctorWwpTemplatesAsync(
                RouteReply(id => ReplyFrame(id, error)),
                TimeSpan.FromSeconds(5),
                sessionId: null);

            Assert.Equal("WorkerError", block["reason"]!.ToString());
            Assert.Equal("KbOpenFailed", block["errorCode"]!.ToString());
        }

        [Fact]
        public async Task A_JSON_RPC_error_on_the_frame_answers_WorkerError_with_its_code()
        {
            var block = await Program.ExchangeDoctorWwpTemplatesAsync(
                rpc =>
                {
                    Program.RouteWorkerReplyForTest(new JObject
                    {
                        ["jsonrpc"] = "2.0",
                        ["id"] = rpc["id"]!.ToString(),
                        ["error"] = new JObject { ["code"] = -32000, ["message"] = "The Worker could not run the command." }
                    }.ToString(Formatting.None));
                    return Task.CompletedTask;
                },
                TimeSpan.FromSeconds(5),
                sessionId: null);

            Assert.Equal("WorkerError", block["reason"]!.ToString());
            Assert.Equal("-32000", block["errorCode"]!.ToString());
        }

        [Fact]
        public async Task A_reply_that_is_not_the_block_answers_UnexpectedReply()
        {
            // An older Worker ignores the action and answers the whole doctor envelope.
            var olderDoctor = new JObject
            {
                ["status"] = "ok",
                ["code"] = "DoctorOk",
                ["result"] = new JObject { ["checkedAt"] = "2026-01-01T00:00:00Z", ["geneXus"] = new JObject() }
            };

            var block = await Program.ExchangeDoctorWwpTemplatesAsync(
                RouteReply(id => ReplyFrame(id, olderDoctor)),
                TimeSpan.FromSeconds(5),
                sessionId: null);

            Assert.Equal("WorkerError", block["reason"]!.ToString());
            Assert.Equal("UnexpectedReply", block["errorCode"]!.ToString());
            Assert.Null(block["geneXus"]);
        }

        [Fact]
        public async Task A_silent_worker_times_out_and_the_request_is_forgotten()
        {
            var block = await Program.ExchangeDoctorWwpTemplatesAsync(
                rpc => Task.CompletedTask,
                TimeSpan.FromMilliseconds(50),
                sessionId: null);

            Assert.Equal("Timeout", block["reason"]!.ToString());
            Assert.Equal(50L, block["timeoutMs"]!.Value<long>());
            Assert.Equal(0, Program.PendingRequestCountForTest);
        }

        [Fact]
        public async Task The_send_stays_inside_the_budget()
        {
            // A send that never completes must not hold the probe past its budget.
            var exchange = Program.ExchangeDoctorWwpTemplatesAsync(
                rpc => new TaskCompletionSource<bool>().Task,
                TimeSpan.FromMilliseconds(100),
                sessionId: null);

            var first = await Task.WhenAny(exchange, Task.Delay(TimeSpan.FromSeconds(3)));

            Assert.True(ReferenceEquals(first, exchange), "the probe must answer within its budget");
            Assert.Equal("Timeout", (await exchange)["reason"]!.ToString());
        }

        [Fact]
        public async Task A_failed_send_answers_WorkerError_and_the_request_is_forgotten()
        {
            var block = await Program.ExchangeDoctorWwpTemplatesAsync(
                rpc => throw new InvalidOperationException("pipe closed"),
                TimeSpan.FromSeconds(5),
                sessionId: null);

            Assert.Equal("WorkerError", block["reason"]!.ToString());
            Assert.Equal(0, Program.PendingRequestCountForTest);
        }

        [Fact]
        public async Task A_full_command_queue_answers_WorkerBusy()
        {
            var block = await Program.ExchangeDoctorWwpTemplatesAsync(
                rpc => throw new WorkerCommandQueueFullException("synthkb", "synth-command", "Doctor", 4, 5000),
                TimeSpan.FromSeconds(5),
                sessionId: null);

            Assert.Equal("WorkerBusy", block["reason"]!.ToString());
        }

        [Fact]
        public void The_envelope_carries_the_block_and_one_warning_for_the_broken_templates()
        {
            var block = Program.ParseWwpTemplatesAnswer(ReplyFrame("synth-id", OkEnvelope(read: 3, invalid: 1)));

            var doctor = Program.BuildGatewayDoctorEnvelope(null, block);
            var result = doctor["result"]!;

            Assert.Equal("ok", doctor["status"]!.ToString());
            Assert.Equal(1, result["wwpTemplates"]!["invalid"]!.Value<int>());
            Assert.Single(result["warnings"]!.Select(w => w.ToString()).Where(w => w.Contains("WorkWithPlus template(s)")));
            Assert.NotNull(result["kb"]);
            Assert.NotNull(result["worker"]);
            Assert.NotNull(result["engine"]);
        }

        [Fact]
        public void An_unavailable_block_keeps_the_kb_and_engine_blocks_and_adds_no_template_warning()
        {
            var doctor = Program.BuildGatewayDoctorEnvelope(null, Program.UnavailableWwpTemplatesBlock("WorkerNotRunning"));
            var result = doctor["result"]!;

            Assert.Equal("WorkerNotRunning", result["wwpTemplates"]!["reason"]!.ToString());
            Assert.DoesNotContain(result["warnings"]!.Select(w => w.ToString()), w => w.Contains("WorkWithPlus template(s)"));
            Assert.NotNull(result["kb"]);
            Assert.NotNull(result["worker"]);
            Assert.NotNull(result["engine"]);
        }

        [Fact]
        public void The_kb_and_engine_blocks_do_not_change_with_the_template_answer()
        {
            var without = Program.BuildGatewayDoctorEnvelope(null, Program.UnavailableWwpTemplatesBlock("WorkerNotRunning"))["result"]!;
            var with = Program.BuildGatewayDoctorEnvelope(null, Program.ParseWwpTemplatesAnswer(ReplyFrame("synth-id", OkEnvelope(read: 3, invalid: 1))))["result"]!;

            Assert.Equal(without["kb"]!.ToString(), with["kb"]!.ToString());
            Assert.Equal(without["engine"]!.ToString(), with["engine"]!.ToString());
        }

        [Fact]
        public void The_tool_result_handed_to_the_client_keeps_the_template_block()
        {
            var doctor = Program.BuildGatewayDoctorEnvelope(null, Program.ParseWwpTemplatesAnswer(ReplyFrame("synth-id", OkEnvelope(read: 3, invalid: 1))));

            var content = Program.BuildToolResultContent(doctor, false, "genexus_doctor", new JObject());
            var payload = JObject.Parse(content["content"]![0]!["text"]!.ToString());

            var block = payload["result"]!["wwpTemplates"]!;
            Assert.Equal(1, block["invalid"]!.Value<int>());
            Assert.Equal("high", block["severity"]!.ToString());
            Assert.Single((JArray)block["listed"]!);
        }

        [Fact]
        public void Doctor_envelope_without_a_probe_reports_NotProbed()
        {
            var doctor = Program.BuildGatewayDoctorEnvelope(null);

            Assert.Equal("NotProbed", doctor["result"]!["wwpTemplates"]!["reason"]!.ToString());
        }
    }
}
