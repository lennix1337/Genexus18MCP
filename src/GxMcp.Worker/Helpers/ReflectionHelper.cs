using System;
using System.Linq;
using System.Reflection;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// Headless and reflection utility providing safe member resolution across
    /// GeneXus SDK inheritance hierarchies where derived types shadow base properties.
    /// </summary>
    public static class ReflectionHelper
    {
        public static object TryGetMember(object target, string name)
        {
            if (target == null || string.IsNullOrEmpty(name)) return null;
            try
            {
                var property = target.GetType().GetProperty(
                    name,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (property != null)
                    return property.GetValue(target, null);
            }
            catch (AmbiguousMatchException)
            {
                try
                {
                    Type current = target.GetType();
                    while (current != null && current != typeof(object))
                    {
                        var prop = current.GetProperty(
                            name,
                            BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.IgnoreCase);
                        if (prop != null)
                            return prop.GetValue(target, null);
                        current = current.BaseType;
                    }
                }
                catch { }
            }
            catch { return null; }
            return null;
        }

        public static object TryGetPropertyBagValue(object target, string name)
        {
            if (target == null || string.IsNullOrEmpty(name)) return null;
            try
            {
                var method = target.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "GetPropertyValue"
                        && m.GetParameters().Length == 1
                        && m.GetParameters()[0].ParameterType == typeof(string));
                return method?.Invoke(target, new object[] { name });
            }
            catch { return null; }
        }

        /// <summary>
        /// Sets a value on SDK property-bag objects whose public CLR surface
        /// differs between GeneXus majors. The SDK exposes SetPropertyValue on
        /// PropertiesObject; the silent overload is retained as a compatibility
        /// fallback for older builds.
        /// </summary>
        public static bool TrySetPropertyBagValue(object target, string name, object value)
        {
            if (target == null || string.IsNullOrEmpty(name)) return false;
            try
            {
                var setter = target.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "SetPropertyValue"
                        && m.GetParameters().Length == 2
                        && m.GetParameters()[0].ParameterType == typeof(string)
                        && m.GetParameters()[1].ParameterType == typeof(object));
                if (setter != null)
                {
                    setter.Invoke(target, new[] { name, value });
                    return true;
                }

                setter = target.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "SilentSetPropertyValue"
                        && m.GetParameters().Length == 2
                        && m.GetParameters()[0].ParameterType == typeof(string)
                        && m.GetParameters()[1].ParameterType == typeof(object));
                if (setter != null)
                {
                    setter.Invoke(target, new[] { name, value });
                    return true;
                }
            }
            catch { }
            return false;
        }
    }
}
