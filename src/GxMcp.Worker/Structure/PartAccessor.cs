using System;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using Artech.Architecture.Common.Objects;
using Artech.Genexus.Common.Objects;
using Artech.Genexus.Common.Parts;

namespace GxMcp.Worker.Structure
{
    public static class PartAccessor
    {
        // v2.3.8 Task 4.4 — Known part-type-names that wrap variable collections across
        // GeneXus object kinds. The typed `obj.Parts.Get<VariablesPart>()` lookup covers
        // Procedure / DataProvider cleanly, but some kinds (WebPanel, Transaction,
        // WorkPanel) historically returned null from that call, triggering the friction
        // "Part 'DeleteVariable' not found in WebPanel" failure mode. This name list
        // is consulted as the second-pass fallback before reflection.
        private static readonly string[] VariablesPartTypeNameCandidates = new[]
        {
            "VariablesPart",
            "Variables",
            "ProcedureVariables",
            "WebFormVariables",
            "TransactionVariables",
            "WorkPanelVariables",
        };

        /// <summary>
        /// v2.3.8 Task 4.4 — Kind-aware <see cref="VariablesPart"/> accessor.
        ///
        /// Resolution order:
        ///   1. <c>obj.Parts.Get&lt;VariablesPart&gt;()</c>           — typed SDK lookup.
        ///   2. Match by concrete type name (e.g. "VariablesPart", "ProcedureVariables").
        ///   3. Reflective fallback — first part exposing a public <c>Variables</c> property.
        ///
        /// Returns <c>null</c> only when the object truly has no variables-bearing part.
        /// </summary>
        public static VariablesPart GetVariablesPart(KBObject obj)
        {
            if (obj == null) return null;

            // 1. Typed SDK accessor — fast path; covers Procedure, DataProvider and any
            // kind whose variables part is the canonical VariablesPart concrete type.
            try
            {
                var typed = obj.Parts.Get<VariablesPart>();
                if (typed != null) return typed;
            }
            catch { /* SDK may throw for kinds that don't expose VariablesPart — try next */ }

            // 2. Name-based dispatch across known kind-specific part type names.
            foreach (KBObjectPart p in obj.Parts)
            {
                if (p == null) continue;
                var typeName = p.GetType().Name;
                var descriptorName = p.TypeDescriptor?.Name;
                foreach (var candidate in VariablesPartTypeNameCandidates)
                {
                    if (string.Equals(typeName, candidate, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(descriptorName, candidate, StringComparison.OrdinalIgnoreCase))
                    {
                        if (p is VariablesPart vp) return vp;
                    }
                }
            }

            // 3. Reflective fallback — any part exposing a `Variables` collection property
            // is considered variables-bearing. Returned as VariablesPart only when it
            // actually inherits from that concrete type (preserves type safety for callers).
            foreach (KBObjectPart p in obj.Parts)
            {
                if (p == null) continue;
                if (p.GetType().GetProperty("Variables", BindingFlags.Public | BindingFlags.Instance) != null)
                {
                    if (p is VariablesPart vp) return vp;
                }
            }

            return null;
        }
        /// <summary>
        /// Returns the variables-bearing part even when a GeneXus major exposes it as a
        /// kind-specific part that does not derive from VariablesPart.  Read/inspect paths
        /// use this structural accessor; typed write paths should continue to require the
        /// native VariablesPart contract before mutating it.
        /// </summary>
        public static KBObjectPart GetVariablesLikePart(KBObject obj)
        {
            if (obj == null) return null;
            try
            {
                var typed = obj.Parts.Get<VariablesPart>();
                if (typed != null) return typed;
            }
            catch { }

            foreach (KBObjectPart part in obj.Parts)
            {
                if (part == null) continue;
                string typeName = part.GetType().Name;
                string descriptorName = part.TypeDescriptor?.Name;
                if (VariablesPartTypeNameCandidates.Any(candidate =>
                    string.Equals(typeName, candidate, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(descriptorName, candidate, StringComparison.OrdinalIgnoreCase)))
                    return part;
            }

            foreach (KBObjectPart part in obj.Parts)
            {
                if (part != null
                    && (FindVariablesProperty(part.GetType()) != null
                        || FindVariablesMethod(part.GetType()) != null))
                    return part;
            }
            return null;
        }

        private static PropertyInfo FindVariablesProperty(Type type)
        {
            if (type == null) return null;
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            return type.GetProperty("Variables", flags)
                ?? type.GetProperty("VariablesList", flags)
                ?? type.GetProperty("VariableList", flags)
                ?? type.GetProperty("VariableDefinitions", flags)
                ?? type.GetProperty("VariablesDefinition", flags);
        }

        private static MethodInfo FindVariablesMethod(Type type)
        {
            if (type == null) return null;
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            return type.GetMethod("GetVariables", flags)
                ?? type.GetMethod("GetVariablesList", flags)
                ?? type.GetMethod("GetVariableList", flags);
        }

        /// <summary>
        /// Enumerates the structural variables collection without assuming every
        /// item derives from the major-specific Variable CLR type. K2B/older SDK
        /// builds can expose proxy declarations with the same named properties;
        /// dropping them here made both explicit reads and inspect silently lose
        /// part of the model.
        /// </summary>
        public static IEnumerable<object> GetVariableObjects(KBObject obj)
        {
            var part = GetVariablesLikePart(obj);
            if (part == null) yield break;
            object value = null;
            try
            {
                var property = FindVariablesProperty(part.GetType());
                if (property != null) value = property.GetValue(part, null);
                if (value == null)
                {
                    var method = FindVariablesMethod(part.GetType());
                    if (method != null && method.GetParameters().Length == 0)
                        value = method.Invoke(part, null);
                }
            }
            catch { }
            if (!(value is System.Collections.IEnumerable enumerable)) yield break;
            foreach (object item in enumerable)
            {
                if (item != null) yield return item;
            }
        }

        public static IEnumerable<global::Artech.Genexus.Common.Variable> GetVariables(KBObject obj)
            => GetVariableObjects(obj).OfType<global::Artech.Genexus.Common.Variable>();

        public static bool HasReadableVariablesCollection(KBObject obj)
        {
            var part = GetVariablesLikePart(obj);
            if (part == null) return false;
            try
            {
                var property = FindVariablesProperty(part.GetType());
                if (property != null)
                {
                    property.GetValue(part, null);
                    return true;
                }
                var method = FindVariablesMethod(part.GetType());
                if (method != null && method.GetParameters().Length == 0)
                {
                    method.Invoke(part, null);
                    return true;
                }
            }
            catch { }
            return false;
        }

        public static string GetVariableName(object variable)
        {
            if (variable == null) return string.Empty;
            if (variable is global::Artech.Genexus.Common.Variable typed) return typed.Name ?? string.Empty;
            try
            {
                const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.IgnoreCase;
                var property = variable.GetType().GetProperty("Name", flags)
                    ?? variable.GetType().GetProperty("VariableName", flags)
                    ?? variable.GetType().GetProperty("Code", flags);
                return property?.GetValue(variable, null)?.ToString() ?? string.Empty;
            }
            catch { return variable.ToString() ?? string.Empty; }
        }

        public static bool MatchesSourcePart(string requestedPartName, string sourcePartName)
        {
            if (string.IsNullOrWhiteSpace(requestedPartName))
            {
                return false;
            }

            bool isEventsRequest = requestedPartName.Equals("Events", StringComparison.OrdinalIgnoreCase);
            bool isSourceAliasRequest =
                requestedPartName.Equals("Source", StringComparison.OrdinalIgnoreCase) ||
                requestedPartName.Equals("Code", StringComparison.OrdinalIgnoreCase);

            if (isEventsRequest)
            {
                return string.Equals(sourcePartName, "Events", StringComparison.OrdinalIgnoreCase);
            }

            if (!isSourceAliasRequest)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(sourcePartName))
            {
                return true;
            }

            return !string.Equals(sourcePartName, "Events", StringComparison.OrdinalIgnoreCase);
        }

        public static Guid GetPartGuid(string objType, string partName)
        {
            string p = partName.ToLower();

            if (objType.Equals("Procedure", StringComparison.OrdinalIgnoreCase))
            {
                if (p == "source" || p == "code") return Guid.Parse("c5f0ef88-9ef8-4218-bf76-915024b3c48f");
                if (p == "rules") return Guid.Parse("9b0a32a3-de6d-4be1-a4dd-1b85d3741534");
                if (p == "variables") return Guid.Parse("e4c4ade7-53f0-4a56-bdfd-843735b66f47");
                if (p == "help") return Guid.Parse("017ea008-6202-4468-a400-3f412c938473");
                if (p == "layout") return Guid.Parse("c414ed00-8cc4-4f44-8820-4baf93547173");
            }
            
            if (objType.Equals("WebPanel", StringComparison.OrdinalIgnoreCase))
            {
                if (p == "events" || p == "source" || p == "code") return Guid.Parse("c44bd5ff-f918-415b-98e6-aca44fed84fa");
                if (p == "rules") return Guid.Parse("9b0a32a3-de6d-4be1-a4dd-1b85d3741534");
                if (p == "variables") return Guid.Parse("e4c4ade7-53f0-4a56-bdfd-843735b66f47");
                if (p == "layout") return Guid.Parse("ad3ca970-19d0-44e1-a7b7-db05556e820c");
                if (p == "webform") return Guid.Parse("d24a58ad-57ba-41b7-9e6e-eaca3543c778");
            }

            if (objType.Equals("Transaction", StringComparison.OrdinalIgnoreCase))
            {
                if (p == "structure") return Guid.Parse("1608677c-a7a2-4a00-8809-6d2466085a5a");
                if (p == "events" || p == "source" || p == "code") return Guid.Parse("c44bd5ff-f918-415b-98e6-aca44fed84fa");
                if (p == "rules") return Guid.Parse("9b0a32a3-de6d-4be1-a4dd-1b85d3741534");
                if (p == "variables") return Guid.Parse("e4c4ade7-53f0-4a56-bdfd-843735b66f47");
                if (p == "layout" || p == "webform") return Guid.Parse("d24a58ad-57ba-41b7-9e6e-eaca3543c778");
            }

            // issue #29: SDPanel (Smart Device Panel) parts are WorkWithDevices *virtual*
            // projection parts whose GUIDs differ from the Web equivalents (the leading hex
            // nibble is masked). The generic switch below returns the Web GUIDs, which never
            // match, so reads fell through to a bare SerializeToXml() → "<Properties />".
            // Map the SD source-bearing parts explicitly. "Source"/"Events"/"Code" resolve to
            // SDEvents (the panel's event code) — matching WebPanel semantics — NOT SDRules.
            if (objType.Equals("SDPanel", StringComparison.OrdinalIgnoreCase)
                || objType.Equals("PanelForSD", StringComparison.OrdinalIgnoreCase))
            {
                if (p == "events" || p == "source" || p == "code" || p == "sdevents") return Guid.Parse("144bd5ff-f918-415b-98e6-aca44fed84fa");
                if (p == "rules" || p == "sdrules") return Guid.Parse("1b0a32a3-de6d-4be1-a4dd-1b85d3741534");
                if (p == "variables" || p == "sdvariables") return Guid.Parse("14c4ade7-53f0-4a56-bdfd-843735b66f47");
                if (p == "layout" || p == "sdlayout") return Guid.Parse("1414ed00-8cc4-4f44-8820-4baf93547173");
                if (p == "conditions" || p == "sdconditions") return Guid.Parse("163f0d8b-d8ac-4db4-8dd4-de8979f2b5b9");
            }

            if (objType.Equals("DataProvider", StringComparison.OrdinalIgnoreCase))
            {
                if (p == "source" || p == "code") return Guid.Parse("91705646-6086-4f32-8871-08149817e754");
                if (p == "variables") return Guid.Parse("e4c4ade7-53f0-4a56-bdfd-843735b66f47");
                if (p == "help") return Guid.Parse("017ea008-6202-4468-a400-3f412c938473");
            }

            if (objType.Equals("SDT", StringComparison.OrdinalIgnoreCase) || objType.Equals("StructuredDataType", StringComparison.OrdinalIgnoreCase))
            {
                if (p == "structure" || p == "source") return Guid.Parse("8597371d-1941-4c12-9c17-48df9911e2f3");
            }

            // Defaults fallback
            switch (p)
            {
                case "source": return Guid.Parse("c5f0ef88-9ef8-4218-bf76-915024b3c48f");
                case "rules": return Guid.Parse("9b0a32a3-de6d-4be1-a4dd-1b85d3741534");
                case "events": return Guid.Parse("c44bd5ff-f918-415b-98e6-aca44fed84fa");
                case "variables": return Guid.Parse("e4c4ade7-53f0-4a56-bdfd-843735b66f47");
                case "structure": return Guid.Parse("1608677c-a7a2-4a00-8809-6d2466085a5a");
                case "layout": return Guid.Parse("c414ed00-8cc4-4f44-8820-4baf93547173");
                case "webform": return Guid.Parse("d24a58ad-57ba-41b7-9e6e-eaca3543c778");
                case "patterninstance": return Guid.Parse("a51ced48-7bee-0001-ab12-04e9e32123d1");
                case "help": return Guid.Parse("017ea008-6202-4468-a400-3f412c938473");
                case "documentation": return Guid.Parse("babf62c5-0111-49e9-a1c3-cc004d90900a");
                default: return Guid.Empty;
            }
        }

        // issue #26 (Humberto DSO case): a Design System object exposes TWO ISource parts —
        // Tokens (75e52d99-…) and Styles (c6b14574-…). The generic ISource fallback below
        // treats any non-Events source alias as matching the FIRST ISource part, which
        // collapsed both onto Tokens. These helpers make the two parts individually
        // addressable and let the write/read paths route tokens vs styles correctly.
        internal static readonly Guid DesignSystemTokensPartGuid = Guid.Parse("75e52d99-6edd-4bad-a1d7-dcc9b7f000ef");
        internal static readonly Guid DesignSystemStylesPartGuid = Guid.Parse("c6b14574-4f5f-4e35-aaa7-e322e88a9a10");

        public static bool IsDesignSystem(KBObject obj)
        {
            try
            {
                var descriptorName = obj?.TypeDescriptor?.Name;
                var runtimeName = obj?.GetType()?.Name;
                return (!string.IsNullOrEmpty(descriptorName)
                        && descriptorName.IndexOf("DesignSystem", StringComparison.OrdinalIgnoreCase) >= 0)
                    || (!string.IsNullOrEmpty(runtimeName)
                        && runtimeName.IndexOf("DesignSystem", StringComparison.OrdinalIgnoreCase) >= 0);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Resolve the Tokens or Styles part of a Design System object (by descriptor
        /// name, then GUID). Returns null if not a DSO or the part isn't present.</summary>
        public static KBObjectPart GetDesignSystemPart(KBObject obj, bool styles)
        {
            if (!IsDesignSystem(obj)) return null;
            string wantName = styles ? "Styles" : "Tokens";
            Guid wantGuid = styles ? DesignSystemStylesPartGuid : DesignSystemTokensPartGuid;
            foreach (KBObjectPart p in obj.Parts)
            {
                if (p == null) continue;
                if (string.Equals(p.TypeDescriptor?.Name, wantName, StringComparison.OrdinalIgnoreCase)) return p;
            }
            return obj.Parts.Cast<KBObjectPart>().FirstOrDefault(p => p.Type == wantGuid);
        }

        public static KBObjectPart GetPart(KBObject obj, string partName)
        {
            if (obj != null && string.Equals(partName, "DataViewIndexes", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var property = obj.GetType().GetProperty("DataViewIndexes", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (property?.GetValue(obj, null) is KBObjectPart dataViewIndexes) return dataViewIndexes;
                }
                catch { }
                foreach (KBObjectPart part in obj.Parts)
                {
                    string typeName = part?.GetType()?.Name ?? string.Empty;
                    string descriptorName = part?.TypeDescriptor?.Name ?? string.Empty;
                    if (typeName.IndexOf("DataViewIndex", StringComparison.OrdinalIgnoreCase) >= 0
                        || descriptorName.IndexOf("DataViewIndex", StringComparison.OrdinalIgnoreCase) >= 0)
                        return part;
                }
            }

            // GeneXus API objects keep their authored methods in the typed
            // ServiceGroupSource part. It is not exposed by the generic Parts
            // descriptor under the user-facing name "Methods" on every GX
            // version, so resolve this alias before the GUID/name fallbacks.
            if (obj != null && string.Equals(obj.TypeDescriptor?.Name, "API", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(partName)
                && (partName.Equals("Methods", StringComparison.OrdinalIgnoreCase)
                    || partName.Equals("ServiceGroupSource", StringComparison.OrdinalIgnoreCase)
                    || partName.Equals("Source", StringComparison.OrdinalIgnoreCase)
                    || partName.Equals("Code", StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    dynamic d = obj;
                    if (d.ServiceGroupSource != null) return (KBObjectPart)d.ServiceGroupSource;
                }
                catch { }
            }

            // issue #26: DSO Tokens/Styles are addressable by name directly, ahead of the
            // generic ISource fallback that would otherwise map both to the first source part.
            if (IsDesignSystem(obj) && !string.IsNullOrEmpty(partName))
            {
                if (partName.Equals("Tokens", StringComparison.OrdinalIgnoreCase))
                {
                    var tp = GetDesignSystemPart(obj, styles: false);
                    if (tp != null) return tp;
                }
                else if (partName.Equals("Styles", StringComparison.OrdinalIgnoreCase))
                {
                    var sp = GetDesignSystemPart(obj, styles: true);
                    if (sp != null) return sp;
                }
            }

            // Theme styles and regular Design System style sheets are not exposed
            // consistently through TypeDescriptor names across GeneXus updates. Use
            // the concrete part names as a stable SDK-compatible alias before the
            // GUID/source fallbacks.
            if (!string.IsNullOrWhiteSpace(partName)
                && (partName.Equals("ThemeStyles", StringComparison.OrdinalIgnoreCase)
                    || partName.Equals("StyleSheet", StringComparison.OrdinalIgnoreCase)
                    || partName.Equals("Theme", StringComparison.OrdinalIgnoreCase)))
            {
                foreach (KBObjectPart p in obj.Parts)
                {
                    string concrete = p?.GetType()?.Name ?? string.Empty;
                    if ((partName.Equals("ThemeStyles", StringComparison.OrdinalIgnoreCase)
                         || partName.Equals("Theme", StringComparison.OrdinalIgnoreCase))
                        && concrete.IndexOf("ThemeStylesPart", StringComparison.OrdinalIgnoreCase) >= 0)
                        return p;
                    if (partName.Equals("StyleSheet", StringComparison.OrdinalIgnoreCase)
                        && concrete.IndexOf("DesignStylesPart", StringComparison.OrdinalIgnoreCase) >= 0)
                        return p;
                }
            }

            Guid partGuid = GetPartGuid(obj.TypeDescriptor.Name, partName);

            if (partGuid != Guid.Empty)
            {
                var part = obj.Parts.Cast<KBObjectPart>().FirstOrDefault(p => p.Type == partGuid);
                if (part != null) return part;
            }

            // Dynamic Discovery Fallback
            foreach (KBObjectPart p in obj.Parts)
            {
                if (p.TypeDescriptor != null && p.TypeDescriptor.Name.Equals(partName, StringComparison.OrdinalIgnoreCase)) return p;
                if (p is ISource && MatchesSourcePart(partName, p.TypeDescriptor?.Name)) return p;
                if (p.GetType().Name.Equals("VariablesPart") && partName.Equals("Variables", StringComparison.OrdinalIgnoreCase)) return p;
                if (partName.Equals("Variables", StringComparison.OrdinalIgnoreCase)
                    && p.GetType().GetProperty("Variables", BindingFlags.Public | BindingFlags.Instance) != null) return p;
            }

            if (partName.Equals("Source", StringComparison.OrdinalIgnoreCase) || partName.Equals("Code", StringComparison.OrdinalIgnoreCase))
            {
                return obj.Parts.Cast<KBObjectPart>().FirstOrDefault(p => p is ISource);
            }

            return null;
        }

        // issue #29: the SDPanel layout/variables/conditions parts are WorkWithDevices
        // *virtual* projection parts (Artech.Patterns.WorkWithDevices.Parts.Virtual*Part).
        // They are not ISource and their SerializeToXml() returns an empty "<Properties />"
        // even when the panel has content, because the real data is projected from the
        // pattern rather than stored on the part. Callers use this to emit an honest
        // "not extractable / not truly empty" note instead of a misleading blank.
        public static bool IsWorkWithDevicesProjectionPart(KBObjectPart part)
        {
            if (part == null) return false;
            if (part is ISource) return false; // SDEvents/SDRules ARE readable via .Source
            var full = part.GetType().FullName ?? string.Empty;
            return full.IndexOf("WorkWithDevices.Parts", StringComparison.OrdinalIgnoreCase) >= 0
                   && part.GetType().Name.StartsWith("Virtual", StringComparison.Ordinal);
        }

        public static string[] GetAvailableParts(KBObject obj)
        {
            if (obj == null)
            {
                return new string[0];
            }

            var names = obj.Parts
                .Cast<KBObjectPart>()
                .Select(GetDisplayPartName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                // PatternVirtual has no readable SDK path through the MCP — hide until one exists.
                .Where(name => !string.Equals(name, "PatternVirtual", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (obj.TypeDescriptor?.Name?.Equals("DataView", StringComparison.OrdinalIgnoreCase) == true)
            {
                try
                {
                    var property = obj.GetType().GetProperty("DataViewIndexes", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (property?.GetValue(obj, null) is KBObjectPart) names.Add("DataViewIndexes");
                }
                catch { }
            }

            // API.ServiceGroupSource is the native methods/route part. Surface
            // the stable MCP name even when the SDK descriptor says Source or
            // ServiceGroupSource, and avoid exposing a misleading duplicate.
            if (obj != null && string.Equals(obj.TypeDescriptor?.Name, "API", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    dynamic d = obj;
                    if (d.ServiceGroupSource != null)
                    {
                        names.Add("Methods");
                        names.RemoveAll(n => string.Equals(n, "Source", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(n, "ServiceGroupSource", StringComparison.OrdinalIgnoreCase));
                    }
                }
                catch { }
            }

            // Expose virtual/aliased parts only when the same resolver used by
            // genexus_read can actually resolve them on this object.
            foreach (string alias in new[] { "Rules", "Events" })
            {
                try
                {
                    if (GetPart(obj, alias) != null && !names.Contains(alias, StringComparer.OrdinalIgnoreCase))
                        names.Add(alias);
                }
                catch { }
            }

            // "Source" and "Events" both resolve to the same ISource part on WebPanels/Transactions —
            // keep only the canonical "Events" name; GetPart still accepts "Source" as alias.
            if (names.Any(n => string.Equals(n, "Events", StringComparison.OrdinalIgnoreCase)))
                names.RemoveAll(n => string.Equals(n, "Source", StringComparison.OrdinalIgnoreCase));

            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names.ToArray();
        }

        /// First ISource part's text, or empty when the object exposes none.
        public static string GetFirstSourceText(KBObject obj)
        {
            if (obj == null) return string.Empty;
            foreach (var p in obj.Parts)
                if (p is ISource s) return s.Source ?? string.Empty;
            return string.Empty;
        }

        private static string GetDisplayPartName(KBObjectPart part)
        {
            if (part == null)
            {
                return null;
            }

            if (part.Type == DesignSystemTokensPartGuid)
            {
                return "Tokens";
            }
            if (part.Type == DesignSystemStylesPartGuid)
            {
                return "Styles";
            }

            if (part is ISource)
            {
                var sourceName = part.TypeDescriptor?.Name;
                if (string.Equals(sourceName, "Events", StringComparison.OrdinalIgnoreCase))
                {
                    return "Events";
                }
                // issue #26: a Design System has two ISource parts — keep them distinct
                // instead of collapsing both to "Source" (which hid the Styles part).
                if (string.Equals(sourceName, "Tokens", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(sourceName, "Styles", StringComparison.OrdinalIgnoreCase))
                {
                    return sourceName;
                }
                // issue #29: an SDPanel has TWO ISource virtual parts — SDEvents (the event
                // code) and SDRules. Collapsing both to "Source" hid SDEvents entirely and
                // made "Source" resolve to whichever came first (SDRules → always empty).
                // Keep the SD-prefixed source parts individually addressable.
                if (!string.IsNullOrEmpty(sourceName) &&
                    sourceName.StartsWith("SD", StringComparison.Ordinal))
                {
                    return sourceName;
                }

                return "Source";
            }

            if (part.GetType().Name.Equals("VariablesPart", StringComparison.OrdinalIgnoreCase))
            {
                return "Variables";
            }

            string concreteName = part.GetType().Name ?? string.Empty;
            if (concreteName.IndexOf("ThemeStylesPart", StringComparison.OrdinalIgnoreCase) >= 0)
                return "ThemeStyles";
            if (concreteName.IndexOf("DesignStylesPart", StringComparison.OrdinalIgnoreCase) >= 0)
                return "StyleSheet";

            if (!string.IsNullOrWhiteSpace(part.TypeDescriptor?.Name))
            {
                return part.TypeDescriptor.Name;
            }

            return part.GetType().Name.Replace("Part", "");
        }
    }
}
