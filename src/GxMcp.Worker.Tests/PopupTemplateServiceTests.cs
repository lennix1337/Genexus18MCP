using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // W3 (mcp-roadmap-ide-parity 2026-05-19): PopupTemplateService produces an
    // IDE-equivalent WebPanel popup. Tests cover the pure layout builder + the
    // service orchestration via a fake IPopupBackend (no SDK / KB required).
    public class PopupTemplateServiceTests
    {
        private sealed class FakeBackend : PopupTemplateService.IPopupBackend
        {
            public HashSet<string> Existing { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public Func<string, string> WebFormXml { get; set; } = _ => null;
            public Func<string, string> WriteResult { get; set; } = _ => "{\"status\":\"Success\"}";
            public List<(string type, string name)> Creates { get; } = new List<(string, string)>();
            public List<(string target, string varName, string typeName)> Variables { get; } =
                new List<(string, string, string)>();
            public List<(string target, string part, string content)> Writes { get; } =
                new List<(string, string, string)>();

            public string CreateObject(string type, string name)
            {
                Creates.Add((type, name));
                Existing.Add(name);
                return "{\"status\":\"Success\"}";
            }

            public string AddVariable(string target, string varName, string typeName)
            {
                Variables.Add((target, varName, typeName));
                return "{\"status\":\"Success\"}";
            }

            public string WriteObject(string target, string partName, string content)
            {
                Writes.Add((target, partName, content));
                return WriteResult(partName);
            }

            public bool ObjectExists(string name) => Existing.Contains(name);
            public GxMcp.Worker.Helpers.WwpConventionProbe.Result ProbeWwpConvention() => null;
            public string ReadWebFormXml(string name) => Existing.Contains(name) ? WebFormXml(name) : null;
        }

        // === Sample spec used across tests (mirrors the UG popup the friction report describes). ===
        private static JObject UgSpec() => JObject.Parse(@"{
            ""title"": ""Registro Profissional"",
            ""description"": ""Antes de continuar, informe se você possui registro profissional."",
            ""inputs"": [
                {""type"":""radio"",""varName"":""RespRegProf"",""label"":""Possui registro?"",""options"":[
                    {""value"":""S"",""label"":""Sim""},
                    {""value"":""N"",""label"":""Não""},
                    {""value"":""X"",""label"":""Meu curso não possui conselho""}
                ]},
                {""type"":""text"",""varName"":""NumRegProf"",""label"":""Número do registro"",""showWhen"":""RespRegProf == 'S'""}
            ],
            ""buttons"": [{""caption"":""Confirmar"",""event"":""Enter""}],
            ""inParms"": [""Alu2AnoCad:Numeric(2)"", ""Alu2SemCad:Numeric(1)""],
            ""outParms"": [""RespRegProf"", ""NumRegProf""]
        }");

        // === ParseSpec ===

        [Fact]
        public void ParseSpec_ValidUgSpec_NoErrors()
        {
            var parsed = PopupLayoutBuilder.ParseSpec(UgSpec());
            Assert.True(parsed.IsValid, string.Join(", ", parsed.Errors));
            Assert.Equal(2, parsed.Spec.Inputs.Count);
            Assert.Equal(3, parsed.Spec.Inputs[0].Options.Count);
            Assert.Equal("RespRegProf == 'S'", parsed.Spec.Inputs[1].ShowWhen);
        }

        [Fact]
        public void ParseSpec_NoInputs_ReportsError()
        {
            var spec = new JObject { ["title"] = "X", ["inputs"] = new JArray() };
            var parsed = PopupLayoutBuilder.ParseSpec(spec);
            Assert.False(parsed.IsValid);
            Assert.Contains(parsed.Errors, e => e.Contains("input"));
        }

        [Fact]
        public void ParseSpec_RadioWithoutOptions_ReportsError()
        {
            var spec = new JObject
            {
                ["inputs"] = new JArray
                {
                    new JObject { ["type"] = "radio", ["varName"] = "X" }
                }
            };
            var parsed = PopupLayoutBuilder.ParseSpec(spec);
            Assert.False(parsed.IsValid);
            Assert.Contains(parsed.Errors, e => e.Contains("options"));
        }

        [Fact]
        public void ParseSpec_UnknownType_ReportsError()
        {
            var spec = new JObject
            {
                ["inputs"] = new JArray
                {
                    new JObject { ["type"] = "checkbox", ["varName"] = "X" }
                }
            };
            var parsed = PopupLayoutBuilder.ParseSpec(spec);
            Assert.False(parsed.IsValid);
            Assert.Contains(parsed.Errors, e => e.Contains("radio|combo|text"));
        }

        // === BuildLayoutXml ===

        [Fact]
        public void BuildLayoutXml_UgSpec_EmitsLayoutFormWithTableStructure()
        {
            var parsed = PopupLayoutBuilder.ParseSpec(UgSpec());
            var xml = PopupLayoutBuilder.BuildLayoutXml(parsed.Spec);
            var doc = XDocument.Parse(xml);

            // Form type="layout" — the W1/W3 contract; not "html".
            var form = doc.Descendants("Form").Single();
            Assert.Equal("layout", (string)form.Attribute("type"));

            // Top-level table with row/cell scaffolding.
            Assert.NotEmpty(doc.Descendants("table"));
            Assert.NotEmpty(doc.Descendants("row"));
            Assert.NotEmpty(doc.Descendants("cell"));

            // gxAttribute bound via &VarName, Radio Button ControlType + ControlValues for radio input.
            var radio = doc.Descendants("gxAttribute")
                .First(a => ((string)a.Attribute("AttID") ?? "").Contains("RespRegProf"));
            Assert.Equal("Radio Button", (string)radio.Attribute("ControlType"));
            var cv = (string)radio.Attribute("ControlValues");
            Assert.Contains("Sim,S", cv);
            Assert.Contains("X", cv); // "Meu curso..., X"

            // Text input: gxAttribute without ControlType (defaults to Edit, editable in any form type).
            var text = doc.Descendants("gxAttribute")
                .First(a => ((string)a.Attribute("AttID") ?? "").Contains("NumRegProf"));
            Assert.Null(text.Attribute("ControlType"));

            // Button rendered via <action onClickEvent="'Enter'" /> — IDE-equivalent in layout forms.
            var action = doc.Descendants("action").Single();
            Assert.Contains("Enter", (string)action.Attribute("onClickEvent"));
        }

        [Fact]
        public void BuildLayoutXml_GeneratedXml_PassesLayoutGotchaScanner()
        {
            // CRITICAL W3 constraint: generated layout must not trip any scanner gotcha
            // (would mean the IDE generator silently breaks it at runtime).
            var parsed = PopupLayoutBuilder.ParseSpec(UgSpec());
            var xml = PopupLayoutBuilder.BuildLayoutXml(parsed.Spec);
            var hits = LayoutGotchaScanner.Scan(xml, _ => null);
            Assert.True(hits.Count == 0,
                "Scanner gotchas in generated layout: " + string.Join("; ",
                    hits.ConvertAll(h => h.Code + " (" + h.Message + ")")));
        }

        // === BuildRulesSource ===

        [Fact]
        public void BuildRulesSource_EmitsParmInAndOut()
        {
            var parsed = PopupLayoutBuilder.ParseSpec(UgSpec());
            var rules = PopupLayoutBuilder.BuildRulesSource(parsed.Spec);
            Assert.Contains("in:&Alu2AnoCad", rules);
            Assert.Contains("in:&Alu2SemCad", rules);
            Assert.Contains("out:&RespRegProf", rules);
            Assert.Contains("out:&NumRegProf", rules);
            Assert.StartsWith("parm(", rules);
            Assert.Contains(");", rules);
        }

        // === BuildEventsSource ===

        [Fact]
        public void BuildEventsSource_WithShowWhen_EmitsRefreshVisibilityToggle()
        {
            var parsed = PopupLayoutBuilder.ParseSpec(UgSpec());
            var events = PopupLayoutBuilder.BuildEventsSource(parsed.Spec);
            Assert.Contains("Event Refresh", events);
            // Input index 1 (NumRegProf) has the showWhen, so its group is GrpInput1.
            Assert.Contains("GrpInput1.Visible = True", events);
            Assert.Contains("GrpInput1.Visible = False", events);
            Assert.Contains("&RespRegProf = 'S' then", events);
        }

        [Fact]
        public void BuildEventsSource_AlwaysIncludesEnterPlaceholder()
        {
            var parsed = PopupLayoutBuilder.ParseSpec(UgSpec());
            var events = PopupLayoutBuilder.BuildEventsSource(parsed.Spec);
            Assert.Contains("Event Enter", events);
            Assert.Contains("// TODO: agent provides Enter event body", events);
            Assert.Contains("EndEvent", events);
        }

        [Fact]
        public void BuildEventsSource_NoShowWhen_OmitsRefreshEvent()
        {
            var spec = JObject.Parse(@"{
                ""inputs"": [{""type"":""text"",""varName"":""X"",""label"":""x""}],
                ""buttons"": [{""caption"":""Go"",""event"":""Enter""}]
            }");
            var parsed = PopupLayoutBuilder.ParseSpec(spec);
            var events = PopupLayoutBuilder.BuildEventsSource(parsed.Spec);
            Assert.DoesNotContain("Event Refresh", events);
            Assert.Contains("Event Enter", events);
        }

        // === CreatePopup (service) ===

        [Fact]
        public void CreatePopup_MissingName_ReturnsError()
        {
            var svc = new PopupTemplateService(new FakeBackend());
            var json = svc.CreatePopup("", UgSpec());
            var resp = JObject.Parse(json);
            Assert.Equal("error", (string)resp["status"]);
            Assert.Equal("InvalidArgs", (string)resp["error"]["code"]);
        }

        [Fact]
        public void CreatePopup_InvalidSpec_ReturnsError()
        {
            var svc = new PopupTemplateService(new FakeBackend());
            var json = svc.CreatePopup("Foo", new JObject { ["inputs"] = new JArray() });
            var resp = JObject.Parse(json);
            Assert.Equal("error", (string)resp["status"]);
            Assert.Equal("InvalidSpec", (string)resp["error"]["code"]);
        }

        [Fact]
        public void CreatePopup_UgSpec_CreatesObjectAndExpectedVariables()
        {
            var backend = new FakeBackend();
            var svc = new PopupTemplateService(backend);

            var json = svc.CreatePopup("RegProfAlunoUGPopup", UgSpec());
            var resp = JObject.Parse(json);

            Assert.Equal("ok", (string)resp["status"]);
            Assert.Equal("layout", (string)resp["result"]["layoutFormType"]);

            // Object created exactly once.
            Assert.Single(backend.Creates);
            Assert.Equal("WebPanel", backend.Creates[0].type);
            Assert.Equal("RegProfAlunoUGPopup", backend.Creates[0].name);

            // Variables: Alu2AnoCad, Alu2SemCad (from inParms) + RespRegProf, NumRegProf (from inputs).
            var varNames = backend.Variables.ConvertAll(v => v.varName);
            Assert.Contains("Alu2AnoCad", varNames);
            Assert.Contains("Alu2SemCad", varNames);
            Assert.Contains("RespRegProf", varNames);
            Assert.Contains("NumRegProf", varNames);

            // Inparm typeName preserved.
            var anoCad = backend.Variables.Find(v => v.varName == "Alu2AnoCad");
            Assert.Equal("Numeric(2)", anoCad.typeName);

            // Radio Character(N) sized by widest option value (here all 1 char).
            var resp1 = backend.Variables.Find(v => v.varName == "RespRegProf");
            Assert.StartsWith("Character(", resp1.typeName);

            // Parts written: Rules, WebForm, Events.
            var parts = backend.Writes.ConvertAll(w => w.part);
            Assert.Contains("Rules", parts);
            Assert.Contains("WebForm", parts);
            Assert.Contains("Events", parts);
        }

        [Fact]
        public void CreatePopup_ObjectAlreadyExists_SkipsCreate()
        {
            var backend = new FakeBackend();
            backend.Existing.Add("RegProfAlunoUGPopup");
            var svc = new PopupTemplateService(backend);

            svc.CreatePopup("RegProfAlunoUGPopup", UgSpec());
            Assert.Empty(backend.Creates);
            // But still writes parts.
            Assert.Contains(backend.Writes, w => w.part == "WebForm");
        }

        // === Issue #876 ===

        private const string DetailLayoutForm =
            "<GxMultiForm rootId=\"1\" version=\"html:15.0.0;layout:17.11.0\"><Form id=\"1\" type=\"layout\"><detail>" +
            "<layout id=\"29de02eb-d8c3-4b4c-8fa9-15588237a2e5\"><table id=\"fda5f7bd-8647-46f9-997c-4bd404f3064e\" controlName=\"MainTable\" tableType=\"Responsive\" />" +
            "</layout></detail></Form></GxMultiForm>";

        [Fact]
        public void CreatePopup_FailedWebFormWrite_ReportsPopupIncompleteAndStops()
        {
            var backend = new FakeBackend
            {
                WriteResult = part => part == "WebForm"
                    ? "{\"status\":\"error\",\"error\":{\"code\":\"VisualWriteFailed\",\"message\":\"Visual write failed\"}}"
                    : "{\"status\":\"ok\"}"
            };
            var resp = JObject.Parse(new PopupTemplateService(backend).CreatePopup("PopupX", UgSpec()));

            Assert.Equal("error", (string)resp["status"]);
            Assert.Equal("PopupIncomplete", (string)resp["error"]["code"]);
            Assert.Contains("write_webform", (string)resp["error"]["message"]);
            Assert.DoesNotContain(backend.Writes, w => w.part == "Events");
        }

        [Fact]
        public void CreatePopup_NewWebPanelWithDetailLayout_WritesTheDualFormSchema()
        {
            var backend = new FakeBackend { WebFormXml = _ => DetailLayoutForm };
            new PopupTemplateService(backend).CreatePopup("PopupX", UgSpec());

            string form = backend.Writes.Single(w => w.part == "WebForm").content;
            Assert.Contains("<detail>", form);
            Assert.Contains("attribute=\"&amp;RespRegProf\"", form);
        }

        [Fact]
        public void CreatePopup_ExistingWebPanelWithDetailLayout_WritesTheDualFormSchema()
        {
            var backend = new FakeBackend { WebFormXml = _ => DetailLayoutForm };
            backend.Existing.Add("PopupX");
            new PopupTemplateService(backend).CreatePopup("PopupX", UgSpec());

            Assert.Contains("<detail>", backend.Writes.Single(w => w.part == "WebForm").content);
        }

        [Fact]
        public void CreatePopup_FlatWebForm_KeepsTheFlatSchema()
        {
            var backend = new FakeBackend { WebFormXml = _ => "<GxMultiForm><Form id=\"1\" type=\"layout\"><table id=\"TblMain\"/></Form></GxMultiForm>" };
            new PopupTemplateService(backend).CreatePopup("PopupX", UgSpec());

            Assert.DoesNotContain("<detail>", backend.Writes.Single(w => w.part == "WebForm").content);
        }
    }
}
