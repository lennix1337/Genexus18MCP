using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Artech.Architecture.Common.Objects;
using Artech.Genexus.Common.Parts;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// Read-only model and write guard for the K2BTools WebPanel Designer.
    ///
    /// A WebPanel Designer is not a WorkWithPlus (or other SDK PatternInstance)
    /// object.  Its designer state is embedded in the WebForm and its generated
    /// methods are marked in Events.  This service deliberately keeps that
    /// distinction explicit: it never creates or pretends to resolve a
    /// PatternInstance, and it never invokes the IDE's regeneration path.
    /// </summary>
    internal static class K2bWebPanelDesignerService
    {
        internal const string CustomPropertiesMarker = "PATTERN_ELEMENT_CUSTOM_PROPERTIES";
        internal const string ProtectedEditorMarker = "// ---- K2BTools - Do Not Change";
        internal const string UserEditorMarker = "// ---- K2BTools - To Be Completed By User";
        internal const string DesignerKind = "K2BToolsWebPanelDesigner";

        private static readonly Regex EditorMarkerRegex = new Regex(
            @"(?im)^[\t ]*//[\t ]*----[\t ]*K2BTools[\t ]*-[\t ]*(?<kind>Do Not Change|To Be Completed By User)[^\r\n]*",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex MethodRegex = new Regex(
            @"(?m)\b(?<name>[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)?)\s*\(",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly string[] BaseTablePropertyNames =
        {
            "BaseTable", "BaseTableName", "TableName", "GridTable", "DataTable", "Table", "MainTable"
        };

        private static readonly string[] KeyPropertyNames =
        {
            "KeyAttributes", "KeyAttribute", "PrimaryKey", "PrimaryKeyAttributes", "Keys", "Key"
        };

        private static readonly string[] SelectionPropertyNames =
        {
            "SelectionMode", "SelectionType", "Selection", "MultiSelect", "AllowSelection", "SelectMode"
        };

        private static readonly string[] OrderPropertyNames =
        {
            "ControlOrder", "GridOrder", "Order"
        };

        private static readonly string[] WherePropertyNames =
        {
            "ControlWhere", "GridWhere", "Where"
        };

        internal sealed class TokenDataList
        {
            internal string Raw { get; private set; }
            internal string DecodedXml { get; private set; }
            internal JToken Decoded { get; private set; }
            internal JArray Tokens { get; private set; }

            internal TokenDataList(string raw)
            {
                Raw = raw ?? string.Empty;
                Tokens = new JArray();
                Parse();
            }

            internal JObject ToJson()
            {
                return new JObject
                {
                    ["raw"] = Raw,
                    ["decodedXml"] = DecodedXml,
                    ["decoded"] = Decoded,
                    ["tokens"] = Tokens
                };
            }

            private void Parse()
            {
                string decoded = DecodeEmbeddedText(Raw);
                XElement root = TryParseEmbeddedXml(decoded);
                if (root == null)
                {
                    DecodedXml = decoded;
                    Decoded = string.IsNullOrWhiteSpace(decoded) ? JValue.CreateNull() : new JValue(decoded);
                    return;
                }

                DecodedXml = root.ToString(SaveOptions.DisableFormatting);
                var candidates = new List<XElement>();
                foreach (var element in root.DescendantsAndSelf())
                {
                    string local = element.Name.LocalName;
                    if (local.Equals("Token", StringComparison.OrdinalIgnoreCase)
                        || local.Equals("TokenData", StringComparison.OrdinalIgnoreCase)
                        || local.Equals("Item", StringComparison.OrdinalIgnoreCase)
                        || local.Equals("Entry", StringComparison.OrdinalIgnoreCase)
                        || local.Equals("Property", StringComparison.OrdinalIgnoreCase)
                        || local.Equals("Data", StringComparison.OrdinalIgnoreCase))
                    {
                        candidates.Add(element);
                    }
                }

                // A TokenDataList can contain a single value directly under the
                // root. Preserve that value instead of claiming an empty list.
                if (candidates.Count == 0 && root.HasElements)
                {
                    candidates.AddRange(root.Elements());
                }

                foreach (var element in candidates.Take(256))
                {
                    var token = new JObject();
                    foreach (var attribute in element.Attributes())
                    {
                        token[attribute.Name.LocalName] = attribute.Value;
                    }

                    string name = FirstNonEmpty(
                        (string)element.Attribute("name"),
                        (string)element.Attribute("Name"),
                        (string)element.Attribute("id"),
                        (string)element.Attribute("Id"),
                        (string)element.Attribute("key"),
                        (string)element.Attribute("Key"),
                        ChildValue(element, "Name", "name", "Key", "key"));
                    string value = FirstNonEmpty(
                        (string)element.Attribute("value"),
                        (string)element.Attribute("Value"),
                        (string)element.Attribute("text"),
                        (string)element.Attribute("Text"),
                        ChildValue(element, "Value", "value", "Text", "text"),
                        element.Value);
                    if (!string.IsNullOrWhiteSpace(name)) token["name"] = name;
                    if (!string.IsNullOrWhiteSpace(value)) token["value"] = value;
                    if (token.Count == 0) token["value"] = element.Value;
                    Tokens.Add(token);
                }

                if (Tokens.Count > 0)
                {
                    Decoded = Tokens.DeepClone();
                }
                else if (!string.IsNullOrWhiteSpace(root.Value))
                {
                    Decoded = new JValue(root.Value);
                }
                else
                {
                    Decoded = JValue.CreateNull();
                }
            }
        }

        internal sealed class GridColumn
        {
            internal string Attribute { get; private set; }
            internal string AttributeId { get; private set; }
            internal string AttributeName { get; private set; }
            internal string Binding { get; private set; }
            internal string TitleExpression { get; private set; }
            internal bool IsKey { get; private set; }
            internal bool IsSelection { get; private set; }

            internal GridColumn(XElement element)
            {
                Attribute = FirstNonEmpty(
                    (string)element.Attribute("attribute"),
                    (string)element.Attribute("Attribute"),
                    (string)element.Attribute("variable"),
                    (string)element.Attribute("Variable"));
                var reference = ParseReference(Attribute);
                AttributeId = reference.Id;
                Binding = reference.Kind;
                AttributeName = FirstNonEmpty(
                    (string)element.Attribute("name"),
                    (string)element.Attribute("Name"),
                    (string)element.Attribute("caption"),
                    (string)element.Attribute("Caption"),
                    (string)element.Attribute("title"),
                    (string)element.Attribute("Title"),
                    (string)element.Attribute("titleExp"),
                    (string)element.Attribute("TitleExp"));
                TitleExpression = FirstNonEmpty(
                    (string)element.Attribute("titleExp"),
                    (string)element.Attribute("TitleExp"),
                    (string)element.Attribute("caption"),
                    (string)element.Attribute("Caption"));
                IsKey = IsTrue(element, "key", "isKey", "primaryKey", "primary")
                    || IsTrue(element, "role", "key");
                IsSelection = IsTrue(element, "selection", "selectable", "checkBox", "checkbox")
                    || element.Attributes().Any(a =>
                        a.Name.LocalName.IndexOf("select", StringComparison.OrdinalIgnoreCase) >= 0
                        && IsTrueValue(a.Value));
            }

            internal JObject ToJson()
            {
                return new JObject
                {
                    ["attribute"] = Attribute,
                    ["attributeId"] = AttributeId,
                    ["attributeName"] = AttributeName,
                    ["binding"] = Binding,
                    ["titleExpression"] = TitleExpression,
                    ["isKey"] = IsKey,
                    ["isSelection"] = IsSelection
                };
            }
        }

        internal sealed class Grid
        {
            internal string Name { get; private set; }
            internal string ElementName { get; private set; }
            internal string ElementId { get; private set; }
            internal string BaseTable { get; private set; }
            internal string BaseTableId { get; private set; }
            internal string SelectionMode { get; private set; }
            internal bool? MultiSelect { get; private set; }
            internal IReadOnlyList<JObject> KeyAttributes { get; private set; }
            internal IReadOnlyList<GridColumn> Columns { get; private set; }
            internal TokenDataList ControlWhere { get; private set; }
            internal TokenDataList ControlOrder { get; private set; }
            internal IReadOnlyDictionary<string, string> Properties { get; private set; }

            internal Grid(XElement element)
            {
                ElementName = element.Name.LocalName;
                ElementId = FirstNonEmpty((string)element.Attribute("id"), (string)element.Attribute("Id"));
                Name = FirstNonEmpty(
                    (string)element.Attribute("controlName"),
                    (string)element.Attribute("ControlName"),
                    (string)element.Attribute("name"),
                    (string)element.Attribute("Name"),
                    ElementId);
                Properties = ReadProperties(element);
                BaseTable = ReadBaseTable(element, Properties);
                var baseReference = ParseReference(BaseTable);
                string explicitBaseId = ReadProperty(Properties, new[] { "BaseTableId", "TableId" });
                BaseTableId = baseReference.Id ?? ParseReference(explicitBaseId).Id;
                KeyAttributes = ReadKeyAttributes(element, Properties);
                Columns = ReadColumns(element).ToList();
                SelectionMode = ReadSelectionMode(element, Properties);
                if (string.IsNullOrWhiteSpace(SelectionMode) && Columns.Any(c => c.IsSelection))
                    SelectionMode = "multi";
                MultiSelect = ReadBoolProperty(Properties, "MultiSelect", "SelectionMode", "SelectionType", "Selection");
                if (!MultiSelect.HasValue && !string.IsNullOrWhiteSpace(SelectionMode))
                {
                    if (SelectionMode.Equals("multi", StringComparison.OrdinalIgnoreCase)) MultiSelect = true;
                    else if (SelectionMode.Equals("single", StringComparison.OrdinalIgnoreCase)) MultiSelect = false;
                }
                ControlWhere = new TokenDataList(ReadProperty(Properties, WherePropertyNames));
                ControlOrder = new TokenDataList(ReadProperty(Properties, OrderPropertyNames));
            }

            internal JObject ToJson()
            {
                var propertiesJson = new JObject();
                foreach (var property in Properties.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
                    propertiesJson[property.Key] = property.Value;

                return new JObject
                {
                    ["name"] = Name,
                    ["element"] = ElementName,
                    ["elementId"] = ElementId,
                    ["baseTable"] = BaseTable,
                    ["baseTableId"] = BaseTableId,
                    ["selectionMode"] = SelectionMode,
                    ["multiSelect"] = MultiSelect.HasValue ? (JToken)MultiSelect.Value : JValue.CreateNull(),
                    ["selection"] = new JObject
                    {
                        ["mode"] = SelectionMode,
                        ["multiSelect"] = MultiSelect.HasValue ? (JToken)MultiSelect.Value : JValue.CreateNull()
                    },
                    ["keyAttributes"] = new JArray(KeyAttributes.Select(x => (JToken)x.DeepClone()).ToArray()),
                    ["keyAttributeIds"] = new JArray(KeyAttributes.Select(x => (JToken)x["id"]).ToArray()),
                    ["keyAttributeNames"] = new JArray(KeyAttributes.Select(x => (JToken)(x["name"] ?? x["attribute"])).ToArray()),
                    ["columns"] = new JArray(Columns.Select(x => (JToken)x.ToJson()).ToArray()),
                    ["controlWhere"] = ControlWhere.ToJson(),
                    ["controlOrder"] = ControlOrder.ToJson(),
                    ["controlWhereRaw"] = ControlWhere.Raw,
                    ["controlOrderRaw"] = ControlOrder.Raw,
                    ["controlWhereDecoded"] = ControlWhere.Decoded?.DeepClone(),
                    ["controlOrderDecoded"] = ControlOrder.Decoded?.DeepClone(),
                    ["properties"] = propertiesJson
                };
            }
        }

        internal sealed class EditorBlock
        {
            internal string Marker { get; set; }
            internal string Kind { get; set; }
            internal int StartOffset { get; set; }
            internal int EndOffsetExclusive { get; set; }
            internal int StartLine { get; set; }
            internal int StartColumn { get; set; }
            internal int EndLine { get; set; }
            internal int EndColumnExclusive { get; set; }
            internal string Text { get; set; }
            internal IReadOnlyList<string> MethodNames { get; set; }

            internal bool IsProtected => Kind.Equals("protected", StringComparison.OrdinalIgnoreCase);

            internal JObject ToJson()
            {
                return new JObject
                {
                    ["part"] = "Events",
                    ["kind"] = Kind,
                    ["marker"] = Marker,
                    ["startOffset"] = StartOffset,
                    ["endOffsetExclusive"] = EndOffsetExclusive,
                    ["offsetUnit"] = "utf16-character",
                    ["lineColumnBase"] = 1,
                    ["startLine"] = StartLine,
                    ["startColumn"] = StartColumn,
                    ["endLine"] = EndLine,
                    ["endColumnExclusive"] = EndColumnExclusive,
                    ["methodNames"] = new JArray(MethodNames.Select(x => (JToken)x).ToArray())
                };
            }
        }

        internal sealed class Model
        {
            internal bool Recognized { get; set; }
            internal string WebFormXml { get; set; }
            internal string EventsSource { get; set; }
            internal int CustomPropertyMarkerCount { get; set; }
            internal int EditorMarkerCount { get; set; }
            internal IReadOnlyList<Grid> Grids { get; set; }
            internal IReadOnlyList<EditorBlock> EditorBlocks { get; set; }
            internal IReadOnlyList<JObject> Actions { get; set; }
            internal IReadOnlyList<JObject> Containers { get; set; }

            internal JObject ToJson()
            {
                var result = new JObject
                {
                    ["designer"] = DesignerKind,
                    ["recognized"] = Recognized,
                    ["patternInstancePresent"] = false,
                    ["patternInstance"] = JValue.CreateNull(),
                    ["regenerationSupported"] = false,
                    ["evidence"] = new JObject
                    {
                        ["patternElementCustomProperties"] = CustomPropertyMarkerCount > 0,
                        ["customPropertyMarkerCount"] = CustomPropertyMarkerCount,
                        ["editorMarker"] = EditorMarkerCount > 0,
                        ["editorMarkerCount"] = EditorMarkerCount,
                        ["editorMarkers"] = new JArray(EditorBlocks.Select(x => (JToken)x.Marker).ToArray())
                    },
                    ["grids"] = new JArray(Grids.Select(x => (JToken)x.ToJson()).ToArray()),
                    ["actions"] = new JArray(Actions.Select(x => x.DeepClone()).ToArray()),
                    ["containers"] = new JArray(Containers.Select(x => x.DeepClone()).ToArray()),
                    ["protectedRanges"] = new JArray(EditorBlocks.Where(x => x.IsProtected).Select(x => (JToken)x.ToJson()).ToArray())
                };
                return result;
            }
        }

        /// <summary>Reads the two designer-owned parts without creating a pattern instance.</summary>
        internal static bool TryRead(KBObject obj, out Model model)
        {
            string webFormXml = null;
            string eventsSource = null;
            try { webFormXml = WebFormXmlHelper.ReadEditableXml(obj); } catch { }
            try { eventsSource = ReadEventsSource(obj); } catch { }
            model = Analyze(webFormXml, eventsSource);
            return model.Recognized;
        }

        /// <summary>Pure fixture seam used by focused tests and non-SDK callers.</summary>
        internal static Model Analyze(string webFormXml, string eventsSource)
        {
            string web = webFormXml ?? string.Empty;
            string events = eventsSource ?? string.Empty;
            var blocks = ExtractEditorBlocks(events).ToList();
            int customCount = CountOccurrences(web, CustomPropertiesMarker);
            int editorCount = blocks.Count;
            // PATTERN_ELEMENT_CUSTOM_PROPERTIES is a GeneXus-wide attribute and
            // can also appear in projected WorkWithPlus content. Require a K2B
            // editor marker, or custom properties carrying K2B designer-specific
            // grid keys, before classifying the object.
            bool recognized = editorCount > 0 || (customCount > 0 && HasK2bDesignerMetadata(web));

            var grids = new List<Grid>();
            var actions = new List<JObject>();
            var containers = new List<JObject>();
            if (recognized)
            {
                try
                {
                    var document = XDocument.Parse(web, LoadOptions.PreserveWhitespace);
                    foreach (var element in document.Descendants().Where(IsGridElement))
                    {
                        if (!HasCustomProperties(element)
                            && !element.Name.LocalName.Equals("simplegrid", StringComparison.OrdinalIgnoreCase))
                            continue;
                        try { grids.Add(new Grid(element)); }
                        catch { /* retain recognition and other readable grids */ }
                    }

                    foreach (var element in document.Descendants().Where(IsActionElement).Take(256))
                    {
                        actions.Add(new JObject
                        {
                            ["name"] = FirstNonEmpty((string)element.Attribute("name"), (string)element.Attribute("Name")),
                            ["caption"] = FirstNonEmpty((string)element.Attribute("caption"), (string)element.Attribute("Caption")),
                            ["event"] = FirstNonEmpty((string)element.Attribute("event"), (string)element.Attribute("Event"))
                        });
                    }

                    foreach (var element in document.Descendants().Where(IsContainerElement).Take(256))
                    {
                        containers.Add(new JObject
                        {
                            ["name"] = FirstNonEmpty((string)element.Attribute("name"), (string)element.Attribute("Name"), (string)element.Attribute("controlName")),
                            ["element"] = element.Name.LocalName,
                            ["caption"] = FirstNonEmpty((string)element.Attribute("caption"), (string)element.Attribute("Caption"))
                        });
                    }
                }
                catch
                {
                    // Recognition remains useful even when the embedded XML is
                    // malformed; the model reports the evidence without inventing
                    // grid values.
                }
            }

            return new Model
            {
                Recognized = recognized,
                WebFormXml = web,
                EventsSource = events,
                CustomPropertyMarkerCount = customCount,
                EditorMarkerCount = editorCount,
                Grids = grids,
                EditorBlocks = blocks,
                Actions = actions,
                Containers = containers
            };
        }

        internal static string BuildMetadataResponse(KBObject obj, Model model)
        {
            var result = model.ToJson();
            result["objectName"] = obj?.Name;
            result["objectType"] = obj?.TypeDescriptor?.Name;
            try { result["objectGuid"] = obj?.Guid.ToString("D"); } catch { }
            result["editSupported"] = false;
            result["designerStructuralEditsSupported"] = false;
            result["userExtensionEditsSupported"] = true;
            result["displayOnlyGridEditsSupported"] = true;
            result["displayOnlyAspects"] = new JArray(GridEditImpact.DisplayOnlyAspects.Select(x => (JToken)x).ToArray());
            result["regenerationRequiredAspects"] = new JArray(
                "baseTable", "keyAttributes", "selectionMode", "multiSelect",
                "controlWhere", "controlOrder", "columnAttributeSet", "gridStructure");
            result["generatedMethodDependency"] = BuildGeneratedMethodDependency(model);
            result["regenerationImpactContract"] = new JObject
            {
                ["description"] = "Grid edits are refused unless they are provably independent of the K2BTools generated 'Do Not Change' methods; unknown aspects fail closed.",
                ["failsClosed"] = true,
                ["regenerationClaimed"] = false
            };
            result["recoveryPath"] = BuildRecoveryPath(obj?.Name);
            return McpResponse.Ok(target: obj?.Name, code: "K2bWebPanelDesignerMetadataRead", result: result);
        }

        /// <summary>
        /// Read-only apply diagnosis.  It intentionally has the existing diagnose
        /// status=blocked shape, but adds an explicit unsupported outcome and the
        /// exact ranges/recovery path instead of pretending that "K2BTools" is an
        /// installed pattern with a missing PatternInstance.
        /// </summary>
        internal static string BuildPatternDiagnosis(string target, string patternKey, Model model, IEnumerable<string> availablePatterns)
        {
            var ranges = ProtectedRanges(model);
            var recovery = BuildRecoveryPath(target);
            var finding = new JObject
            {
                ["code"] = "K2BWebPanelDesignerUnsupported",
                ["reason"] = "k2bWebPanelDesigner",
                ["kind"] = "embedded-designer",
                ["severity"] = "critical",
                ["detail"] = "This WebPanel is recognized as a K2BTools WebPanel Designer object. Its designer state is embedded in WebForm/Events and it has no SDK PatternInstance for the MCP to apply or regenerate.",
                ["remediation"] = "Use the GeneXus IDE WebPanel Designer for grid/base-table/column/selection changes, save there, then re-read the object.",
                ["protectedRanges"] = ranges,
                ["recoveryPath"] = recovery
            };
            var response = new JObject
            {
                ["status"] = "blocked",
                ["outcome"] = "unsupported",
                ["code"] = "K2BWebPanelDesignerUnsupported",
                ["unsupported"] = true,
                ["target"] = target ?? string.Empty,
                ["patternKey"] = patternKey ?? string.Empty,
                ["findings"] = new JArray(finding),
                ["designer"] = model.ToJson(),
                ["patternInstancePresent"] = false,
                ["patternInstance"] = JValue.CreateNull(),
                ["editSupported"] = false,
                ["designerStructuralEditsSupported"] = false,
                ["userExtensionEditsSupported"] = true,
                ["regenerationSupported"] = false,
                ["protectedRanges"] = ranges,
                ["recoveryPath"] = recovery,
                ["availablePatterns"] = new JArray((availablePatterns ?? Enumerable.Empty<string>()).ToArray())
            };
            return response.ToString(Newtonsoft.Json.Formatting.None);
        }

        /// <summary>
        /// Pure part-level decision used by both the live writer and fixture tests.
        /// A user-completion/U_* edit is safe; generated Do Not Change blocks and
        /// embedded grid metadata are not.
        /// </summary>
        internal static bool ShouldRejectEdit(Model model, string partName, string requestedText, out string reason)
        {
            reason = null;
            if (model == null || !model.Recognized) return false;
            string part = string.IsNullOrWhiteSpace(partName) ? "Source" : partName;
            if (PatternAnalysisService.IsPatternPart(part))
            {
                reason = "patternInstanceUnsupported";
                return true;
            }
            if ((part.Equals("Events", StringComparison.OrdinalIgnoreCase)
                 || part.Equals("Source", StringComparison.OrdinalIgnoreCase)
                 || part.Equals("Code", StringComparison.OrdinalIgnoreCase))
                && ProtectedBlocksChanged(model.EventsSource, requestedText))
            {
                reason = "protectedEditorBlockChanged";
                return true;
            }
            if (WebFormXmlHelper.IsVisualPart(part)
                && HasDesignerGridChanges(model.WebFormXml, requestedText))
            {
                // A grid edit is only refused when it can invalidate a K2BTools
                // generated method.  Display-only changes (column titles/order)
                // are proven independent of the generated editor blocks and stay
                // on the normal write path; everything else fails closed.
                var impact = ClassifyGridEdit(model, requestedText);
                if (impact.RequiresRegeneration)
                {
                    reason = "designerGridMetadataChanged";
                    return true;
                }
                return false;
            }
            return false;
        }

        /// <summary>
        /// Checks a prospective edit without invoking any SDK save/apply/build
        /// operation.  Returns a canonical unsupported envelope when the request
        /// would change designer-owned grid state or a generated editor block.
        /// Safe edits outside those surfaces return false and remain on the
        /// existing write path.
        /// </summary>
        internal static bool TryBuildEditRejection(
            KBObject obj,
            string partName,
            string requestedText,
            out string response)
        {
            response = null;
            if (obj == null) return false;
            if (!TryRead(obj, out Model model)) return false;

            if (!ShouldRejectEdit(model, partName, requestedText, out string reason))
                return false;
            string part = string.IsNullOrWhiteSpace(partName) ? "Source" : partName;

            var impact = WebFormXmlHelper.IsVisualPart(part)
                ? ClassifyGridEdit(model, requestedText)
                : null;

            response = BuildEditRejectionResponse(model, obj.Name, part, reason, impact);
            return true;
        }

        internal static string BuildEditRejectionResponse(
            Model model,
            string target,
            string partName,
            string reason,
            GridEditImpact impact = null)
        {
            var ranges = ProtectedRanges(model);
            var recovery = BuildRecoveryPath(target);
            var extra = new JObject
            {
                ["reason"] = reason,
                ["designer"] = model.ToJson(),
                ["part"] = partName,
                ["protectedRanges"] = ranges,
                ["generatedMethodDependency"] = BuildGeneratedMethodDependency(model),
                ["regenerationImpact"] = impact?.ToJson(),
                ["displayOnlyAspects"] = new JArray(GridEditImpact.DisplayOnlyAspects.Select(x => (JToken)x).ToArray()),
                ["recoveryPath"] = recovery,
                ["persisted"] = false,
                ["writeAttempted"] = false,
                ["regenerationClaimed"] = false,
                ["patternInstancePresent"] = false,
                ["patternInstance"] = JValue.CreateNull()
            };
            return McpResponse.Err(
                code: reason == "protectedEditorBlockChanged"
                    ? "K2BProtectedEditorBlock"
                    : reason == "patternInstanceUnsupported"
                        ? "K2BWebPanelDesignerUnsupported"
                        : "K2BDesignerEditUnsupported",
                message: reason == "protectedEditorBlockChanged"
                    ? "The edit touches a K2BTools generated editor block marked 'Do Not Change'."
                    : reason == "patternInstanceUnsupported"
                        ? "This K2BTools WebPanel Designer has no PatternInstance; the MCP will not fabricate one or claim editor regeneration."
                        : "The edit changes K2BTools WebPanel Designer grid metadata, which requires the IDE designer to regenerate dependent methods.",
                hint: "Open the object in the GeneXus IDE K2BTools WebPanel Designer, change and save the grid there, then re-read WebForm and Events.",
                nextSteps: new JArray(
                    McpResponse.NextStep(
                        tool: "genexus_analyze",
                        args: new JObject { ["name"] = target, ["mode"] = "pattern_metadata" },
                        why: "Re-read the embedded K2BTools designer model after the IDE save."),
                    McpResponse.NextStep(
                        tool: "genexus_read",
                        args: new JObject { ["name"] = target, ["part"] = "Events" },
                        why: "Inspect the generated and user-completion blocks before adapting U_* methods.")),
                target: target,
                extra: extra,
                errorExtra: extra);
        }

        internal static JArray ProtectedRanges(Model model)
        {
            return new JArray((model?.EditorBlocks ?? new EditorBlock[0])
                .Where(x => x.IsProtected)
                .Select(x => (JToken)x.ToJson())
                .ToArray());
        }

        /// <summary>
        /// Aspects of the embedded designer grid that K2BTools may materialize into
        /// the "Do Not Change" editor methods.  A proposed edit is only safe to let
        /// through the existing write path when it cannot invalidate any generated
        /// method; everything else must be refused so the IDE designer regenerates.
        /// </summary>
        internal sealed class GridEditImpact
        {
            /// <summary>Grid aspects the generated editor methods are allowed to be independent of.</summary>
            internal static readonly string[] DisplayOnlyAspects =
            {
                "columnTitleExpression",
                "columnCaption",
                "columnOrder"
            };

            internal bool RequiresRegeneration { get; set; }
            internal IReadOnlyList<string> ChangedAspects { get; set; } = new string[0];
            internal IReadOnlyList<string> RegenerationReasons { get; set; } = new string[0];
            internal IReadOnlyList<string> DisplayOnlyChanges { get; set; } = new string[0];
            internal bool Compared { get; set; }

            internal JObject ToJson()
            {
                return new JObject
                {
                    ["compared"] = Compared,
                    ["requiresRegeneration"] = RequiresRegeneration,
                    ["changedAspects"] = new JArray(ChangedAspects.Select(x => (JToken)x).ToArray()),
                    ["regenerationReasons"] = new JArray(RegenerationReasons.Select(x => (JToken)x).ToArray()),
                    ["displayOnlyChanges"] = new JArray(DisplayOnlyChanges.Select(x => (JToken)x).ToArray()),
                    ["regenerationClaimed"] = false
                };
            }
        }

        /// <summary>
        /// Derives what the generated "Do Not Change" blocks actually consume from
        /// the real Events source, so display-only grid edits can be proven safe
        /// instead of being refused wholesale.
        /// </summary>
        internal static JObject BuildGeneratedMethodDependency(Model model)
        {
            var protectedText = string.Join("\n", (model?.EditorBlocks ?? new EditorBlock[0])
                .Where(x => x.IsProtected)
                .Select(x => x.Text ?? string.Empty));
            var gridNames = (model?.Grids ?? new Grid[0])
                .Select(x => x.Name)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var keyIds = (model?.Grids ?? new Grid[0])
                .SelectMany(x => x.KeyAttributes ?? (IReadOnlyList<JObject>)new JObject[0])
                .Select(x => (string)x["id"])
                .Concat((model?.Grids ?? new Grid[0])
                    .SelectMany(x => x.Columns ?? (IReadOnlyList<GridColumn>)new GridColumn[0])
                    .Where(c => c.IsKey)
                    .Select(c => c.AttributeId))
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new JObject
            {
                ["hasGeneratedBlocks"] = !string.IsNullOrWhiteSpace(protectedText),
                ["gridControlNames"] = new JArray(gridNames.Select(x => (JToken)x).ToArray()),
                ["keyAttributeIds"] = new JArray(keyIds.Select(x => (JToken)x).ToArray()),
                ["referencesGridControls"] = gridNames.Any(name =>
                    protectedText.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0),
                ["referencesKeyAttributes"] = keyIds.Any(id =>
                    protectedText.IndexOf(id, StringComparison.OrdinalIgnoreCase) >= 0)
            };
        }

        /// <summary>
        /// Classifies a proposed WebForm grid edit against the aspects K2BTools
        /// materializes into generated methods.  Fails closed: when the grids
        /// cannot be compared, or an aspect outside the proven display-only set
        /// changed, the edit is reported as requiring regeneration.
        /// </summary>
        internal static GridEditImpact ClassifyGridEdit(Model model, string requestedXml)
        {
            var impact = new GridEditImpact();
            if (string.IsNullOrWhiteSpace(requestedXml))
            {
                impact.RequiresRegeneration = true;
                impact.RegenerationReasons = new[] { "requestedWebFormEmpty" };
                return impact;
            }

            List<Grid> current;
            List<Grid> requested;
            if (!TryExtractComparableGrids(model?.WebFormXml, requestedXml, out current, out requested))
            {
                impact.RequiresRegeneration = true;
                impact.RegenerationReasons = new[] { "gridComparisonUnavailable" };
                return impact;
            }

            impact.Compared = true;
            var changed = new List<string>();
            var reasons = new List<string>();
            var displayOnly = new List<string>();

            if (current.Count != requested.Count)
            {
                changed.Add("gridCount");
                reasons.Add("gridStructureChanged");
            }
            else
            {
                for (int i = 0; i < current.Count; i++)
                {
                    var a = current[i];
                    var b = requested[i];
                    string label = string.IsNullOrWhiteSpace(a.Name) ? ("grid[" + i + "]") : a.Name;

                    if (!string.Equals(a.BaseTable, b.BaseTable, StringComparison.OrdinalIgnoreCase))
                    {
                        changed.Add(label + ".baseTable");
                        reasons.Add("baseTableChanged");
                    }
                    if (!SameKeyAttributes(a, b))
                    {
                        changed.Add(label + ".keyAttributes");
                        reasons.Add("keyAttributesChanged");
                    }
                    if (!string.Equals(a.SelectionMode, b.SelectionMode, StringComparison.OrdinalIgnoreCase))
                    {
                        changed.Add(label + ".selectionMode");
                        reasons.Add("selectionModeChanged");
                    }
                    if (!SameScalar(a.ControlWhere?.Raw, b.ControlWhere?.Raw))
                    {
                        changed.Add(label + ".controlWhere");
                        reasons.Add("conditionsChanged");
                    }
                    if (!SameScalar(a.ControlOrder?.Raw, b.ControlOrder?.Raw))
                    {
                        changed.Add(label + ".controlOrder");
                        reasons.Add("orderingChanged");
                    }
                    if (a.MultiSelect != b.MultiSelect)
                    {
                        changed.Add(label + ".multiSelect");
                        reasons.Add("selectionModeChanged");
                    }

                    var columnChanges = CompareColumns(a.Columns, b.Columns, label, displayOnly);
                    if (columnChanges > 0)
                    {
                        changed.Add(label + ".columns");
                        if (DisplayOnlyColumnChanges(a.Columns, b.Columns))
                            displayOnly.Add(label + ".columns");
                        else
                            reasons.Add("columnSetChanged");
                    }
                }
            }

            bool displayOnlyChange = changed.Count > 0 && reasons.Count == 0;
            impact.ChangedAspects = changed;
            impact.DisplayOnlyChanges = displayOnlyChange ? displayOnly : new string[0];
            impact.RegenerationReasons = reasons;
            // Fail closed: an unrecognized or empty comparison is never "safe".
            impact.RequiresRegeneration = !displayOnlyChange;
            return impact;
        }

        private static bool SameScalar(string a, string b)
            => string.Equals(NormalizeScalar(a), NormalizeScalar(b), StringComparison.Ordinal);

        private static string NormalizeScalar(string value)
            => (value ?? string.Empty).Trim();

        private static bool SameKeyAttributes(Grid a, Grid b)
        {
            var left = KeyIds(a).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
            var right = KeyIds(b).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
            return left.Count == right.Count && left.SequenceEqual(right, StringComparer.OrdinalIgnoreCase);
        }

        private static List<string> KeyIds(Grid grid)
        {
            var ids = new List<string>();
            foreach (var key in grid.KeyAttributes ?? (IReadOnlyList<JObject>)new JObject[0])
            {
                var id = (string)key["id"];
                if (!string.IsNullOrWhiteSpace(id)) ids.Add(id);
            }
            foreach (var column in grid.Columns ?? (IReadOnlyList<GridColumn>)new GridColumn[0])
            {
                if (column.IsKey && !string.IsNullOrWhiteSpace(column.AttributeId)) ids.Add(column.AttributeId);
            }
            return ids.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static int CompareColumns(
            IReadOnlyList<GridColumn> current,
            IReadOnlyList<GridColumn> requested,
            string label,
            List<string> displayOnly)
        {
            int changes = 0;
            var a = current ?? new GridColumn[0];
            var b = requested ?? new GridColumn[0];
            if (a.Count != b.Count)
            {
                displayOnly.Add(label + ".columns.addedOrRemoved");
                return 1;
            }
            for (int i = 0; i < a.Count; i++)
            {
                bool sameAttribute = string.Equals(a[i].Attribute, b[i].Attribute, StringComparison.OrdinalIgnoreCase);
                bool sameKey = a[i].IsKey == b[i].IsKey;
                bool sameSelection = a[i].IsSelection == b[i].IsSelection;
                if (!sameAttribute) { changes++; displayOnly.Add(label + ".columns[" + i + "].attribute"); }
                if (!sameKey) { changes++; displayOnly.Add(label + ".columns[" + i + "].isKey"); }
                if (!sameSelection) { changes++; displayOnly.Add(label + ".columns[" + i + "].isSelection"); }
                if (!SameScalar(a[i].TitleExpression, b[i].TitleExpression))
                {
                    changes++;
                    displayOnly.Add(label + ".columns[" + i + "].titleExpression");
                }
            }
            return changes;
        }

        /// <summary>
        /// A column change is display-only when the attribute bindings, key flags
        /// and selection flags are identical across the two grids; only titles or
        /// ordering may differ.
        /// </summary>
        /// <summary>
        /// A column-set change is display-only when the identity/selection
        /// contract the generated editor methods depend on is preserved: the
        /// key and selection columns must keep the same attributes, flags and
        /// relative order, and every added/removed column must be a plain
        /// display column.  Retained columns may only change title/caption.
        /// </summary>
        private static bool DisplayOnlyColumnChanges(IReadOnlyList<GridColumn> current, IReadOnlyList<GridColumn> requested)
        {
            var a = current ?? new GridColumn[0];
            var b = requested ?? new GridColumn[0];

            // The generated "Do Not Change" methods are materialized against the
            // key and the selection item.  If either set changed identity, flags,
            // count or relative order, the edit is not display-only.
            if (!SameContractColumns(ContractColumns(a), ContractColumns(b)))
                return false;

            // A column may be added or removed only when it is not part of that
            // contract, and the surviving columns may only change titles.
            var remaining = b.ToList();
            foreach (var column in a)
            {
                int index = remaining.FindIndex(candidate =>
                    string.Equals(candidate.Attribute, column.Attribute, StringComparison.OrdinalIgnoreCase)
                    && candidate.IsKey == column.IsKey
                    && candidate.IsSelection == column.IsSelection);
                if (index < 0)
                {
                    if (column.IsKey || column.IsSelection) return false;
                    continue;
                }

                var match = remaining[index];
                remaining.RemoveAt(index);
                if (!SameScalar(column.TitleExpression, match.TitleExpression)) continue;
                if (!string.Equals(column.AttributeName, match.AttributeName, StringComparison.Ordinal)) return false;
            }

            foreach (var column in remaining)
            {
                if (column.IsKey || column.IsSelection) return false;
            }
            return true;
        }

        private static List<GridColumn> ContractColumns(IReadOnlyList<GridColumn> columns)
        {
            return (columns ?? new GridColumn[0])
                .Where(c => c.IsKey || c.IsSelection)
                .ToList();
        }

        private static bool SameContractColumns(List<GridColumn> left, List<GridColumn> right)
        {
            if (left.Count != right.Count) return false;
            for (int i = 0; i < left.Count; i++)
            {
                if (!string.Equals(left[i].Attribute, right[i].Attribute, StringComparison.OrdinalIgnoreCase)) return false;
                if (left[i].IsKey != right[i].IsKey) return false;
                if (left[i].IsSelection != right[i].IsSelection) return false;
            }
            return true;
        }

        private static bool TryExtractComparableGrids(
            string currentXml,
            string requestedXml,
            out List<Grid> current,
            out List<Grid> requested)
        {
            current = new List<Grid>();
            requested = new List<Grid>();
            try
            {
                XDocument currentDoc = XDocument.Parse(currentXml ?? string.Empty, LoadOptions.PreserveWhitespace);
                XDocument requestedDoc = XDocument.Parse(requestedXml, LoadOptions.PreserveWhitespace);
                var currentMarked = currentDoc.Descendants().Where(IsGridElement).Where(HasCustomProperties).ToList();
                var requestedMarked = requestedDoc.Descendants().Where(IsGridElement).Where(HasCustomProperties).ToList();
                var currentGrids = currentMarked.Count > 0 ? currentMarked : currentDoc.Descendants().Where(IsGridElement).ToList();
                var requestedGrids = requestedMarked.Count > 0 ? requestedMarked : requestedDoc.Descendants().Where(IsGridElement).ToList();
                current = currentGrids.Select(x => new Grid(x)).ToList();
                requested = requestedGrids.Select(x => new Grid(x)).ToList();
                return current.Count > 0 && requested.Count > 0;
            }
            catch
            {
                current = new List<Grid>();
                requested = new List<Grid>();
                return false;
            }
        }

        internal static JObject BuildRecoveryPath(string target)
        {
            return new JObject
            {
                ["tool"] = "GeneXus IDE",
                ["surface"] = "K2BTools WebPanel Designer",
                ["instruction"] = "Open the WebPanel in the K2BTools WebPanel Designer, change the grid/base table/columns/conditions/order, and save so K2BTools can regenerate its editor methods.",
                ["steps"] = new JArray(
                    "Open the WebPanel Designer in the GeneXus IDE.",
                    "Make the structural grid change in the designer and save it there.",
                    "Let K2BTools regenerate the blocks marked 'Do Not Change'.",
                    "Re-read the object with genexus_analyze mode=pattern_metadata before adapting user-completion U_* subs."),
                ["readBack"] = new JObject
                {
                    ["tool"] = "genexus_analyze",
                    ["args"] = new JObject { ["name"] = target, ["mode"] = "pattern_metadata" }
                },
                ["regenerationClaimed"] = false
            };
        }

        private static string ReadEventsSource(KBObject obj)
        {
            if (obj == null) return null;
            var part = GxMcp.Worker.Structure.PartAccessor.GetPart(obj, "Events");
            if (part is ISource source) return source.Source;
            try
            {
                var property = part?.GetType().GetProperty("Source", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                return property?.GetValue(part, null) as string;
            }
            catch { return null; }
        }

        private static IReadOnlyList<EditorBlock> ExtractEditorBlocks(string source)
        {
            var result = new List<EditorBlock>();
            if (string.IsNullOrEmpty(source)) return result;
            var matches = EditorMarkerRegex.Matches(source);
            for (int i = 0; i < matches.Count; i++)
            {
                Match match = matches[i];
                int start = match.Index;
                int end = i + 1 < matches.Count ? matches[i + 1].Index : source.Length;
                if (end <= start) continue;
                string kind = match.Groups["kind"].Value.Equals("Do Not Change", StringComparison.OrdinalIgnoreCase)
                    ? "protected"
                    : "userExtension";
                var startPosition = GetLineColumn(source, start);
                var endPosition = GetLineColumn(source, Math.Max(start, end - 1));
                var methods = MethodRegex.Matches(source.Substring(start, end - start))
                    .Cast<Match>()
                    .Select(m => m.Groups["name"].Value)
                    .Where(n => !string.IsNullOrWhiteSpace(n) && !IsLanguageKeyword(n))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(32)
                    .ToList();
                result.Add(new EditorBlock
                {
                    Marker = match.Value.Trim(),
                    Kind = kind,
                    StartOffset = start,
                    EndOffsetExclusive = end,
                    StartLine = startPosition.Line,
                    StartColumn = startPosition.Column,
                    EndLine = endPosition.Line,
                    EndColumnExclusive = endPosition.Column + 1,
                    Text = source.Substring(start, end - start),
                    MethodNames = methods
                });
            }
            return result;
        }

        private static bool ProtectedBlocksChanged(string currentSource, string requestedSource)
        {
            var current = ExtractEditorBlocks(currentSource).Where(x => x.IsProtected).ToList();
            var requested = ExtractEditorBlocks(requestedSource).Where(x => x.IsProtected).ToList();
            if (current.Count != requested.Count) return true;
            for (int i = 0; i < current.Count; i++)
            {
                if (!string.Equals(NormalizeLineEndings(current[i].Text), NormalizeLineEndings(requested[i].Text), StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        private static bool HasDesignerGridChanges(string currentXml, string requestedXml)
        {
            if (string.IsNullOrWhiteSpace(requestedXml)) return false;
            XDocument current;
            XDocument requested;
            try
            {
                current = XDocument.Parse(currentXml ?? string.Empty, LoadOptions.PreserveWhitespace);
                requested = XDocument.Parse(requestedXml, LoadOptions.PreserveWhitespace);
            }
            catch { return false; }

            var currentMarkedGrids = current.Descendants().Where(IsGridElement).Where(HasCustomProperties).ToList();
            var requestedMarkedGrids = requested.Descendants().Where(IsGridElement).Where(HasCustomProperties).ToList();
            bool compareAllGrids = currentMarkedGrids.Count == 0 && requestedMarkedGrids.Count == 0;
            var currentGrids = compareAllGrids
                ? current.Descendants().Where(IsGridElement).ToList()
                : currentMarkedGrids;
            var requestedGrids = compareAllGrids
                ? requested.Descendants().Where(IsGridElement).ToList()
                : requestedMarkedGrids;
            if (currentGrids.Count != requestedGrids.Count) return true;

            for (int i = 0; i < currentGrids.Count; i++)
            {
                if (!XNode.DeepEquals(currentGrids[i], requestedGrids[i])) return true;
            }
            return false;
        }

        private static bool IsGridElement(XElement element)
        {
            string name = element.Name.LocalName;
            return name.Equals("simplegrid", StringComparison.OrdinalIgnoreCase)
                || name.Equals("grid", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsActionElement(XElement element)
        {
            string name = element.Name.LocalName;
            return name.IndexOf("action", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsContainerElement(XElement element)
        {
            string name = element.Name.LocalName;
            return name.Equals("table", StringComparison.OrdinalIgnoreCase)
                || name.Equals("container", StringComparison.OrdinalIgnoreCase)
                || name.Equals("fieldset", StringComparison.OrdinalIgnoreCase)
                || name.Equals("form", StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasCustomProperties(XElement element)
        {
            return element.Attributes().Any(a => a.Name.LocalName.Equals(CustomPropertiesMarker, StringComparison.OrdinalIgnoreCase));
        }

        private static bool HasK2bDesignerMetadata(string webFormXml)
        {
            if (string.IsNullOrWhiteSpace(webFormXml)) return false;
            try
            {
                var document = XDocument.Parse(webFormXml, LoadOptions.PreserveWhitespace);
                foreach (var grid in document.Descendants().Where(IsGridElement).Where(HasCustomProperties))
                {
                    var properties = ReadProperties(grid);
                    var normalizedKeys = new HashSet<string>(
                        properties.Keys.Select(NormalizePropertyName), StringComparer.OrdinalIgnoreCase);
                    bool hasWhere = normalizedKeys.Contains("controlwhere") || normalizedKeys.Contains("gridwhere");
                    bool hasOrder = normalizedKeys.Contains("controlorder") || normalizedKeys.Contains("gridorder");
                    bool hasBase = normalizedKeys.Contains("basetable") || normalizedKeys.Contains("basetablename");
                    bool hasKey = normalizedKeys.Contains("keyattributes") || normalizedKeys.Contains("keyattribute");
                    bool hasSelection = normalizedKeys.Contains("selectionmode")
                        || normalizedKeys.Contains("selectiontype")
                        || normalizedKeys.Contains("multiselect");
                    bool hasMaxRows = normalizedKeys.Contains("maxrows");
                    if ((hasWhere && hasOrder) || (hasBase && hasKey)
                        || (hasSelection && (hasWhere || hasOrder))
                        || (hasMaxRows && (hasWhere || hasOrder || hasBase)))
                        return true;
                }
            }
            catch { }
            return false;
        }

        private static IReadOnlyDictionary<string, string> ReadProperties(XElement element)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var attribute = element.Attributes().FirstOrDefault(a =>
                a.Name.LocalName.Equals(CustomPropertiesMarker, StringComparison.OrdinalIgnoreCase));
            if (attribute == null) return result;
            string value = DecodeEmbeddedText(attribute.Value);
            XElement root = TryParseEmbeddedXml(value);
            if (root == null) return result;
            foreach (var property in root.DescendantsAndSelf().Where(e =>
                e.Name.LocalName.Equals("Property", StringComparison.OrdinalIgnoreCase)))
            {
                string name = FirstNonEmpty(
                    (string)property.Attribute("Name"),
                    (string)property.Attribute("name"),
                    ChildValue(property, "Name", "name"));
                if (string.IsNullOrWhiteSpace(name)) continue;
                XElement valueElement = property.Elements().FirstOrDefault(e =>
                    e.Name.LocalName.Equals("Value", StringComparison.OrdinalIgnoreCase)
                    || e.Name.LocalName.Equals("Data", StringComparison.OrdinalIgnoreCase));
                string propertyValue = valueElement == null
                    ? FirstNonEmpty((string)property.Attribute("Value"), (string)property.Attribute("value"), property.Value)
                    : (valueElement.HasElements
                        ? valueElement.ToString(SaveOptions.DisableFormatting)
                        : valueElement.Value);
                result[name.Trim()] = DecodeEmbeddedText(propertyValue).Trim();
            }
            return result;
        }

        private static string ChildValue(XElement parent, params string[] names)
        {
            foreach (var name in names)
            {
                var child = parent.Elements().FirstOrDefault(e => e.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (child != null) return child.Value;
            }
            return null;
        }

        private static string ReadProperty(IReadOnlyDictionary<string, string> properties, IEnumerable<string> names)
        {
            foreach (string name in names)
            {
                if (properties.TryGetValue(name, out string value)) return value;
                var match = properties.FirstOrDefault(p => NormalizePropertyName(p.Key).Equals(NormalizePropertyName(name), StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrEmpty(match.Key)) return match.Value;
            }
            return null;
        }

        private static string ReadBaseTable(XElement element, IReadOnlyDictionary<string, string> properties)
        {
            string value = ReadProperty(properties, BaseTablePropertyNames);
            if (!string.IsNullOrWhiteSpace(value))
            {
                XElement embedded = TryParseEmbeddedXml(DecodeEmbeddedText(value));
                if (embedded != null)
                {
                    string name = FirstNonEmpty(
                        (string)embedded.Attribute("name"),
                        (string)embedded.Attribute("Name"),
                        (string)embedded.Attribute("id"),
                        (string)embedded.Attribute("Id"),
                        embedded.Value);
                    if (!string.IsNullOrWhiteSpace(name)) return name;
                }
                return value.Trim();
            }
            return FirstNonEmpty(
                (string)element.Attribute("baseTable"),
                (string)element.Attribute("baseTableName"),
                (string)element.Attribute("table"),
                (string)element.Attribute("tableName"),
                (string)element.Attribute("dataTable"));
        }

        private static IReadOnlyList<JObject> ReadKeyAttributes(XElement element, IReadOnlyDictionary<string, string> properties)
        {
            var result = new List<JObject>();
            string value = ReadProperty(properties, KeyPropertyNames);
            foreach (string token in SplitReferences(value))
            {
                var reference = ParseReference(token);
                result.Add(new JObject
                {
                    ["attribute"] = token,
                    ["id"] = reference.Id,
                    ["kind"] = reference.Kind,
                    ["name"] = reference.Name
                });
            }

            foreach (var child in element.Elements().Where(e =>
                e.Name.LocalName.Equals("item", StringComparison.OrdinalIgnoreCase)
                || e.Name.LocalName.Equals("column", StringComparison.OrdinalIgnoreCase)))
            {
                var column = new GridColumn(child);
                if (column.IsKey && !result.Any(x => string.Equals((string)x["attribute"], column.Attribute, StringComparison.OrdinalIgnoreCase)))
                {
                    var reference = ParseReference(column.Attribute);
                    result.Add(new JObject
                    {
                        ["attribute"] = column.Attribute,
                        ["id"] = reference.Id,
                        ["kind"] = reference.Kind,
                        ["name"] = reference.Name
                    });
                }
            }
            return result;
        }

        private static IReadOnlyList<GridColumn> ReadColumns(XElement element)
        {
            return element.Elements()
                .Where(e => e.Name.LocalName.Equals("item", StringComparison.OrdinalIgnoreCase)
                    || e.Name.LocalName.Equals("column", StringComparison.OrdinalIgnoreCase)
                    || e.Name.LocalName.Equals("field", StringComparison.OrdinalIgnoreCase))
                .Select(e => new GridColumn(e))
                .ToList();
        }

        private static string ReadSelectionMode(XElement element, IReadOnlyDictionary<string, string> properties)
        {
            string value = ReadProperty(properties, SelectionPropertyNames);
            if (string.IsNullOrWhiteSpace(value))
            {
                value = FirstNonEmpty(
                    (string)element.Attribute("selectionMode"),
                    (string)element.Attribute("SelectionMode"),
                    (string)element.Attribute("selection"),
                    (string)element.Attribute("multiSelect"));
            }
            if (string.IsNullOrWhiteSpace(value)) return null;
            if (value.IndexOf("multi", StringComparison.OrdinalIgnoreCase) >= 0
                || IsTrueValue(value) && value.IndexOf("select", StringComparison.OrdinalIgnoreCase) >= 0)
                return "multi";
            if (value.IndexOf("single", StringComparison.OrdinalIgnoreCase) >= 0
                || value.IndexOf("one", StringComparison.OrdinalIgnoreCase) >= 0)
                return "single";
            return value.Trim();
        }

        private static bool? ReadBoolProperty(IReadOnlyDictionary<string, string> properties, params string[] names)
        {
            string value = ReadProperty(properties, names);
            if (string.IsNullOrWhiteSpace(value)) return null;
            if (IsTrueValue(value)) return true;
            if (IsFalseValue(value)) return false;
            return null;
        }

        private static IEnumerable<string> SplitReferences(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return Enumerable.Empty<string>();
            string text = DecodeEmbeddedText(value);
            XElement root = TryParseEmbeddedXml(text);
            if (root != null)
            {
                return root.DescendantsAndSelf()
                    .Select(e => FirstNonEmpty((string)e.Attribute("attribute"), (string)e.Attribute("name"), (string)e.Attribute("Name"), e.Value))
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            return text.Split(new[] { ',', ';', '|', '\r', '\n', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static bool IsTrue(XElement element, params string[] names)
        {
            foreach (string name in names)
            {
                string value = (string)element.Attribute(name);
                if (IsTrueValue(value)) return true;
            }
            return false;
        }

        private static bool IsTrueValue(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            string v = value.Trim();
            return v.Equals("true", StringComparison.OrdinalIgnoreCase)
                || v.Equals("1", StringComparison.Ordinal)
                || v.Equals("yes", StringComparison.OrdinalIgnoreCase)
                || v.Equals("y", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsFalseValue(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            string v = value.Trim();
            return v.Equals("false", StringComparison.OrdinalIgnoreCase)
                || v.Equals("0", StringComparison.Ordinal)
                || v.Equals("no", StringComparison.OrdinalIgnoreCase)
                || v.Equals("n", StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizePropertyName(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            var sb = new StringBuilder();
            foreach (char c in value)
            {
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        private static int CountOccurrences(string value, string marker)
        {
            if (string.IsNullOrEmpty(value) || string.IsNullOrEmpty(marker)) return 0;
            int count = 0;
            int index = 0;
            while ((index = value.IndexOf(marker, index, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                count++;
                index += marker.Length;
            }
            return count;
        }

        private static string DecodeEmbeddedText(string value)
        {
            if (string.IsNullOrEmpty(value)) return value ?? string.Empty;
            string current = value;
            for (int i = 0; i < 3; i++)
            {
                string next = WebUtility.HtmlDecode(current);
                if (string.Equals(next, current, StringComparison.Ordinal)) break;
                current = next;
            }
            return current;
        }

        private static XElement TryParseEmbeddedXml(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            string text = value.Trim();
            try { return XElement.Parse(text, LoadOptions.PreserveWhitespace); }
            catch { }
            try
            {
                if (text.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase))
                    return XDocument.Parse(text, LoadOptions.PreserveWhitespace).Root;
            }
            catch { }
            try
            {
                var document = XDocument.Parse("<K2BEmbedded>" + text + "</K2BEmbedded>", LoadOptions.PreserveWhitespace);
                return document.Root;
            }
            catch { return null; }
        }

        private static (int Line, int Column) GetLineColumn(string source, int offset)
        {
            offset = Math.Max(0, Math.Min(offset, source?.Length ?? 0));
            int line = 1;
            int lastNewLine = -1;
            string text = source ?? string.Empty;
            for (int i = 0; i < offset; i++)
            {
                if (text[i] == '\n')
                {
                    line++;
                    lastNewLine = i;
                }
            }
            return (line, offset - lastNewLine);
        }

        private static string NormalizeLineEndings(string value) =>
            (value ?? string.Empty).Replace("\r\n", "\n").Replace("\r", "\n");

        private static string FirstNonEmpty(params string[] values)
        {
            return values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        }

        private static bool IsLanguageKeyword(string value)
        {
            switch ((value ?? string.Empty).ToLowerInvariant())
            {
                case "if":
                case "for":
                case "foreach":
                case "while":
                case "switch":
                case "catch":
                case "return":
                case "new":
                case "and":
                case "or":
                case "not":
                case "function":
                case "sub":
                case "call":
                case "do":
                    return true;
                default:
                    return false;
            }
        }

        private static Reference ParseReference(string value)
        {
            string text = (value ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(text)) return new Reference(null, null, null);
            int separator = text.IndexOfAny(new[] { ':', '-', '.' });
            if (separator > 0 && separator + 1 < text.Length)
            {
                string kind = text.Substring(0, separator);
                if (kind.Equals("var", StringComparison.OrdinalIgnoreCase)
                    || kind.Equals("att", StringComparison.OrdinalIgnoreCase)
                    || kind.Equals("attribute", StringComparison.OrdinalIgnoreCase)
                    || kind.Equals("variable", StringComparison.OrdinalIgnoreCase))
                {
                    string id = text.Substring(separator + 1).Trim();
                    return new Reference(kind.ToLowerInvariant() == "variable" ? "variable" : (kind.ToLowerInvariant() == "attribute" ? "attribute" : kind.ToLowerInvariant()), id, null);
                }
            }
            return new Reference(null, null, text);
        }

        private sealed class Reference
        {
            internal string Kind { get; private set; }
            internal string Id { get; private set; }
            internal string Name { get; private set; }

            internal Reference(string kind, string id, string name)
            {
                Kind = kind;
                Id = id;
                Name = name;
            }
        }
    }
}
