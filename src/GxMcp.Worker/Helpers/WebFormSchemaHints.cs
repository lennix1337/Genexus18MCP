using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Artech.Architecture.Common.Objects;

namespace GxMcp.Worker.Helpers
{
    // Best-effort element→accepted-attribute hints derived from observed SDK
    // persistence and publish/worker/Definitions. A missing hint is explicitly
    // unverified, never a guarantee that the SDK will sanitize the attribute.
    public static class WebFormSchemaHints
    {
        private static readonly string[] _commonCtrlAttrs =
        {
            "id", "name", "ControlName", "controlName", "AttID", "Class", "classref", "Width", "Height", "Visible", "Tooltip"
        };

        private static string[] WithCommon(params string[] extras)
        {
            var arr = new string[_commonCtrlAttrs.Length + extras.Length];
            System.Array.Copy(_commonCtrlAttrs, arr, _commonCtrlAttrs.Length);
            System.Array.Copy(extras, 0, arr, _commonCtrlAttrs.Length, extras.Length);
            return arr;
        }

        // Lookup is case-insensitive so mixed-case markup ("WIDTH" vs "Width") still resolves.
        private static readonly Dictionary<string, string[]> _accepted = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            // Legacy HTML WebForms use a deliberately permissive shape. These are
            // observed SDK-persisted attributes, not a claim that the modern
            // GxMultiForm schema accepts every HTML presentation attribute.
            ["table"]          = new[] { "id", "name", "ControlName", "controlName", "classref", "Class", "AttID", "cellPadding", "cellSpacing", "Width", "Height", "BackColor", "ForeColor", "Border", "AutoGrow", "Background", "BackgroundType", "align", "vAlign", "borderColor", "borderStyle", "title", "style" },
            ["gxAttribute"]    = WithCommon("CaptionExpression", "DataField", "ReadOnly", "ControlType", "Format", "Event", "ReturnOnClick", "GxFormat", "OnClickEvent", "OnEnterEvent", "Caption"),
            ["gxTextBlock"]    = WithCommon("CaptionExpression", "Format", "Event", "ReturnOnClick", "GxFormat", "OnClickEvent", "OnEnterEvent", "Caption", "InnerText", "align", "vAlign"),
            ["gxButton"]       = WithCommon("CaptionExpression", "OnClickEvent", "Event", "Enabled", "ReturnOnClick", "GxFormat", "Caption", "OnEnterEvent", "action", "type"),
            ["gxBitmap"]       = WithCommon("ImageData", "Event", "OnClickEvent", "OnEnterEvent", "ReturnOnClick", "GxFormat"),
            ["gxImage"]        = WithCommon("ImageData", "Event", "OnClickEvent", "OnEnterEvent", "ReturnOnClick", "GxFormat"),
            ["gxGrid"]         = WithCommon("DataField", "Rows", "Columns", "AllowSelection", "AllowOrdering", "CaptionExpression", "Caption", "Event", "OnClickEvent"),
            ["gxTab"]          = WithCommon("CaptionExpression", "Caption", "Event", "OnClickEvent", "Selected", "ReturnOnClick"),
            ["gxCard"]         = WithCommon("CaptionExpression", "Caption", "Event", "OnClickEvent", "ReturnOnClick"),
            ["gxGroup"]        = WithCommon("CaptionExpression", "Caption", "Event", "OnClickEvent", "ReturnOnClick"),
            ["gxEmbeddedPage"] = WithCommon("ObjectCall", "Event", "OnClickEvent", "ReturnOnClick"),
            ["tr"]             = new[] { "Height", "align", "vAlign" },
            ["td"]             = new[] { "id", "ColSpan", "RowSpan", "Width", "Height", "HAlign", "VAlign", "ClassRef", "Class", "align", "vAlign", "nowrap", "title" },
            ["row"]            = new[] { "Height", "HAlign", "VAlign" },
            ["cell"]           = new[] { "id", "ColSpan", "RowSpan", "Width", "Height", "HAlign", "VAlign", "ClassRef", "Class", "align", "vAlign", "nowrap" },
        };

        // null = no hint registered for this element; caller treats as "SDK is authoritative".
        public static string[] GetAcceptedAttributes(string elementName)
        {
            if (string.IsNullOrEmpty(elementName)) return null;
            return _accepted.TryGetValue(elementName, out var attrs) ? attrs : null;
        }

        // Walks the XML, flags any attribute outside the element's accept-list. Silent on
        // parse failure (the writer surfaces XML errors via a more specific code path).
        public static List<SuspectAttribute> ScanForRejectedAttributes(string xml)
        {
            var hits = new List<SuspectAttribute>();
            if (string.IsNullOrWhiteSpace(xml)) return hits;
            XDocument doc;
            try { doc = XDocument.Parse(xml); }
            catch { return hits; }

            foreach (var el in doc.Descendants())
            {
                var accepted = GetAcceptedAttributes(el.Name.LocalName);
                if (accepted == null) continue; // no hint registered → can't judge
                var acceptedSet = new HashSet<string>(accepted, StringComparer.OrdinalIgnoreCase);
                foreach (var a in el.Attributes())
                {
                    if (acceptedSet.Contains(a.Name.LocalName)) continue;
                    hits.Add(new SuspectAttribute
                    {
                        Element = el.Name.LocalName,
                        Attribute = a.Name.LocalName,
                        Reason = "Attribute is not in the hint table (unverified); SDK persistence is not guaranteed.",
                    });
                }
            }
            return hits;
        }

