using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml;
using Artech.Architecture.Common.Objects;
using Artech.Common.Properties;
using Artech.Packages.Patterns.Objects;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// A control of a layout-type WebForm (<c>&lt;Form type="layout"&gt;</c>, the
    /// GxMultiForm dialect of Web Panels and Transactions). Its model is the
    /// WorkWithDevices <c>PatternInstance</c>, not the HTML <c>IWebTag</c> tree, which
    /// is why the tag enumeration (<c>WebFormHelper.EnumerateWebTag</c>) is empty for it.
    /// </summary>
    internal sealed class LayoutControl
    {
        /// <summary>The live <see cref="PatternInstanceElement"/> of the detached model.</summary>
        public PatternInstanceElement Element { get; set; }
        public PropertiesObject Attributes { get; set; }
        /// <summary>XML element name: <c>data</c>, <c>attribute</c>, <c>userAction</c>, <c>table</c>...</summary>
        public string ElementName { get; set; }
        /// <summary>Pattern specification type, e.g. <c>LayoutDataItem</c>.</summary>
        public string SpecType { get; set; }
        public string ControlName { get; set; }
        public string Id { get; set; }
        /// <summary>The serialized binding as it appears in the XML: <c>att:226</c>, <c>var:5</c> or <c>&amp;Name</c>.</summary>
        public string Binding { get; set; }
        /// <summary>The attribute or variable name behind <see cref="Binding"/>, when the KB resolves it.</summary>
        public string BindingName { get; set; }
        public string Path { get; set; }
        public int Depth { get; set; }
        /// <summary>The element's own <c>PATTERN_ELEMENT_CUSTOM_PROPERTIES</c> value as serialized by the SDK.</summary>
        public string CustomPropertiesXml { get; set; }
    }

    /// <summary>
    /// The detached typed model of a layout form. It is built from a CLONE of the part's
    /// <c>&lt;detail&gt;</c> element: <c>LoadPanelElement</c> stamps <c>PanelType</c> on the
    /// element it is given, and a read or a dry run must never touch the live part.
    /// </summary>
    internal sealed class LayoutPanel
    {
        public KBObject Object { get; set; }
        public PatternInstanceElement Root { get; set; }
        public XmlDocument Scratch { get; set; }
        public List<LayoutControl> Controls { get; } = new List<LayoutControl>();
    }

    internal static class LayoutFormModel
    {
        internal const string LayoutFormType = "layout";
        private const string HandlerTypeName = "Artech.Genexus.Common.Parts.WebForm.MultiForm";

        private static readonly Regex AttRef = new Regex(@"^att:(\d+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex Guid36 = new Regex(
            @"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        // The "<element>:<guid>" spelling of a control id (grid:9c1f2a3b-...), as some clients print it (issue #830).
        private static readonly Regex KindedGuid = new Regex(
            @"^([A-Za-z][A-Za-z0-9_]*):([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>The <c>&lt;detail&gt;</c> element of the main layout form, or null.</summary>
        internal static XmlElement FindDetailElement(XmlDocument document)
        {
            if (document?.DocumentElement == null) return null;
            foreach (XmlNode form in document.SelectNodes("//*[local-name()='Form']"))
            {
                var element = form as XmlElement;
                if (element == null) continue;
                if (!string.Equals(element.GetAttribute("type"), LayoutFormType, StringComparison.OrdinalIgnoreCase)) continue;
                foreach (XmlNode child in element.ChildNodes)
                    if (child is XmlElement detail && string.Equals(detail.LocalName, "detail", StringComparison.Ordinal))
                        return detail;
            }
            return null;
        }

        internal static bool IsLayoutForm(XmlDocument document) => FindDetailElement(document) != null;

        /// <summary>
        /// Builds the typed model of the layout form of <paramref name="obj"/>. Returns null
        /// with a failure reason when the object has no layout form or the SDK refuses to
        /// load it; the reason is meant for the caller's diagnostics, not for the log only.
        /// </summary>
        internal static LayoutPanel TryLoad(KBObject obj, out string failure)
        {
            failure = null;
            var part = WebFormXmlHelper.GetWebFormPart(obj);
            if (part == null) { failure = "The object has no WebForm part."; return null; }
            return TryLoad(obj, WebFormSdkReflection.GetPartDocument(part), out failure);
        }

        internal static LayoutPanel TryLoad(KBObject obj, XmlDocument partDocument, out string failure)
        {
            failure = null;
            var detail = FindDetailElement(partDocument);
            if (detail == null) { failure = "The WebForm is not a layout form (no <Form type=\"layout\"><detail>)."; return null; }

            try
            {
                var handler = Artech.Genexus.Common.Parts.WebForm.MultiForm.Layout;
                if (handler == null) { failure = "The layout form handler is not available in this GeneXus installation."; return null; }

                var load = handler.GetType().GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => m.Name == "LoadPanelElement" && m.GetParameters().Length == 3);
                if (load == null) { failure = "WebLayoutHandler.LoadPanelElement was not found."; return null; }

                var scratch = new XmlDocument { PreserveWhitespace = true };
                var clone = (XmlElement)scratch.ImportNode(detail, true);
                scratch.AppendChild(clone);

                object root;
                try { root = load.Invoke(null, new object[] { obj, null, clone }); }
                catch (TargetInvocationException ex)
                {
                    var inner = ex.InnerException ?? ex;
                    failure = "The SDK could not load the layout form: " + inner.GetType().Name + ": " + inner.Message;
                    return null;
                }

                var panel = new LayoutPanel { Object = obj, Root = root as PatternInstanceElement, Scratch = scratch };
                if (panel.Root == null) { failure = "The SDK returned no layout model."; return null; }
                Collect(panel, panel.Root, string.Empty, 0);
                return panel;
            }
            catch (Exception ex)
            {
                var inner = ex.InnerException ?? ex;
                failure = "Layout model load failed: " + inner.GetType().Name + ": " + inner.Message;
                return null;
            }
        }

        private static void Collect(LayoutPanel panel, PatternInstanceElement element, string parentPath, int depth)
        {
            if (element == null) return;
            string name = SafeString(() => element.Name);
            string path = parentPath + "/" + name;
            var attributes = SafeGet(() => (PropertiesObject)element.Attributes);
            var children = ChildrenOf(element);

            var control = new LayoutControl
            {
                Element = element,
                Attributes = attributes,
                ElementName = name,
                SpecType = SafeString(() => element.Type),
                Path = path,
                Depth = depth,
                ControlName = ReadBag(attributes, "controlName"),
                Id = ReadBag(attributes, "id")
            };

            // A leaf (data item, variable, action...) is cheap to serialize on its own, and its
            // serialized attributes carry the binding exactly as the file spells it (att:226).
            if (children.Count == 0)
            {
                try
                {
                    var holder = panel.Scratch.CreateElement(name);
                    element.ToXml(holder, SerializationFormat.Internal);
                    control.Binding = Empty(holder.GetAttribute("attribute"));
                    control.ControlName = control.ControlName ?? Empty(holder.GetAttribute("controlName"));
                    control.Id = control.Id ?? Empty(holder.GetAttribute("id"));
                    control.CustomPropertiesXml = Empty(holder.GetAttribute(CustomPropertiesPayload.AttributeName));
                }
                catch (Exception ex)
                {
                    Logger.Debug("[LayoutModel] ToXml failed for " + path + ": " + (ex.InnerException ?? ex).Message);
                }
            }
            else
            {
                control.Binding = null;
            }

            control.BindingName = ResolveBindingName(panel.Object, control.Binding);
            panel.Controls.Add(control);

            foreach (var child in children) Collect(panel, child, path, depth + 1);
        }

        /// <summary>
        /// The SDK's own validation of a layout form (<c>IMultiFormHandler.Validate</c>, the check
        /// the IDE runs before it saves one), run on a detached copy so the live part is never
        /// touched. Returns the errors; <paramref name="unavailable"/> is set when the validator
        /// could not run at all, which is not the same as the form being valid.
        /// </summary>
        internal static List<ParameterListDiagnostic> ValidateDetached(KBObject obj, XmlDocument document, out string unavailable)
        {
            unavailable = null;
            var diagnostics = new List<ParameterListDiagnostic>();
            var detail = FindDetailElement(document);
            if (detail == null) { unavailable = "The form is not a layout form."; return diagnostics; }
            try
            {
                var handler = Artech.Genexus.Common.Parts.WebForm.MultiForm.Layout;
                var validate = handler?.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "Validate" && m.GetParameters().Length == 3
                        && m.GetParameters()[1].ParameterType == typeof(XmlElement));
                if (validate == null) { unavailable = "The layout form validator was not found in this GeneXus installation."; return diagnostics; }

                var scratch = new XmlDocument { PreserveWhitespace = true };
                var clone = (XmlElement)scratch.ImportNode(detail, true);
                scratch.AppendChild(clone);

                var output = new Artech.Common.Diagnostics.OutputMessages();
                object ok;
                try { ok = validate.Invoke(handler, new object[] { obj, clone, output }); }
                catch (TargetInvocationException ex) { throw ex.InnerException ?? ex; }

                foreach (var error in output.OnlyErrors)
                    diagnostics.Add(new ParameterListDiagnostic
                    {
                        Code = string.IsNullOrWhiteSpace(error.ErrorCode) ? null : error.ErrorCode,
                        Message = (error.Text ?? string.Empty).Trim()
                    });
                if (ok is bool valid && !valid && diagnostics.Count == 0)
                    diagnostics.Add(new ParameterListDiagnostic { Message = "The SDK layout validator rejected the form without a message." });
            }
            catch (Exception ex)
            {
                unavailable = "The SDK layout validator failed to run: " + (ex.InnerException ?? ex).Message;
            }
            return diagnostics;
        }

        private static List<PatternInstanceElement> ChildrenOf(PatternInstanceElement element)
        {
            var list = new List<PatternInstanceElement>();
            try
            {
                var children = element.Children as IEnumerable;
                if (children == null) return list;
                foreach (var child in children)
                    if (child is PatternInstanceElement pie) list.Add(pie);
            }
            catch (Exception ex)
            {
                Logger.Debug("[LayoutModel] children enumeration failed: " + (ex.InnerException ?? ex).Message);
            }
            return list;
        }

        internal static string ResolveBindingName(KBObject obj, string binding)
        {
            if (string.IsNullOrEmpty(binding)) return null;
            if (binding.StartsWith("&", StringComparison.Ordinal)) return binding.Substring(1);
            var match = AttRef.Match(binding);
            if (!match.Success) return null;
            if (!int.TryParse(match.Groups[1].Value, out int id)) return null;
            try
            {
                var attribute = Artech.Genexus.Common.Objects.Attribute.Get(obj.Model, id);
                return attribute?.Name;
            }
            catch (Exception ex)
            {
                Logger.Debug("[LayoutModel] attribute " + binding + " could not be resolved: " + (ex.InnerException ?? ex).Message);
                return null;
            }
        }

        /// <summary>An att:&lt;id&gt;, a GUID or an &lt;element&gt;:&lt;guid&gt;: spellings that only a layout control can answer to.</summary>
        internal static bool IsLayoutOnlyReference(string reference)
        {
            if (string.IsNullOrWhiteSpace(reference)) return false;
            string trimmed = reference.Trim();
            return AttRef.IsMatch(trimmed) || TryGetControlGuid(trimmed, out _);
        }

        /// <summary>The control id a reference spells: a bare GUID, or the GUID of an <c>&lt;element&gt;:&lt;guid&gt;</c> reference.</summary>
        internal static bool TryGetControlGuid(string reference, out string guid)
            => TryGetControlGuid(reference, out guid, out _);

        /// <summary>
        /// The control id a reference spells and, for <c>&lt;element&gt;:&lt;guid&gt;</c> (<c>grid:9c1f2a3b-...</c>), the element
        /// it names (null for a bare GUID). <see cref="Find"/> accepts such a reference only for a control of that element, so
        /// a reference that names the wrong element never acts on the control that has the id.
        /// </summary>
        internal static bool TryGetControlGuid(string reference, out string guid, out string element)
        {
            guid = null;
            element = null;
            if (string.IsNullOrWhiteSpace(reference)) return false;
            string trimmed = reference.Trim();
            if (Guid36.IsMatch(trimmed)) { guid = trimmed; return true; }
            var kinded = KindedGuid.Match(trimmed);
            if (!kinded.Success) return false;
            guid = kinded.Groups[2].Value;
            element = kinded.Groups[1].Value;
            return true;
        }

        /// <summary>What a reference can match: a control name, a GUID id (bare or as &lt;element&gt;:&lt;guid&gt;), an attribute (name or att:N).</summary>
        internal static List<LayoutControl> Find(LayoutPanel panel, string reference)
        {
            var matches = new List<LayoutControl>();
            if (panel == null || string.IsNullOrWhiteSpace(reference)) return matches;
            string wanted = reference.Trim();

            if (TryGetControlGuid(wanted, out string wantedGuid, out string wantedElement))
                return panel.Controls.Where(c => string.Equals(c.Id, wantedGuid, StringComparison.OrdinalIgnoreCase)
                    && (wantedElement == null || string.Equals(c.ElementName, wantedElement, StringComparison.OrdinalIgnoreCase))).ToList();

            matches = panel.Controls.Where(c => !string.IsNullOrEmpty(c.ControlName)
                && string.Equals(c.ControlName, wanted, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count > 0) return matches;

            string bare = wanted.StartsWith("&", StringComparison.Ordinal) ? wanted.Substring(1) : wanted;
            return panel.Controls.Where(c => !string.IsNullOrEmpty(c.Binding) &&
                (string.Equals(c.Binding, wanted, StringComparison.OrdinalIgnoreCase)
                 || string.Equals(c.BindingName, bare, StringComparison.OrdinalIgnoreCase))).ToList();
        }

        /// <summary>One property of the control's custom-properties payload (ControlType, DataProvider...).</summary>
        internal static string ReadCustomProperty(LayoutControl control, string name)
        {
            if (control == null || string.IsNullOrEmpty(control.CustomPropertiesXml)) return null;
            if (!CustomPropertiesPayload.TryParse(control.CustomPropertiesXml, out var payload) || payload == null) return null;
            return payload.TryGet(name, out string value) ? Empty(value) : null;
        }

        private static string ReadBag(PropertiesObject bag, string name)
        {
            if (bag == null) return null;
            try
            {
                if (!bag.ContainsPropertyDefinition(name)) return null;
                object value = bag.GetPropertyValue(name);
                return Empty(value?.ToString());
            }
            catch (Exception ex)
            {
                Logger.Debug("[LayoutModel] property '" + name + "' could not be read: " + (ex.InnerException ?? ex).Message);
                return null;
            }
        }

        private static string Empty(string value) => string.IsNullOrEmpty(value) ? null : value;

        private static string SafeString(Func<string> read)
        {
            try { return read(); }
            catch (Exception ex)
            {
                Logger.Debug("[LayoutModel] element member could not be read: " + (ex.InnerException ?? ex).Message);
                return null;
            }
        }

        private static T SafeGet<T>(Func<T> read) where T : class
        {
            try { return read(); }
            catch (Exception ex)
            {
                Logger.Debug("[LayoutModel] element member could not be read: " + (ex.InnerException ?? ex).Message);
                return null;
            }
        }
    }
}
