using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Artech.Architecture.Common.Objects;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// Lossless snapshot and exact verification for a KB object move.
    /// Placement is deliberately excluded from the snapshot: the parent is the only state
    /// the operation is allowed to change. Every persisted object part and the object's
    /// authored property XML must otherwise remain semantically equivalent after removing
    /// placement/bookkeeping fields and normalizing XML ordering.
    /// </summary>
    internal sealed class ObjectMoveSnapshot
    {
        private readonly Dictionary<string, PartSnapshot> _parts;
        private readonly byte[] _objectXml;
        private readonly byte[] _objectRestoreData;

        private ObjectMoveSnapshot(Dictionary<string, PartSnapshot> parts, byte[] objectXml, byte[] objectRestoreData)
        {
            _parts = parts;
            _objectXml = objectXml ?? new byte[0];
            _objectRestoreData = objectRestoreData ?? throw new ArgumentNullException(nameof(objectRestoreData));
            Hash = ComputeAggregateHash(_objectXml, _parts);
        }

        public string Hash { get; }

        public JArray PreservedParts => new JArray(_parts.Values
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(p => p.Name));

        internal JArray DescribeParts(params string[] ignoredPartNames)
        {
            var ignored = new HashSet<string>(ignoredPartNames ?? new string[0], StringComparer.OrdinalIgnoreCase);
            return new JArray(_parts.Values.Where(p => !ignored.Contains(p.Name))
                .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Select(p => new JObject
                {
                    ["part"] = p.Name, ["format"] = p.VerificationFormat,
                    ["restoreFormat"] = p.Format, ["length"] = p.VerificationData.Length,
                    ["hash"] = HashBytes(p.VerificationData)
                }));
        }

        public static ObjectMoveSnapshot Capture(KBObject obj)
        {
            if (obj == null) throw new ArgumentNullException(nameof(obj));

            var parts = new Dictionary<string, PartSnapshot>(StringComparer.OrdinalIgnoreCase);
            foreach (KBObjectPart part in obj.Parts.Cast<KBObjectPart>())
            {
                var captured = PartSnapshot.Capture(part);
                parts[captured.Key] = captured;
            }

            byte[] objectRestoreData = SerializeEntityData(obj);
            if (objectRestoreData == null)
                throw new MissingMethodException(obj.GetType().FullName, "SerializeData");
            return new ObjectMoveSnapshot(parts,
                Utf8(NormalizeObjectXml(obj.SerializeToXml() ?? string.Empty)),
                objectRestoreData);
        }

        public Comparison Compare(KBObject obj, params string[] ignoredPartNames)
            => Compare(obj, null, ignoredPartNames);

        /// <summary>
        /// Issue #238: placement-aware comparison. <paramref name="placementName"/> is the
        /// destination Folder/Module name; generic-property VALUE divergences that exactly
        /// echo it are treated as the SDK's re-parent bookkeeping instead of content
        /// mutation. Null keeps the legacy strict comparison.
        /// </summary>
        public Comparison Compare(KBObject obj, string placementName, params string[] ignoredPartNames)
            => Compare(obj, placementName, null, ignoredPartNames);

        /// <summary>
        /// <paramref name="placementType"/> is the destination container's object type; when
        /// set, the SDK's FolderType rewrite to exactly that type is re-parent bookkeeping.
        /// </summary>
        public Comparison Compare(KBObject obj, string placementName, Guid? placementType, params string[] ignoredPartNames)
        {
            if (obj == null)
                return Comparison.Failed(new[] { "ObjectMissing" }, null);

            ObjectMoveSnapshot current;
            try { current = Capture(obj); }
            catch (Exception ex) { return Comparison.Failed(new[] { "SnapshotReadFailed: " + ex.Message }, null); }

            var changed = new List<string>();
            var changedKeys = new List<string>();
            JArray propertyDifferences = null;
            if (!_objectXml.SequenceEqual(current._objectXml))
            {
                string beforeXml = Encoding.UTF8.GetString(_objectXml);
                string afterXml = Encoding.UTF8.GetString(current._objectXml);
                var propertyPaths = DiffObjectXml(beforeXml, afterXml, placementName, placementType);
                if (propertyPaths.Length > 0)
                {
                    changed.Add("Properties: " + string.Join(", ", propertyPaths));
                    propertyDifferences = DescribePropertyDifferences(beforeXml, afterXml, propertyPaths);
                }
            }

            var ignored = new HashSet<string>(ignoredPartNames ?? new string[0], StringComparer.OrdinalIgnoreCase);
            Func<KeyValuePair<string, PartSnapshot>, bool> include = p => !ignored.Contains(p.Value.Name);
            var expectedFingerprints = _parts.Where(include)
                .ToDictionary(p => p.Key, p => p.Value.VerificationData, StringComparer.OrdinalIgnoreCase);
            var currentFingerprints = current._parts.Where(include)
                .ToDictionary(p => p.Key, p => p.Value.VerificationData, StringComparer.OrdinalIgnoreCase);
            foreach (string key in FindChangedPartKeys(expectedFingerprints, currentFingerprints))
            {
                changedKeys.Add(key);
                PartSnapshot expectedPart;
                PartSnapshot currentPart;
                if (_parts.TryGetValue(key, out expectedPart) && !current._parts.ContainsKey(key))
                    changed.Add(expectedPart.Name + " (missing)");
                else if (current._parts.TryGetValue(key, out currentPart) && !_parts.ContainsKey(key))
                    changed.Add(currentPart.Name + " (unexpected)");
                else
                    changed.Add(expectedPart?.Name ?? currentPart?.Name ?? key);
            }

            return changed.Count == 0
                ? Comparison.Verified(Hash)
                : Comparison.Failed(changed, current.Hash, changedKeys, propertyDifferences);
        }

        public Comparison CompareParts(KBObject obj, params string[] ignoredPartNames)
        {
            if (obj == null)
                return Comparison.Failed(new[] { "ObjectMissing" }, null);

            ObjectMoveSnapshot current;
            try { current = Capture(obj); }
            catch (Exception ex) { return Comparison.Failed(new[] { "SnapshotReadFailed: " + ex.Message }, null); }

            var ignored = new HashSet<string>(ignoredPartNames ?? new string[0], StringComparer.OrdinalIgnoreCase);
            Func<KeyValuePair<string, PartSnapshot>, bool> include = p => !ignored.Contains(p.Value.Name);
            var expectedFingerprints = _parts.Where(include)
                .ToDictionary(p => p.Key, p => p.Value.VerificationData, StringComparer.OrdinalIgnoreCase);
            var currentFingerprints = current._parts.Where(include)
                .ToDictionary(p => p.Key, p => p.Value.VerificationData, StringComparer.OrdinalIgnoreCase);
            string[] changedKeys = FindChangedPartKeys(expectedFingerprints, currentFingerprints);
            var changed = new List<string>();
            foreach (string key in changedKeys)
            {
                PartSnapshot expectedPart;
                PartSnapshot currentPart;
                if (_parts.TryGetValue(key, out expectedPart) && !current._parts.ContainsKey(key))
                    changed.Add(expectedPart.Name + " (missing)");
                else if (current._parts.TryGetValue(key, out currentPart) && !_parts.ContainsKey(key))
                    changed.Add(currentPart.Name + " (unexpected)");
                else
                    changed.Add(expectedPart?.Name ?? currentPart?.Name ?? key);
            }

            return changed.Count == 0
                ? Comparison.Verified(current.Hash)
                : Comparison.Failed(changed, current.Hash, changedKeys);
        }

        /// <summary>
        /// The comparison that decides whether a property rollback put the object back. It is the object comparison of
        /// <see cref="Compare(KBObject, string, Guid?, string[])"/> with the representation noise of the two normalized parts
        /// removed (<see cref="PropertyPartNormalizer"/>). The move, SDT and variable rollbacks keep the strict comparison.
        /// </summary>
        public Comparison ComparePropertyVerification(KBObject obj)
        {
            if (obj == null)
                return Comparison.Failed(new[] { "ObjectMissing" }, null);

            ObjectMoveSnapshot current;
            try { current = Capture(obj); }
            catch (Exception ex) { return Comparison.Failed(new[] { "SnapshotReadFailed: " + ex.Message }, null); }

            var changed = new List<string>();
            JArray propertyDifferences = null;
            if (!_objectXml.SequenceEqual(current._objectXml))
            {
                string beforeXml = Encoding.UTF8.GetString(_objectXml);
                string afterXml = Encoding.UTF8.GetString(current._objectXml);
                var propertyPaths = DiffObjectXml(beforeXml, afterXml, null, null);
                if (propertyPaths.Length > 0)
                {
                    changed.Add("Properties: " + string.Join(", ", propertyPaths));
                    propertyDifferences = DescribePropertyDifferences(beforeXml, afterXml, propertyPaths);
                }
            }

            var expected = _parts.ToDictionary(p => p.Key, p => NormalizedPart(p.Value), StringComparer.OrdinalIgnoreCase);
            var actual = current._parts.ToDictionary(p => p.Key, p => NormalizedPart(p.Value), StringComparer.OrdinalIgnoreCase);
            string[] changedKeys = FindChangedPartKeys(expected, actual);
            var changedLines = new JArray();
            foreach (string key in changedKeys)
            {
                PartSnapshot expectedPart;
                PartSnapshot currentPart;
                string name;
                if (_parts.TryGetValue(key, out expectedPart) && !current._parts.ContainsKey(key))
                    name = expectedPart.Name + " (missing)";
                else if (current._parts.TryGetValue(key, out currentPart) && !_parts.ContainsKey(key))
                    name = currentPart.Name + " (unexpected)";
                else
                    name = expectedPart?.Name ?? currentPart?.Name ?? key;
                changed.Add(name);

                expected.TryGetValue(key, out byte[] expectedBytes);
                actual.TryGetValue(key, out byte[] actualBytes);
                string expectedText = expectedBytes == null ? string.Empty : Encoding.UTF8.GetString(expectedBytes);
                string actualText = actualBytes == null ? string.Empty : Encoding.UTF8.GetString(actualBytes);
                foreach (JToken line in ChangedLines.Describe(name, expectedText, actualText, Math.Max(0, ChangedLines.DefaultMaxLines - changedLines.Count)))
                    changedLines.Add(line);
            }

            if (changed.Count == 0) return Comparison.Verified(current.Hash);
            var failed = Comparison.Failed(changed, current.Hash, changedKeys, propertyDifferences);
            failed.ChangedLines = changedLines;
            return failed;
        }

        private static byte[] NormalizedPart(PartSnapshot part)
            => Utf8(PropertyPartNormalizer.Normalize(part.Name, Encoding.UTF8.GetString(part.VerificationData ?? new byte[0])));

        internal JArray DescribeDifferences(KBObject obj, params string[] ignoredPartNames)
        {
            var result = new JArray();
            if (obj == null) return result;

            ObjectMoveSnapshot current;
            try { current = Capture(obj); }
            catch { return result; }

            var ignored = new HashSet<string>(ignoredPartNames ?? new string[0], StringComparer.OrdinalIgnoreCase);
            foreach (string key in FindChangedPartKeys(
                _parts.Where(p => !ignored.Contains(p.Value.Name)).ToDictionary(p => p.Key, p => p.Value.VerificationData, StringComparer.OrdinalIgnoreCase),
                current._parts.Where(p => !ignored.Contains(p.Value.Name)).ToDictionary(p => p.Key, p => p.Value.VerificationData, StringComparer.OrdinalIgnoreCase)))
            {
                _parts.TryGetValue(key, out var before);
                current._parts.TryGetValue(key, out var after);
                result.Add(new JObject
                {
                    ["part"] = before?.Name ?? after?.Name ?? key,
                    ["beforeFormat"] = before?.VerificationFormat,
                    ["afterFormat"] = after?.VerificationFormat,
                    ["beforeRestoreFormat"] = before?.Format,
                    ["afterRestoreFormat"] = after?.Format,
                    ["beforeLength"] = before?.VerificationData?.Length ?? 0,
                    ["afterLength"] = after?.VerificationData?.Length ?? 0,
                    ["beforeHash"] = before == null ? null : HashBytes(before.VerificationData),
                    ["afterHash"] = after == null ? null : HashBytes(after.VerificationData)
                });
            }
            return result;
        }

        /// <summary>
        /// Compensating restoration used only when the enclosing SDK transaction did not
        /// fully undo a failed move. The normal rollback path is the transaction rollback.
        /// </summary>
        public void RestoreParts(KBObject obj, params string[] ignoredPartNames)
        {
            if (obj == null) throw new ArgumentNullException(nameof(obj));

            var ignored = new HashSet<string>(ignoredPartNames ?? new string[0], StringComparer.OrdinalIgnoreCase);
            var current = obj.Parts.Cast<KBObjectPart>()
                .ToDictionary(PartSnapshot.GetKey, StringComparer.OrdinalIgnoreCase);
            foreach (var expected in _parts.Values)
            {
                if (ignored.Contains(expected.Name)) continue;
                KBObjectPart part;
                if (!current.TryGetValue(expected.Key, out part))
                    throw new InvalidOperationException("Cannot restore missing part '" + expected.Name + "'.");
                expected.Restore(part);
                part.Dirty = true;
                part.Save();
            }
        }

        /// <summary>
        /// Writes the captured text back into every source part, after the binary restore of the parts. That restore does not
        /// bring a source part's text back (a Procedure's Source comes back empty), so the text the snapshot already holds is set
        /// and saved here. Only the property rollback calls this; the move, SDT and variables rollbacks do not. Returns the names
        /// of the source parts whose text could not be written; the others still get theirs.
        /// </summary>
        public IReadOnlyList<string> RestoreSourceTexts(KBObject obj)
        {
            if (obj == null) throw new ArgumentNullException(nameof(obj));

            var current = obj.Parts.Cast<KBObjectPart>()
                .ToDictionary(PartSnapshot.GetKey, StringComparer.OrdinalIgnoreCase);
            var sourceTexts = _parts.Values.Where(p => p.VerificationFormat == "source")
                .Select(p => new KeyValuePair<string, string>(p.Key, Encoding.UTF8.GetString(p.VerificationData)));
            var failedKeys = PropertyRollbackRestore.RestoreSourceTexts(sourceTexts, (key, text) =>
            {
                KBObjectPart part;
                if (!current.TryGetValue(key, out part) || !(part is ISource source))
                    throw new InvalidOperationException("The source part is not in the restored object.");
                source.Source = text;
                part.Dirty = true;
                part.Save();
            });
            return failedKeys.Select(key => _parts[key].Name).ToList();
        }

        public void RestoreObject(KBObject obj)
        {
            if (obj == null) throw new ArgumentNullException(nameof(obj));
            DeserializeEntityData(obj, _objectRestoreData);
        }

        internal static byte[] CaptureEntity(object entity) => SerializeEntityData(entity);

        internal static void RestoreEntity(object entity, byte[] data) => DeserializeEntityData(entity, data);

        internal static string ComputeAggregateHash(byte[] objectXml, IDictionary<string, PartSnapshot> parts)
        {
            using (var sha = SHA256.Create())
            {
                var buffer = new List<byte>();
                buffer.AddRange(objectXml ?? new byte[0]);
                foreach (var part in parts.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
                {
                    buffer.AddRange(Utf8(part.Key));
                    buffer.Add(0);
                    buffer.AddRange(part.Value.VerificationData ?? new byte[0]);
                    buffer.Add(0xff);
                }
                return ToHex(sha.ComputeHash(buffer.ToArray()));
            }
        }

        internal static string[] FindChangedPartKeys(
            IDictionary<string, byte[]> expected,
            IDictionary<string, byte[]> actual)
        {
            expected = expected ?? new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            actual = actual ?? new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            return expected.Keys.Concat(actual.Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(key => !expected.ContainsKey(key)
                    || !actual.ContainsKey(key)
                    || !(expected[key] ?? new byte[0]).SequenceEqual(actual[key] ?? new byte[0]))
                .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value ?? string.Empty);

        // KBObject XML includes placement and save bookkeeping even though the authored
        // property bag is unchanged. Those fields are the expected effect of a move and
        // must not produce a false content-divergence. All other XML remains exact.
        // Issue #238: moving into a Folder can also rewrite an existing generic property
        // VALUE to echo the new placement (e.g. the SDK re-pointing a path-like property
        // at Properties/Property[2]/Value[1]) while the authored parts stay byte-identical.
        // NormalizeObjectXml strips placement-KEYED entries entirely, which cannot bridge a
        // value change on a property whose KEY is authored content. DiffObjectXml therefore
        // additionally ignores the SDK's narrow Folder Property[2] identity rewrite and
        // generic-property VALUE divergences that exactly mirror the requested placement
        // name: these are re-parent bookkeeping, not authored content. Any other divergence
        // — including value changes that do NOT match the placement — still fails the guard.
        internal static string NormalizeObjectXml(string xml)
        {
            if (string.IsNullOrWhiteSpace(xml)) return xml ?? string.Empty;
            try
            {
                var document = XDocument.Parse(xml);
                string[] placementOrBookkeeping =
                {
                    "Parent", "ParentKey", "ParentId", "Folder", "FolderId", "FolderGuid",
                    "Module", "ModuleId", "LastUpdate", "LastModified", "Version", "EntityVersionId"
                };
                Func<string, bool> ignored = name => placementOrBookkeeping.Any(x =>
                    string.Equals(name, x, StringComparison.OrdinalIgnoreCase));

                // GeneXus also serializes header values as generic
                // <Properties><Property><Name>Module</Name><Value>...</Value></Property>.
                // Remove only entries whose property key is known placement/bookkeeping;
                // an authored element merely named <Module> remains protected below.
                foreach (var property in document.Descendants().Where(e =>
                    string.Equals(e.Name.LocalName, "Property", StringComparison.OrdinalIgnoreCase)).ToList())
                {
                    string propertyName = property.Elements().FirstOrDefault(e =>
                        string.Equals(e.Name.LocalName, "Name", StringComparison.OrdinalIgnoreCase))?.Value
                        ?? property.Attributes().FirstOrDefault(a =>
                            string.Equals(a.Name.LocalName, "Name", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(a.Name.LocalName, "Key", StringComparison.OrdinalIgnoreCase))?.Value;
                    if (ignored(propertyName)) property.Remove();
                }

                foreach (var attribute in document.Descendants().Attributes().Where(a => ignored(a.Name.LocalName)).ToList())
                    attribute.Remove();
                foreach (var element in document.Descendants().Where(e => ignored(e.Name.LocalName)
                    && !e.Ancestors().Any(a => string.Equals(a.Name.LocalName, "Properties", StringComparison.OrdinalIgnoreCase))).ToList())
                    element.Remove();
                var flattened = FlattenXml(document.ToString(SaveOptions.DisableFormatting));
                return string.Join("\n", flattened.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(x => x.Key + "=" + x.Value));
            }
            catch { return xml; }
        }

        internal static string[] FindCanonicalDifferencePaths(string expected, string actual)
            => DiffObjectXml(expected, actual, null);

        /// <summary>
        /// Issue #238: placement-aware canonical diff. <paramref name="placementName"/> is
        /// the destination Folder/Module name supplied by the caller; when null the diff is
        /// exactly the legacy canonical comparison.
        /// </summary>
        internal static string[] DiffObjectXml(string expected, string actual, string placementName)
            => DiffObjectXml(expected, actual, placementName, null);

        internal static string[] DiffObjectXml(string expected, string actual, string placementName, Guid? placementType)
        {
            var expectedValues = ParseCanonicalLines(expected);
            var actualValues = ParseCanonicalLines(actual);
            var keys = expectedValues.Keys.Concat(actualValues.Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase);
            var differences = new List<string>();
            foreach (string key in keys)
            {
                expectedValues.TryGetValue(key, out var before);
                actualValues.TryGetValue(key, out var after);
                if (string.Equals(before, after, StringComparison.Ordinal)) continue;
                if (!string.IsNullOrWhiteSpace(placementName)
                    && IsPlacementValueEcho(key, expectedValues, actualValues, placementName))
                    continue;
                if (IsFolderTypeRewrite(key, expectedValues, actualValues, placementType))
                    continue;
                differences.Add(key);
            }
            return differences.OrderBy(key => key, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        /// <summary>
        /// True when the only divergence on a canonical line is a generic property VALUE
        /// that changed to (or from) exactly the placement name. The property key itself
        /// must be present on both sides with an identical name — a key addition/removal
        /// is authored-content territory and never tolerated.
        /// </summary>
        private static bool IsPlacementValueEcho(
            string canonicalKey,
            IDictionary<string, string> beforeValues,
            IDictionary<string, string> afterValues,
            string placementName)
        {
            if (string.IsNullOrWhiteSpace(placementName)) return false;
            if (string.IsNullOrWhiteSpace(canonicalKey)) return false;
            // Only generic <Properties><Property><Name>..</Name><Value>..</Value> entries
            // are eligible; authored elements keep full protection.
            if (canonicalKey.IndexOf("/Property[", StringComparison.OrdinalIgnoreCase) < 0
                || !canonicalKey.EndsWith("]/Value[1]", StringComparison.OrdinalIgnoreCase)
                && !canonicalKey.EndsWith("]/Value", StringComparison.OrdinalIgnoreCase))
                return false;
            bool isPropertiesPath = canonicalKey.StartsWith("Properties/", StringComparison.OrdinalIgnoreCase)
                || canonicalKey.IndexOf("/Properties/", StringComparison.OrdinalIgnoreCase) >= 0
                || canonicalKey.IndexOf("/Properties[", StringComparison.OrdinalIgnoreCase) >= 0;
            if (!isPropertiesPath) return false;

            // The property element must exist on BOTH sides — a key present on only one
            // side (added/removed property) is authored content and never tolerated.
            if (!beforeValues.TryGetValue(canonicalKey, out var before)
                || !afterValues.TryGetValue(canonicalKey, out var after)) return false;

            string namePath = PropertyNamePath(canonicalKey);
            string beforeName = null;
            string afterName = null;
            bool hasStablePropertyName = namePath != null
                && beforeValues.TryGetValue(namePath, out beforeName)
                && afterValues.TryGetValue(namePath, out afterName)
                && string.Equals(beforeName, afterName, StringComparison.Ordinal);
            bool isKnownFolderEchoPath = canonicalKey.IndexOf("/Property[2]/Value[1]", StringComparison.OrdinalIgnoreCase) >= 0;

            // The U10 Folder move surface uses this stable generic-property slot but does not
            // expose a meaningful Name value in every SDK build. Keep the exact slot narrowly
            // covered while still rejecting arbitrary authored properties (for example the
            // Property[1] Description regression below).
            if (!isKnownFolderEchoPath
                && (!hasStablePropertyName || !IsPlacementPropertyName(beforeName)))
                return false;

            string placement = placementName.Trim();
            bool destinationEcho = string.Equals(after, placement, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(before, after, StringComparison.Ordinal);
            bool sdkIdentityRewrite = isKnownFolderEchoPath
                && IsSdkPlacementToken(before)
                && IsSdkPlacementToken(after)
                && !string.Equals(before, after, StringComparison.Ordinal);
            return destinationEcho || sdkIdentityRewrite;
        }

        /// <summary>
        /// Re-parenting a Folder makes the SDK rewrite its generic FolderType property, the
        /// object type of its parent container (Module c88fffcd-... to Folder 00000000-...-0008).
        /// Only that property name is tolerated, only between two type GUIDs, and only when
        /// the new value is exactly the destination container's type.
        /// </summary>
        private static bool IsFolderTypeRewrite(
            string canonicalKey,
            IDictionary<string, string> beforeValues,
            IDictionary<string, string> afterValues,
            Guid? placementType)
        {
            if (!placementType.HasValue || placementType.Value == Guid.Empty) return false;
            if (string.IsNullOrWhiteSpace(canonicalKey)
                || canonicalKey.IndexOf("Property[", StringComparison.OrdinalIgnoreCase) < 0
                || !canonicalKey.EndsWith("]/Value[1]", StringComparison.OrdinalIgnoreCase))
                return false;
            string namePath = PropertyNamePath(canonicalKey);
            if (namePath == null
                || !beforeValues.TryGetValue(namePath, out var beforeName)
                || !afterValues.TryGetValue(namePath, out var afterName)
                || !string.Equals(beforeName, "FolderType", StringComparison.Ordinal)
                || !string.Equals(afterName, "FolderType", StringComparison.Ordinal))
                return false;
            return beforeValues.TryGetValue(canonicalKey, out var before)
                && afterValues.TryGetValue(canonicalKey, out var after)
                && Guid.TryParse((before ?? string.Empty).Trim(), out _)
                && Guid.TryParse((after ?? string.Empty).Trim(), out var afterType)
                && afterType == placementType.Value;
        }

        /// <summary>
        /// Returns the sibling Name path for a generic Property Value path. The caller uses it
        /// to confirm that the same placement property exists on both sides of the diff.
        /// </summary>
        private static string PropertyNamePath(string canonicalKey)
        {
            int valueIndex = canonicalKey.LastIndexOf("/Value", StringComparison.OrdinalIgnoreCase);
            return valueIndex < 0 ? null : canonicalKey.Substring(0, valueIndex) + "/Name[1]";
        }

        private static bool IsPlacementPropertyName(string value)
        {
            string name = (value ?? string.Empty).Trim();
            return new[]
            {
                "Parent", "ParentKey", "ParentId", "ParentPath",
                "Folder", "FolderId", "FolderGuid", "FolderPath", "WebFolder", "WebFolderPath",
                "Module", "ModuleId", "ModuleGuid", "ModulePath", "ObjectFolder"
            }.Any(candidate => string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsSdkPlacementToken(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            if (Guid.TryParse(value.Trim(), out _)) return true;
            return long.TryParse(value.Trim(), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out _);
        }

        /// <summary>
        /// Names each rejected generic-property divergence (its sibling Name value plus the
        /// before/after values) so a refused move reports which property the SDK rewrote
        /// instead of an ordinal path alone.
        /// </summary>
        internal static JArray DescribePropertyDifferences(string expected, string actual, IEnumerable<string> paths)
        {
            var expectedValues = ParseCanonicalLines(expected);
            var actualValues = ParseCanonicalLines(actual);
            var result = new JArray();
            foreach (string path in paths ?? Enumerable.Empty<string>())
            {
                string namePath = PropertyNamePath(path);
                string name = null;
                if (namePath != null && !expectedValues.TryGetValue(namePath, out name))
                    actualValues.TryGetValue(namePath, out name);
                expectedValues.TryGetValue(path, out var before);
                actualValues.TryGetValue(path, out var after);
                result.Add(new JObject
                {
                    ["path"] = path,
                    ["property"] = name,
                    ["before"] = Truncate(before),
                    ["after"] = Truncate(after)
                });
            }
            return result;
        }

        private static string Truncate(string value)
            => value == null || value.Length <= 200 ? value : value.Substring(0, 200) + "...";

        private static Dictionary<string, string> ParseCanonicalLines(string value)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in (value ?? string.Empty).Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int separator = line.IndexOf('=');
                if (separator < 0) result[line] = string.Empty;
                else result[line.Substring(0, separator)] = line.Substring(separator + 1);
            }
            return result;
        }

        private static Dictionary<string, string> FlattenXml(string xml)
        {
            var document = XDocument.Parse(xml ?? string.Empty);
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var element in document.Descendants())
            {
                string path = string.Join("/", element.AncestorsAndSelf().Reverse().Select(XmlPathSegment));
                if (!element.HasElements) values[path] = element.Value;
                foreach (var attribute in element.Attributes()) values[path + "/@" + attribute.Name.LocalName] = attribute.Value;
            }
            return values;
        }

        private static string XmlPathSegment(XElement element)
        {
            if (element.Parent == null) return element.Name.LocalName;
            int ordinal = element.Parent.Elements(element.Name).TakeWhile(x => x != element).Count() + 1;
            return element.Name.LocalName + "[" + ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture) + "]";
        }

        private static string ToHex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        private static string HashBytes(byte[] bytes)
        {
            using (var sha = SHA256.Create()) return ToHex(sha.ComputeHash(bytes ?? new byte[0]));
        }

        private static byte[] SerializeEntityData(object entity)
        {
            var method = FindEntityMethod(entity.GetType(), "SerializeData", Type.EmptyTypes);
            if (method == null) return null;
            try { return method.Invoke(entity, null) as byte[]; }
            catch (TargetInvocationException ex) { throw ex.InnerException ?? ex; }
        }

        private static void DeserializeEntityData(object entity, byte[] data)
        {
            var method = FindEntityMethod(entity.GetType(), "DeserializeData", new[] { typeof(byte[]) });
            if (method == null) throw new MissingMethodException(entity.GetType().FullName, "DeserializeData(byte[])");
            try { method.Invoke(entity, new object[] { data }); }
            catch (TargetInvocationException ex) { throw ex.InnerException ?? ex; }
        }

        private static MethodInfo FindEntityMethod(Type type, string name, Type[] parameterTypes)
        {
            while (type != null)
            {
                var method = type.GetMethod(name,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                    null, parameterTypes, null);
                if (method != null) return method;
                type = type.BaseType;
            }
            return null;
        }

        internal sealed class Comparison
        {
            private Comparison(bool equal, IEnumerable<string> changedParts, string persistedHash, IEnumerable<string> changedPartKeys = null, JArray propertyDifferences = null)
            {
                Equal = equal;
                ChangedParts = new JArray(changedParts ?? Enumerable.Empty<string>());
                PersistedHash = persistedHash;
                ChangedPartKeys = (changedPartKeys ?? Enumerable.Empty<string>()).ToArray();
                PropertyDifferences = propertyDifferences ?? new JArray();
            }

            public bool Equal { get; }
            public JArray ChangedParts { get; }
            public string PersistedHash { get; }
            public string[] ChangedPartKeys { get; }
            public JArray PropertyDifferences { get; }

            /// <summary>The differing lines of the changed parts, set only by <see cref="ObjectMoveSnapshot.ComparePropertyVerification"/>.</summary>
            public JArray ChangedLines { get; set; } = new JArray();

            public static Comparison Verified(string hash) => new Comparison(true, null, hash);
            public static Comparison Failed(IEnumerable<string> changedParts, string hash, IEnumerable<string> changedPartKeys = null, JArray propertyDifferences = null) => new Comparison(false, changedParts, hash, changedPartKeys, propertyDifferences);
        }

        internal sealed class PartSnapshot
        {
            private PartSnapshot(string key, string name, string format, byte[] restoreData, byte[] verificationData, string verificationFormat)
            {
                Key = key;
                Name = name;
                Format = format;
                RestoreData = restoreData ?? new byte[0];
                VerificationData = verificationData ?? new byte[0];
                VerificationFormat = verificationFormat;
            }

            public string Key { get; }
            public string Name { get; }
            public string Format { get; }
            public byte[] RestoreData { get; }
            public byte[] VerificationData { get; }
            public string VerificationFormat { get; }

            public static PartSnapshot Capture(KBObjectPart part)
                => Capture(GetKey(part), part.TypeDescriptor?.Name ?? part.GetType().Name, part);

            internal static PartSnapshot Capture(string key, string name, object part)
            {
                var source = part as ISource;
                // SourcePart.SerializeData consumes its cached Source; reading first
                // materializes persisted tokens instead of serializing an unloaded null.
                byte[] text = source == null ? null : Utf8(source.Source ?? string.Empty);
                byte[] native = SerializeEntityData(part);
                if (source != null)
                {
                    return new PartSnapshot(key, name, native != null ? "binary" : "source", native ?? text, text, "source");
                }

                byte[] xml = Utf8(((KBObjectPart)part).SerializeToXml() ?? string.Empty);
                return new PartSnapshot(key, name, native != null ? "binary" : "xml", native ?? xml, xml, "xml");
            }

            public static string GetKey(KBObjectPart part)
            {
                try { return part.Type.ToString("D"); }
                catch { return part.TypeDescriptor?.Name ?? part.GetType().FullName; }
            }

            public void Restore(KBObjectPart part)
            {
                if (Format == "binary")
                {
                    DeserializeEntityData(part, RestoreData);
                    return;
                }
                if (Format == "source")
                {
                    var source = part as ISource;
                    if (source == null) throw new InvalidOperationException("Part '" + Name + "' no longer implements ISource.");
                    source.Source = Encoding.UTF8.GetString(RestoreData);
                    return;
                }
                if (Format == "xml")
                {
                    part.DeserializeFromXml(Encoding.UTF8.GetString(RestoreData));
                    return;
                }
                throw new InvalidOperationException("Unknown snapshot format '" + Format + "'.");
            }

            private static byte[] SerializeEntityData(object entity)
            {
                return ObjectMoveSnapshot.SerializeEntityData(entity);
            }

            private static void DeserializeEntityData(object entity, byte[] data)
            {
                ObjectMoveSnapshot.DeserializeEntityData(entity, data);
            }
        }
    }
}
