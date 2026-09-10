using System;
using Artech.Architecture.Common.Helpers;
using Artech.Architecture.Common.Objects;
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
    /// action=list is read-only: enumerates
    /// <see cref="KBVersion.GetAll(KnowledgeBase)"/> against the open KB and
    /// reports which one <see cref="KBVersion.GetActive(KnowledgeBase)"/> says is
    /// active. freeze/branch/set_active/revert mutate the KB's version tree via
    /// the SDK — the same code path the IDE's Version menu uses.
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
                case "freeze": return Freeze(kbase, args);
                case "branch": return Branch(kbase, args);
                case "set_active": return SetActive(kbase, args);
                case "revert": return Revert(kbase, args);
                default:
                    return McpResponse.Err(
                        code: "BadAction",
                        message: "Unknown action '" + action + "'. Expected one of: list, freeze, branch, set_active, revert.",
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
                KBVersion created = KBVersionHelper.BranchModel(name, description, parent, includeEnvironments);
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
    }
}
