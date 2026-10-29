using System;
using System.Collections.Generic;
using System.Linq;
using GxMcp.TestSupport;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // #702: genexus_create action=save_as failed on a WebPanel at its Events part and gave no reason. Three separate
    // causes, each reproduced against a disposable KB:
    //   1. the parts were cloned alphabetically, Events before Variables and WebForm, so the SDK refused the first
    //      event that names a control or a variable the empty target did not have yet (src0265 "invalid attribute");
    //   2. the part was read with the default "mcp" page (~200 lines / 16 KB), so any larger part was cut short;
    //   3. the reason the part was refused travelled only in the envelope's top level, which the client-facing error
    //      shape drops, so the answer named the part and nothing else.
    public sealed class SaveAsCloneFidelityTests
    {
        [Fact]
        public void A_WebPanel_is_cloned_declarations_first_then_the_layout_then_the_code_that_uses_them()
        {
            // Alphabetical, the way PartAccessor.GetAvailableParts lists them; which parts an object has depends on the object.
            var alphabetical = new[] { "Conditions", "Documentation", "Events", "Help", "Rules", "Variables", "WebForm" };

            Assert.Equal(
                new[] { "Variables", "WebForm", "Conditions", "Rules", "Events", "Documentation", "Help" },
                SdkObjectCloner.OrderPartsForClone(alphabetical));
        }

        [Theory]
        [InlineData(new[] { "Documentation", "Events", "Help", "Rules", "Structure", "Variables", "WebForm" },
                    new[] { "Structure", "Variables", "WebForm", "Rules", "Events", "Documentation", "Help" })]
        [InlineData(new[] { "Documentation", "Help", "Layout", "Rules", "Source", "Variables" },
                    new[] { "Variables", "Layout", "Rules", "Source", "Documentation", "Help" })]
        [InlineData(new[] { "Documentation", "SDTStructure" },
                    new[] { "SDTStructure", "Documentation" })]
        [InlineData(new[] { "Documentation", "Styles", "Tokens" },
                    new[] { "Documentation", "Styles", "Tokens" })]
        public void The_order_holds_for_the_other_object_types_and_leaves_parts_it_does_not_know_where_they_were(string[] parts, string[] expected)
        {
            Assert.Equal(expected, SdkObjectCloner.OrderPartsForClone(parts));
        }

        [Fact]
        public void Ordering_never_drops_or_repeats_a_part_and_keeps_the_arrival_order_inside_a_rank()
        {
            var parts = new[] { "Zeta", "Alpha", "Events", "Variables", "Help", "WebForm", "Source", "Rules" };

            var ordered = SdkObjectCloner.OrderPartsForClone(parts);

            Assert.Equal(parts.OrderBy(p => p, StringComparer.Ordinal), ordered.OrderBy(p => p, StringComparer.Ordinal));
            Assert.Equal(new[] { "Variables", "WebForm", "Rules", "Events", "Source", "Zeta", "Alpha", "Help" }, ordered);
            Assert.Empty(SdkObjectCloner.OrderPartsForClone(null));
        }

        [Fact]
        public void The_production_cloner_hands_the_plan_and_the_clone_the_ordered_parts()
        {
            string source = RepoSource.WithoutComments("src", "GxMcp.Worker", "Services", "SaveAsService.cs");
            string findSource = SourceAssert.MethodBody(source, "public SaveAsService.SourceDescriptor FindSource(string name, string typeFilter)");

            Assert.Contains("Parts = OrderPartsForClone(parts)", findSource, StringComparison.Ordinal);
        }

        [Fact]
        public void A_part_is_read_in_full_for_the_clone_not_as_the_default_page()
        {
            string source = RepoSource.WithoutComments("src", "GxMcp.Worker", "Services", "SaveAsService.cs");
            string clonePart = SourceAssert.MethodBody(source, "public string ClonePart(string sourceName, string newName, string partName, string typeFilter)");

            // limit 0 is the explicit full read; null/null with client "mcp" is the ~200 lines / 16 KB page.
            Assert.Contains("_objects.ReadObjectSource(sourceName, partName, null, 0, \"mcp\"", clonePart, StringComparison.Ordinal);
            Assert.DoesNotContain("ReadObjectSource(sourceName, partName, null, null,", clonePart, StringComparison.Ordinal);
            Assert.Contains("RefuseTruncatedRead(readObj, partName)", clonePart, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData("truncated")]
        [InlineData("isTruncatedByWorker")]
        public void A_partial_read_is_refused_instead_of_written_as_the_whole_part(string flag)
        {
            var page = new JObject { ["source"] = "Event Start\n// ...", [flag] = true, ["limit"] = 200, ["totalLines"] = 2306 };

            var refusal = JObject.Parse(SdkObjectCloner.RefuseTruncatedRead(page, "Events"));

            Assert.Equal("error", refusal["status"]?.ToString());
            Assert.Equal("SourcePartTruncated", refusal["error"]?["code"]?.ToString());
            Assert.Contains("'Events'", refusal["error"]?["message"]?.ToString());
            Assert.Contains("200 of 2306", refusal["error"]?["message"]?.ToString());
        }

        [Fact]
        public void A_complete_read_is_not_refused()
        {
            Assert.Null(SdkObjectCloner.RefuseTruncatedRead(new JObject { ["source"] = "x", ["truncated"] = false, ["isTruncatedByWorker"] = false }, "Events"));
            Assert.Null(SdkObjectCloner.RefuseTruncatedRead(new JObject { ["source"] = "x" }, "Events"));
            Assert.Null(SdkObjectCloner.RefuseTruncatedRead(null, "Events"));
        }

        [Fact]
        public void The_reason_the_part_was_refused_reaches_the_client_inside_error_and_in_the_message()
        {
            // The SDK lists one validation error per line.
            var cloner = new FailingCloner(
                "{\"status\":\"error\",\"error\":{\"code\":\"TransactionFailed\",\"message\":\"SDK Save Exception: validation failed. Detailed Messages: \\r\\n[VALIDATION]: src0265: invalid attribute 'ViewAll' (Events, Line: 10, Char: 3)\\r\\nsrc0216: 'Visible' invalid property. (Events, Line: 59, Char: 19)\\r\\n\"}}");

            var json = JObject.Parse(new SaveAsService(cloner).SaveAs(new JObject { ["name"] = "PanelA", ["newName"] = "PanelACopy" }));

            var error = json["error"];
            Assert.Equal("SaveAsPartFailed", error?["code"]?.ToString());
            Assert.Equal("Events", error?["failedPart"]?.ToString());
            Assert.Contains("src0265: invalid attribute 'ViewAll'", error?["cause"]?.ToString());
            Assert.Contains("\n", error?["cause"]?.ToString());
            Assert.Equal("TransactionFailed", error?["causeCode"]?.ToString());
            // The default client-facing error keeps the first line of the message only: every validation error has to be on it.
            string message = error?["message"]?.ToString();
            Assert.DoesNotContain("\n", message);
            Assert.DoesNotContain("\r", message);
            Assert.Contains("src0265: invalid attribute 'ViewAll'", message);
            Assert.Contains("src0216: 'Visible' invalid property. (Events, Line: 59, Char: 19)", message);
            Assert.Contains("incomplete target was removed", message);
            Assert.True(error?["cleanup"]?["removed"]?.ToObject<bool>());
            Assert.Equal("PanelACopy", error?["newName"]?.ToString());
            // The top-level fields existing consumers read are still there.
            Assert.True(json["cleanup"]?["removed"]?.ToObject<bool>());
            Assert.Equal("Events", json["failedPart"]?.ToString());
        }

        [Fact]
        public void The_queued_post_save_index_update_does_not_bring_back_an_object_deleted_in_the_meantime()
        {
            // The post-save index update is queued when each part is written and runs after the command that queued
            // it. A refused save_as deletes its target inside that command, so without the check the update indexed
            // the deleted object again: query listed it and every read answered IndexedObjectUnavailable (#702).
            string source = RepoSource.WithoutComments("src", "GxMcp.Worker", "Services", "WriteService.cs");

            int guard = source.IndexOf("_objectService.IsAbsentFromModel(objToIndex)", StringComparison.Ordinal);
            int update = source.IndexOf("GetIndexCache().UpdateEntry(objToIndex)", StringComparison.Ordinal);

            Assert.True(guard > 0, "the queued post-save index update must check that the object is still in the model");
            Assert.True(update > guard, "the check must come before UpdateEntry");
            Assert.Equal(update, source.LastIndexOf("GetIndexCache().UpdateEntry(objToIndex)", StringComparison.Ordinal));
        }

        [Theory]
        [InlineData("{\"status\":\"Error\",\"error\":\"part write blew up\"}", null, "part write blew up")]
        [InlineData("{\"status\":\"error\",\"message\":\"top level message\",\"code\":\"X\"}", "X", "top level message")]
        // #746: the WebForm writer's errors travel in validationDiagnostics, not in its message; warnings stay out.
        [InlineData("{\"status\":\"error\",\"error\":{\"code\":\"WebFormValidationFailed\",\"message\":\"The WebForm SDK validator found errors.\"},\"validationDiagnostics\":[{\"severity\":\"Error\",\"message\":\"Unknown attribute 'BDIId'\",\"source\":\"GridWWP\"},{\"severity\":\"Warning\",\"message\":\"Theme class not found\"},{\"severity\":\"Error\",\"message\":\"Invalid property 'Visible'\"}]}",
            "WebFormValidationFailed", "The WebForm SDK validator found errors.\nGridWWP: Unknown attribute 'BDIId'\nInvalid property 'Visible'")]
        [InlineData("not json at all", null, "not json at all")]
        [InlineData("", null, null)]
        public void The_failure_summary_reads_each_envelope_shape_a_writer_answers_with(string inner, string code, string message)
        {
            SaveAsService.SummarizeFailure(inner, out string actualCode, out string actualMessage);

            Assert.Equal(code, actualCode);
            Assert.Equal(message, actualMessage);
        }

        [Fact]
        public void A_very_long_diagnostic_is_bounded()
        {
            string inner = new JObject { ["error"] = new JObject { ["message"] = new string('x', 5000) } }.ToString();

            SaveAsService.SummarizeFailure(inner, out _, out string message);

            Assert.True(message.Length <= 1203);
            Assert.EndsWith("...", message);
        }

        private sealed class FailingCloner : SaveAsService.IObjectCloner
        {
            private readonly string _partFailure;
            public FailingCloner(string partFailure) { _partFailure = partFailure; }
            private readonly HashSet<string> _existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            public SaveAsService.SourceDescriptor FindSource(string name, string typeFilter)
                => new SaveAsService.SourceDescriptor { Name = name, Type = "WebPanel", Parts = new List<string> { "Variables", "WebForm", "Events" } };

            public bool TargetExists(string newName) => _existing.Contains(newName);

            public string CreateObject(string type, string newName) { _existing.Add(newName); return "{\"status\":\"ok\"}"; }

            public string ClonePart(string sourceName, string newName, string partName, string typeFilter)
                => string.Equals(partName, "Events", StringComparison.OrdinalIgnoreCase) ? _partFailure : "{\"status\":\"ok\"}";

            public string DeleteTarget(string newName, string typeFilter) { _existing.Remove(newName); return "{\"status\":\"ok\"}"; }

            public SaveAsService.PatternInstanceDescriptor FindWwpInstance(string sourceName) => null;

            public string ApplyWwpPattern(string newName, SaveAsService.PatternInstanceDescriptor sourceInstance) => "{\"status\":\"ok\"}";
        }
    }
}
