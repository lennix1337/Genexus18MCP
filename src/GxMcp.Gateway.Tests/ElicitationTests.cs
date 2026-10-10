using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    [Collection("Gateway route state")]
    public sealed class ElicitationTests
    {
        [Theory]
        [InlineData("genexus_delete_object", "{\"name\":\"OldPanel\",\"type\":\"WebPanel\"}", true)]
        [InlineData("genexus_delete_object", "{\"name\":\"OldPanel\",\"dryRun\":true}", false)]
        [InlineData("genexus_data_view", "{\"action\":\"delete\",\"transaction\":\"T\",\"dataViewName\":\"DV\"}", true)]
        [InlineData("genexus_data_view", "{\"action\":\"inspect\",\"transaction\":\"T\",\"dataViewName\":\"DV\"}", false)]
        [InlineData("genexus_transfer", "{\"action\":\"import\",\"file\":\"C:/a.xpz\",\"dryRun\":false}", true)]
        [InlineData("genexus_transfer", "{\"action\":\"import\",\"file\":\"C:/a.xpz\"}", false)]
        [InlineData("genexus_deploy", "{\"action\":\"deploy\"}", true)]
        [InlineData("genexus_deploy", "{\"action\":\"list_targets\"}", false)]
        [InlineData("genexus_read", "{\"name\":\"OldPanel\"}", false)]
        public void DestructivePolicy_GatesOnlyIrreversibleCalls(string tool, string args, bool gated)
        {
            Assert.Equal(gated, Program.DescribeDestructiveOperation(tool, JObject.Parse(args)) != null);
        }

        [Theory]
        [InlineData(null, "Auto")]
        [InlineData("off", "Off")]
        [InlineData("0", "Off")]
        [InlineData("strict", "Strict")]
        [InlineData("auto", "Auto")]
        public void Mode_ParsesEnvironmentValue(string? raw, string expected)
        {
            Assert.Equal(expected, ElicitationBroker.ParseMode(raw).ToString());
        }

        [Fact]
        public void UnknownResponseIds_AreNotIntercepted()
        {
            Assert.False(ElicitationBroker.TryCompleteResponse(JObject.Parse("{\"jsonrpc\":\"2.0\",\"id\":\"gxmcp-elicit-999999\",\"result\":{}}")));
            Assert.False(ElicitationBroker.TryCompleteResponse(JObject.Parse("{\"jsonrpc\":\"2.0\",\"id\":7,\"result\":{}}")));
            Assert.False(ElicitationBroker.TryCompleteResponse(JObject.Parse("{\"jsonrpc\":\"2.0\",\"id\":\"gxmcp-elicit-1\",\"method\":\"ping\"}")));
        }

        [Fact]
        public async Task DeleteWithoutConfirm_HumanDeclines_NothingReachesTheWorker()
        {
            using var fixture = ElicitationFixture.Create(answer: _ => new JObject { ["action"] = "decline" });

            var response = await fixture.CallToolAsync("genexus_delete_object", new JObject { ["name"] = "OldPanel", ["type"] = "WebPanel" });

            var elicitation = Assert.Single(fixture.Elicitations);
            Assert.Equal("elicitation/create", elicitation["method"]?.ToString());
            Assert.Contains("WebPanel 'OldPanel'", elicitation["params"]?["message"]?.ToString());
            Assert.Equal("boolean", elicitation["params"]?["requestedSchema"]?["properties"]?["confirm"]?["type"]?.ToString());
            Assert.True(response["result"]?["isError"]?.Value<bool>());
            Assert.Equal(Program.HumanDeclinedCode, Program.ExtractErrorCode(response));
        }

        [Fact]
        public async Task DeleteWithoutConfirm_HumanApproves_ForwardsConfirmTrue()
        {
            using var fixture = ElicitationFixture.Create(answer: _ => new JObject
            {
                ["action"] = "accept",
                ["content"] = new JObject { ["confirm"] = true }
            });

            var (request, refusal) = await Program.ConfirmDestructiveCallAsync(
                ElicitationFixture.ToolCall("genexus_delete_object", new JObject { ["name"] = "OldPanel" }),
                fixture.Session,
                default);

            Assert.Null(refusal);
            Assert.True(request["params"]?["arguments"]?["confirm"]?.Value<bool>());
            Assert.Single(fixture.Elicitations);
        }

        [Fact]
        public async Task HumanAcceptsWithConfirmFalse_IsTreatedAsDecline()
        {
            using var fixture = ElicitationFixture.Create(answer: _ => new JObject
            {
                ["action"] = "accept",
                ["content"] = new JObject { ["confirm"] = false }
            });

            var (_, refusal) = await Program.ConfirmDestructiveCallAsync(
                ElicitationFixture.ToolCall("genexus_deploy", new JObject { ["action"] = "deploy" }),
                fixture.Session,
                default);

            Assert.NotNull(refusal);
            Assert.Equal(Program.HumanDeclinedCode, Program.ExtractErrorCode(refusal!));
        }

        [Fact]
        public async Task AgentConfirmed_IsNotAskedInAutoMode_ButIsAskedInStrictMode()
        {
            using var fixture = ElicitationFixture.Create(answer: _ => new JObject { ["action"] = "cancel" });
            var call = ElicitationFixture.ToolCall("genexus_delete_object", new JObject { ["name"] = "OldPanel", ["confirm"] = true });

            var (_, autoRefusal) = await Program.ConfirmDestructiveCallAsync(call, fixture.Session, default);
            Assert.Null(autoRefusal);
            Assert.Empty(fixture.Elicitations);

            ElicitationBroker.ModeOverrideForTest = ElicitationMode.Strict;
            var (_, strictRefusal) = await Program.ConfirmDestructiveCallAsync(call, fixture.Session, default);
            Assert.Single(fixture.Elicitations);
            Assert.Equal(Program.HumanCancelledCode, Program.ExtractErrorCode(strictRefusal!));
        }

        [Fact]
        public async Task NoHumanAnswer_TimesOutWithoutMutating()
        {
            using var fixture = ElicitationFixture.Create(answer: null);
            ElicitationBroker.TimeoutOverrideForTest = TimeSpan.FromMilliseconds(200);

            var (_, refusal) = await Program.ConfirmDestructiveCallAsync(
                ElicitationFixture.ToolCall("genexus_delete_object", new JObject { ["name"] = "OldPanel" }),
                fixture.Session,
                default);

            Assert.Equal(Program.HumanConfirmationTimeoutCode, Program.ExtractErrorCode(refusal!));
        }

        [Fact]
        public async Task ClientWithoutElicitationCapability_IsNeverAsked()
        {
            using var fixture = ElicitationFixture.Create(answer: _ => new JObject { ["action"] = "decline" }, clientSupportsElicitation: false);

            var response = await fixture.CallToolAsync("genexus_delete_object", new JObject { ["name"] = "OldPanel" });

            Assert.Empty(fixture.Elicitations);
            Assert.NotEqual(Program.HumanDeclinedCode, Program.ExtractErrorCode(response));
        }

        [Fact]
        public async Task ModeOff_DisablesElicitation()
        {
            using var fixture = ElicitationFixture.Create(answer: _ => new JObject { ["action"] = "decline" });
            ElicitationBroker.ModeOverrideForTest = ElicitationMode.Off;

            var response = await fixture.CallToolAsync("genexus_delete_object", new JObject { ["name"] = "OldPanel" });

            Assert.Empty(fixture.Elicitations);
            Assert.NotEqual(Program.HumanDeclinedCode, Program.ExtractErrorCode(response));
        }

        [Fact]
        public async Task AmbiguousKb_HumanPicksKb_SessionSelectsItAndCallIsReplayed()
        {
            using var fixture = ElicitationFixture.Create(answer: _ => new JObject
            {
                ["action"] = "accept",
                ["content"] = new JObject { ["kb"] = "orders" }
            });

            // The replay reaches Worker dispatch for the picked KB; no Worker binary exists in
            // this fixture, so the replayed call may fail after the selection was applied.
            JObject? response = null;
            try { response = await fixture.CallToolAsync("genexus_read", new JObject { ["name"] = "Customer" }); }
            catch (Exception ex) when (ex is not Xunit.Sdk.XunitException) { }

            var elicitation = Assert.Single(fixture.Elicitations);
            var kbSchema = elicitation["params"]?["requestedSchema"]?["properties"]?["kb"];
            Assert.Equal(new[] { "customer", "orders" }, kbSchema?["enum"]?.ToObject<string[]>());
            if (response?["result"] is JObject result)
                Assert.Equal("orders", result["_meta"]?["gxmcp/humanSelectedKb"]?.ToString());
            Assert.NotEqual("KB_AMBIGUOUS", response == null ? null : Program.ExtractErrorCode(response));

            var list = await fixture.CallToolAsync("genexus_kb", new JObject { ["action"] = "list" });
            Assert.Equal("orders", JObject.Parse(list["result"]!["content"]![0]!["text"]!.ToString())["selectedKb"]?.ToString());
        }

        [Fact]
        public async Task AmbiguousKb_HumanDeclines_ReturnsOriginalError()
        {
            using var fixture = ElicitationFixture.Create(answer: _ => new JObject { ["action"] = "decline" });

            var response = await fixture.CallToolAsync("genexus_read", new JObject { ["name"] = "Customer" });

            Assert.Single(fixture.Elicitations);
            Assert.Equal("KB_AMBIGUOUS", Program.ExtractErrorCode(response));
        }

        [Fact]
        public async Task AmbiguousKb_ExplicitKbArgument_IsNotOverriddenByAPicker()
        {
            using var fixture = ElicitationFixture.Create(answer: _ => new JObject
            {
                ["action"] = "accept",
                ["content"] = new JObject { ["kb"] = "orders" }
            });

            await fixture.CallToolAsync("genexus_read", new JObject { ["name"] = "Customer", ["kb"] = "missing" });

            Assert.Empty(fixture.Elicitations);
        }

        private sealed class ElicitationFixture : IDisposable
        {
            private readonly IDisposable _state;
            private readonly IDisposable _sender;
            private readonly string _directory;

            internal string Session { get; }
            internal ConcurrentQueue<JObject> Elicitations { get; } = new();

            private ElicitationFixture(string directory, string session, IDisposable state, Func<JObject, JObject?>? answer)
            {
                _directory = directory;
                Session = session;
                _state = state;
                _sender = ElicitationBroker.RegisterSender(session, outbound =>
                {
                    Elicitations.Enqueue(outbound);
                    var result = answer?.Invoke(outbound);
                    if (result != null)
                    {
                        var reply = new JObject { ["jsonrpc"] = "2.0", ["id"] = outbound["id"]!.DeepClone(), ["result"] = result };
                        _ = Task.Run(() => Program.ProcessMcpRequest(reply, session));
                    }
                    return Task.CompletedTask;
                });
            }

            internal static ElicitationFixture Create(Func<JObject, JObject?>? answer, bool clientSupportsElicitation = true)
            {
                ElicitationBroker.ModeOverrideForTest = ElicitationMode.Auto;
                ElicitationBroker.TimeoutOverrideForTest = TimeSpan.FromSeconds(10);
                string directory = Path.Combine(Path.GetTempPath(), "gxmcp-elicit-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                string configPath = Path.Combine(directory, "config.json");
                File.WriteAllText(configPath, "{\"Environment\":{\"ResolutionPolicy\":\"strict\",\"KBs\":[{\"Alias\":\"customer\",\"Path\":\"C:/KB/Customer\"},{\"Alias\":\"orders\",\"Path\":\"C:/KB/Orders\"}]}}");
                var config = new Configuration
                {
                    Environment = new EnvironmentConfig
                    {
                        ResolutionPolicy = "strict",
                        KBs =
                        {
                            new KbEntry { Alias = "customer", Path = "C:/KB/Customer" },
                            new KbEntry { Alias = "orders", Path = "C:/KB/Orders" }
                        }
                    }
                };
                string session = "elicit-" + Guid.NewGuid().ToString("N");
                var fixture = new ElicitationFixture(directory, session, Program.ConfigureRouteStateForTest(config, configPath), answer);
                var capabilities = new JObject();
                if (clientSupportsElicitation) capabilities["elicitation"] = new JObject();
                ElicitationBroker.ObserveInitialize(new JObject
                {
                    ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "initialize",
                    ["params"] = new JObject { ["protocolVersion"] = "2025-06-18", ["capabilities"] = capabilities }
                }, session);
                return fixture;
            }

            internal static JObject ToolCall(string tool, JObject arguments) => new()
            {
                ["jsonrpc"] = "2.0", ["id"] = Guid.NewGuid().ToString("N"), ["method"] = "tools/call",
                ["params"] = new JObject { ["name"] = tool, ["arguments"] = arguments }
            };

            internal async Task<JObject> CallToolAsync(string tool, JObject arguments)
            {
                var call = Program.ProcessMcpRequest(ToolCall(tool, arguments), Session);
                var finished = await Task.WhenAny(call, Task.Delay(TimeSpan.FromSeconds(60)));
                Assert.Same(call, finished);
                return (await call)!;
            }

            public void Dispose()
            {
                _sender.Dispose();
                ElicitationBroker.ForgetSessionForTest(Session);
                ElicitationBroker.ModeOverrideForTest = null;
                ElicitationBroker.TimeoutOverrideForTest = null;
                _state.Dispose();
                try { Directory.Delete(_directory, true); } catch { }
            }
        }
    }
}
