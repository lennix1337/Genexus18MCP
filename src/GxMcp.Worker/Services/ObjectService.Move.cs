using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Artech.Architecture.Common.Objects;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// Folder/Module placement: resolving a destination, moving an existing object
    /// (<c>genexus_properties action=move</c>) and the position proof shared with
    /// <c>genexus_create action=object_atomic</c>. Neither operation compiles, specifies,
    /// generates, reorganizes or runs anything: every receipt says <c>implicitOperations: []</c>.
    /// </summary>
    public partial class ObjectService
    {
        // A step of the move transaction failed; carries the machine-readable reason.
        private sealed class MoveStepException : Exception
        {
            public MoveStepException(string reasonCode, string message) : base(message) { ReasonCode = reasonCode; }
            public string ReasonCode { get; }
        }

        // ---- the Folder/Module tree ----------------------------------------------------------

        /// <summary>Reads every Folder and Module of the KB with the SDK: the single source of truth for a destination.</summary>
        internal PlacementCatalog BuildPlacementCatalog(KnowledgeBase kb)
        {
            var found = new List<PlacementContainer>();
            AddContainers(kb, global::Artech.Architecture.Common.KBObjectClasses.Module, PlacementContainer.ModuleKind, found);
            AddContainers(kb, global::Artech.Architecture.Common.KBObjectClasses.Folder, PlacementContainer.FolderKind, found);
            return new PlacementCatalog(found);
        }

        private static void AddContainers(KnowledgeBase kb, Guid typeGuid, string kind, List<PlacementContainer> into)
        {
            foreach (KBObject container in kb.DesignModel.Objects.GetAll(typeGuid))
            {
                string name = null;
                try { name = container.Name; } catch { }
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (IsRootModule(container)) continue; // the root of the tree, not a container in it
                into.Add(new PlacementContainer(container.Guid, name, kind, ContainerGuidOf(container)));
            }
        }

        /// <summary>
        /// The one place a destination text becomes a container. <c>genexus_properties action=move</c>
        /// (preview and write) and <c>genexus_create action=object_atomic</c> both call it.
        /// </summary>
        internal PlacementResolution ResolvePlacement(string requested, string kind, out PlacementCatalog catalog)
        {
            catalog = null;
            var kb = _kbService.GetKB();
            if (kb == null)
                return new PlacementResolution
                {
                    Status = PlacementStatus.Invalid, Requested = requested, RequestedKind = kind,
                    Message = "No KB open", Hint = "Open a KB first with genexus_kb action=open."
                };
            catalog = BuildPlacementCatalog(kb);
            return PlacementResolver.Resolve(requested, kind, catalog);
        }

        internal static string PlacementRefused(PlacementResolution resolution, string target, string phase)
            => McpResponse.Err(code: resolution.Code, message: resolution.Message, hint: resolution.Hint, target: target,
                errorExtra: new JObject
                {
                    ["reasonCode"] = resolution.Code,
                    ["phase"] = phase,
                    ["placement"] = resolution.ToDiagnostic(),
                    ["persisted"] = false,
                    ["implicitOperations"] = new JArray()
                });

        // The container (Module/Folder) holding an object; null when it sits directly under the Root Module.
        internal static Guid? ContainerGuidOf(KBObject obj)
        {
            KBObject parent = null;
            try { parent = obj?.Parent; } catch { }
            if (parent == null) return null;
            string type = null;
            try { type = parent.TypeDescriptor?.Name; } catch { }
            if (string.Equals(type, "DesignModel", StringComparison.OrdinalIgnoreCase)) return null;
            string name = null;
            try { name = parent.Name; } catch { }
            if (string.IsNullOrWhiteSpace(name)) return null; // a nameless root
            if (IsRootModule(parent)) return null;
            try { return parent.Guid; } catch { return null; }
        }

        // The SDK models the root of the KB Explorer tree as a Module object named "Root Module" (the
        // parent of every top-level Module/Folder/object), under the DesignModel. For the placement code it
        // is the root of the tree - "directly under the Root Module" is ParentGuid == null - never a container.
        internal static bool IsRootModule(KBObject candidate)
        {
            if (candidate == null) return false;
            try
            {
                var root = candidate.Model?.RootModule;
                if (root != null && root.Guid == candidate.Guid) return true;
            }
            catch { }
            try
            {
                if (!(candidate is global::Artech.Architecture.Common.Objects.Module)) return false;
                if (!string.Equals(candidate.Name, PlacementCatalog.RootName, StringComparison.OrdinalIgnoreCase)) return false;
                var above = candidate.Parent;
                string aboveType = null;
                try { aboveType = above?.TypeDescriptor?.Name; } catch { }
                return above == null || string.Equals(aboveType, "DesignModel", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        // Root Module/<Module>/<Folder>: the container's path read from the SDK parent chain (never from the index).
        internal static string ContainerPathOf(KBObject container)
        {
            var names = new List<string>();
            var seen = new HashSet<Guid>();
            KBObject current = container;
            while (current != null)
            {
                Guid guid;
                try { guid = current.Guid; } catch { break; }
                if (!seen.Add(guid)) break;
                string type = null;
                try { type = current.TypeDescriptor?.Name; } catch { }
                if (string.Equals(type, "DesignModel", StringComparison.OrdinalIgnoreCase)) break;
                string name = null;
                try { name = current.Name; } catch { }
                if (IsRootModule(current)) break;
                if (!string.IsNullOrWhiteSpace(name)) names.Insert(0, name);
                try { current = current.Parent; } catch { break; }
            }
            return PlacementCatalog.RootName + (names.Count > 0 ? "/" + string.Join("/", names) : string.Empty);
        }

        // Moving a Folder/Module re-qualifies everything inside it (the SDK updates the children's module
        // when the parent changes, seen live: the child resolves by its new module-qualified name), but the
        // Worker still holds the children it had loaded, and the index still carries their old paths.
        // Drop the cached instances and re-index them so a later read or list shows where they are now.
        private void RefreshDescendantsAfterContainerMove(KnowledgeBase kb, KBObject moved)
        {
            if (!(moved is Folder) && !(moved is global::Artech.Architecture.Common.Objects.Module)) return;
            const int MaxRefreshed = 2000;
            try
            {
                var idx = _kbService?.GetIndexCache();
                int refreshed = 0;
                foreach (KBObject child in kb.DesignModel.Objects.GetAllChildren(moved).ToList())
                {
                    if (++refreshed > MaxRefreshed)
                    {
                        Logger.Warn("MoveObject: '" + moved.Name + "' holds more than " + MaxRefreshed + " objects; the rest keep their old index path until the next reindex.");
                        break;
                    }
                    try
                    {
                        InvalidateCache(child);
                        // Evict the loaded instance too (as FindObjectFresh does): invalidating alone left the
                        // child's cached module in place, so its FullyQualifiedName still read the old module.
                        try { (child.Model.Objects as IKBModelObjectsCacheConfiguration)?.RemoveFromCaches(child); }
                        catch (Exception ex) { Logger.Debug("MoveObject: cache eviction skipped for '" + child.Name + "': " + ex.GetType().Name); }
                        RemoveReadCacheEntries(child.Guid, null);
                        var reloaded = kb.DesignModel.Objects.Get(child.Guid) ?? child;
                        if (idx != null)
                        {
                            idx.InvalidateHierarchy(reloaded.Guid);
                            idx.UpdateEntry(reloaded);
                        }
                    }
                    catch (Exception ex) { Logger.Debug("MoveObject: descendant refresh skipped for '" + child.Name + "': " + ex.Message); }
                }
            }
            catch (Exception ex) { Logger.Warn("MoveObject: descendants of '" + moved.Name + "' could not be refreshed: " + ex.Message); }
        }

        private static Guid? SafeModuleGuid(KnowledgeBase kb, KBObject obj)
        {
            try { return (obj?.Module ?? kb?.DesignModel?.RootModule)?.Guid; } catch { return null; }
        }

        // A Folder/Module with objects inside. When the answer cannot be read it is "yes": the caller refuses.
        private static bool HoldsChildren(KnowledgeBase kb, KBObject obj)
        {
            if (!(obj is Folder) && !(obj is global::Artech.Architecture.Common.Objects.Module)) return false;
            try { return kb.DesignModel.Objects.HasChildren(obj); }
            catch (Exception ex)
            {
                Logger.Debug("MoveObject: child probe unavailable for '" + obj.Name + "': " + ex.Message);
                return true;
            }
        }

        // An imported (interface) object, or a container below an imported module, is read-only for the SDK:
        // a normal save refuses to put anything in it, but EntityManager.UpdateParent would not notice.
        private static bool IsImported(KBObject obj)
        {
            try { return obj != null && !IsRootModule(obj) && Compatibility.SdkCompat.IsModuleInterface(obj); } catch { return false; }
        }

        /// <summary>The path of <paramref name="container"/> when it or an enclosing module is imported, else null.</summary>
        internal static string ImportedContainerPath(KBObject container)
        {
            var seen = new HashSet<Guid>();
            for (KBObject current = container; current != null; current = ParentOf(current))
            {
                Guid guid;
                try { guid = current.Guid; } catch { return null; }
                if (!seen.Add(guid)) return null;
                if (IsImported(current)) return ContainerPathOf(container);
            }
            return null;
        }

        internal KBObject LoadContainer(PlacementResolution resolution)
        {
            if (resolution == null || resolution.IsRoot) return null;
            var kb = _kbService.GetKB();
            return kb?.DesignModel.Objects.Get(resolution.Container.Guid);
        }

        /// <summary>The path of the imported module the destination sits in, else null (a creation is refused there too).</summary>
        internal string ImportedDestinationPath(PlacementResolution resolution)
            => resolution == null || resolution.IsRoot ? null : ImportedContainerPath(LoadContainer(resolution));

        private static KBObject ParentOf(KBObject obj)
        {
            try { return obj?.Parent; } catch { return null; }
        }

        private static string ModuleLabelOf(KnowledgeBase kb, KBObject container)
        {
            try
            {
                var module = container as global::Artech.Architecture.Common.Objects.Module ?? container?.Module ?? kb?.DesignModel?.RootModule;
                string name = module?.Name;
                return string.IsNullOrWhiteSpace(name) ? PlacementCatalog.RootName : name;
            }
            catch { return PlacementCatalog.RootName; }
        }

        private static Guid? ModuleGuidOf(KnowledgeBase kb, KBObject container)
        {
            try
            {
                global::Artech.Architecture.Common.Objects.Module module = container == null
                    ? kb?.DesignModel?.RootModule
                    : (container as global::Artech.Architecture.Common.Objects.Module ?? container.Module ?? kb?.DesignModel?.RootModule);
                return module?.Guid;
            }
            catch { return null; }
        }

        // An object with the moved object's name and type already living in the module the destination belongs to.
        private MoveNameConflict FindNameConflict(KnowledgeBase kb, KBObject obj, KBObject container)
            => FindNameConflict(kb, obj.Type, obj.Name, obj.Guid, container);

        /// <summary>The same name-in-module check a move makes, for an object about to be created in the destination.</summary>
        internal MoveNameConflict FindCreateNameConflict(string type, string name, PlacementResolution resolution)
        {
            var kb = _kbService.GetKB();
            Guid typeGuid = ResolveObjectTypeGuid(type);
            if (kb == null || typeGuid == Guid.Empty) return null;
            return FindNameConflict(kb, typeGuid, name, Guid.Empty, LoadContainer(resolution));
        }

        private MoveNameConflict FindNameConflict(KnowledgeBase kb, Guid type, string name, Guid excludeGuid, KBObject container)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(name)) return null;
                Guid? targetModule = ModuleGuidOf(kb, container);
                if (!targetModule.HasValue) return null;
                foreach (KBObject other in kb.DesignModel.Objects.GetByName(null, type, name))
                {
                    if (other == null || other.Guid == excludeGuid) continue;
                    if (!string.Equals(other.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                    Guid? otherModule = null;
                    try { otherModule = (other.Module ?? kb?.DesignModel?.RootModule)?.Guid; } catch { }
                    if (otherModule != targetModule) continue;
                    return new MoveNameConflict
                    {
                        Guid = other.Guid,
                        Path = ContainerPathOf(ParentOf(other)) + "/" + other.Name,
                        QualifiedName = SafeQualifiedName(other)
                    };
                }
            }
            catch (Exception ex) { Logger.Debug("MoveObject: name-conflict probe unavailable: " + ex.Message); }
            return null;
        }

        private static string SafeQualifiedName(KBObject obj)
        {
            try { return obj.QualifiedName?.ToString(); } catch { return obj?.Name; }
        }

        /// <summary>What a FRESH read (SDK caches invalidated, resolved by native identity) says about where an object is.</summary>
        internal AtomicPlacementFlow.Probe ProbeFresh(KBObject saved)
        {
            try
            {
                var fresh = FindObjectFreshByIdentity(saved);
                if (fresh == null)
                    return new AtomicPlacementFlow.Probe
                    {
                        Readable = false,
                        Error = GetLastResolutionDiagnostic()?["message"]?.ToString() ?? "the fresh read is unavailable"
                    };
                return new AtomicPlacementFlow.Probe
                {
                    Readable = true,
                    ObjectGuid = fresh.Guid,
                    ParentGuid = ContainerGuidOf(fresh),
                    ParentPath = ContainerPathOf(ParentOf(fresh))
                };
            }
            catch (Exception ex)
            {
                return new AtomicPlacementFlow.Probe { Readable = false, Error = ex.Message };
            }
        }

        /// <summary>The persisted identity of an object: GUID, native key and the path read from the SDK parent chain.</summary>
        internal JObject DescribePersistedIdentity(KBObject obj)
        {
            var identity = BuildObjectIdentity(obj);
            var parent = ParentOf(obj);
            string parentPath = ContainerPathOf(parent);
            identity["name"] = obj.Name;
            try { identity["type"] = obj.TypeDescriptor?.Name; } catch { }
            identity["parentPath"] = parentPath;
            Guid? parentGuid = ContainerGuidOf(obj);
            identity["parentGuid"] = parentGuid?.ToString();
            identity["path"] = parentPath + "/" + obj.Name;
            return identity;
        }

        // ---- move ------------------------------------------------------------------------------

        // Move an object into a Folder or Module (its KB Explorer parent). Replaces the
        // old FolderPlacementUnsupported/FolderMoveNotSupported rejects - see ObjectMover
        // for why the "SDK can't do it" verdict was wrong (facade-DLL decompilation).
        public string MoveObject(string target, string destination, string typeFilter = null, string destKind = null,
            bool dryRun = false, string baseVersion = null, bool rollbackOnFailure = true)
        {
            try
            {
                var kb = _kbService.GetKB();
                if (kb == null) return McpResponse.Err(code: "NoKb", message: "No KB open");
                if (string.IsNullOrWhiteSpace(target))
                    return McpResponse.Err(code: "BadArgs", message: "target (object to move) is required.");
                if (string.IsNullOrWhiteSpace(destination))
                    return McpResponse.Err(code: "BadArgs", message: "destination (folder or module name) is required.");

                var obj = FindObject(target, typeFilter);
                if (obj == null)
                    return McpResponse.Err(code: "ObjectNotFound", message: "Object '" + target + "' not found.",
                        hint: "Check the name/type. genexus_list_objects lists available objects.",
                        nextSteps: new JArray(McpResponse.NextStep("genexus_list_objects", null, "Lists available objects to verify the target name.")),
                        target: target);

                // The destination resolves here, through the same resolver object_atomic uses, for the
                // preview and for the write alike.
                var resolution = ResolvePlacement(destination, destKind, out PlacementCatalog catalog);
                if (!resolution.Ok) return PlacementRefused(resolution, target, "destination-resolution");

                return MoveResolved(kb, obj, target, resolution, catalog, dryRun, baseVersion, rollbackOnFailure);
            }
            catch (Exception ex)
            {
                Logger.Error("MoveObject failed for '" + target + "': " + ex.Message);
                return McpResponse.Err(code: "MoveError", message: ex.Message, target: target);
            }
        }

        /// <summary>
        /// Moves (or previews moving) <paramref name="obj"/> into an already resolved destination. The
        /// refusals, the snapshot and the receipt are the same for <paramref name="dryRun"/> and the write;
        /// only the SDK persistence, which cannot be rehearsed without writing, is left out of a preview.
        /// </summary>
        internal string MoveResolved(KnowledgeBase kb, KBObject obj, string target, PlacementResolution destination,
            PlacementCatalog catalog, bool dryRun, string baseVersion, bool rollbackOnFailure)
        {
            var sw = Stopwatch.StartNew();

            KBObject container = null;
            if (!destination.IsRoot)
            {
                container = kb.DesignModel.Objects.Get(destination.Container.Guid);
                if (container == null)
                    return McpResponse.Err(code: "DestinationNotFound",
                        message: "'" + destination.Container.Path + "' was listed but the SDK cannot load it.",
                        hint: "Refresh the KB and retry; the container may have been deleted meanwhile.", target: target,
                        errorExtra: new JObject { ["reasonCode"] = "DestinationNotFound", ["phase"] = "destination-resolution", ["persisted"] = false, ["implicitOperations"] = new JArray() });
            }

            string containerType = null;
            Guid? containerTypeGuid = null;
            if (container != null)
            {
                try { containerType = container.TypeDescriptor?.Name; } catch { }
                // The SDK rewrites a moved Folder's FolderType to this destination type.
                try { containerTypeGuid = container.Type; } catch { }
            }

            string from = ImmediateParentName(obj);
            Guid? fromParentGuid = ContainerGuidOf(obj);
            string fromPath = ContainerPathOf(ParentOf(obj));
            string toPath = destination.ResolvedPath;
            string toName = destination.IsRoot ? PlacementCatalog.RootName : destination.Container.Name;
            string beforeVersion = WriteService.ComputeVersionToken(obj);

            string strategyDetail = null;
            var facts = new MoveFacts
            {
                DryRun = dryRun,
                BaseVersion = baseVersion,
                CurrentVersion = beforeVersion,
                ObjectName = obj.Name,
                CurrentParentGuid = fromParentGuid,
                Destination = destination,
                DestinationInsideObject = !destination.IsRoot && catalog != null && catalog.IsSelfOrDescendant(destination.Container.Guid, obj.Guid),
                NameConflict = destination.IsRoot ? null : FindNameConflict(kb, obj, container),
                ObjectIsImported = IsImported(obj),
                ObjectHoldsChildren = HoldsChildren(kb, obj),
                CrossesModules = !destination.IsRoot && ModuleGuidOf(kb, container) != SafeModuleGuid(kb, obj),
                DestinationImportedPath = destination.IsRoot ? null : ImportedContainerPath(container),
                PersistStrategyAvailable = destination.IsRoot || ObjectMover.CanPersistParent(obj, container, out strategyDetail)
            };
            var verdict = MovePreflight.Evaluate(facts);
            if (!verdict.CanApply)
                return MoveRefused(verdict, target, facts, destination, strategyDetail);

            ObjectMoveSnapshot snapshot;
            try { snapshot = ObjectMoveSnapshot.Capture(obj); }
            catch (Exception ex)
            {
                return McpResponse.Err(code: "MoveSnapshotFailed",
                    message: "Could not capture every object part before moving: " + ex.Message,
                    hint: "No move was attempted.", target: target,
                    errorExtra: new JObject { ["reasonCode"] = "MoveSnapshotFailed", ["phase"] = "snapshot", ["persisted"] = false, ["implicitOperations"] = new JArray() });
            }

            string fromModule = ModuleLabelOf(kb, ParentOf(obj));
            string toModule = destination.IsRoot ? PlacementCatalog.RootName : ModuleLabelOf(kb, container);

            if (verdict.AlreadyInDestination)
            {
                var already = ProbeFresh(obj);
                return McpResponse.Ok(target: target, code: "ObjectAlreadyInDestination", result: new JObject
                {
                    ["moved"] = false,
                    ["noop"] = true,
                    ["dryRun"] = dryRun,
                    ["from"] = from,
                    ["to"] = toName,
                    ["fromPath"] = fromPath,
                    ["toPath"] = toPath,
                    ["destination"] = destination.Describe(),
                    ["containerType"] = containerType,
                    ["persisted"] = true,
                    ["verified"] = already.Readable && already.ParentGuid == (destination.IsRoot ? (Guid?)null : destination.Container.Guid),
                    ["versionToken"] = beforeVersion,
                    ["implicitOperations"] = new JArray(),
                    ["hint"] = "The object already is in this container; nothing was written."
                });
            }

            if (dryRun)
            {
                MarkReadCacheDirty(obj);
                KBObject dryFresh = null;
                try { dryFresh = kb.DesignModel.Objects.Get(obj.Guid); } catch { }
                string afterDryVersion = WriteService.ComputeVersionToken(dryFresh ?? obj);
                var dryComparison = snapshot.Compare(dryFresh ?? obj);
                bool versionUnchanged = string.Equals(beforeVersion, afterDryVersion, StringComparison.Ordinal);
                if (!versionUnchanged || !dryComparison.Equal)
                {
                    return McpResponse.Err(code: "DryRunMutationDetected",
                        message: "The persisted object changed while evaluating the move preview.",
                        hint: "The move was not executed. Re-read the object before retrying.",
                        target: target,
                        extra: new JObject
                        {
                            ["persisted"] = false,
                            ["versionUnchanged"] = versionUnchanged,
                            ["beforeVersion"] = beforeVersion,
                            ["afterVersion"] = afterDryVersion,
                            ["requestedHash"] = snapshot.Hash,
                            ["persistedHash"] = dryComparison.PersistedHash,
                            ["changedParts"] = dryComparison.ChangedParts,
                            ["implicitOperations"] = new JArray()
                        });
                }
                var preview = new JObject
                {
                    ["move"] = target,
                    ["from"] = from,
                    ["to"] = toName,
                    ["fromPath"] = fromPath,
                    ["toPath"] = toPath,
                    ["destination"] = destination.Describe(),
                    ["containerType"] = containerType,
                    ["canApply"] = true,
                    ["applyBlockers"] = new JArray(),
                    ["persisted"] = false,
                    ["verified"] = false,
                    ["previewChecked"] = new JArray("destination", "versionToken", "nameConflict", "destinationNotInsideObject", "sdkPersistPath", "contentSnapshot"),
                    ["previewCannotCheck"] = new JArray("sdkPersistResult"),
                    ["preservedParts"] = snapshot.PreservedParts,
                    ["before"] = new JObject { ["parent"] = from, ["hash"] = snapshot.Hash },
                    ["afterProjected"] = new JObject { ["parent"] = toName, ["hash"] = snapshot.Hash },
                    ["versionToken"] = beforeVersion,
                    ["versionUnchanged"] = true,
                    ["implicitOperations"] = new JArray(),
                    ["hint"] = "Re-run without dryRun and with baseVersion=<versionToken> to move. The write re-reads the object freshly, compares its position and every part, and restores the previous state (confirmed by a fresh read) if anything differs."
                };
                if (!string.Equals(fromModule, toModule, StringComparison.OrdinalIgnoreCase))
                    preview["moduleChange"] = new JObject { ["from"] = fromModule, ["to"] = toModule };
                return McpResponse.Ok(target: target, code: "DryRun", result: preview);
            }

            Helpers.ObjectMover.MoveResult res = default(Helpers.ObjectMover.MoveResult);
            KBObject fresh = null;
            KBObject originalParent = ParentOf(obj);
            string to = null;
            ObjectMoveSnapshot.Comparison comparison = null;
            JArray rejectedPropertyDifferences = null;
            bool committed = false;
            using (var tx = kb.BeginTransaction())
            {
                try
                {
                    MarkReadCacheDirty(obj);
                    var current = kb.DesignModel.Objects.Get(obj.Guid) ?? obj;
                    string transactionVersion = WriteService.ComputeVersionToken(current);
                    if (!string.IsNullOrWhiteSpace(baseVersion)
                        && !string.Equals(baseVersion, transactionVersion, StringComparison.Ordinal))
                    {
                        return McpResponse.Err(code: "VersionConflict",
                            message: "The object changed before the move transaction acquired it; no write was committed.",
                            hint: "Re-read the object and retry with the new versionToken.", target: target,
                            errorExtra: new JObject
                            {
                                ["reasonCode"] = "VersionConflict", ["phase"] = "transaction",
                                ["expectedVersion"] = baseVersion, ["currentVersion"] = transactionVersion,
                                ["persisted"] = false, ["implicitOperations"] = new JArray()
                            },
                            extra: new JObject { ["baseVersion"] = baseVersion, ["currentVersion"] = transactionVersion });
                    }

                    res = Helpers.ObjectMover.SetParentAndSave(current, container);
                    if (!res.Ok)
                        throw new MoveStepException("SdkPersistRefused", "the SDK refused to persist the new parent: " + (res.Error ?? "no persist path answered")
                            + AttemptsSuffix(res.Attempts));

                    // Validate inside the transaction before exposing the new placement.
                    // This is especially important for the SaveWithParent fallback on U16,
                    // which can rebuild default Procedure parts while reporting success.
                    // Issue #238: the destination name is passed so the SDK's Folder-destination
                    // re-parent bookkeeping (a generic property VALUE re-pointed at the new
                    // folder, e.g. Properties/Property[2]/Value[1]) is tolerated here while
                    // every authored divergence still fails the move.
                    MarkReadCacheDirty(current);
                    var pending = kb.DesignModel.Objects.Get(obj.Guid) ?? current;
                    var pendingComparison = snapshot.Compare(pending, toName, containerTypeGuid, Array.Empty<string>());
                    if (!IsInContainer(pending, destination))
                        throw new MoveStepException("ParentNotPersisted", "the SDK did not persist the requested parent inside the move transaction"
                            + " (it still reports '" + ContainerPathOf(ParentOf(pending)) + "')" + AttemptsSuffix(res.Attempts));
                    if (!pendingComparison.Equal)
                    {
                        rejectedPropertyDifferences = pendingComparison.PropertyDifferences;
                        throw new MoveStepException("ContentChangedInTransaction", "the SDK changed object content inside the move transaction: "
                            + string.Join(", ", pendingComparison.ChangedParts.Values<string>()));
                    }

                    tx.Commit();
                    committed = true;
                    PropertyService.InvalidatePropertyCache(current);
                }
                catch (Exception ex)
                {
                    if (!committed) try { tx.Rollback(); } catch { }
                    var rollback = VerifyMoveRollback(kb, obj, snapshot, fromParentGuid);
                    if (!(rollback["verified"]?.ToObject<bool>() ?? false) && rollbackOnFailure)
                        rollback = CompensateMoveRollback(kb, obj.Guid, originalParent, snapshot, fromParentGuid);
                    string reason = (ex as MoveStepException)?.ReasonCode ?? "TransactionFailed";
                    return McpResponse.Err(code: "MoveFailed",
                        message: "Could not safely persist the move into '" + toPath + "': " + ex.Message,
                        hint: MoveFailureHint(reason),
                        target: target,
                        errorExtra: new JObject
                        {
                            ["reasonCode"] = reason,
                            ["phase"] = "transaction",
                            ["strategy"] = res.Strategy,
                            ["expectedParent"] = new JObject { ["guid"] = destination.IsRoot ? null : destination.Container.Guid.ToString(), ["path"] = toPath },
                            ["actualParent"] = new JObject { ["guid"] = fromParentGuid?.ToString(), ["path"] = (rollback["parent"]?["path"]?.ToString()) ?? fromPath },
                            ["saved"] = res.Ok,
                            ["persisted"] = false,
                            ["verified"] = false,
                            ["requestedHash"] = snapshot.Hash,
                            ["persistedHash"] = rollback["persistedHash"]?.DeepClone(),
                            ["propertyDifferences"] = rejectedPropertyDifferences ?? new JArray(),
                            ["rollback"] = rollback,
                            ["implicitOperations"] = new JArray()
                        });
                }
            }

            // Re-read after commit as an independent persistence barrier. If the SDK
            // reports a different persisted state now, restore the complete snapshot.
            bool freshRead = false;
            try
            {
                fresh = FindObjectFreshByIdentity(obj);
                freshRead = fresh != null;
                if (fresh == null)
                {
                    MarkReadCacheDirty(obj);
                    fresh = kb.DesignModel.Objects.Get(obj.Guid) ?? obj;
                }
                to = ImmediateParentName(fresh);
                // Issue #238: same placement-aware tolerance as the in-transaction check;
                // the Folder-destination property echo must not fail post-commit either.
                comparison = snapshot.Compare(fresh, toName, containerTypeGuid, Array.Empty<string>());
            }
            catch (Exception ex)
            {
                var rollback = rollbackOnFailure
                    ? CompensateMoveRollback(kb, obj.Guid, originalParent, snapshot, fromParentGuid)
                    : VerifyMoveRollback(kb, obj, snapshot, fromParentGuid, attempted: false);
                return McpResponse.Err(code: "MoveVerificationFailed",
                    message: "The moved object could not be re-read for persistence verification: " + ex.Message,
                    hint: "The pre-move snapshot was restored when rollbackOnFailure was enabled; see rollback.",
                    target: target,
                    errorExtra: new JObject
                    {
                        ["reasonCode"] = "MoveVerificationFailed", ["phase"] = "verification", ["strategy"] = res.Strategy,
                        ["saved"] = true, ["persisted"] = false, ["verified"] = false,
                        ["requestedHash"] = snapshot.Hash,
                        ["persistedHash"] = rollback["persistedHash"]?.DeepClone(),
                        ["rollback"] = rollback,
                        ["implicitOperations"] = new JArray()
                    });
            }

            bool finalMoved = IsInContainer(fresh, destination);
            if (!finalMoved || !comparison.Equal)
            {
                var rollback = rollbackOnFailure
                    ? CompensateMoveRollback(kb, obj.Guid, originalParent, snapshot, fromParentGuid)
                    : VerifyMoveRollback(kb, obj, snapshot, fromParentGuid, attempted: false);
                string reason = !finalMoved ? "MoveNotPersisted" : "MoveContentNotPreserved";
                return McpResponse.Err(code: reason,
                    message: !finalMoved
                        ? "The SDK reported success, but the destination was not persisted: a fresh read finds the object in '" + ContainerPathOf(ParentOf(fresh)) + "', not in '" + toPath + "'."
                        : "The object moved, but persisted content changed (" +
                          string.Join(", ", comparison.ChangedParts.Values<string>()) + "); success was withheld.",
                    hint: "The pre-move snapshot was restored when rollbackOnFailure was enabled; see rollback.", target: target,
                    errorExtra: new JObject
                    {
                        ["reasonCode"] = reason, ["phase"] = "verification", ["strategy"] = res.Strategy,
                        ["expectedParent"] = new JObject { ["guid"] = destination.IsRoot ? null : destination.Container.Guid.ToString(), ["path"] = toPath },
                        ["actualParent"] = new JObject { ["guid"] = ContainerGuidOf(fresh)?.ToString(), ["path"] = ContainerPathOf(ParentOf(fresh)) },
                        ["saved"] = true, ["persisted"] = false, ["verified"] = false,
                        ["requestedHash"] = snapshot.Hash,
                        ["persistedHash"] = comparison.PersistedHash,
                        ["changedParts"] = comparison.ChangedParts,
                        ["propertyDifferences"] = comparison.PropertyDifferences,
                        ["rollback"] = rollback,
                        ["implicitOperations"] = new JArray()
                    },
                    extra: new JObject { ["from"] = from, ["to"] = to });
            }

            try
            {
                var idx = _kbService?.GetIndexCache();
                if (idx != null)
                {
                    // Drop the stale (old-parent) hierarchy cache + child slot first, else
                    // UpdateEntry reuses the cached parent and list/inspect keep showing the
                    // old folder (the object moved on disk but the index would lie).
                    idx.InvalidateHierarchy(fresh.Guid);
                    idx.UpdateEntry(fresh);
                }
            }
            catch (Exception ex) { Logger.Error("MoveObject: index refresh failed for " + target + ": " + ex.Message); }
            RefreshDescendantsAfterContainerMove(kb, fresh);

            Logger.Info(string.Format("Moved '{0}' from '{1}' to '{2}' via {3} in {4}ms", target, from, to, res.Strategy, sw.ElapsedMilliseconds));
            var result = new JObject
            {
                ["moved"] = target,
                ["from"] = from,
                ["to"] = to,
                ["fromPath"] = fromPath,
                ["toPath"] = ContainerPathOf(ParentOf(fresh)),
                ["destination"] = destination.Describe(),
                ["containerType"] = containerType,
                ["strategy"] = res.Strategy,
                ["saved"] = true,
                ["persisted"] = true,
                ["verified"] = true,
                ["verification"] = new JObject
                {
                    ["freshRead"] = freshRead,
                    ["positionVerified"] = true,
                    ["partsIdentical"] = true,
                    ["partsCompared"] = snapshot.PreservedParts.Count
                },
                ["identity"] = DescribePersistedIdentity(fresh),
                ["preservedParts"] = snapshot.PreservedParts,
                ["requestedHash"] = snapshot.Hash,
                ["persistedHash"] = comparison?.PersistedHash,
                ["previousVersionToken"] = beforeVersion,
                ["versionToken"] = WriteService.ComputeVersionToken(fresh),
                ["generated"] = false,
                ["implicitOperations"] = new JArray()
            };
            if (!string.Equals(fromModule, toModule, StringComparison.OrdinalIgnoreCase))
                result["moduleChange"] = new JObject { ["from"] = fromModule, ["to"] = toModule };
            return McpResponse.Ok(target: target, code: "ObjectMovedAndVerified", result: result);
        }

        private static string AttemptsSuffix(IReadOnlyList<string> attempts)
            => attempts == null || attempts.Count == 0 ? string.Empty : " [" + string.Join("; ", attempts) + "]";

        private static string MoveFailureHint(string reasonCode)
        {
            switch (reasonCode)
            {
                case "SdkPersistRefused":
                    return "The SDK would not persist the new parent for this object. Nothing was left changed (see rollback); move it in the GeneXus IDE (KB Explorer > Move) if the SDK keeps refusing.";
                case "ParentNotPersisted":
                    return "The SDK accepted the call but kept the old parent. Nothing was left changed (see rollback).";
                case "ContentChangedInTransaction":
                    return "The SDK rewrote object content while re-parenting it, so the move was aborted. See propertyDifferences/rollback.";
                default:
                    return "The move was aborted and the previous state restored (see rollback). Inspect the worker log for the SDK exception.";
            }
        }

        // A refused preflight: the same envelope for dryRun=true and for the write.
        internal static string MoveRefused(MoveVerdict verdict, string target, MoveFacts facts, PlacementResolution destination, string strategyDetail)
        {
            var first = verdict.First;
            var errorExtra = new JObject
            {
                ["reasonCode"] = first.Code,
                ["phase"] = "preflight",
                ["blockers"] = verdict.BlockersJson(),
                ["placement"] = destination.Describe(),
                ["persisted"] = false,
                ["implicitOperations"] = new JArray()
            };
            if (!string.IsNullOrEmpty(strategyDetail)) errorExtra["strategyDetail"] = strategyDetail;
            JObject extra = null;
            if (first.Code == MovePreflight.VersionConflictCode)
            {
                errorExtra["expectedVersion"] = facts.BaseVersion;
                errorExtra["currentVersion"] = facts.CurrentVersion;
                extra = new JObject { ["baseVersion"] = facts.BaseVersion, ["currentVersion"] = facts.CurrentVersion };
            }
            else if (first.Code == MovePreflight.BaseVersionRequiredCode)
            {
                errorExtra["currentVersion"] = facts.CurrentVersion;
                extra = new JObject { ["currentVersion"] = facts.CurrentVersion };
            }
            return McpResponse.Err(code: first.Code, message: first.Message, hint: first.Remedy, target: target,
                errorExtra: errorExtra, extra: extra);
        }

        // The fresh object sits where the destination says (by identity, never by name).
        private static bool IsInContainer(KBObject obj, PlacementResolution destination)
        {
            Guid? actual = ContainerGuidOf(obj);
            return destination.IsRoot ? !actual.HasValue : actual.HasValue && actual.Value == destination.Container.Guid;
        }

        // Immediate KB Explorer parent (folder/module) name of an object, normalized so
        // the Root Module reads as "Root Module" (its SDK Name is empty / DesignModel).
        private static string ImmediateParentName(KBObject obj)
        {
            try
            {
                var parent = obj?.Parent;
                if (parent == null) return "Root Module";
                string ptype = null;
                try { ptype = parent.TypeDescriptor?.Name; } catch { }
                if (string.Equals(ptype, "DesignModel", StringComparison.OrdinalIgnoreCase))
                    return "Root Module";
                string pname = null;
                try { pname = parent.Name; } catch { }
                return string.IsNullOrWhiteSpace(pname) ? "Root Module" : pname;
            }
            catch { return null; }
        }

        // After a failed move: a FRESH read (SDK caches invalidated, resolved by native identity) of the
        // object, compared with the pre-move snapshot by position (identity) and by every part.
        private JObject VerifyMoveRollback(KnowledgeBase kb, KBObject seed, ObjectMoveSnapshot snapshot,
            Guid? expectedParentGuid, bool attempted = true)
        {
            try
            {
                KBObject restored = FindObjectFreshByIdentity(seed);
                bool freshRead = restored != null;
                if (restored == null)
                {
                    MarkReadCacheDirty(seed);
                    restored = kb.DesignModel.Objects.Get(seed.Guid) ?? seed;
                }
                var comparison = snapshot.Compare(restored);
                Guid? actualParent = ContainerGuidOf(restored);
                bool parentRestored = actualParent == expectedParentGuid;
                bool stateMatchesSnapshot = parentRestored && comparison.Equal;
                return new JObject
                {
                    ["attempted"] = attempted,
                    ["verified"] = attempted && stateMatchesSnapshot,
                    ["freshRead"] = freshRead,
                    ["stateMatchesSnapshot"] = stateMatchesSnapshot,
                    ["parentRestored"] = parentRestored,
                    ["contentRestored"] = comparison.Equal,
                    ["parent"] = new JObject { ["guid"] = actualParent?.ToString(), ["path"] = ContainerPathOf(ParentOf(restored)) },
                    ["persistedHash"] = comparison.PersistedHash,
                    ["changedParts"] = comparison.ChangedParts
                };
            }
            catch (Exception ex)
            {
                return new JObject { ["attempted"] = attempted, ["verified"] = false, ["error"] = ex.Message };
            }
        }

        private JObject CompensateMoveRollback(KnowledgeBase kb, Guid objectGuid, KBObject originalParent,
            ObjectMoveSnapshot snapshot, Guid? expectedParentGuid)
        {
            try
            {
                using (var tx = kb.BeginTransaction())
                {
                    bool committed = false;
                    try
                    {
                        var current = kb.DesignModel.Objects.Get(objectGuid);
                        if (current == null) throw new InvalidOperationException("The moved object no longer exists.");
                        snapshot.RestoreObject(current);
                        current.Dirty = true;
                        current.Save();
                        snapshot.RestoreParts(current);
                        if (originalParent != null)
                        {
                            var moveBack = Helpers.ObjectMover.SetParentAndSave(current, originalParent);
                            if (!moveBack.Ok) throw new InvalidOperationException("Parent rollback failed: " + moveBack.Error);
                        }
                        tx.Commit();
                        committed = true;
                    }
                    finally { if (!committed) try { tx.Rollback(); } catch { } }
                }

                var seed = kb.DesignModel.Objects.Get(objectGuid);
                var verified = VerifyMoveRollback(kb, seed, snapshot, expectedParentGuid);
                verified["compensating"] = true;
                return verified;
            }
            catch (Exception ex)
            {
                return new JObject
                {
                    ["attempted"] = true,
                    ["compensating"] = true,
                    ["verified"] = false,
                    ["error"] = ex.Message
                };
            }
        }
    }
}
