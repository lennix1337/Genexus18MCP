using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using GxMcp.Gateway;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    // Shares the Gateway's pending-request registry with the worker-lifecycle tests, so counts stay exact.
    [Collection("WorkerLifecycleSerial")]
    public class DoctorWwpTemplatesProbeTests
    {
        // The Worker's answer to the WwpTemplates action, in the shape DoctorService.WwpTemplatesResponse returns.
        private static string WorkerAnswer(int read, int invalid) => new JObject
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
                ["listed"] = new JArray(new JObject { ["name"] = "SynthTemplate", ["problem"] = "emptyType" }),
                ["severity"] = "high",
                ["hint"] = "Correct each listed template with genexus_properties set."
            }
        }.ToString();

        private static Task Answer(JObject rpc, string answerJson)
        {
            Assert.True(Program.CompleteDoctorWwpTemplatesForTest(rpc["id"]!.ToString(), answerJson));
            return Task.CompletedTask;
        }

        [Fact]
        public async Task Probe_without_an_open_kb_answers_KbNotOpen()
        {
            var block = await Program.ProbeDoctorWwpTemplatesAsync(null);

            Assert.False(block["available"]!.Value<bool>());
            Assert.Equal("KbNotOpen", block["reason"]!.ToString());
        }

        [Fact]
        public async Task Exchange_sends_the_template_action_and_returns_the_block_the_worker_answers()
        {
            JObject sent = null;

            var block = await Program.ExchangeDoctorWwpTemplatesAsync(
                rpc => { sent = rpc; return Answer(rpc, WorkerAnswer(read: 3, invalid: 1)); },
                TimeSpan.FromSeconds(5),
                sessionId: null);

            Assert.Equal("Doctor", sent["method"]!.ToString());
            Assert.Equal("WwpTemplates", sent["action"]!.ToString());
            Assert.True(block["available"]!.Value<bool>());
            Assert.Equal(3, block["read"]!.Value<int>());
            Assert.Equal(1, block["invalid"]!.Value<int>());
            Assert.Equal("high", block["severity"]!.ToString());
            Assert.Equal(0, Program.PendingRequestCountForTest);
        }

        [Fact]
        public async Task Exchange_answers_Timeout_and_forgets_the_request_when_the_worker_stays_silent()
        {
            var block = await Program.ExchangeDoctorWwpTemplatesAsync(
                rpc => Task.CompletedTask,
                TimeSpan.FromMilliseconds(50),
                sessionId: null);

            Assert.False(block["available"]!.Value<bool>());
            Assert.Equal("Timeout", block["reason"]!.ToString());
            Assert.Equal(50L, block["timeoutMs"]!.Value<long>());
            Assert.Equal(0, Program.PendingRequestCountForTest);
        }

        [Fact]
        public async Task Exchange_answers_WorkerError_and_forgets_the_request_when_the_send_throws()
        {
            var block = await Program.ExchangeDoctorWwpTemplatesAsync(
                rpc => throw new InvalidOperationException("pipe closed"),
                TimeSpan.FromSeconds(5),
                sessionId: null);

            Assert.False(block["available"]!.Value<bool>());
            Assert.Equal("WorkerError", block["reason"]!.ToString());
            Assert.Equal(0, Program.PendingRequestCountForTest);
        }

        [Fact]
        public async Task Exchange_answers_WorkerError_with_the_error_code_of_an_error_envelope()
        {
            var error = new JObject
            {
                ["status"] = "error",
                ["error"] = new JObject { ["code"] = "KbOpenFailed", ["message"] = "The KB could not be read." }
            }.ToString();

            var block = await Program.ExchangeDoctorWwpTemplatesAsync(
                rpc => Answer(rpc, error),
                TimeSpan.FromSeconds(5),
                sessionId: null);

            Assert.Equal("WorkerError", block["reason"]!.ToString());
            Assert.Equal("KbOpenFailed", block["errorCode"]!.ToString());
        }

        [Fact]
        public void Doctor_envelope_carries_the_template_block_and_warns_about_broken_templates()
        {
            var templates = Program.ParseWwpTemplatesAnswer(WorkerAnswer(read: 3, invalid: 1));

            var doctor = Program.BuildGatewayDoctorEnvelope(null, templates);
            var result = doctor["result"]!;

            Assert.Equal("ok", doctor["status"]!.ToString());
            Assert.Equal(1, result["wwpTemplates"]!["invalid"]!.Value<int>());
            Assert.Contains(result["warnings"]!.Select(w => w.ToString()), w => w.Contains("WorkWithPlus template(s)"));
            Assert.NotNull(result["kb"]);
            Assert.NotNull(result["worker"]);
            Assert.NotNull(result["engine"]);
        }

        [Fact]
        public void Doctor_envelope_with_an_unavailable_block_keeps_the_other_blocks_and_adds_no_template_warning()
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
        public void The_tool_result_handed_to_the_client_keeps_the_template_block()
        {
            var doctor = Program.BuildGatewayDoctorEnvelope(null, Program.ParseWwpTemplatesAnswer(WorkerAnswer(read: 3, invalid: 1)));

            var content = Program.BuildToolResultContent(doctor, false, "genexus_doctor", new JObject());
            var payload = JObject.Parse(content["content"]![0]!["text"]!.ToString());

            var block = payload["result"]!["wwpTemplates"]!;
            Assert.Equal(1, block["invalid"]!.Value<int>());
            Assert.Equal("high", block["severity"]!.ToString());
            Assert.Equal(1, ((JArray)block["listed"]!).Count);
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
        public void Doctor_envelope_keeps_the_kb_and_engine_blocks_whatever_the_template_answer()
        {
            var without = Program.BuildGatewayDoctorEnvelope(null, Program.UnavailableWwpTemplatesBlock("WorkerNotRunning"))["result"]!;
            var with = Program.BuildGatewayDoctorEnvelope(null, Program.ParseWwpTemplatesAnswer(WorkerAnswer(read: 3, invalid: 1)))["result"]!;

            Assert.Equal(without["kb"]!.ToString(), with["kb"]!.ToString());
            Assert.Equal(without["engine"]!.ToString(), with["engine"]!.ToString());
        }

        [Fact]
        public void Doctor_envelope_without_a_probe_reports_NotProbed()
        {
            var doctor = Program.BuildGatewayDoctorEnvelope(null);

            Assert.Equal("NotProbed", doctor["result"]!["wwpTemplates"]!["reason"]!.ToString());
        }
    }
}
