using System;
using System.Diagnostics;
using Microsoft.Win32;
using System.IO;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    internal sealed class WorkerSdkCompatibilityProbeResult
    {
        internal string? InstallationPath { get; init; }
        internal string? Version { get; init; }
        internal string? Major { get; init; }
        internal string? Driver { get; init; }
        internal string Status { get; init; } = "unavailable";
        internal string Code { get; init; } = "GXMCP_SDK_VERSION_UNDETECTED";
        internal string Diagnostic { get; init; } = string.Empty;

        internal bool IsCompatible => string.Equals(Status, "compatible", StringComparison.Ordinal);
        internal bool IsRejected => string.Equals(Status, "incompatible", StringComparison.Ordinal);

        internal static bool TryFindLegacyProvider(string? major, out string? provider)
        {
            provider = null;
            if (!OperatingSystem.IsWindows()) return false;

            string[] candidates = string.Equals(major, "8", StringComparison.Ordinal)
                ? new[] { "GXPublic.GXPublic.4", "GXPubGXX.GXPublic.5", "GXPubGXX.GXPublic" }
                : new[] { "GXPublic.GXPublic.5", "GXPubGXX.GXPublic.5", "GXPubGXX.GXPublic", "GXPublic.GXPublic.4" };
            try
            {
                using RegistryKey classesRoot = RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, RegistryView.Registry32);
                foreach (string candidate in candidates)
                {
                    using RegistryKey? registration = classesRoot.OpenSubKey(candidate);
                    if (registration != null)
                    {
                        provider = candidate;
                        return true;
                    }
                }
            }
            catch
            {
                // The Worker remains the final authority; this probe only gives
                // the Gateway a fail-fast diagnostic before spawning a dead worker.
            }
            return false;
        }

        internal JObject ToDiagnosticObject()
        {
            JObject? capabilities = null;
            if (string.Equals(Driver, "com-gxpublic", StringComparison.OrdinalIgnoreCase))
            {
                capabilities = new JObject
                {
                    ["metadataQuery"] = "supported-after-provider-open",
                    ["metadataList"] = "supported-after-provider-open",
                    ["sourceParts"] = "unsupported-by-gxpublic-contract",
                    ["objectMutation"] = "unsupported-by-gxpublic-contract",
                    ["xpzTransfer"] = "unsupported-until-proven-by-matching-provider",
                    ["index"] = "provider-catalogue-not-native-search-index"
                };
            }

            return new JObject
            {
                ["installationPath"] = InstallationPath,
                ["version"] = Version,
                ["major"] = Major,
                ["matchedMajor"] = IsCompatible ? Major : null,
                ["driver"] = Driver,
                ["status"] = Status,
                ["code"] = Code,
                ["diagnostic"] = Diagnostic,
                ["supportedMajors"] = JArray.FromObject(GeneXusVersionCatalog.SupportedMajors),
                ["legacyMajors"] = JArray.FromObject(GeneXusVersionCatalog.LegacyMajors),
                ["supportLevel"] = string.Equals(Driver, "native-sdk", StringComparison.OrdinalIgnoreCase)
                    ? "native-sdk"
                    : (string.IsNullOrWhiteSpace(Driver) ? null : "basic-legacy"),
                ["capabilities"] = capabilities,
                ["catalogSource"] = GeneXusVersionCatalog.CatalogSource
            };
        }
    }

    /// <summary>
    /// Cheap Gateway-side mirror of the Worker SDK major gate. It deliberately checks
    /// only the explicit major catalog: the Worker remains responsible for the full
    /// manifest/fingerprint check. This probe prevents a known unsupported major from
    /// entering the worker respawn loop and gives whoami/doctor a synchronous reason.
    /// </summary>
    internal static class WorkerSdkCompatibilityProbe
    {
        private static readonly string[] ClassicAnchors = { "gxw32.exe", "gx.exe", "gxdl32.dll" };

        [ThreadStatic]
        internal static Func<string, string?>? FileVersionReader;

        internal static WorkerSdkCompatibilityProbeResult Check(string? installationPath)
        {
            if (string.IsNullOrWhiteSpace(installationPath) || !Directory.Exists(installationPath))
            {
                return new WorkerSdkCompatibilityProbeResult
                {
                    InstallationPath = installationPath,
                    Status = "unavailable",
                    Code = "GXMCP_SDK_PATH_MISSING",
                    Diagnostic = "GXMCP_SDK_PATH_MISSING path="
                        + (string.IsNullOrWhiteSpace(installationPath) ? "<missing>" : installationPath)
                };
            }

            string? version = ReadAnchorVersion(installationPath);
            if (string.IsNullOrWhiteSpace(version))
                version = Program.DetectGeneXusVersion(installationPath);

            return Evaluate(installationPath, version);
        }

        internal static WorkerSdkCompatibilityProbeResult Evaluate(string installationPath, string? version)
        {

            string? major = GeneXusVersionCatalog.GetMajor(version);
            if (string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(major))
            {
                return new WorkerSdkCompatibilityProbeResult
                {
                    InstallationPath = installationPath,
                    Version = version,
                    Major = major,
                    Status = "unavailable",
                    Code = "GXMCP_SDK_VERSION_UNDETECTED",
                    Diagnostic = "GXMCP_SDK_VERSION_UNDETECTED path=" + installationPath
                };
            }

            if (GeneXusVersionCatalog.IsLegacyMajor(major))
            {
                string? driver = GeneXusVersionCatalog.GetDriverProfile(major);
                return new WorkerSdkCompatibilityProbeResult
                {
                    InstallationPath = installationPath,
                    Version = version,
                    Major = major,
                    Driver = driver,
                    Status = "compatible",
                    Code = "GXMCP_SDK_LEGACY_COMPATIBLE",
                    Diagnostic = "GXMCP_SDK_LEGACY_COMPATIBLE version=" + version
                        + " major=" + major
                        + " driver=" + driver
                };
            }

            if (GeneXusVersionCatalog.IsSupported(version))
            {
                return new WorkerSdkCompatibilityProbeResult
                {
                    InstallationPath = installationPath,
                    Version = version,
                    Major = major,
                    Driver = "native-sdk",
                    Status = "compatible",
                    Code = "GXMCP_SDK_COMPATIBLE",
                    Diagnostic = "GXMCP_SDK_COMPATIBLE version=" + version
                        + " major=" + major
                        + " supportedMajors=" + GeneXusVersionCatalog.SupportedMajorsDisplay
                };
            }

            return new WorkerSdkCompatibilityProbeResult
            {
                InstallationPath = installationPath,
                Version = version,
                Major = major,
                Status = "incompatible",
                Code = "GXMCP_SDK_VERSION_MISMATCH",
                Diagnostic = "GXMCP_SDK_VERSION_MISMATCH expectedMajors="
                    + GeneXusVersionCatalog.SupportedMajorsDisplay
                    + " actualVersion=" + version
            };
        }

        private static string? ReadAnchorVersion(string installationPath)
        {
            try
            {
                string anchor = Path.Combine(installationPath, "Artech.Architecture.Common.dll");
                if (File.Exists(anchor))
                {
                    string? version = GetFileVersion(anchor);
                    if (!string.IsNullOrWhiteSpace(version))
                    {
                        return version;
                    }
                }

                foreach (string classicAnchor in ClassicAnchors)
                {
                    string classicPath = Path.Combine(installationPath, classicAnchor);
                    if (File.Exists(classicPath))
                    {
                        string? version = GetFileVersion(classicPath);
                        if (!string.IsNullOrWhiteSpace(version))
                        {
                            return version;
                        }
                    }
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        internal static string? ReadArtechArchitectureVersion(string? installationPath)
        {
            if (string.IsNullOrWhiteSpace(installationPath)) return null;
            try
            {
                string[] assemblies =
                {
                    "Artech.Architecture.Common.dll",
                    "Artech.Architecture.BL.Framework.dll",
                    "Artech.Architecture.UI.Framework.dll"
                };
                foreach (string assembly in assemblies)
                {
                    string fullPath = Path.Combine(installationPath, assembly);
                    if (File.Exists(fullPath))
                    {
                        string? version = GetFileVersion(fullPath);
                        if (!string.IsNullOrWhiteSpace(version)) return version;
                    }
                }
            }
            catch
            {
            }
            return null;
        }

        internal static string? FindRegisteredLegacyProviders(string? major)
        {
            if (!OperatingSystem.IsWindows()) return null;
            string[] candidates = string.Equals(major, "8", StringComparison.Ordinal)
                ? new[] { "GXPublic.GXPublic.4", "GXPubGXX.GXPublic.5", "GXPubGXX.GXPublic" }
                : new[] { "GXPublic.GXPublic.5", "GXPubGXX.GXPublic.5", "GXPubGXX.GXPublic", "GXPublic.GXPublic.4" };
            var found = new System.Collections.Generic.List<string>();
            try
            {
                using RegistryKey classesRoot = RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, RegistryView.Registry32);
                foreach (string candidate in candidates)
                {
                    using RegistryKey? registration = classesRoot.OpenSubKey(candidate);
                    if (registration != null) found.Add(candidate);
                }
            }
            catch
            {
            }
            return found.Count > 0 ? string.Join(", ", found) : null;
        }

        private static string? GetFileVersion(string filePath)
        {
            if (FileVersionReader != null)
            {
                string? mocked = FileVersionReader(filePath);
                if (mocked != null) return string.IsNullOrWhiteSpace(mocked) ? null : mocked.Trim();
            }

            var info = FileVersionInfo.GetVersionInfo(filePath);
            string? version = string.IsNullOrWhiteSpace(info.ProductVersion) ? info.FileVersion : info.ProductVersion;
            return string.IsNullOrWhiteSpace(version) ? null : version.Trim();
        }
    }
}
