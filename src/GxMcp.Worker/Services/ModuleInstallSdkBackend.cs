using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Artech.Architecture.Common.Objects;
using Artech.Architecture.Common.Parts;
using Artech.Architecture.Common.Services;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    // A pinned, ephemeral server avoids the SDK file overload resolving another OPC
    // from the configured local server. It never registers or publishes a server.
    internal sealed class ModuleInstallSdkBackend : IModuleInstallBackend
    {
        private readonly KbService kbService;
        private readonly KnowledgeBase kb;
        private readonly KBModel model;
        private readonly string version;
        private readonly IModuleManagerService manager;
        private readonly IndexCacheService index;
        private readonly HashSet<Guid> plannedObjects;
        internal JArray CacheDiagnostics { get; } = new JArray();

        internal ModuleInstallSdkBackend(KbService kbService, KnowledgeBase kb,
            IModuleManagerService manager, IndexCacheService index, IReadOnlyList<ModuleInstallPackage> packages)
        {
            this.kbService = kbService;
            this.kb = kb;
            this.model = kb.DesignModel;
            this.manager = manager;
            this.index = index;
            plannedObjects = new HashSet<Guid>(packages.SelectMany(p => p.Objects).Select(o => o.Guid));
            version = KBVersion.GetActive(kb)?.Guid.ToString();
            ValidateDestination();
        }

        public void ValidateDestination()
        {
            if (!ReferenceEquals(kbService.GetKB(), kb) || !ReferenceEquals(kb.DesignModel, model)
                || string.IsNullOrEmpty(version) || KBVersion.GetActive(kb)?.Guid.ToString() != version
                || KBVersion.GetActive(kb).IsFrozen)
                throw new InvalidOperationException("ModuleDestinationChanged");
            if (WriteDestinationGuard.CheckCommand(kbService, "Module", "Run", new JObject { ["action"] = "install" }) != null)
                throw new InvalidOperationException("ModuleDestinationMismatch");
            if (BuildService.GetActiveBuilds(kb.Location).Count != 0)
                throw new InvalidOperationException("ModuleOperationBusy");
        }

        public ModuleInstallSnapshot Read(ModuleInstallPackage package)
        {
            ValidateDestination();
            // Query the SDK again, not the search index or the install boolean.
            var all = model.Objects.GetAll().OfType<KBObject>().ToList();
            var id = Guid.Parse(package.Metadata.ID);
            var plannedIds = new HashSet<Guid>(package.Objects.Select(o => o.Guid));
            for (int i = 0; i < all.Count; i++)
                if (plannedIds.Contains(all[i].Guid) || BelongsTo(all[i], id))
                    all[i] = Helpers.EventsSaveIsolation.Fresh(kb, all[i].Guid);
            var module = all.FirstOrDefault(o => o.Guid == id) as Module;
            var inventory = new JArray();
            var missing = new JArray();
            bool matches = module != null && module.IsInterface;
            string conflict = null;
            foreach (var expected in package.Objects)
            {
                var obj = all.FirstOrDefault(o => o.Guid == expected.Guid);
                if (obj == null)
                {
                    missing.Add(new JObject { ["guid"] = expected.Guid.ToString(), ["name"] = expected.Name });
                    matches = false;
                    if (all.Any(o => o.Name == expected.SimpleName && o.Key.Type == expected.TypeGuid
                        && (o.Parent?.Guid == id || expected.Guid == id))) conflict = "ModuleObjectNameConflict";
                    continue;
                }
                if (obj.Name != expected.SimpleName || obj.Key.Type != expected.TypeGuid
                    || (expected.Name.Contains(".") && obj.QualifiedName?.ToString() != expected.Name)
                    || (expected.Guid != id && !BelongsTo(obj, id)))
                { matches = false; conflict = "ModuleObjectIdentityConflict"; }
                if (module == null) conflict = "ModulePartialInventoryConflict";
                inventory.Add(Describe(obj));
            }
            // Include unexpected descendants so concurrent additions and partial imports
            // cannot disappear from the receipt.
            foreach (var extra in all.Where(o => !plannedIds.Contains(o.Guid) && BelongsTo(o, id)).OrderBy(o => o.Guid))
            {
                inventory.Add(Describe(extra));
                matches = false;
                conflict = "ModuleUnexpectedObjects";
            }
            var dependencies = new JArray();
            if (module != null)
            {
                var content = module.Parts.Get<ModuleContentPart>();
                var observed = (content?.ExportDependencies ?? Enumerable.Empty<PackagedModuleDependency>()).ToList();
                foreach (var dep in observed.OrderBy(d => d.Guid))
                    dependencies.Add(new JObject { ["guid"] = dep.Guid.ToString(), ["name"] = dep.Name,
                        ["version"] = dep.Version, ["minimumVersion"] = dep.MinimumVersion, ["maximumVersion"] = dep.MaximumVersion });
                matches &= observed.Count == package.Metadata.Dependencies.Count()
                    && package.Metadata.Dependencies.All(d => observed.Any(o => o.Guid == d.Guid && o.Name == d.Name
                        && o.Version == d.Version && (o.MinimumVersion ?? "") == (d.MinimumVersion ?? "")
                        && (o.MaximumVersion ?? "") == (d.MaximumVersion ?? "")));
            }
            string installedVersion = module?.GetPropertyValue<string>("ModuleVersion");
            var outsidePlan = all.Where(o => !plannedObjects.Contains(o.Guid)).ToList();
            matches &= string.Equals(installedVersion, package.Metadata.Version, StringComparison.Ordinal);
            return new ModuleInstallSnapshot
            {
                ModuleExists = module != null, Version = installedVersion,
                MatchesPackage = matches, Conflict = conflict,
                Inventory = new JObject { ["scope"] = "planned objects and module descendants", ["version"] = installedVersion,
                    ["objects"] = inventory, ["missingObjects"] = missing, ["dependencies"] = dependencies,
                    ["outsidePlanObjectCount"] = outsidePlan.Count,
                    ["outsidePlanLatestUpdate"] = outsidePlan.Count == 0 ? null : outsidePlan.Max(o => o.LastUpdate).ToUniversalTime().ToString("O") }
            };
        }

        private static bool BelongsTo(KBObject obj, Guid module)
        {
            var seen = new HashSet<Guid>();
            for (var parent = obj.Parent; parent != null && seen.Add(parent.Guid); parent = parent.Parent)
                if (parent.Guid == module) return true;
            return false;
        }

        private static JObject Describe(KBObject obj) => new JObject
        {
            ["guid"] = obj.Guid.ToString(), ["name"] = obj.Name, ["qualifiedName"] = obj.QualifiedName?.ToString(),
            ["type"] = obj.Key.Type.ToString(), ["parentGuid"] = obj.Parent?.Guid.ToString(),
            ["timestamp"] = obj.Timestamp.ToUniversalTime().ToString("O")
        };

        public bool Install(ModuleInstallPackage package)
        {
            ValidateDestination();
            // Deny changes to the package until the SDK returns, including its cache upload.
            using (var pinned = File.Open(package.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (ModuleInstallPackage.Read(package.FilePath).Sha256 != package.Sha256)
                    throw new InvalidOperationException("ModulePackageChanged");
                using (var cached = PinCachedPackage(TryGetModuleCachePath(manager), package))
                {
                    CacheDiagnostics.Add(new JObject { ["module"] = package.Metadata.Name, ["cachePinned"] = cached != null,
                        ["cacheProbedPath"] = package.Metadata.Name + "_" + package.Metadata.ID + "/" + package.Metadata.Version + "/" + package.Metadata.GetStorageName(),
                        ["scope"] = "existing OPC bytes; SDK owns cache creation and extraction" });
                    try { return manager.Install(model, new PinnedPackageServer(package), package.Metadata); }
                    catch (ArgumentNullException ex) when ((ex.StackTrace ?? "").Contains("ModuleManagerService.InstallOPC"))
                    {
                        throw new ModuleInstallPlanException("ModuleCacheStagingFailed",
                            "SDK InstallOPC received a null cached package path. Inspect cache permissions and extraction diagnostics; no cache deletion or automatic retry was attempted.");
                    }
                    finally
                    {
                        // Refresh objects that exist even if the SDK threw after a partial import.
                        try
                        {
                            var expected = new HashSet<Guid>(package.Objects.Select(o => o.Guid));
                            foreach (KBObject obj in model.Objects.GetAll())
                                if (expected.Contains(obj.Guid)) index?.UpdateEntry(obj);
                        }
                        catch (Exception ex)
                        {
                            Helpers.Logger.Warn("Module inventory index refresh failed (" + ex.GetType().Name + "); SDK readback remains authoritative.");
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Resolves the SDK module cache directory. <c>GetSettings</c> only exists
        /// from GeneXus 17, so a direct call would stop the Worker from compiling
        /// against GeneXus 16. Returns null when the directory cannot be read, and
        /// the caller refuses the install: without the cache path the existing
        /// cache identity cannot be verified, which this flow promises.
        /// </summary>
        internal static string TryGetModuleCachePath(IModuleManagerService manager)
        {
            if (manager == null) return null;
            try
            {
                var settings = manager.GetType()
                    .GetMethod("GetSettings", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance,
                        null, Type.EmptyTypes, null)
                    ?.Invoke(manager, null);
                if (settings == null) return null;
                return settings.GetType()
                    .GetProperty("CachePath", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                    ?.GetValue(settings) as string;
            }
            catch (Exception ex)
            {
                GxMcp.Worker.Helpers.Logger.Warn("Module cache path probe failed: " + ex.Message);
                return null;
            }
        }

        internal static FileStream PinCachedPackage(string cachePath, ModuleInstallPackage package)
        {
            if (string.IsNullOrWhiteSpace(cachePath))
                throw new ModuleInstallPlanException("ModuleCacheUnavailable", "The SDK module manager did not expose its cache directory.");
            string bucket = Path.Combine(cachePath, package.Metadata.Name + "_" + package.Metadata.ID, package.Metadata.Version);
            string path = Path.Combine(bucket, package.Metadata.GetStorageName());
            if (!File.Exists(path)) return null;
            var pinned = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                using (var sha = SHA256.Create())
                    if (BitConverter.ToString(sha.ComputeHash(pinned)).Replace("-", "").ToLowerInvariant() != package.Sha256)
                        throw new ModuleInstallPlanException("ModuleCacheConflict", "The SDK cache contains different bytes for this module version. Reconcile it through the GeneXus Module Manager before installation.");
                return pinned;
            }
            catch { pinned.Dispose(); throw; }
        }

        private sealed class PinnedPackageServer : IModuleManagerServer
        {
            private readonly ModuleInstallPackage package;
            internal PinnedPackageServer(ModuleInstallPackage package) { this.package = package; }
            public string Name { get; set; } = "Validated local package";
            public bool NeedsProfile => false;
            public bool CanDelete => false;
            public bool CanUpdate => false;
            public Task<IEnumerable<Profile>> GetProfiles(IDictionary<string, object> context = null, IProgress<int> progress = null, CancellationToken cancellationToken = default(CancellationToken)) => Task.FromResult(Enumerable.Empty<Profile>());
            public IEnumerable<ModulePackage> List() => new[] { package.Metadata };
            public IEnumerable<ModulePackage> List(string filter) => List();
            public Task<string> DownloadModuleAsync(ModulePackage requested, IDictionary<string, object> context = null, IProgress<int> progress = null, CancellationToken cancellationToken = default(CancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (requested?.ID != package.Metadata.ID || requested.Version != package.Metadata.Version)
                    throw new InvalidOperationException("ModulePackageIdentityMismatch");
                return Task.FromResult(package.FilePath);
            }
            public Task<bool> UploadModuleAsync(string opcFile, IDictionary<string, object> context = null, IProgress<int> progress = null, CancellationToken cancellationToken = default(CancellationToken)) => throw new NotSupportedException();
            public Task<bool> UploadModuleAsync(string opcFile, string user, string password, IDictionary<string, object> context = null) => throw new NotSupportedException();
            public bool delete() => throw new NotSupportedException();
        }
    }
}
