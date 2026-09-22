using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Artech.Architecture.Common.Services;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public sealed class ModuleInstallFlowTests
    {
        [Fact]
        public void Dry_run_returns_dependency_first_plan_without_installing()
        {
            var backend = new FakeBackend();
            JObject result = Run(backend, true, "Dependency", "Target");
            Assert.Equal("ModuleInstallDryRun", (string)result["code"]);
            Assert.False((bool)result["persisted"]);
            Assert.Empty(backend.Installs);
            Assert.Equal(new[] { "Dependency", "Target" }, result["plan"].Select(p => (string)p["module"]));
            Assert.NotEmpty(result["plan"][0]["objects"]);
        }

        [Fact]
        public void Installs_each_package_once_and_repetition_is_verified_noop()
        {
            var backend = new FakeBackend();
            JObject first = Run(backend, false, "Dependency", "Target");
            Assert.Equal("ModuleInstalled", (string)first["code"]);
            Assert.True((bool)first["persisted"]);
            Assert.True((bool)first["verifiedByReadback"]);
            JObject second = Run(backend, false, "Dependency", "Target");
            Assert.Equal("ModuleInstalled", (string)second["code"]);
            Assert.True((bool)second["noMutation"]);
            Assert.Equal(new[] { "Dependency", "Target" }, backend.Installs);
            Assert.Empty(second["implicitLifecycleOperations"]);
        }

        [Theory]
        [InlineData("2.0", true, null)]
        [InlineData("1.0", false, null)]
        [InlineData(null, false, "ObjectIdentityCollision")]
        public void Conflicting_version_partial_module_and_object_collision_reject_before_write(string version, bool complete, string conflict)
        {
            var backend = new FakeBackend();
            backend.States["Target"] = Snapshot(version, complete, conflict);
            JObject result = Run(backend, false, "Target");
            Assert.Equal("ModuleInstallConflict", (string)result["code"]);
            Assert.False((bool)result["verifiedByReadback"]);
            Assert.Empty(backend.Installs);
        }

        [Fact]
        public void Destination_is_revalidated_before_install()
        {
            var backend = new FakeBackend { OnValidate = count => { if (count == 2) throw new InvalidOperationException("secret destination"); } };
            JObject result = Run(backend, false, "Target");
            Assert.Equal("recheck", (string)result["stage"]);
            Assert.Empty(backend.Installs);
            Assert.DoesNotContain("secret", result.ToString());
        }

        [Fact]
        public void Concurrent_change_rejects_without_overwriting_it()
        {
            var backend = new FakeBackend();
            backend.OnValidate = count => { if (count == 2) backend.States["Target"] = Snapshot("2.0", false); };
            JObject result = Run(backend, false, "Target");
            Assert.Equal("ModuleInstallConcurrentChange", (string)result["code"]);
            Assert.Equal("2.0", (string)result["inventory"][0]["after"]["version"]);
            Assert.Empty(backend.Installs);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void False_or_exception_after_partial_write_reports_inventory_without_retry_or_rollback(bool throwAfterWrite)
        {
            var backend = new FakeBackend();
            backend.OnInstall = package =>
            {
                backend.States[package.Metadata.Name] = Snapshot("1.0", false);
                if (throwAfterWrite) throw new ArgumentNullException("service", "credential must never leak");
                return false;
            };
            JObject result = Run(backend, false, "Target");
            Assert.True((bool)result["persisted"]);
            Assert.True((bool)result["persistedStateKnown"]);
            Assert.True((bool)result["partialPersistenceDetected"]);
            Assert.False((bool)result["verifiedByReadback"]);
            Assert.False((bool)result["rollback"]["attempted"]);
            Assert.False((bool)result["retryable"]);
            Assert.Single(backend.Installs);
            Assert.DoesNotContain("credential", result.ToString());
            if (throwAfterWrite) Assert.Equal("service", (string)result["diagnostic"]["parameter"]);
        }

        [Fact]
        public void Failed_readback_does_not_claim_unpersisted_or_successful_rollback()
        {
            var backend = new FakeBackend { FailReadsAfterInstall = true };
            JObject result = Run(backend, false, "Target");
            Assert.Equal(JTokenType.Null, result["persisted"].Type);
            Assert.False((bool)result["persistedStateKnown"]);
            Assert.False((bool)result["verifiedByReadback"]);
            Assert.False((bool)result["rollback"]["verified"]);
            Assert.Single(backend.Installs);
        }

        [Fact]
        public void Verified_dependency_remains_visible_when_target_fails_without_writing()
        {
            var backend = new FakeBackend();
            backend.OnInstall = package =>
            {
                if (package.Metadata.Name == "Target") throw new InvalidOperationException();
                backend.States[package.Metadata.Name] = Snapshot("1.0", true);
                return true;
            };
            JObject result = Run(backend, false, "Dependency", "Target");
            Assert.True((bool)result["partialPersistenceDetected"]);
            Assert.True((bool)result["inventory"][0]["matches"]);
            Assert.False((bool)result["inventory"][1]["matches"]);
            Assert.Equal(2, backend.Installs.Count);
        }

        [Fact]
        public void Outside_plan_change_does_not_claim_complete_known_persistence()
        {
            var backend = new FakeBackend();
            var before = Snapshot(null, false);
            before.Inventory["outsidePlanObjectCount"] = 5;
            backend.States["Target"] = before;
            backend.OnInstall = package =>
            {
                var after = Snapshot("1.0", true);
                after.Inventory["outsidePlanObjectCount"] = 6;
                backend.States["Target"] = after;
                return true;
            };
            var receipt = Run(backend, false, "Target");
            Assert.Equal("ModuleInstallUnexpectedChange", (string)receipt["code"]);
            Assert.True((bool)receipt["outsidePlanChangeDetected"]);
            Assert.False((bool)receipt["persistedStateKnown"]);
            Assert.False((bool)receipt["verifiedByReadback"]);
            Assert.Single(backend.Installs);
        }

        [Fact]
        public void Sdk_success_without_matching_readback_is_not_success()
        {
            var backend = new FakeBackend { OnInstall = package => true };
            JObject result = Run(backend, false, "Target");
            Assert.Equal("ModuleInstallNotVerified", (string)result["code"]);
            Assert.False((bool)result["persisted"]);
            Assert.False((bool)result["verifiedByReadback"]);
        }

        private static JObject Run(FakeBackend backend, bool dryRun, params string[] names) =>
            ModuleInstallFlow.Run(names.Select(Package).ToArray(), dryRun, backend);

        private static ModuleInstallPackage Package(string name)
        {
            // Isolate orchestration from archive parsing; real OPC validation has its own tests.
            var package = new ModuleInstallPackage();
            typeof(ModuleInstallPackage).GetProperty("Metadata", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(package, new ModulePackage { Name = name, Version = "1.0" });
            typeof(ModuleInstallPackage).GetProperty("Objects", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(package, new[] { new ModuleInstallObject { Guid = Guid.NewGuid(), Name = name + ".Object", TypeGuid = Guid.NewGuid() } });
            return package;
        }

        private static ModuleInstallSnapshot Snapshot(string version, bool complete, string conflict = null) => new ModuleInstallSnapshot
        {
            ModuleExists = version != null, Version = version, MatchesPackage = complete, Conflict = conflict,
            Inventory = new JObject { ["version"] = version, ["objects"] = new JArray(complete ? "complete" : version == null ? "absent" : "partial") }
        };

        private sealed class FakeBackend : IModuleInstallBackend
        {
            internal readonly Dictionary<string, ModuleInstallSnapshot> States = new Dictionary<string, ModuleInstallSnapshot>();
            internal readonly List<string> Installs = new List<string>();
            internal Action<int> OnValidate;
            internal Func<ModuleInstallPackage, bool> OnInstall;
            internal bool FailReadsAfterInstall;
            private int validations;
            public void ValidateDestination() => OnValidate?.Invoke(++validations);
            public ModuleInstallSnapshot Read(ModuleInstallPackage package)
            {
                if (FailReadsAfterInstall && Installs.Count > 0) throw new InvalidOperationException();
                return States.TryGetValue(package.Metadata.Name, out var state) ? state : Snapshot(null, false);
            }
            public bool Install(ModuleInstallPackage package)
            {
                Installs.Add(package.Metadata.Name);
                if (OnInstall != null) return OnInstall(package);
                States[package.Metadata.Name] = Snapshot(package.Metadata.Version, true);
                return true;
            }
        }
    }
}
