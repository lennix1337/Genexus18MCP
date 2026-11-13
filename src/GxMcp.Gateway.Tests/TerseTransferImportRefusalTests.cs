using System.Linq;
using GxMcp.Gateway;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// A failed XPZ import must keep, through the terse error projection, what the Worker found: whether the SDK supplied any
    /// diagnostic and, for a fidelity failure, which object's WebForm differed and how the repair and the rollback ended.
    /// Without them the caller sees only the message and the hint, and cannot tell a problem of the package from one of the SDK.
    /// </summary>
    public class TerseTransferImportRefusalTests
    {
        private static JObject Mismatch(int index, int diffLength = 40, JObject? repairResponse = null)
        {
            var mismatch = new JObject
            {
                ["name"] = "SynthMaster" + index,
                ["part"] = "WebForm",
                ["initialDiff"] = "Child count differs at /GxMultiForm/Form " + new string('x', diffLength),
                ["repaired"] = false,
                ["rollbackAction"] = "delete_imported_object",
                ["rollbackSucceeded"] = true
            };
            mismatch["repairResponse"] = repairResponse ?? new JObject
            {
                ["status"] = "error",
                ["error"] = new JObject
                {
                    ["code"] = "VisualWriteFailed",
                    ["message"] = "Synthetic validation message",
                    ["hint"] = "synthetic hint"
                },
                ["verboseOnly"] = new string('y', 5000)
            };
            return mismatch;
        }

        // The envelope TransferService.Import returns for TransferImportFidelityFailed: code, message and hint under error, the
        // rest beside it. The repair answer is the whole envelope of the repair write, as the Worker attaches it.
        private static JObject FidelityFailure(int mismatches = 1, int diffLength = 40)
        {
            var list = new JArray();
            for (int i = 0; i < mismatches; i++) list.Add(Mismatch(i, diffLength));
            return new JObject
            {
                ["status"] = "error",
                ["error"] = new JObject
                {
                    ["code"] = "TransferImportFidelityFailed",
                    ["message"] = "The XPZ import completed, but one or more WebForm parts did not survive the SDK import unchanged.",
                    ["hint"] = "The affected existing objects were restored when possible; inspect the fidelity block before retrying."
                },
                ["imported"] = true,
                ["file"] = "C:\\synthetic\\package.xpz",
                ["sdkDiagnostics"] = new JArray(),
                ["fidelityVerified"] = false,
                ["fidelity"] = new JObject
                {
                    ["objectsChecked"] = mismatches + 1,
                    ["repaired"] = 1,
                    ["mismatches"] = list,
                    ["rollbackAttempted"] = true,
                    ["rollbackSucceeded"] = true
                }
            };
        }

        private static JObject Failure(string code, JArray diagnostics) => new JObject
        {
            ["status"] = "error",
            ["error"] = new JObject { ["code"] = code, ["message"] = "synthetic", ["hint"] = "synthetic hint" },
            ["imported"] = false,
            ["sdkDiagnostics"] = diagnostics,
            ["diagnosticsAvailable"] = true,
            ["diagnosticsTruncated"] = false
        };

        private static JObject Message(string severity, string text, string? code = null) =>
            new JObject { ["severity"] = severity, ["code"] = code, ["message"] = text };

        [Fact]
        public void Terse_fidelity_failure_keeps_the_object_the_diff_and_the_outcome_of_repair_and_rollback()
        {
            var trimmed = McpRouter.TrimErrorEnvelope(FidelityFailure(), verbose: false);

            Assert.Equal("TransferImportFidelityFailed", trimmed["code"]?.ToString());
            Assert.True(trimmed["imported"]?.ToObject<bool>());
            Assert.False(trimmed["fidelityVerified"]?.ToObject<bool>());
            var fidelity = Assert.IsType<JObject>(trimmed["fidelity"]);
            Assert.Equal(2, fidelity["objectsChecked"]?.ToObject<int>());
            Assert.Equal(1, fidelity["repaired"]?.ToObject<int>());
            Assert.True(fidelity["rollbackAttempted"]?.ToObject<bool>());
            Assert.True(fidelity["rollbackSucceeded"]?.ToObject<bool>());
            var mismatch = Assert.IsType<JObject>(Assert.Single((JArray)fidelity["mismatches"]!));
            Assert.Equal("SynthMaster0", mismatch["name"]?.ToString());
            Assert.Equal("WebForm", mismatch["part"]?.ToString());
            Assert.Contains("Child count differs", mismatch["initialDiff"]?.ToString());
            Assert.False(mismatch["repaired"]?.ToObject<bool>());
            Assert.Equal("delete_imported_object", mismatch["rollbackAction"]?.ToString());
            Assert.True(mismatch["rollbackSucceeded"]?.ToObject<bool>());
            // Why the repair failed, not the repair's whole answer.
            Assert.Equal("error", mismatch["repairStatus"]?.ToString());
            Assert.Equal("VisualWriteFailed", mismatch["repairCode"]?.ToString());
            Assert.Equal("Synthetic validation message", mismatch["repairMessage"]?.ToString());
            Assert.Null(mismatch["repairResponse"]);
        }

        [Fact]
        public void Terse_fidelity_failure_caps_the_mismatches_and_the_long_diffs_with_their_total()
        {
            var trimmed = McpRouter.TrimErrorEnvelope(FidelityFailure(mismatches: 25, diffLength: 5000), verbose: false);

            var fidelity = Assert.IsType<JObject>(trimmed["fidelity"]);
            var kept = (JArray)fidelity["mismatches"]!;
            Assert.Equal(10, kept.Count);
            Assert.Equal(25, fidelity["mismatchesTotal"]?.ToObject<int>());
            Assert.True(fidelity["mismatchesTruncated"]?.ToObject<bool>());
            string original = "Child count differs at /GxMultiForm/Form " + new string('x', 5000);
            Assert.Equal(original.Substring(0, 500) + "... (" + (original.Length - 500) + " more characters; verbose_errors keeps them)",
                kept[0]["initialDiff"]!.ToString());
        }

        [Theory]
        [InlineData(10, false)]
        [InlineData(11, true)]
        public void Terse_fidelity_failure_flags_the_mismatch_cut_only_when_something_was_cut(int mismatches, bool cut)
        {
            var fidelity = (JObject)McpRouter.TrimErrorEnvelope(FidelityFailure(mismatches), verbose: false)["fidelity"]!;

            Assert.Equal(10, ((JArray)fidelity["mismatches"]!).Count);
            Assert.Equal(cut, fidelity["mismatchesTruncated"] != null);
            Assert.Equal(cut, fidelity["mismatchesTotal"] != null);
        }

        [Fact]
        public void Terse_declined_import_says_that_the_sdk_supplied_no_diagnostic()
        {
            var declined = Failure("TransferImportDeclined", new JArray());
            declined["success"] = false;
            declined["diagnosticsAvailable"] = false;
            declined["fidelityVerified"] = false;
            declined["fidelity"] = new JObject { ["objectsChecked"] = 0 };

            var trimmed = McpRouter.TrimErrorEnvelope(declined, verbose: false);

            Assert.Equal("TransferImportDeclined", trimmed["code"]?.ToString());
            Assert.False(trimmed["imported"]?.ToObject<bool>());
            // An empty list is information: it is the answer "the SDK said nothing", so it is not dropped.
            Assert.Empty(Assert.IsType<JArray>(trimmed["sdkDiagnostics"]));
            Assert.False(trimmed["diagnosticsAvailable"]?.ToObject<bool>());
            Assert.False(trimmed["diagnosticsTruncated"]?.ToObject<bool>());
            Assert.Equal(0, trimmed["fidelity"]?["objectsChecked"]?.ToObject<int>());
        }

        [Fact]
        public void Terse_failed_import_keeps_the_sdk_messages_capped_with_their_total()
        {
            var diagnostics = new JArray();
            for (int i = 0; i < 30; i++) diagnostics.Add(Message("Error", new string('m', 2000), "synth" + i));
            var failed = Failure("TransferImportFailed", diagnostics);
            failed["imported"] = null;

            var trimmed = McpRouter.TrimErrorEnvelope(failed, verbose: false);

            var kept = Assert.IsType<JArray>(trimmed["sdkDiagnostics"]);
            Assert.Equal(20, kept.Count);
            Assert.Equal(30, trimmed["sdkDiagnosticsTotal"]?.ToObject<int>());
            Assert.True(trimmed["sdkDiagnosticsTruncated"]?.ToObject<bool>());
            Assert.Equal("Error", kept[0]["severity"]?.ToString());
            Assert.Equal("synth0", kept[0]["code"]?.ToString());
            Assert.StartsWith("mmm", kept[0]["message"]!.ToString());
            Assert.Equal(500 + "... (".Length + "1500 more characters; verbose_errors keeps them)".Length, kept[0]["message"]!.ToString().Length);
            Assert.True(trimmed["diagnosticsAvailable"]?.ToObject<bool>());
        }

        [Fact]
        public void Terse_failed_import_keeps_the_error_that_follows_many_information_lines()
        {
            // The SDK is asked for verbose output, and the exception of a failed import is the last record of the list.
            var diagnostics = new JArray();
            for (int i = 0; i < 40; i++) diagnostics.Add(Message("Information", "step " + i));
            diagnostics.Add(Message("Error", "REAL CAUSE", "InvalidOperationException"));

            var trimmed = McpRouter.TrimErrorEnvelope(Failure("TransferImportFailed", diagnostics), verbose: false);

            var kept = (JArray)trimmed["sdkDiagnostics"]!;
            Assert.Equal(20, kept.Count);
            Assert.Equal(41, trimmed["sdkDiagnosticsTotal"]?.ToObject<int>());
            Assert.Contains(kept, m => m["message"]?.ToString() == "REAL CAUSE");
        }

        [Fact]
        public void Terse_failed_import_keeps_an_error_over_a_crowd_of_warnings()
        {
            var diagnostics = new JArray();
            for (int i = 0; i < 25; i++) diagnostics.Add(Message("Warning", "warning " + i));
            diagnostics.Add(Message("Error", "REAL CAUSE"));

            var kept = (JArray)McpRouter.TrimErrorEnvelope(Failure("TransferImportDeclined", diagnostics), verbose: false)["sdkDiagnostics"]!;

            Assert.Equal(20, kept.Count);
            Assert.Contains(kept, m => m["message"]?.ToString() == "REAL CAUSE");
            Assert.DoesNotContain(kept, m => m["message"]?.ToString() == "warning 19");
        }

        [Fact]
        public void Terse_import_keeps_errors_then_warnings_then_the_rest_and_the_original_order()
        {
            var diagnostics = new JArray();
            for (int i = 0; i < 30; i++) diagnostics.Add(Message("Information", "info " + i));
            diagnostics.Insert(3, Message("Warning", "warn A"));
            diagnostics.Insert(10, Message("Error", "err A"));
            diagnostics.Insert(20, Message("Warning", "warn B"));
            diagnostics.Add(Message("Error", "err B"));

            var kept = (JArray)McpRouter.TrimErrorEnvelope(Failure("TransferImportDeclined", diagnostics), verbose: false)["sdkDiagnostics"]!;

            string[] messages = kept.Select(m => m["message"]!.ToString()).ToArray();
            Assert.Equal(20, messages.Length);
            Assert.Contains("err A", messages);
            Assert.Contains("err B", messages);
            Assert.Contains("warn A", messages);
            Assert.Contains("warn B", messages);
            // The 16 that remain are the first information lines, and every kept message keeps its place.
            Assert.Equal(16, messages.Count(m => m.StartsWith("info ")));
            Assert.Equal("info 0", messages.First(m => m.StartsWith("info ")));
            Assert.DoesNotContain("info 16", messages);
            Assert.Equal(messages.OrderBy(m => IndexIn(diagnostics, m)).ToArray(), messages);
        }

        private static int IndexIn(JArray diagnostics, string message)
        {
            for (int i = 0; i < diagnostics.Count; i++)
                if (diagnostics[i]["message"]?.ToString() == message) return i;
            return -1;
        }

        [Theory]
        [InlineData(20, false)]
        [InlineData(21, true)]
        public void Terse_import_flags_the_message_cut_only_when_something_was_cut(int messages, bool cut)
        {
            var diagnostics = new JArray();
            for (int i = 0; i < messages; i++) diagnostics.Add(Message("Information", "step " + i));

            var trimmed = McpRouter.TrimErrorEnvelope(Failure("TransferImportDeclined", diagnostics), verbose: false);

            Assert.Equal(20, ((JArray)trimmed["sdkDiagnostics"]!).Count);
            Assert.Equal(cut, trimmed["sdkDiagnosticsTruncated"] != null);
            Assert.Equal(cut, trimmed["sdkDiagnosticsTotal"] != null);
        }

        [Theory]
        [InlineData(499, false)]
        [InlineData(500, false)]
        [InlineData(501, true)]
        public void Terse_import_bounds_a_text_at_exactly_the_limit(int length, bool cut)
        {
            var diagnostics = new JArray { Message("Error", new string('t', length)) };

            string text = ((JArray)McpRouter.TrimErrorEnvelope(Failure("TransferImportDeclined", diagnostics), verbose: false)["sdkDiagnostics"]!)[0]["message"]!.ToString();

            Assert.Equal(cut, text.EndsWith("verbose_errors keeps them)"));
            if (!cut) Assert.Equal(length, text.Length);
        }

        [Fact]
        public void Terse_import_never_splits_a_surrogate_pair_when_it_cuts_a_text()
        {
            // 499 characters and then one emoji (two UTF-16 units) put the cut between the halves of the pair.
            var diagnostics = new JArray { Message("Error", new string('a', 499) + "\U0001F600" + new string('b', 50)) };

            string text = ((JArray)McpRouter.TrimErrorEnvelope(Failure("TransferImportDeclined", diagnostics), verbose: false)["sdkDiagnostics"]!)[0]["message"]!.ToString();

            Assert.Equal(new string('a', 499) + "... (52 more characters; verbose_errors keeps them)", text);
            Assert.DoesNotContain(text, c => char.IsSurrogate(c));
        }

        [Fact]
        public void Terse_import_finds_the_evidence_inside_the_canonical_error_object_too()
        {
            var envelope = new JObject
            {
                ["status"] = "error",
                ["error"] = new JObject
                {
                    ["code"] = "TransferImportFidelityFailed",
                    ["message"] = "synthetic",
                    ["hint"] = "synthetic hint",
                    ["imported"] = true,
                    ["fidelityVerified"] = false,
                    ["sdkDiagnostics"] = new JArray { Message("Warning", "inside error") },
                    ["fidelity"] = new JObject { ["objectsChecked"] = 1, ["mismatches"] = new JArray { Mismatch(0) } }
                }
            };

            var trimmed = McpRouter.TrimErrorEnvelope(envelope, verbose: false);

            Assert.True(trimmed["imported"]?.ToObject<bool>());
            Assert.False(trimmed["fidelityVerified"]?.ToObject<bool>());
            Assert.Equal("inside error", trimmed["sdkDiagnostics"]?[0]?["message"]?.ToString());
            Assert.Equal("SynthMaster0", trimmed["fidelity"]?["mismatches"]?[0]?["name"]?.ToString());
        }

        [Fact]
        public void Terse_import_reduces_a_repair_to_its_status_code_and_the_first_lines_of_its_message()
        {
            var repair = new JObject
            {
                ["status"] = "error",
                ["error"] = new JObject
                {
                    ["code"] = "WriteFailed",
                    ["message"] = "Validation failed:\n* first reason\n* second reason\nnot a reason line",
                    ["hint"] = "synthetic hint"
                }
            };
            var envelope = FidelityFailure();
            envelope["fidelity"]!["mismatches"]![0] = Mismatch(0, repairResponse: repair);

            var mismatch = (JObject)McpRouter.TrimErrorEnvelope(envelope, verbose: false)["fidelity"]!["mismatches"]![0]!;

            Assert.Equal("error", mismatch["repairStatus"]?.ToString());
            Assert.Equal("WriteFailed", mismatch["repairCode"]?.ToString());
            Assert.Equal("Validation failed: first reason; second reason", mismatch["repairMessage"]?.ToString());
        }

        [Fact]
        public void Terse_import_shows_that_a_repair_that_worked_did_not_fix_the_form()
        {
            // The repair write succeeded and the re-read still differs: the success code sits beside repaired=false.
            var repair = new JObject { ["status"] = "ok", ["code"] = "WriteApplied" };
            var envelope = FidelityFailure();
            envelope["fidelity"]!["mismatches"]![0] = Mismatch(0, repairResponse: repair);
            envelope["fidelity"]!["mismatches"]![0]!["repairDiff"] = "Attribute count differs";

            var mismatch = (JObject)McpRouter.TrimErrorEnvelope(envelope, verbose: false)["fidelity"]!["mismatches"]![0]!;

            Assert.Equal("ok", mismatch["repairStatus"]?.ToString());
            Assert.Equal("WriteApplied", mismatch["repairCode"]?.ToString());
            Assert.Null(mismatch["repairMessage"]);
            Assert.False(mismatch["repaired"]?.ToObject<bool>());
            Assert.Equal("Attribute count differs", mismatch["repairDiff"]?.ToString());
        }

        [Fact]
        public void Terse_import_bounds_a_long_repair_message_and_drops_a_repair_that_is_not_an_envelope()
        {
            var longRepair = new JObject
            {
                ["status"] = "error",
                ["error"] = new JObject { ["code"] = "WriteFailed", ["message"] = new string('r', 2000) }
            };
            var envelope = FidelityFailure(mismatches: 2);
            envelope["fidelity"]!["mismatches"]![0] = Mismatch(0, repairResponse: longRepair);
            envelope["fidelity"]!["mismatches"]![1]!["repairResponse"] = new JObject { ["raw"] = "not json" };

            var mismatches = (JArray)McpRouter.TrimErrorEnvelope(envelope, verbose: false)["fidelity"]!["mismatches"]!;

            Assert.Equal(500 + "... (".Length + "1500 more characters; verbose_errors keeps them)".Length, mismatches[0]["repairMessage"]!.ToString().Length);
            Assert.Null(mismatches[1]["repairResponse"]);
            Assert.Null(mismatches[1]["repairCode"]);
            Assert.Null(mismatches[1]["repairMessage"]);
            Assert.Null(mismatches[1]["repairStatus"]);
        }

        [Fact]
        public void Terse_import_keeps_a_message_that_is_not_an_object_bounded()
        {
            var diagnostics = new JArray { "plain " + new string('p', 700), 7 };

            var kept = (JArray)McpRouter.TrimErrorEnvelope(Failure("TransferImportDeclined", diagnostics), verbose: false)["sdkDiagnostics"]!;

            Assert.Equal(JTokenType.String, kept[0].Type);
            Assert.EndsWith("verbose_errors keeps them)", kept[0].ToString());
            Assert.Equal(7, kept[1].ToObject<int>());
        }

        [Fact]
        public void Terse_envelope_of_another_code_does_not_pick_up_the_import_evidence()
        {
            var other = FidelityFailure();
            other["error"]!["code"] = "WriteNotPersisted";

            var trimmed = McpRouter.TrimErrorEnvelope(other, verbose: false);

            Assert.Equal("WriteNotPersisted", trimmed["code"]?.ToString());
            Assert.Null(trimmed["fidelity"]);
            Assert.Null(trimmed["sdkDiagnostics"]);
            Assert.Null(trimmed["fidelityVerified"]);
        }

        [Fact]
        public void Verbose_failed_import_keeps_the_whole_envelope()
        {
            var failure = FidelityFailure();

            var verbose = McpRouter.TrimErrorEnvelope(failure, verbose: true);

            Assert.Same(failure, verbose);
            Assert.NotNull(verbose["fidelity"]?["mismatches"]?[0]?["repairResponse"]);
        }
    }
}
