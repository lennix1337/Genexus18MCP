using System.Collections.Generic;
using GxMcp.TestSupport;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // #746: the WebForm of a clone binds its controls to variables by id. A text clone writes the Variables first and
    // the target numbers them in the order it reads them, so a control can be bound to another variable with no error.
    // The clone rebinds each binding by name to the target's variable of that name, after the Variables are written and
    // before the WebForm is written, and reports the rebinding only when there is something to report.
    public sealed class SaveAsWebFormRemapTests
    {
        [Fact]
        public void The_clone_writes_the_webform_through_the_rebinding_with_the_target_read_again()
        {
            string source = RepoSource.WithoutComments("src", "GxMcp.Worker", "Services", "SaveAsService.cs");
            string clonePart = SourceAssert.MethodBody(source, "public string ClonePart(string sourceName, string newName, string partName, string typeFilter)");

            // The target is read inside the clone of the WebForm, after its Variables were written, and the text that is
            // written is the rebound one.
            Assert.Contains("WriteRemappedWebForm(", clonePart, System.StringComparison.Ordinal);
            Assert.Contains("VariableIdsOf(_objects.FindObject(newName, null))", clonePart, System.StringComparison.Ordinal);
            Assert.Contains("text => _writes.WriteObject(newName, partName, text, typeFilter)", clonePart, System.StringComparison.Ordinal);
        }

        [Fact]
        public void The_text_written_is_the_webform_with_its_bindings_rebound()
        {
            string written = null;

            var json = JObject.Parse(SdkObjectCloner.WriteRemappedWebForm(
                "PanelACopy",
                Form(Data("var:11"), "<data attribute=\"att:11\" />"),
                new[] { new SaveAsVariableIdRemap.NamedId("Eta", 11) },
                new[] { new SaveAsVariableIdRemap.NamedId("Eta", 14) },
                text => { written = text; return "{\"status\":\"ok\"}"; }));

            // The attribute that is not a binding keeps its number.
            Assert.Equal(Form(Data("var:14"), "<data attribute=\"att:11\" />"), written);
            Assert.Equal("ok", json["status"]?.ToString());
        }

        [Fact]
        public void A_variable_the_target_does_not_declare_refuses_the_webform_and_writes_nothing()
        {
            bool written = false;

            var json = JObject.Parse(SdkObjectCloner.WriteRemappedWebForm(
                "PanelACopy",
                Form(Data("var:15")),
                new[] { new SaveAsVariableIdRemap.NamedId("Lambda", 15) },
                new[] { new SaveAsVariableIdRemap.NamedId("Eta", 11) },
                text => { written = true; return "{\"status\":\"ok\"}"; }));

            Assert.False(written);
            Assert.Equal("error", json["status"]?.ToString());
            Assert.Equal("WebFormVariableNotInTarget", json["error"]?["code"]?.ToString());
            Assert.Contains("'Lambda'", json["error"]?["message"]?.ToString());
        }

        [Fact]
        public void A_rebound_control_is_reported_in_the_answer_of_the_write()
        {
            var json = JObject.Parse(SdkObjectCloner.WriteRemappedWebForm(
                "PanelACopy",
                Form(Data("var:11")),
                new[] { new SaveAsVariableIdRemap.NamedId("Eta", 11) },
                new[] { new SaveAsVariableIdRemap.NamedId("Eta", 14) },
                text => "{\"status\":\"ok\"}"));

            Assert.Equal(1, json["variableIdRemap"]?["remapped"]?.ToObject<int>());
        }

        [Fact]
        public void A_skipped_remap_writes_the_text_as_it_is_and_says_why()
        {
            string written = null;
            string form = Form(Data("var:11"));

            var json = JObject.Parse(SdkObjectCloner.WriteRemappedWebForm(
                "PanelACopy",
                form,
                new SaveAsVariableIdRemap.NamedId[0],
                new[] { new SaveAsVariableIdRemap.NamedId("Eta", 14) },
                text => { written = text; return "{\"status\":\"ok\"}"; }));

            Assert.Same(form, written);
            Assert.Equal(SaveAsVariableIdRemap.SourceVariablesEmpty, json["variableIdRemap"]?["skipped"]?.ToString());
        }

        [Fact]
        public void An_object_that_was_not_found_writes_the_text_as_it_is_and_says_why()
        {
            var json = JObject.Parse(SdkObjectCloner.WriteRemappedWebForm(
                "PanelACopy",
                Form(Data("var:11")),
                null,
                new[] { new SaveAsVariableIdRemap.NamedId("Eta", 14) },
                text => "{\"status\":\"ok\"}"));

            Assert.Equal(SaveAsVariableIdRemap.SourceOrTargetNotFound, json["variableIdRemap"]?["skipped"]?.ToString());
        }

        [Fact]
        public void A_binding_left_unmapped_is_reported_and_the_text_is_written_as_it_is()
        {
            string written = null;
            string form = Form(Data("var:99"));

            var json = JObject.Parse(SdkObjectCloner.WriteRemappedWebForm(
                "PanelACopy",
                form,
                new[] { new SaveAsVariableIdRemap.NamedId("Eta", 11) },
                new[] { new SaveAsVariableIdRemap.NamedId("Eta", 14) },
                text => { written = text; return "{\"status\":\"ok\"}"; }));

            Assert.Same(form, written);
            Assert.Equal(0, json["variableIdRemap"]?["remapped"]?.ToObject<int>());
            Assert.Equal(99, json["variableIdRemap"]?["unmapped"]?[0]?.ToObject<int>());
        }

        [Fact]
        public void An_other_reference_is_reported_and_the_text_is_written_as_it_is()
        {
            string written = null;
            string form = Form("<label>var:7</label>");

            var json = JObject.Parse(SdkObjectCloner.WriteRemappedWebForm(
                "PanelACopy",
                form,
                new[] { new SaveAsVariableIdRemap.NamedId("Eta", 11) },
                new[] { new SaveAsVariableIdRemap.NamedId("Eta", 14) },
                text => { written = text; return "{\"status\":\"ok\"}"; }));

            Assert.Same(form, written);
            Assert.Equal(7, json["variableIdRemap"]?["otherReferences"]?[0]?.ToObject<int>());
        }

        [Fact]
        public void A_webform_with_nothing_to_rebind_is_written_and_its_answer_is_returned_as_it_was()
        {
            const string writeAnswer = "{\"status\":\"ok\",\"result\":{\"name\":\"PanelACopy\"}}";

            string answer = SdkObjectCloner.WriteRemappedWebForm(
                "PanelACopy",
                Form(Data("var:11")),
                new[] { new SaveAsVariableIdRemap.NamedId("Eta", 11) },
                new[] { new SaveAsVariableIdRemap.NamedId("Eta", 11) },
                text => writeAnswer);

            Assert.Same(writeAnswer, answer);
        }

        [Fact]
        public void The_clone_reports_the_rebinding_when_a_control_changed_its_variable_id()
        {
            var cloner = new ScriptedCloner("{\"status\":\"ok\",\"variableIdRemap\":{\"remapped\":3,\"unmapped\":[99]}}");

            var json = JObject.Parse(new SaveAsService(cloner).SaveAs(new JObject { ["name"] = "PanelA", ["newName"] = "PanelACopy" }));

            Assert.Equal("SavedAs", json["code"]?.ToString());
            Assert.Equal(3, json["result"]?["variableIdRemap"]?["remapped"]?.ToObject<int>());
            Assert.Equal(99, json["result"]?["variableIdRemap"]?["unmapped"]?[0]?.ToObject<int>());
        }

        [Fact]
        public void The_clone_reports_a_skipped_rebinding_when_an_id_cannot_be_read()
        {
            var cloner = new ScriptedCloner("{\"status\":\"ok\",\"variableIdRemap\":{\"skipped\":\"IdUnavailable\"}}");

            var json = JObject.Parse(new SaveAsService(cloner).SaveAs(new JObject { ["name"] = "PanelA", ["newName"] = "PanelACopy" }));

            Assert.Equal("SavedAs", json["code"]?.ToString());
            Assert.Equal("IdUnavailable", json["result"]?["variableIdRemap"]?["skipped"]?.ToString());
        }

        [Fact]
        public void The_production_reader_takes_the_variable_id_from_its_primary_property_only()
        {
            string source = RepoSource.WithoutComments("src", "GxMcp.Worker", "Services", "SaveAsService.cs");
            string reader = SourceAssert.MethodBody(source, "private static IEnumerable<GxMcp.Worker.Helpers.SaveAsVariableIdRemap.NamedId> VariableIdsOf(global::Artech.Architecture.Common.Objects.KBObject obj)");

            Assert.Contains("PrimaryVariableId(", reader, System.StringComparison.Ordinal);
            Assert.DoesNotContain("GetVariableInternalId", reader, System.StringComparison.Ordinal);
        }

        [Fact]
        public void The_clone_says_nothing_about_variable_ids_when_no_control_changed_its_id()
        {
            var cloner = new ScriptedCloner("{\"status\":\"ok\"}");

            var json = JObject.Parse(new SaveAsService(cloner).SaveAs(new JObject { ["name"] = "PanelA", ["newName"] = "PanelACopy" }));

            Assert.Equal("SavedAs", json["code"]?.ToString());
            Assert.Null(json["result"]?["variableIdRemap"]);
        }

        [Fact]
        public void A_webform_variable_the_target_does_not_declare_refuses_the_part_and_the_clone_is_removed()
        {
            var cloner = new ScriptedCloner(
                "{\"status\":\"error\",\"error\":{\"code\":\"WebFormVariableNotInTarget\",\"message\":\"The variable 'Lambda' of the source WebForm is not declared in the target, so its controls cannot be bound to it.\"}}");

            var json = JObject.Parse(new SaveAsService(cloner).SaveAs(new JObject { ["name"] = "PanelA", ["newName"] = "PanelACopy" }));

            Assert.Equal("SaveAsPartFailed", json["error"]?["code"]?.ToString());
            Assert.Equal("WebForm", json["error"]?["failedPart"]?.ToString());
            Assert.Contains("'Lambda'", json["error"]?["message"]?.ToString());
            Assert.True(json["error"]?["cleanup"]?["removed"]?.ToObject<bool>());
            Assert.Contains("delete", cloner.Calls);
        }

        [Fact]
        public void The_webform_is_written_after_the_variables_when_the_parts_come_in_that_order()
        {
            var cloner = new ScriptedCloner("{\"status\":\"ok\"}");

            new SaveAsService(cloner).SaveAs(new JObject { ["name"] = "PanelA", ["newName"] = "PanelACopy" });

            Assert.Equal(new[] { "create", "part:Variables", "part:WebForm", "part:Events" }, cloner.Calls.GetRange(0, 4).ToArray());
        }

        [Fact]
        public void The_receipt_is_attached_when_a_control_was_rebound()
        {
            var answer = JObject.Parse(SdkObjectCloner.AttachVariableIdRemap("{\"status\":\"ok\"}", Remap(remapped: 2)));

            Assert.Equal(2, answer["variableIdRemap"]?["remapped"]?.ToObject<int>());
            Assert.Empty(answer["variableIdRemap"]["unmapped"].Children());
            Assert.Null(answer["variableIdRemap"]?["otherReferences"]);
        }

        [Fact]
        public void The_receipt_is_attached_when_a_binding_was_kept_unmapped_and_nothing_was_rebound()
        {
            var answer = JObject.Parse(SdkObjectCloner.AttachVariableIdRemap("{\"status\":\"ok\"}", Remap(unmapped: new[] { 99 })));

            Assert.Equal(0, answer["variableIdRemap"]?["remapped"]?.ToObject<int>());
            Assert.Equal(99, answer["variableIdRemap"]?["unmapped"]?[0]?.ToObject<int>());
        }

        [Fact]
        public void The_receipt_is_attached_when_only_other_references_remain()
        {
            var answer = JObject.Parse(SdkObjectCloner.AttachVariableIdRemap("{\"status\":\"ok\"}", Remap(other: new[] { 7 })));

            Assert.Equal(0, answer["variableIdRemap"]?["remapped"]?.ToObject<int>());
            Assert.Equal(7, answer["variableIdRemap"]?["otherReferences"]?[0]?.ToObject<int>());
        }

        [Fact]
        public void The_receipt_carries_the_reason_of_a_skipped_remap()
        {
            var answer = JObject.Parse(SdkObjectCloner.AttachVariableIdRemap("{\"status\":\"ok\"}", Remap(skipped: SaveAsVariableIdRemap.SourceVariablesEmpty)));

            Assert.Equal(SaveAsVariableIdRemap.SourceVariablesEmpty, answer["variableIdRemap"]?["skipped"]?.ToString());
            Assert.Null(answer["variableIdRemap"]?["remapped"]);
        }

        [Fact]
        public void The_answer_is_returned_as_it_was_when_there_is_nothing_to_report()
        {
            const string written = "{\"status\":\"ok\",\"result\":{\"name\":\"PanelACopy\"}}";

            Assert.Same(written, SdkObjectCloner.AttachVariableIdRemap(written, Remap()));
            Assert.Same(written, SdkObjectCloner.AttachVariableIdRemap(written, null));
        }

        [Fact]
        public void The_answer_is_returned_as_it_was_when_it_cannot_be_read()
        {
            Assert.Equal("not json", SdkObjectCloner.AttachVariableIdRemap("not json", Remap(remapped: 1)));
        }

        private static SaveAsVariableIdRemap.Outcome Remap(int remapped = 0, int[] unmapped = null, int[] other = null, string skipped = null)
        {
            var outcome = new SaveAsVariableIdRemap.Outcome { Xml = string.Empty, Remapped = remapped, Skipped = skipped };
            outcome.Unmapped.AddRange(unmapped ?? new int[0]);
            outcome.OtherReferences.AddRange(other ?? new int[0]);
            return outcome;
        }

        private static string Form(params string[] tags)
        {
            return "<GxMultiForm rootId=\"1\">\n" + string.Join("\n", tags) + "\n</GxMultiForm>";
        }

        private static string Data(string binding) => "<data attribute=\"" + binding + "\" />";

        // A cloner whose WebForm write answers as given, and which records the calls in the order they arrive.
        private sealed class ScriptedCloner : SaveAsService.IObjectCloner
        {
            private readonly string _webFormAnswer;

            public ScriptedCloner(string webFormAnswer)
            {
                _webFormAnswer = webFormAnswer;
            }

            public List<string> Calls { get; } = new List<string>();

            public SaveAsService.SourceDescriptor FindSource(string name, string typeFilter)
                => new SaveAsService.SourceDescriptor { Name = name, Type = "WebPanel", Parts = new List<string> { "Variables", "WebForm", "Events" } };

            public bool TargetExists(string newName) => false;

            public string CreateObject(string type, string newName)
            {
                Calls.Add("create");
                return "{\"status\":\"ok\"}";
            }

            public string ClonePart(string sourceName, string newName, string partName, string typeFilter)
            {
                Calls.Add("part:" + partName);
                return partName == "WebForm" ? _webFormAnswer : "{\"status\":\"ok\"}";
            }

            public string DeleteTarget(string newName, string typeFilter)
            {
                Calls.Add("delete");
                return "{\"status\":\"ok\"}";
            }

            public SaveAsService.PatternInstanceDescriptor FindWwpInstance(string sourceName) => null;

            public string ApplyWwpPattern(string newName, SaveAsService.PatternInstanceDescriptor sourceInstance) => "{\"status\":\"ok\"}";
        }
    }
}
