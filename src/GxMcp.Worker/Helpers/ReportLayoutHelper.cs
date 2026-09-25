using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using System.Reflection;
using Artech.Architecture.Common.Objects;
using Artech.Genexus.Common.Objects;
using Artech.Genexus.Common.Parts;

namespace GxMcp.Worker.Helpers
{
    public static class ReportLayoutHelper
    {
        public static KBObjectPart IsReportPart(KBObjectPart part)
        {
            if (part == null) return null;
            var name = part.GetType().FullName;
            if (name.Contains("ReportPart") || name.Contains("LayoutPart")) return part;
            return null;
        }

        public static string ReadLayout(KBObjectPart part)
        {
            try
            {
                var partType = part.GetType();
                var layoutProp = partType.GetProperty("MyLayout", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)
                              ?? partType.GetProperty("Layout", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                
                if (layoutProp == null) return null;
                var layout = layoutProp.GetValue(part, null);
                if (layout == null) return null;

                var bandsProp = layout.GetType().GetProperty("ReportBands", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (bandsProp == null) return null;

                var bands = bandsProp.GetValue(layout, null) as System.Collections.IEnumerable;
                if (bands == null) return null;

                return GenerateVisualXmlFromBands(bands, layout);
            }
            catch (Exception ex)
            {
                Logger.Error("ReportLayoutHelper.ReadLayout Error: " + ex.Message);
                return null;
            }
        }

        // Visual-attribute -> SDK property for type-specific report controls. Projected
        // only when the concrete control exposes the SDK property (see probe 2026-08:
        // ReportLine has Direction/LineWidth/BorderStyle; ReportRectangle has per-side
        // BorderStyle* + CornerRadius*; ReportImage has ImageReference; ReportAttribute
        // has AttributeReference/RowExpression/ColExpression/FieldSpecifierString).
        private static readonly KeyValuePair<string, string>[] TypeSpecificReportProperties =
        {
            new KeyValuePair<string, string>("Direction", "Direction"),
            new KeyValuePair<string, string>("LineWidth", "LineWidth"),
            new KeyValuePair<string, string>("BorderStyle", "BorderStyle"),
            new KeyValuePair<string, string>("BorderStyleTop", "BorderStyleTop"),
            new KeyValuePair<string, string>("BorderStyleRight", "BorderStyleRight"),
            new KeyValuePair<string, string>("BorderStyleBottom", "BorderStyleBottom"),
            new KeyValuePair<string, string>("BorderStyleLeft", "BorderStyleLeft"),
            new KeyValuePair<string, string>("CornerRadiusTopLeft", "CornerRadiusTopLeft"),
            new KeyValuePair<string, string>("CornerRadiusTopRight", "CornerRadiusTopRight"),
            new KeyValuePair<string, string>("CornerRadiusBottomLeft", "CornerRadiusBottomLeft"),
            new KeyValuePair<string, string>("CornerRadiusBottomRight", "CornerRadiusBottomRight"),
            new KeyValuePair<string, string>("ImageReference", "ImageReference"),
            new KeyValuePair<string, string>("AttributeReference", "AttributeReference"),
            new KeyValuePair<string, string>("RowExpression", "RowExpression"),
            new KeyValuePair<string, string>("ColExpression", "ColExpression"),
            new KeyValuePair<string, string>("FieldSpecifierString", "FieldSpecifierString")
        };

        private static void AppendTypeSpecificProjection(XElement el, Type iType, object item, KeyValuePair<string, string>[] entries)
        {
            foreach (var entry in entries)
            {
                var prop = iType.GetProperty(entry.Value, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (prop == null) continue;
                var pVal = prop.GetValue(item, null);
                if (pVal == null) continue;

                string serialized;
                try
                {
                    if (entry.Key == "ImageReference")
                    {
                        // KBObjectReference serializes as XML element content; use its Name when available.
                        serialized = pVal.GetType().GetProperty("Name", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)?.GetValue(pVal, null)?.ToString();
                        if (string.IsNullOrEmpty(serialized)) continue;
                    }
                    else
                    {
                        serialized = pVal.ToString();
                        if (string.IsNullOrWhiteSpace(serialized)) continue;
                    }
                }
                catch { continue; }

                el.SetAttributeValue(entry.Key, serialized);
            }
        }

        // ReportLayout page-setup properties, projected on the <Report> root element.
        private static readonly KeyValuePair<string, string>[] PageSetupProperties =
        {
            new KeyValuePair<string, string>("PaperSize", "PaperSize"),
            new KeyValuePair<string, string>("PaperOrientation", "PaperOrientation"),
            new KeyValuePair<string, string>("PaperWidth", "PaperWidth"),
            new KeyValuePair<string, string>("PaperHeight", "PaperHeight"),
            new KeyValuePair<string, string>("RightMargin", "RightMargin"),
            new KeyValuePair<string, string>("UsePrinterSettings", "UsePrinterSettings")
        };

        private static string GenerateVisualXmlFromBands(System.Collections.IEnumerable bands, object layout = null)
        {
            var root = new XElement("Report");

            // Page-setup projection on the root (only when the layout exposes the prop).
            if (layout != null)
            {
                AppendTypeSpecificProjection(root, layout.GetType(), layout, PageSetupProperties);
            }

            foreach (var band in bands)
            {
                var bType = band.GetType();
                var bName = AttributeTypeApplier.GetPropertyUnambiguous(bType, "Name")?.GetValue(band, null)?.ToString() ?? "Band";
                var pb = new XElement("PrintBlock", new XAttribute("Name", bName), new XAttribute("ControlName", bName));

                foreach (var pName in new[] { "Height" })
                {
                    var pVal = AttributeTypeApplier.GetPropertyUnambiguous(bType, pName)?.GetValue(band, null);
                    if (pVal != null) pb.SetAttributeValue(pName, pVal.ToString());
                }

                var itemsProp = AttributeTypeApplier.GetPropertyUnambiguous(bType, "Items")
                             ?? AttributeTypeApplier.GetPropertyUnambiguous(bType, "Elements")
                             ?? AttributeTypeApplier.GetPropertyUnambiguous(bType, "Controls")
                             ?? AttributeTypeApplier.GetPropertyUnambiguous(bType, "Components");
                var items = itemsProp?.GetValue(band, null) as System.Collections.IEnumerable;

                if (items != null)
                {
                    foreach (var item in items)
                    {
                        var iType = item.GetType();
                        var el = new XElement("Control");
                        el.SetAttributeValue("TypeName", iType.Name);

                        var map = new Dictionary<string, string> {
                            { "Name", "Name" },
                            { "Left", "X" },
                            { "Top", "Y" },
                            { "Width", "Width" },
                            { "Height", "Height" },
                            { "Caption", "Text" },
                            { "ControlSource", "ControlSource" },
                            { "ForeColor", "ForeColor" },
                            { "BackColor", "BackColor" },
                            { "Borders", "Borders" },
                            { "BorderWidth", "BorderWidth" },
                            { "BorderColor", "BorderColor" },
                            { "Alignment", "Alignment" },
                            { "WordWrap", "WordWrap" },
                            { "Visible", "Visible" },
                            { "Font", "Font" },
                            { "FontName", "FontName" },
                            { "FontSize", "FontSize" },
                            { "Picture", "Picture" }
                        };

                        // Type-specific report control properties (ReportLine, ReportRectangle,
                        // ReportImage, ReportAttribute). Each entry is projected only when the
                        // concrete control type actually exposes the SDK property.
                        AppendTypeSpecificProjection(el, iType, item, TypeSpecificReportProperties);

                        foreach (var entry in map)
                        {
                            var prop = iType.GetProperty(entry.Value, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)
                                    ?? iType.GetProperty(entry.Key, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                            var pVal = prop?.GetValue(item, null);
                            if (pVal != null)
                            {
                                var serialized = pVal.ToString();
                                if (ColorHelper.IsColorAttributeName(entry.Key))
                                {
                                    serialized = ColorHelper.NormalizeColorToken(serialized);
                                }
                                el.SetAttributeValue(entry.Key, serialized);
                            }
                        }

                        var currentName = AttributeTypeApplier.GetPropertyUnambiguous(iType, "Name")?.GetValue(item, null)?.ToString();
                        var ctrlName = (AttributeTypeApplier.GetPropertyUnambiguous(iType, "ControlName")?.GetValue(item, null) ?? currentName)?.ToString();
                        if (!string.IsNullOrEmpty(ctrlName)) el.SetAttributeValue("ControlName", ctrlName);

                        pb.Add(el);
                    }
                }
                root.Add(pb);
            }
            return root.ToString();
        }

        public static bool WriteLayout(KBObjectPart part, string xml, string baselineXml = null, bool allowEnsureSaveFallback = true)
        {
            if (part == null || string.IsNullOrWhiteSpace(xml)) return false;

            try
            {
                var visualDoc = XDocument.Parse(xml);
                XDocument baselineDoc = null;
                if (!string.IsNullOrWhiteSpace(baselineXml))
                {
                    try
                    {
                        baselineDoc = XDocument.Parse(baselineXml);
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn("ReportLayoutHelper.WriteLayout baseline parse failed; applying legacy mapping: " + ex.Message);
                    }
                }

                var partType = part.GetType();
                var layoutProp = partType.GetProperty("MyLayout", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)
                              ?? partType.GetProperty("Layout", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                
                if (layoutProp == null) return false;
                var layout = layoutProp.GetValue(part, null);
                if (layout == null) return false;

                var bandsProp = layout.GetType().GetProperty("ReportBands", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (bandsProp == null) return false;

                var bandsCollection = bandsProp.GetValue(layout, null) as System.Collections.IEnumerable;
                if (bandsCollection == null) return false;
                var bandsList = bandsCollection.Cast<object>().ToList();

                bool anyChange = false;
                int appliedAssignments = 0;

                // Page-setup attributes on the <Report> root are applied back to the
                // layout object itself (PaperSize, PaperOrientation, margins, ...).
                var rootXml = visualDoc.Root;
                if (rootXml != null && string.Equals(rootXml.Name.LocalName, "Report", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var entry in PageSetupProperties)
                    {
                        var attr = rootXml.Attribute(entry.Key);
                        if (attr == null) continue;
                        if (!HasRootAttributeChanged(rootXml, baselineDoc, entry.Key)) continue;

                        foreach (var sdkPropName in ResolveSdkPropertyCandidates(entry.Key))
                        {
                            if (TrySetProperty(layout, layout.GetType(), sdkPropName, attr.Value))
                            {
                                appliedAssignments++;
                                anyChange = true;
                                break;
                            }
                        }
                    }
                }

                foreach (var elXml in visualDoc.Descendants("Control"))
                {
                    var elName = elXml.Attribute("ControlName")?.Value ?? elXml.Attribute("Name")?.Value;
                    if (string.IsNullOrEmpty(elName)) continue;
                    var baselineEl = FindBaselineControl(elXml, baselineDoc);
                    var incomingBlock = elXml.Parent;
                    string blockName = incomingBlock?.Attribute("ControlName")?.Value ?? incomingBlock?.Attribute("Name")?.Value;

                    foreach (var bandObj in bandsList)
                    {
                        if (!IsMatchingReportBand(bandObj, blockName)) continue;

                        var items = GetCollection(bandObj, "Items", "Elements", "Controls", "Components");
                        if (items == null) continue;

                        foreach (var item in items)
                        {
                            var iType = item.GetType();
                            var nameProp = iType.GetProperty("Name", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                            var currentName = nameProp?.GetValue(item, null)?.ToString();
                            var controlNameProp = iType.GetProperty("ControlName", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                            var currentControlName = controlNameProp?.GetValue(item, null)?.ToString();
                            
                            if (string.Equals(currentName, elName, StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(currentControlName, elName, StringComparison.OrdinalIgnoreCase))
                            {
                                foreach (var attr in elXml.Attributes())
                                {
                                    string aName = attr.Name.LocalName;
                                    if (IsExcludedAttribute(aName)) continue;
                                    if (!HasReportAttributeChanged(elXml, baselineEl, aName)) continue;
                                    string rawValue = attr.Value;
                                    if (ColorHelper.IsColorAttributeName(aName))
                                    {
                                        rawValue = ColorHelper.NormalizeColorToken(rawValue);
                                    }

                                    bool assignmentApplied = false;
                                    foreach (var sdkPropName in ResolveSdkPropertyCandidates(aName))
                                    {
                                        if (TryReadProperty(item, iType, sdkPropName, out string currentValue))
                                        {
                                            if (IsPropertyEquivalent(aName, sdkPropName, currentValue, rawValue))
                                            {
                                                assignmentApplied = true;
                                                break;
                                            }
                                        }

                                        if (TrySetProperty(item, iType, sdkPropName, rawValue))
                                        {
                                            assignmentApplied = true;
                                            appliedAssignments++;
                                            if (!TryReadProperty(item, iType, sdkPropName, out string afterValue))
                                            {
                                                Logger.Debug($"ReportLayoutHelper: assigned {elName}.{sdkPropName}='{rawValue}' (read-back unavailable).");
                                            }
                                            else
                                            {
                                                Logger.Debug($"ReportLayoutHelper: assigned {elName}.{sdkPropName}='{rawValue}' (read-back='{afterValue}').");
                                            }
                                            break;
                                        }
                                    }

                                    if (!assignmentApplied)
                                    {
                                        Logger.Warn($"ReportLayoutHelper: failed to map attribute '{aName}' for control '{elName}' to any writable SDK property.");
                                    }
                                }

                                anyChange = appliedAssignments > 0;
                            }
                        }
                    }
                }

                // New-control creation: controls present in the incoming XML but absent
                // from the band are created by cloning an existing control of the same
                // TypeName (or any control in the layout as a last resort), then applying
                // the incoming attributes. This is what makes adding ReportLines to an
                // empty PrintBlock work end-to-end.
                MethodInfo addChild = null;
                foreach (var bandObj in bandsList)
                {
                    string blockName = GetBandName(bandObj) ?? GetBandControlName(bandObj);
                    var blockXml = visualDoc.Descendants("PrintBlock")
                        .FirstOrDefault(b => string.Equals(
                            b.Attribute("ControlName")?.Value ?? b.Attribute("Name")?.Value,
                            blockName,
                            StringComparison.OrdinalIgnoreCase));
                    if (blockXml == null) continue;

                    var items = GetCollection(bandObj, "Items", "Elements", "Controls", "Components");
                    // Preferred mutator: collection Add; fallback: ReportBand.AddChild(ReportElement).
                    var addMethod = items != null
                        ? items.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                            .FirstOrDefault(m =>
                                string.Equals(m.Name, "Add", StringComparison.OrdinalIgnoreCase) &&
                                m.GetParameters().Length == 1)
                        : null;
                    if (addMethod == null)
                    {
                        addChild = bandObj.GetType().GetMethod("AddChild", BindingFlags.Public | BindingFlags.Instance)
                            ?? bandObj.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                                .FirstOrDefault(m => string.Equals(m.Name, "AddControl", StringComparison.OrdinalIgnoreCase));
                        if (addChild == null) continue;
                    }

                    var existingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var item in items)
                    {
                        var t = item.GetType();
                        string n = t.GetProperty("Name", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)?.GetValue(item, null)?.ToString();
                        string c = t.GetProperty("ControlName", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)?.GetValue(item, null)?.ToString();
                        if (!string.IsNullOrWhiteSpace(n)) existingNames.Add(n);
                        if (!string.IsNullOrWhiteSpace(c)) existingNames.Add(c);
                    }

                    foreach (var elXml in blockXml.Elements("Control"))
                    {
                        var elName = elXml.Attribute("ControlName")?.Value ?? elXml.Attribute("Name")?.Value;
                        if (string.IsNullOrEmpty(elName)) continue;

                        bool alreadyExists = false;
                        foreach (var item in items)
                        {
                            var iType = item.GetType();
                            var currentName = iType.GetProperty("Name", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)?.GetValue(item, null)?.ToString();
                            var currentControlName = iType.GetProperty("ControlName", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)?.GetValue(item, null)?.ToString();
                            if (string.Equals(currentName, elName, StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(currentControlName, elName, StringComparison.OrdinalIgnoreCase))
                            {
                                alreadyExists = true;
                                break;
                            }
                        }
                        if (alreadyExists) continue;

                        object newControl = CreateBandControlClone(bandObj, bandsList, elXml.Attribute("TypeName")?.Value);
                        if (newControl == null)
                        {
                            Logger.Warn($"ReportLayoutHelper.WriteLayout: cannot create new control '{elName}' — no clonable template control available.");
                            continue;
                        }

                        var nType = newControl.GetType();
                        bool named = TrySetProperty(newControl, nType, "Name", elName);
                        named = TrySetProperty(newControl, nType, "ControlName", elName) || named;
                        named = TrySetPropertyValueFallback(newControl, "Name", elName) || named;
                        named = TrySetPropertyValueFallback(newControl, "ControlName", elName) || named;
                        if (!named)
                        {
                            Logger.Warn($"ReportLayoutHelper.WriteLayout: unable to name new control '{elName}'.");
                            continue;
                        }

                        // Apply geometry LAST: SDK setters like Direction can recompute the
                        // control bounds, so Left/Top/Width/Height must be set after them or
                        // the requested geometry gets overwritten by SDK defaults.
                        foreach (var attr in elXml.Attributes()
                                     .OrderBy(a => IsGeometryAttribute(a.Name.LocalName) ? 1 : 0))
                        {
                            string aName = attr.Name.LocalName;
                            if (IsExcludedAttribute(aName)) continue;
                            string rawValue = attr.Value;
                            if (ColorHelper.IsColorAttributeName(aName))
                            {
                                rawValue = ColorHelper.NormalizeColorToken(rawValue);
                            }

                            bool applied = false;
                            foreach (var sdkPropName in ResolveSdkPropertyCandidates(aName))
                            {
                                if (TrySetProperty(newControl, nType, sdkPropName, rawValue))
                                {
                                    applied = true;
                                    break;
                                }
                            }
                            if (!applied)
                            {
                                Logger.Warn($"ReportLayoutHelper.WriteLayout: new control '{elName}' attribute '{aName}' could not be mapped to a writable SDK property.");
                            }
                        }

                        try
                        {
                            if (addMethod != null)
                            {
                                addMethod.Invoke(items, new[] { newControl });
                            }
                            else
                            {
                                addChild.Invoke(bandObj, new[] { newControl });
                            }
                            appliedAssignments++;
                            anyChange = true;
                            existingNames.Add(elName);
                            Logger.Info($"ReportLayoutHelper.WriteLayout: created new control '{elName}' in print block '{blockName}'.");
                        }
                        catch (Exception addEx)
                        {
                            Logger.Warn($"ReportLayoutHelper.WriteLayout: failed to add new control '{elName}' to band '{blockName}': {addEx.Message}");
                        }
                    }
                }

                // Explicit control removal: a typed remove_report_control request supplies
                // the complete projected XML, so controls present in the baseline but absent
                // from the requested block are intentional deletions.  Without this pass a
                // remove request would update the XML projection and then silently leave the
                // SDK control in place.  The baseline guard keeps partial legacy writes from
                // accidentally treating omitted controls as deletions.
                if (baselineDoc != null)
                {
                    foreach (var baselineBlock in baselineDoc.Descendants("PrintBlock"))
                    {
                        string baselineBlockName = GetXmlIdentity(baselineBlock, "ControlName", "Name");
                        var requestedBlock = visualDoc.Descendants("PrintBlock")
                            .FirstOrDefault(b => string.Equals(GetXmlIdentity(b, "ControlName", "Name"), baselineBlockName, StringComparison.OrdinalIgnoreCase));
                        if (requestedBlock == null) continue;

                        var requestedNames = new HashSet<string>(
                            requestedBlock.Elements("Control")
                                .Select(c => GetXmlIdentity(c, "ControlName", "Name"))
                                .Where(n => !string.IsNullOrWhiteSpace(n)),
                            StringComparer.OrdinalIgnoreCase);
                        var bandObj = bandsList.FirstOrDefault(b => IsMatchingReportBand(b, baselineBlockName));
                        if (bandObj == null) continue;
                        var items = GetCollection(bandObj, "Items", "Elements", "Controls", "Components");
                        if (items == null) continue;
                        foreach (var item in items.Cast<object>().ToList())
                        {
                            string itemName = GetObjectIdentity(item);
                            if (string.IsNullOrWhiteSpace(itemName) || requestedNames.Contains(itemName)) continue;
                            if (TryRemoveBandControl(bandObj, items, item))
                            {
                                anyChange = true;
                                appliedAssignments++;
                                Logger.Info("ReportLayoutHelper.WriteLayout: removed control '" + itemName + "' from print block '" + baselineBlockName + "'.");
                            }
                        }
                    }
                }

                foreach (var bandObj in bandsList)
                {
                    string blockName = GetBandName(bandObj) ?? GetBandControlName(bandObj);
                    var requestedBlock = visualDoc.Descendants("PrintBlock")
                        .FirstOrDefault(b => string.Equals(
                            b.Attribute("ControlName")?.Value ?? b.Attribute("Name")?.Value,
                            blockName, StringComparison.OrdinalIgnoreCase));
                    if (requestedBlock == null) continue;
                    var items = GetCollection(bandObj, "Items", "Elements", "Controls", "Components");
                    if (items == null) continue;
                    if (ApplyRequestedControlOrder(items, requestedBlock))
                    {
                        anyChange = true;
                        appliedAssignments++;
                    }
                }

                if (anyChange)
                {
                    try
                    {
                        // Some GX SDK implementations expose layout snapshots; assigning back reinforces persistence semantics.
                        layoutProp.SetValue(part, layout, null);
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn("ReportLayoutHelper: layout reassignment failed: " + ex.Message);
                    }

                    try
                    {
                        part.Save();
                        if (part.KBObject != null) part.KBObject.Save();
                        return true;
                    }
                    catch (Exception ex)
                    {
                        if (allowEnsureSaveFallback)
                        {
                            Logger.Warn("ReportLayoutHelper.WriteLayout Save failed. Trying fallback without validation barriers: " + ex.Message);
                            try
                            {
                                if (part.KBObject != null)
                                {
                                    part.KBObject.EnsureSave(false);
                                    Logger.Info("ReportLayoutHelper.WriteLayout fallback EnsureSave(false) succeeded.");
                                    return true;
                                }
                            }
                            catch (Exception fallbackEx)
                            {
                                Logger.Error("ReportLayoutHelper.WriteLayout fallback failed: " + fallbackEx.Message);
                            }
                        }
                        else
                        {
                            Logger.Warn("ReportLayoutHelper.WriteLayout strict report save failed: " + ex.Message);
                        }

                        Logger.Error("ReportLayoutHelper.WriteLayout Save failed: " + ex.Message);
                        return false;
                    }
                }
                return anyChange;
            }
            catch (Exception ex)
            {
                Logger.Error("ReportLayoutHelper.WriteLayout Error: " + ex.Message);
                return false;
            }
        }

        public static bool RenamePrintBlock(KBObjectPart part, string currentName, string newName, bool persist = true)
        {
            if (part == null || string.IsNullOrWhiteSpace(currentName) || string.IsNullOrWhiteSpace(newName))
            {
                return false;
            }

            try
            {
                var layout = GetLayoutInstance(part);
                if (layout == null) return false;
                var bands = GetBandsList(layout);
                if (bands == null || bands.Count == 0) return false;

                object band = bands.FirstOrDefault(b =>
                {
                    var n = b.GetType().GetProperty("Name", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)?.GetValue(b, null)?.ToString();
                    return string.Equals(n, currentName, StringComparison.OrdinalIgnoreCase);
                });

                if (band == null) return false;

                bool renamed = TrySetProperty(band, band.GetType(), "Name", newName);
                renamed = TrySetProperty(band, band.GetType(), "ControlName", newName) || renamed;
                renamed = TrySetPropertyValueFallback(band, "Name", newName) || renamed;
                renamed = TrySetPropertyValueFallback(band, "ControlName", newName) || renamed;

                bool readBackMatches = false;
                if (TryReadProperty(band, band.GetType(), "Name", out string afterName) &&
                    string.Equals(afterName, newName, StringComparison.OrdinalIgnoreCase))
                {
                    readBackMatches = true;
                }
                if (TryReadProperty(band, band.GetType(), "ControlName", out string afterControlName) &&
                    string.Equals(afterControlName, newName, StringComparison.OrdinalIgnoreCase))
                {
                    readBackMatches = true;
                }

                if (!renamed || !readBackMatches)
                {
                    // Fallback: clone+swap to force a new band identity bound to new name.
                    if (!TryCloneSwapBand(layout, bands, band, newName))
                    {
                        Logger.Warn($"ReportLayoutHelper.RenamePrintBlock: unable to persist rename for band '{currentName}'.");
                        return false;
                    }
                }

                TryMarkLayoutDirty(part, layout);

                if (persist)
                {
                    if (!TryPersistPart(part, "RenamePrintBlock"))
                    {
                        return false;
                    }
                    var persistedBands = GetBandsList(GetLayoutInstance(part));
                    bool exists = persistedBands != null && persistedBands.Any(b =>
                        string.Equals(GetBandName(b), newName, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(GetBandControlName(b), newName, StringComparison.OrdinalIgnoreCase));
                    if (!exists)
                    {
                        Logger.Warn($"ReportLayoutHelper.RenamePrintBlock: post-save verification failed for '{newName}'.");
                        return false;
                    }
                }
                Logger.Info(persist
                    ? $"ReportLayoutHelper.RenamePrintBlock: '{currentName}' -> '{newName}' persisted and verified."
                    : $"ReportLayoutHelper.RenamePrintBlock: '{currentName}' -> '{newName}' staged in memory.");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error("ReportLayoutHelper.RenamePrintBlock Error: " + ex.Message);
                return false;
            }
        }

        public static bool AddPrintBlock(KBObjectPart part, string printBlockName, int? height, bool persist = true)
        {
            if (part == null || string.IsNullOrWhiteSpace(printBlockName))
            {
                return false;
            }

            try
            {
                var layout = GetLayoutInstance(part);
                if (layout == null) return false;

                var bands = GetBandsList(layout);
                if (bands == null || bands.Count == 0) return false;

                var templateBand = bands.FirstOrDefault(b =>
                    string.Equals(GetBandName(b), "printBlock3", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(GetBandName(b), "printBlock1", StringComparison.OrdinalIgnoreCase))
                    ?? bands.FirstOrDefault();
                if (templateBand == null) return false;

                object newBand = null;
                try
                {
                    // Prefer an independent instance; shallow clone can alias underlying SDK handles.
                    newBand = Activator.CreateInstance(templateBand.GetType());
                }
                catch
                {
                }

                if (newBand == null)
                {
                    newBand = CloneBand(templateBand);
                }
                if (newBand == null) return false;

                var newBandType = newBand.GetType();

                bool named = TrySetProperty(newBand, newBandType, "Name", printBlockName);
                named = TrySetProperty(newBand, newBandType, "ControlName", printBlockName) || named;
                named = TrySetPropertyValueFallback(newBand, "Name", printBlockName) || named;
                named = TrySetPropertyValueFallback(newBand, "ControlName", printBlockName) || named;
                if (!named)
                {
                    Logger.Warn($"ReportLayoutHelper.AddPrintBlock: unable to set Name/ControlName='{printBlockName}'.");
                    return false;
                }

                int bandHeight = height.HasValue && height.Value > 0 ? height.Value : 40;
                TrySetProperty(newBand, newBandType, "Height", bandHeight.ToString());
                TrySetPropertyValueFallback(newBand, "Height", bandHeight.ToString());

                // Prevent duplicate fixed control names when cloning a template.
                var existingControlNames = CollectControlNames(bands);
                EnsureUniqueControlNames(newBand, existingControlNames);

                bool inserted = TryInsertBandBeforeFooter(layout, bands, newBand);
                if (!inserted)
                {
                    if (!TryAddBandToCollection(layout, newBand))
                    {
                        Logger.Warn("ReportLayoutHelper.AddPrintBlock: no compatible AddBand/collection mutator found.");
                        return false;
                    }
                }

                TryMarkLayoutDirty(part, layout);

                if (persist)
                {
                    if (!TryPersistPart(part, "AddPrintBlock"))
                    {
                        return false;
                    }
                    var persistedBands = GetBandsList(GetLayoutInstance(part));
                    bool exists = persistedBands != null && persistedBands.Any(b =>
                        string.Equals(GetBandName(b), printBlockName, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(GetBandControlName(b), printBlockName, StringComparison.OrdinalIgnoreCase));
                    if (!exists)
                    {
                        Logger.Warn($"ReportLayoutHelper.AddPrintBlock: post-save verification failed for '{printBlockName}'.");
                        return false;
                    }
                }
                Logger.Info(persist
                    ? $"ReportLayoutHelper.AddPrintBlock: '{printBlockName}' persisted and verified."
                    : $"ReportLayoutHelper.AddPrintBlock: '{printBlockName}' staged in memory.");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error("ReportLayoutHelper.AddPrintBlock Error: " + ex.Message);
                return false;
            }
        }

        public static bool DeletePrintBlock(KBObjectPart part, string printBlockName, bool persist = true)
        {
            if (part == null || string.IsNullOrWhiteSpace(printBlockName))
            {
                return false;
            }

            try
            {
                var layout = GetLayoutInstance(part);
                if (layout == null) return false;
                var bands = GetBandsList(layout);
                if (bands == null || bands.Count == 0) return false;

                object band = bands.FirstOrDefault(b =>
                    string.Equals(GetBandName(b), printBlockName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(GetBandControlName(b), printBlockName, StringComparison.OrdinalIgnoreCase));
                if (band == null) return false;

                if (!TryRemoveBand(layout, band, bands.IndexOf(band)))
                {
                    Logger.Warn($"ReportLayoutHelper.DeletePrintBlock: no compatible RemoveBand/Remove mutator found for '{printBlockName}'.");
                    return false;
                }

                TryMarkLayoutDirty(part, layout);

                if (persist)
                {
                    if (!TryPersistPart(part, "DeletePrintBlock"))
                    {
                        return false;
                    }
                    var persistedBands = GetBandsList(GetLayoutInstance(part));
                    bool exists = persistedBands != null && persistedBands.Any(b =>
                        string.Equals(GetBandName(b), printBlockName, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(GetBandControlName(b), printBlockName, StringComparison.OrdinalIgnoreCase));
                    if (exists)
                    {
                        Logger.Warn($"ReportLayoutHelper.DeletePrintBlock: post-save verification failed for '{printBlockName}'.");
                        return false;
                    }
                }
                Logger.Info(persist
                    ? $"ReportLayoutHelper.DeletePrintBlock: '{printBlockName}' persisted and verified."
                    : $"ReportLayoutHelper.DeletePrintBlock: '{printBlockName}' staged in memory.");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error("ReportLayoutHelper.DeletePrintBlock Error: " + ex.Message);
                return false;
            }
        }

        private static bool ApplyRequestedControlOrder(object items, XElement requestedBlock)
        {
            if (items == null || requestedBlock == null) return false;
            var current = (items as System.Collections.IEnumerable)?.Cast<object>().ToList();
            if (current == null || current.Count == 0) return false;
            var desiredNames = requestedBlock.Elements("Control")
                .Select(el => el.Attribute("ControlName")?.Value ?? el.Attribute("Name")?.Value)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToList();
            if (desiredNames.Count != current.Count) return false;
            var byName = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (object item in current)
            {
                string name = GetObjectIdentity(item);
                if (string.IsNullOrWhiteSpace(name) || byName.ContainsKey(name)) return false;
                byName[name] = item;
            }
            var desired = new List<object>();
            foreach (string name in desiredNames)
            {
                if (!byName.TryGetValue(name, out object item)) return false;
                desired.Add(item);
            }
            if (desired.Select(GetObjectIdentity)
                .SequenceEqual(current.Select(GetObjectIdentity), StringComparer.OrdinalIgnoreCase)) return false;

            var collectionType = items.GetType();
            var countProperty = collectionType.GetProperty("Count", BindingFlags.Public | BindingFlags.Instance);
            var itemMethod = collectionType.GetMethod("get_Item", BindingFlags.Public | BindingFlags.Instance)
                ?? collectionType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(method => string.Equals(method.Name, "Item", StringComparison.OrdinalIgnoreCase)
                        && method.GetParameters().Length == 1);
            var removeAt = collectionType.GetMethod("RemoveAt", BindingFlags.Public | BindingFlags.Instance);
            var insert = collectionType.GetMethod("Insert", BindingFlags.Public | BindingFlags.Instance);
            if (countProperty == null || itemMethod == null || removeAt == null || insert == null
                || itemMethod.GetParameters().Length != 1 || removeAt.GetParameters().Length != 1
                || insert.GetParameters().Length != 2) return false;
            int count = Convert.ToInt32(countProperty.GetValue(items, null));
            try
            {
                for (int i = count - 1; i >= 0; i--) removeAt.Invoke(items, new object[] { i });
                for (int i = 0; i < desired.Count; i++) insert.Invoke(items, new object[] { i, desired[i] });
                return true;
            }
            catch
            {
                try
                {
                    int remaining = Convert.ToInt32(countProperty.GetValue(items, null));
                    for (int i = remaining - 1; i >= 0; i--) removeAt.Invoke(items, new object[] { i });
                    for (int i = 0; i < current.Count; i++) insert.Invoke(items, new object[] { i, current[i] });
                }
                catch { }
                return false;
            }
        }

        private static bool IsExcludedAttribute(string name)
        {
            string[] excluded = { "ControlName", "Name", "TypeName" };
            return excluded.Contains(name, StringComparer.OrdinalIgnoreCase);
        }

        private static bool IsGeometryAttribute(string name)
            => string.Equals(name, "Left", StringComparison.OrdinalIgnoreCase)
               || string.Equals(name, "Top", StringComparison.OrdinalIgnoreCase)
               || string.Equals(name, "Width", StringComparison.OrdinalIgnoreCase)
               || string.Equals(name, "Height", StringComparison.OrdinalIgnoreCase);

        private static bool IsMatchingReportBand(object band, string blockName)
        {
            if (string.IsNullOrEmpty(blockName)) return true;

            string bandName = GetBandName(band);
            string bandControlName = GetBandControlName(band);
            if (string.IsNullOrEmpty(bandName) && string.IsNullOrEmpty(bandControlName)) return true;

            return string.Equals(bandName, blockName, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(bandControlName, blockName, StringComparison.OrdinalIgnoreCase);
        }

        private static XElement FindBaselineControl(XElement incomingControl, XDocument baselineDoc)
        {
            if (incomingControl == null || baselineDoc == null)
            {
                return null;
            }

            var incomingBlock = incomingControl.Parent;
            if (incomingBlock == null || !string.Equals(incomingBlock.Name.LocalName, "PrintBlock", StringComparison.OrdinalIgnoreCase))
            {
                return baselineDoc.Descendants("Control")
                    .FirstOrDefault(control => SameControlIdentity(control, incomingControl));
            }

            string blockName = incomingBlock.Attribute("ControlName")?.Value ?? incomingBlock.Attribute("Name")?.Value;
            var baselineBlock = baselineDoc.Descendants("PrintBlock")
                .FirstOrDefault(block => string.Equals(
                    block.Attribute("ControlName")?.Value ?? block.Attribute("Name")?.Value,
                    blockName,
                    StringComparison.OrdinalIgnoreCase));
            if (baselineBlock == null)
            {
                return null;
            }

            var incomingControls = incomingBlock.Elements("Control").ToList();
            int occurrence = incomingControls.IndexOf(incomingControl);
            var baselineControls = baselineBlock.Elements("Control").ToList();
            if (occurrence >= 0 && occurrence < baselineControls.Count &&
                SameControlIdentity(baselineControls[occurrence], incomingControl))
            {
                return baselineControls[occurrence];
            }

            return baselineControls.FirstOrDefault(control => SameControlIdentity(control, incomingControl));
        }

        private static bool SameControlIdentity(XElement first, XElement second)
        {
            if (first == null || second == null) return false;

            string firstName = first.Attribute("ControlName")?.Value ?? first.Attribute("Name")?.Value;
            string secondName = second.Attribute("ControlName")?.Value ?? second.Attribute("Name")?.Value;
            return string.Equals(firstName, secondName, StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasRootAttributeChanged(XElement incomingRoot, XDocument baselineDoc, string attributeName)
        {
            // Same baseline discipline as control attributes: without a baseline apply;
            // with one, only send back what the caller actually changed.
            if (incomingRoot == null) return false;
            if (baselineDoc?.Root == null) return true;
            var incoming = incomingRoot.Attribute(attributeName);
            var baseline = baselineDoc.Root.Attribute(attributeName);
            return baseline == null || !string.Equals(incoming?.Value, baseline.Value, StringComparison.Ordinal);
        }

        private static bool HasReportAttributeChanged(XElement incomingControl, XElement baselineControl, string attributeName)
        {
            // The visual projection is intentionally lossy. If a baseline is available,
            // only values changed by the caller may be sent back to the SDK; reapplying
            // the projection for every control is what resets untouched report properties.
            if (baselineControl == null) return true;

            var incoming = incomingControl.Attribute(attributeName);
            var baseline = baselineControl.Attribute(attributeName);
            return baseline == null || !string.Equals(incoming?.Value, baseline.Value, StringComparison.Ordinal);
        }

        private static bool TryPersistPart(KBObjectPart part, string operation)
        {
            if (part == null) return false;
            try
            {
                part.Save();
                part.KBObject?.Save();
                return true;
            }
            catch (Exception ex)
            {
                Logger.Warn($"ReportLayoutHelper.{operation}: default save failed, trying EnsureSave(false). Reason: {ex.Message}");
                try
                {
                    if (part.KBObject != null)
                    {
                        part.KBObject.EnsureSave(false);
                        Logger.Info($"ReportLayoutHelper.{operation}: fallback EnsureSave(false) succeeded.");
                        return true;
                    }
                }
                catch (Exception fallbackEx)
                {
                    Logger.Error($"ReportLayoutHelper.{operation}: fallback EnsureSave(false) failed: {fallbackEx.Message}");
                }

                Logger.Error($"ReportLayoutHelper.{operation}: persist failed after fallback.");
                return false;
            }
        }

        private static object ConvertValue(string value, Type targetType)
        {
            if (targetType == null) return null;

            var nullableType = Nullable.GetUnderlyingType(targetType);
            if (nullableType != null)
            {
                targetType = nullableType;
            }

            if (targetType == typeof(string)) return value;
            if (targetType == typeof(int)) return int.TryParse(value, out int i) ? (object)i : null;
            if (targetType == typeof(float)) return float.TryParse(value, out float f) ? (object)f : null;
            if (targetType == typeof(double)) return double.TryParse(value, out double d) ? (object)d : null;
            if (targetType == typeof(decimal)) return decimal.TryParse(value, out decimal dec) ? (object)dec : null;
            if (targetType == typeof(bool)) return bool.TryParse(value, out bool b) ? (object)b : null;

            if (targetType.IsEnum)
            {
                try
                {
                    return Enum.Parse(targetType, value, true);
                }
                catch
                {
                    return null;
                }
            }

            if (targetType == typeof(System.Drawing.Color))
            {
                try
                {
                    if (ColorHelper.TryParseColor(value, out var parsedColor))
                    {
                        return parsedColor;
                    }
                }
                catch { }
            }

            return null;
        }

        // Type-specific report control attribute names accepted on writes. Kept as a
        // set for O(1) lookup in ResolveSdkPropertyCandidates.
        private static readonly HashSet<string> TypeSpecificWriteAttributeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Direction", "LineWidth", "BorderStyle",
            "BorderStyleTop", "BorderStyleRight", "BorderStyleBottom", "BorderStyleLeft",
            "CornerRadiusTopLeft", "CornerRadiusTopRight", "CornerRadiusBottomLeft", "CornerRadiusBottomRight",
            "ImageReference", "AttributeReference", "RowExpression", "ColExpression", "FieldSpecifierString",
            "PaperSize", "PaperOrientation", "PaperWidth", "PaperHeight", "RightMargin", "UsePrinterSettings"
        };

        private static IEnumerable<string> ResolveSdkPropertyCandidates(string visualAttributeName)
        {
            if (string.Equals(visualAttributeName, "Caption", StringComparison.OrdinalIgnoreCase))
            {
                // In report controls, Text is the canonical SDK property, but some controls still expose Caption.
                yield return "Text";
                yield return "Caption";
                yield break;
            }

            if (string.Equals(visualAttributeName, "Left", StringComparison.OrdinalIgnoreCase))
            {
                yield return "X";
                yield return "Left";
                yield break;
            }

            if (string.Equals(visualAttributeName, "Top", StringComparison.OrdinalIgnoreCase))
            {
                yield return "Y";
                yield return "Top";
                yield break;
            }

            if (string.Equals(visualAttributeName, "ControlSource", StringComparison.OrdinalIgnoreCase))
            {
                yield return "ControlSource";
                yield return "AttributeReference";
                yield break;
            }
            if (string.Equals(visualAttributeName, "Picture", StringComparison.OrdinalIgnoreCase))
            {
                yield return "Picture";
                yield return "ImageReference";
                yield break;
            }

            // Type-specific report control properties pass through by name — the
            // per-type projection table in the reader uses identical names, and each
            // concrete SDK control only exposes what it supports.
            if (TypeSpecificWriteAttributeNames.Contains(visualAttributeName))
            {
                yield return visualAttributeName;
                yield break;
            }

            yield return visualAttributeName;
        }

        // Complex reference properties (KBObjectReference for ImageReference,
        // AttributeVariableReference for AttributeReference) can't come from a plain
        // string via ConvertValue. Build an instance and set its Name (and, when the
        // type exposes it, the object Type) from the visual value.
        private static bool IsComplexReferenceType(Type propertyType)
        {
            if (propertyType == null || propertyType == typeof(string)) return false;
            var n = propertyType.Name;
            return n.IndexOf("Reference", StringComparison.OrdinalIgnoreCase) >= 0 && !propertyType.IsEnum;
        }

        private static object BuildReferenceValue(string value, Type propertyType)
        {
            if (string.IsNullOrWhiteSpace(value) || propertyType == null) return null;
            try
            {
                object instance = null;
                foreach (var ctor in propertyType.GetConstructors())
                {
                    var pars = ctor.GetParameters();
                    if (pars.Length == 0)
                    {
                        instance = Activator.CreateInstance(propertyType);
                        break;
                    }
                    if (pars.Length == 1 && pars[0].ParameterType == typeof(string))
                    {
                        instance = Activator.CreateInstance(propertyType, value);
                        break;
                    }
                }
                if (instance == null) return null;

                // String-ctor path already carries the name; name-prop path needs it set.
                var nameProp = propertyType.GetProperty("Name", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (nameProp != null && nameProp.CanWrite && string.Equals(nameProp.GetValue(instance, null)?.ToString(), string.Empty, StringComparison.Ordinal))
                {
                    nameProp.SetValue(instance, value);
                }
                return instance;
            }
            catch (Exception ex)
            {
                Logger.Warn($"ReportLayoutHelper.BuildReferenceValue({propertyType.Name}, '{value}') failed: {ex.Message}");
                return null;
            }
        }

        private static bool TrySetProperty(object instance, Type instanceType, string sdkPropertyName, string rawValue)
        {
            if (instance == null || instanceType == null || string.IsNullOrWhiteSpace(sdkPropertyName))
            {
                return false;
            }
            string normalizedForSdk = ColorHelper.IsColorAttributeName(sdkPropertyName)
                ? ColorHelper.NormalizeColorTokenForSdkWrite(rawValue)
                : rawValue;

            var prop = instanceType.GetProperty(sdkPropertyName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (prop != null && prop.CanWrite)
            {
                object val = ConvertValue(normalizedForSdk, prop.PropertyType);
                if (val == null && IsComplexReferenceType(prop.PropertyType))
                {
                    val = BuildReferenceValue(normalizedForSdk, prop.PropertyType);
                }
                if (val != null)
                {
                    prop.SetValue(instance, val);
                    return true;
                }
            }

            // Fallback for controls that expose dynamic property setters instead of writable CLR properties.
            object dynamicValue = normalizedForSdk;
            if (ColorHelper.IsColorAttributeName(sdkPropertyName) && ColorHelper.TryParseColor(rawValue, out var dynamicColor))
            {
                dynamicValue = dynamicColor;
            }

            var setPropertyValue = instanceType.GetMethod(
                "SetPropertyValue",
                BindingFlags.Public | BindingFlags.Instance,
                null,
                new[] { typeof(string), typeof(object) },
                null);

            if (setPropertyValue != null)
            {
                setPropertyValue.Invoke(instance, new object[] { sdkPropertyName, dynamicValue });
                return true;
            }

            var setPropertyValueString = instanceType.GetMethod(
                "SetPropertyValueString",
                BindingFlags.Public | BindingFlags.Instance,
                null,
                new[] { typeof(string), typeof(string) },
                null);

            if (setPropertyValueString != null)
            {
                setPropertyValueString.Invoke(instance, new object[] { sdkPropertyName, normalizedForSdk });
                return true;
            }

            return false;
        }

        internal static bool IsColorAttributeName(string attributeName)
            => ColorHelper.IsColorAttributeName(attributeName);

        internal static bool TryParseColor(string raw, out System.Drawing.Color color)
            => ColorHelper.TryParseColor(raw, out color);

        internal static string NormalizeColorToken(string raw)
            => ColorHelper.NormalizeColorToken(raw);

        internal static string NormalizeColorTokenForSdkWrite(string raw)
            => ColorHelper.NormalizeColorTokenForSdkWrite(raw);

        internal static string ExtractColorLeafToken(string raw)
            => ColorHelper.ExtractColorLeafToken(raw);

        // Creates a new report-band control by cloning an existing control: first one
        // matching the requested TypeName (e.g. "ReportLine"), then any control in the
        // same band, then any control anywhere in the layout. Returns null when the
        // layout has no clonable control at all.
        private static object CreateBandControlClone(object band, List<object> bands, string typeName)
        {
            object template = null;

            if (!string.IsNullOrWhiteSpace(typeName))
            {
                var items = GetCollection(band, "Items", "Elements", "Controls", "Components");
                if (items != null)
                {
                    foreach (var item in items)
                    {
                        if (string.Equals(item.GetType().Name, typeName, StringComparison.OrdinalIgnoreCase))
                        {
                            template = item;
                            break;
                        }
                    }
                }
            }

            if (template == null && band != null)
            {
                var items = GetCollection(band, "Items", "Elements", "Controls", "Components");
                if (items != null) template = items.Cast<object>().FirstOrDefault();
            }

            if (template == null && bands != null)
            {
                foreach (var otherBand in bands)
                {
                    if (otherBand == null || ReferenceEquals(otherBand, band)) continue;
                    var items = GetCollection(otherBand, "Items", "Elements", "Controls", "Components");
                    if (items == null) continue;
                    var sameType = string.IsNullOrWhiteSpace(typeName)
                        ? null
                        : items.Cast<object>().FirstOrDefault(c => string.Equals(c.GetType().Name, typeName, StringComparison.OrdinalIgnoreCase));
                    template = sameType ?? items.Cast<object>().FirstOrDefault();
                    if (template != null) break;
                }
            }

            if (template == null)
            {
                // No template anywhere in the layout (e.g. every print block is empty).
                // Fall back to constructing the control directly from its SDK type name
                // (ReportLine, ReportRectangle, ReportLabel and ReportComponent all have a
                // public parameterless constructor).
                if (!string.IsNullOrWhiteSpace(typeName))
                {
                    try
                    {
                        var candidate = FindSdkControlType(typeName);
                        if (candidate != null)
                        {
                            return Activator.CreateInstance(candidate);
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"ReportLayoutHelper.CreateBandControlClone: direct construction of '{typeName}' failed: {ex.Message}");
                    }
                }
                return null;
            }

            try
            {
                // Prefer an independent instance; shallow clone can alias underlying SDK handles.
                return Activator.CreateInstance(template.GetType()) ?? CloneControl(template);
            }
            catch
            {
                return CloneControl(template);
            }
        }

        // Resolves an Artech.Genexus.Common report control type by its short name,
        // searching the Layout namespace first (ReportLine/ReportRectangle/...).
        private static Type FindSdkControlType(string typeName)
        {
            var loaded = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => a.GetName().Name.StartsWith("Artech.Genexus", StringComparison.OrdinalIgnoreCase));
            foreach (var asm in loaded)
            {
                var t = asm.GetType("Artech.Genexus.Common.Parts.Layout." + typeName);
                if (t != null && !t.IsAbstract && !t.IsInterface) return t;
                t = asm.GetTypes().FirstOrDefault(x => string.Equals(x.Name, typeName, StringComparison.OrdinalIgnoreCase)
                                                       && !x.IsAbstract && !x.IsInterface
                                                       && x.Namespace != null && x.Namespace.Contains("Parts.Layout"));
                if (t != null) return t;
            }
            return null;
        }

        private static object CloneControl(object source)
        {
            try
            {
                var cloneMethod = typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic);
                return cloneMethod?.Invoke(source, null);
            }
            catch
            {
                return null;
            }
        }

        private static bool TrySetPropertyValueFallback(object instance, string propertyName, string value)
        {
            if (instance == null || string.IsNullOrWhiteSpace(propertyName)) return false;
            var type = instance.GetType();

            try
            {
                var setPropertyValue = type.GetMethod(
                    "SetPropertyValue",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null,
                    new[] { typeof(string), typeof(object) },
                    null);
                if (setPropertyValue != null)
                {
                    setPropertyValue.Invoke(instance, new object[] { propertyName, value });
                    return true;
                }
            }
            catch { }

            try
            {
                var setPropertyValueString = type.GetMethod(
                    "SetPropertyValueString",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null,
                    new[] { typeof(string), typeof(string) },
                    null);
                if (setPropertyValueString != null)
                {
                    setPropertyValueString.Invoke(instance, new object[] { propertyName, value });
                    return true;
                }
            }
            catch { }

            return false;
        }

        private static bool IsGeometryAttributeName(string name)
        {
            return string.Equals(name, "X", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "Y", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "Left", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "Top", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "Width", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "Height", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "BorderWidth", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsEnumAttributeName(string name)
        {
            return string.Equals(name, "Alignment", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "Borders", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "WordWrap", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "Visible", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "Enabled", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsPropertyEquivalent(string aName, string sdkPropName, string currentValue, string rawValue)
        {
            if (currentValue == null && rawValue == null) return true;
            if (currentValue == null || rawValue == null) return false;

            if (string.Equals(currentValue, rawValue, StringComparison.Ordinal)) return true;

            if (ColorHelper.IsColorAttributeName(aName) || ColorHelper.IsColorAttributeName(sdkPropName))
            {
                return ColorHelper.IsColorEquivalent(currentValue, rawValue);
            }

            if (IsGeometryAttributeName(aName) || IsGeometryAttributeName(sdkPropName))
            {
                if (double.TryParse(currentValue, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double d1) &&
                    double.TryParse(rawValue, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double d2))
                {
                    return Math.Abs(d1 - d2) < 0.001;
                }
            }

            if (IsEnumAttributeName(aName) || IsEnumAttributeName(sdkPropName))
            {
                return string.Equals(currentValue.Trim(), rawValue.Trim(), StringComparison.OrdinalIgnoreCase);
            }

            return false;
        }

        private static bool TryReadProperty(object instance, Type instanceType, string sdkPropertyName, out string value)
        {
            value = null;
            try
            {
                var prop = instanceType.GetProperty(sdkPropertyName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (prop != null && prop.CanRead)
                {
                    var raw = prop.GetValue(instance, null);
                    value = raw != null ? raw.ToString() : null;
                    return true;
                }
            }
            catch { }

            return false;
        }

        private static object GetLayoutInstance(KBObjectPart part)
        {
            var partType = part.GetType();
            var layoutProp = partType.GetProperty("MyLayout", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)
                          ?? partType.GetProperty("Layout", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            return layoutProp?.GetValue(part, null);
        }

        private static List<object> GetBandsList(object layout)
        {
            if (layout == null) return null;
            var bandsProp = layout.GetType().GetProperty("ReportBands", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            var bandsCollection = bandsProp?.GetValue(layout, null) as System.Collections.IEnumerable;
            return bandsCollection?.Cast<object>().ToList();
        }

        private static string GetBandName(object band)
        {
            if (band == null) return null;
            return band.GetType().GetProperty("Name", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)?.GetValue(band, null)?.ToString();
        }

        private static string GetBandControlName(object band)
        {
            if (band == null) return null;
            return band.GetType().GetProperty("ControlName", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)?.GetValue(band, null)?.ToString();
        }

        private static object CloneBand(object sourceBand)
        {
            if (sourceBand == null) return null;
            try
            {
                var cloneMethod = typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic);
                return cloneMethod?.Invoke(sourceBand, null);
            }
            catch
            {
                return null;
            }
        }

        private static bool TryCloneSwapBand(object layout, List<object> currentBands, object oldBand, string newName)
        {
            if (layout == null || currentBands == null || oldBand == null || string.IsNullOrWhiteSpace(newName))
            {
                return false;
            }

            object replacement = CloneBand(oldBand);
            if (replacement == null) return false;

            var replacementType = replacement.GetType();
            bool renamed = TrySetProperty(replacement, replacementType, "Name", newName);
            renamed = TrySetProperty(replacement, replacementType, "ControlName", newName) || renamed;
            renamed = TrySetPropertyValueFallback(replacement, "Name", newName) || renamed;
            renamed = TrySetPropertyValueFallback(replacement, "ControlName", newName) || renamed;
            if (!renamed) return false;

            int oldIndex = currentBands.IndexOf(oldBand);
            if (oldIndex < 0) return false;

            if (!TryInsertBandAt(layout, replacement, oldIndex))
            {
                return false;
            }

            return TryRemoveBand(layout, oldBand, oldIndex + 1);
        }

        private static bool TryInsertBandBeforeFooter(object layout, List<object> currentBands, object newBand)
        {
            if (layout == null || currentBands == null || newBand == null) return false;
            int footerIndex = currentBands.FindIndex(b =>
                string.Equals(GetBandName(b), "footer", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(GetBandControlName(b), "footer", StringComparison.OrdinalIgnoreCase));
            if (footerIndex < 0) return false;
            return TryInsertBandAt(layout, newBand, footerIndex);
        }

        private static bool TryInsertBandAt(object layout, object band, int index)
        {
            if (layout == null || band == null || index < 0) return false;

            var insertBandMethod = layout.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m =>
                    string.Equals(m.Name, "InsertBand", StringComparison.OrdinalIgnoreCase) &&
                    m.GetParameters().Length == 2 &&
                    m.GetParameters()[0].ParameterType == typeof(int) &&
                    m.GetParameters()[1].ParameterType.IsAssignableFrom(band.GetType()));

            if (insertBandMethod != null)
            {
                insertBandMethod.Invoke(layout, new object[] { index, band });
                return true;
            }

            var childrenProp = layout.GetType().GetProperty("Children", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            var children = childrenProp?.GetValue(layout, null);
            if (children != null)
            {
                var insertMethod = children.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m =>
                        string.Equals(m.Name, "Insert", StringComparison.OrdinalIgnoreCase) &&
                        m.GetParameters().Length == 2 &&
                        m.GetParameters()[0].ParameterType == typeof(int) &&
                        m.GetParameters()[1].ParameterType.IsAssignableFrom(band.GetType()));
                if (insertMethod != null)
                {
                    insertMethod.Invoke(children, new object[] { index, band });
                    return true;
                }
            }

            return false;
        }

        private static bool TryRemoveBand(object layout, object band, int indexHint)
        {
            if (layout == null || band == null) return false;

            // ReportLayout (GeneXus 18) exposes only AddBand/InsertBand/ClearBands — no
            // single-band remove. Strategy: ClearBands() then re-insert every band except
            // the removed one, preserving order.
            var clearBands = layout.GetType().GetMethod("ClearBands", BindingFlags.Public | BindingFlags.Instance);
            var addBand = layout.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m =>
                    string.Equals(m.Name, "AddBand", StringComparison.OrdinalIgnoreCase) &&
                    m.GetParameters().Length == 1);
            if (clearBands != null && addBand != null)
            {
                try
                {
                    // Capture the surviving bands BEFORE clearing: the layout's iterator may
                    // be backed by the collection being cleared.
                    var survivors = GetBandsList(layout)?
                        .Where(b => !ReferenceEquals(b, band))
                        .ToList();
                    if (survivors == null) return false;

                    clearBands.Invoke(layout, null);
                    foreach (var survivor in survivors)
                    {
                        addBand.Invoke(layout, new[] { survivor });
                    }
                    return true;
                }
                catch (Exception ex)
                {
                    Logger.Warn("ReportLayoutHelper.TryRemoveBand ClearBands/re-add failed: " + ex.Message);
                }
            }

            var childrenProp = layout.GetType().GetProperty("Children", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            var children = childrenProp?.GetValue(layout, null);
            if (children != null)
            {
                var removeMethod = children.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m =>
                        string.Equals(m.Name, "Remove", StringComparison.OrdinalIgnoreCase) &&
                        m.GetParameters().Length == 1 &&
                        m.GetParameters()[0].ParameterType.IsAssignableFrom(band.GetType()));
                if (removeMethod != null)
                {
                    var removed = removeMethod.Invoke(children, new object[] { band });
                    if (removed is bool removedBool) return removedBool;
                    return true;
                }

                if (indexHint >= 0)
                {
                    var removeAtMethod = children.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                        .FirstOrDefault(m =>
                            string.Equals(m.Name, "RemoveAt", StringComparison.OrdinalIgnoreCase) &&
                            m.GetParameters().Length == 1 &&
                            m.GetParameters()[0].ParameterType == typeof(int));
                    if (removeAtMethod != null)
                    {
                        removeAtMethod.Invoke(children, new object[] { indexHint });
                        return true;
                    }
                }
            }

            return false;
        }

        private static HashSet<string> CollectControlNames(IEnumerable<object> bands)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (bands == null) return names;

            foreach (var band in bands)
            {
                var items = GetCollection(band, "Items", "Elements", "Controls", "Components");
                if (items == null) continue;
                foreach (var item in items)
                {
                    var type = item.GetType();
                    string n = type.GetProperty("Name", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)?.GetValue(item, null)?.ToString();
                    string c = type.GetProperty("ControlName", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)?.GetValue(item, null)?.ToString();
                    if (!string.IsNullOrWhiteSpace(n)) names.Add(n);
                    if (!string.IsNullOrWhiteSpace(c)) names.Add(c);
                }
            }

            return names;
        }

        private static void EnsureUniqueControlNames(object band, HashSet<string> usedNames)
        {
            if (band == null) return;
            if (usedNames == null) usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var items = GetCollection(band, "Items", "Elements", "Controls", "Components");
            if (items == null) return;

            foreach (var item in items)
            {
                var type = item.GetType();
                var nameProp = type.GetProperty("Name", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                var controlNameProp = type.GetProperty("ControlName", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);

                string current = nameProp?.GetValue(item, null)?.ToString();
                if (string.IsNullOrWhiteSpace(current))
                {
                    current = controlNameProp?.GetValue(item, null)?.ToString();
                }
                if (string.IsNullOrWhiteSpace(current))
                {
                    continue;
                }

                if (current.StartsWith("&", StringComparison.Ordinal))
                {
                    // Attribute controls can repeat across print blocks.
                    continue;
                }

                string candidate = current;
                int index = 1;
                while (usedNames.Contains(candidate))
                {
                    candidate = current + "_mcp" + index.ToString();
                    index++;
                }

                if (!string.Equals(candidate, current, StringComparison.OrdinalIgnoreCase))
                {
                    TrySetProperty(item, type, "Name", candidate);
                    TrySetProperty(item, type, "ControlName", candidate);
                    TrySetPropertyValueFallback(item, "Name", candidate);
                    TrySetPropertyValueFallback(item, "ControlName", candidate);
                }

                usedNames.Add(candidate);
            }
        }

        private static bool TryAddBandToCollection(object layout, object band)
        {
            if (layout == null || band == null) return false;

            // ReportLayout exposes AddBand(ReportBand) / InsertBand(int, ReportBand) directly on
            // the layout; ReportBands is a read-only iterator (yield-return, no Add). Prefer the
            // layout's own AddBand — this is the mutator the SDK actually provides. (Verified on
            // GeneXus 18: Artech.Genexus.Common.Parts.Layout.ReportLayout.AddBand(ReportBand).)
            var addBand = layout.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m =>
                    string.Equals(m.Name, "AddBand", StringComparison.OrdinalIgnoreCase) &&
                    m.GetParameters().Length == 1 &&
                    m.GetParameters()[0].ParameterType.IsAssignableFrom(band.GetType()));
            if (addBand != null)
            {
                addBand.Invoke(layout, new[] { band });
                return true;
            }

            var bandsProp = layout.GetType().GetProperty("ReportBands", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            var bandsCollection = bandsProp?.GetValue(layout, null);
            if (bandsCollection != null)
            {
                var addMethod = bandsCollection.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m =>
                        string.Equals(m.Name, "Add", StringComparison.OrdinalIgnoreCase) &&
                        m.GetParameters().Length == 1 &&
                        m.GetParameters()[0].ParameterType.IsAssignableFrom(band.GetType()));

                if (addMethod != null)
                {
                    addMethod.Invoke(bandsCollection, new[] { band });
                    return true;
                }
            }

            var childrenProp = layout.GetType().GetProperty("Children", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            var childrenCollection = childrenProp?.GetValue(layout, null);
            if (childrenCollection != null)
            {
                var addMethod = childrenCollection.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m =>
                        string.Equals(m.Name, "Add", StringComparison.OrdinalIgnoreCase) &&
                        m.GetParameters().Length == 1 &&
                        m.GetParameters()[0].ParameterType.IsAssignableFrom(band.GetType()));

                if (addMethod != null)
                {
                    addMethod.Invoke(childrenCollection, new[] { band });
                    return true;
                }
            }

            return false;
        }

        private static string GetXmlIdentity(XElement element, params string[] names)
        {
            if (element == null) return null;
            foreach (string name in names)
            {
                var value = element.Attributes().FirstOrDefault(a => string.Equals(a.Name.LocalName, name, StringComparison.OrdinalIgnoreCase))?.Value;
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
            return null;
        }

        private static string GetObjectIdentity(object item)
        {
            if (item == null) return null;
            var type = item.GetType();
            string name = type.GetProperty("Name", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)?.GetValue(item, null)?.ToString();
            string controlName = type.GetProperty("ControlName", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)?.GetValue(item, null)?.ToString();
            return !string.IsNullOrWhiteSpace(controlName) ? controlName : name;
        }

        private static bool TryRemoveBandControl(object band, System.Collections.IEnumerable items, object item)
        {
            if (band == null || items == null || item == null) return false;
            var collectionType = items.GetType();
            var remove = collectionType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m => string.Equals(m.Name, "Remove", StringComparison.OrdinalIgnoreCase)
                    && m.GetParameters().Length == 1
                    && m.GetParameters()[0].ParameterType.IsAssignableFrom(item.GetType()));
            if (remove != null)
            {
                try
                {
                    object result = remove.Invoke(items, new[] { item });
                    return !(result is bool removed) || removed;
                }
                catch { }
            }

            if (items is System.Collections.IList list)
            {
                int index = list.IndexOf(item);
                if (index >= 0)
                {
                    list.RemoveAt(index);
                    return true;
                }
            }

            var removeChild = band.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m => (string.Equals(m.Name, "RemoveChild", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(m.Name, "RemoveControl", StringComparison.OrdinalIgnoreCase))
                    && m.GetParameters().Length == 1
                    && m.GetParameters()[0].ParameterType.IsAssignableFrom(item.GetType()));
            if (removeChild != null)
            {
                try
                {
                    object result = removeChild.Invoke(band, new[] { item });
                    return !(result is bool removed) || removed;
                }
                catch { }
            }
            return false;
        }

        private static System.Collections.IEnumerable GetCollection(object target, params string[] propNames)
        {
            if (target == null) return null;
            var t = target.GetType();
            foreach (var name in propNames)
            {
                var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (p != null) return p.GetValue(target, null) as System.Collections.IEnumerable;
            }
            return null;
        }

        private static void TryMarkLayoutDirty(KBObjectPart part, object layout)
        {
            if (part == null || layout == null) return;
            try
            {
                var partType = part.GetType();

                var myLayoutProp = partType.GetProperty("MyLayout", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (myLayoutProp != null && myLayoutProp.CanWrite)
                {
                    myLayoutProp.SetValue(part, layout, null);
                }

                var layoutProp = partType.GetProperty("Layout", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (layoutProp != null && layoutProp.CanWrite)
                {
                    layoutProp.SetValue(part, layout, null);
                }

                var updateMethod = partType.GetMethod("UpdateMyLayout", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (updateMethod != null)
                {
                    updateMethod.Invoke(part, new[] { layout });
                }

                var dirtyProp = partType.GetProperty("Dirty", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (dirtyProp != null && dirtyProp.CanWrite && dirtyProp.PropertyType == typeof(bool))
                {
                    dirtyProp.SetValue(part, true, null);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("ReportLayoutHelper.TryMarkLayoutDirty warning: " + ex.Message);
            }
        }
    }
}
