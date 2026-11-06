using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
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
        public void SelectedManifest_AllowsSameMajorDriftAndReportsChangedFingerprints(string version)
        {
            using (var fixture = new SdkFixture(version, "selected-sdk"))
            {
                Assert.True(SdkCompatibilityValidator.Validate(fixture.Root, fixture.Manifest, _ => version).IsCompatible);
                Assert.True(SdkCompatibilityValidator.Validate(fixture.Root, fixture.Manifest, _ => version + "-different").IsCompatible);
                File.WriteAllText(Path.Combine(fixture.Root, "Artech.Architecture.Common.dll"), "different-sdk-bytes");
                var result = SdkCompatibilityValidator.Validate(fixture.Root, fixture.Manifest, _ => version);
                Assert.True(result.IsCompatible);
                Assert.Contains("GXMCP_SDK_FINGERPRINT_DRIFT", result.Diagnostic);
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
        public void Validate_AllowsCatalogSupportedMajorFromNpmPackageLayout()
        {
            using (var fixture = new SdkFixture("18.0.10.184260", "supported"))
            {
                string packageRoot = Path.Combine(fixture.Root, "package");
                string workerDirectory = Path.Combine(packageRoot, "publish", "worker");
                string packagedManifest = Path.Combine(workerDirectory, "sdk-compatibility.json");
                string packagedCatalog = Path.Combine(packageRoot, "config", "gx-versions.json");
                Directory.CreateDirectory(workerDirectory);
                Directory.CreateDirectory(Path.GetDirectoryName(packagedCatalog));
                File.Move(fixture.Manifest, packagedManifest);
                File.Move(fixture.Catalog, packagedCatalog);
                var result = SdkCompatibilityValidator.Validate(fixture.Root, packagedManifest, _ => "17.0.4.153047");

                Assert.True(result.IsCompatible);
                Assert.Equal("GXMCP_SDK_COMPATIBLE", result.Code);
                Assert.Contains("supported major 17; reference major 18", result.Diagnostic);
                Assert.Contains("supportedMajors=16,17,18", result.Diagnostic);
            }
        }

        [Fact]
        public void Validate_SupportsGeneXus16Major()
        {
            using (var fixture = new SdkFixture("18.0.10.184260", "supported"))
            {
                var result = SdkCompatibilityValidator.Validate(fixture.Root, fixture.Manifest, _ => "16.0.11.144151");

                Assert.True(result.IsCompatible);
                Assert.Equal("GXMCP_SDK_COMPATIBLE", result.Code);
                Assert.Contains("supported major 16; reference major 18", result.Diagnostic);
                Assert.Contains("supportedMajors=16,17,18", result.Diagnostic);
            }
        }

        [Fact]
        public void Validate_ReportsFingerprintDriftWithTheSameProductVersionWithoutBlocking()
        {
            using (var fixture = new SdkFixture("18.0.10.184260", "supported"))
            {
                File.WriteAllText(Path.Combine(fixture.Root, "Artech.Architecture.Common.dll"), "tampered");
                var result = GxMcp.Worker.SdkCompatibilityValidator.Validate(fixture.Root, fixture.Manifest, _ => "18.0.10.184260");

                Assert.True(result.IsCompatible);
                Assert.Equal("GXMCP_SDK_COMPATIBLE", result.Code);
                Assert.Contains("GXMCP_SDK_FINGERPRINT_DRIFT", result.Diagnostic);
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

        [Theory]
        [InlineData("19.0.10.184260")]
        [InlineData("invalid")]
        [InlineData("")]
        [InlineData(null)]
        public void Validate_RejectsDifferentOrUnreadableMajorBeforeAssemblyDetails(string actualVersion)
        {
            using (var fixture = new SdkFixture("18.0.10.184260", "supported"))
            {
                var json = JObject.Parse(File.ReadAllText(fixture.Manifest));
                json["assemblies"] = new JArray();
                File.WriteAllText(fixture.Manifest, json.ToString());
                var result = SdkCompatibilityValidator.Validate(fixture.Root, fixture.Manifest, _ => actualVersion);

                Assert.False(result.IsCompatible);
                Assert.Equal("GXMCP_SDK_VERSION_MISMATCH", result.Code);
                Assert.Contains("expectedVersion=18.0.10.184260", result.Diagnostic);
                Assert.Contains("actualVersion=" + actualVersion, result.Diagnostic);
                Assert.Contains("supportedMajors=16,17,18", result.Diagnostic);
            }
        }

        [Fact]
        public void Validate_RejectsMissingVersionCatalog()
        {
            using (var fixture = new SdkFixture("18.0.10.184260", "supported"))
            {
                File.Delete(fixture.Catalog);
                var result = SdkCompatibilityValidator.Validate(fixture.Root, fixture.Manifest, _ => "18.0.10.184260");

                Assert.False(result.IsCompatible);
                Assert.Equal("GXMCP_SDK_CATALOG_MISSING", result.Code);
                Assert.Contains("gx-versions.json", result.Diagnostic);
            }
        }

        [Theory]
        [InlineData("18.0.14.187794", true)]
        [InlineData("18.0.16.189550+build16", false)]
        [InlineData("18.1.0.0", false)]
        public void Validate_AllowsSameMajorDriftRegardlessOfLegacyOptIn(string actualVersion, bool legacyOptIn)
        {
            using (var fixture = new SdkFixture("18.0.10.184260", "supported"))
            {
                var json = JObject.Parse(File.ReadAllText(fixture.Manifest));
                json["allowPatchVersionDrift"] = legacyOptIn;
                File.WriteAllText(fixture.Manifest, json.ToString());
                var result = SdkCompatibilityValidator.Validate(fixture.Root, fixture.Manifest, _ => actualVersion);
                Assert.True(result.IsCompatible);
                Assert.Contains("compatible major; patch/build drift", result.Diagnostic);
            }
        }

        [Fact]
        public void Validate_AllowsPatchDriftWithoutLegacyOptIn()
        {
            using (var fixture = new SdkFixture("18.0.10.184260", "supported"))
            {
                var result = GxMcp.Worker.SdkCompatibilityValidator.Validate(fixture.Root, fixture.Manifest, _ => "18.0.14.187794");
                Assert.True(result.IsCompatible);
                Assert.Equal("GXMCP_SDK_COMPATIBLE", result.Code);
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Validate_RejectsMissingRequiredAssemblyEvenWithCompatibleVersion(bool patchDrift)
        {
            using (var fixture = new SdkFixture("18.0.10.184260", "supported"))
            {
                var json = JObject.Parse(File.ReadAllText(fixture.Manifest));
                json["assemblies"][0]["path"] = "missing-required.dll";
                File.WriteAllText(fixture.Manifest, json.ToString());
                var result = SdkCompatibilityValidator.Validate(fixture.Root, fixture.Manifest,
                    _ => patchDrift ? "18.0.16.189550" : "18.0.10.184260");
                Assert.False(result.IsCompatible);
                Assert.Equal("GXMCP_SDK_ASSEMBLY_MISSING", result.Code);
            }
        }

        [Fact]
        public void Validate_RejectsManifestWithoutRequiredAssemblies()
        {
            using (var fixture = new SdkFixture("18.0.10.184260", "supported"))
            {
                var json = JObject.Parse(File.ReadAllText(fixture.Manifest));
                json["assemblies"] = new JArray();
                File.WriteAllText(fixture.Manifest, json.ToString());
                var result = SdkCompatibilityValidator.Validate(fixture.Root, fixture.Manifest, _ => "18.0.16.189550");
                Assert.False(result.IsCompatible);
                Assert.Equal("GXMCP_SDK_MANIFEST_INVALID", result.Code);
            }
        }

        [Theory]
        [InlineData("10.3.16.146128")]
        [InlineData("10.3.0.86550")]
        public void Evolution3Manifest_AcceptsEvolution3Sdk(string actual)
        {
            using (var fixture = new SdkFixture("10.3.16.146128", "ev3", "10.3", "16", "17", "18"))
            {
                var result = SdkCompatibilityValidator.Validate(fixture.Root, fixture.Manifest, _ => actual);

                Assert.True(result.IsCompatible, result.Diagnostic);
                Assert.Contains("supportedMajors=10.3,16,17,18", result.Diagnostic);
            }
        }

        [Theory]
        [InlineData("10.3.16.146128", "18.0.10.184260")]
        [InlineData("10.3.16.146128", "16.0.11.144151")]
        [InlineData("18.0.10.184260", "10.3.16.146128")]
        [InlineData("10.3.16.146128", "10.2.5.1000")]
        public void A_Worker_Never_Runs_Against_The_Other_Sdk_Binary_Line(string reference, string actual)
        {
            using (var fixture = new SdkFixture(reference, "sdk", "10.3", "16", "17", "18"))
            {
                var result = SdkCompatibilityValidator.Validate(fixture.Root, fixture.Manifest, _ => actual);

                Assert.False(result.IsCompatible);
                Assert.Equal("GXMCP_SDK_VERSION_MISMATCH", result.Code);
            }
        }

        [Fact]
        public void Evolution3Sdk_IsRejectedWhenTheCatalogDoesNotListIt()
        {
            using (var fixture = new SdkFixture("10.3.16.146128", "ev3"))
            {
                var result = SdkCompatibilityValidator.Validate(fixture.Root, fixture.Manifest, _ => "10.3.16.146128");

                Assert.False(result.IsCompatible);
                Assert.Equal("GXMCP_SDK_VERSION_MISMATCH", result.Code);
            }
        }

        private sealed class SdkFixture : IDisposable
        {
            public readonly string Root = Path.Combine(Path.GetTempPath(), "gxmcp-sdk-" + Guid.NewGuid().ToString("N"));
            public readonly string Manifest;
            public readonly string Catalog;

            public SdkFixture(string version, string contents, params string[] catalogMajors)
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
                Catalog = Path.Combine(Root, "gx-versions.json");
                var catalog = new JObject
                {
                    ["supportedMajors"] = new JArray(
                        (catalogMajors.Length == 0 ? new[] { "16", "17", "18" } : catalogMajors)
                            .Select(major => new JObject { ["major"] = major }))
                };
                File.WriteAllText(Catalog, catalog.ToString());
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
