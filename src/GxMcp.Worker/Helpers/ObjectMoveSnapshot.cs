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
        {
            if (obj == null)
                return Comparison.Failed(new[] { "ObjectMissing" }, null);

            ObjectMoveSnapshot current;
            try { current = Capture(obj); }
            catch (Exception ex) { return Comparison.Failed(new[] { "SnapshotReadFailed: " + ex.Message }, null); }

            var changed = new List<string>();
            var changedKeys = new List<string>();
            if (!_objectXml.SequenceEqual(current._objectXml))
            {
                var propertyPaths = FindCanonicalDifferencePaths(
                    Encoding.UTF8.GetString(_objectXml), Encoding.UTF8.GetString(current._objectXml));
                changed.Add(propertyPaths.Length == 0 ? "Properties" : "Properties: " + string.Join(", ", propertyPaths));
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
                ? Comparison.Verified(current.Hash)
                : Comparison.Failed(changed, current.Hash, changedKeys);
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
                    ["beforeFormat"] = before?.Format,
                    ["afterFormat"] = after?.Format,
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
        {
            var expectedValues = ParseCanonicalLines(expected);
            var actualValues = ParseCanonicalLines(actual);
            return expectedValues.Keys.Concat(actualValues.Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(key => !expectedValues.ContainsKey(key) || !actualValues.ContainsKey(key)
                    || !string.Equals(expectedValues[key], actualValues[key], StringComparison.Ordinal))
                .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

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
            private Comparison(bool equal, IEnumerable<string> changedParts, string persistedHash, IEnumerable<string> changedPartKeys = null)
            {
                Equal = equal;
                ChangedParts = new JArray(changedParts ?? Enumerable.Empty<string>());
                PersistedHash = persistedHash;
                ChangedPartKeys = (changedPartKeys ?? Enumerable.Empty<string>()).ToArray();
            }

            public bool Equal { get; }
            public JArray ChangedParts { get; }
            public string PersistedHash { get; }
            public string[] ChangedPartKeys { get; }

            public static Comparison Verified(string hash) => new Comparison(true, null, hash);
            public static Comparison Failed(IEnumerable<string> changedParts, string hash, IEnumerable<string> changedPartKeys = null) => new Comparison(false, changedParts, hash, changedPartKeys);
        }

        internal sealed class PartSnapshot
        {
            private PartSnapshot(string key, string name, string format, byte[] restoreData, byte[] verificationData)
            {
                Key = key;
                Name = name;
                Format = format;
                RestoreData = restoreData ?? new byte[0];
                VerificationData = verificationData ?? new byte[0];
            }

            public string Key { get; }
            public string Name { get; }
            public string Format { get; }
            public byte[] RestoreData { get; }
            public byte[] VerificationData { get; }

            public static PartSnapshot Capture(KBObjectPart part)
            {
                string key = GetKey(part);
                string name = part.TypeDescriptor?.Name ?? part.GetType().Name;
                byte[] native = SerializeEntityData(part);
                var source = part as ISource;
                if (source != null)
                {
                    byte[] text = Utf8(source.Source ?? string.Empty);
                    return new PartSnapshot(key, name, native != null ? "binary" : "source", native ?? text, text);
                }

                byte[] xml = Utf8(part.SerializeToXml() ?? string.Empty);
                return new PartSnapshot(key, name, native != null ? "binary" : "xml", native ?? xml, xml);
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
