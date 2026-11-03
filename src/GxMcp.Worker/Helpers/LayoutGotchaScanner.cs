using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Artech.Architecture.Common.Objects;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// Static analysis for WebForm layouts that catches patterns the GeneXus HTML
    /// generator silently breaks (or runtime renders read-only) — issues that compile
    /// cleanly but fail at user-facing behavior. Driven by friction-report 2026-05-19
    /// findings: gxButton custom OnClickEvent ignored in &lt;Form type="html"&gt;; gxAttribute
    /// Radio/Combo rendered disabled when local var shadows a transaction attribute.
    ///
    /// Returns warnings the caller can attach to inspect/edit responses so the agent
    /// learns at the failing call instead of after a build + browser smoke cycle.
    /// </summary>
    public static class LayoutGotchaScanner
    {
        public sealed class Gotcha
        {
            public string Code;       // stable identifier for grep/dedup; PascalCase with "Gotcha" prefix
            public string Severity;   // "Warning" — these compile clean
            public string Element;
            public string ControlId;
            public string Message;
            public string Workaround;
            // Friction 2026-05-22 #62: resolvable resource the agent can fetch via
            // resources/read for the long-form explanation of this code. Populated
            // automatically by GotchaCodes.DocUrlFor(Code) at construction time
            // (set inline below; emit sites pass it through to the JSON envelope).
            public string DocUrl => GotchaCodes.DocUrlFor(Code);
        }

        /// <summary>
        /// Scans a layout XML and returns the list of detected gotchas. Empty list if
        /// none / on parse failure. KB-aware overload — uses the live model to resolve
        /// var:N bindings for descriptive messages.
        /// </summary>
        public static List<Gotcha> Scan(string layoutXml, KBObject obj)
        {
            return ScanInternal(layoutXml, attId => ResolveVarName(obj, attId));
        }

        /// <summary>
        /// Testable overload: accepts a delegate for var:N → variable name. The tests
        /// assembly does not reference Artech.Genexus.Common, so the KBObject overload
        /// can't run in unit tests.
        /// </summary>
        public static List<Gotcha> Scan(string layoutXml, Func<string, string> varNameResolver)
        {
            return ScanInternal(layoutXml, varNameResolver ?? (_ => null));
        }

        // Kept for backwards compatibility with callers that passed an attribute-existence
        // predicate (FR#2 originally hypothesized that shadowing was the cause). The predicate
        // is now ignored — see the comment on GotchaGxAttributeHtmlFormDiscreteReadOnly.
        public static List<Gotcha> Scan(string layoutXml, Func<string, string> varNameResolver, Func<string, bool> attributeExistsIgnored)
        {
            return ScanInternal(layoutXml, varNameResolver ?? (_ => null));
        }

        private static List<Gotcha> ScanInternal(string layoutXml, Func<string, string> varNameResolver)
        {
            var hits = new List<Gotcha>();
            if (string.IsNullOrWhiteSpace(layoutXml)) return hits;

            XDocument doc;
            try { doc = XDocument.Parse(layoutXml); }
            catch { return hits; }

            bool isHtmlForm = doc.Descendants("Form")
                .Any(f => string.Equals((string)f.Attribute("type"), "html", StringComparison.OrdinalIgnoreCase));

            // C18 — a pathologically deep element tree can blow the SDK's recursive
            // WebTag walker's stack (StackOverflowException → uncatchable worker crash).
            // Real layouts nest well under ~50 levels; a cap of 300 only trips on
            // malformed/adversarial input. Reject before the SDK ever parses it.
            if (doc.Root != null)
            {
                int maxDepth = MaxElementDepth(doc.Root, 1, 320);
                if (maxDepth >= 300)
                {
                    hits.Add(new Gotcha
                    {
                        Code = "GotchaLayoutNestingTooDeep",
                        Severity = "Error",
                        Element = doc.Root.Name.LocalName,
                        ControlId = null,
                        Message = $"Layout element nesting is {maxDepth}+ levels deep. This can overflow the GeneXus SDK's recursive layout parser and crash the worker process.",
                        Workaround = "Flatten the nesting — no legitimate WebForm layout needs hundreds of nested tables/controls."
                    });
                }
            }

            // Reserved — kept as a no-op placeholder so existing helpers that referenced an
            // attribute-existence cache (in the v2.5.1 shadow-hypothesis design) still compile
            // without runtime cost. The current detection does not require KB attribute lookup.

            foreach (var el in doc.Descendants())
            {
                string elName = el.Name.LocalName;

                // FR#1 — gxButton in Form type="html" only fires the Enter event; any custom
                // OnClickEvent value is dropped by the generator (renders data-gx-evt="5"). The
                // SDK accepts the XML attribute (build passes) but the wired event at runtime
                // is always Enter. ListaAti-style buttons use <action onClickEvent="'X'" /> in
                // Form type="layout"; gxBitmap eventGX="'X'" also works for custom events.
                if (isHtmlForm && elName.Equals("gxButton", StringComparison.OrdinalIgnoreCase))
                {
                    string ev = AttrAny(el, "OnClickEvent", "onClickEvent", "eventGX", "event");
                    if (!string.IsNullOrWhiteSpace(ev))
                    {
                        string normalized = ev.Trim().Trim('\'').Trim();
                        if (!normalized.Equals("Enter", StringComparison.OrdinalIgnoreCase)
                            && !normalized.Equals("Cancel", StringComparison.OrdinalIgnoreCase)
                            && !normalized.Equals("Refresh", StringComparison.OrdinalIgnoreCase)
                            && normalized.Length > 0)
                        {
                            hits.Add(new Gotcha
                            {
                                Code = "GotchaGxButtonHtmlFormCustomEvent",
                                Severity = "Warning",
                                Element = elName,
                                ControlId = (string)el.Attribute("id"),
                                Message = $"gxButton OnClickEvent=\"'{normalized}'\" will compile but the HTML generator " +
                                          "wires data-gx-evt=5 (Enter) regardless. Custom events are not supported on " +
                                          "gxButton inside <Form type=\"html\">.",
                                Workaround = "Use a <gxBitmap eventGX=\"'" + normalized + "'\" /> styled as a button, or " +
                                             "move the control to a <Form type=\"layout\"> table and use <action onClickEvent=\"'" + normalized + "'\" />."
                            });
                        }
                    }
                }

                // C18 — a gxButton nested inside another gxButton is structurally invalid
                // (a control leaf cannot contain a control). This malformed shape is a
                // plausible trigger for the unbounded recursion in the SDK's gxButton
                // event-reference resolver that takes down the whole worker process with a
                // StackOverflowException (uncatchable — no try/catch can save it, so it MUST
                // be rejected before it reaches ApplyEditableXml). Error severity → the
                // write path blocks the save.
                if (elName.Equals("gxButton", StringComparison.OrdinalIgnoreCase)
                    && el.Descendants().Any(d => d.Name.LocalName.Equals("gxButton", StringComparison.OrdinalIgnoreCase)))
                {
                    hits.Add(new Gotcha
                    {
                        Code = "GotchaGxButtonNestedInvalid",
                        Severity = "Error",
                        Element = elName,
                        ControlId = (string)el.Attribute("id"),
                        Message = "A <gxButton> contains another <gxButton>. This is structurally invalid and can crash the GeneXus SDK (worker process) while resolving the control's event reference.",
                        Workaround = "Flatten the layout so each <gxButton> is a sibling inside a table cell, not nested inside another button."
                    });
                }

                // FR#2 (revised 2026-05-19) — gxAttribute with ControlType="Radio Button" or
                // "Combo Box" in <Form type="html"> renders disabled in the HTML output (the
                // <input> gets disabled="" class="gx-disabled" data-gx-readonly=""), regardless
                // of the variable name or ReadOnly="False" / Enabled="True" attributes. Default
                // text-input ControlType is unaffected. Confirmed empirically: renaming
                // &Alu2RegProf → &RespRegProf (no attribute shadow) did NOT fix the disabled
                // render. The hypothesis that variable-name shadowing was the cause was WRONG —
                // the html-form generator simply never emits an editable radio/combo input.
                if (isHtmlForm && elName.Equals("gxAttribute", StringComparison.OrdinalIgnoreCase))
                {
                    string ctrlType = (string)el.Attribute("ControlType");
                    if (!string.IsNullOrWhiteSpace(ctrlType)
                        && (ctrlType.Equals("Radio Button", StringComparison.OrdinalIgnoreCase)
                            || ctrlType.Equals("Combo Box", StringComparison.OrdinalIgnoreCase)))
                    {
                        // Resolve var:N for the message (best-effort — the scanner still emits
                        // even if the binding can't be named).
                        string attId = (string)el.Attribute("AttID");
                        string varName = !string.IsNullOrWhiteSpace(attId) && attId.StartsWith("var:", StringComparison.OrdinalIgnoreCase)
                            ? varNameResolver(attId)
                            : null;
                        string bindingDesc = !string.IsNullOrEmpty(varName) ? "&" + varName : (attId ?? "(unbound)");

                        hits.Add(new Gotcha
                        {
                            Code = "GotchaGxAttributeHtmlFormDiscreteReadOnly",
                            Severity = "Warning",
                            Element = elName,
                            ControlId = (string)el.Attribute("id") ?? attId,
                            Message = $"gxAttribute ControlType=\"{ctrlType}\" bound to {bindingDesc} will render " +
                                      "disabled in the HTML output (the <input> gets disabled=\"\" + class=\"gx-disabled\"). " +
                                      "The html-form generator does not produce editable radio/combo widgets — " +
                                      "ReadOnly=\"False\" / Enabled=\"True\" are ignored here.",
                            Workaround = "Move the control to a <Form type=\"layout\"> and use the WWP table " +
                                         "pattern, OR render the radio/combo via a User Control, OR replace with " +
                                         "raw HTML <input type=\"radio\"> inside a gxTextBlock Format=\"HTML\" + JS " +
                                         "wiring back to a hidden gxAttribute (default ControlType, which IS editable)."
                        });
                    }
                }

                // W5 phase 2 rules — derived from W5 SDK probe (friction-report roadmap 2026-05-19).
                // These catch generator-silent breakages that compile clean and pass save-time
                // validation but render wrong / broken at runtime.

                // GotchaGxAttributeMissingDataField: gxAttribute with no AttID nor DataField →
                // SDK leaves a phantom control; generator emits markup but never binds it.
                if (elName.Equals("gxAttribute", StringComparison.OrdinalIgnoreCase))
                {
                    var attId = (string)el.Attribute("AttID");
                    var dataField = (string)el.Attribute("DataField");
                    if (string.IsNullOrWhiteSpace(attId) && string.IsNullOrWhiteSpace(dataField))
                    {
                        hits.Add(new Gotcha
                        {
                            Code = "GotchaGxAttributeMissingDataField",
                            Severity = "Warning",
                            Element = elName,
                            ControlId = (string)el.Attribute("id") ?? "(no-id)",
                            Message = "gxAttribute has neither AttID nor DataField — it will render but bind to nothing. " +
                                      "FixWebFormData keeps the element silently, masking the problem.",
                            Workaround = "Add AttID=\"var:N\" or DataField=\"<attributeName>\" so the control binds to a value."
                        });
                    }
                }

                // GotchaUnknownControlType: gxAttribute ControlType="..." with a misspelled value
                // (e.g. "RadioButton" without the space, "Combobox" all-lowercase) → SDK silently
                // falls back to default Edit. Known valid values:
                //   Edit, Text Box, Combo Box, Radio Button, Check Box, Calendar, Image,
                //   Picture, Hyperlink, Button, Static (legacy), Description, Embedded Page
                if (elName.Equals("gxAttribute", StringComparison.OrdinalIgnoreCase))
                {
                    var ctrlType = (string)el.Attribute("ControlType");
                    if (!string.IsNullOrWhiteSpace(ctrlType) && !_validControlTypes.Contains(ctrlType))
                    {
                        hits.Add(new Gotcha
                        {
                            Code = "GotchaUnknownControlType",
                            Severity = "Warning",
                            Element = elName,
                            ControlId = (string)el.Attribute("id") ?? (string)el.Attribute("AttID") ?? "(no-id)",
                            Message = $"gxAttribute ControlType=\"{ctrlType}\" is not a recognized SDK value. " +
                                      "Generator falls back to Edit; your intended control type is lost.",
                            Workaround = "Use one of: " + string.Join(", ", _validControlTypes.OrderBy(s => s)) + "."
                        });
                    }
                }

                // GotchaWebComponentMissingObjectCall: gxEmbeddedPage / gxWebComponent with no
                // ObjectCall — runtime renders an empty <div>, no embedded object loads.
                if (elName.Equals("gxEmbeddedPage", StringComparison.OrdinalIgnoreCase)
                    || elName.Equals("gxWebComponent", StringComparison.OrdinalIgnoreCase))
                {
                    var objCall = (string)el.Attribute("ObjectCall");
                    if (string.IsNullOrWhiteSpace(objCall))
                    {
                        hits.Add(new Gotcha
                        {
                            Code = "GotchaWebComponentMissingObjectCall",
                            Severity = "Warning",
                            Element = elName,
                            ControlId = (string)el.Attribute("id") ?? "(no-id)",
                            Message = elName + " has no ObjectCall attribute — runtime renders an empty <div>.",
                            Workaround = "Add ObjectCall=\"<ComponentName>.Create()\" (or equivalent factory call)."
                        });
                    }
                }

                // Item 6 (friction-report 2026-05-22): gxTextBlock Format="HTML" with <script>,
                // <iframe>, or <img onerror=...> inside its CDATA — GeneXus HTML generator
                // escapes these tags so they render as literal text instead of executing. The
                // write succeeds and builds clean, but the agent's JS never runs. Surface a
                // warning so the caller knows before a build+browser cycle.
                if (elName.Equals("gxTextBlock", StringComparison.OrdinalIgnoreCase))
                {
                    string fmt = (string)el.Attribute("Format");
                    if (string.Equals(fmt, "HTML", StringComparison.OrdinalIgnoreCase))
                    {
                        // Content may be in a CDATA section or as text; XLinq merges both into Value.
                        string content = el.Value ?? "";
                        if (ContainsEscapedHtmlPatterns(content))
                        {
                            hits.Add(new Gotcha
                            {
                                Code = "GotchaHtmlFormatScriptStripped",
                                Severity = "Warning",
                                Element = elName,
                                ControlId = (string)el.Attribute("id") ?? "(no-id)",
                                Message = "gxTextBlock Format=\"HTML\" contains <script>, <iframe>, or <img onerror=...> — " +
                                          "the GeneXus HTML generator escapes these tags on render so they appear as " +
                                          "literal text instead of executing. The write succeeds and builds clean, but " +
                                          "your JS will NOT run.",
                                Workaround = "Use <body onmousedown=\"...\"> + addEventListener for runtime JS injection. " +
                                             "Inline event attributes on raw HTML elements (e.g. <input type=\"radio\" onclick=\"...\">) " +
                                             "inside Format=\"HTML\" blocks ARE preserved. Only block-level script tags and " +
                                             "img onerror patterns are escaped."
                            });
                        }
                    }
                }
            }

            // Structural rules — run after element loop so we have the doc available.

            // GotchaCellOutsideTable / GotchaRowOutsideTable: <cell> or <row> with no <table>
            // ancestor → generator wraps silently OR drops the element.
            foreach (var el in doc.Descendants())
            {
                string elName = el.Name.LocalName;
                if (elName.Equals("cell", StringComparison.OrdinalIgnoreCase) || elName.Equals("row", StringComparison.OrdinalIgnoreCase))
                {
                    bool hasTableAncestor = el.Ancestors().Any(a =>
                        a.Name.LocalName.Equals("table", StringComparison.OrdinalIgnoreCase));
                    if (!hasTableAncestor)
                    {
                        hits.Add(new Gotcha
                        {
                            Code = "GotchaCellOutsideTable",
                            Severity = "Warning",
                            Element = elName,
                            ControlId = (string)el.Attribute("id") ?? "(no-id)",
                            Message = "<" + elName + "> has no <table> ancestor — generator wraps silently or drops the element. Layout structure may be malformed.",
                            Workaround = "Wrap " + elName + " in a <table>...<tbody>...</tbody></table> hierarchy."
                        });
                    }
                }
            }

            // GotchaDuplicateControlName: two elements with same id/Name. SDK auto-renames
            // via GetUniqueName during save, but the caller loses the reference they wrote.
            // Pre-empt by reporting which id was duplicated.
            var idGroups = doc.Descendants()
                .Where(e => !string.IsNullOrWhiteSpace((string)e.Attribute("id")))
                .GroupBy(e => (string)e.Attribute("id"), StringComparer.OrdinalIgnoreCase);
            foreach (var grp in idGroups.Where(g => g.Count() > 1))
            {
                hits.Add(new Gotcha
                {
                    Code = "GotchaDuplicateControlName",
                    Severity = "Warning",
                    Element = grp.First().Name.LocalName,
                    ControlId = grp.Key,
                    Message = $"id=\"{grp.Key}\" appears on {grp.Count()} elements. SDK auto-renames via GetUniqueName on save, so caller references may break silently.",
                    Workaround = "Make each id unique. If you intended several controls with the same logical role, suffix them (Btn1, Btn2, ...)."
                });
            }

            // GotchaGridConditionIgnored (issue #830): in a layout form the Conditions of a grid are not a <condition>
            // child or attribute: the SDK drops those (a write that carries one fails verification), and a caller who reads
            // only saved/persisted can take the failure for a save. The grid's Conditions are its ControlWhere property.
            // Say so before the save, not after a failed verification.
            foreach (var grid in doc.Descendants().Where(e => IsLayoutGridName(e.Name.LocalName) && !IsInsideHtmlForm(e)))
            {
                object carrier = (object)grid.Elements().FirstOrDefault(c => IsConditionCarrierName(c.Name.LocalName))
                    ?? grid.Attributes().FirstOrDefault(a => IsConditionCarrierName(a.Name.LocalName));
                if (carrier == null) continue;
                string shape = carrier is XElement child
                    ? "<" + child.Name.LocalName + "> child"
                    : "'" + ((XAttribute)carrier).Name.LocalName + "' attribute";
                hits.Add(new Gotcha
                {
                    Code = GotchaCodes.GotchaGridConditionIgnored,
                    Severity = "Warning",
                    Element = grid.Name.LocalName,
                    ControlId = (string)grid.Attribute("controlName") ?? (string)grid.Attribute("id"),
                    Message = "A " + shape + " on <" + grid.Name.LocalName + "> is not part of the layout format: the SDK drops it, so the write does not "
                              + "keep those Conditions.",
                    Workaround = "Set the grid's Conditions as its ControlWhere property: genexus_properties action=get control=<grid> propertyName=ControlWhere "
                                 + "returns the versionToken, then action=set with value=<expression>, expectedVersion and rollbackOnFailure=true."
                });
            }

            return hits;
        }

        private static bool IsLayoutGridName(string name)
            => name.Equals("grid", StringComparison.OrdinalIgnoreCase) || name.Equals("simplegrid", StringComparison.OrdinalIgnoreCase);

        private static bool IsConditionCarrierName(string name)
            => name.Equals("condition", StringComparison.OrdinalIgnoreCase)
               || name.Equals("conditions", StringComparison.OrdinalIgnoreCase)
               || name.Equals("where", StringComparison.OrdinalIgnoreCase);

        // A document can carry an html form and a layout form side by side: what counts is the form the element is in.
        private static bool IsInsideHtmlForm(XElement element)
        {
            var form = element.Ancestors().FirstOrDefault(a => a.Name.LocalName.Equals("Form", StringComparison.OrdinalIgnoreCase));
            return form != null && string.Equals((string)form.Attribute("type"), "html", StringComparison.OrdinalIgnoreCase);
        }

        // Valid ControlType values for gxAttribute, per SDK PropertyDescriptor.Converter.GetStandardValues().
        // Conservative set — extend if a legitimate type appears that's missing here.
        private static readonly HashSet<string> _validControlTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "Edit", "Text Box", "Combo Box", "Radio Button", "Check Box", "Calendar", "Image",
            "Picture", "Hyperlink", "Button", "Static", "Description", "Embedded Page",
            "Dynamic Combo Box", "List Box", "Multi Selection List Box", "Textarea", "Password"
        };

        // C18 — iterative (never recursive, so this guard can't itself overflow) max
        // element depth, short-circuited at `cap` so a huge tree isn't fully walked.
        private static int MaxElementDepth(XElement root, int startDepth, int cap)
        {
            int max = startDepth;
            var stack = new Stack<(XElement el, int depth)>();
            stack.Push((root, startDepth));
            while (stack.Count > 0)
            {
                var (el, depth) = stack.Pop();
                if (depth > max) max = depth;
                if (max >= cap) return max;
                foreach (var child in el.Elements())
                    stack.Push((child, depth + 1));
            }
            return max;
        }

        private static string AttrAny(XElement el, params string[] names)
        {
            foreach (var n in names)
            {
                var a = el.Attribute(n);
                if (a != null && !string.IsNullOrWhiteSpace(a.Value)) return a.Value;
            }
            return null;
        }

        // Item 6: patterns that GeneXus' HTML generator escapes inside gxTextBlock Format="HTML".
        // Matches: <script, </script, <iframe, </iframe, <img with onerror attribute.
        private static readonly Regex _rxEscapedHtml = new Regex(
            @"<\s*(/?script|/?iframe|img\b[^>]*\bonerror\s*=)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        internal static bool ContainsEscapedHtmlPatterns(string content)
        {
            if (string.IsNullOrEmpty(content)) return false;
            return _rxEscapedHtml.IsMatch(content);
        }

        // Resolve var:N to the variable name using the object's VariablesPart. Empty/null if
        // not resolvable. NOTE: GetVariableInternalId falls back to enumeration position when
        // SDK doesn't expose a real Id, so this may map the wrong variable in objects where
        // var:N != position. Best-effort.
        private static string ResolveVarName(KBObject obj, string attId)
        {
            if (obj == null || string.IsNullOrEmpty(attId)) return null;
            if (!attId.StartsWith("var:", StringComparison.OrdinalIgnoreCase)) return null;
            if (!int.TryParse(attId.Substring(4), out var id)) return null;
            return WebFormSchemaHints.LookupVarNameById(obj, id);
        }

    }
}
