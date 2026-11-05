using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using GxMcp.Worker.Helpers;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services.Structure
{
    /// <summary>
    /// The language-neutral names the SDK declares for SDT member properties: the typed constant of every property
    /// (Properties.SDTITEM.JsonNullSerialization = "idJsonInclude") and the member names of its enumeration
    /// (JsonNullSerialization_Values.Empty = "idJsonEmpty"). The IDE names are localized; these are not.
    /// </summary>
    internal static class SdtTypedCatalog
    {
        internal sealed class Entry
        {
            public string Constant { get; set; }
            public Dictionary<string, string> Members { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
        }

        internal static Dictionary<string, Entry> Load(Assembly sdkAssembly, bool isLevel)
        {
            var result = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            if (sdkAssembly == null) return result;
            try
            {
                Type properties = sdkAssembly.GetType("Artech.Genexus.Common.Properties");
                Type section = properties?.GetNestedType(isLevel ? "SDTLEVEL" : "SDTITEM", BindingFlags.Public);
                if (section == null) return result;
                foreach (FieldInfo field in section.GetFields(BindingFlags.Public | BindingFlags.Static))
                {
                    if (!field.IsLiteral || field.FieldType != typeof(string)) continue;
                    string native = field.GetRawConstantValue() as string;
                    if (string.IsNullOrEmpty(native) || result.ContainsKey(native)) continue;
                    var entry = new Entry { Constant = field.Name };
                    Type values = section.GetNestedType(field.Name + "_Values", BindingFlags.Public);
                    if (values != null)
                        foreach (FieldInfo value in values.GetFields(BindingFlags.Public | BindingFlags.Static))
                        {
                            string stored = value.IsLiteral && value.FieldType == typeof(string) ? value.GetRawConstantValue() as string : null;
                            if (stored != null) entry.Members[stored] = value.Name;
                        }
                    result[native] = entry;
                }
            }
            catch (Exception ex)
            {
                Logger.Debug("SDT member typed property catalog could not be loaded: " + ex.Message);
            }
            return result;
        }
    }

    /// <summary>
    /// Reads SDT members through the SDK object model: path resolution, the stored-property snapshot, the property catalog
    /// and the effective values. Everything is duck-typed (dynamic) like the rest of the SDT code, so it runs against the
    /// SDK of every supported GeneXus major and against test doubles.
    /// </summary>
    internal static class SdtSdkReader
    {
        private const int MaxListedSiblings = 40;

        /// <summary>The snapshot key of the root level, which holds the collection flag and the item name of the whole SDT.</summary>
        internal const string RootKey = "$root";

        /// <summary>
        /// Every member of the structure keyed by its slash-joined path (plus the root level), with its kind, its position among
        /// its siblings and the properties the entity would persist. A default-valued property is not serialized, so a snapshot
        /// difference is exactly a persisted difference.
        /// </summary>
        internal static JObject CaptureMembers(dynamic root)
        {
            var sink = new JObject();
            try { sink[RootKey] = Describe(root, "root", -1); }
            catch (Exception ex) { Logger.Debug("SDT root level could not be snapshotted: " + ex.Message); }
            Walk(root, string.Empty, sink);
            return sink;
        }

        private static JObject Describe(dynamic member, string kind, int index)
        {
            var stored = new SortedDictionary<string, string>(StringComparer.Ordinal);
            dynamic entity = member.ItemEntity;
            foreach (dynamic property in entity.SerializedProperties())
            {
                string key = (string)property.Name;
                if (!string.IsNullOrEmpty(key)) stored[key] = (string)property.Value;
            }
            var properties = new JObject();
            foreach (var pair in stored) properties[pair.Key] = pair.Value == null ? JValue.CreateNull() : new JValue(pair.Value);
            var description = new JObject { ["kind"] = kind };
            if (index >= 0) description["index"] = index;
            description["properties"] = properties;
            return description;
        }

        private static void Walk(dynamic level, string prefix, JObject sink)
        {
            int index = 0;
            foreach (dynamic child in level.Items)
            {
                string name = (string)child.Name;
                string path = prefix.Length == 0 ? name : prefix + "/" + name;
                bool leaf = (bool)child.IsLeafItem;
                sink[path] = Describe((object)child, leaf ? "item" : "level", index++);
                if (!leaf) Walk((object)child, path, sink);
            }
        }

        /// <summary>Walks itemPath from the root; throws <see cref="SdtUserFacingException"/> naming the failing segment.</summary>
        internal static dynamic ResolveMember(dynamic root, IList<string> itemPath, out List<string> canonicalPath)
        {
            canonicalPath = new List<string>();
            dynamic current = root;
            for (int i = 0; i < itemPath.Count; i++)
            {
                string wanted = itemPath[i];
                string location = canonicalPath.Count == 0 ? "the SDT root" : "'" + string.Join("/", canonicalPath) + "'";
                var matches = new List<dynamic>();
                var names = new List<string>();
                foreach (dynamic candidate in current.Items)
                {
                    string name = (string)candidate.Name;
                    names.Add(name);
                    if (string.Equals(name, wanted, StringComparison.OrdinalIgnoreCase)) matches.Add(candidate);
                }
                if (matches.Count > 1)
                {
                    var exact = new List<dynamic>();
                    foreach (dynamic candidate in matches)
                        if (string.Equals((string)candidate.Name, wanted, StringComparison.Ordinal)) exact.Add(candidate);
                    if (exact.Count == 1) matches = exact;
                }
                if (matches.Count == 0)
                    throw new SdtUserFacingException("ItemNotFound",
                        "SDT member '" + wanted + "' was not found in " + location + ".",
                        "Members there: " + Listed(names) + ".");
                if (matches.Count > 1)
                    throw new SdtUserFacingException("AmbiguousItem",
                        "SDT member '" + wanted + "' matches more than one member in " + location + ".",
                        "Members there: " + Listed(names) + ".");
                current = matches[0];
                canonicalPath.Add((string)current.Name);
                if (i < itemPath.Count - 1 && (bool)current.IsLeafItem)
                    throw new SdtUserFacingException("ItemNotALevel",
                        "SDT member '" + string.Join("/", canonicalPath) + "' is a leaf item and has no members below it.",
                        "Shorten itemPath to '" + string.Join("/", canonicalPath) + "'.");
            }
            return current;
        }

        private static string Listed(IList<string> names)
        {
            if (names.Count == 0) return "(none)";
            string shown = string.Join(", ", names.Take(MaxListedSiblings));
            return names.Count > MaxListedSiblings ? shown + ", … (" + names.Count + " in total)" : shown;
        }

        /// <summary>The properties the SDK exposes for the member, with the typed names and enumeration members the SDK declares.</summary>
        internal static IList<SdtPropertyInfo> BuildCatalog(dynamic member, bool isLevel)
        {
            var catalog = new List<SdtPropertyInfo>();
            dynamic entity = member.ItemEntity;
            object entityObject = entity;
            var typed = SdtTypedCatalog.Load(entityObject.GetType().Assembly, isLevel);

            foreach (dynamic definition in entity.GetPropertiesDefinition())
            {
                try
                {
                    string native = (string)definition.Name;
                    if (string.IsNullOrEmpty(native)) continue;
                    var info = new SdtPropertyInfo { NativeName = native };
                    try { info.DisplayName = (string)definition.DisplayName; } catch { }
                    try { info.Category = (string)definition.Category; } catch { }
                    try { info.Description = (string)definition.Description; } catch { }
                    try { info.ClrType = (Type)definition.PropertyType; } catch { }
                    if (info.ClrType == null) info.MarkIncomplete("the stored type of the property could not be read");

                    // The read-only, hidden and applicable flags decide whether a write is allowed: when the descriptor
                    // cannot be read, their defaults (writable, visible, applicable) must not stand in for the SDK's answer.
                    dynamic descriptor = null;
                    try { descriptor = entity.GetPropertyDescriptor(native); }
                    catch (Exception ex) { info.MarkIncomplete("the property descriptor could not be read: " + ex.Message); }
                    if (descriptor != null)
                    {
                        try { info.ReadOnly = (bool)descriptor.IsReadOnly; } catch { info.MarkIncomplete("the read-only flag could not be read"); }
                        try { info.Browsable = (bool)descriptor.IsBrowsable; } catch { info.MarkIncomplete("the visibility flag could not be read"); }
                        try { info.Applicable = (bool)descriptor.IsApplicable; } catch { info.MarkIncomplete("the applicable flag could not be read"); }
                    }
                    else if (!info.CatalogIncomplete) info.MarkIncomplete("the SDK returned no descriptor for the property");

                    SdtTypedCatalog.Entry entry;
                    typed.TryGetValue(native, out entry);
                    if (entry != null) info.ConstantName = entry.Constant;

                    try
                    {
                        if ((bool)definition.HasStandardValues(member))
                        {
                            foreach (object stored in (IEnumerable)definition.GetStandardValues(member))
                            {
                                string raw = SdtItemPropertyResolver.RenderRaw(stored);
                                if (raw == null) continue;
                                string label = null;
                                try { label = (string)definition.GetStandardNameFromValue(stored); } catch { }
                                string memberName = null;
                                if (entry != null) entry.Members.TryGetValue(raw, out memberName);
                                info.Options.Add(new SdtPropertyOption { Raw = raw, Label = label, Member = memberName, Value = stored });
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        // An enumeration whose values cannot be listed would look like free text and accept any string.
                        info.MarkIncomplete("the allowed values could not be read: " + ex.Message);
                        info.Options.Clear();
                        Logger.Debug("SDT member property '" + native + "' allowed values could not be read: " + ex.Message);
                    }
                    if (info.Options.Count == 0 && info.ClrType != null && info.ClrType.IsEnum)
                        foreach (string name in Enum.GetNames(info.ClrType))
                            info.Options.Add(new SdtPropertyOption { Raw = name, Member = name });

                    catalog.Add(info);
                }
                catch (Exception ex)
                {
                    Logger.Debug("SDT member property definition skipped: " + ex.Message);
                }
            }
            return catalog;
        }

        /// <summary>The value each cataloged property reads today and whether it is only the default. Unreadable properties are left out.</summary>
        internal static IDictionary<string, SdtEffectiveValue> ReadEffective(dynamic member, IList<SdtPropertyInfo> catalog)
        {
            var effective = new Dictionary<string, SdtEffectiveValue>(StringComparer.OrdinalIgnoreCase);
            dynamic entity = member.ItemEntity;
            foreach (SdtPropertyInfo info in catalog)
            {
                try
                {
                    object value = entity.GetPropertyValue(info.NativeName);
                    bool isDefault = (bool)entity.IsPropertyDefault(info.NativeName);
                    object defaultValue = null;
                    try
                    {
                        dynamic descriptor = entity.GetPropertyDescriptor(info.NativeName);
                        if (descriptor != null) defaultValue = descriptor.Definition.GetDefaultValue(descriptor.Manager);
                    }
                    catch { }
                    effective[info.NativeName] = new SdtEffectiveValue
                    {
                        Value = value,
                        Raw = SdtItemPropertyResolver.RenderRaw(value),
                        IsDefault = isDefault,
                        DefaultValue = defaultValue,
                        DefaultRaw = SdtItemPropertyResolver.RenderRaw(defaultValue)
                    };
                }
                catch (Exception ex)
                {
                    Logger.Debug("SDT member property '" + info.NativeName + "' could not be read: " + ex.Message);
                }
            }
            return effective;
        }
    }
}
