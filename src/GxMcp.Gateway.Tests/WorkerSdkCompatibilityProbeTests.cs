using System;
using System.IO;
using Xunit;
using GxMcp.Gateway;

namespace GxMcp.Gateway.Tests
{
    public class WorkerSdkCompatibilityProbeTests
    {
        [Fact]
        public void Evaluate_AllowsEveryMajorInTheExplicitCatalog()
        {
            var result = WorkerSdkCompatibilityProbe.Evaluate("C:/GeneXus17", "17.0.4.153047");

            Assert.True(result.IsCompatible);
            Assert.False(result.IsRejected);
            Assert.Equal("GXMCP_SDK_COMPATIBLE", result.Code);
            Assert.Equal("17", result.Major);
            Assert.Equal("native-sdk", result.Driver);
            Assert.Contains("17", result.ToDiagnosticObject()["supportedMajors"]!.ToString());
            Assert.Equal("native-sdk", result.ToDiagnosticObject()["driver"]?.ToString());
            Assert.Equal("native-sdk", result.ToDiagnosticObject()["supportLevel"]?.ToString());
        }

        [Fact]
        public void Evaluate_RejectsMajorOutsideTheExplicitCatalog()
        {
            var result = WorkerSdkCompatibilityProbe.Evaluate("C:/GeneXus19", "19.0.1.0");

            Assert.False(result.IsCompatible);
            Assert.True(result.IsRejected);
            Assert.Equal("GXMCP_SDK_VERSION_MISMATCH", result.Code);
            Assert.Equal("19", result.Major);
            Assert.Null(result.Driver);
            Assert.Contains("expectedMajors=", result.Diagnostic);
            Assert.Contains("actualVersion=19.0.1.0", result.Diagnostic);
        }

        [Fact]
        public void Evaluate_Evolution3_IsANativeSdkMajor()
        {
            var result = WorkerSdkCompatibilityProbe.Evaluate("C:/GeneXusXEv3", "10.3.16.146128");

            Assert.True(result.IsCompatible);
            Assert.False(result.IsRejected);
            Assert.Equal("compatible", result.Status);
            Assert.Equal("GXMCP_SDK_COMPATIBLE", result.Code);
            Assert.Equal("10.3", result.Major);
            Assert.Equal("native-sdk", result.Driver);
            Assert.Equal("native-sdk", result.ToDiagnosticObject()["supportLevel"]?.ToString());
            Assert.Equal("10.3", result.ToDiagnosticObject()["matchedMajor"]?.ToString());
        }

        [Fact]
        public void Evaluate_LegacyMajor_Ev2_ProducesLegacyCompatibleResult()
        {
            var result = WorkerSdkCompatibilityProbe.Evaluate("C:/GeneXusXEv2", "10.2.0.50000");

            Assert.True(result.IsCompatible);
            Assert.Equal("GXMCP_SDK_LEGACY_COMPATIBLE", result.Code);
            Assert.Equal("10.2", result.Major);
            Assert.Equal("dotnet-reflection", result.Driver);
            Assert.Equal("basic-legacy", result.ToDiagnosticObject()["supportLevel"]?.ToString());
            Assert.Contains("8", result.ToDiagnosticObject()["legacyMajors"]!.ToString());
            Assert.Equal("GXMCP_SDK_LEGACY_COMPATIBLE version=10.2.0.50000 major=10.2 driver=dotnet-reflection", result.Diagnostic);
        }

