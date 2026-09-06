using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// Item 14 — curated SDK introspection. Where <see cref="SdkSurfaceProbe"/> dumps the
    /// full surface to disk, this service answers in-memory questions like
    /// "what methods does Form expose?" without writing files. Results are cached
    /// for the worker's lifetime since the SDK assembly does not change at runtime.
    ///
    /// Action: list-methods type=&lt;name&gt; → { type, fullName, methods:[{name, signature, returnType, oneLineDoc}] }
    /// Action: list-types                  → { types:[{name, fullName, assembly, methodCount, propCount}] }
    /// </summary>
    public class SdkProbeService
    {
        // Curated short-name → candidate full names. The agent passes a short name
        // (Form, WebPanel, Transaction, SDT, Application) and we resolve to the
        // first matching public type across loaded Artech/GeneXus/DVelop assemblies.
        // Multiple candidates are common (Form lives both in WinForms-flavoured and
        // Web-flavoured SDK namespaces); we return ALL matches for transparency.
        private static readonly Dictionary<string, string[]> CuratedAliases =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["Form"] = new[] { "Form" },
                ["WebPanel"] = new[] { "WebPanel" },
                ["Transaction"] = new[] { "Transaction" },
                ["SDT"] = new[] { "SDT", "StructuredDataType" },
                ["Application"] = new[] { "Application", "GenexusApplication" },
                ["Procedure"] = new[] { "Procedure" },
                ["DataProvider"] = new[] { "DataProvider" },
                ["KnowledgeBase"] = new[] { "KnowledgeBase" },
                ["KBObject"] = new[] { "KBObject" },
                ["Window"] = new[] { "Window" },
                ["Theme"] = new[] { "Theme" },
                ["Domain"] = new[] { "Domain" },
                ["Attribute"] = new[] { "Attribute", "AttributeData" }
            };

        // Cached: full assembly scan keyed by short type name (case-insensitive).
        // The cache is built lazily on the first list-methods call.
        private static readonly ConcurrentDictionary<string, JObject> _methodCache =
            new(StringComparer.OrdinalIgnoreCase);

        private static JObject _typesIndex;
        private static readonly object _typesIndexLock = new();

        private static readonly string[] AssemblyPrefixes = new[]
        {
            "Artech.", "Genexus.", "DVelop.", "GeneXus."
        };

        private sealed class CapabilityDefinition
        {
            public string Name;
            public string[] TypeAliases;
            public bool Deferred;
            public string DeferredReason;
        }

        private static readonly CapabilityDefinition[] CapabilityDefinitions =
        {
            new CapabilityDefinition { Name = "authoring.attribute_properties", TypeAliases = new[] { "Attribute", "KBObject" } },
            new CapabilityDefinition { Name = "authoring.transaction", TypeAliases = new[] { "Transaction" } },
            new CapabilityDefinition { Name = "authoring.sdt", TypeAliases = new[] { "SDT", "StructuredDataType" } },
            new CapabilityDefinition { Name = "authoring.patterns", TypeAliases = new[] { "Pattern", "PatternInstance" } },
            new CapabilityDefinition { Name = "authoring.layout", TypeAliases = new[] { "Form", "WebPanel" } },
            new CapabilityDefinition { Name = "authoring.translations", TypeAliases = new[] { "Translation", "Translations" } },
            new CapabilityDefinition
            {
                Name = "data.business_components",
                TypeAliases = new string[0],
                Deferred = true,
                DeferredReason = "Business Components execute in a generated application runtime, not the design-time SDK worker."
            }
        };

        /// <summary>
        /// Curated short-name list returned by <c>action=list-types</c>. The agent
        /// is free to call <c>action=list-methods type=&lt;anyName&gt;</c> with a name
        /// that is not in this list; the resolver still walks the loaded SDK.
        /// </summary>
        public static IReadOnlyCollection<string> CuratedNames => CuratedAliases.Keys;

        public string ListTypes()
        {
            lock (_typesIndexLock)
            {
                if (_typesIndex != null) return _typesIndex.ToString(Newtonsoft.Json.Formatting.None);

                var arr = new JArray();
                foreach (var alias in CuratedAliases)
                {
                    foreach (var match in ResolveTypes(alias.Value))
                    {
                        arr.Add(new JObject
                        {
                            ["alias"] = alias.Key,
                            ["name"] = match.Name,
                            ["fullName"] = match.FullName,
                            ["assembly"] = match.Assembly.GetName().Name,
                            ["methodCount"] = SafeCount(() => match.GetMethods(PublicFlags).Count(m => !m.IsSpecialName)),
                            ["propertyCount"] = SafeCount(() => match.GetProperties(PublicFlags).Length)
                        });
                    }
                }

                _typesIndex = new JObject
                {
                    ["types"] = arr,
                    ["aliases"] = new JArray(CuratedAliases.Keys.ToArray()),
                    ["hint"] = "Call genexus_sdk_probe { action: 'list-methods', type: '<alias|FullName>' } for per-type signatures."
                };
                return _typesIndex.ToString(Newtonsoft.Json.Formatting.None);
            }
        }

        public string ListMethods(string typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName))
            {
                return Err("type is required.",
                    "Pass action='list-types' to enumerate curated names, or supply a FullName like 'Artech.Genexus.Common.Objects.Transaction'.");
            }

            string cacheKey = typeName.Trim();
            if (_methodCache.TryGetValue(cacheKey, out var cached))
                return cached.ToString(Newtonsoft.Json.Formatting.None);

            // Resolve: 1) curated alias, 2) FullName, 3) Name substring across loaded SDK.
            string[] candidates;
            if (CuratedAliases.TryGetValue(cacheKey, out var aliasCandidates))
                candidates = aliasCandidates;
            else
                candidates = new[] { cacheKey };

            var resolved = ResolveTypes(candidates).ToList();
            if (resolved.Count == 0)
            {
                var err = new JObject
                {
                    ["error"] = $"No SDK type matched '{typeName}'.",
                    ["hint"] = "Try the curated aliases listed by action='list-types', or a full SDK type FullName.",
                    ["curatedAliases"] = new JArray(CuratedAliases.Keys.ToArray())
                };
                return err.ToString(Newtonsoft.Json.Formatting.None);
            }

            var matches = new JArray();
            foreach (var t in resolved)
            {
                matches.Add(DumpType(t));
            }

            var payload = new JObject
            {
                ["query"] = typeName,
                ["matches"] = matches,
                ["cachedForLifetime"] = true,
                ["note"] = matches.Count > 1
                    ? "Multiple SDK types matched; first match is usually the runtime-loaded one."
                    : null
            };

            _methodCache[cacheKey] = payload;
            return payload.ToString(Newtonsoft.Json.Formatting.None);
        }

        /// <summary>
        /// Returns a stable, read-only capability matrix. A reflected type only
        /// proves that a signature is present; persistence and IDE parity remain
        /// explicitly unverified until a fixture test certifies them.
        /// </summary>
        public string Capabilities()
        {
            var capabilities = new JArray();
            foreach (var definition in CapabilityDefinitions)
            {
                var matches = definition.Deferred
                    ? new List<Type>()
                    : ResolveTypes(definition.TypeAliases).ToList();
                string status = definition.Deferred
                    ? "deferred"
                    : matches.Count == 0 ? "unavailable" : "available_unverified";

                var evidence = new JObject
                {
                    ["kind"] = definition.Deferred ? "runtime_boundary" : "signature_probe",
                    ["persistenceVerified"] = false
                };
                if (!string.IsNullOrWhiteSpace(definition.DeferredReason)) evidence["reason"] = definition.DeferredReason;
                if (matches.Count > 0)
                {
                    evidence["matchedTypes"] = new JArray(matches
                        .Select(type => type.FullName)
                        .Where(name => !string.IsNullOrWhiteSpace(name))
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(name => name, StringComparer.Ordinal));
                }

                capabilities.Add(new JObject
                {
                    ["capability"] = definition.Name,
                    ["status"] = status,
                    ["scope"] = new JObject { ["designTimeSdk"] = !definition.Deferred },
                    ["evidence"] = evidence
                });
            }

            string sdkVersion = AppDomain.CurrentDomain.GetAssemblies()
                .Where(assembly =>
                {
                    try { return AssemblyPrefixes.Any(prefix => assembly.GetName().Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)); }
                    catch { return false; }
                })
                .Select(assembly =>
                {
                    try { return assembly.GetName().Version?.ToString(); }
                    catch { return null; }
                })
                .Where(version => !string.IsNullOrWhiteSpace(version))
                .OrderByDescending(version => version, StringComparer.Ordinal)
                .FirstOrDefault();

            return new JObject
            {
                ["schemaVersion"] = "genexus-sdk-capabilities/1",
                ["installedSdkVersion"] = sdkVersion ?? "unknown",
                ["capabilities"] = capabilities,
                ["note"] = "Signature availability is not proof of a successful save. Run the certified fixture before enabling an authoring path."
            }.ToString(Newtonsoft.Json.Formatting.None);
        }

        private const BindingFlags PublicFlags =
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        private static IEnumerable<Type> ResolveTypes(string[] candidates)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                string asmName = asm.GetName().Name ?? string.Empty;
                if (!AssemblyPrefixes.Any(p => asmName.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                    continue;

                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException rtle) { types = rtle.Types.Where(t => t != null).ToArray(); }
                catch { continue; }

                foreach (var t in types)
                {
                    if (t == null || (!t.IsPublic && !t.IsNestedPublic)) continue;
                    foreach (var cand in candidates)
                    {
                        if (string.IsNullOrEmpty(cand)) continue;
                        bool match =
                            string.Equals(t.Name, cand, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(t.FullName, cand, StringComparison.OrdinalIgnoreCase);
                        if (!match) continue;
                        if (seen.Add(t.FullName ?? t.Name))
                            yield return t;
                    }
                }
            }
        }

        private static JObject DumpType(Type t)
        {
            var doc = XmlDocLookup.ForAssembly(t.Assembly);
            var methods = new JArray();
            try
            {
                foreach (var m in t.GetMethods(PublicFlags).OrderBy(x => x.Name, StringComparer.Ordinal))
                {
                    if (m.IsSpecialName) continue; // skip get_/set_/add_/remove_
                    string scope = m.IsStatic ? "static " : string.Empty;
                    string sig = scope + m.Name + "(" +
                        string.Join(", ", m.GetParameters()
                            .Select(p => SafeTypeName(p.ParameterType) + " " + p.Name)) +
                        ") -> " + SafeTypeName(m.ReturnType);

                    methods.Add(new JObject
                    {
                        ["name"] = m.Name,
                        ["signature"] = sig,
                        ["returnType"] = SafeTypeName(m.ReturnType),
                        ["oneLineDoc"] = doc?.LookupMethod(m) ?? string.Empty
                    });
                }
            }
            catch { /* tolerate reflection failures on individual members */ }

            var props = new JArray();
            try
            {
                foreach (var p in t.GetProperties(PublicFlags).OrderBy(x => x.Name, StringComparer.Ordinal))
                {
                    props.Add(new JObject
                    {
                        ["name"] = p.Name,
                        ["type"] = SafeTypeName(p.PropertyType),
                        ["canWrite"] = p.CanWrite,
                        ["oneLineDoc"] = doc?.LookupProperty(p) ?? string.Empty
                    });
                }
            }
            catch { }

            return new JObject
            {
                ["name"] = t.Name,
                ["fullName"] = t.FullName,
                ["assembly"] = t.Assembly.GetName().Name,
                ["isAbstract"] = t.IsAbstract,
                ["isInterface"] = t.IsInterface,
                ["baseType"] = t.BaseType?.FullName,
                ["methods"] = methods,
                ["properties"] = props
            };
        }

        private static string SafeTypeName(Type t)
        {
            if (t == null) return "?";
            try
            {
                if (t.IsGenericType)
                {
                    var def = t.GetGenericTypeDefinition().Name;
                    var args = string.Join(",", t.GetGenericArguments().Select(SafeTypeName));
                    return def + "<" + args + ">";
                }
                return t.Name;
            }
            catch { return "?"; }
        }

        private static int SafeCount(Func<int> f)
        {
            try { return f(); } catch { return 0; }
        }

        private static string Err(string msg, string hint)
        {
            return new JObject { ["error"] = msg, ["hint"] = hint }
                .ToString(Newtonsoft.Json.Formatting.None);
        }

        /// <summary>
        /// Optional XML-doc lookup. GeneXus SDK assemblies rarely ship .xml doc
        /// files alongside the DLLs, but we look anyway and fall back to empty.
        /// </summary>
        private sealed class XmlDocLookup
        {
            private static readonly ConcurrentDictionary<string, XmlDocLookup> _byAssembly =
                new(StringComparer.OrdinalIgnoreCase);

            private readonly Dictionary<string, string> _summaries =
                new(StringComparer.Ordinal);

            public static XmlDocLookup ForAssembly(Assembly asm)
            {
                if (asm == null) return null;
                string key = asm.GetName().Name ?? "(anon)";
                return _byAssembly.GetOrAdd(key, _ => Build(asm));
            }

            private static XmlDocLookup Build(Assembly asm)
            {
                var lookup = new XmlDocLookup();
                try
                {
                    string location = null;
                    try { location = asm.Location; } catch { }
                    if (string.IsNullOrEmpty(location)) return lookup;
                    string xmlPath = Path.ChangeExtension(location, ".xml");
                    if (!File.Exists(xmlPath)) return lookup;

                    var doc = new XmlDocument();
                    doc.Load(xmlPath);
                    foreach (XmlNode m in doc.SelectNodes("//doc/members/member"))
                    {
                        string name = m.Attributes?["name"]?.Value;
                        if (string.IsNullOrEmpty(name)) continue;
                        string summary = m.SelectSingleNode("summary")?.InnerText?.Trim();
                        if (!string.IsNullOrEmpty(summary))
                        {
                            // Compress to one line.
                            summary = System.Text.RegularExpressions.Regex.Replace(summary, "\\s+", " ").Trim();
                            lookup._summaries[name] = summary;
                        }
                    }
                }
                catch { /* doc files are optional */ }
                return lookup;
            }

            public string LookupMethod(MethodInfo m)
            {
                if (m == null || _summaries.Count == 0) return null;
                // Best-effort: try a few candidate keys (M:Type.Method, M:Type.Method(p,...))
                string typeName = m.DeclaringType?.FullName;
                if (string.IsNullOrEmpty(typeName)) return null;
                string key1 = "M:" + typeName + "." + m.Name;
                if (_summaries.TryGetValue(key1, out var s)) return s;
                // Try with arg list.
                string args = string.Join(",", m.GetParameters()
                    .Select(p => p.ParameterType.FullName ?? p.ParameterType.Name));
                string key2 = "M:" + typeName + "." + m.Name + "(" + args + ")";
                _summaries.TryGetValue(key2, out var s2);
                return s2;
            }

            public string LookupProperty(PropertyInfo p)
            {
                if (p == null || _summaries.Count == 0) return null;
                string typeName = p.DeclaringType?.FullName;
                if (string.IsNullOrEmpty(typeName)) return null;
                string key = "P:" + typeName + "." + p.Name;
                _summaries.TryGetValue(key, out var s);
                return s;
            }
        }
    }
}
