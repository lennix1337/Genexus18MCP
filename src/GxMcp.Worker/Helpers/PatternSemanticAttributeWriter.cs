using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// Applies PatternInstance attribute edits through the SDK's semantic change
    /// command. The XML write remains the public contract; this repairs the one
    /// attribute that the SDK deserializer drops before the normal save path.
    /// </summary>
    internal static class PatternSemanticAttributeWriter
    {
        private sealed class PathSegment
        {
            public string Name;
            public string Identity;
        }

        private sealed class RequestedAttribute
        {
            public List<PathSegment> Path;
            public string Value;
        }

        private sealed class NativeElement
        {
            public object Value;
            public List<PathSegment> Path;
            public string Name;
            public string ElementName;
        }

        internal static int ApplyGxObjectAttributes(object resolvedPart, string requestedXml)
        {
            if (resolvedPart == null || string.IsNullOrWhiteSpace(requestedXml)) return 0;

            try
            {
                var requested = ReadRequestedAttributes(requestedXml);
                if (requested.Count == 0) return 0;

                object root = ReadProperty(resolvedPart, "RootElement");
                if (root == null)
                {
                    Logger.Debug("[PATTERN-WRITE] Semantic gxobject repair skipped: RootElement unavailable.");
                    return 0;
                }

                var native = new List<NativeElement>();
                CollectElements(root, new List<PathSegment>(), native,
                    new HashSet<object>(ReferenceEqualityComparer.Instance));

                int applied = 0;
                var used = new HashSet<object>(ReferenceEqualityComparer.Instance);
                foreach (var wanted in requested)
                {
                    NativeElement target = native.FirstOrDefault(e =>
                        !used.Contains(e.Value)
                        && string.Equals(e.ElementName, "userAction", StringComparison.OrdinalIgnoreCase)
                        && PathsEqual(e.Path, wanted.Path));

                    if (target == null)
                    {
                        Logger.Debug("[PATTERN-WRITE] Semantic gxobject repair could not locate userAction '" +
                            GetNamedSegment(wanted.Path) + "'.");
                        continue;
                    }

                    used.Add(target.Value);
                    if (ApplySemanticAttribute(target.Value, "gxobject", wanted.Value))
                    {
                        applied++;
                        Logger.Info("[PATTERN-WRITE] Semantic gxobject repair applied to userAction '" +
                            target.Name + "'.");
                    }
                    else
                    {
                        Logger.Debug("[PATTERN-WRITE] Semantic gxobject repair was rejected for userAction '" +
                            target.Name + "'.");
                    }
                }

                return applied;
            }
            catch (Exception ex)
            {
                Logger.Debug("[PATTERN-WRITE] Semantic gxobject repair skipped: " + ex.Message);
                return 0;
            }
        }

        private static List<RequestedAttribute> ReadRequestedAttributes(string requestedXml)
        {
            var result = new List<RequestedAttribute>();
            var document = XDocument.Parse(requestedXml, LoadOptions.PreserveWhitespace);
            foreach (XElement element in document.Descendants().Where(e =>
                e.Name.LocalName.Equals("userAction", StringComparison.OrdinalIgnoreCase)
                && e.Attribute("gxobject") != null))
            {
                result.Add(new RequestedAttribute
                {
                    Path = BuildXmlPath(element),
                    Value = (string)element.Attribute("gxobject")
                });
            }
            return result;
        }

        private static List<PathSegment> BuildXmlPath(XElement element)
        {
            var result = new List<PathSegment>();
            XElement current = element;
            while (current != null)
            {
                var sameName = current.Parent == null
                    ? new List<XElement> { current }
                    : current.Parent.Elements().Where(e => e.Name.LocalName.Equals(current.Name.LocalName, StringComparison.OrdinalIgnoreCase)).ToList();
                int index = Math.Max(1, sameName.IndexOf(current) + 1);
                result.Insert(0, new PathSegment
                {
                    Name = current.Name.LocalName,
                    Identity = string.IsNullOrWhiteSpace((string)current.Attribute("name"))
                        ? "#" + index
                        : "@" + (string)current.Attribute("name")
                });
                current = current.Parent;
            }
            return result;
        }

        private static void CollectElements(object element, List<PathSegment> path,
            List<NativeElement> result, HashSet<object> visited)
        {
            if (element == null || !visited.Add(element)) return;

            object attributes = ReadProperty(element, "Attributes");
            string elementName = ReadStringProperty(element, "Type");
            if (string.IsNullOrWhiteSpace(elementName)) elementName = ReadStringProperty(element, "TypeName");
            if (string.IsNullOrWhiteSpace(elementName)) elementName = ReadStringProperty(element, "Name");
            if (string.IsNullOrWhiteSpace(elementName)) return;

            string named = ReadAttribute(attributes, "name");
            if (string.IsNullOrWhiteSpace(named)) named = ReadStringProperty(element, "Name");

            var currentPath = new List<PathSegment>(path);
            if (currentPath.Count == 0)
            {
                currentPath.Add(new PathSegment
                {
                    Name = elementName,
                    Identity = string.IsNullOrWhiteSpace(named) ? "#1" : "@" + named
                });
            }

            result.Add(new NativeElement
            {
                Value = element,
                Path = currentPath,
                Name = named,
                ElementName = elementName
            });

            object children = ReadProperty(element, "Children");
            if (!(children is IEnumerable enumerable)) return;

            var occurrences = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (object child in enumerable)
            {
                if (child == null) continue;
                string childName = ReadStringProperty(child, "Type");
                if (string.IsNullOrWhiteSpace(childName)) childName = ReadStringProperty(child, "TypeName");
                if (string.IsNullOrWhiteSpace(childName)) childName = ReadStringProperty(child, "Name");
                if (string.IsNullOrWhiteSpace(childName)) continue;

                int occurrence = occurrences.TryGetValue(childName, out int count) ? count + 1 : 1;
                occurrences[childName] = occurrence;
                object childAttributes = ReadProperty(child, "Attributes");
                string childNamed = ReadAttribute(childAttributes, "name");
                if (string.IsNullOrWhiteSpace(childNamed)) childNamed = ReadStringProperty(child, "Name");

                var childPath = new List<PathSegment>(currentPath)
                {
                    new PathSegment
                    {
                        Name = childName,
                        Identity = string.IsNullOrWhiteSpace(childNamed) ? "#" + occurrence : "@" + childNamed
                    }
                };
                CollectElements(child, childPath, result, visited);
            }
        }

        private static bool PathsEqual(List<PathSegment> left, List<PathSegment> right)
        {
            if (left == null || right == null || left.Count != right.Count) return false;
            for (int i = 0; i < left.Count; i++)
            {
                if (!string.Equals(left[i].Name, right[i].Name, StringComparison.OrdinalIgnoreCase)) return false;
                if (i == 0) continue;
                if (!string.Equals(left[i].Identity, right[i].Identity, StringComparison.OrdinalIgnoreCase)) return false;
            }
            return true;
        }

        private static string GetNamedSegment(List<PathSegment> path)
        {
            if (path == null) return string.Empty;
            PathSegment action = path.LastOrDefault(p => p.Name.Equals("userAction", StringComparison.OrdinalIgnoreCase));
            return action == null || action.Identity == null || !action.Identity.StartsWith("@", StringComparison.Ordinal)
                ? string.Empty
                : action.Identity.Substring(1);
        }

        internal static bool ApplySemanticAttribute(object element, string attributeName, string value)
        {
            if (element == null || string.IsNullOrWhiteSpace(attributeName) || value == null) return false;
            return ApplySemanticAttributeObject(element, attributeName, value, value);
        }

        internal static object ReadSemanticAttributeObject(object element, string attributeName)
        {
            if (element == null || string.IsNullOrWhiteSpace(attributeName)) return null;
            object attributes = ReadProperty(element, "Attributes");
            return attributes == null ? null : ReadAttributeObject(attributes, attributeName);
        }

        internal static bool ApplySemanticAttributeObject(object element, string attributeName,
            object value, string expectedString = null)
        {
            if (element == null || string.IsNullOrWhiteSpace(attributeName) || value == null) return false;
            object attributes = ReadProperty(element, "Attributes");
            if (attributes == null) return false;

            if (expectedString != null
                && string.Equals(ReadAttribute(attributes, attributeName), expectedString, StringComparison.Ordinal)) return true;

            Type commandType = element.GetType().Assembly.GetType(
                "Artech.Packages.Patterns.Objects.ChangeAttributeValueCommand", false);
            if (commandType == null)
            {
                Logger.Debug("[PATTERN-WRITE] ChangeAttributeValueCommand is unavailable.");
                return false;
            }

            ConstructorInfo constructor = commandType.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .FirstOrDefault(c =>
                {
                    ParameterInfo[] parameters = c.GetParameters();
                    return parameters.Length == 4
                        && parameters[0].ParameterType.IsInstanceOfType(element)
                        && parameters[1].ParameterType == typeof(string)
                        && parameters[2].ParameterType == typeof(object)
                        && parameters[3].ParameterType == typeof(object);
                });
            if (constructor == null)
            {
                Logger.Debug("[PATTERN-WRITE] ChangeAttributeValueCommand constructor is unavailable.");
                return false;
            }

            object oldValue = ReadAttributeObject(attributes, attributeName);
            object command = constructor.Invoke(new object[] { element, attributeName, oldValue, value });
            MethodInfo safeMethod = commandType.GetMethod("IsSafeToExecute", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (safeMethod != null && safeMethod.ReturnType == typeof(bool)
                && !(bool)safeMethod.Invoke(command, null))
            {
                Logger.Debug("[PATTERN-WRITE] ChangeAttributeValueCommand reported an unsafe gxobject update.");
                return false;
            }

            MethodInfo executeMethod = commandType.GetMethod("Execute", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (executeMethod == null) return false;
            executeMethod.Invoke(command, null);
            object resultingValue = ReadAttributeObject(attributes, attributeName);
            if (expectedString == null) return resultingValue != null;
            return string.Equals(ReadAttribute(attributes, attributeName), expectedString, StringComparison.Ordinal)
                || ValuesEquivalent(resultingValue, value);
        }

        private static bool ValuesEquivalent(object left, object right)
        {
            if (left == null || right == null) return false;
            if (ReferenceEquals(left, right) || left.Equals(right)) return true;
            try
            {
                PropertyInfo leftGuid = left.GetType().GetProperty("Guid", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                PropertyInfo rightGuid = right.GetType().GetProperty("Guid", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                string leftValue = leftGuid?.GetValue(left, null)?.ToString();
                string rightValue = rightGuid?.GetValue(right, null)?.ToString();
                return !string.IsNullOrWhiteSpace(leftValue)
                    && string.Equals(leftValue, rightValue, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static object ReadAttributeObject(object attributes, string name)
        {
            MethodInfo getter = attributes.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name.Equals("GetPropertyValue", StringComparison.OrdinalIgnoreCase)
                    && !m.IsGenericMethod
                    && m.GetParameters().Length == 1
                    && m.GetParameters()[0].ParameterType == typeof(string)
                    && m.ReturnType != typeof(void));
            if (getter != null)
            {
                try { return getter.Invoke(attributes, new object[] { name }); }
                catch { }
            }
            return ReadAttribute(attributes, name);
        }

        private static string ReadAttribute(object attributes, string name)
        {
            if (attributes == null || string.IsNullOrWhiteSpace(name)) return null;
            MethodInfo getter = attributes.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name.Equals("GetPropertyValueString", StringComparison.OrdinalIgnoreCase)
                    && m.GetParameters().Length == 1
                    && m.GetParameters()[0].ParameterType == typeof(string));
            if (getter != null)
            {
                try { return getter.Invoke(attributes, new object[] { name })?.ToString(); }
                catch { }
            }

            PropertyInfo indexer = attributes.GetType().GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .FirstOrDefault(p => p.Name.Equals("Item", StringComparison.OrdinalIgnoreCase)
                    && p.GetIndexParameters().Length == 1
                    && p.GetIndexParameters()[0].ParameterType == typeof(string));
            if (indexer != null)
            {
                try { return indexer.GetValue(attributes, new object[] { name })?.ToString(); }
                catch { }
            }
            return null;
        }

        private static object ReadProperty(object instance, string name)
        {
            if (instance == null) return null;
            PropertyInfo property = instance.GetType().GetProperty(name,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (property == null || property.GetIndexParameters().Length > 0) return null;
            try { return property.GetValue(instance, null); }
            catch { return null; }
        }

        private static string ReadStringProperty(object instance, string name)
        {
            return ReadProperty(instance, name)?.ToString();
        }

        private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
        {
            internal static readonly ReferenceEqualityComparer Instance = new ReferenceEqualityComparer();
            public new bool Equals(object x, object y) => ReferenceEquals(x, y);
            public int GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }
    }
}
