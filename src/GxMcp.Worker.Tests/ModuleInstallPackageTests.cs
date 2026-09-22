using System;
using System.IO;
using System.IO.Packaging;
using System.Linq;
using System.Text;
using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public sealed class OfficialCryptoPackagesFactAttribute : FactAttribute
    {
        public OfficialCryptoPackagesFactAttribute()
        {
            string sdk = Environment.GetEnvironmentVariable("GX_PATH");
            if (string.IsNullOrEmpty(sdk) || !File.Exists(Path.Combine(sdk, "Modules", "SecurityAPICommons_3.16.13.opc"))
                || !File.Exists(Path.Combine(sdk, "Modules", "GeneXusCryptography_3.16.13.opc")))
                Skip = "GX_PATH must point to an SDK with both official cryptography OPC fixtures at version 3.16.13; no KB is required.";
        }
    }

    public sealed class ModuleInstallPackageTests : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "gx-module-plan-" + Guid.NewGuid().ToString("N"));
        private static readonly Guid ModuleType = new Guid("c88fffcd-b6f8-0000-8fec-00b5497e2117");
        public ModuleInstallPackageTests() { Directory.CreateDirectory(Path.Combine(root, "Modules")); }
        public void Dispose() { Directory.Delete(root, true); }

        [OfficialCryptoPackagesFact]
        public void Official_crypto_packages_produce_complete_read_only_dependency_plan()
        {
            string sdk = Environment.GetEnvironmentVariable("GX_PATH");
            string commons = Path.Combine(sdk, "Modules", "SecurityAPICommons_3.16.13.opc");
            string crypto = Path.Combine(sdk, "Modules", "GeneXusCryptography_3.16.13.opc");
            byte[] commonsBefore = File.ReadAllBytes(commons), cryptoBefore = File.ReadAllBytes(crypto);
            var plan = ModuleInstallPackage.Plan(null, "GeneXusCryptography", "3.16.13", sdk);
            Assert.Equal(new[] { "SecurityAPICommons", "GeneXusCryptography" }, plan.Select(p => p.Metadata.Name));
            Assert.Equal(14, plan[0].Objects.Count);
            Assert.Equal(35, plan[1].Objects.Count);
            Assert.Equal("44fbb6d2-0a48-44e7-98e9-64d0e359861f", plan[0].Metadata.ID);
            Assert.Equal("e58026a8-9276-412d-a45d-63252580557f", plan[1].Metadata.ID);
            Assert.Empty(plan[0].Metadata.Dependencies);
            var dependency = Assert.Single(plan[1].Metadata.Dependencies);
            Assert.Equal(plan[0].Metadata.ID, dependency.Guid.ToString());
            Assert.Equal("1.8.6.138122", dependency.MinimumVersion);
            Assert.Equal(new[] { 12, 15, 41 }, plan[1].Metadata.Platforms.Select(p => p.Generator).OrderBy(p => p));
            Assert.Contains(plan[0].Objects, o => o.Name == "SecurityAPICommons.Certificate" && o.SimpleName == "Certificate");
            Assert.Equal(commonsBefore, File.ReadAllBytes(commons));
            Assert.Equal(cryptoBefore, File.ReadAllBytes(crypto));
        }

        [Fact]
        public void Cache_copy_must_match_and_stays_pinned_without_cache_repair()
        {
            var package = ModuleInstallPackage.Read(Write("Common", Guid.NewGuid(), "1.2.3"));
            string cache = Path.Combine(root, "Cache");
            Assert.Null(ModuleInstallSdkBackend.PinCachedPackage(cache, package));
            Assert.False(Directory.Exists(cache));
            string bucket = Path.Combine(cache, package.Metadata.Name + "_" + package.Metadata.ID, package.Metadata.Version);
            Directory.CreateDirectory(bucket);
            string copy = Path.Combine(bucket, package.Metadata.GetStorageName());
            File.Copy(package.FilePath, copy);
            using (ModuleInstallSdkBackend.PinCachedPackage(cache, package))
                Assert.Throws<IOException>(() => File.WriteAllText(copy, "changed"));
            File.WriteAllText(copy, "different cached package");
            Assert.Equal("ModuleCacheConflict", Assert.Throws<ModuleInstallPlanException>(() => ModuleInstallSdkBackend.PinCachedPackage(cache, package)).Code);
            Assert.Equal("different cached package", File.ReadAllText(copy));
        }

        [Fact]
        public void Reads_nested_members_and_hash_without_modifying_package()
        {
            string path = Write("Common", Guid.NewGuid(), "1.2.3");
            byte[] before = File.ReadAllBytes(path);
            var plan = ModuleInstallPackage.Read(path);
            Assert.Equal("Common", plan.Metadata.Name);
            Assert.Equal(2, plan.Objects.Count);
            Assert.Equal(64, plan.Sha256.Length);
            Assert.Equal(before, File.ReadAllBytes(path));
            Assert.Single(Directory.GetFiles(Path.Combine(root, "Modules")));
        }

        [Fact]
        public void Dependencies_are_ordered_before_root_and_not_duplicated()
        {
            Guid dependency = Guid.NewGuid();
            Write("Common", dependency, "1.2.3");
            Write("Crypto", Guid.NewGuid(), "1.2.3", Dependency("Common", dependency, "1.2.3"));
            var plan = ModuleInstallPackage.Plan(null, "Crypto", "1.2.3", root);
            Assert.Equal(new[] { "Common", "Crypto" }, plan.Select(p => p.Metadata.Name));
        }

        [Fact]
        public void Range_resolution_chooses_highest_supported_local_version()
        {
            Guid dependency = Guid.NewGuid();
            Write("Common", dependency, "1.2.3");
            Write("Common", dependency, "1.3.0");
            Write("Common", dependency, "2.0.0");
            Write("Crypto", Guid.NewGuid(), "1.2.3", Dependency("Common", dependency, "", "1.2.0", "1.9.0"));
            Assert.Equal("1.3.0", ModuleInstallPackage.Plan(null, "Crypto", null, root)[0].Metadata.Version);
        }

        [Fact]
        public void Dependency_cycle_is_rejected()
        {
            Guid a = Guid.NewGuid(), b = Guid.NewGuid();
            Write("Alpha", a, "1.0.0", Dependency("Beta", b, "1.0.0"));
            Write("Beta", b, "1.0.0", Dependency("Alpha", a, "1.0.0"));
            Assert.Equal("ModuleDependencyCycle", Assert.Throws<ModuleInstallPlanException>(() => ModuleInstallPackage.Plan(null, "Alpha", null, root)).Code);
        }

        [Fact]
        public void Wrong_dependency_identity_is_rejected()
        {
            Write("Common", Guid.NewGuid(), "1.2.3");
            Write("Crypto", Guid.NewGuid(), "1.2.3", Dependency("Common", Guid.NewGuid(), "1.2.3"));
            Assert.Equal("ModuleDependencyConflict", Assert.Throws<ModuleInstallPlanException>(() => ModuleInstallPackage.Plan(null, "Crypto", null, root)).Code);
        }

        [Fact]
        public void Missing_dependency_does_not_create_files()
        {
            Write("Crypto", Guid.NewGuid(), "1.2.3", Dependency("Common", Guid.NewGuid(), "1.2.3"));
            Assert.Equal("ModulePackageNotFound", Assert.Throws<ModuleInstallPlanException>(() => ModuleInstallPackage.Plan(null, "Crypto", null, root)).Code);
            Assert.Single(Directory.GetFiles(Path.Combine(root, "Modules")));
        }

        [Theory]
        [InlineData("<Actions><Action /></Actions>", false)]
        [InlineData("<ImportHooks />", false)]
        [InlineData("", true)]
        public void Unsupported_import_side_effects_are_rejected(string extra, bool database)
        {
            string path = Write("Common", Guid.NewGuid(), "1.2.3", extra: extra, database: database);
            Assert.Equal("ModulePackageUnsupported", Assert.Throws<ModuleInstallPlanException>(() => ModuleInstallPackage.Read(path)).Code);
        }

        [Fact]
        public void Explicit_name_and_version_must_match_manifest()
        {
            string path = Write("Common", Guid.NewGuid(), "1.2.3");
            Assert.Equal("ModulePackageIdentityMismatch", Assert.Throws<ModuleInstallPlanException>(() => ModuleInstallPackage.Plan(path, "Other", null, root)).Code);
            Assert.Equal("ModulePackageIdentityMismatch", Assert.Throws<ModuleInstallPlanException>(() => ModuleInstallPackage.Plan(path, null, "2.0.0", root)).Code);
        }

        [Fact]
        public void Invalid_archive_errors_do_not_echo_private_path()
        {
            string path = Path.Combine(root, "secret-company.opc");
            File.WriteAllText(path, "invalid");
            var error = Assert.Throws<ModuleInstallPlanException>(() => ModuleInstallPackage.Read(path));
            Assert.DoesNotContain(root, error.Message);
            Assert.DoesNotContain("secret-company", error.Message);
        }

        [Fact]
        public void Dtd_is_rejected_without_resolving_external_entity()
        {
            string path = Write("Common", Guid.NewGuid(), "1.2.3", manifestPrefix: "<!DOCTYPE ModulePackage [<!ENTITY x SYSTEM 'file:///private-secret'>]>");
            Assert.Equal("ModulePackageInvalid", Assert.Throws<ModuleInstallPlanException>(() => ModuleInstallPackage.Read(path)).Code);
        }

        [Fact]
        public void Definition_cannot_hide_a_dependency_missing_from_manifest()
        {
            string reference = "<Dependencies><Reference Type='Module' Id='" + Guid.NewGuid() + "'><Properties Name='Hidden' Version='1.0.0' /></Reference></Dependencies>";
            string path = Write("Common", Guid.NewGuid(), "1.2.3", extra: reference);
            Assert.Equal("ModulePackageInvalid", Assert.Throws<ModuleInstallPlanException>(() => ModuleInstallPackage.Read(path)).Code);
        }

        [Fact]
        public void Conflicting_transitive_versions_are_rejected()
        {
            Guid common = Guid.NewGuid(), left = Guid.NewGuid(), right = Guid.NewGuid();
            Write("Common", common, "1.0.0");
            Write("Common", common, "2.0.0");
            Write("Left", left, "1.0.0", Dependency("Common", common, "1.0.0"));
            Write("Right", right, "1.0.0", Dependency("Common", common, "2.0.0"));
            Write("Root", Guid.NewGuid(), "1.0.0", Dependency("Left", left, "1.0.0") + Dependency("Right", right, "1.0.0"));
            Assert.Equal("ModuleDependencyConflict", Assert.Throws<ModuleInstallPlanException>(() => ModuleInstallPackage.Plan(null, "Root", null, root)).Code);
        }

        [Fact]
        public void Invalid_lower_version_does_not_block_latest_and_is_reported_safely()
        {
            File.WriteAllText(Path.Combine(root, "Modules", "Common_1.0.0.opc"), "broken");
            Write("Common", Guid.NewGuid(), "2.0.0");
            var package = Assert.Single(ModuleInstallPackage.Plan(null, "Common", null, root));
            Assert.Equal("2.0.0", package.Metadata.Version);
            string warning = Assert.Single(package.ResolutionWarnings);
            Assert.Contains("1.0.0", warning);
            Assert.DoesNotContain(root, warning);
            Assert.Single(ModuleInstallPackage.Plan(null, "Common", "2.0.0", root));
        }

        [Fact]
        public void Invalid_selected_version_is_not_silently_replaced()
        {
            File.WriteAllText(Path.Combine(root, "Modules", "Common_2.0.0.opc"), "broken");
            Write("Common", Guid.NewGuid(), "1.0.0");
            Assert.Equal("ModulePackageInvalid", Assert.Throws<ModuleInstallPlanException>(() => ModuleInstallPackage.Plan(null, "Common", "2.0.0", root)).Code);
            Assert.Equal("ModulePackageInvalid", Assert.Throws<ModuleInstallPlanException>(() => ModuleInstallPackage.Plan(null, "Common", null, root)).Code);
        }

        [Fact]
        public void Explicit_package_resolves_adjacent_dependencies_before_sdk()
        {
            string adjacent = Path.Combine(root, "adjacent");
            Directory.CreateDirectory(adjacent);
            Guid common = Guid.NewGuid();
            string dependency = Write("Common", common, "1.0.0");
            File.Move(dependency, Path.Combine(adjacent, Path.GetFileName(dependency)));
            string requested = Write("Crypto", Guid.NewGuid(), "1.0.0", Dependency("Common", common, ""));
            string explicitPath = Path.Combine(adjacent, Path.GetFileName(requested));
            File.Move(requested, explicitPath);
            Write("Common", common, "2.0.0");
            var plan = ModuleInstallPackage.Plan(explicitPath, null, null, root);
            Assert.Equal("1.0.0", plan[0].Metadata.Version);
            Assert.Equal(adjacent, Path.GetDirectoryName(plan[0].FilePath));
        }

        [Fact]
        public void Adjacent_and_sdk_same_version_with_different_contents_conflict()
        {
            string adjacent = Path.Combine(root, "adjacent");
            Directory.CreateDirectory(adjacent);
            Guid common = Guid.NewGuid();
            string dependency = Write("Common", common, "1.0.0");
            File.Move(dependency, Path.Combine(adjacent, Path.GetFileName(dependency)));
            string requested = Write("Crypto", Guid.NewGuid(), "1.0.0", Dependency("Common", common, "1.0.0"));
            string explicitPath = Path.Combine(adjacent, Path.GetFileName(requested));
            File.Move(requested, explicitPath);
            Write("Common", common, "1.0.0"); // New member GUID means different contents.
            Assert.Equal("ModuleDependencyConflict", Assert.Throws<ModuleInstallPlanException>(() => ModuleInstallPackage.Plan(explicitPath, null, null, root)).Code);
        }

        [Theory]
        [InlineData("../Common")]
        [InlineData("*")]
        [InlineData("Common/Other")]
        public void Module_names_cannot_change_package_search_path(string name)
        {
            Assert.Equal("ModulePackageInvalid", Assert.Throws<ModuleInstallPlanException>(() => ModuleInstallPackage.Plan(null, name, null, root)).Code);
        }

        private static string Dependency(string name, Guid id, string version, string min = "", string max = "") => "<PackagedModuleDependency><Name>" + name + "</Name><Guid>" + id + "</Guid><Version>" + version + "</Version><MinimumVersion>" + min + "</MinimumVersion><MaximumVersion>" + max + "</MaximumVersion></PackagedModuleDependency>";

        private string Write(string name, Guid id, string version, string deps = "", string extra = "", bool database = false, string manifestPrefix = "")
        {
            string path = Path.Combine(root, "Modules", name + "_" + version + ".opc");
            using (var package = Package.Open(path, FileMode.Create, FileAccess.ReadWrite))
            {
                Put(package, "ModuleManifest.mf", manifestPrefix + "<ModulePackage><ID>" + id + "</ID><Name>" + name + "</Name><Version>" + version + "</Version><HasDatabase>" + database.ToString().ToLowerInvariant() + "</HasDatabase><Dependencies>" + deps + "</Dependencies></ModulePackage>");
                Put(package, "ModuleDefinition.xml", "<ExportFile><Objects><Object guid='" + id + "' name='" + name + "' type='" + ModuleType + "' moduleVersion='" + version + "'><Part><ExportFile><Objects><Object guid='" + Guid.NewGuid() + "' name='Member' type='" + Guid.NewGuid() + "' /></Objects></ExportFile></Part></Object></Objects>" + extra + "</ExportFile>");
            }
            return path;
        }

        private static void Put(Package package, string name, string xml)
        {
            var part = package.CreatePart(PackUriHelper.CreatePartUri(new Uri(name, UriKind.Relative)), "application/xml");
            using (var writer = new StreamWriter(part.GetStream(), new UTF8Encoding(false))) writer.Write(xml);
        }
    }
}
