using System;
using System.Collections.Generic;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class BulkWriteIntegrityTests
    {
        [Theory]
        [InlineData("{\"status\":\"error\"}")]
        [InlineData("{\"status\":\"partial\"}")]
        [InlineData("{\"status\":\"accepted\"}")]
        [InlineData("{}")]
        [InlineData("not JSON")]
        [InlineData("{\"status\":\"ok\",\"isError\":true}")]
        [InlineData("{\"status\":\"ok\",\"error\":{\"code\":\"Failed\"}}")]
        [InlineData("{\"status\":\"ok\",\"result\":{\"verified\":false,\"persisted\":true}}")]
        [InlineData("{\"status\":\"ok\",\"verified\":false}")]
        [InlineData("{\"status\":\"ok\",\"persistedVerified\":false}")]
        [InlineData("{\"status\":\"ok\",\"persistedStateKnown\":false}")]
        [InlineData("{\"status\":\"ok\",\"persistedState\":\"unknown\"}")]
        [InlineData("{\"status\":\"ok\",\"persistedState\":\"not_persisted\"}")]
        [InlineData("{\"status\":\"ok\",\"persisted\":false}")]
        [InlineData("{\"status\":\"ok\",\"reconciliationRequired\":true}")]
        [InlineData("{\"status\":\"ok\",\"result\":{\"isError\":true}}")]
        [InlineData("{\"status\":\"ok\",\"result\":{\"error\":{\"code\":\"Failed\"}}}")]
        [InlineData(null)]
        public void UnconfirmedRestoreNeverClaimsTheBatchWasRolledBack(string restoreResponse)
        {
            var state = new Dictionary<string, string> { ["A"] = "before" };
            var response = Run(Batch("A", "MissingContent"), name => Snapshot(name, state[name]),
                (name, content, dryRun) =>
                {
                    if (content == "before") return restoreResponse;
                    if (!dryRun) state[name] = content;
                    return "{\"status\":\"ok\"}";
                });
            Assert.False(Payload(response).Value<bool>("rollbackSucceeded"));
            Assert.Equal(0, Payload(response)["counts"].Value<int>("rolledBack"));
            Assert.Equal(1, Payload(response)["counts"].Value<int>("rollbackErrored"));
            Assert.Equal("after", state["A"]);
        }

        [Theory]
        [InlineData("ok")]
        [InlineData("Success")]
        public void ConfirmedRestoresReportOriginalStateAndSuccessfulReceipt(string status)
        {
            string state = "before";
            var response = Run(Batch("A", "MissingContent"), name => Snapshot(name, state),
                (_, content, dryRun) =>
                {
                    if (!dryRun) state = content;
                    return new JObject { ["status"] = status, ["error"] = JValue.CreateNull() }.ToString();
                });
            Assert.Equal("before", state);
            Assert.True(Payload(response).Value<bool>("rollbackSucceeded"));
            Assert.False(Payload(response).Value<bool>("reconciliationRequired"));
            Assert.Equal(1, Payload(response)["counts"].Value<int>("rolledBack"));
        }

        [Theory]
        [InlineData("{\"status\":\"partial\"}")]
        [InlineData("{\"status\":\"accepted\"}")]
        [InlineData("{\"status\":\"error\"}")]
        [InlineData("not JSON")]
        [InlineData("{\"status\":\"ok\",\"result\":{\"verified\":false,\"persisted\":true}}")]
        [InlineData("{\"status\":\"ok\",\"verified\":false}")]
        [InlineData("{\"status\":\"ok\",\"persistedVerified\":false}")]
        [InlineData("{\"status\":\"ok\",\"persistedStateKnown\":false}")]
        [InlineData("{\"status\":\"ok\",\"persistedState\":\"unknown\"}")]
        [InlineData("{\"status\":\"ok\",\"persistedState\":\"not_persisted\"}")]
        [InlineData("{\"status\":\"ok\",\"persisted\":false}")]
        [InlineData("{\"status\":\"ok\",\"reconciliationRequired\":true}")]
        public void UncertainFailedWriteRequiresReconciliationEvenAfterPriorWritesAreRestored(string failedReply)
        {
            var state = new Dictionary<string, string> { ["A"] = "before", ["B"] = "before" };
            var response = Run(Batch("A", "B"), name => Snapshot(name, state[name]),
                (name, content, dryRun) =>
                {
                    if (!dryRun) state[name] = content;
                    return name == "B" ? failedReply : "{\"status\":\"ok\"}";
                });
            Assert.Equal("before", state["A"]);
            Assert.Equal("after", state["B"]);
            Assert.False(Payload(response).Value<bool>("rollbackSucceeded"));
            Assert.True(Payload(response).Value<bool>("reconciliationRequired"));
            Assert.Equal("BulkRollbackUnconfirmed", response["code"]?.ToString());
            Assert.Equal(1, Payload(response)["counts"].Value<int>("rolledBack"));
        }

        [Fact]
        public void WriterExceptionReconcilesFailedTargetAndStillRestoresPriorWrites()
        {
            string state = "before";
            var response = Run(Batch("A", "B"), name => Snapshot(name, "before"),
                (name, content, dryRun) =>
                {
                    if (name == "B") throw new InvalidOperationException("synthetic write failure");
                    if (!dryRun) state = content;
                    return "{\"status\":\"ok\"}";
                });
            Assert.Equal("before", state);
            Assert.True(Payload(response).Value<bool>("reconciliationRequired"));
            Assert.Contains("BulkWriteOutcomeUnknown", response.ToString());
        }

        [Fact]
        public void KnownUnchangedFailureCanConfirmRollbackOfPriorWrites()
        {
            string state = "before";
            var response = Run(Batch("A", "B"), name => Snapshot(name, "before"),
                (name, content, dryRun) =>
                {
                    if (name == "B") return "{\"status\":\"error\",\"persistedState\":\"unchanged\"}";
                    if (!dryRun) state = content;
                    return "{\"status\":\"ok\"}";
                });
            Assert.Equal("before", state);
            Assert.True(Payload(response).Value<bool>("rollbackSucceeded"));
        }

        [Fact]
        public void MissingFirstSnapshotStopsBeforeAnyWrite()
        {
            int writes = 0;
            var response = Run(Batch("A", "MissingContent"), _ => null,
                (_, _, _) => { writes++; return "{\"status\":\"ok\"}"; });
            Assert.Equal(0, writes);
            Assert.Contains("RollbackSnapshotUnavailable", response.ToString());
        }

        [Theory]
        [InlineData(null, "before")]
        [InlineData("snapshot", null)]
        public void IncompleteSnapshotStopsBeforeAnyWrite(string path, string priorContent)
        {
            int writes = 0;
            var response = Run(Batch("A"), _ => new EditSnapshotStore.SnapshotInfo { Path = path, PriorContent = priorContent },
                (_, _, _) => { writes++; return "{\"status\":\"ok\"}"; });
            Assert.Equal(0, writes);
            Assert.Contains("RollbackSnapshotUnavailable", response.ToString());
        }

        [Fact]
        public void TransactionalVariablesRefusesBeforeWriteBecauseTextCannotRestoreMetadata()
        {
            int writes = 0;
            var args = Batch("A");
            args["targets"][0]["part"] = "Variables";
            var response = Run(args, name => Snapshot(name, "before"),
                (_, _, dryRun) => { if (!dryRun) writes++; return "{\"status\":\"ok\"}"; });
            Assert.Equal(0, writes);
            Assert.Contains("RollbackSnapshotUnavailable", response.ToString());
        }

        [Fact]
        public void MissingSecondSnapshotRestoresFirstAndDoesNotWriteSecond()
        {
            var state = new Dictionary<string, string> { ["A"] = "before", ["B"] = "before" };
            var writes = new List<string>();
            var response = Run(Batch("A", "B", "MissingContent"), name => name == "A" ? Snapshot(name, state[name]) : null,
                (name, content, dryRun) =>
                {
                    writes.Add(name + ":" + content);
                    if (!dryRun) state[name] = content;
                    return "{\"status\":\"ok\"}";
                });
            Assert.Equal(new[] { "A:after", "A:before" }, writes);
            Assert.Equal("before", state["A"]);
            Assert.Equal("before", state["B"]);
            Assert.True(Payload(response).Value<bool>("rollbackSucceeded"));
            Assert.Contains("RollbackSnapshotUnavailable", response.ToString());
        }

        [Fact]
        public void PartialRollbackContinuesInReverseOrderAndCountsOnlyRestoredItems()
        {
            var state = new Dictionary<string, string> { ["A"] = "before", ["B"] = "before", ["C"] = "before" };
            var restores = new List<string>();
            var response = Run(Batch("A", "B", "C", "MissingContent"), name => Snapshot(name, state[name]),
                (name, content, dryRun) =>
                {
                    if (content == "before")
                    {
                        restores.Add(name);
                        if (name == "B") return "{\"status\":\"error\"}";
                    }
                    if (!dryRun) state[name] = content;
                    return "{\"status\":\"ok\"}";
                });
            Assert.Equal(new[] { "C", "B", "A" }, restores);
            Assert.Equal("before", state["A"]);
            Assert.Equal("after", state["B"]);
            Assert.Equal("before", state["C"]);
            Assert.Equal("BulkRollbackPartial", response["code"]?.ToString());
            Assert.False(Payload(response).Value<bool>("rollbackSucceeded"));
            Assert.Equal(2, Payload(response)["counts"].Value<int>("rolledBack"));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void FailedSnapshotReadDoesNotWriteRecoveryAndDoesNotClaimRestored(bool throws)
        {
            int restores = 0;
            var response = Run(Batch("A", "MissingContent"), name => Snapshot(name, "before"),
                (_, content, _) => { if (content == "before") restores++; return "{\"status\":\"ok\"}"; },
                _ => throws ? throw new InvalidOperationException("synthetic read failure") : null);
            Assert.Equal(0, restores);
            Assert.False(Payload(response).Value<bool>("rollbackSucceeded"));
        }

        [Fact]
        public void WriterExceptionDuringRestoreDoesNotPreventOtherRestores()
        {
            var state = new Dictionary<string, string> { ["A"] = "before", ["B"] = "before" };
            var response = Run(Batch("A", "B", "MissingContent"), name => Snapshot(name, state[name]),
                (name, content, dryRun) =>
                {
                    if (name == "B" && content == "before") throw new InvalidOperationException("synthetic restore failure");
                    if (!dryRun) state[name] = content;
                    return "{\"status\":\"ok\"}";
                });
            Assert.Equal("before", state["A"]);
            Assert.Equal("after", state["B"]);
            Assert.False(Payload(response).Value<bool>("rollbackSucceeded"));
        }

        [Fact]
        public void DryRunDoesNotRequireSnapshotsOrWriteRecovery()
        {
            int captured = 0;
            var args = Batch("A");
            args["dryRun"] = true;
            var response = Run(args, _ => { captured++; return null; },
                (_, _, dryRun) => { Assert.True(dryRun); return "{\"status\":\"ok\"}"; });
            Assert.Equal(0, captured);
            Assert.Equal("BulkWriteCompleted", response["code"]?.ToString());
        }

        [Fact]
        public void DryRunAcceptsTheRealTextVerificationReceiptWithoutClaimingPersistence()
        {
            var args = Batch("A");
            args["dryRun"] = true;
            var response = Run(args, _ => throw new InvalidOperationException("dry-run must not capture"),
                (_, _, _) => WriteService.ApplyTextVerificationReceipt(
                    new JObject { ["status"] = "ok", ["code"] = "WriteDryRun" }, "A", "Source",
                    "before", "after", "before", "version", false, null, null, dryRun: true).ToString());
            Assert.Equal("BulkWriteCompleted", response["code"]?.ToString());
            Assert.Equal(1, Payload(response)["counts"].Value<int>("success"));
        }

        [Fact]
        public void ConfirmedTextVerificationReceiptRestoresOriginalState()
        {
            string state = "before";
            var response = Run(Batch("A", "MissingContent"), name => Snapshot(name, state),
                (_, content, _) =>
                {
                    string before = state;
                    state = content;
                    return WriteService.ApplyTextVerificationReceipt(
                        new JObject { ["status"] = "ok", ["code"] = "WriteApplied", ["result"] = new JObject { ["verified"] = true } },
                        "A", "Source", before, content, state, "version", false, null, null).ToString();
                });
            Assert.Equal("before", state);
            Assert.True(Payload(response).Value<bool>("rollbackSucceeded"));
        }

        private static JObject Run(JObject args, Func<string, EditSnapshotStore.SnapshotInfo> capture,
            Func<string, string, bool, string> writer, Func<string, string> reader = null)
        {
            var service = new WriteService(null);
            return JObject.Parse(service.BulkWrite(args, (name, part, type) => capture(name),
                (name, part, content, type, dryRun) => writer(name, content, dryRun), reader ?? (_ => "before")));
        }

        private static JObject Payload(JObject response) => response["result"] as JObject ?? response;

        private static EditSnapshotStore.SnapshotInfo Snapshot(string name, string content)
            => new EditSnapshotStore.SnapshotInfo { Path = "snapshot-" + name, PriorContent = content };

        private static JObject Batch(params string[] names)
        {
            var items = new JArray();
            foreach (string name in names)
            {
                var item = new JObject { ["name"] = name, ["part"] = "Source" };
                if (name != "MissingContent") item["content"] = "after";
                items.Add(item);
            }
            return new JObject { ["transactional"] = true, ["targets"] = items };
        }
    }
}
