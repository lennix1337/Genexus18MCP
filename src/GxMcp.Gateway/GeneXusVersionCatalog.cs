using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    /// <summary>
    /// The explicit compatibility contract for GeneXus installations.
    /// Add a major to config/gx-versions.json only after the Worker has a
    /// build/runtime validation path for it.
    /// </summary>
    internal static class GeneXusVersionCatalog
    {
        private sealed class CatalogData
        {
            internal string PrimaryMajor { get; set; } = "18";
            internal IReadOnlyList<string> SupportedMajors { get; set; } = new[] { "16", "17", "18" };
            internal IReadOnlyList<string> LegacyMajors { get; set; } = new[] { "10.3", "10.2", "10.1", "15", "9", "8" };
            internal Dictionary<string, string> LegacyDriverMap { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["10.3"] = "dotnet-reflection",
                ["10.2"] = "dotnet-reflection",
                ["10.1"] = "dotnet-reflection",
                ["15"] = "dotnet-reflection",
                ["9"] = "com-gxpublic",
                ["8"] = "com-gxpublic"
            };
            internal string PrimaryInstallPath { get; set; } = @"C:\Program Files (x86)\GeneXus\GeneXus18";
            internal string Source { get; set; } = "built-in-fallback";
            internal JArray Entries { get; set; } = new JArray();
            internal JArray LegacyEntries { get; set; } = new JArray();
        }

        private static readonly CatalogData Data = Load();

        internal static string PrimaryMajor => Data.PrimaryMajor;
        internal static IReadOnlyList<string> SupportedMajors => Data.SupportedMajors;
        internal static IReadOnlyList<string> LegacyMajors => Data.LegacyMajors;
        internal static string PrimaryInstallPath => Data.PrimaryInstallPath;
        internal static string CatalogSource => Data.Source;

        private static readonly Regex MajorRegex =
            new Regex(@"^\s*(?<major>10\.[1-3](?!\d)|\d+)", RegexOptions.Compiled);

        internal static string SupportedMajorsDisplay
        {
            get { return string.Join(", ", SupportedMajors); }
        }

        internal static string? GetMajor(string? version)
        {
            if (string.IsNullOrWhiteSpace(version)) return null;

            Match match = MajorRegex.Match(version);
            return match.Success ? match.Groups["major"].Value : null;
        }

        internal static bool IsSupported(string? version)
        {
            string? major = GetMajor(version);
            return IsSupportedMajor(major);
        }

        internal static bool IsLegacyMajor(string? major)
        {
            if (string.IsNullOrWhiteSpace(major)) return false;

            string normalized = GetMajor(major) ?? major.Trim();
            for (int i = 0; i < LegacyMajors.Count; i++)
            {
                if (string.Equals(LegacyMajors[i], normalized, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        internal static bool IsSupportedOrLegacy(string? version)
        {
            if (string.IsNullOrWhiteSpace(version)) return false;
            string? major = GetMajor(version);
            return IsSupportedMajor(major) || IsLegacyMajor(major);
        }

        internal static string? GetDriverProfile(string? major)
        {
            if (string.IsNullOrWhiteSpace(major)) return null;

            string normalized = GetMajor(major) ?? major.Trim();
            if (IsSupportedMajor(normalized))
            {
                return "native-sdk";
            }

            if (Data.LegacyDriverMap.TryGetValue(normalized, out string? legacyDriver))
            {
                return legacyDriver;
            }

            return null;
        }

        internal static string? GetMatchingMajor(string? version)
        {
            string? major = GetMajor(version);
            return IsSupportedMajor(major) ? major : null;
        }

        private static bool IsSupportedMajor(string? major)
        {
            if (string.IsNullOrEmpty(major)) return false;

            for (int i = 0; i < SupportedMajors.Count; i++)
                if (string.Equals(SupportedMajors[i], major, StringComparison.Ordinal)) return true;

            return false;
        }

        internal static JObject ToDiagnosticObject()
        {
            return ToDiagnosticObject(includeEntryDetail: true);
        }

        /// <summary>
        /// Diagnostic projection of the compatibility catalog.
        /// <paramref name="includeEntryDetail"/> adds the per-major entries
        /// (displayName/driver/defaultInstallPath/registry names) that KB
        /// provisioning needs to resolve an install path.
        /// Hot-path callers such as genexus_whoami pass false: that detail is
        /// static for the whole session and is already reachable on demand
        /// through genexus_whoami(verbose=true), so echoing it on every health
        /// check only inflates the response. The identity fields the whoami
        /// contract exposes (source/primaryMajor/supportedMajors/legacyMajors)
        /// stay present either way.
        /// </summary>
        internal static JObject ToDiagnosticObject(bool includeEntryDetail)
        {
            var result = new JObject
            {
                ["source"] = CatalogSource,
                ["primaryMajor"] = PrimaryMajor,
                ["supportedMajors"] = JArray.FromObject(SupportedMajors),
                ["legacyMajors"] = JArray.FromObject(LegacyMajors)
            };

            if (includeEntryDetail)
            {
                result["entries"] = Data.Entries.DeepClone();
                result["legacyEntries"] = Data.LegacyEntries.DeepClone();
            }
            else
            {
                result["entryDetail"] = "genexus_whoami(verbose=true)";
            }

            return result;
        }

        private static CatalogData Load()
        {
            string[] candidates = GetCatalogCandidates();
            foreach (string path in candidates)
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    JObject document = JObject.Parse(File.ReadAllText(path));
                    string? primary = document["primaryMajor"]?.ToString();
                    var entries = document["supportedMajors"] as JArray;
                    if (string.IsNullOrWhiteSpace(primary) || entries == null || entries.Count == 0) continue;

                    var majors = entries
                        .OfType<JObject>()
                        .Select(entry => entry["major"]?.ToString())
                        .Where(major => !string.IsNullOrWhiteSpace(major))
                        .Select(major => major!)
                        .Distinct(StringComparer.Ordinal)
                        .ToArray();
                    if (majors.Length == 0 || !majors.Contains(primary, StringComparer.Ordinal)) continue;

                    string? primaryPath = entries
                        .OfType<JObject>()
                        .Where(entry => string.Equals(entry["major"]?.ToString(), primary, StringComparison.Ordinal))
                        .Select(entry => entry["defaultInstallPath"]?.ToString())
                        .FirstOrDefault(pathValue => !string.IsNullOrWhiteSpace(pathValue));
                    if (string.IsNullOrWhiteSpace(primaryPath)) continue;

                    var legacyEntries = document["legacyMajors"] as JArray;
                    var legacyMajorsList = new List<string>();
                    var legacyDriverMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                    if (legacyEntries != null)
                    {
                        foreach (var legObj in legacyEntries.OfType<JObject>())
                        {
                            string? legMajor = legObj["major"]?.ToString()?.Trim();
                            if (!string.IsNullOrEmpty(legMajor))
                            {
                                if (!legacyMajorsList.Contains(legMajor, StringComparer.Ordinal))
                                    legacyMajorsList.Add(legMajor);

                                string? driver = legObj["driver"]?.ToString()?.Trim();
                                if (!string.IsNullOrEmpty(driver))
                                {
                                    legacyDriverMap[legMajor] = driver;
                                }
                            }
                        }
                    }

                    return new CatalogData
                    {
                        PrimaryMajor = primary,
                        SupportedMajors = majors,
                        LegacyMajors = legacyMajorsList.ToArray(),
                        LegacyDriverMap = legacyDriverMap,
                        PrimaryInstallPath = primaryPath,
                        Source = path,
                        Entries = entries.DeepClone() as JArray ?? new JArray(),
                        LegacyEntries = legacyEntries?.DeepClone() as JArray ?? new JArray()
                    };
                }
                catch
                {
                    // A broken optional catalog must not prevent the Gateway from
                    // starting. The diagnostic source exposes that fallback was used.
                }
            }

            return new CatalogData
            {
                Entries = new JArray
                {
                    new JObject { ["major"] = "16", ["displayName"] = "GeneXus 16" },
                    new JObject { ["major"] = "17", ["displayName"] = "GeneXus 17" },
                    new JObject { ["major"] = "18", ["displayName"] = "GeneXus 18" }
                },
                LegacyEntries = new JArray
                {
                    new JObject { ["major"] = "10.3", ["displayName"] = "GeneXus Evolution 3", ["driver"] = "dotnet-reflection" },
                    new JObject { ["major"] = "10.2", ["displayName"] = "GeneXus Evolution 2", ["driver"] = "dotnet-reflection" },
                    new JObject { ["major"] = "10.1", ["displayName"] = "GeneXus Evolution 1", ["driver"] = "dotnet-reflection" },
                    new JObject { ["major"] = "15", ["displayName"] = "GeneXus 15", ["driver"] = "dotnet-reflection" },
                    new JObject { ["major"] = "9", ["displayName"] = "GeneXus 9.0", ["driver"] = "com-gxpublic" },
                    new JObject { ["major"] = "8", ["displayName"] = "GeneXus 8.0", ["driver"] = "com-gxpublic" }
                }
            };
        }

        private static string[] GetCatalogCandidates()
        {
            var paths = new List<string>();
            string? configured = Environment.GetEnvironmentVariable("GXMCP_VERSION_CATALOG");
            if (!string.IsNullOrWhiteSpace(configured)) paths.Add(configured);

            string baseDirectory = AppContext.BaseDirectory;
            paths.Add(Path.Combine(baseDirectory, "config", "gx-versions.json"));
            paths.Add(Path.Combine(baseDirectory, "gx-versions.json"));

            string current = Directory.GetCurrentDirectory();
            paths.Add(Path.Combine(current, "config", "gx-versions.json"));

            return paths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path =>
                {
                    try { return Path.GetFullPath(path); }
                    catch { return path; }
                })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }
}