        [Fact]
        public void Evaluate_LegacyMajor_GeneXus9_ProducesLegacyCompatibleResult()
        {
            var result = WorkerSdkCompatibilityProbe.Evaluate("C:/GeneXus90", "9.0.123");

            Assert.True(result.IsCompatible);
            Assert.False(result.IsRejected);
            Assert.Equal("compatible", result.Status);
            Assert.Equal("GXMCP_SDK_LEGACY_COMPATIBLE", result.Code);
            Assert.Equal("9", result.Major);
            Assert.Equal("com-gxpublic", result.Driver);
            Assert.Equal("com-gxpublic", result.ToDiagnosticObject()["driver"]?.ToString());
            Assert.Equal("basic-legacy", result.ToDiagnosticObject()["supportLevel"]?.ToString());
            Assert.Equal("GXMCP_SDK_LEGACY_COMPATIBLE version=9.0.123 major=9 driver=com-gxpublic", result.Diagnostic);
            Assert.Equal("supported-after-provider-open", result.ToDiagnosticObject()["capabilities"]?["metadataQuery"]?.ToString());
            Assert.Equal("unsupported-by-gxpublic-contract", result.ToDiagnosticObject()["capabilities"]?["sourceParts"]?.ToString());
        }

        [Fact]
        public void Check_DetectsClassicInstallation_WhenGxExePresent()
        {
            string tmp = Path.Combine(Path.GetTempPath(), "gx9-probe-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            try
            {
                File.WriteAllText(Path.Combine(tmp, "gx.exe"), string.Empty);
                WorkerSdkCompatibilityProbe.FileVersionReader = path =>
                    path.EndsWith("gx.exe", StringComparison.OrdinalIgnoreCase) ? "9.0.123" : null;

                var result = WorkerSdkCompatibilityProbe.Check(tmp);

                Assert.True(result.IsCompatible);
                Assert.False(result.IsRejected);
                Assert.Equal("compatible", result.Status);
                Assert.Equal("GXMCP_SDK_LEGACY_COMPATIBLE", result.Code);
                Assert.Equal("9", result.Major);
                Assert.Equal("com-gxpublic", result.Driver);
                Assert.Equal("com-gxpublic", result.ToDiagnosticObject()["driver"]?.ToString());
            }
            finally
            {
                WorkerSdkCompatibilityProbe.FileVersionReader = null;
                try { Directory.Delete(tmp, recursive: true); } catch { }
            }
        }

        [Fact]
        public void Check_DetectsClassicInstallation_WhenGxdl32DllPresent()
        {
            string tmp = Path.Combine(Path.GetTempPath(), "gx9-gxdl32-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            try
            {
                File.WriteAllText(Path.Combine(tmp, "gxdl32.dll"), string.Empty);
                WorkerSdkCompatibilityProbe.FileVersionReader = path =>
                    path.EndsWith("gxdl32.dll", StringComparison.OrdinalIgnoreCase) ? "9.0.456" : null;

                var result = WorkerSdkCompatibilityProbe.Check(tmp);

                Assert.True(result.IsCompatible);
                Assert.Equal("GXMCP_SDK_LEGACY_COMPATIBLE", result.Code);
                Assert.Equal("9", result.Major);
                Assert.Equal("com-gxpublic", result.Driver);
            }
            finally
            {
                WorkerSdkCompatibilityProbe.FileVersionReader = null;
                try { Directory.Delete(tmp, recursive: true); } catch { }
            }
        }

        [Fact]
        public void Check_DetectsGeneXus8Installation_WhenGxw32ExePresent()
        {
            string tmp = Path.Combine(Path.GetTempPath(), "gx8-gxw32-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            try
            {
                File.WriteAllText(Path.Combine(tmp, "gxw32.exe"), string.Empty);
                WorkerSdkCompatibilityProbe.FileVersionReader = path =>
                    path.EndsWith("gxw32.exe", StringComparison.OrdinalIgnoreCase) ? "8.0.0.632" : null;

                var result = WorkerSdkCompatibilityProbe.Check(tmp);

                Assert.True(result.IsCompatible);
                Assert.Equal("GXMCP_SDK_LEGACY_COMPATIBLE", result.Code);
                Assert.Equal("8", result.Major);
                Assert.Equal("com-gxpublic", result.Driver);
            }
            finally
            {
                WorkerSdkCompatibilityProbe.FileVersionReader = null;
                try { Directory.Delete(tmp, recursive: true); } catch { }
            }
        }
    }
}
