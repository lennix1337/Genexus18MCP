using System;
using System.Collections.Generic;
using System.Linq;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// W3 (mcp-roadmap-ide-parity 2026-05-19) — first-class tool for the common
    /// "popup WebPanel with radio/combo/text inputs + Confirmar button" pattern.
    /// Takes a domain-level JObject spec and emits a WebPanel KBObject whose
    /// Form type="layout" body renders editable Radio/Combo controls (which a raw
    /// Form type="html" panel cannot — see LayoutGotchaScanner FR#2).
    ///
    /// Layered on top of existing primitives:
    ///   - ObjectService.CreateObject (creates the WebPanel KBObject)
    ///   - WriteService.AddVariable (one per inParm + one per input.varName, W5-validated)
    ///   - WriteService.WriteObject (Rules / WebForm / Events parts)
    ///
    /// Self-validates the generated layout XML through LayoutGotchaScanner; refuses
    /// to write if any structural gotcha would render the popup non-functional.
    /// </summary>
    public class PopupTemplateService
    {
        public interface IPopupBackend
        {
            string CreateObject(string type, string name);
            string AddVariable(string target, string varName, string typeName);
            string WriteObject(string target, string partName, string content);
            bool ObjectExists(string name);
            // Friction 2026-05-25 — returns the WWP theme-class GUID prefix
            // (e.g. "d4876646-98dd-419b-8c1c-896f83c48368") when the KB uses
            // the WorkWithPlus dual-form convention; null otherwise. Tests
            // return null so the existing flat-schema emit path stays exercised.
            GxMcp.Worker.Helpers.WwpConventionProbe.Result ProbeWwpConvention();
            // Issue #876 — the editable WebForm XML of an existing WebPanel, or null.
            string ReadWebFormXml(string name);
        }

        private sealed class DefaultBackend : IPopupBackend
        {
            private readonly ObjectService _obj;
            private readonly WriteService _write;
            public DefaultBackend(ObjectService obj, WriteService write) { _obj = obj; _write = write; }
            public string CreateObject(string type, string name) => _obj.CreateObject(type, name);
            public string AddVariable(string target, string varName, string typeName)
                => _write.AddVariable(target, varName, typeName);
            public string WriteObject(string target, string partName, string content)
                => _write.WriteObject(target, partName, content, "WebPanel", true, false, true, false);
            public bool ObjectExists(string name)
            {
                try { return _obj.FindObject(name, "WebPanel") != null; }
                catch { return false; }
            }
            public GxMcp.Worker.Helpers.WwpConventionProbe.Result ProbeWwpConvention()
            {
                try { return GxMcp.Worker.Helpers.WwpConventionProbe.Probe(_obj?.GetKbService()); }
                catch { return null; }
            }
            public string ReadWebFormXml(string name)
            {
                try
                {
                    var o = _obj.FindObject(name, "WebPanel");
                    return o == null ? null : WebFormXmlHelper.ReadEditableXml(o);
                }
                catch { return null; }
            }
        }

        private readonly IPopupBackend _backend;

        public PopupTemplateService(ObjectService objectService, WriteService writeService)
            : this(new DefaultBackend(objectService, writeService)) { }

        // Test seam — tests pass a fake backend.
        public PopupTemplateService(IPopupBackend backend)
        {
            _backend = backend;
        }

        public string CreatePopup(string name, JObject spec) => CreatePopup(name, spec, false);

        public string CreatePopup(string name, JObject spec, bool dryRun)
        {
            if (string.IsNullOrWhiteSpace(name))
                return Err("name is required");

            var parsed = PopupLayoutBuilder.ParseSpec(spec);
            if (!parsed.IsValid)
                return McpResponse.Err(
                    code: "InvalidSpec",
                    message: "Popup spec validation failed.",
                    hint: "Fix the spec errors listed in error.errors and retry.",
                    extra: new JObject { ["errors"] = new JArray(parsed.Errors) });

            var pspec = parsed.Spec;

            // 1) Build & self-validate the layout XML BEFORE touching the KB.
            //    LayoutGotchaScanner gates structural problems (Form type, cell-outside-table,
            //    unknown ControlType, duplicate ids). Use varNameResolver returning the matching
            //    input.varName for the placeholder var:N bindings we emit — though we use
            //    "&VarName" AttIDs (not var:N), so resolver is rarely hit.
            //
            //    Friction 2026-05-25 — KBs with the WorkWithPlus convention reject the flat
            //    layout body with "marca (table) não corresponde ao nome do elemento (detail)"
            //    (WebLayoutHandler.LoadPanelElement). When the probe identifies a WWP KB we
            //    emit the dual-form <detail><layout><table controlName tableType="Responsive"
            //    class="GUID-N"> structure with the harvested theme prefix. Probe failure or
            //    non-WWP KB falls back to the flat schema (which is what worked pre-fix).
            //
            //    Issue #876 — the KB-wide probe misses GeneXus 18 KBs without WorkWithPlus whose
            //    WebPanels already use <detail><layout>. The target's own WebForm decides when
            //    it exists; a new object is re-checked right after it is created (step 2).
            bool objectExisted = _backend.ObjectExists(name);
            GxMcp.Worker.Helpers.WwpConventionProbe.Result wwpProbe = null;
            if (objectExisted) wwpProbe = ProbeTargetForm(name);
            if (wwpProbe == null)
            {
                try { wwpProbe = _backend.ProbeWwpConvention(); } catch { /* best-effort */ }
            }
            string layoutXml = BuildLayout(pspec, wwpProbe);
            string gotchaRefusal = RefuseOnGotchas(layoutXml);
            if (gotchaRefusal != null) return gotchaRefusal;

            // Item 21 (friction 2026-05-22) — universal dryRun. Spec was parsed +
            // layout XML rendered + LayoutGotchaScanner cleared above; short-circuit
            // before the four mutating backend calls (CreateObject, AddVariable,
            // WriteObject Rules/WebForm/Events). The agent gets the same shape as
            // the live path but with the persisted bytes inlined so it can preview.
            if (dryRun)
            {
                string rulesPreview = PopupLayoutBuilder.BuildRulesSource(pspec) ?? string.Empty;
                string eventsPreview = PopupLayoutBuilder.BuildEventsSource(pspec) ?? string.Empty;
                return McpResponse.Ok(
                    target: name,
                    code: "DryRun",
                    result: new JObject
                    {
                        ["dryRun"] = true,
                        ["name"] = name,
                        ["type"] = "WebPanel",
                        ["layoutFormType"] = "layout",
                        ["inputs"] = pspec.Inputs.Count,
                        ["buttons"] = pspec.Buttons.Count,
                        ["wouldCreateObject"] = !objectExisted,
                        ["webFormXml"] = layoutXml,
                        ["rulesSource"] = rulesPreview,
                        ["eventsSource"] = eventsPreview,
                        ["hint"] = "Re-run without dryRun to persist."
                    });
            }

            var responseSteps = new JArray();

            // 2) Create the WebPanel if missing (idempotent).
            if (!objectExisted)
            {
                string createResult = _backend.CreateObject("WebPanel", name);
                responseSteps.Add(new JObject
                {
                    ["step"] = "create_object",
                    ["result"] = TryParse(createResult)
                });
                string createFailure = RefuseIfLastStepFailed(name, responseSteps, objectCreated: false);
                if (createFailure != null) return createFailure;

                if (wwpProbe == null || !wwpProbe.IsWwp)
                {
                    var targetProbe = ProbeTargetForm(name);
                    if (targetProbe != null)
                    {
                        wwpProbe = targetProbe;
                        layoutXml = BuildLayout(pspec, wwpProbe);
                        gotchaRefusal = RefuseOnGotchas(layoutXml);
                        if (gotchaRefusal != null) return gotchaRefusal;
                    }
                }
            }
            else
            {
                responseSteps.Add(new JObject
                {
                    ["step"] = "create_object",
                    ["result"] = new JObject { ["status"] = "ok", ["code"] = "Skipped", ["reason"] = "already exists" }
                });
            }

            // 3) Add variables — one per inParm + one per input.varName. Dedup by name.
            var addedVars = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in pspec.InParms ?? new List<string>())
            {
                var (vn, vt) = SplitParm(p);
                if (string.IsNullOrEmpty(vn) || !addedVars.Add(vn)) continue;
                string r = _backend.AddVariable(name, vn, vt);
                responseSteps.Add(new JObject { ["step"] = "add_variable", ["var"] = vn, ["type"] = vt, ["result"] = TryParse(r) });
                string failure = RefuseIfLastStepFailed(name, responseSteps, objectCreated: true);
                if (failure != null) return failure;
            }
            foreach (var inp in pspec.Inputs)
            {
                if (string.IsNullOrEmpty(inp.VarName) || !addedVars.Add(inp.VarName)) continue;
                string typeName = DefaultTypeForInput(inp);
                string r = _backend.AddVariable(name, inp.VarName, typeName);
                responseSteps.Add(new JObject { ["step"] = "add_variable", ["var"] = inp.VarName, ["type"] = typeName, ["result"] = TryParse(r) });
                string failure = RefuseIfLastStepFailed(name, responseSteps, objectCreated: true);
                if (failure != null) return failure;
            }

            // 4) Write Rules part.
            string rules = PopupLayoutBuilder.BuildRulesSource(pspec);
            if (!string.IsNullOrEmpty(rules))
            {
                string r = _backend.WriteObject(name, "Rules", rules);
                responseSteps.Add(new JObject { ["step"] = "write_rules", ["result"] = TryParse(r) });
                string rulesFailure = RefuseIfLastStepFailed(name, responseSteps, objectCreated: true);
                if (rulesFailure != null) return rulesFailure;
            }

            // 5) Write WebForm (layout) part.
            string wfResult = _backend.WriteObject(name, "WebForm", layoutXml);
            responseSteps.Add(new JObject { ["step"] = "write_webform", ["result"] = TryParse(wfResult) });
            string formFailure = RefuseIfLastStepFailed(name, responseSteps, objectCreated: true);
            if (formFailure != null) return formFailure;

            // 6) Write Events part.
            string events = PopupLayoutBuilder.BuildEventsSource(pspec);
            if (!string.IsNullOrEmpty(events))
            {
                string r = _backend.WriteObject(name, "Events", events);
                responseSteps.Add(new JObject { ["step"] = "write_events", ["result"] = TryParse(r) });
                string eventsFailure = RefuseIfLastStepFailed(name, responseSteps, objectCreated: true);
                if (eventsFailure != null) return eventsFailure;
            }

            // FR#18 (Stream G, v2.6.6): inline the popup-branch hint so the
            // agent learns the popup conventions (no Link() in Enter,
            // Cancel.OnClick = Hide(), ReturnTo() for values) at create
            // time — same string analyze mode=parent_context surfaces.
            return McpResponse.Ok(
                target: name,
                code: "PopupCreated",
                result: new JObject
                {
                    ["name"] = name,
                    ["type"] = "WebPanel",
                    ["layoutFormType"] = "layout",
                    ["inputs"] = pspec.Inputs.Count,
                    ["buttons"] = pspec.Buttons.Count,
                    ["popupHint"] = AnalyzeService.HintForOpenedAs("popup"),
                    ["steps"] = responseSteps
                });
        }

        private GxMcp.Worker.Helpers.WwpConventionProbe.Result ProbeTargetForm(string name)
        {
            string form = null;
            try { form = _backend.ReadWebFormXml(name); } catch { /* best-effort */ }
            return GxMcp.Worker.Helpers.WwpConventionProbe.FromWebFormSource(form, name);
        }

        private static string BuildLayout(PopupLayoutBuilder.PopupSpec pspec, GxMcp.Worker.Helpers.WwpConventionProbe.Result probe)
        {
            return probe != null && probe.IsWwp
                ? PopupLayoutBuilder.BuildWwpLayoutXml(pspec, probe.ThemeClassPrefix)
                : PopupLayoutBuilder.BuildLayoutXml(pspec);
        }

        private static string RefuseOnGotchas(string layoutXml)
        {
            var gotchas = LayoutGotchaScanner.Scan(layoutXml, _ => null);
            if (gotchas.Count == 0) return null;
            var arr = new JArray();
            foreach (var g in gotchas)
            {
                arr.Add(new JObject
                {
                    ["code"] = g.Code,
                    ["severity"] = g.Severity,
                    ["element"] = g.Element,
                    ["controlId"] = g.ControlId,
                    ["message"] = g.Message,
                    ["workaround"] = g.Workaround
                });
            }
            return McpResponse.Err(
                code: "LayoutGotcha",
                message: "Generated layout XML failed LayoutGotchaScanner self-validation. Refusing to write.",
                hint: "Review the gotchas list and fix the spec before retrying.",
                extra: new JObject { ["gotchas"] = arr });
        }

        // Issue #876 — a step that answered status=error used to be recorded and then
        // ignored, so the call reported PopupCreated over an empty WebForm. Stop at the
        // first failure and name the partial object.
        private static string RefuseIfLastStepFailed(string name, JArray steps, bool objectCreated)
        {
            var last = steps.Count == 0 ? null : steps[steps.Count - 1] as JObject;
            var result = last?["result"] as JObject;
            if (!string.Equals(result?["status"]?.ToString(), "error", StringComparison.OrdinalIgnoreCase))
                return null;

            string step = last["step"]?.ToString();
            string detail = result["error"]?["message"]?.ToString() ?? result["message"]?.ToString() ?? "step failed";
            return McpResponse.Err(
                code: "PopupIncomplete",
                message: "genexus_create_popup stopped at step '" + step + "': " + detail,
                hint: objectCreated
                    ? "WebPanel '" + name + "' exists with only the steps listed before the failure. Fix the cause in steps[].result.error, then delete the WebPanel and run genexus_create_popup again."
                    : "Nothing was written. Fix the cause in steps[].result.error and retry.",
                target: name,
                extra: new JObject
                {
                    ["failedStep"] = step,
                    ["objectExists"] = objectCreated,
                    ["steps"] = steps
                });
        }

        private static (string name, string type) SplitParm(string parm)
        {
            if (string.IsNullOrWhiteSpace(parm)) return (null, null);
            string s = parm.Trim().TrimStart('&');
            int colon = s.IndexOf(':');
            if (colon < 0) return (s, "Character(40)");
            string nm = s.Substring(0, colon).Trim();
            string tp = s.Substring(colon + 1).Trim();
            return (nm, string.IsNullOrEmpty(tp) ? "Character(40)" : tp);
        }

        private static string DefaultTypeForInput(PopupLayoutBuilder.PopupInput inp)
        {
            // Pick a type based on the option value lengths for radio/combo, fall back
            // to Character(80) for text inputs.
            if (inp.Type == "radio" || inp.Type == "combo")
            {
                int maxLen = 1;
                foreach (var o in inp.Options)
                    if (!string.IsNullOrEmpty(o?.Value)) maxLen = Math.Max(maxLen, o.Value.Length);
                return "Character(" + maxLen + ")";
            }
            return "Character(80)";
        }

        private static JToken TryParse(string json)
        {
            if (string.IsNullOrEmpty(json)) return JValue.CreateNull();
            try { return JToken.Parse(json); }
            catch { return new JValue(json); }
        }

        private static string Err(string message) =>
            McpResponse.Err(
                code: "InvalidArgs",
                message: message,
                hint: "Check the required arguments and retry.");
    }
}
