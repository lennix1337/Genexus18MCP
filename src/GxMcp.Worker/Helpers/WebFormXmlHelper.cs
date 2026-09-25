using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Xml;
using System.Xml.Linq;
using Artech.Architecture.Common.Objects;

namespace GxMcp.Worker.Helpers
{
    public static class WebFormXmlHelper
    {
        public static bool IsVisualPart(string partName)
        {
            return string.Equals(partName, "Layout", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(partName, "WebForm", StringComparison.OrdinalIgnoreCase);
        }

        public static KBObjectPart GetWebFormPart(KBObject obj)
        {
            if (obj == null) return null;

            return obj.Parts
                .Cast<KBObjectPart>()
                .FirstOrDefault(part =>
                {
                    string name = part.TypeDescriptor?.Name ?? "";
                    string typeName = part.GetType().Name;
                    
                    // ELITE: In GeneXus Procedures, the 'Layout' part is often a ReportPart.
                    // We allow it here so that ObjectService can read it as Visual XML.
                    if (name.Equals("WebForm", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("Layout", StringComparison.OrdinalIgnoreCase) ||
                        typeName.IndexOf("WebForm", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        typeName.IndexOf("Report", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return true;
                    }

                    return false;
                });
        }

        public static string ReadEditableXml(KBObject obj)
        {
            var part = GetWebFormPart(obj);
            if (part == null)
            {
                Logger.Info($"[LayoutFix] GetWebFormPart returned NULL for {obj.Name}");
                return string.Empty;
            }

            Logger.Info($"[LayoutFix] Found visual part for {obj.Name}: {part.TypeDescriptor?.Name} (GUID: {part.Type})");

            WebFormSdkProbe.DumpOnce(part);

            // ELITE: If it's a ReportPart, we use the specialized ReportLayoutHelper.
            if (ReportLayoutHelper.IsReportPart(part) != null)
            {
                Logger.Info($"[LayoutFix] Part identified as Report for {obj.Name}. Reading via ReportLayoutHelper.");
                var xml = ReportLayoutHelper.ReadLayout(part);
                if (!string.IsNullOrEmpty(xml)) return xml;
                Logger.Info($"[LayoutFix] ReportLayoutHelper.ReadLayout returned empty/null for {obj.Name}");
            }

            try
            {
                dynamic dPart = part;
                var document = dPart.Document as XmlDocument;
                if (document?.DocumentElement == null)
                {
                    Logger.Info($"[LayoutFix] DocumentElement is NULL for {obj.Name}");
                    return string.Empty;
                }

                return XDocument.Parse(document.OuterXml).ToString();
            }
            catch (Exception ex)
            {
                Logger.Info($"[LayoutFix] ReadEditableXml error for {obj.Name}: {ex.Message}");
                try
                {
                    dynamic dPart = part;
                    return dPart.Document?.OuterXml ?? string.Empty;
                }
                catch
                {
                    return string.Empty;
                }
            }
        }

        public static string NormalizeEditableXmlInput(string xml, string partName)
        {
            if (string.IsNullOrWhiteSpace(xml))
            {
                throw new ArgumentException("Visual XML payload is empty.");
            }

            string trimmed = xml.Trim();
            if (trimmed.StartsWith("<!DOCTYPE html", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Layout writes require raw XML (GxMultiForm, BODY, or HTML), not preview HTML. Read part='WebForm' or part='Layout' again and edit the returned XML.");
            }

            var doc = XDocument.Parse(trimmed, LoadOptions.PreserveWhitespace);
            string rootName = doc.Root?.Name.LocalName ?? string.Empty;
            if (!string.Equals(rootName, "GxMultiForm", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(rootName, "BODY", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(rootName, "HTML", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(rootName, "Layout", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(rootName, "ReportPart", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(rootName, "Report", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    string.Format("Visual writes currently require a valid GxMultiForm, BODY, HTML, Layout, ReportPart, or Report XML document. Received root '{0}' for part '{1}'.", rootName, partName ?? "Layout"));
            }

            return doc.ToString();
        }

        /// <summary>
        /// Identifies the two WebForm XML dialects that are observed in supported
        /// GeneXus majors.  A legacy HTML form is rooted at BODY (or carries an
        /// explicit Form/@type="html"); modern GxMultiForm layouts use the
        /// layout form type and must not be rewritten using the legacy caption
        /// representation.
        /// </summary>
        public enum WebFormMarkupFlavor
        {
            LegacyHtml,
            ModernGxMultiForm
        }

        public static WebFormMarkupFlavor DetectMarkupFlavor(string xml)
        {
            if (string.IsNullOrWhiteSpace(xml)) return WebFormMarkupFlavor.ModernGxMultiForm;

            try
            {
                var doc = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
                return DetectMarkupFlavor(doc);
            }
            catch
            {
                // Keep the write fail-closed at the normal XML validation boundary;
                // flavor detection must never make a malformed document look safe.
                return WebFormMarkupFlavor.ModernGxMultiForm;
            }
        }

        internal static WebFormMarkupFlavor DetectMarkupFlavor(XDocument doc)
        {
            var root = doc?.Root;
            if (root == null) return WebFormMarkupFlavor.ModernGxMultiForm;

            if (IsElement(root, "BODY")) return WebFormMarkupFlavor.LegacyHtml;
            if (IsElement(root, "HTML") && root.DescendantsAndSelf().Any(e => IsElement(e, "BODY")))
                return WebFormMarkupFlavor.LegacyHtml;

            // Some older writers wrap the BODY in GxMultiForm without a type;
            // an explicit html form is equally authoritative.
            var form = IsElement(root, "Form")
                ? root
                : root.DescendantsAndSelf().FirstOrDefault(e => IsElement(e, "Form"));
            if (form != null)
            {
                string type = GetAttribute(form, "type");
                if (string.Equals(type, "html", StringComparison.OrdinalIgnoreCase))
                    return WebFormMarkupFlavor.LegacyHtml;
                if (string.IsNullOrWhiteSpace(type)
                    && !IsElement(root, "GxMultiForm")
                    && form.DescendantsAndSelf().Any(e => IsElement(e, "BODY")))
                    return WebFormMarkupFlavor.LegacyHtml;
            }

            string rootType = GetAttribute(root, "type");
            if (string.Equals(rootType, "html", StringComparison.OrdinalIgnoreCase))
                return WebFormMarkupFlavor.LegacyHtml;

            return WebFormMarkupFlavor.ModernGxMultiForm;
        }

        public static bool IsLegacyHtmlWebForm(string xml)
            => DetectMarkupFlavor(xml) == WebFormMarkupFlavor.LegacyHtml;

        internal static bool IsLegacyHtmlWebForm(XmlDocument document)
            => document?.DocumentElement != null
                && IsLegacyHtmlWebForm(document.OuterXml);

        private static bool IsElement(XElement element, string localName)
            => element != null && string.Equals(element.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase);

        private static string GetAttribute(XElement element, string name)
        {
            if (element == null) return null;
            var attr = element.Attributes().FirstOrDefault(a =>
                string.Equals(a.Name.LocalName, name, StringComparison.OrdinalIgnoreCase));
            return attr?.Value;
        }

        public static void ApplyEditableXml(KBObjectPart part, string xml, string baselineXml = null)
        {
            if (part == null)
            {
                throw new ArgumentNullException(nameof(part));
            }

            string normalized = NormalizeEditableXmlInput(xml, part.TypeDescriptor?.Name);

            // ELITE: Support ReportPart persistence
            if (ReportLayoutHelper.IsReportPart(part) != null)
            {
                if (!ReportLayoutHelper.WriteLayout(part, normalized, baselineXml))
                {
                    throw new InvalidOperationException("Failed to write Report layout via reflection.");
                }
                return;
            }

            WebFormSdkProbe.DumpOnce(part);

            string currentXml = null;
            try
            {
                dynamic currentPart = part;
                var rawXml = (currentPart.Document as XmlDocument)?.OuterXml;
                if (!string.IsNullOrEmpty(rawXml))
                {
                    // Normalize through XDocument the SAME WAY ReadEditableXml does, so
                    // currentXml is byte-for-byte comparable to the XML the PatchService
                    // applied its diff to. Without this, raw OuterXml differs from the
                    // formatted ReadEditableXml output and the structural diff trips on
                    // empty-element / whitespace shape.
                    currentXml = XDocument.Parse(rawXml, LoadOptions.PreserveWhitespace).ToString();
                }
            }
            catch (Exception ex)
            {
                Logger.Info("[LayoutFix] Failed to read current Document.OuterXml: " + ex.GetType().Name + ": " + ex.Message);
            }
            if (string.IsNullOrEmpty(currentXml))
            {
                Logger.Info("[LayoutFix] currentXml is null/empty — delta detection will be skipped.");
            }

            var propertyDeltas = WebFormPropertyDeltaDetector.DetectSupportedPropertyDeltas(currentXml, normalized);
            var changedControlNames = WebFormTypedPropertyWriter.GetChangedControlNames(currentXml, normalized);
            if (propertyDeltas.IsSupported && propertyDeltas.Deltas.Count > 0)
            {
                Logger.Info("[LayoutFix] Detected " + propertyDeltas.Deltas.Count + " property delta(s) — trying typed-property write via IWebTag.");
                string failure;
                if (WebFormTypedPropertyWriter.TryApply(part, propertyDeltas.Deltas, out failure))
                {
                    Logger.Info("[LayoutFix] Typed-property write succeeded; raw XML rewrite skipped.");
                    // FR#1: still run descriptor-property fixup so any pre-existing wrong-named
                    // XML attrs (e.g. gxButton OnClickEvent= written by an earlier raw-XML pass)
                    // get routed through SDK and the canonical attr gets emitted.
                    WebFormTypedPropertyWriter.ApplyDescriptorPathFixup(
                        part,
                        baselineXml: currentXml,
                        updatedXml: normalized,
                        changedControlNames: changedControlNames);
                    return;
                }
                Logger.Info("[LayoutFix] Typed-property write rejected: " + failure + " — falling back to raw XML rewrite.");
            }
            else if (!propertyDeltas.IsSupported)
            {
                Logger.Info("[LayoutFix] Delta detector rejected diff: " + propertyDeltas.Reason + " — using raw XML rewrite.");
            }

            // CANONICAL IDE FLOW for WebFormPart (verified via SDK reflection):
            //   Step A: assign EditableContent (string) — fills m_EditableContent and sets
            //           m_EditableToStoredNeeded = true. Does NOT immediately materialise the data model.
            //   Step B: invoke EditableToStored() — converts m_EditableContent → m_Document + parsed Form
            //           tree (the structures actually persisted on save).
            // Skipping step B leaves the part with the new string in memory but the OLD parsed Form on
            // disk after save. We always run both.
            bool setterUsed = TrySetEditableContent(part, normalized);
            if (!setterUsed)
            {
                dynamic fallbackPart = part;
                XmlDocument existingDocument = fallbackPart.Document as XmlDocument;
                if (existingDocument == null)
                {
                    throw new InvalidOperationException("The WebForm part does not expose an XML document.");
                }
                existingDocument.RemoveAll();
                existingDocument.LoadXml(normalized);
            }

            PushDocumentToStoredModel(part);

            // FR#1 (friction-report 2026-05-19): post-write descriptor-property hook.
            // Some XML attributes the user authors are descriptor names (e.g. gxButton
            // "OnClickEvent") whose canonical XML attr name in the SDK is different
            // (e.g. "Event"). The HTML generator reads the SDK's name, so the user's
            // attribute sits ignored unless we route it through PropertiesObject.
            // SetPropertyValueString — which writes the right XML attr internally.
            // Runs always (not just on add/remove), idempotent — SDK no-ops when the
            // value already matches the canonical form.
            WebFormTypedPropertyWriter.ApplyDescriptorPathFixup(
                part,
                baselineXml: currentXml,
                updatedXml: normalized,
                changedControlNames: changedControlNames);
        }

        private static bool TrySetEditableContent(object part, string normalizedXml)
        {
            var type = part.GetType();
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            var prop = type.GetProperty("EditableContent", flags);
            if (prop == null || !prop.CanWrite || prop.PropertyType != typeof(string)) return false;

            try
            {
                prop.SetValue(part, normalizedXml);
                Logger.Info($"[LayoutFix] Set EditableContent on {type.Name} ({normalizedXml.Length} chars).");

                // Best-effort: invalidate the "last modification" snapshot so dirty tracking notices the change.
                try
                {
                    var invalidate = type.GetMethod("InvalidateLastModification", flags, null, Type.EmptyTypes, null);
                    invalidate?.Invoke(part, null);
                }
                catch { }
                return true;
            }
            catch (Exception ex)
            {
                Logger.Info($"[LayoutFix] Setting EditableContent threw: {ex.Message}");
                return false;
            }
        }

        private static void PushDocumentToStoredModel(object part)
        {
            var type = part.GetType();
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;

            // Use DeserializeDataFromDocument ONLY — NOT EditableToStored.
            //
            // EditableToStored() in the WebFormPart performs strict attribute-reference resolution
            // (AttributeVariableConverter.GetAttVarByName) that throws "Atributo desconhecido 'att:NNNN'"
            // when invoked from the headless worker context (the lookup against KBModel returns null even
            // for valid att IDs that the form references — likely because the worker doesn't fully prime
            // the same model lookup tables the IDE does). The throw poisons the part state, leaving
            // controls partially mutated, which is why downstream Save persists corrupted XML missing
            // the modified controls.
            //
            // DeserializeDataFromDocument() reparses the same WebTags but is tolerant of unresolved
            // references — it walks the tree without throwing, and updates the internal Properties so
            // BeforeSaveKBObject's SaveProperties pass writes the NEW values back to m_Document instead
            // of the old in-memory ones.
            string[] candidates = { "DeserializeDataFromDocument" };
            foreach (var name in candidates)
            {
                var method = type.GetMethods(flags).FirstOrDefault(m =>
                    string.Equals(m.Name, name, StringComparison.Ordinal) &&
                    m.GetParameters().Length == 0);
                if (method == null) continue;

                try
                {
                    method.Invoke(part, null);
                    Logger.Info($"[LayoutFix] Invoked {name}() on {type.Name} to push Document into stored model.");

                    // Invalidate the "last modification" cache so the SDK's dirty-tracking notices the change.
                    var invalidate = type.GetMethods(flags).FirstOrDefault(m =>
                        string.Equals(m.Name, "InvalidateLastModification", StringComparison.Ordinal) &&
                        m.GetParameters().Length == 0);
                    try { invalidate?.Invoke(part, null); } catch { }
                    return;
                }
                catch (Exception ex)
                {
                    var inner = ex.InnerException ?? ex;
                    Logger.Info($"[LayoutFix] {name}() threw: {inner.GetType().Name}: {inner.Message}");
                    if (inner.StackTrace != null)
                    {
                        var firstFrame = inner.StackTrace.Split('\n')[0].Trim();
                        Logger.Info($"[LayoutFix]   at {firstFrame}");
                    }
                }
            }

            Logger.Info("[LayoutFix] No editable→stored conversion method found on " + type.FullName +
                        " — falling back to Document-only update (Form cache may stay stale).");
        }

    }
}
