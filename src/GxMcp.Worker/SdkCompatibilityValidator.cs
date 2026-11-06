using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker
{
    public sealed class SdkCompatibilityResult
    {
        public bool IsCompatible { get; private set; }
        public string Code { get; private set; }
        public string Diagnostic { get; private set; }

        internal SdkCompatibilityResult(bool compatible, string code, string diagnostic)
        {
            IsCompatible = compatible;
            Code = code;
            Diagnostic = diagnostic;
        }
    }

    public static class SdkCompatibilityValidator
    {
        public static SdkCompatibilityResult Validate(string sdkPath, string manifestPath)
        {
            return Validate(sdkPath, manifestPath, FileVersionInfoFor);
        }

        public static SdkCompatibilityResult Validate(string sdkPath, string manifestPath, Func<string, string> versionReader)
        {
            if (string.IsNullOrWhiteSpace(sdkPath) || !Directory.Exists(sdkPath))
                return Fail("GXMCP_SDK_PATH_MISSING", "GXMCP_SDK_PATH_MISSING path=<missing>");
            if (string.IsNullOrWhiteSpace(manifestPath) || !File.Exists(manifestPath))
                return Fail("GXMCP_SDK_MANIFEST_MISSING", "GXMCP_SDK_MANIFEST_MISSING manifest=" + (manifestPath ?? "<missing>"));

            JObject manifest;
            try { manifest = JObject.Parse(File.ReadAllText(manifestPath)); }
            catch (Exception ex) { return Fail("GXMCP_SDK_MANIFEST_INVALID", "GXMCP_SDK_MANIFEST_INVALID manifest=" + manifestPath + " error=" + ex.GetType().Name); }

            string expectedVersion = (string)manifest["supportedVersion"];
            if (!TryParseMajor(expectedVersion, out string expectedMajor))
                return Fail("GXMCP_SDK_MANIFEST_INVALID", "GXMCP_SDK_MANIFEST_INVALID manifest=" + manifestPath + " error=supportedVersion");

            if (!TryLoadSupportedMajors(manifestPath, out string catalogPath, out HashSet<string> supportedMajors, out string catalogError))
                return Fail(catalogError, catalogError + " catalog=" + catalogPath);

            string anchor = (string)manifest["anchor"];
            string anchorPath = Path.Combine(sdkPath, anchor ?? string.Empty);
            if (string.IsNullOrWhiteSpace(anchor) || !File.Exists(anchorPath))
                return Fail("GXMCP_SDK_ANCHOR_MISSING", "GXMCP_SDK_ANCHOR_MISSING path=" + (anchor ?? "<missing>"));

            string actualVersion = versionReader(anchorPath);
            bool exactVersion = string.Equals(expectedVersion, actualVersion, StringComparison.OrdinalIgnoreCase);
            if (!TryParseMajor(actualVersion, out string actualMajor)
                || !supportedMajors.Contains(actualMajor)
                || SdkBinaryLine(actualMajor) != SdkBinaryLine(expectedMajor))
                return Fail("GXMCP_SDK_VERSION_MISMATCH", "GXMCP_SDK_VERSION_MISMATCH expectedVersion=" + expectedVersion + " actualVersion=" + actualVersion + " supportedMajors=" + JoinMajors(supportedMajors));

            var assemblies = manifest["assemblies"] as JArray;
            if (assemblies == null || assemblies.Count == 0)
                return Fail("GXMCP_SDK_MANIFEST_INVALID", "GXMCP_SDK_MANIFEST_INVALID manifest=" + manifestPath + " error=assemblies");
            var fingerprintDrift = new List<string>();
            foreach (var token in assemblies)
            {
                string relativePath = (string)token["path"];
                string expectedHash = (string)token["sha256"];
                bool optional = (bool?)token["optional"] == true;
                string filePath = Path.Combine(sdkPath, relativePath ?? string.Empty);
                if (string.IsNullOrWhiteSpace(relativePath) || !File.Exists(filePath))
                {
                    if (optional) continue;
                    return Fail("GXMCP_SDK_ASSEMBLY_MISSING", "GXMCP_SDK_ASSEMBLY_MISSING path=" + (relativePath ?? "<missing>"));
                }
                string actualHash = Sha256(filePath);
                if (!string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
                    fingerprintDrift.Add("GXMCP_SDK_FINGERPRINT_DRIFT path=" + relativePath + " expectedSha256=" + expectedHash + " actualSha256=" + actualHash);
            }
            string versionDiagnostic = exactVersion
                ? expectedVersion
                : expectedMajor == actualMajor
                    ? expectedVersion + " actualVersion=" + actualVersion + " (compatible major; patch/build drift)"
                    : expectedVersion + " actualVersion=" + actualVersion + " (supported major " + actualMajor + "; reference major " + expectedMajor + ")";
            string diagnostic = "GXMCP_SDK_COMPATIBLE version=" + versionDiagnostic + " supportedMajors=" + JoinMajors(supportedMajors) + " assemblies=" + assemblies.Count;
            if (fingerprintDrift.Count > 0) diagnostic += "\n" + string.Join("\n", fingerprintDrift);
            return new SdkCompatibilityResult(true, "GXMCP_SDK_COMPATIBLE", diagnostic);
        }

        // GeneXus X Evolution 1-3 report their major as "10.<n>"; GeneXus 15 onwards as a
        // single number.
        private static bool TryParseMajor(string version, out string major)
        {
            major = null;
            if (string.IsNullOrWhiteSpace(version)) return false;
            string[] parts = version.Trim().Split('.');
            if (!int.TryParse(parts[0], out int first) || first <= 0) return false;
            if (first == 10)
            {
                if (parts.Length < 2 || !int.TryParse(parts[1], out int minor) || minor < 1 || minor > 3) return false;
                major = "10." + minor;
                return true;
            }
            major = first.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return true;
        }

        // GeneXus 16-18 share one SDK binary line, so a single Worker build serves them all.
        // Evolution 3 ships assemblies 10.1.0.0: only a Worker built against that SDK may load it.
        private static string SdkBinaryLine(string major)
        {
            return major != null && major.StartsWith("10.", StringComparison.Ordinal) ? major : "modern";
        }

        private static bool TryLoadSupportedMajors(string manifestPath, out string catalogPath, out HashSet<string> supportedMajors, out string error)
        {
            supportedMajors = new HashSet<string>(StringComparer.Ordinal);
            string manifestDirectory = Path.GetDirectoryName(Path.GetFullPath(manifestPath)) ?? string.Empty;
            var candidates = new List<string>();
            string configuredCatalog = Environment.GetEnvironmentVariable("GXMCP_VERSION_CATALOG");
            if (!string.IsNullOrWhiteSpace(configuredCatalog))
            {
                configuredCatalog = configuredCatalog.Trim().Trim('"');
                candidates.Add(Path.IsPathRooted(configuredCatalog) ? configuredCatalog : Path.Combine(manifestDirectory, configuredCatalog));
            }
            candidates.Add(Path.Combine(manifestDirectory, "gx-versions.json"));
            candidates.Add(Path.Combine(manifestDirectory, "config", "gx-versions.json"));
            candidates.Add(Path.Combine(manifestDirectory, "..", "..", "config", "gx-versions.json"));

            catalogPath = null;
            foreach (string candidate in candidates)
            {
                if (File.Exists(candidate))
                {
                    catalogPath = Path.GetFullPath(candidate);
                    break;
                }
            }
            if (catalogPath == null)
            {
                catalogPath = string.Join(";", candidates);
                error = "GXMCP_SDK_CATALOG_MISSING";
                return false;
            }

            try
            {
                var catalog = JObject.Parse(File.ReadAllText(catalogPath));
                var entries = catalog["supportedMajors"] as JArray;
                if (entries != null)
                {
                    foreach (var entry in entries)
                    {
                        if (TryParseMajor((string)entry["major"], out string major)) supportedMajors.Add(major);
                    }
                }
            }
            catch
            {
                error = "GXMCP_SDK_CATALOG_INVALID";
                return false;
            }
            if (supportedMajors.Count == 0)
            {
                error = "GXMCP_SDK_CATALOG_INVALID";
                return false;
            }

            error = null;
            return true;
        }

        private static string JoinMajors(HashSet<string> majors)
        {
            var ordered = new List<string>(majors);
            ordered.Sort((a, b) => double.Parse(a, System.Globalization.CultureInfo.InvariantCulture)
                .CompareTo(double.Parse(b, System.Globalization.CultureInfo.InvariantCulture)));
            return string.Join(",", ordered);
        }

        private static SdkCompatibilityResult Fail(string code, string diagnostic)
        {
            return new SdkCompatibilityResult(false, code, diagnostic);
        }

        private static string Sha256(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        private static string FileVersionInfoFor(string path)
        {
            var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
            return info.ProductVersion ?? string.Empty;
        }
    }
}
