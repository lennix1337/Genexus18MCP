using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using GxMcp.Gateway;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    [Collection("Gateway route state")]
    public sealed class Issue192LeaseRecoveryTests
    {
        [Fact]
        public async Task ExpiredSessionLease_ReturnsExpiredAndActionableRecoveryError()
        {
            using var fixture = RouteFixture.Create();
            string session = "issue192-recover-" + Guid.NewGuid().ToString("N");
            Program.SetSessionSelectedKb(session, "orders", "C:/KB/Orders");

            try
            {
                Assert.True(Program.TryGetSessionSnapshotForTest(session, out var snapshot));
                ExpireLease(snapshot!.Lease!.Token);

                var response = await Program.ProcessMcpRequest(new JObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = "issue192-recover",
                    ["method"] = "tools/call",
                    ["params"] = new JObject
                    {
                        ["name"] = "genexus_connection_recover",
                        ["arguments"] = new JObject { ["force"] = true }
                    }
                }, session);

                var payload = ExtractPayload(response!);
                var result = response!["result"] as JObject;
                Assert.NotNull(result);
                var isError = result!["isError"];
                Assert.NotNull(isError);
                Assert.True(isError!.Value<bool>() == true);
                var error = payload["error"] as JObject;
                Assert.NotNull(error);
                Assert.Equal("KB_LEASE_EXPIRED", error!["code"]?.ToString());
                Assert.Contains("select", error!["hint"]?.ToString() ?? string.Empty,
                    StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                Program.ClearSessionSelectedKb(session);
            }
        }

        [Fact]
        public async Task ExpiredSessionLease_IsVisibleAsSelectedAliasButInactiveLease()
        {
            using var fixture = RouteFixture.Create();
            string session = "issue192-whoami-" + Guid.NewGuid().ToString("N");
            Program.SetSessionSelectedKb(session, "orders", "C:/KB/Orders");

            try
            {
                Assert.True(Program.TryGetSessionSnapshotForTest(session, out var snapshot));
                ExpireLease(snapshot!.Lease!.Token);

                var response = await Program.ProcessMcpRequest(new JObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = "issue192-whoami",
                    ["method"] = "tools/call",
                    ["params"] = new JObject
                    {
                        ["name"] = "genexus_whoami",
                        ["arguments"] = new JObject()
                    }
                }, session);

                var payload = ExtractPayload(response!);
                var kb = (JObject)payload["kb"]!;
                Assert.Equal("orders", kb["sessionSelection"]?.ToString());
                Assert.Equal("valid", kb["selectionState"]?.ToString());
                Assert.Equal("expired", kb["leaseState"]?.ToString());
                Assert.False(kb["leaseActive"]?.Value<bool>());
                Assert.True(kb["contextRequired"]?.Value<bool>());
            }
            finally
            {
                Program.ClearSessionSelectedKb(session);
            }
        }

        [Fact]
        public async Task ExpiredSessionLease_OpenReportsAliasSelectionSeparatelyFromLease()
        {
            using var fixture = RouteFixture.Create();
            fixture.UseFakeWorker();
            string session = "issue192-open-" + Guid.NewGuid().ToString("N");
            Program.SetSessionSelectedKb(session, "orders", "C:/KB/Orders");

            try
            {
                Assert.True(Program.TryGetSessionSnapshotForTest(session, out var snapshot));
                ExpireLease(snapshot!.Lease!.Token);

                var listResponse = await CallToolAsync(session, "genexus_kb", new JObject
                {
                    ["action"] = "list"
                });
                var listPayload = ExtractPayload(listResponse);
                Assert.Equal("orders", listPayload["selectedKb"]?.ToString());
                Assert.Equal("expired", listPayload["leaseState"]?.ToString());
                Assert.False(listPayload["leaseActive"]?.Value<bool>());

                var response = await Program.ProcessMcpRequest(new JObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = "issue192-open",
                    ["method"] = "tools/call",
                    ["params"] = new JObject
                    {
                        ["name"] = "genexus_kb",
                        ["arguments"] = new JObject
                        {
                            ["action"] = "open",
                            ["alias"] = "orders"
                        }
                    }
                }, session);

                var payload = ExtractPayload(response!);
                var result = response!["result"] as JObject;
                Assert.NotNull(result);
                var isError = result!["isError"];
                Assert.NotNull(isError);
                Assert.True(isError!.Value<bool>() == false);
                Assert.True(payload["selected"]?.Value<bool>() == true);
                Assert.Equal(JTokenType.Null, payload["workerPid"]?.Type);
                Assert.Equal("expired", payload["leaseState"]?.ToString());
                Assert.False(payload["leaseActive"]?.Value<bool>());
                Assert.True(payload["contextRequired"]?.Value<bool>());
            }
            finally
            {
                Program.ClearSessionSelectedKb(session);
            }
        }

        [Fact]
        public async Task ExplicitSelectAfterExpiry_CreatesFreshLeaseAndAllowsRecovery()
        {
            using var fixture = RouteFixture.Create();
            fixture.UseFakeWorker(ready: true);
            fixture.RegisterKnown("orders", "C:/KB/Orders");
            string session = "issue192-select-" + Guid.NewGuid().ToString("N");
            Program.SetSessionSelectedKb(session, "orders", "C:/KB/Orders");

            try
            {
                Assert.True(Program.TryGetSessionSnapshotForTest(session, out var before));
                string expiredToken = before!.Lease!.Token;
                ExpireLease(expiredToken);

                var selectResponse = await CallToolAsync(session, "genexus_kb", new JObject
                {
                    ["action"] = "select",
                    ["alias"] = "orders"
                });
                var selectPayload = ExtractPayload(selectResponse);
                Assert.False(selectResponse["result"]?["isError"]?.Value<bool>());
                Assert.Equal("orders", selectPayload["selectedKb"]?.ToString());
                Assert.Equal("active", selectPayload["leaseState"]?.ToString());
                Assert.True(selectPayload["leaseActive"]?.Value<bool>());

                Assert.True(Program.TryGetSessionSnapshotForTest(session, out var after));
                Assert.NotEqual(expiredToken, after!.Lease!.Token);
                Assert.True(after.ContextGeneration > before.ContextGeneration);
                Assert.Equal("active", Program.GetSessionLeaseState(session));

                var recoverResponse = await CallToolAsync(session, "genexus_connection_recover", new JObject
                {
                    ["force"] = true
                });
                var recoverPayload = ExtractPayload(recoverResponse);
                Assert.False(recoverResponse["result"]?["isError"]?.Value<bool>());
                Assert.Equal("Recovered", recoverPayload["status"]?.ToString());
            }
            finally
            {
                Program.ClearSessionSelectedKb(session);
            }
        }

        [Fact]
        public async Task KbAliasExplicitCall_RenewsLease_AndAutoRecoversAfterExpiry()
        {
            using var fixture = RouteFixture.Create();
            fixture.UseFakeWorker(ready: true);
            fixture.RegisterKnown("orders", "C:/KB/Orders");
            string session = "issue303-kbalias-" + Guid.NewGuid().ToString("N");
            Program.SetSessionSelectedKb(session, "orders", "C:/KB/Orders");

            try
            {
                Assert.True(Program.TryGetSessionSnapshotForTest(session, out var before));
                string expiredToken = before!.Lease!.Token;
                ExpireLease(expiredToken);

                // With kbAlias explicit on a stateful call, gateway auto-recovers lease instead of failing with KB_LEASE_EXPIRED
                var recoverResponse = await CallToolAsync(session, "genexus_connection_recover", new JObject
                {
                    ["force"] = true,
                    ["kbAlias"] = "orders"
                });
                var recoverPayload = ExtractPayload(recoverResponse);
                Assert.False(recoverResponse["result"]?["isError"]?.Value<bool>());
                Assert.Equal("Recovered", recoverPayload["status"]?.ToString());

                Assert.True(Program.TryGetSessionSnapshotForTest(session, out var after));
                Assert.NotEqual(expiredToken, after!.Lease!.Token);
                Assert.True(after.ContextGeneration > before.ContextGeneration);
                Assert.Equal("active", Program.GetSessionLeaseState(session));
            }
            finally
            {
                Program.ClearSessionSelectedKb(session);
            }
        }

        // #830 item 3: after ~10 minutes idle the session lease expired, and polling a job by id
        // returned KB_LEASE_EXPIRED ("re-select the KB") although the job itself was readable.
        [Fact]
        public async Task ExpiredSessionLease_LifecycleJobResultIsReadableByJobId()
        {
            using var fixture = RouteFixture.Create();
            fixture.UseFakeWorker(ready: true);
            fixture.RegisterKnown("orders", "C:/KB/Orders");
            string session = "issue830-job-" + Guid.NewGuid().ToString("N");
            Program.SetSessionSelectedKb(session, "orders", "C:/KB/Orders");
            JobEntry? job = null;
            try
            {
                Assert.True(Program.TryGetSessionSnapshotForTest(session, out var snapshot));
                ExpireLease(snapshot!.Lease!.Token);
                job = Program.JobRegistry.Start(session, "lifecycle/build", 10);
                Program.JobRegistry.Complete(job.Id, success: false, summary: "Build Failed: 2 errors, 0 warnings",
                    result: new JObject
                    {
                        ["Status"] = "Failed",
                        ["ErrorCount"] = 2,
                        ["Errors"] = new JArray("a.cs(1,1): error CS0103: x", "b.cs(2,2): error CS0103: y")
                    });

                var response = await CallToolAsync(session, "genexus_lifecycle", new JObject
                {
                    ["action"] = "result",
                    ["job_id"] = job.Id
                });

                var payload = ExtractPayload(response);
                Assert.NotEqual("KB_LEASE_EXPIRED", payload["error"]?["code"]?.ToString());
                Assert.Equal(job.Id, payload["operationId"]?.ToString());
                Assert.Equal("failed", payload["status"]?.ToString());
                Assert.Equal(2, payload["result"]!["errorCount"]!.Value<int>());
                Assert.True(response["result"]!["isError"]!.Value<bool>());
            }
            finally
            {
                if (job != null) Program.JobRegistry.Cancel(job.Id, "test cleanup");
                Program.ClearSessionSelectedKb(session);
            }
        }

        // #830 item 2: a failed build's result and status keep the error list and can page the log.
        [Fact]
        public async Task FailedBuildJob_ResultAndStatusCarryErrorsAndPageableLog()
        {
            using var fixture = RouteFixture.Create();
            fixture.UseFakeWorker(ready: true);
            fixture.RegisterKnown("orders", "C:/KB/Orders");
            string session = "issue830-log-" + Guid.NewGuid().ToString("N");
            Program.SetSessionSelectedKb(session, "orders", "C:/KB/Orders");
            string logDirectory = Path.Combine(Path.GetTempPath(), "gxmcp-830-e2e-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(logDirectory);
            JobEntry? job = null;
            try
            {
                string logPath = Path.Combine(logDirectory, "build-abcdef12.log");
                File.WriteAllText(logPath, "first line\r\nsecond line\r\nthird line");
                job = Program.JobRegistry.Start(session, "lifecycle/build", 10);
                Program.JobRegistry.SetWorkerTaskId(job.Id, "abcdef12");
                Program.JobRegistry.Complete(job.Id, success: false, summary: "Build Failed: 2 errors, 0 warnings",
                    result: new JObject
                    {
                        ["Status"] = "Failed",
                        ["TaskId"] = "abcdef12",
                        ["ErrorCount"] = 2,
                        ["Errors"] = new JArray("a.cs(1,1): error CS0103: x", "b.cs(2,2): error CS0103: y"),
                        ["TailLines"] = new JArray("tail one"),
                        ["FullLogPath"] = logPath
                    });

                var result = await CallToolAsync(session, "genexus_lifecycle", new JObject
                {
                    ["action"] = "result",
                    ["job_id"] = job.Id,
                    ["log_offset"] = 1,
                    ["log_limit"] = 1
                });
                var resultPayload = ExtractPayload(result);
                Assert.Equal(2, resultPayload["result"]!["errorCount"]!.Value<int>());
                Assert.Equal("a.cs(1,1): error CS0103: x", resultPayload["result"]!["errors"]![0]!.ToString());
                Assert.Null(resultPayload["result"]!["TailLines"]);
                Assert.Equal("second line", resultPayload["log"]!["lines"]![0]!.ToString());
                Assert.Equal(3, resultPayload["log"]!["totalLines"]!.Value<int>());
                Assert.Equal(2, resultPayload["log"]!["nextOffset"]!.Value<int>());

                var status = await CallToolAsync(session, "genexus_lifecycle", new JObject
                {
                    ["action"] = "status",
                    ["job_id"] = job.Id,
                    ["log_limit"] = 2
                });
                var statusPayload = ExtractPayload(status);
                Assert.Equal(2, statusPayload["result"]!["errorCount"]!.Value<int>());
                Assert.Equal("first line", statusPayload["log"]!["lines"]![0]!.ToString());
                Assert.Equal(2, statusPayload["log"]!["returned"]!.Value<int>());
            }
            finally
            {
                if (job != null) Program.JobRegistry.Cancel(job.Id, "test cleanup");
                Program.ClearSessionSelectedKb(session);
                try { Directory.Delete(logDirectory, true); } catch { }
            }
        }

        private static async Task<JObject> CallToolAsync(string session, string name, JObject arguments)
        {
            return (await Program.ProcessMcpRequest(new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = Guid.NewGuid().ToString("N"),
                ["method"] = "tools/call",
                ["params"] = new JObject
                {
                    ["name"] = name,
                    ["arguments"] = arguments
                }
            }, session))!;
        }

        private static JObject ExtractPayload(JObject response)
        {
            return JObject.Parse(response["result"]!["content"]![0]!["text"]!.ToString());
        }

        private static void ExpireLease(string token)
        {
            var programLeaseField = typeof(Program).GetField("_kbLeases", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(programLeaseField);
            var registry = (KbUseLeaseRegistry)programLeaseField!.GetValue(null)!;
            var tokenMapField = typeof(KbUseLeaseRegistry).GetField("_byToken", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(tokenMapField);
            var tokenMap = (IDictionary)tokenMapField!.GetValue(registry)!;
            var entry = tokenMap[token];
            Assert.NotNull(entry);
            var expiresAtField = entry!.GetType().GetField("ExpiresAt", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.NotNull(expiresAtField);
            expiresAtField!.SetValue(entry, TimeSpan.Zero);
        }

        private static WorkerPool GetCurrentWorkerPool()
        {
            var poolField = typeof(Program).GetField("_workerPool", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(poolField);
            return (WorkerPool)poolField!.GetValue(null)!;
        }

        private sealed class RouteFixture : IDisposable
        {
            private readonly IDisposable _state;
            private readonly string _directory;
            private readonly Configuration _config;
            private readonly WorkerPool _pool;

            private RouteFixture(IDisposable state, string directory, Configuration config, WorkerPool pool)
            {
                _state = state;
                _directory = directory;
                _config = config;
                _pool = pool;
            }

            internal static RouteFixture Create()
            {
                string directory = Path.Combine(Path.GetTempPath(), "gxmcp-issue192-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                string configPath = Path.Combine(directory, "config.json");
                File.WriteAllText(configPath, "{}");
                var config = new Configuration
                {
                    Environment = new EnvironmentConfig
                    {
                        ResolutionPolicy = "strict",
                        DefaultKb = "customer",
                        ActiveKb = "customer",
                        KBs =
                        {
                            new KbEntry { Alias = "customer", Path = "C:/KB/Customer" },
                            new KbEntry { Alias = "orders", Path = "C:/KB/Orders" }
                        }
                    }
                };
                var state = Program.ConfigureRouteStateForTest(config, configPath);
                return new RouteFixture(state, directory, config, GetCurrentWorkerPool());
            }

            internal void UseFakeWorker(bool ready = false)
            {
                _pool.SpawnFactoryForTest = handle =>
                {
                    var worker = new WorkerProcess(_config, handle);
                    worker.SetProcessStateForTest(alive: true, exitConfirmed: false);
                    if (ready)
                        worker.HandleWorkerRpcResponseForTest("{\"jsonrpc\":\"2.0\",\"id\":\"ready\",\"result\":{}}");
                    return worker;
                };
            }

            internal void RegisterKnown(string alias, string path)
            {
                _pool.RegisterKnown(new KbHandle(alias, path));
            }

            public void Dispose()
            {
                _pool.StopAll(WorkerStopReason.GatewayShutdown);
                _state.Dispose();
                try { Directory.Delete(_directory, true); } catch { }
            }
        }
    }
}
