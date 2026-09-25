using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Xml;
using System.Xml.Linq;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// Applies a set of WebFormPropertyDelta items to a live WebFormPart by going through
    /// the canonical SDK path:
    ///   1. Enumerate IWebTag instances via WebFormHelper.EnumerateWebTag(part).
    ///   2. Match by tag.Node id / ControlName attribute.
    ///   3. Call WebFormEditable.SetTagProperty(tag, tag.Properties, null, propName, value, ref changed, null)
    ///      so the SDK runs the proper PropertyValueConverter and updates typed Properties.
    ///
    /// On save (later), WebFormPart.BeforeSaveKBObject iterates tags and calls
    /// tag.SaveProperties() — which writes the typed Properties back into the tag's XmlNode
    /// inside m_Document. Result: persisted XML matches the requested change WITHOUT us
    /// touching the raw Document.
    /// </summary>
    public static class WebFormTypedPropertyWriter
    {
        private const string HelperTypeName = "Artech.Genexus.Common.Parts.WebForm.WebFormHelper";
        private const string EditableTypeName = "Artech.Genexus.Common.Parts.WebForm.WebFormEditable";

        public static bool TryApply(object webFormPart, IReadOnlyList<WebFormPropertyDelta> deltas, out string failure)
        {
            failure = null;
            if (webFormPart == null) { failure = "part is null"; return false; }
            if (deltas == null || deltas.Count == 0) { failure = "no deltas"; return false; }

            Type helperType = FindType(HelperTypeName);
            if (helperType == null) { failure = "WebFormHelper type not loaded"; return false; }

            Type editableType = FindType(EditableTypeName);
            if (editableType == null) { failure = "WebFormEditable type not loaded"; return false; }

            // Pick the EnumerateWebTag overload that takes (KBObject, XmlDocument) so tags are rooted
            // in part.Document — the SAME document the SDK's BeforeSaveKBObject iterates.
            // Access the m_Document FIELD (not the property) — the property may clone.
            XmlDocument partDocForEnum = null;
            try
            {
                var docField = webFormPart.GetType().GetField("m_Document",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                partDocForEnum = docField?.GetValue(webFormPart) as XmlDocument;
            }
            catch { }
            if (partDocForEnum == null) partDocForEnum = GetReadProperty(webFormPart, "Document") as XmlDocument;
            Logger.Info("[TypedWriter] m_Document field length=" + (partDocForEnum?.OuterXml.Length ?? -1));
            var partKbObj = GetReadProperty(webFormPart, "KBObject") ?? GetReadProperty(webFormPart, "ContainerObject") ?? GetReadProperty(webFormPart, "Parent") ?? GetReadProperty(webFormPart, "Container");
            MethodInfo enumerate = null;
            object[] enumArgs = null;
            if (partDocForEnum != null && partKbObj != null)
            {
                enumerate = helperType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m =>
                    {
                        if (m.Name != "EnumerateWebTag") return false;
                        var ps = m.GetParameters();
                        return ps.Length == 2 && ps[1].ParameterType == typeof(XmlDocument) && ps[0].ParameterType.IsInstanceOfType(partKbObj);
                    });
                if (enumerate != null) enumArgs = new object[] { partKbObj, partDocForEnum };
            }
            if (enumerate == null)
            {
                enumerate = helperType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m =>
                    {
                        if (m.Name != "EnumerateWebTag") return false;
                        var ps = m.GetParameters();
                        return ps.Length == 1 && ps[0].ParameterType.IsInstanceOfType(webFormPart);
                    });
                enumArgs = new object[] { webFormPart };
            }
            if (enumerate == null) { failure = "no EnumerateWebTag overload found"; return false; }
            Logger.Info("[TypedWriter] using " + enumerate.Name + "(" + string.Join(",", enumerate.GetParameters().Select(p => p.ParameterType.Name)) + ")");

            MethodInfo setTagProperty = editableType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "SetTagProperty");
            if (setTagProperty == null) { failure = "WebFormEditable.SetTagProperty not found"; return false; }

            // Alternative path: IWebTag.SetProperties(IDictionary) — higher-level API exposed by the
            // interface itself. Used when SetTagProperty throws because of TypeDescriptorContext=null.
            Type webTagInterface = FindType("Artech.Genexus.Common.Parts.WebForm.IWebTag");
            MethodInfo setPropertiesDict = webTagInterface?.GetMethod("SetProperties", new[] { typeof(IDictionary) });

            IDictionary<string, object> byId = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            IDictionary<string, object> byControlName = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            int total = 0;
            try
            {
                IEnumerable tags = (IEnumerable)enumerate.Invoke(null, enumArgs);
                foreach (var tag in tags)
                {
                    total++;
                    var node = GetReadProperty(tag, "Node") as XmlNode;
                    if (node?.Attributes == null) continue;
                    string id = node.Attributes["id"]?.Value;
                    string cn = node.Attributes["ControlName"]?.Value ?? node.Attributes["controlName"]?.Value;
                    if (!string.IsNullOrEmpty(id) && !byId.ContainsKey(id)) byId[id] = tag;
                    if (!string.IsNullOrEmpty(cn) && !byControlName.ContainsKey(cn)) byControlName[cn] = tag;
                }
            }
            catch (Exception ex)
            {
                var inner = ex.InnerException ?? ex;
                failure = "EnumerateWebTag threw: " + inner.GetType().Name + ": " + inner.Message;
                return false;
            }
            Logger.Info("[TypedWriter] Indexed " + total + " IWebTag(s): " + byId.Count + " by id, " + byControlName.Count + " by ControlName.");

            // Group deltas by control so SetProperties is called once per tag with all changes.
            var byControl = new Dictionary<string, List<WebFormPropertyDelta>>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in deltas)
            {
                if (!byControl.TryGetValue(d.ControlName ?? string.Empty, out var list)) { list = new List<WebFormPropertyDelta>(); byControl[d.ControlName ?? string.Empty] = list; }
                list.Add(d);
            }

            foreach (var kv in byControl)
            {
                string controlName = kv.Key;
                object tag = null;
                if (!string.IsNullOrEmpty(controlName))
                {
                    byId.TryGetValue(controlName, out tag);
                    if (tag == null) byControlName.TryGetValue(controlName, out tag);
                }
                if (tag == null) { failure = "control '" + controlName + "' not found in tag enumeration"; return false; }

                // Canonical-XML strategy:
                //  1) Mutate the tag's XmlNode attributes directly. The Node is shared with m_Document,
                //     so this updates the on-disk XML for free.
                //  2) Invalidate the typed Property cache on the tag (m_Props=null, m_PropertiesLoaded=false).
                //     This forces the SDK to re-load typed Properties FROM the now-updated XmlNode the next
                //     time tag.Properties is accessed — which happens during BeforeSaveKBObject/SaveProperties.
                //     Result: SaveProperties writes the SAME values back to m_Document (no-op clobber).
                // `EnumerateWebTag` returns tags whose Node lives in an INTERNAL XmlDocument, not the
                // live part.Document. Mutating tag.Node alone wouldn't propagate to what gets persisted.
                // Find the matching element in part.Document by id/ControlName and mutate THAT instead.
                var partDoc = partDocForEnum;
                if (partDoc == null) { failure = "part m_Document is null"; return false; }
                XmlElement node = FindElementInPartDoc(partDoc, controlName);
                if (node == null) { failure = "no element id='" + controlName + "' (nor ControlName) in part.Document"; return false; }

                // FR#1 (friction-report 2026-05-19): properties whose XML attribute name differs
                // from the descriptor name MUST go through PropertiesObject.SetPropertyValueString,
                // not raw XML mutation. The HTML generator reads the XML attribute the SDK chose
                // (e.g. gxButton: descriptor "OnClickEvent" → XML attr "Event"), so writing the
                // descriptor name as an XML attribute leaves it unread → silent fallback to Enter.
                //
                // Legacy HTML WebForms are different: CaptionExpression Tokens is the
                // persisted canonical representation, not a misspelled descriptor name.
                // Keep that value on the raw-XML path and never send it through the modern
                // Caption converter.
                bool legacyHtml = WebFormXmlHelper.IsLegacyHtmlWebForm(partDocForEnum?.OuterXml);
                var effectiveDeltas = kv.Value
                    .Where(d => !(legacyHtml && IsCaptionExpression(d.PropertyName)))
                    .ToList();

                // Strategy: extract these properties into a "descriptor delta" set; remaining
                // properties still go through raw XML mutation (which works for Caption, Class,
                // Visible, etc. where XML attr name == descriptor name).
                var descriptorDeltas = new List<WebFormPropertyDelta>();
                var rawXmlDeltas = new List<WebFormPropertyDelta>();
                foreach (var d in kv.Value)
                {
                    if (NeedsDescriptorPath(d.PropertyName, legacyHtml)) descriptorDeltas.Add(d);
                    else rawXmlDeltas.Add(d);
                }

                // 1. Descriptor path — PropertiesObject.SetPropertyValueString
                if (descriptorDeltas.Count > 0)
                {
                    ApplyDescriptorDeltas(tag, controlName, descriptorDeltas);
                }

                // 2. Raw XML path — direct attribute mutation. This includes
                // CaptionExpression on legacy HTML forms, where it is authoritative.
                foreach (var d in rawXmlDeltas)
                {
                    if (d.Value == null)
                    {
                        RemoveAttribute(node, d.PropertyName);
                        Logger.Info("[TypedWriter] removed attr " + controlName + "." + d.PropertyName);
                        continue;
                    }
                    var attr = FindAttribute(node, d.PropertyName);
                    if (attr == null)
                    {
                        attr = node.OwnerDocument.CreateAttribute(d.PropertyName);
                        node.Attributes.Append(attr);
                    }
                    attr.Value = d.Value;
                    Logger.Info("[TypedWriter] node[" + controlName + "]." + d.PropertyName + " <- '" + Truncate(d.Value, 80) + "'");
                }

                // Invalidate the tag's cached typed Properties so the next read reloads from the new XML.
                InvalidateTagPropertyCache(tag, controlName);

                // CANONICAL FIX (session 4): update the typed PropertiesObject via
                // IWebTag.SetProperties(IDictionary). Direct XmlNode mutation alone leaves the
                // typed model untouched; on Save the SDK serializes BOTH layers and inserts
                // TWO EntityVersion rows for the WebFormPart — one from m_Document (our bytes)
                // and one regenerated from the stale typed model (= original bytes). The
                // EntityVersionComposition pointer at the parent WebPanel lands on the
                // regenerated sibling, so reads return the original. By updating the typed
                // model here, both serializations match and composition resolves correctly.
                if (setPropertiesDict != null && effectiveDeltas.Count > 0)
                {
                    try
                    {
                        var dict = new System.Collections.Hashtable();
                        foreach (var d in effectiveDeltas)
                        {
                            // Key is the XML attribute name; SetProperties handles the
                            // attribute↔typed-property mapping internally. Legacy
                            // CaptionExpression is intentionally excluded above because
                            // its Tokens XML is already the canonical persisted value.
                            dict[d.PropertyName] = d.Value;
                        }
                        setPropertiesDict.Invoke(tag, new object[] { dict });
                        Logger.Info("[TypedWriter] tag.SetProperties(IDictionary) invoked for '" + controlName + "' with " + dict.Count + " key(s).");
                    }
                    catch (Exception ex)
                    {
                        var inner = ex.InnerException ?? ex;
                        Logger.Info("[TypedWriter] tag.SetProperties threw: " + inner.GetType().Name + ": " + inner.Message + " — falling back to SetTagProperty per-key.");

                        // Fallback: WebFormEditable.SetTagProperty per key.
                        try
                        {
                            var propsBag = GetReadProperty(tag, "Properties");
                            foreach (var d in effectiveDeltas)
                            {
                                if (propsBag == null) break;
                                bool changed = false;
                                var args = new object[] { tag, propsBag, null, d.PropertyName, (object)d.Value, changed, null };
                                setTagProperty.Invoke(null, args);
                                Logger.Info("[TypedWriter] SetTagProperty fallback: " + controlName + "." + d.PropertyName + " changed=" + args[5]);
                            }
                        }
                        catch (Exception ex2)
                        {
                            var inner2 = ex2.InnerException ?? ex2;
                            Logger.Info("[TypedWriter] SetTagProperty fallback also threw: " + inner2.GetType().Name + ": " + inner2.Message);
                        }
                    }
                }

                // Verify the mutation actually landed in part.Document by re-querying.
                var verify = FindElementInPartDoc(partDoc, controlName);
                foreach (var d in effectiveDeltas)
                {
                    string after = verify?.Attributes?[d.PropertyName]?.Value;
                    Logger.Info("[TypedWriter] verify part.Document <" + node.Name + " id=" + controlName + ">." + d.PropertyName + " = '" + Truncate(after, 80) + "' (wanted '" + Truncate(d.Value, 80) + "', match=" + (after == d.Value) + ")");
                }
            }

            // Bump LastModification so the part is considered dirty and gets persisted on EnsureSave.
            try
            {
                var inv = webFormPart.GetType().GetMethod("InvalidateLastModification",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
                inv?.Invoke(webFormPart, null);
            }
            catch { }

            // THE KEY: signal the Udm Entity that we modified data so EnsureSave actually persists.
            // Without this, the SDK considers the part clean (no Property setter fired) and skips it.
            // SetModeModified(Modification.Data, null) is the canonical "data changed" notification.
            try
            {
                Type modEnum = FindType("Artech.Udm.Framework.Entity+Modification");
                object dataMod = modEnum != null ? Enum.Parse(modEnum, "Data") : null;
                if (dataMod != null)
                {
                    var setMode = FindInstanceMethod(webFormPart.GetType(), "SetModeModified", new[] { modEnum, typeof(object) });
                    if (setMode != null)
                    {
                        setMode.Invoke(webFormPart, new[] { dataMod, (object)null });
                        Logger.Info("[TypedWriter] SetModeModified(Modification.Data, null) — part marked dirty.");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Info("[TypedWriter] SetModeModified threw: " + (ex.InnerException ?? ex).Message);
            }

            // Belt-and-suspenders: also set Entity.Dirty = true via property.
            try
            {
                var dirtyProp = webFormPart.GetType().GetProperty("Dirty",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (dirtyProp != null && dirtyProp.CanWrite && dirtyProp.PropertyType == typeof(bool))
                {
                    dirtyProp.SetValue(webFormPart, true, null);
                    Logger.Info("[TypedWriter] Entity.Dirty = true.");
                }
            }
            catch { }

            // Call EditableToStored to sync typed model from XML through the SDK's canonical converter.
            // For pure attribute changes (no new att:NNNN references), this should not throw.
            try
            {
                var etsByPart = webFormPart.GetType().GetMethod("EditableToStored",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (etsByPart != null)
                {
                    etsByPart.Invoke(webFormPart, null);
                    Logger.Info("[TypedWriter] EditableToStored() invoked on WebFormPart.");
                }
            }
            catch (Exception ex)
            {
                var inner = ex.InnerException ?? ex;
                Logger.Info("[TypedWriter] EditableToStored() threw: " + inner.GetType().Name + ": " + inner.Message);
            }

            // Clear the editable-to-stored pending flag so EnsureSave doesn't replay old m_EditableContent.
            try
            {
                var flag = webFormPart.GetType().GetField("m_EditableToStoredNeeded",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                if (flag != null && flag.FieldType == typeof(bool))
                {
                    flag.SetValue(webFormPart, false);
                    Logger.Info("[TypedWriter] cleared m_EditableToStoredNeeded.");
                }
            }
            catch { }

            // Also rewrite m_EditableContent to match the new XML, so any latent path that goes
            // through "editable -> stored" produces the same result we wrote directly to m_Document.
            try
            {
                var docNow = GetReadProperty(webFormPart, "Document") as XmlDocument;
                if (docNow != null)
                {
                    var ecField = webFormPart.GetType().GetField("m_EditableContent",
                        BindingFlags.NonPublic | BindingFlags.Instance);
                    if (ecField != null && ecField.FieldType == typeof(string))
                    {
                        ecField.SetValue(webFormPart, docNow.OuterXml);
                        Logger.Info("[TypedWriter] synced m_EditableContent from m_Document (" + docNow.OuterXml.Length + " chars).");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Info("[TypedWriter] failed to sync m_EditableContent: " + ex.Message);
            }

            // CRITICAL: trigger Document property SETTER with a NEW XmlDocument instance.
            // Direct field mutation (m_Document attribute writes) bypasses OnPropertyValueChanged,
            // which is the SDK's hook for registering the entity in the active transaction's
            // unit-of-work / dirty-set. Without that registration, SaveWithParent may serialize
            // correct bytes but the KB persistence layer drops them because the entity wasn't
            // enrolled in the commit batch.
            //
            // Cloning m_Document and re-assigning via the setter triggers OnPropertyValueChanged
            // (which fires base.OnPropertyValueChanged → IPropertyBag notification → transaction
            // dirty-set registration) WITHOUT setting m_EditableToStoredNeeded=true (that would
            // cause EditableToStored to run on save and throw on unresolved att: refs).
            try
            {
                var docNow = GetReadProperty(webFormPart, "Document") as XmlDocument;
                if (docNow != null)
                {
                    var clone = (XmlDocument)docNow.Clone();
                    var docProp = webFormPart.GetType().GetProperty("Document",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (docProp != null && docProp.CanWrite)
                    {
                        docProp.SetValue(webFormPart, clone, null);
                        Logger.Info("[TypedWriter] Document property setter invoked (clone len=" + clone.OuterXml.Length + ") — fired OnPropertyValueChanged for UoW registration.");
                        // Document setter sets m_FixPending=true & m_EditableContent=null. Re-sync m_EditableContent now.
                        var ecField2 = webFormPart.GetType().GetField("m_EditableContent",
                            BindingFlags.NonPublic | BindingFlags.Instance);
                        ecField2?.SetValue(webFormPart, clone.OuterXml);
                        // Setter does NOT set m_EditableToStoredNeeded but make doubly sure:
                        var flag2 = webFormPart.GetType().GetField("m_EditableToStoredNeeded",
                            BindingFlags.NonPublic | BindingFlags.Instance);
                        if (flag2 != null && flag2.FieldType == typeof(bool))
                            flag2.SetValue(webFormPart, false);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Info("[TypedWriter] Document property setter threw: " + (ex.InnerException ?? ex).Message);
            }

            return true;
        }

        // Post-write hook called from WebFormXmlHelper.ApplyEditableXml after the raw XML is
        // persisted and the part has reparsed via DeserializeDataFromDocument. Only controls
        // changed by this request are considered. This is important for legacy HTML forms:
        // their CaptionExpression Tokens attributes are canonical persisted data, and walking
        // the whole part can silently strip every untouched caption.
        // Compatibility overload for callers compiled against the original
        // one-argument hook. New write paths pass the baseline/updated scope.
        public static void ApplyDescriptorPathFixup(object webFormPart)
            => ApplyDescriptorPathFixup(webFormPart, null, null, null);

        public static void ApplyDescriptorPathFixup(
            object webFormPart,
            string baselineXml = null,
            string updatedXml = null,
            IEnumerable<string> changedControlNames = null)
        {
            if (webFormPart == null) return;
            try
            {
                Type helperType = FindType(HelperTypeName);
                if (helperType == null) return;

                XmlDocument partDoc = null;
                try
                {
                    var docField = webFormPart.GetType().GetField("m_Document",
                        BindingFlags.NonPublic | BindingFlags.Instance);
                    partDoc = docField?.GetValue(webFormPart) as XmlDocument;
                }
                catch { }
                if (partDoc == null) partDoc = GetReadProperty(webFormPart, "Document") as XmlDocument;
                if (partDoc == null) return;

                var scope = ResolveChangedControlNames(baselineXml, updatedXml);
                if (changedControlNames != null)
                {
                    scope = scope ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var name in changedControlNames)
                        if (!string.IsNullOrWhiteSpace(name)) scope.Add(name);
                }
                bool legacyHtml = WebFormXmlHelper.IsLegacyHtmlWebForm(partDoc.OuterXml);

                var partKbObj = GetReadProperty(webFormPart, "KBObject") ?? GetReadProperty(webFormPart, "ContainerObject") ?? GetReadProperty(webFormPart, "Parent") ?? GetReadProperty(webFormPart, "Container");
                MethodInfo enumerate = null;
                object[] enumArgs = null;
                if (partKbObj != null)
                {
                    enumerate = helperType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                        .FirstOrDefault(m =>
                        {
                            if (m.Name != "EnumerateWebTag") return false;
                            var ps = m.GetParameters();
                            return ps.Length == 2 && ps[1].ParameterType == typeof(XmlDocument) && ps[0].ParameterType.IsInstanceOfType(partKbObj);
                        });
                    if (enumerate != null) enumArgs = new object[] { partKbObj, partDoc };
                }
                if (enumerate == null)
                {
                    enumerate = helperType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                        .FirstOrDefault(m =>
                        {
                            if (m.Name != "EnumerateWebTag") return false;
                            var ps = m.GetParameters();
                            return ps.Length == 1 && ps[0].ParameterType.IsInstanceOfType(webFormPart);
                        });
                    enumArgs = new object[] { webFormPart };
                }
                if (enumerate == null) return;

                int fixupCount = 0;
                int preservedCount = 0;
                IEnumerable tags;
                try { tags = (IEnumerable)enumerate.Invoke(null, enumArgs); }
                catch (Exception ex) { Logger.Info("[DescFixup] EnumerateWebTag threw: " + (ex.InnerException ?? ex).Message); return; }

                foreach (var tag in tags)
                {
                    var node = GetReadProperty(tag, "Node") as XmlNode;
                    if (node?.Attributes == null) continue;

                    string ctrlId = GetControlId(node);
                    if (scope != null && !scope.Contains(ctrlId)) continue;

                    // Collect descriptor-name attributes present on this tag's XML node.
                    var deltas = new List<WebFormPropertyDelta>();
                    string elementName = node.LocalName;
                    foreach (var descName in _descriptorPathProps)
                    {
                        if (!NeedsDescriptorPath(descName, legacyHtml, elementName)) continue;
                        var attr = FindAttribute(node, descName);
                        if (attr == null) continue;
                        deltas.Add(new WebFormPropertyDelta {
                            ControlName = ctrlId,
                            PropertyName = descName,
                            Value = attr.Value
                        });
                    }
                    if (deltas.Count == 0) continue;

                    var applied = ApplyDescriptorDeltas(tag, ctrlId, deltas);
                    foreach (var d in deltas)
                    {
                        string canonical = ResolveCanonicalAttr(d.PropertyName, elementName);
                        bool canonicalObserved = HasCanonicalAttribute(partDoc, ctrlId, canonical);
                        bool sdkAccepted = applied != null && applied.TryGetValue(d.PropertyName, out bool accepted) && accepted;

                        // Never delete the source descriptor on a failed conversion. A
                        // missing canonical attribute is the exact legacy failure mode
                        // this guard is intended to prevent.
                        if (!sdkAccepted || !canonicalObserved)
                        {
                            preservedCount++;
                            Logger.Info("[DescFixup] preserved " + ctrlId + "." + d.PropertyName +
                                " because canonical '" + canonical + "' was not observed.");
                            continue;
                        }

                        RemoveAttribute(node, d.PropertyName);
                        var liveNode = FindElementInPartDoc(partDoc, ctrlId);
                        if (liveNode != null && !object.ReferenceEquals(liveNode, node))
                            RemoveAttribute(liveNode, d.PropertyName);
                        fixupCount++;
                        // Item 77 — record only a verified rename so the caller can surface
                        // GotchaWebFormTypedPropertyAutoRouted without claiming a lossless
                        // conversion that the SDK did not actually perform.
                        RecordAutoRoute(elementName, ctrlId, d.PropertyName, canonical);
                    }
                }

                if (fixupCount > 0 || preservedCount > 0)
                    Logger.Info("[DescFixup] routed " + fixupCount + " descriptor-property write(s); preserved " +
                        preservedCount + " unverified/legacy source attribute(s).");
            }
            catch (Exception ex)
            {
                // Descriptor repair is advisory. A reflection failure must never become
                // permission to remove source XML.
                Logger.Info("[DescFixup] outer fault: " + (ex.InnerException ?? ex).Message);
            }
        }

        // FR#1 (friction-report 2026-05-19): properties whose XML attribute name differs from
        // the SDK descriptor name. Writing them as raw XML attributes leaves them unread by the
        // HTML generator (it looks for the descriptor's mapped XML attr — e.g. "Event" for
        // gxButton.OnClickEvent, not "OnClickEvent"). These MUST route through
        // PropertiesObject.SetPropertyValueString so the SDK applies the canonical mapping.
        //
        // List is intentionally conservative — only properties confirmed via probe / friction
        // report. Extend cautiously: a wrong-positive (mapped-when-shouldn't) makes the SDK
        // path swallow user intent.
        private static readonly HashSet<string> _descriptorPathProps =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
                "OnClickEvent",      // gxButton → Event; gxAttribute/gxImage → eventGX
                "OnEnterEvent",      // gxAttribute/gxButton → eventGX (Enter override)
                "CaptionExpression", // modern gxButton/gxTextBlock → Caption; legacy keeps Tokens XML
            };

        private static bool IsCaptionExpression(string propertyName)
            => string.Equals(propertyName, "CaptionExpression", StringComparison.OrdinalIgnoreCase);

        private static bool NeedsDescriptorPath(string propertyName, bool legacyHtml, string elementName = null)
        {
            if (string.IsNullOrEmpty(propertyName) || !_descriptorPathProps.Contains(propertyName)) return false;
            // CaptionExpression is a descriptor only for the modern GxMultiForm
            // dialect. In a legacy BODY/HTML form it is the canonical Tokens
            // representation and must remain an ordinary XML attribute.
            if (IsCaptionExpression(propertyName) && legacyHtml) return false;
            return true;
        }

        // Friction-report 2026-05-22 item 77 — surface auto-routes to the caller.
        // ApplyDescriptorPathFixup pushes a (from, to, element, control) record here
        // each time it rewrites a descriptor-named XML attribute through the SDK.
        // WebFormXmlHelper / WriteService drain the list after the call so the response
        // can attach `GotchaWebFormTypedPropertyAutoRouted` warnings per rewrite.
        [ThreadStatic] private static List<AutoRouteRecord> _autoRoutes;

        public sealed class AutoRouteRecord
        {
            public string Element;
            public string ControlId;
            public string From;
            public string To;
        }

        /// <summary>
        /// Returns and clears the per-thread list of descriptor-name auto-routes
        /// recorded by the most recent ApplyDescriptorPathFixup call. Returns an
        /// empty list when nothing was routed; never null.
        /// </summary>
        public static List<AutoRouteRecord> DrainAutoRoutes()
        {
            var list = _autoRoutes;
            _autoRoutes = null;
            return list ?? new List<AutoRouteRecord>();
        }

        // Canonical XML-attr name table. Key: descriptor name. Value: element-name
        // (case-insensitive) -> XML attr name. "*" is the fallback when the
        // element-specific entry is absent.
        private static readonly Dictionary<string, Dictionary<string, string>> _descriptorCanonical =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["OnClickEvent"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["gxButton"] = "Event",
                    ["gxAttribute"] = "eventGX",
                    ["gxImage"] = "eventGX",
                    ["gxBitmap"] = "eventGX",
                    ["*"] = "Event"
                },
                ["OnEnterEvent"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["*"] = "eventGX"
                },
                ["CaptionExpression"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["*"] = "Caption"
                }
            };

        public static string ResolveCanonicalAttr(string descriptorName, string elementName)
        {
            if (string.IsNullOrEmpty(descriptorName)) return descriptorName;
            if (!_descriptorCanonical.TryGetValue(descriptorName, out var byEl)) return descriptorName;
            if (!string.IsNullOrEmpty(elementName) && byEl.TryGetValue(elementName, out var v)) return v;
            return byEl.TryGetValue("*", out var any) ? any : descriptorName;
        }

        internal static void RecordAutoRoute(string element, string controlId, string from, string to)
        {
            if (_autoRoutes == null) _autoRoutes = new List<AutoRouteRecord>();
            _autoRoutes.Add(new AutoRouteRecord
            {
                Element = element,
                ControlId = controlId,
                From = from,
                To = to
            });
        }

        // Apply deltas via Artech.Common.Properties.PropertiesObject.SetPropertyValueString(desc,
        // value). The SDK runs the registered PropertyValueConverter (e.g. GxEventReferenceConverter
        // for OnClickEvent) which produces the correct XML attribute name and quoted format.
        // SaveProperties() then mirrors the typed model into the tag's XmlNode in m_Document.
        private static Dictionary<string, bool> ApplyDescriptorDeltas(object tag, string controlName, List<WebFormPropertyDelta> deltas)
        {
            var results = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            if (tag == null || deltas == null || deltas.Count == 0) return results;
            foreach (var d in deltas) results[d.PropertyName] = false;

            object propsObj;
            try { propsObj = GetReadProperty(tag, "Properties"); }
            catch (Exception ex)
            {
                Logger.Info("[TypedWriter] descriptor path: GetProperties on " + controlName + " threw: " + (ex.InnerException ?? ex).Message);
                return results;
            }
            if (propsObj == null)
            {
                Logger.Info("[TypedWriter] descriptor path: tag " + controlName + " has no Properties — skipping " + deltas.Count + " delta(s).");
                return results;
            }

            // Probe candidate methods: SetPropertyValueString(name,value), SetPropertyValue(name,object).
            // The exact signature varies across SDK versions; try several.
            var poType = propsObj.GetType();
            var candidates = new List<MethodInfo>();
            foreach (var bindAttrs in new[] {
                BindingFlags.Public | BindingFlags.Instance,
                BindingFlags.NonPublic | BindingFlags.Instance,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance })
            {
                foreach (var name in new[] { "SetPropertyValueString", "SetPropertyValue" })
                {
                    var m = poType.GetMethods(bindAttrs).Where(mi => mi.Name == name && mi.GetParameters().Length == 2).ToList();
                    candidates.AddRange(m);
                }
            }
            candidates = candidates.Distinct().ToList();
            if (candidates.Count == 0)
            {
                Logger.Info("[TypedWriter] descriptor path: no SetPropertyValue* found on " + poType.FullName + " — methods: " +
                    string.Join(",", poType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                        .Where(mi => mi.Name.StartsWith("Set", StringComparison.OrdinalIgnoreCase))
                        .Select(mi => mi.Name + "(" + string.Join(",", mi.GetParameters().Select(p => p.ParameterType.Name)) + ")")));
                return results;
            }

            foreach (var d in deltas)
            {
                bool applied = false;
                foreach (var setMethod in candidates)
                {
                    try
                    {
                        var ps = setMethod.GetParameters();
                        object[] args;
                        if (ps[1].ParameterType == typeof(string))
                            args = new object[] { d.PropertyName, d.Value ?? string.Empty };
                        else
                            args = new object[] { d.PropertyName, (object)(d.Value ?? string.Empty) };
                        setMethod.Invoke(propsObj, args);
                        Logger.Info("[TypedWriter] descriptor " + controlName + "." + d.PropertyName + " <- '" + Truncate(d.Value, 80) + "' via " + setMethod.Name + "(" + ps[1].ParameterType.Name + ")");
                        applied = true;
                        break;
                    }
                    catch (Exception ex)
                    {
                        var inner = ex.InnerException ?? ex;
                        Logger.Info("[TypedWriter] descriptor " + setMethod.Name + " threw: " + inner.GetType().Name + ": " + inner.Message);
                    }
                }
                results[d.PropertyName] = applied;
                if (!applied)
                    Logger.Info("[TypedWriter] descriptor path: no candidate accepted " + controlName + "." + d.PropertyName);
            }

            // Flush typed model back to tag.Node XML (mirrors into m_Document).
            try
            {
                var save = tag.GetType().GetMethod("SaveProperties", Type.EmptyTypes);
                save?.Invoke(tag, null);
            }
            catch (Exception ex)
            {
                Logger.Info("[TypedWriter] descriptor path: SaveProperties on " + controlName + " threw: " + (ex.InnerException ?? ex).Message);
            }
            return results;
        }

        public sealed class DescriptorProjectionNotice
        {
            public string Element;
            public string ControlId;
            public string Source;
            public string Canonical;
            public string Action;
            public string Reason;
        }

        public sealed class DescriptorAttributeDelta
        {
            public string Element;
            public string ControlId;
            public string Attribute;
            public string Kind;
            public string Before;
            public string After;
            public string Canonical;
        }

        public sealed class DescriptorProjectionResult
        {
            public string Xml;
            public bool IsLegacyHtml;
            public List<DescriptorProjectionNotice> Notices = new List<DescriptorProjectionNotice>();
            public List<DescriptorAttributeDelta> AttributeChanges = new List<DescriptorAttributeDelta>();
        }

        /// <summary>
        /// Projects the safe descriptor decisions onto a detached XML copy. It is
        /// intentionally conservative: a source attribute is removed only when the
        /// canonical attribute is already present in the detached document. A route
        /// that still needs the SDK is reported as a notice and is not presented as
        /// a proven transformation.
        /// </summary>
        public static DescriptorProjectionResult ProjectDescriptorFixupOntoDetachedXml(
            string baselineXml, string updatedXml)
        {
            var result = new DescriptorProjectionResult { Xml = updatedXml };
            if (string.IsNullOrWhiteSpace(updatedXml)) return result;

            XDocument updated;
            try { updated = XDocument.Parse(updatedXml, LoadOptions.PreserveWhitespace); }
            catch { return result; }
            if (updated.Root == null) return result;

            result.IsLegacyHtml = WebFormXmlHelper.IsLegacyHtmlWebForm(updated.ToString());
            var scope = ResolveChangedControlNames(baselineXml, updatedXml);
            CollectAttributeDeltas(baselineXml, updated, scope, result);
            foreach (var element in updated.Descendants())
            {
                string controlId = GetElementIdentity(element);
                if (scope != null && !scope.Contains(controlId)) continue;

                foreach (var descriptor in _descriptorPathProps)
                {
                    if (!NeedsDescriptorPath(descriptor, result.IsLegacyHtml, element.Name.LocalName)) continue;
                    var source = FindAttribute(element, descriptor);
                    if (source == null) continue;
                    string canonical = ResolveCanonicalAttr(descriptor, element.Name.LocalName);
                    var notice = new DescriptorProjectionNotice
                    {
                        Element = element.Name.LocalName,
                        ControlId = controlId,
                        Source = descriptor,
                        Canonical = canonical
                    };

                    if (result.IsLegacyHtml && IsCaptionExpression(descriptor))
                    {
                        notice.Action = "preserved";
                        notice.Reason = "legacy-canonical-caption-expression";
                    }
                    else if (FindAttribute(element, canonical) != null)
                    {
                        element.Attribute(descriptor)?.Remove();
                        var delta = result.AttributeChanges.FirstOrDefault(d =>
                            string.Equals(d.ControlId, controlId, StringComparison.OrdinalIgnoreCase)
                            && string.Equals(d.Attribute, descriptor, StringComparison.OrdinalIgnoreCase));
                        if (delta != null)
                        {
                            delta.Kind = "renamed";
                            delta.Canonical = canonical;
                        }
                        notice.Action = "removed-source";
                        notice.Reason = "canonical-observed";
                    }
                    else
                    {
                        notice.Action = "would-route";
                        notice.Reason = "canonical-not-observed-in-detached-input";
                    }
                    result.Notices.Add(notice);
                }
            }

            result.Xml = updated.Declaration != null
                ? updated.Declaration + Environment.NewLine + updated.Root.ToString(SaveOptions.None)
                : updated.Root.ToString(SaveOptions.None);
            return result;
        }

        private static void CollectAttributeDeltas(
            string baselineXml,
            XDocument updated,
            HashSet<string> scope,
            DescriptorProjectionResult result)
        {
            if (updated == null || result == null || string.IsNullOrWhiteSpace(baselineXml)) return;
            XDocument baseline;
            try { baseline = XDocument.Parse(baselineXml, LoadOptions.PreserveWhitespace); }
            catch { return; }

            var before = BuildControlRecords(baseline);
            var after = BuildControlRecords(updated);
            var keys = new HashSet<string>(before.Keys, StringComparer.OrdinalIgnoreCase);
            keys.UnionWith(after.Keys);
            foreach (var key in keys)
            {
                if (scope != null && !scope.Contains(key)) continue;
                before.TryGetValue(key, out var oldRecord);
                after.TryGetValue(key, out var newRecord);
                AddAttributeDeltas(result, key, oldRecord?.Element, newRecord?.Element);
            }
        }

        private static void AddAttributeDeltas(
            DescriptorProjectionResult result,
            string controlId,
            XElement before,
            XElement after)
        {
            var oldAttrs = before?.Attributes()
                .Where(a => !a.IsNamespaceDeclaration)
                .ToDictionary(a => a.Name.LocalName, a => a.Value, StringComparer.OrdinalIgnoreCase)
                ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var newAttrs = after?.Attributes()
                .Where(a => !a.IsNamespaceDeclaration)
                .ToDictionary(a => a.Name.LocalName, a => a.Value, StringComparer.OrdinalIgnoreCase)
                ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var names = new HashSet<string>(oldAttrs.Keys, StringComparer.OrdinalIgnoreCase);
            names.UnionWith(newAttrs.Keys);
            foreach (var name in names)
            {
                oldAttrs.TryGetValue(name, out var oldValue);
                newAttrs.TryGetValue(name, out var newValue);
                bool hadOld = oldAttrs.ContainsKey(name);
                bool hasNew = newAttrs.ContainsKey(name);
                if (hadOld && hasNew && string.Equals(oldValue, newValue, StringComparison.Ordinal)) continue;
                result.AttributeChanges.Add(new DescriptorAttributeDelta
                {
                    Element = (after ?? before)?.Name.LocalName,
                    ControlId = controlId,
                    Attribute = name,
                    Kind = hadOld ? (hasNew ? "changed" : "removed") : "added",
                    Before = hadOld ? oldValue : null,
                    After = hasNew ? newValue : null
                });
            }
        }

        internal static HashSet<string> ResolveChangedControlNames(string baselineXml, string updatedXml)
        {
            var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(updatedXml))
                return string.IsNullOrWhiteSpace(baselineXml) ? null : changed;

            XDocument updated;
            try { updated = XDocument.Parse(updatedXml, LoadOptions.PreserveWhitespace); }
            catch { return changed; }

            if (string.IsNullOrWhiteSpace(baselineXml))
            {
                foreach (var element in updated.Descendants())
                {
                    string id = GetElementIdentity(element);
                    if (!string.IsNullOrWhiteSpace(id)) changed.Add(id);
                }
                return changed;
            }

            XDocument baseline;
            try { baseline = XDocument.Parse(baselineXml, LoadOptions.PreserveWhitespace); }
            catch { return changed; }

            var before = BuildControlRecords(baseline);
            var after = BuildControlRecords(updated);
            var keys = new HashSet<string>(before.Keys, StringComparer.OrdinalIgnoreCase);
            keys.UnionWith(after.Keys);
            foreach (var key in keys)
            {
                before.TryGetValue(key, out var oldRecord);
                after.TryGetValue(key, out var newRecord);
                var oldElement = oldRecord?.Element;
                var newElement = newRecord?.Element;
                if (oldElement == null || newElement == null || !EquivalentElement(oldElement, newElement))
                    changed.Add(key);
            }
            return changed;
        }

        public static IReadOnlyCollection<string> GetChangedControlNames(string baselineXml, string updatedXml)
            => ResolveChangedControlNames(baselineXml, updatedXml);

        private sealed class ControlRecord
        {
            public XElement Element;
        }

        private static Dictionary<string, ControlRecord> BuildControlRecords(XDocument document)
        {
            var records = new Dictionary<string, ControlRecord>(StringComparer.OrdinalIgnoreCase);
            var occurrences = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var element in document?.Descendants() ?? Enumerable.Empty<XElement>())
            {
                string identity = GetElementIdentity(element);
                if (string.IsNullOrWhiteSpace(identity)) continue;
                int occurrence;
                occurrences.TryGetValue(identity, out occurrence);
                occurrences[identity] = occurrence + 1;
                string key = occurrence == 0 ? identity : identity + "#" + occurrence;
                records[key] = new ControlRecord { Element = element };
            }
            return records;
        }

        private static bool EquivalentElement(XElement left, XElement right)
        {
            if (left == null || right == null) return left == right;
            if (!string.Equals(left.Name.LocalName, right.Name.LocalName, StringComparison.OrdinalIgnoreCase))
                return false;

            var leftAttrs = left.Attributes().ToDictionary(a => a.Name.LocalName, a => a.Value, StringComparer.OrdinalIgnoreCase);
            var rightAttrs = right.Attributes().ToDictionary(a => a.Name.LocalName, a => a.Value, StringComparer.OrdinalIgnoreCase);
            if (leftAttrs.Count != rightAttrs.Count) return false;
            foreach (var item in leftAttrs)
                if (!rightAttrs.TryGetValue(item.Key, out var value) || !string.Equals(item.Value, value, StringComparison.Ordinal))
                    return false;

            string leftText = string.Concat(left.Nodes().OfType<XText>().Where(t => !string.IsNullOrWhiteSpace(t.Value)).Select(t => t.Value));
            string rightText = string.Concat(right.Nodes().OfType<XText>().Where(t => !string.IsNullOrWhiteSpace(t.Value)).Select(t => t.Value));
            if (!string.Equals(leftText, rightText, StringComparison.Ordinal)) return false;

            var leftChildren = left.Elements().ToList();
            var rightChildren = right.Elements().ToList();
            if (leftChildren.Count != rightChildren.Count) return false;
            for (int i = 0; i < leftChildren.Count; i++)
                if (!EquivalentElement(leftChildren[i], rightChildren[i])) return false;
            return true;
        }

        private static string GetElementIdentity(XElement element)
        {
            if (element == null) return null;
            string id = FindAttribute(element, "id")?.Value;
            string controlName = FindAttribute(element, "ControlName")?.Value;
            if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(controlName)
                && !string.Equals(id, controlName, StringComparison.OrdinalIgnoreCase))
                return id + "|" + controlName;
            if (!string.IsNullOrWhiteSpace(id)) return id;
            if (!string.IsNullOrWhiteSpace(controlName)) return controlName;
            return FindAttribute(element, "controlName")?.Value
                ?? FindAttribute(element, "InternalName")?.Value ?? FindAttribute(element, "name")?.Value;
        }

        private static string GetControlId(XmlNode node)
        {
            string id = FindAttribute(node, "id")?.Value;
            string controlName = FindAttribute(node, "ControlName")?.Value
                ?? FindAttribute(node, "controlName")?.Value;
            if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(controlName)
                && !string.Equals(id, controlName, StringComparison.OrdinalIgnoreCase))
                return id + "|" + controlName;
            if (!string.IsNullOrWhiteSpace(id)) return id;
            if (!string.IsNullOrWhiteSpace(controlName)) return controlName;
            string internalName = FindAttribute(node, "InternalName")?.Value;
            if (!string.IsNullOrWhiteSpace(internalName)) return internalName;
            string name = FindAttribute(node, "name")?.Value;
            return !string.IsNullOrWhiteSpace(name) ? name : node?.LocalName;
        }

        private static XmlAttribute FindAttribute(XmlNode node, string name)
        {
            if (node?.Attributes == null || string.IsNullOrEmpty(name)) return null;
            foreach (XmlAttribute attr in node.Attributes)
                if (string.Equals(attr.Name, name, StringComparison.OrdinalIgnoreCase))
                    return attr;
            return null;
        }

        private static XAttribute FindAttribute(XElement element, string name)
        {
            if (element == null || string.IsNullOrEmpty(name)) return null;
            return element.Attributes().FirstOrDefault(attr =>
                string.Equals(attr.Name.LocalName, name, StringComparison.OrdinalIgnoreCase));
        }

        private static void RemoveAttribute(XmlNode node, string name)
        {
            var attr = FindAttribute(node, name);
            if (attr != null) node.Attributes.Remove(attr);
        }

        private static bool HasCanonicalAttribute(XmlDocument partDoc, string controlName, string canonical)
        {
            // Only the live m_Document node is persistence evidence. A canonical
            // attribute that exists solely on an internal SDK tag is not enough
            // to authorize deleting the source from the part that will be saved.
            var liveNode = FindElementInPartDoc(partDoc, controlName);
            return liveNode != null && FindAttribute(liveNode, canonical) != null;
        }

        private static XmlElement FindElementInPartDoc(XmlDocument doc, string controlName)
        {
            if (doc?.DocumentElement == null || string.IsNullOrEmpty(controlName)) return null;
            var names = controlName.Split('|');
            foreach (XmlNode candidate in doc.SelectNodes("//*"))
            {
                var element = candidate as XmlElement;
                if (element == null) continue;
                foreach (var name in new[] { "id", "ControlName", "controlName", "InternalName", "name" })
                {
                    string value = FindAttribute(element, name)?.Value;
                    if (names.Any(candidateName => string.Equals(value, candidateName, StringComparison.OrdinalIgnoreCase)))
                        return element;
                }
            }
            return null;
        }
        private static void InvalidateTagPropertyCache(object tag, string controlName)
        {
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            int invalidated = 0;
            for (var t = tag.GetType(); t != null && t != typeof(object); t = t.BaseType)
            {
                foreach (var f in t.GetFields(flags))
                {
                    string n = f.Name;
                    if (string.Equals(n, "m_Props", StringComparison.Ordinal) ||
                        string.Equals(n, "m_Properties", StringComparison.Ordinal))
                    {
                        try { f.SetValue(tag, null); invalidated++; }
                        catch { }
                    }
                    else if (string.Equals(n, "m_PropertiesLoaded", StringComparison.Ordinal) ||
                             string.Equals(n, "m_PropsLoaded", StringComparison.Ordinal))
                    {
                        try { f.SetValue(tag, false); invalidated++; }
                        catch { }
                    }
                }
            }
            Logger.Info("[TypedWriter] invalidated " + invalidated + " cache field(s) on tag '" + controlName + "'.");
        }

        private static MethodInfo FindInstanceMethod(Type type, string name, Type[] paramTypes)
        {
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            for (var t = type; t != null && t != typeof(object); t = t.BaseType)
            {
                var m = t.GetMethod(name, flags, null, paramTypes, null);
                if (m != null) return m;
            }
            return null;
        }

        private static Type FindType(string fullName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t = null;
                try { t = asm.GetType(fullName, false); } catch { }
                if (t != null) return t;
            }
            return null;
        }

        private static object GetReadProperty(object instance, string name)
        {
            if (instance == null) return null;
            try
            {
                var pi = instance.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (pi != null && pi.CanRead && pi.GetIndexParameters().Length == 0) return pi.GetValue(instance);
            }
            catch { }
            return null;
        }

        private static string Truncate(string s, int n) => string.IsNullOrEmpty(s) ? s : (s.Length > n ? s.Substring(0, n) + "…" : s);
    }
}