        public sealed class SuspectAttribute
        {
            public string Element;
            public string Attribute;
            public string Reason;
        }

        // ── Task 4.5 (v2.3.8) — Ghost-binding diagnostics + [var:N] resolver ─────
        //
        // GeneXus layout XML references variables by an internal numeric id
        // (e.g. AttID="var:64"). When the SDK rejects a delete/modify because a
        // control still binds to the variable, the surfaced message embeds the
        // raw [var:N] token. ResolveVarBindings substitutes those tokens with
        // the symbolic "&Name" form so callers don't have to perform a manual
        // lookup. Unknown ids are tagged "[var:N (unresolved)]" rather than
        // silently dropped, keeping the diagnostic actionable.

        private static readonly Regex _varBindingRegex = new Regex(
            @"\[var:(\d+)\]",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>
        /// Substitutes <c>[var:N]</c> tokens in <paramref name="message"/> with
        /// the matching variable's symbolic name (<c>&amp;Name</c>) using
        /// <see cref="VariableInjector.GetVariableInternalId"/> against the
        /// object's VariablesPart. Returns <paramref name="message"/> unchanged
        /// when null/empty; ids that don't resolve become <c>[var:N (unresolved)]</c>.
        /// </summary>
        public static string ResolveVarBindings(string message, KBObject obj)
        {
            if (string.IsNullOrEmpty(message)) return message;
            return ResolveVarBindings(message, BuildLookup(obj));
        }

        /// <summary>
        /// Testable overload: takes a delegate mapping internal id → variable
        /// name (or null when unknown). The tests assembly does not reference
        /// <c>Artech.Genexus.Common</c>, so the KBObject overload cannot be
        /// exercised in unit tests without an SDK install.
        /// </summary>
        public static string ResolveVarBindings(string message, Func<int, string> lookup)
        {
            if (string.IsNullOrEmpty(message)) return message;
            if (lookup == null) lookup = _ => null;
            return _varBindingRegex.Replace(message, m =>
            {
                if (!int.TryParse(m.Groups[1].Value, out var id)) return m.Value;
                var name = lookup(id);
                return !string.IsNullOrEmpty(name)
                    ? "&" + name
                    : $"[var:{id} (unresolved)]";
            });
        }

        /// <summary>
        /// Returns the symbolic variable name for the given internal id, or
        /// null when no variable on the object matches.
        /// </summary>
        public static string LookupVarNameById(KBObject obj, int id)
        {
            return BuildLookup(obj)(id);
        }

        // Walks VariablesPart once, caching id → name so a regex with multiple
        // [var:N] tokens doesn't re-iterate the part. Returns a delegate that
        // always answers null when the part is missing.
        private static Func<int, string> BuildLookup(KBObject obj)
        {
            if (obj == null) return _ => null;
            global::Artech.Genexus.Common.Parts.VariablesPart part;
            try { part = Structure.PartAccessor.GetVariablesPart(obj); }
            catch { return _ => null; }
            if (part == null) return _ => null;

            var map = new Dictionary<int, string>();
            int index = 1;
            foreach (var v in part.Variables)
            {
                int? id = VariableInjector.GetVariableInternalId(v, index);
                if (id.HasValue && !map.ContainsKey(id.Value))
                    map[id.Value] = v.Name;
                index++;
            }
            return key => map.TryGetValue(key, out var name) ? name : null;
        }

        /// <summary>
        /// Scans <paramref name="webFormXml"/> for attribute values referencing
        /// <c>var:<paramref name="variableId"/></c> (typically AttID), returning
        /// one entry per matching element with its element name and the nearest
        /// <c>id</c>/<c>name</c> attribute. Used to build the <c>bindings</c>
        /// array on <c>BoundToControls</c> errors. Returns an empty list on
        /// parse failure or no matches.
        /// </summary>
        public static List<VarBinding> FindVarBindings(string webFormXml, int variableId)
        {
            var hits = new List<VarBinding>();
            if (string.IsNullOrWhiteSpace(webFormXml) || variableId <= 0) return hits;
            XDocument doc;
            try { doc = XDocument.Parse(webFormXml); }
            catch { return hits; }

            string needle = "var:" + variableId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            foreach (var el in doc.Descendants())
            {
                foreach (var a in el.Attributes())
                {
                    var val = a.Value;
                    if (string.IsNullOrEmpty(val)) continue;
                    if (!TokenMatch(val, needle)) continue;
                    string controlId = el.Attribute("id")?.Value;
                    string controlName = el.Attribute("name")?.Value ?? el.Attribute("Name")?.Value;
                    hits.Add(new VarBinding
                    {
                        Element = el.Name.LocalName,
                        Attribute = a.Name.LocalName,
                        ControlId = controlId,
                        ControlName = controlName,
                    });
                    break; // one entry per element — avoid duplicates from sibling attrs
                }
            }
            return hits;
        }

        // True iff needle appears as a whole token (start/end of string or
        // delimited by non-digit chars). Prevents matching var:6 inside var:64.
        private static bool TokenMatch(string value, string needle)
        {
            int idx = 0;
            while ((idx = value.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
            {
                int end = idx + needle.Length;
                bool leftOk = idx == 0 || !char.IsLetterOrDigit(value[idx - 1]);
                bool rightOk = end >= value.Length || !char.IsDigit(value[end]);
                if (leftOk && rightOk) return true;
                idx = end;
            }
            return false;
        }

        public sealed class VarBinding
        {
            public string Element;
            public string Attribute;
            public string ControlId;
            public string ControlName;
        }
    }
}
