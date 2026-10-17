using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // Issue #542: changing the type of an existing SDT member. Deterministic coverage
    // for the verified-rollback receipt of genexus_edit part=Structure, its patch
    // receipt, and the update_visual mismatch detail. Whether the SDK accepts the
    // type change itself needs a live KB and is not covered here.
    public class Issue542SdtMemberTypeRollbackTests
    {
        [Fact]
        public void StructureRoundTripMissingLines_ReturnsRequestedLinesAbsentFromPersisted()
        {
            string expected = "codigo : NUMERIC(7)\nvalor : NUMERIC(12,2)";
            string persisted = "codigo : VARCHAR(7)\nvalor : NUMERIC(12,2)";

            string[] missing = WriteService.StructureRoundTripMissingLines(expected, persisted);

            Assert.Equal(new[] { "codigo : NUMERIC(7)" }, missing);
        }

        [Fact]
        public void StructureRoundTripMissingLines_ReturnsEmptyWhenEveryLinePersisted()
        {
            string expected = "codigo : NUMERIC(7)\nvalor : NUMERIC(12,2)";
            string persisted = "valor : NUMERIC(12,2)\ncodigo : NUMERIC(7)";

            Assert.Empty(WriteService.StructureRoundTripMissingLines(expected, persisted));
            Assert.Empty(WriteService.StructureRoundTripMissingLines(null, persisted));
            Assert.Empty(WriteService.StructureRoundTripMissingLines(string.Empty, persisted));
        }

        [Fact]
        public void StructureRoundTripMissingLines_IgnoresBracesWhitespaceLengthAndOrder()
        {
            string expected = "ZTstMcpSdt\r\n{\r\n  codigo:NUMERIC(7)\r\n  valor : NUMERIC(12,2)\r\n  abrev : Attribute:ABREV_BAI\r\n}";
            string persisted = "ZTstMcpSdt\n{\n  abrev : Attribute:ABREV_BAI\n  valor : NUMERIC(12,2)\n  codigo : NUMERIC(9)\n}";

            Assert.Empty(WriteService.StructureRoundTripMissingLines(expected, persisted));

            // Several missing lines come back trimmed, in request order, each once.
            string multi = "  b : DATE\n  a : NUMERIC(7)\n  a : NUMERIC(7)\n  c : VARCHAR(40)";
            Assert.Equal(new[] { "b : DATE", "a : NUMERIC(7)" },
                WriteService.StructureRoundTripMissingLines(multi, "c : VARCHAR(40)"));
        }

        [Fact]
        public void BuildStructureRoundTripMessage_NamesUnappliedMembersAndRestoreState_WithoutRetry()
        {
            string restored = WriteService.BuildStructureRoundTripMessage(
                new[] { "codigo : NUMERIC(7)" }, rolledBack: true, rollbackError: null);
            Assert.Contains("codigo : NUMERIC(7)", restored);
            Assert.Contains("restored", restored);
            Assert.Contains("rolledBack=true", restored);
            Assert.DoesNotContain("EntityVersion", restored);
            Assert.DoesNotContain("and retry", restored);

            string failed = WriteService.BuildStructureRoundTripMessage(
                new[] { "codigo : NUMERIC(7)" }, rolledBack: false, rollbackError: "AddItem threw.");
            Assert.Contains("codigo : NUMERIC(7)", failed);
            Assert.Contains("could not be restored", failed);
            Assert.Contains("rolledBack=false", failed);
            Assert.Contains("AddItem threw", failed);
            Assert.DoesNotContain("EntityVersion", failed);
            Assert.DoesNotContain("and retry", failed);
        }

        [Fact]
        public void IsWriterRollbackReported_RequiresThePlainAttemptFlag_EvenNextToTheWritersAtomicRollbackObject()
        {
            Assert.True(PatchService.IsWriterRollbackReported(new JObject { ["rollbackAttempted"] = true, ["rolledBack"] = true }));
            Assert.True(PatchService.IsWriterRollbackReported(new JObject { ["rollbackAttempted"] = true, ["rolledBack"] = false }));
            Assert.False(PatchService.IsWriterRollbackReported(new JObject { ["rollbackAttempted"] = false, ["rolledBack"] = false }));
            Assert.False(PatchService.IsWriterRollbackReported(new JObject { ["rolledBack"] = true }));
            // GX18 U16 proof: on every refused Structure patch the writer's
            // RollbackFullWriteFailure also attaches an atomic `rollback` object
            // (attempted:false, rolledBack:true, atomic:true). The flag still wins.
            Assert.True(PatchService.IsWriterRollbackReported(new JObject
            {
                ["rollbackAttempted"] = true,
                ["rollback"] = new JObject { ["attempted"] = false, ["rolledBack"] = true, ["atomic"] = true }
            }));
            Assert.False(PatchService.IsWriterRollbackReported(new JObject
            {
                ["rollback"] = new JObject { ["attempted"] = false, ["rolledBack"] = true, ["atomic"] = true }
            }));
            Assert.False(PatchService.IsWriterRollbackReported(null));
        }

        [Fact]
        public void MarkWriterRollback_MergesIntoTheWritersAtomicRollbackObject()
        {
            var payload = new JObject
            {
                ["rollbackAttempted"] = true,
                ["rolledBack"] = true,
                ["unappliedMembers"] = new JArray("codigo : VARCHAR(40)"),
                ["rollback"] = new JObject
                {
                    ["requested"] = true, ["attempted"] = false, ["rolledBack"] = true, ["verified"] = true,
                    ["atomic"] = true, ["saveRequired"] = false, ["reReadConfirmed"] = true
                }
            };
            PatchPersistenceReceipt.MarkNotPersisted(payload, saved: true, verifyError: null);

            PatchPersistenceReceipt.MarkWriterRollback(payload, rolledBack: true, rollbackError: null, unappliedMembers: payload["unappliedMembers"] as JArray);

            var rollback = (JObject)payload["rollback"];
            Assert.Equal("write-service", rollback["source"]?.ToString());
            Assert.True(rollback["attempted"]?.Value<bool>());
            Assert.True(rollback["saveAttempted"]?.Value<bool>());
            Assert.True(rollback["rolledBack"]?.Value<bool>());
            // The writer's own evidence survives the merge.
            Assert.True(rollback["atomic"]?.Value<bool>());
            Assert.True(rollback["reReadConfirmed"]?.Value<bool>());
            Assert.False(rollback["saveRequired"]?.Value<bool>());
            Assert.Contains("reverted", payload["message"]?.ToString());
            Assert.Equal("RolledBack", payload["commitState"]?.ToString());
        }

        [Fact]
        public void MarkWriterRollback_RolledBack_ReportsRevertNotNormalization()
        {
            var payload = new JObject
            {
                ["rollbackAttempted"] = true,
                ["rolledBack"] = true,
                ["unappliedMembers"] = new JArray("codigo : NUMERIC(7)")
            };
            PatchPersistenceReceipt.MarkNotPersisted(payload, saved: true, verifyError: null, commentOnly: false, normalizedMatches: true);
            Assert.Contains("normalization", payload["message"]?.ToString());

            PatchPersistenceReceipt.MarkWriterRollback(
                payload, rolledBack: true, rollbackError: null, unappliedMembers: payload["unappliedMembers"] as JArray);

            string message = payload["message"]?.ToString();
            Assert.Equal("WriteNotPersisted", payload["code"]?.ToString());
            Assert.Contains("reverted", message);
            Assert.Contains("codigo : NUMERIC(7)", message);
            Assert.DoesNotContain("normalization", message);
            Assert.True(payload["rolledBack"]?.Value<bool>());
            Assert.True(payload["rollback"]?["rolledBack"]?.Value<bool>());
            Assert.True(payload["rollback"]?["verified"]?.Value<bool>());
            Assert.Equal("write-service", payload["rollback"]?["source"]?.ToString());
            Assert.Equal("RolledBack", payload["commitState"]?.ToString());
            Assert.Equal("codigo : NUMERIC(7)", payload["unappliedMembers"]?[0]?.ToString());
            Assert.False(payload["saved"]?.Value<bool>());
            Assert.False(payload["persistedVerified"]?.Value<bool>());
        }

        [Fact]
        public void MarkWriterRollback_NotRolledBack_ReportsCauseAndNoRestore()
        {
            var payload = new JObject();
            PatchPersistenceReceipt.MarkNotPersisted(payload, saved: true, verifyError: null);

            PatchPersistenceReceipt.MarkWriterRollback(
                payload, rolledBack: false, rollbackError: "re-read still lacks codigo : VARCHAR(40)", unappliedMembers: null);

            string message = payload["message"]?.ToString();
            Assert.Contains("could not be verified", message);
            Assert.Contains("re-read still lacks codigo : VARCHAR(40)", message);
            Assert.DoesNotContain("normalization", message);
            Assert.False(payload["rolledBack"]?.Value<bool>());
            Assert.False(payload["rollback"]?["verified"]?.Value<bool>());
            Assert.Equal("re-read still lacks codigo : VARCHAR(40)", payload["rollback"]?["error"]?.ToString());
            Assert.Null(payload["commitState"]);
            Assert.Null(payload["stateRestored"]);
        }

        [Fact]
        public void UpdateVisualReplace_TypeChangeMismatch_NamesMemberExpectedAndActual()
        {
            var requested = new JArray
            {
                new JObject { ["name"] = "codigo", ["type"] = "Numeric", ["length"] = 7, ["decimals"] = 0 },
                new JObject { ["name"] = "valor", ["type"] = "Numeric", ["length"] = 12, ["decimals"] = 2 }
            };
            // The SDK kept VARCHAR and applied only the length (the #542 report).
            var persisted = new JArray
            {
                new JObject { ["name"] = "codigo", ["type"] = "VARCHAR", ["length"] = 7, ["decimals"] = 0 },
                new JObject { ["name"] = "valor", ["type"] = "Numeric", ["length"] = 12, ["decimals"] = 2 }
            };

            Assert.False(SDTService.MatchesPlan(new JArray(), requested, persisted, false, out string detail));
            string message = SDTService.BuildStagedPlanMismatchMessage(detail);

            Assert.Contains("did not match the requested plan", message);
            Assert.Contains("member 'codigo' type mismatch", message);
            Assert.Contains("expected 'Numeric'", message);
            Assert.Contains("actual 'VARCHAR'", message);
        }
    }
}
