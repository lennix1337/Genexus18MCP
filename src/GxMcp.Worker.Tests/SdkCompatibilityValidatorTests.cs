using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class SdkCompatibilityValidatorTests
    {
        [Theory]
        [InlineData("18.0.10.184260")]
        [InlineData("18.0.11.185416+build11")]
        [InlineData("18.0.12.186073+build12")]
        [InlineData("18.0.16.189550+build16")]
        public void SelectedLock_AcceptsOnlyMatchingUpgradeAndFingerprint(string version)
        {
            using (var fixture = new SdkFixture(version, "selected-sdk"))
            {
                Assert.True(SdkCompatibilityValidator.Validate(fixture.Root, fixture.Manifest, _ => version).IsCompatible);
                Assert.Equal("GXMCP_SDK_VERSION_MISMATCH",
                    SdkCompatibilityValidator.Validate(fixture.Root, fixture.Manifest, _ => version + "-different").Code);
                File.WriteAllText(Path.Combine(fixture.Root, "Artech.Architecture.Common.dll"), "different-sdk-bytes");
                Assert.Equal("GXMCP_SDK_FINGERPRINT_MISMATCH",
                    SdkCompatibilityValidator.Validate(fixture.Root, fixture.Manifest, _ => version).Code);
            }
        }

        [Fact]
        public void Validate_ReturnsCompatibleForMatchingVersionAndFingerprint()
        {
            using (var fixture = new SdkFixture("18.0.10.184260", "supported"))
            {
                var result = GxMcp.Worker.SdkCompatibilityValidator.Validate(fixture.Root, fixture.Manifest, _ => "18.0.10.184260");

                Assert.True(result.IsCompatible);
                Assert.Equal("GXMCP_SDK_COMPATIBLE", result.Code);
            }
        }

        [Fact]
        public void Validate_RejectsFingerprintMismatchWithStableDiagnostic()
        {
            using (var fixture = new SdkFixture("18.0.10.184260", "supported"))
            {
                File.WriteAllText(Path.Combine(fixture.Root, "Artech.Architecture.Common.dll"), "tampered");
                var result = GxMcp.Worker.SdkCompatibilityValidator.Validate(fixture.Root, fixture.Manifest, _ => "18.0.10.184260");

                Assert.False(result.IsCompatible);
                Assert.Equal("GXMCP_SDK_FINGERPRINT_MISMATCH", result.Code);
                Assert.Contains("expectedSha256=", result.Diagnostic);
                Assert.Contains("actualSha256=", result.Diagnostic);
            }
        }

        [Fact]
        public void Validate_RejectsMissingSdkPathWithoutThrowing()
        {
            var result = GxMcp.Worker.SdkCompatibilityValidator.Validate(
                Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
                "missing-manifest.json");

            Assert.False(result.IsCompatible);
            Assert.Equal("GXMCP_SDK_PATH_MISSING", result.Code);
            Assert.Equal("GXMCP_SDK_PATH_MISSING path=<missing>", result.Diagnostic);
        }

        [Fact]
        public void Validate_ReportsVersionMismatchBeforeAssemblyDetails()
        {
            using (var fixture = new SdkFixture("18.0.10.184260", "supported"))
            {
                File.WriteAllText(fixture.Manifest, File.ReadAllText(fixture.Manifest).Replace("18.0.10.184260", "18.0.9.0"));
                var result = GxMcp.Worker.SdkCompatibilityValidator.Validate(fixture.Root, fixture.Manifest, _ => "18.0.10.184260");

                Assert.False(result.IsCompatible);
                Assert.Equal("GXMCP_SDK_VERSION_MISMATCH", result.Code);
                Assert.Contains("expectedVersion=18.0.9.0", result.Diagnostic);
                Assert.Contains("actualVersion=18.0.10.184260", result.Diagnostic);
            }
        }

        private sealed class SdkFixture : IDisposable
        {
            public readonly string Root = Path.Combine(Path.GetTempPath(), "gxmcp-sdk-" + Guid.NewGuid().ToString("N"));
            public readonly string Manifest;

            public SdkFixture(string version, string contents)
            {
                Directory.CreateDirectory(Root);
                var assembly = Path.Combine(Root, "Artech.Architecture.Common.dll");
                File.WriteAllText(assembly, contents);
                Manifest = Path.Combine(Root, "manifest.json");
                var hash = Sha256(assembly);
                var manifest = new JObject
                {
                    ["schemaVersion"] = 1,
                    ["supportedVersion"] = version,
                    ["anchor"] = "Artech.Architecture.Common.dll",
                    ["assemblies"] = new JArray(new JObject
                    {
                        ["path"] = "Artech.Architecture.Common.dll",
                        ["sha256"] = hash
                    })
                };
                File.WriteAllText(Manifest, manifest.ToString());
            }

            public void Dispose()
            {
                try { Directory.Delete(Root, true); } catch { }
            }

            private static string Sha256(string path)
            {
                using (var sha = SHA256.Create())
                using (var stream = File.OpenRead(path))
                    return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
            }
        }
    }
}
