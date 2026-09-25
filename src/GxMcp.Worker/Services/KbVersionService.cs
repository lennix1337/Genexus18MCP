using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Artech.Architecture.Common.Helpers;
using Artech.Architecture.Common.Objects;
using Artech.Architecture.Common.Services;
using GxMcp.Worker.Models;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// genexus_kb_version — KB model-version management (Create Version / Branch /
    /// Activate / Revert) over the GeneXus SDK's static
    /// <see cref="KBVersionHelper"/>. This is the version-tree surface, distinct
    /// from <c>genexus_versioning</c> (object-level history/undo/time-travel).
    ///
    /// action=list and action=changed_objects are read-only: list enumerates
    /// <see cref="KBVersion.GetAll(KnowledgeBase)"/> against the open KB and
    /// reports which one <see cref="KBVersion.GetActive(KnowledgeBase)"/> says is
    /// active. freeze/branch/set_active/revert mutate the KB's version tree via
    /// the same code path the IDE's Version menu uses.
    /// changed_objects compares the active Design model with a frozen model snapshot
    /// through the SDK's version-model surfaces and IComparerService content checks;
    /// it never queries internal tables.
    ///
    /// See docs/sdk-probe/INDEX.md (KBVersionHelper / KBVersion) for the
    /// reflected surface this was built against.
    /// </summary>
    public class KbVersionService
    {
        private readonly KbService _kb;

        public KbVersionService(KbService kb)
        {
            _kb = kb;
        }

        public string Run(JObject args)
        {
            string action = args?["action"]?.ToString();
            if (string.IsNullOrWhiteSpace(action)) action = "list";
            action = action.Trim().ToLowerInvariant();

            KnowledgeBase kbase;
            try
            {
                kbase = _kb?.GetKB() as KnowledgeBase;
            }
            catch (Exception ex)
            {
                return McpResponse.Err(code: "KbVersionFailed", message: ex.Message, hint: "Check the worker log for details.");
            }

            if (kbase == null)
            {
                return McpResponse.Err(
                    code: "NoOpenKb",
                    message: "No KB is currently open.",
                    hint: "Open a KB first (genexus_kb action=open).");
            }

            switch (action)
            {
                case "list": return ListVersions(kbase);
                case "changed_objects": return ChangedObjects(kbase, args);
                case "freeze": return Freeze(kbase, args);
                case "branch": return Branch(kbase, args);
                case "set_active": return SetActive(kbase, args);
                case "revert": return Revert(kbase, args);
                default:
                    return McpResponse.Err(
                        code: "BadAction",
                        message: "Unknown action '" + action + "'. Expected one of: list, changed_objects, freeze, branch, set_active, revert.",
                        hint: "Pass action=list to enumerate versions first.");
            }
        }

        private string ListVersions(KnowledgeBase kbase)
        {
            try
            {
                KBVersion active = SafeGetActive(kbase);
                var versions = new JArray();
                foreach (KBVersion v in KBVersion.GetAll(kbase))
                {
                    versions.Add(DescribeVersion(v, active));
                }
                return McpResponse.Ok(
                    code: "KbVersionListRetrieved",
                    result: new JObject
                    {
                        ["versions"] = versions,
                        ["activeVersion"] = active?.Name,
                        ["source"] = "sdk:KBVersion"
                    });
            }
            catch (Exception ex)
            {
                return McpResponse.Err(code: "KbVersionFailed", message: ex.Message, hint: "Check the worker log for details.");
            }
        }

        private string ChangedObjects(KnowledgeBase kbase, JObject args)
        {
            string requestedVersion = args?["fromVersion"]?.ToString();
            KBVersion frozen;
            string resolveError = ResolveFrozenVersion(kbase, requestedVersion, out frozen);
            if (resolveError != null) return resolveError;

            int offset = args?["offset"]?.ToObject<int?>() ?? 0;
            int limit = args?["limit"]?.ToObject<int?>() ?? 50;
            if (offset < 0 || limit <= 0)
            {
                return McpResponse.Err(
                    code: "BadArgs",
                    message: "offset must be >= 0 and limit must be > 0.",
                    hint: "Use offset=0 and a limit between 1 and 200.");
            }
            limit = Math.Min(limit, 200);

            try
            {
                KBModel design = kbase.DesignModel;
                if (design == null || design.Objects == null)
                    return ChangedObjectsNotSupported();

                FrozenObjectSnapshot frozenSnapshot = TryGetFrozenObjects(design, frozen);
                if (frozenSnapshot?.Objects == null)
                    return ChangedObjectsNotSupported();

                var baselineByIdentity = BuildObjectMap(frozenSnapshot.Objects, out int baselineMetadataExcluded);
                var changes = new List<JObject>();
                int designMetadataExcluded = 0;
                IComparerService comparer = null;
                var seenDesign = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (KBObject current in design.Objects.GetAll() ?? Enumerable.Empty<KBObject>())
                {
                    string identity = StableObjectIdentity(current);
                    if (string.IsNullOrWhiteSpace(identity) || string.IsNullOrWhiteSpace(current?.Name))
                    {
                        designMetadataExcluded++;
                        continue;
                    }
                    if (!seenDesign.Add(identity)) continue;

                    string changeType;
                    if (!baselineByIdentity.TryGetValue(identity, out var previous))
                        changeType = "NEW";
                    else if (!TryCompareContent(ref comparer, previous, current, out bool changed))
                        return ChangedObjectsNotSupported();
                    else if (changed)
                        changeType = "CHANGED";
                    else
                        continue;

                    changes.Add(DescribeChangedObject(current, changeType, identity));
                }

                changes = changes
                    .OrderBy(item => item["name"]?.ToString() ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(item => item["type"]?.ToString() ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(item => item["guid"]?.ToString() ?? item["entityKey"]?.ToString() ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                int total = changes.Count;
                var page = changes.Skip(offset).Take(limit).ToArray();
                int nextOffset = offset + page.Length;
                var result = new JObject
                {
                    ["fromVersion"] = frozen.Name,
                    ["fromVersionFrozen"] = true,
                    ["activeModel"] = "Design",
                    ["changeDetection"] = "sdk:KBObject.LastUpdate(candidate)+IComparerService.AreEqualInContent",
                    ["items"] = new JArray(page),
                    ["offset"] = offset,
                    ["limit"] = limit,
                    ["returned"] = page.Length,
                    ["total"] = total,
                    ["hasMore"] = nextOffset < total,
                    ["nextOffset"] = nextOffset < total ? nextOffset : (JToken)null,
                    ["metadataExcluded"] = baselineMetadataExcluded + designMetadataExcluded,
                    ["baselineSource"] = frozenSnapshot.Source,
                    ["source"] = frozenSnapshot.Source
                };
                return McpResponse.Ok(code: "ChangedObjectsListed", result: result);
            }
            catch (Exception ex)
            {
                GxMcp.Worker.Helpers.Logger.Debug("[KB-VERSION-DELTA] SDK comparison unavailable: " + ex.Message);
                return ChangedObjectsNotSupported();
            }
        }

        private static string ResolveFrozenVersion(KnowledgeBase kbase, string requestedName, out KBVersion frozen)
        {
            frozen = null;
            if (!string.IsNullOrWhiteSpace(requestedName))
            {
                string error = ResolveVersion(kbase, requestedName, out frozen);
                if (error != null) return error;
                if (!IsFrozen(frozen))
                {
                    return McpResponse.Err(
                        code: "VersionNotFrozen",
                        message: "Version '" + requestedName + "' is not frozen; changed_objects requires a frozen version.",
                        hint: "Pass fromVersion with a frozen version returned by action=list.");
                }
                return null;
            }

            try
            {
                frozen = KBVersion.GetAll(kbase)
                    .Where(IsFrozen)
                    .OrderByDescending(v => SafeDate(() => v.LastUpdate))
                    .ThenBy(v => v.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
            }
            catch (Exception ex)
            {
                GxMcp.Worker.Helpers.Logger.Debug("[KB-VERSION-DELTA] Could not resolve latest frozen version: " + ex.Message);
            }

            return frozen != null
                ? null
                : McpResponse.Err(
                    code: "NoFrozenVersion",
                    message: "No frozen KB version is available for comparison.",
                    hint: "Create a frozen version with genexus_kb_version action=freeze, then retry changed_objects.");
        }

        private string Freeze(KnowledgeBase kbase, JObject args)
        {
            string name = args?["name"]?.ToString();
            if (string.IsNullOrWhiteSpace(name))
            {
                return McpResponse.Err(code: "BadArgs", message: "name is required for action=freeze.", hint: "Pass name=<new version name>.");
            }
            string description = args?["description"]?.ToString() ?? string.Empty;
            bool backupModel = args?["backupModel"]?.ToObject<bool?>() ?? false;

            KBVersion parent;
            string err = ResolveOrActive(kbase, args?["parentVersion"]?.ToString(), out parent);
            if (err != null) return err;

            try
            {
                KBVersion created = KBVersionHelper.FreezeModel(name, description, parent, backupModel);
                KBVersion active = SafeGetActive(kbase);
                return McpResponse.Ok(code: "KbVersionFrozen", result: DescribeVersion(created, active));
            }
            catch (Exception ex)
            {
                return McpResponse.Err(
                    code: "KbVersionFailed",
                    message: ex.Message,
                    hint: "Check the worker log for details. The name may already exist, or the parent version may be invalid.");
            }
        }

        private string Branch(KnowledgeBase kbase, JObject args)
        {
            string name = args?["name"]?.ToString();
            if (string.IsNullOrWhiteSpace(name))
            {
                return McpResponse.Err(code: "BadArgs", message: "name is required for action=branch.", hint: "Pass name=<new branch name>.");
            }
            string description = args?["description"]?.ToString() ?? string.Empty;
            bool includeEnvironments = args?["includeEnvironments"]?.ToObject<bool?>() ?? false;

            KBVersion parent;
            string err = ResolveOrActive(kbase, args?["parentVersion"]?.ToString(), out parent);
            if (err != null) return err;

            try
            {
                KBVersion created = BranchModelCompatible(name, description, parent, includeEnvironments);
                KBVersion active = SafeGetActive(kbase);
                return McpResponse.Ok(code: "KbVersionBranched", result: DescribeVersion(created, active));
            }
            catch (Exception ex)
            {
                return McpResponse.Err(
                    code: "KbVersionFailed",
                    message: ex.Message,
                    hint: "Check the worker log for details. The name may already exist, or the parent version may be invalid.");
            }
        }

        private string SetActive(KnowledgeBase kbase, JObject args)
        {
            string targetName = args?["targetVersion"]?.ToString();
            if (string.IsNullOrWhiteSpace(targetName))
            {
                return McpResponse.Err(code: "BadArgs", message: "targetVersion is required for action=set_active.", hint: "Call action=list to see existing version names.");
            }

            KBVersion target;
            string err = ResolveVersion(kbase, targetName, out target);
            if (err != null) return err;

            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(WriteDestinationGuard.VersionVariable)) && target.IsFrozen)
                return McpResponse.Err(code: "WriteDestinationVersionNotWritable", message: "The pinned target version is frozen; activation was not performed.", hint: "Choose a writable version explicitly in the profile.");

            try
            {
                if (args?["autoUpdate"] != null)
                {
                    bool autoUpdate = args["autoUpdate"].ToObject<bool>();
                    KBVersionHelper.SetAsActive(target, autoUpdate);
                }
                else
                {
                    KBVersionHelper.SetAsActive(target);
                }
                return McpResponse.Ok(code: "KbVersionActivated", result: DescribeVersion(target, target));
            }
            catch (Exception ex)
            {
                return McpResponse.Err(code: "KbVersionFailed", message: ex.Message, hint: "Check the worker log for details.");
            }
        }

        private string Revert(KnowledgeBase kbase, JObject args)
        {
            string toName = args?["targetVersion"]?.ToString();
            if (string.IsNullOrWhiteSpace(toName))
            {
                return McpResponse.Err(
                    code: "BadArgs",
                    message: "targetVersion is required for action=revert (the version to revert TO).",
                    hint: "Call action=list to see existing version names.");
            }

            KBVersion to;
            string errTo = ResolveVersion(kbase, toName, out to);
            if (errTo != null) return errTo;

            KBVersion from;
            string errFrom = ResolveOrActive(kbase, args?["fromVersion"]?.ToString(), out from);
            if (errFrom != null) return errFrom;

            try
            {
                KBVersionHelper.Revert(from, to);
                KBVersion active = SafeGetActive(kbase);
                return McpResponse.Ok(
                    code: "KbVersionReverted",
                    result: new JObject
                    {
                        ["from"] = from?.Name,
                        ["to"] = to?.Name,
                        ["activeVersion"] = active?.Name
                    });
            }
            catch (Exception ex)
            {
                return McpResponse.Err(code: "KbVersionFailed", message: ex.Message, hint: "Check the worker log for details.");
            }
        }

        private sealed class FrozenObjectSnapshot
        {
            public IEnumerable<KBObject> Objects { get; set; }
            public string Source { get; set; }
        }

        private static FrozenObjectSnapshot TryGetFrozenObjects(KBModel design, KBVersion version)
        {
            try
            {
                object versionModel = version == null
                    ? null
                    : version.GetType().GetProperty("Model", BindingFlags.Public | BindingFlags.Instance)?.GetValue(version, null);
                if (versionModel is KBModel typedVersionModel && typedVersionModel.Objects != null)
                    return new FrozenObjectSnapshot
                    {
                        Objects = typedVersionModel.Objects.GetAll(),
                        Source = "sdk:KBVersion.Model"
                    };
                // GeneXus 16 exposes KBVersion.Model through a different
                // framework base type. Keep the same SDK authority without a
                // compile-time cast that only exists in newer majors.
                object versionObjects = versionModel?.GetType().GetProperty("Objects", BindingFlags.Public | BindingFlags.Instance)?.GetValue(versionModel, null);
                MethodInfo versionGetAll = versionObjects?.GetType().GetMethod("GetAll", BindingFlags.Public | BindingFlags.Instance);
                object all = versionGetAll?.Invoke(versionObjects, null);
                if (all is IEnumerable<KBObject> typedObjects)
                    return new FrozenObjectSnapshot
                    {
                        Objects = typedObjects,
                        Source = "sdk:KBVersion.Model(reflection)"
                    };
                if (all is IEnumerable enumerableObjects)
                {
                    var fallbackObjects = enumerableObjects.Cast<object>().OfType<KBObject>().ToList();
                    if (fallbackObjects.Count > 0)
                        return new FrozenObjectSnapshot
                        {
                            Objects = fallbackObjects,
                            Source = "sdk:KBVersion.Model(reflection-enumerable)"
                        };
                }
            }
            catch (Exception ex)
            {
                GxMcp.Worker.Helpers.Logger.Debug("[KB-VERSION-DELTA] Frozen KBVersion.Model unavailable: " + ex.Message);
            }

            try
            {
                Type type = typeof(KBModel).Assembly.GetType(
                    "Artech.Architecture.Common.Objects.KBModelVersionObjects", false);
                ConstructorInfo ctor = type?.GetConstructor(new[] { typeof(KBModel), typeof(DateTime) });
                MethodInfo getAll = type?.GetMethod("GetAll", BindingFlags.Public | BindingFlags.Instance);
                if (ctor == null || getAll == null) return null;
                object view = ctor.Invoke(new object[] { design, version.LastUpdate });
                return new FrozenObjectSnapshot
                {
                    Objects = (getAll.Invoke(view, null) as IEnumerable)?.Cast<object>().OfType<KBObject>().ToArray(),
                    Source = "sdk:KBModelVersionObjects"
                };
            }
            catch (Exception ex)
            {
                GxMcp.Worker.Helpers.Logger.Debug("[KB-VERSION-DELTA] KBModelVersionObjects unavailable: " + ex.Message);
                return null;
            }
        }

        private static Dictionary<string, KBObject> BuildObjectMap(IEnumerable<KBObject> objects, out int metadataExcluded)
        {
            metadataExcluded = 0;
            var result = new Dictionary<string, KBObject>(StringComparer.OrdinalIgnoreCase);
            foreach (KBObject obj in objects ?? Enumerable.Empty<KBObject>())
            {
                string identity = StableObjectIdentity(obj);
                if (string.IsNullOrWhiteSpace(identity) || string.IsNullOrWhiteSpace(obj?.Name))
                {
                    metadataExcluded++;
                    continue;
                }
                if (!result.ContainsKey(identity)) result[identity] = obj;
            }
            return result;
        }

        private static JObject DescribeChangedObject(KBObject obj, string changeType, string identity)
        {
            var result = new JObject
            {
                ["changeType"] = changeType,
                ["name"] = obj.Name,
                ["type"] = SafeString(() => obj.TypeDescriptor?.Name),
                ["lastUpdate"] = SafeDate(() => obj.LastUpdate).ToUniversalTime().ToString("o"),
                ["identitySource"] = identity.StartsWith("guid:", StringComparison.OrdinalIgnoreCase) ? "guid" : "entityKey"
            };
            if (identity.StartsWith("guid:", StringComparison.OrdinalIgnoreCase))
                result["guid"] = identity.Substring("guid:".Length);
            else
                result["entityKey"] = identity.Substring("entityKey:".Length);
            string parentPath = ParentPath(obj);
            if (!string.IsNullOrWhiteSpace(parentPath)) result["parentPath"] = parentPath;
            return result;
        }

        private static string StableObjectIdentity(KBObject obj)
        {
            if (obj == null) return null;
            try
            {
                if (obj.Guid != Guid.Empty) return "guid:" + obj.Guid.ToString("D");
            }
            catch { }
            try
            {
                string key = obj.Key?.ToString();
                if (!string.IsNullOrWhiteSpace(key)) return "entityKey:" + key;
            }
            catch { }
            return null;
        }

        internal static bool IsContentCandidate(DateTime baselineLastUpdate, DateTime currentLastUpdate)
        {
            // When either timestamp is unavailable, compare content rather than
            // reintroducing the old token-only false-positive/false-negative split.
            if (baselineLastUpdate == DateTime.MinValue || currentLastUpdate == DateTime.MinValue)
                return true;
            return currentLastUpdate > baselineLastUpdate;
        }

        private static bool TryCompareContent(
            ref IComparerService comparer,
            KBObject baseline,
            KBObject current,
            out bool changed)
        {
            changed = false;
            if (!IsContentCandidate(SafeDate(() => baseline.LastUpdate), SafeDate(() => current.LastUpdate)))
                return true;

            comparer = comparer ?? GxMcp.Worker.Helpers.SdkServiceResolver.Resolve<IComparerService>();
            if (comparer == null)
                return false;

            try
            {
                changed = !comparer.AreEqualInContent(baseline, current, CompareObjectOptions.Default);
                return true;
            }
            catch (Exception ex)
            {
                GxMcp.Worker.Helpers.Logger.Debug("[KB-VERSION-DELTA] Content comparison unavailable: " + ex.Message);
                return false;
            }
        }

        private static string ParentPath(KBObject obj)
        {
            var names = new List<string>();
            var seen = new HashSet<Guid>();
            try
            {
                object parent = obj?.Parent;
                while (parent is KBObject parentObject)
                {
                    if (parentObject.Guid != Guid.Empty && !seen.Add(parentObject.Guid)) break;
                    if (!string.IsNullOrWhiteSpace(parentObject.Name)) names.Add(parentObject.Name);
                    parent = parentObject.Parent;
                }
            }
            catch { }
            names.Reverse();
            return string.Join("/", names);
        }

        private static string ChangedObjectsNotSupported()
        {
            return McpResponse.Err(
                code: "ChangedObjectsNotSupported",
                message: "This GeneXus SDK worker cannot expose a read-only Design-versus-frozen object inventory.",
                hint: "The inventory requires a frozen SDK model and IComparerService.AreEqualInContent; SQL/internal model tables are not part of the MCP contract.");
        }

        private static bool IsFrozen(KBVersion version)
        {
            try { return version != null && version.IsFrozen; } catch { return false; }
        }

        private static DateTime SafeDate(Func<DateTime> getter)
        {
            try { return getter(); } catch { return DateTime.MinValue; }
        }

        private static string SafeString(Func<string> getter)
        {
            try { return getter(); } catch { return null; }
        }

        // ----- shared helpers -----

        /// <summary>
        /// Resolves a version by name when given, otherwise falls back to the
        /// currently active version. Used for freeze/branch's parentVersion and
        /// revert's fromVersion, all of which default to "current state" when
        /// the caller doesn't pin an explicit name.
        /// </summary>
        private static string ResolveOrActive(KnowledgeBase kbase, string name, out KBVersion version)
        {
            version = null;
            if (string.IsNullOrWhiteSpace(name))
            {
                version = SafeGetActive(kbase);
                if (version == null)
                {
                    return McpResponse.Err(
                        code: "KbVersionFailed",
                        message: "Could not resolve the KB's active version.",
                        hint: "Pass parentVersion/fromVersion explicitly, or call action=list first.");
                }
                return null;
            }
            return ResolveVersion(kbase, name, out version);
        }

        private static string ResolveVersion(KnowledgeBase kbase, string name, out KBVersion version)
        {
            version = null;
            try
            {
                version = KBVersion.Get(kbase, name);
            }
            catch (Exception ex)
            {
                return McpResponse.Err(code: "KbVersionFailed", message: ex.Message, hint: "Check the worker log for details.");
            }
            if (version == null)
            {
                return McpResponse.Err(
                    code: "VersionNotFound",
                    message: "Version '" + name + "' not found.",
                    hint: "Call action=list to see existing version names.",
                    nextSteps: new JArray { McpResponse.NextStep("genexus_kb_version", new JObject { ["action"] = "list" }, "List existing versions.") });
            }
            return null;
        }

        private static KBVersion SafeGetActive(KnowledgeBase kbase)
        {
            try { return KBVersion.GetActive(kbase); } catch { return null; }
        }

        private static bool SafeSameVersion(KBVersion a, KBVersion b)
        {
            if (a == null || b == null) return false;
            try { return a.Guid == b.Guid; } catch { return false; }
        }

        private static JObject DescribeVersion(KBVersion v, KBVersion active)
        {
            if (v == null) return null;
            var result = new JObject
            {
                ["name"] = v.Name,
                ["description"] = SafeStr(() => v.Description),
                ["isFrozen"] = SafeBool(() => v.IsFrozen),
                ["isBranch"] = SafeBool(() => v.IsBranch),
                ["isTrunk"] = SafeBool(() => v.IsTrunk),
                ["isActive"] = SafeSameVersion(v, active),
                ["parent"] = SafeStr(() => v.Parent?.Name),
                ["lastUpdate"] = SafeStr(() => v.LastUpdate.ToUniversalTime().ToString("o")),
                ["lastUpdateSource"] = "sdk:KBVersion.LastUpdate",
                ["userName"] = SafeStr(() => v.UserName)
            };
            AddCreationTimestampMetadata(result);
            return result;
        }

        internal static void AddCreationTimestampMetadata(JObject result)
        {
            // KBVersion exposes LastUpdate but no creation timestamp. Do not
            // infer creation from it: a later edit can change LastUpdate.
            result["createdAt"] = JValue.CreateNull();
            result["createdAtAvailable"] = false;
            result["createdAtSource"] = "unavailable:sdk-KBVersion";
            result["createdAtNote"] = "The GeneXus SDK does not expose a reliable creation timestamp for KB versions.";
        }

        private static string SafeStr(Func<string> f)
        {
            try { return f(); } catch { return null; }
        }

        private static bool SafeBool(Func<bool> f)
        {
            try { return f(); } catch { return false; }
        }

        private static KBVersion BranchModelCompatible(string name, string description, KBVersion parent, bool includeEnvironments)
        {
            var method = typeof(KBVersionHelper).GetMethod("BranchModel");
            if (method == null) throw new InvalidOperationException("KBVersionHelper.BranchModel method not found");
            var pars = method.GetParameters();
            if (pars.Length >= 4 && pars[3].ParameterType == typeof(bool))
            {
                return (KBVersion)method.Invoke(null, new object[] { name, description, parent, includeEnvironments });
            }
            else
            {
                // GX16: Func<string, Guid> environmentGuidProvider
                Func<string, Guid> provider = includeEnvironments ? (Func<string, Guid>)(_ => Guid.NewGuid()) : null;
                return (KBVersion)method.Invoke(null, new object[] { name, description, parent, provider });
            }
        }
    }
}
