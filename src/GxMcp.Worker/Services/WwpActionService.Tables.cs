using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Artech.Architecture.Common.Objects;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    public sealed partial class WwpActionService
    {
        private static readonly HashSet<string> TableTypeOperations = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "set_table_type"
        };

        private static readonly Regex TablePathTokenPattern = new Regex(
            @"(?<name>[A-Za-z_][A-Za-z0-9_.-]*)(?:\s*\[\s*(?<index>\d+)\s*\])?",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static bool IsTableTypeOperation(string operation) =>
            TableTypeOperations.Contains(operation ?? string.Empty);

        private string RunTableTypeOperation(string target, KBObject requestedObject, KBObject instance,
            KBObjectPart instancePart, string xml, JObject args)
        {
            string tablePath = args?["tablePath"]?.ToString();
            if (!TryNormalizeTableType(args, out string tableType, out string validationError))
                return McpResponse.Err(code: "InvalidTableType", message: validationError, target: target);
            if (string.IsNullOrWhiteSpace(tablePath))
                return McpResponse.Err(code: "MissingTablePath",
                    message: "tablePath is required and must identify one existing table.", target: target);

            XDocument beforeDocument = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
            XDocument previewDocument = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
            JObject preview = ApplyTableTypeXml(previewDocument, tablePath, tableType);
            if (preview["error"] != null)
                return McpResponse.Err(code: preview["code"]?.ToString() ?? "WwpTableTypeInvalid",
                    message: preview["error"].ToString(), target: target, extra: preview);

            XElement beforeTable = FindXmlTableOrNull(beforeDocument, tablePath);
            XElement previewTable = FindXmlTableOrNull(previewDocument, tablePath);
            JObject typedDiff = BuildTableTypeDiff(tablePath, beforeTable, previewTable, tableType);
            string versionToken = WriteService.ComputeContentVersionToken(instance, xml);
            if (args?["dryRun"]?.ToObject<bool?>() == true)
                return McpResponse.Ok(target: target, code: "WwpTableTypeDryRun", result: new JObject
                {
                    ["instance"] = instance.Name,
                    ["typedDiff"] = typedDiff,
                    ["mutationMode"] = "native-pattern-sdk",
                    ["sdkOperation"] = "PatternInstance.RootElement + semantic type attribute",
                    ["preserves"] = new JArray("defaultType", "childrenOrderedList", "children", "bindings", "events", "controls", "names", "theme classes"),
                    ["versionToken"] = versionToken,
                    ["persisted"] = false,
                    ["patternReReadConfirmed"] = false,
                    ["webFormProjectionConfirmed"] = false,
                    ["lifecycleExecuted"] = false
                });

            if (!preview["changed"].ToObject<bool>())
                return McpResponse.Ok(target: target, code: "WwpTableTypeNoChange", result: new JObject
                {
                    ["instance"] = instance.Name,
                    ["typedDiff"] = typedDiff,
                    ["versionToken"] = versionToken,
                    ["persisted"] = false,
                    ["saved"] = false,
                    ["unchanged"] = true,
                    ["lifecycleExecuted"] = false
                });

            lock (WriteService.AcquirePerTargetLock(target))
            {
                KBObject lockedTarget = _objects.FindObject(target) ?? requestedObject;
                string currentXml = _patterns.ReadPatternPartXml(lockedTarget, "PatternInstance", PatternRegistry.WorkWithPlusPatternId,
                    out KBObject currentInstance, out _);
                if (currentInstance == null || string.IsNullOrWhiteSpace(currentXml))
                    return BuildWwpInstanceNotResolvable(target);
                // The part is resolved from the instance that was read (issue #638): resolving it from
                // the requested host yields another copy of the WorkWithPlus object, and saving one copy
                // with the other's part persists nothing.
                _patterns.BuildPatternPartEnvelope(currentInstance, "PatternInstance", currentXml, PatternRegistry.WorkWithPlusPatternId,
                    out currentInstance, out KBObjectPart currentPart);
                if (currentInstance == null || currentPart == null || string.IsNullOrWhiteSpace(currentXml))
                    return BuildWwpInstanceNotResolvable(target);

                string expectedVersion = args?["baseVersion"]?.ToString()
                    ?? args?["expectedVersion"]?.ToString()
                    ?? args?["versionToken"]?.ToString();
                string currentVersion = WriteService.ComputeContentVersionToken(currentInstance, currentXml);
                if (!string.IsNullOrWhiteSpace(expectedVersion)
                    && !string.Equals(expectedVersion, currentVersion, StringComparison.Ordinal))
                    return BuildWwpStaleObject(target, expectedVersion, currentVersion,
                        "The WorkWithPlus PatternInstance changed after the caller's read/dry-run; no table type mutation was applied.");

                XDocument lockedBeforeDocument = XDocument.Parse(currentXml, LoadOptions.PreserveWhitespace);
                XDocument lockedPreviewDocument = XDocument.Parse(currentXml, LoadOptions.PreserveWhitespace);
                JObject lockedMutation = ApplyTableTypeXml(lockedPreviewDocument, tablePath, tableType);
                if (lockedMutation["error"] != null)
                    return McpResponse.Err(code: lockedMutation["code"]?.ToString() ?? "WwpTableTypeInvalid",
                        message: lockedMutation["error"].ToString(), target: target, extra: lockedMutation);
                XElement lockedBeforeTable = FindXmlTableOrNull(lockedBeforeDocument, tablePath);
                XElement lockedPreviewTable = FindXmlTableOrNull(lockedPreviewDocument, tablePath);
                JObject lockedDiff = BuildTableTypeDiff(tablePath, lockedBeforeTable, lockedPreviewTable, tableType);
                if (!lockedMutation["changed"].ToObject<bool>())
                    return McpResponse.Ok(target: target, code: "WwpTableTypeNoChange", result: new JObject
                    {
                        ["instance"] = currentInstance.Name,
                        ["typedDiff"] = lockedDiff,
                        ["versionToken"] = currentVersion,
                        ["persisted"] = false,
                        ["saved"] = false,
                        ["unchanged"] = true,
                        ["lifecycleExecuted"] = false
                    });

                KBObject parent = WwpProjectionHelper.ResolveHostParent(currentInstance, _objects, currentXml);
                string parentWebFormBefore = ReadPart(parent, "WebForm");
                byte[] nativeBytes = ReadPartBytes(currentPart);
                SnapshotBundle snapshots = CaptureSnapshots(currentInstance, currentXml, parent, parentWebFormBefore);
                string applyOnSaveBefore = ReadObjectProperty(currentInstance, "SDPlus_Editor_Apply_On_Save");
                if (WwpSnapshotsIncomplete(nativeBytes, parent, parentWebFormBefore, snapshots))
                    return McpResponse.Err(code: "WwpSnapshotRequired",
                        message: "Exact PatternInstance/WebForm snapshots could not be captured; no table type mutation was applied.",
                        target: target, extra: new JObject
                        {
                            ["snapshot"] = snapshots.ToJson(),
                            ["nativeBytesAvailable"] = nativeBytes != null,
                            ["parentResolved"] = parent != null,
                            ["parentWebFormAvailable"] = parentWebFormBefore != null,
                            ["persisted"] = false
                        });

                object root = GetProperty(currentPart, "RootElement");
                if (!TryFindNativeTable(root, tablePath, out object nativeTable, out string nativePathError))
                    return McpResponse.Err(code: "WwpTableNotFound", message: nativePathError, target: target,
                        extra: new JObject { ["tablePath"] = tablePath, ["persisted"] = false });

                string nativeBeforeType = NativeAttributeOrXml(nativeTable, "type");
                string nativeBeforeDefaultType = NativeAttributeOrXml(nativeTable, "defaultType");
                string nativeBeforeChildren = NativeAttributeOrXml(nativeTable, "childrenOrderedList");
                string xmlBeforeType = Attr(lockedBeforeTable, "type");
                if (!string.Equals(nativeBeforeType, xmlBeforeType, StringComparison.OrdinalIgnoreCase))
                    return McpResponse.Err(code: "WwpTableIdentityMismatch",
                        message: "The native table identity resolved to a different type than the persisted PatternInstance path; no mutation was applied.",
                        target: target, extra: new JObject
                        {
                            ["tablePath"] = tablePath,
                            ["xmlType"] = xmlBeforeType,
                            ["nativeType"] = nativeBeforeType,
                            ["persisted"] = false
                        });

                try
                {
                    MethodInfo executeUpdate = currentPart.GetType().GetMethod("ExecuteUpdate",
                        BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(string), typeof(Action) }, null);
                    Action mutation = () => SetNativeAttribute(nativeTable, "type", tableType);
                    if (executeUpdate != null)
                        executeUpdate.Invoke(currentPart, new object[] { "genexus_wwp set_table_type", mutation });
                    else
                        mutation();

                    if (!string.Equals(NativeAttributeOrXml(nativeTable, "type"), tableType, StringComparison.OrdinalIgnoreCase))
                        throw new WwpTabException("WwpTableTypeRejected", "The WorkWithPlus SDK did not accept the requested table type.");
                    if (!string.Equals(NativeAttributeOrXml(nativeTable, "defaultType"), nativeBeforeDefaultType, StringComparison.Ordinal))
                        throw new WwpTabException("WwpTableMetadataChanged", "The native table type mutation changed defaultType.");
                    if (!string.Equals(NativeAttributeOrXml(nativeTable, "childrenOrderedList"), nativeBeforeChildren, StringComparison.Ordinal))
                        throw new WwpTabException("WwpTableMetadataChanged", "The native table type mutation changed childrenOrderedList.");

                    SaveNativePattern(currentInstance, currentPart);
                    bool applyOnSaveReenabled = WwpApplyOnSaveHelper.TryEnable(currentInstance);
                    string persistedXml = _patterns.ReadPatternPartXml(currentInstance, "PatternInstance", PatternRegistry.WorkWithPlusPatternId,
                        out KBObject persistedInstance, out _);
                    if (string.IsNullOrWhiteSpace(persistedXml))
                        throw new WwpTabException("WwpTableTypeNotPersisted", "The SDK save completed, but the PatternInstance could not be re-read.");

                    XDocument persistedDocument = XDocument.Parse(persistedXml, LoadOptions.PreserveWhitespace);
                    if (!VerifyOnlyTableTypeChanged(lockedBeforeDocument, persistedDocument, tablePath, tableType,
                        out string verificationError))
                        throw new WwpTabException("WwpTableTypeNotPersisted", verificationError);

                    string applyOnSaveAfter = ReadObjectProperty(persistedInstance ?? currentInstance,
                        "SDPlus_Editor_Apply_On_Save");
                    if (IsFalse(applyOnSaveAfter))
                        throw new WwpTabException("WwpApplyOnSaveDisabled",
                            "SDPlus_Editor_Apply_On_Save became False after the native table type save.");

                    bool projected = WwpProjectionHelper.TryProjectHostOntoParent(parent,
                        persistedInstance ?? currentInstance);
                    if (!projected)
                        throw new WwpTabException("WwpProjectionFailed",
                            "The PatternInstance persisted, but the WorkWithPlus SDK did not project the parent WebForm.");
                    string projectedWebForm = ReadPart(parent, "WebForm");
                    if (string.IsNullOrWhiteSpace(projectedWebForm))
                        throw new WwpTabException("WwpProjectionNotConfirmed",
                            "The projected parent WebForm could not be re-read.");

                    WriteService.NotePerTargetWrite(target);
                    XElement persistedTable = FindXmlTableOrNull(persistedDocument, tablePath);
                    return McpResponse.Ok(target: target, code: "WwpTableTypeUpdated", result: new JObject
                    {
                        ["instance"] = persistedInstance?.Name ?? currentInstance.Name,
                        ["parent"] = parent.Name,
                        ["operation"] = "set_table_type",
                        ["tablePath"] = tablePath,
                        ["tableType"] = tableType,
                        ["typedDiff"] = BuildTableTypeDiff(tablePath, lockedBeforeTable, persistedTable, tableType),
                        ["mutationMode"] = "native-pattern-sdk",
                        ["sdkOperation"] = executeUpdate == null
                            ? "PatternInstance.RootElement + semantic type attribute"
                            : "PatternInstance.ExecuteUpdate + semantic type attribute",
                        ["defaultTypePreserved"] = string.Equals(Attr(lockedBeforeTable, "defaultType"), Attr(persistedTable, "defaultType"), StringComparison.Ordinal),
                        ["childrenOrderedListPreserved"] = string.Equals(Attr(lockedBeforeTable, "childrenOrderedList"), Attr(persistedTable, "childrenOrderedList"), StringComparison.Ordinal),
                        ["patternReReadConfirmed"] = true,
                        ["webFormProjectionConfirmed"] = true,
                        ["versionToken"] = WriteService.ComputeContentVersionToken(persistedInstance ?? currentInstance, persistedXml),
                        ["persisted"] = true,
                        ["saved"] = true,
                        ["applyOnSaveBefore"] = applyOnSaveBefore,
                        ["applyOnSaveAfter"] = applyOnSaveAfter,
                        ["applyOnSaveReenabled"] = applyOnSaveReenabled,
                        ["snapshot"] = snapshots.ToJson(),
                        ["rollbackPerformed"] = false,
                        ["lifecycleExecuted"] = false,
                        ["specified"] = false,
                        ["generated"] = false,
                        ["built"] = false
                    });
                }
                catch (Exception ex)
                {
                    WwpTabException typed = ex as WwpTabException;
                    JObject rollback = RestoreSnapshots(currentInstance, currentPart, nativeBytes,
                        currentXml, parent, parentWebFormBefore, applyOnSaveBefore);
                    ExceptionRoot.Log("[WWP-TABLE] failed", ex);
                    return McpResponse.Err(code: typed?.Code ?? "WwpTableTypeFailed", message: ExceptionRoot.Message(ex),
                        target: target, extra: new JObject
                        {
                            ["exceptionType"] = ExceptionRoot.Unwrap(ex)?.GetType().Name,
                            ["failureTrace"] = ExceptionRoot.FailureTrace(ex),
                            ["persisted"] = false,
                            ["patternReReadConfirmed"] = false,
                            ["webFormProjectionConfirmed"] = false,
                            ["snapshot"] = snapshots.ToJson(),
                            ["rollback"] = rollback,
                            ["lifecycleExecuted"] = false
                        });
                }
            }
        }

        internal static JObject ApplyTableTypeXml(XDocument document, string tablePath, string tableType)
        {
            if (!TryNormalizeTableType(tableType, out string normalizedType, out string typeError))
                return TableError("InvalidTableType", typeError);
            XElement table = FindXmlTableOrNull(document, tablePath, out string pathError);
            if (table == null)
                return TableError("WwpTableNotFound", pathError);

            string before = Attr(table, "type");
            bool changed = !string.Equals(before, normalizedType, StringComparison.OrdinalIgnoreCase);
            if (changed) table.SetAttributeValue("type", normalizedType);
            return new JObject
            {
                ["changed"] = changed,
                ["tablePath"] = tablePath,
                ["tableType"] = normalizedType,
                ["before"] = before,
                ["after"] = normalizedType
            };
        }

        internal static bool VerifyOnlyTableTypeChanged(XDocument before, XDocument after, string tablePath,
            string expectedType, out string error)
        {
            error = null;
            XElement beforeTable = FindXmlTableOrNull(before, tablePath, out string beforeError);
            XElement afterTable = FindXmlTableOrNull(after, tablePath, out string afterError);
            if (beforeTable == null || afterTable == null)
            {
                error = beforeError ?? afterError ?? "The requested table path was not found after the PatternInstance save.";
                return false;
            }
            if (!string.Equals(Attr(afterTable, "type"), expectedType, StringComparison.OrdinalIgnoreCase))
            {
                error = "The persisted table type does not match the requested value.";
                return false;
            }

            XDocument beforeComparable = CloneDocumentWithoutTableType(before, tablePath, out error);
            if (beforeComparable == null) return false;
            XDocument afterComparable = CloneDocumentWithoutTableType(after, tablePath, out error);
            if (afterComparable == null) return false;
            if (!XNode.DeepEquals(beforeComparable, afterComparable))
            {
                error = "The persisted PatternInstance differs beyond the requested table @type; children, bindings, events, or metadata may have changed.";
                return false;
            }
            return true;
        }

        private static XDocument CloneDocumentWithoutTableType(XDocument source, string tablePath, out string error)
        {
            error = null;
            if (source == null)
            {
                error = "The PatternInstance document is unavailable.";
                return null;
            }
            XDocument clone = XDocument.Parse(source.ToString(SaveOptions.DisableFormatting), LoadOptions.PreserveWhitespace);
            XElement table = FindXmlTableOrNull(clone, tablePath, out error);
            if (table == null) return null;
            XAttribute type = table.Attributes().FirstOrDefault(a =>
                a.Name.LocalName.Equals("type", StringComparison.OrdinalIgnoreCase));
            type?.Remove();
            return clone;
        }

        private static JObject BuildTableTypeDiff(string tablePath, XElement before, XElement after, string tableType)
        {
            return new JObject
            {
                ["operation"] = "set_table_type",
                ["tablePath"] = tablePath,
                ["tableType"] = tableType,
                ["before"] = before?.ToString(SaveOptions.DisableFormatting),
                ["after"] = after?.ToString(SaveOptions.DisableFormatting)
            };
        }

        private static bool TryNormalizeTableType(JObject args, out string tableType, out string error)
        {
            return TryNormalizeTableType(args?["tableType"]?.ToString() ?? args?["type"]?.ToString(),
                out tableType, out error);
        }

        private static bool TryNormalizeTableType(string value, out string tableType, out string error)
        {
            tableType = null;
            error = null;
            if (string.Equals(value?.Trim(), "Regular", StringComparison.OrdinalIgnoreCase))
            {
                tableType = "Regular";
                return true;
            }
            if (string.Equals(value?.Trim(), "Responsive", StringComparison.OrdinalIgnoreCase))
            {
                tableType = "Responsive";
                return true;
            }
            error = "tableType is required and must be Regular or Responsive.";
            return false;
        }

        private static List<TablePathSegment> ParseTablePath(string path)
        {
            var result = new List<TablePathSegment>();
            if (string.IsNullOrWhiteSpace(path)) return result;
            string normalized = Regex.Replace(path, @"(?<name>[A-Za-z_][A-Za-z0-9_.-]*)\s*/\s*\[\s*(?<index>\d+)\s*\]",
                match => match.Groups["name"].Value + "[" + match.Groups["index"].Value + "]");
            if (normalized.IndexOf('>') >= 0)
            {
                foreach (string part in normalized.Split('>'))
                {
                    Match match = TablePathTokenPattern.Match(part.Trim());
                    if (match.Success && match.Index == 0)
                        result.Add(ParseTablePathSegment(match));
                }
                return result;
            }
            foreach (Match match in TablePathTokenPattern.Matches(normalized))
                result.Add(ParseTablePathSegment(match));
            return result;
        }

        private static TablePathSegment ParseTablePathSegment(Match match) => new TablePathSegment
        {
            Name = match.Groups["name"].Value,
            Index = match.Groups["index"].Success ? (int?)int.Parse(match.Groups["index"].Value) : null
        };

        private static bool TryFindNativeTable(object root, string path, out object table, out string error)
        {
            table = null;
            error = null;
            List<TablePathSegment> segments = ParseTablePath(path);
            if (root == null) { error = "PatternInstance RootElement is unavailable."; return false; }
            if (segments.Count == 0) { error = "tablePath does not contain a valid table breadcrumb."; return false; }

            object cursor = root;
            int position = 0;
            if (MatchesNativeSegment(cursor, segments[0]) || IsSyntheticInstanceSegment(cursor, segments[0]))
                position++;
            else
            {
                List<object> direct = NativeChildren(root)
                    .Where(candidate => MatchesNativeSegment(candidate, segments[0])).ToList();
                if (direct.Count > 0)
                {
                    if (!TrySelect(direct, segments[0], out cursor, out error)) return false;
                    position++;
                }
                else if (!segments[0].IsTypeSelector)
                {
                    List<object> named = Walk(root).Where(candidate => IsNativeTable(candidate)
                        && string.Equals(NativeAttribute(candidate, "name"), segments[0].Name, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (!TrySelect(named, segments[0], out cursor, out error)) return false;
                    position++;
                }
                else
                {
                    error = "No native PatternInstance element matched tablePath segment '" + segments[0].Name + "'.";
                    return false;
                }
            }

            while (position < segments.Count)
            {
                List<object> candidates = NativeChildren(cursor)
                    .Where(candidate => MatchesNativeSegment(candidate, segments[position])).ToList();
                if (!TrySelect(candidates, segments[position], out cursor, out error)) return false;
                position++;
            }
            if (!IsNativeTable(cursor))
            {
                error = "tablePath resolved to a non-table PatternInstance element.";
                return false;
            }
            table = cursor;
            return true;
        }

        private static bool TrySelect(List<object> candidates, TablePathSegment segment, out object selected, out string error)
        {
            selected = null;
            error = null;
            if (segment.Index.HasValue)
            {
                if (segment.Index.Value < 0 || segment.Index.Value >= candidates.Count)
                {
                    error = "tablePath index " + segment.Index.Value + " is outside the matching native children.";
                    return false;
                }
                selected = candidates[segment.Index.Value];
                return true;
            }
            if (candidates.Count == 0)
            {
                error = "No native PatternInstance element matched tablePath segment '" + segment.Name + "'.";
                return false;
            }
            if (candidates.Count > 1)
            {
                error = "tablePath segment '" + segment.Name + "' is ambiguous; include the table name or an index.";
                return false;
            }
            selected = candidates[0];
            return true;
        }

        private static bool MatchesNativeSegment(object element, TablePathSegment segment)
        {
            if (segment.IsTypeSelector) return IsNativeTable(element);
            return string.Equals(NativeAttribute(element, "name"), segment.Name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(NativeType(element), segment.Name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(NativeElementXml(element)?.Name.LocalName, segment.Name, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsSyntheticInstanceSegment(object root, TablePathSegment segment) =>
            segment.Name.Equals("instance", StringComparison.OrdinalIgnoreCase)
            && NativeElementXml(root)?.Name.LocalName.Equals("instance", StringComparison.OrdinalIgnoreCase) != true;

        private static XElement FindXmlTableOrNull(XDocument document, string path)
        {
            return FindXmlTableOrNull(document, path, out _);
        }

        private static XElement FindXmlTableOrNull(XDocument document, string path, out string error)
        {
            error = null;
            List<TablePathSegment> segments = ParseTablePath(path);
            if (document?.Root == null) { error = "PatternInstance XML is unavailable."; return null; }
            if (segments.Count == 0) { error = "tablePath does not contain a valid table breadcrumb."; return null; }

            XElement cursor = document.Root;
            int position = 0;
            if (MatchesXmlSegment(cursor, segments[0]) || IsSyntheticXmlInstanceSegment(cursor, segments[0]))
                position++;
            else
            {
                List<XElement> direct = cursor.Elements()
                    .Where(candidate => MatchesXmlSegment(candidate, segments[0])).ToList();
                if (direct.Count > 0)
                {
                    if (!TrySelect(direct, segments[0], out cursor, out error)) return null;
                    position++;
                }
                else if (!segments[0].IsTypeSelector)
                {
                    List<XElement> named = document.Descendants().Where(candidate => IsXmlTable(candidate)
                        && string.Equals(Attr(candidate, "name"), segments[0].Name, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (!TrySelect(named, segments[0], out cursor, out error)) return null;
                    position++;
                }
                else
                {
                    error = "No PatternInstance XML element matched tablePath segment '" + segments[0].Name + "'.";
                    return null;
                }
            }

            while (position < segments.Count)
            {
                List<XElement> candidates = cursor.Elements()
                    .Where(candidate => MatchesXmlSegment(candidate, segments[position])).ToList();
                if (!TrySelect(candidates, segments[position], out cursor, out error)) return null;
                position++;
            }
            if (!IsXmlTable(cursor))
            {
                error = "tablePath resolved to a non-table PatternInstance element.";
                return null;
            }
            return cursor;
        }

        private static bool TrySelect(List<XElement> candidates, TablePathSegment segment, out XElement selected, out string error)
        {
            selected = null;
            error = null;
            if (segment.Index.HasValue)
            {
                if (segment.Index.Value < 0 || segment.Index.Value >= candidates.Count)
                {
                    error = "tablePath index " + segment.Index.Value + " is outside the matching XML children.";
                    return false;
                }
                selected = candidates[segment.Index.Value];
                return true;
            }
            if (candidates.Count == 0)
            {
                error = "No PatternInstance XML element matched tablePath segment '" + segment.Name + "'.";
                return false;
            }
            if (candidates.Count > 1)
            {
                error = "tablePath segment '" + segment.Name + "' is ambiguous; include the table name or an index.";
                return false;
            }
            selected = candidates[0];
            return true;
        }

        private static bool MatchesXmlSegment(XElement element, TablePathSegment segment)
        {
            if (segment.IsTypeSelector) return IsXmlTable(element);
            return string.Equals(Attr(element, "name"), segment.Name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(element.Name.LocalName, segment.Name, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsSyntheticXmlInstanceSegment(XElement root, TablePathSegment segment) =>
            segment.Name.Equals("instance", StringComparison.OrdinalIgnoreCase)
            && !root.Name.LocalName.Equals("instance", StringComparison.OrdinalIgnoreCase);

        private static bool IsXmlTable(XElement element) =>
            element != null && element.Name.LocalName.Equals("table", StringComparison.OrdinalIgnoreCase);

        private static string NativeAttributeOrXml(object element, string name)
        {
            string value = NativeAttribute(element, name);
            if (value != null) return value;
            XElement xml = NativeElementXml(element);
            return xml == null ? null : Attr(xml, name);
        }

        private static JObject TableError(string code, string message) => new JObject
        {
            ["code"] = code,
            ["error"] = message
        };

        private sealed class TablePathSegment
        {
            internal string Name;
            internal int? Index;
            internal bool IsTypeSelector => Name.Equals("table", StringComparison.OrdinalIgnoreCase)
                || Name.Equals("WPTable", StringComparison.OrdinalIgnoreCase);
        }
    }
}
