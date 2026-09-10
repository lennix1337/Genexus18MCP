using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Helpers
{
    public class DomainEnumValueSpec
    {
        public string Name { get; set; }
        public string Value { get; set; }
        public string Description { get; set; }
    }

    /// <summary>
    /// Applies Type/Length/Decimals/Signed/EnumValues/DomainBasedOn onto a freshly-created
    /// Artech.Genexus.Common.Objects.Domain instance via reflection. The "Type" property is
    /// an eDBType enum on the real SDK and a string on test fakes — both are handled.
    ///
    /// EnumValues are not a direct settable property on Domain. The SDK stores them via
    /// Artech.Genexus.Common.Properties+ATT.SetEnumValues(IPropertyBag, EnumValues), and
    /// Domain implements IPropertyBag. We resolve those types once and cache the MethodInfo
    /// so batch domain creation doesn't repeat the assembly walk.
    /// </summary>
    public static class DomainPropertyApplier
    {
        public static bool ApplyPrimitive(object domain, string canonicalType, int? length, int? decimals, bool? signed)
        {
            if (domain == null || string.IsNullOrEmpty(canonicalType)) return false;
            if (!AttributeTypeApplier.CanonicalToEdb.TryGetValue(canonicalType, out var edbName)) return false;

            var typeProp = AttributeTypeApplier.GetPropertyUnambiguous(domain.GetType(), "Type");
            if (typeProp == null) return false;

            object enumValue;
            if (typeProp.PropertyType == typeof(string))
            {
                enumValue = edbName;
            }
            else
            {
                try { enumValue = Enum.Parse(typeProp.PropertyType, edbName, ignoreCase: true); }
                catch { return false; }
            }

            try { typeProp.SetValue(domain, enumValue, null); }
            catch { return false; }

            TrySetProperty(domain, "Length", length);
            TrySetProperty(domain, "Decimals", decimals);
            TrySetProperty(domain, "Signed", signed);
            return true;
        }

        public static bool ApplyDomainBasedOn(object domain, object basedOnDomain)
        {
            if (domain == null || basedOnDomain == null) return false;
            var p = AttributeTypeApplier.GetPropertyUnambiguous(domain.GetType(), "DomainBasedOn");
            if (p == null) return false;
            try
            {
                p.SetValue(domain, basedOnDomain, null);
                return true;
            }
            catch { return false; }
        }

        public static bool ApplyAttributeBasedOn(object target, object basedOnAttribute)
        {
            if (target == null || basedOnAttribute == null) return false;
            var p = AttributeTypeApplier.GetPropertyUnambiguous(target.GetType(), "AttributeBasedOn");
            if (p == null) return false;
            try
            {
                p.SetValue(target, basedOnAttribute, null);
                return true;
            }
            catch { return false; }
        }

        public static bool ClearDomainBasedOn(object target)
        {
            if (target == null) return false;
            var p = AttributeTypeApplier.GetPropertyUnambiguous(target.GetType(), "DomainBasedOn");
            if (p == null) return false;
            try { p.SetValue(target, null, null); return true; }
            catch { return false; }
        }

        public static bool ClearAttributeBasedOn(object target)
        {
            if (target == null) return false;
            var p = AttributeTypeApplier.GetPropertyUnambiguous(target.GetType(), "AttributeBasedOn");
            if (p == null) return false;
            try { p.SetValue(target, null, null); return true; }
            catch { return false; }
        }

        public static string GetAttributeBasedOnName(object target)
        {
            if (target == null) return null;
            try
            {
                dynamic d = target;
                object abo = d.AttributeBasedOn;
                if (abo != null)
                {
                    dynamic attr = abo;
                    string name = (string)attr.Name;
                    if (!string.IsNullOrEmpty(name)) return name;
                }
            }
            catch { }
            try
            {
                dynamic d = target;
                object bo = d.BasedOn;
                if (bo != null)
                {
                    dynamic r = bo;
                    string typeName = r.BasedOn?.ToString();
                    if (string.Equals(typeName, "Attribute", StringComparison.OrdinalIgnoreCase))
                    {
                        string n = r.Name?.ToString();
                        if (!string.IsNullOrEmpty(n)) return n;
                    }
                }
            }
            catch { }
            try
            {
                var p = AttributeTypeApplier.GetPropertyUnambiguous(target.GetType(), "AttributeBasedOn");
                if (p != null)
                {
                    var val = p.GetValue(target, null);
                    if (val != null)
                    {
                        var nameProp = val.GetType().GetProperty("Name");
                        if (nameProp != null)
                        {
                            return (string)nameProp.GetValue(val, null);
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        public static string GetDomainBasedOnName(object target)
        {
            if (target == null) return null;
            try
            {
                dynamic d = target;
                object dbo = d.DomainBasedOn;
                if (dbo != null)
                {
                    dynamic dom = dbo;
                    string name = (string)dom.Name;
                    if (!string.IsNullOrEmpty(name)) return name;
                }
            }
            catch { }
            try
            {
                dynamic d = target;
                object bo = d.BasedOn;
                if (bo != null)
                {
                    dynamic r = bo;
                    string typeName = r.BasedOn?.ToString();
                    if (string.Equals(typeName, "Domain", StringComparison.OrdinalIgnoreCase))
                    {
                        string n = r.Name?.ToString();
                        if (!string.IsNullOrEmpty(n)) return n;
                    }
                }
            }
            catch { }
            try
            {
                var p = AttributeTypeApplier.GetPropertyUnambiguous(target.GetType(), "DomainBasedOn");
                if (p != null)
                {
                    var val = p.GetValue(target, null);
                    if (val != null)
                    {
                        var nameProp = val.GetType().GetProperty("Name");
                        if (nameProp != null)
                        {
                            return (string)nameProp.GetValue(val, null);
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Returns count of enum values applied, or -1 if the SDK helper / types could not be
        /// resolved (caller decides whether that's fatal).
        /// </summary>
        public static int ApplyEnumValues(object domain, IList<DomainEnumValueSpec> values)
        {
            if (domain == null || values == null || values.Count == 0) return 0;

            Type evType = ResolveType("Artech.Genexus.Common.CustomTypes.EnumValue");
            Type evsType = ResolveType("Artech.Genexus.Common.CustomTypes.EnumValues");
            if (evType == null || evsType == null) return -1;

            object evsInstance;
            try { evsInstance = Activator.CreateInstance(evsType); }
            catch { return -1; }

            var valuesProp = evsType.GetProperty("Values");
            if (!(valuesProp?.GetValue(evsInstance, null) is IList listObj)) return -1;

            int applied = 0;
            foreach (var spec in values)
            {
                if (spec == null || string.IsNullOrEmpty(spec.Name)) continue;
                object ev;
                try { ev = Activator.CreateInstance(evType); }
                catch { continue; }
                TrySetProperty(ev, "Name", spec.Name);
                if (spec.Value != null) TrySetProperty(ev, "Value", spec.Value);
                if (spec.Description != null) TrySetProperty(ev, "Description", spec.Description);
                try { listObj.Add(ev); applied++; } catch { }
            }

            if (applied == 0) return 0;
            if (InvokePropertyBagSetEnumValues(domain, evsInstance)) return applied;
            if (TrySetProperty(domain, "EnumValues", evsInstance)) return applied;
            return -1;
        }

        /// <summary>Return the values currently exposed by the SDK in a stable JSON shape.</summary>
        public static JArray ReadEnumValues(object domain)
        {
            var result = new JArray();
            if (domain == null) return result;
            try
            {
                object enumValues = AttributeTypeApplier.GetPropertyUnambiguous(domain.GetType(), "EnumValues")
                    ?.GetValue(domain, null);
                if (enumValues == null) enumValues = InvokePropertyBagGetEnumValues(domain);
                object values = enumValues is IEnumerable
                    ? enumValues
                    : enumValues == null ? null
                    : AttributeTypeApplier.GetPropertyUnambiguous(enumValues.GetType(), "Values")?.GetValue(enumValues, null);
                if (!(values is IEnumerable enumerable)) return result;
                foreach (object item in enumerable)
                {
                    if (item == null) continue;
                    var jo = new JObject();
                    foreach (string propertyName in new[] { "Name", "Value", "Description" })
                    {
                        object value = null;
                        try { value = AttributeTypeApplier.GetPropertyUnambiguous(item.GetType(), propertyName)?.GetValue(item, null); }
                        catch { }
                        if (value != null) jo[char.ToLowerInvariant(propertyName[0]) + propertyName.Substring(1)] = value.ToString();
                    }
                    result.Add(jo);
                }
            }
            catch { }
            return result;
        }

        private static bool InvokePropertyBagSetEnumValues(object domain, object evsInstance)
        {
            var mi = _setEnumValuesMethod;
            if (mi == null)
            {
                mi = ResolveSetEnumValuesMethod();
                _setEnumValuesMethod = mi;
            }
            if (mi == null) return false;
            try { mi.Invoke(null, new[] { domain, evsInstance }); return true; }
            catch { return false; }
        }

        private static object InvokePropertyBagGetEnumValues(object domain)
        {
            var mi = _getEnumValuesMethod;
            if (mi == null)
            {
                mi = ResolveGetEnumValuesMethod();
                _getEnumValuesMethod = mi;
            }
            if (mi == null) return null;
            try { return mi.Invoke(null, new[] { domain }); }
            catch { return null; }
        }

        private static MethodInfo ResolveSetEnumValuesMethod()
        {
            var attType = ResolveType("Artech.Genexus.Common.Properties+ATT");
            return attType?.GetMethod("SetEnumValues", BindingFlags.Public | BindingFlags.Static);
        }

        private static MethodInfo ResolveGetEnumValuesMethod()
        {
            var attType = ResolveType("Artech.Genexus.Common.Properties+ATT");
            return attType?.GetMethod("GetEnumValues", BindingFlags.Public | BindingFlags.Static);
        }

        private static Type ResolveType(string fullName)
        {
            if (_typeCache.TryGetValue(fullName, out var cached)) return cached;
            // Prefer the SDK assembly directly; fall back to a full scan for test/fake hosts.
            Type t = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var n = asm.GetName().Name;
                if (n != null && n.StartsWith("Artech.Genexus.Common", StringComparison.Ordinal))
                {
                    t = asm.GetType(fullName, throwOnError: false);
                    if (t != null) break;
                }
            }
            if (t == null)
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    t = asm.GetType(fullName, throwOnError: false);
                    if (t != null) break;
                }
            }
            if (t != null) _typeCache[fullName] = t;
            return t;
        }

        private static bool TrySetProperty(object obj, string prop, object value)
        {
            if (obj == null || value == null) return false;
            var p = AttributeTypeApplier.GetPropertyUnambiguous(obj.GetType(), prop);
            if (p == null || !p.CanWrite) return false;
            Type target = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
            try
            {
                object coerced = target.IsInstanceOfType(value) ? value : Convert.ChangeType(value, target);
                p.SetValue(obj, coerced, null);
                return true;
            }
            catch { return false; }
        }

        private static readonly ConcurrentDictionary<string, Type> _typeCache =
            new ConcurrentDictionary<string, Type>(StringComparer.Ordinal);

        private static MethodInfo _setEnumValuesMethod;
        private static MethodInfo _getEnumValuesMethod;
    }
}
