using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Artech.Architecture.Common.Objects;
using Artech.Architecture.Common.Services;
using Artech.Architecture.Common.Services.TeamDevData.Server;
using Artech.Architecture.Common.Services.TeamDev;
using Artech.Udm.Framework;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;
using Newtonsoft.Json.Linq;
using SdkServices = Artech.Architecture.Common.Services.Services;
using ClientTeamDev = Artech.Architecture.Common.Services.TeamDevData.Client;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// GxServer (Team Development) WRITE surface for genexus_gxserver — commit,
    /// update, lock, resolve. Sibling to <see cref="GxServerSyncService"/> (the
    /// read-only status/pending/conflicts/history path); shares the same
    /// SDK-service-resolution idiom (SdkServices.TryGetService, guard every
    /// null/throw path, never crash the worker).
    ///
    /// Resolves <see cref="IGXserverService"/> for Commit/GetUpdateFile/Lock,
    /// and <see cref="ITeamDevClientService"/> (same service the read path
    /// already resolves) for MarkAsResolved.
    ///
    /// Feasibility note (spike, v2.14): the SDK's write-path data-transfer
    /// objects (<see cref="ServerCommitData"/>/<see cref="ServerUpdateData"/>/
    /// <see cref="ServerLockData"/>) are plain settable POCOs — confirmed via
    /// reflection against Artech.Architecture.Common.dll, no hidden IDE/session
    /// context required to *construct* them (Model + KBAlias + the action's own
    /// fields is enough). What remains unverified is whether
    /// <see cref="IGXserverService"/> itself resolves in a headless worker and
    /// whether Commit/GetUpdateFile/Lock succeed without the authenticated
    /// Team-Dev session the IDE's login flow normally establishes — no
    /// GXserver-linked KB was available locally to exercise this live. Every
    /// call is defensively wrapped; failures surface as clean error envelopes
    /// rather than crashing the worker.
    /// </summary>
    public class GxServerWriteService
    {
        private readonly KbService _kb;

        public GxServerWriteService(KbService kb)
        {
            _kb = kb;
        }

        public string Run(string action, JObject args)
        {
            KnowledgeBase kb;
            try
            {
                kb = _kb?.GetKB() as KnowledgeBase;
            }
            catch (Exception ex)
            {
                return McpResponse.Err(code: "KbUnavailable", message: ex.Message, hint: "Open a KB first (genexus_kb action=open).");
            }
            if (kb == null)
            {
                return McpResponse.Err(code: "KbUnavailable", message: "No KB is open in this worker.", hint: "Open a KB first (genexus_kb action=open).");
            }

            ITeamDevClientService tdSvc = GxMcp.Worker.Helpers.SdkServiceResolver.Resolve<ITeamDevClientService>();
            if (tdSvc == null)
            {
                return McpResponse.Err(
                    code: "GxServerServiceUnavailable",
                    message: "The GeneXus SDK's ITeamDevClientService is not registered in this worker session (self-heal retries were exhausted).",
                    hint: "Restart the worker (genexus_worker_reload mode=hard) and retry.");
            }

            bool linked;
            try { linked = tdSvc.IsLinkedKB(kb); }
            catch (Exception ex)
            {
                return McpResponse.Err(code: "GxServerCheckFailed", message: ex.Message, hint: "Check the worker log for details.");
            }
            if (!linked)
            {
                return McpResponse.Err(
                    code: "NotLinked",
                    message: "This KB is not linked to a GeneXus Server instance.",
                    hint: "Link the KB to a GeneXus Server first (IDE: Team Development > Connect), then retry.");
            }

            KBModel model = kb.DesignModel;
            string kbPath = _kb?.GetKbPath();
            string kbAlias = Environment.GetEnvironmentVariable("GX_KB_ALIAS")
                             ?? (string.IsNullOrEmpty(kbPath) ? string.Empty : Path.GetFileName(kbPath.TrimEnd('\\', '/')));

            switch (action)
            {
                case "commit": return DoCommit(tdSvc, kb, model, kbAlias, args);
                case "update": return DoUpdate(tdSvc, kb, model, kbAlias, args);
                case "lock": return DoLock(model, kbAlias, args);
                case "resolve": return DoResolve(tdSvc, kb, model, args);
                default:
                    return McpResponse.Err(code: "BadAction", message: "Unknown write action '" + action + "'.");
            }
        }

        private string DoCommit(ITeamDevClientService tdSvc, KnowledgeBase kb, KBModel model, string kbAlias, JObject args)
        {
            string message = args?["message"]?.ToString();
            if (string.IsNullOrWhiteSpace(message))
            {
                return McpResponse.Err(
                    code: "BadArgs",
                    message: "message is required for commit.",
                    hint: "Pass action=commit, message=\"<commit comment>\".");
            }

            var targetsToken = args?["targets"] as JArray;
            HashSet<string> wanted = null;
            if (targetsToken != null && targetsToken.Count > 0)
            {
                wanted = new HashSet<string>(
                    targetsToken.Select(t => t?.ToString()).Where(s => !string.IsNullOrWhiteSpace(s)),
                    StringComparer.OrdinalIgnoreCase);
                if (wanted.Count == 0)
                    return McpResponse.Err(code: "BadArgs", message: "targets must contain at least one non-empty object name.");
            }

            // Read the pending changelist via ITeamDevClientService — which resolves in a
            // headless worker (IGXserverService does NOT; live-verified 2026-07-14).
            List<KBObjectHistory> pending;
            try
            {
                pending = (tdSvc.GetLocalChanges(model) ?? Enumerable.Empty<KBObjectHistory>())
                    .Where(h => h != null).ToList();
            }
            catch (Exception ex)
            {
                return McpResponse.Err(code: "CommitFailed", message: "Could not read the pending changelist: " + ex.Message, hint: "Check the worker log.");
            }

            // Build the EXACT list of objects to commit. A partial commit passes an explicit
            // ObjectList to SendChanges, so it can never accidentally push another developer's
            // unrelated pending work — only the named objects reach the server.
            List<KBObjectHistory> toCommit;
            if (wanted != null)
            {
                var pendingNames = new HashSet<string>(
                    pending.Select(h => SafeStr(() => h.ObjectName)).Where(n => !string.IsNullOrEmpty(n)),
                    StringComparer.OrdinalIgnoreCase);
                var unknown = wanted.Where(w => !pendingNames.Contains(w)).ToList();
                if (unknown.Count > 0)
                    return McpResponse.Err(
                        code: "NoMatchingPending",
                        message: "These targets are not in the pending changelist: " + string.Join(", ", unknown) + ". Nothing was committed.",
                        hint: "Check action=pending for the exact committable object names, then retry.");
                toCommit = pending.Where(h => wanted.Contains(SafeStr(() => h.ObjectName) ?? "\0")).ToList();
            }
            else
            {
                toCommit = pending;
            }

            if (toCommit.Count == 0)
                return McpResponse.Err(code: "NothingToCommit", message: "The pending changelist is empty.", hint: "Edit or create an object first (action=pending to inspect).");

            // Commit talks to the server → credentials required (url auto from the linked KB).
            var creds = ReadCreds(tdSvc, kb, args);
            if (string.IsNullOrWhiteSpace(creds.Url))
                return CredentialsRequired("commit");
            string authToken = AcquireAuthToken(tdSvc, kb, model, creds, out string authErr);

            // Clear any "ignore for commit" flag on the objects we intend to send — the IDE's
            // "Ignored Objects" list is exactly these, and SendChanges silently skips ignored
            // objects (a partial/whole send of only-ignored objects then returns false).
            foreach (var h in toCommit)
            {
                try { tdSvc.EnableForCommit(model, h.Key); } catch { /* best-effort */ }
            }

            try
            {
                var scd = new ClientTeamDev.SendChangesData
                {
                    Model = model,
                    // Identify WHICH server KB — the url alone isn't enough (a server hosts many
                    // KBs). Without this the send has no target and SendChanges returns false.
                    KnowledgeBase = SafeStr(() => tdSvc.GetRemoteKBName(kb)),
                    Comments = message,
                    ObjectList = toCommit,
                    Options = GxMcp.Worker.Compatibility.SdkCompat.SilentExportOptions(),
                    MinUnselectedTimestamp = DateTime.Now,
                    Url = creds.Url,
                    User = creds.User,
                    Password = creds.Password,
#if !GXMCP_SDK_EV3
                    AuthenticationToken = string.IsNullOrEmpty(authToken) ? creds.Token : authToken
#endif
                };

                bool ok = tdSvc.SendChanges(scd);
                if (!ok)
                {
                    // On failure, attach a conclusive diagnostic: did the server accept the
                    // credentials, is there a broken commit, and were the target objects staged.
                    var diag = new JObject { ["authTokenAcquired"] = !string.IsNullOrEmpty(authToken) };
                    if (!string.IsNullOrEmpty(authErr)) diag["authError"] = authErr;
#if GXMCP_SDK_EV3
                    try { diag["serverReachableWithCreds"] = tdSvc.CheckConectivitySecure(scd); }
#else
                    try { diag["serverReachableWithCreds"] = tdSvc.GetServerInfo(creds.Url, creds.User, creds.Password) != null; }
#endif
                    catch (Exception dx) { diag["serverInfoError"] = dx.Message; }
                    try { diag["hasBrokenCommits"] = tdSvc.HasBrokenCommits(model); } catch { }
                    try
                    {
                        var states = new JArray();
                        foreach (var h in toCommit)
                            states.Add(new JObject { ["name"] = SafeStr(() => h.ObjectName), ["insertedForCommit"] = TryBool(() => (bool)((dynamic)tdSvc).IsInsertedForCommit(model, h.Key)) });
                        diag["objects"] = states;
                    }
                    catch { }
                    // Fold the diagnostic into the message too: the gateway trims error envelopes
                    // to code/message/hint, so `extra` alone wouldn't reach the caller.
                    return McpResponse.Err(
                        code: "CommitFailed",
                        message: "SendChanges returned false. " + diag.ToString(Newtonsoft.Json.Formatting.None),
                        hint: "authError set → token/credentials; serverReachableWithCreds=false → auth; true → server rejected the changelist.",
                        extra: diag);
                }

                var committedNames = toCommit
                    .Select(h => SafeStr(() => h.ObjectName)).Where(n => !string.IsNullOrEmpty(n)).ToList();

                var result = new JObject
                {
                    ["committed"] = true,
                    ["message"] = message,
                    ["committedObjects"] = new JArray(committedNames),
                    ["committedCount"] = committedNames.Count,
                    ["remoteVersion"] = SafeStr(() => tdSvc.RemoteVersionName(model)),
                    ["source"] = "sdk:ITeamDevClientService.SendChanges"
                };
                if (wanted != null)
                {
                    result["partial"] = true;
                    result["committedTargets"] = new JArray(wanted);
                }
                return McpResponse.Ok(code: "GxServerCommitCompleted", result: result);
            }
            catch (Exception ex)
            {
                return McpResponse.Err(code: "CommitFailed", message: ex.Message, hint: "Check the worker log for details.");
            }
        }

        private string DoUpdate(ITeamDevClientService tdSvc, KnowledgeBase kb, KBModel model, string kbAlias, JObject args)
        {
            bool exportAll = args?["all"]?.ToObject<bool?>() ?? false;
#if GXMCP_SDK_EV3
            // ReceiveChangesData has no ExportAll in Evolution 3: refuse instead of silently
            // receiving only the pending changes.
            if (exportAll)
                return Evolution3Unsupported.Refuse("genexus_gxserver", "update", "receiving every object (all=true)");
#endif

            // Update applies the server's pending changes into the local KB via
            // ITeamDevClientService.JustReceiveChanges(...).Update() — which resolves in a
            // headless worker (IGXserverService.GetUpdateFile does NOT; live-verified
            // 2026-07-14). Talks to the server, so credentials are required (url auto-resolves
            // from the linked KB; user/password from args or GXMCP_TEAMDEV_* env). Conflicting
            // objects are left flagged for action=resolve.
            var creds = ReadCreds(tdSvc, kb, args);
            if (string.IsNullOrWhiteSpace(creds.Url))
                return CredentialsRequired("update");
            string authToken = AcquireAuthToken(tdSvc, kb, model, creds, out _);

            try
            {
                var rc = new ClientTeamDev.ReceiveChangesData
                {
                    Model = model,
                    // Identify WHICH server KB (see commit) — without it JustReceiveChanges
                    // can't resolve the remote and returns null.
                    KnowledgeBase = SafeStr(() => tdSvc.GetRemoteKBName(kb)),
                    Url = creds.Url,
                    User = creds.User,
                    Password = creds.Password,
#if !GXMCP_SDK_EV3
                    AuthenticationToken = string.IsNullOrEmpty(authToken) ? creds.Token : authToken,
                    ExportAll = exportAll,
#endif
                    IncludeReferencesDependencies = true
                };

                ITeamDevClientUpdate updater = tdSvc.JustReceiveChanges(rc);
                if (updater == null)
                    return McpResponse.Err(code: "UpdateFailed", message: "JustReceiveChanges returned no updater.", hint: "Verify server credentials and that the KB is linked.");

                bool ok = updater.Update();

                var conflicts = CollectConflictNames(tdSvc, model);
                var result = new JObject
                {
                    ["applied"] = ok,
                    ["conflictCount"] = conflicts.Count,
                    ["conflicts"] = new JArray(conflicts),
                    ["source"] = "sdk:ITeamDevClientUpdate"
                };
                if (conflicts.Count > 0)
                    result["hint"] = "Update applied with conflicts. Resolve them: action=resolve, strategy=mine|theirs|automerge, targets=[" + string.Join(",", conflicts) + "].";

                // Conflicts are an expected outcome, not a failure — report them as Ok so the
                // agent proceeds to resolve. A false `ok` with NO conflicts is a real failure.
                if (!ok && conflicts.Count == 0)
                    return McpResponse.Err(code: "UpdateFailed", message: "Update returned false with no conflicts reported.", hint: "Check the worker log; verify credentials and connectivity.");

                return McpResponse.Ok(code: "GxServerUpdateApplied", result: result);
            }
            catch (Exception ex)
            {
                return McpResponse.Err(code: "UpdateFailed", message: ex.Message, hint: "Check the worker log for details.");
            }
        }

        private string DoLock(KBModel model, string kbAlias, JObject args)
        {
            string target = args?["target"]?.ToString();
            if (string.IsNullOrWhiteSpace(target))
            {
                return McpResponse.Err(
                    code: "BadArgs",
                    message: "target is required for lock.",
                    hint: "Pass action=lock, target=<object name>.");
            }

            IGXserverService svc = GxMcp.Worker.Helpers.SdkServiceResolver.Resolve<IGXserverService>();
            if (svc == null) return ServerServiceUnavailable();

            try
            {
                var data = new ServerLockData
                {
                    Model = model,
                    KBAlias = kbAlias,
                    FilePath = target
                };

                ServerLockData result = svc.Lock(data);
                return McpResponse.Ok(
                    code: "GxServerLockAcquired",
                    result: new JObject
                    {
                        ["target"] = target,
                        ["locked"] = result != null,
                        ["note"] = "ServerLockData.FilePath expects the KB-relative export path, not necessarily the bare object name — unverified against a live GXserver.",
                        ["source"] = "sdk:IGXserverService"
                    });
            }
            catch (Exception ex)
            {
                return McpResponse.Err(code: "LockFailed", message: ex.Message, hint: "Check the worker log for details.");
            }
        }

        private string DoResolve(ITeamDevClientService tdSvc, KnowledgeBase kb, KBModel model, JObject args)
        {
            var targetsToken = args?["targets"] as JArray;
            if (targetsToken == null || targetsToken.Count == 0)
            {
                return McpResponse.Err(
                    code: "BadArgs",
                    message: "targets (non-empty array of object names) is required for resolve.",
                    hint: "Pass action=resolve, targets=[\"Customer\", ...], strategy=mine|theirs|automerge.");
            }

            var wanted = new HashSet<string>(
                targetsToken.Select(t => t?.ToString()).Where(s => !string.IsNullOrEmpty(s)),
                StringComparer.OrdinalIgnoreCase);
            if (wanted.Count == 0)
            {
                return McpResponse.Err(code: "BadArgs", message: "targets must contain at least one non-empty object name.");
            }

            // Strategy chooses which version wins per conflicted object:
            //  mine      — keep the local object, discard the incoming server version (creds-free);
            //  theirs    — overwrite local with the server version (needs credentials);
            //  automerge — 3-way merge base+mine+theirs via IMergeService (needs credentials).
            // Default is the safe, creds-free "mine" (previous behavior only cleared the flag).
            string strategy = (args?["strategy"]?.ToString() ?? "mine").Trim().ToLowerInvariant();
            if (strategy != "mine" && strategy != "theirs" && strategy != "automerge")
                return McpResponse.Err(code: "BadArgs", message: "strategy must be one of: mine, theirs, automerge.", hint: "Default is 'mine' (keep local).");

            try
            {
                // Collect the conflicted entities matching the requested targets, keyed so we
                // can fetch base/theirs per object and MarkAsResolved at the end.
                var matches = new List<(string name, EntityKey key)>();
                foreach (var ct in new[] { UpdateConflict.YesMustOverwrite, UpdateConflict.YesWithAutoMerge })
                {
                    IEnumerable<Entity> raw = tdSvc.GetConflictEntities(model, ct);
                    if (raw == null) continue;
                    foreach (Entity e in raw)
                    {
                        // Resolve the real object name via the KBObject behind the key (the
                        // entity's own Name isn't the object name) — must match how
                        // action=conflicts reports names so the caller's targets line up.
                        string name = ResolveConflictName(model, e);
                        if (name != null && wanted.Contains(name))
                            matches.Add((name, e.Key));
                    }
                }

                if (matches.Count == 0)
                {
                    return McpResponse.Err(
                        code: "NoMatchingConflicts",
                        message: "None of the requested targets have an active conflict.",
                        hint: "Check action=conflicts for current conflict object names.");
                }

                var applied = new JArray();

                // theirs / automerge need to fetch the server version → credentials + services.
                Creds creds = default;
                IMergeService mergeSvc = null;
                if (strategy == "theirs" || strategy == "automerge")
                {
                    creds = ReadCreds(tdSvc, kb, args);
                    if (string.IsNullOrWhiteSpace(creds.Url))
                        return CredentialsRequired("resolve strategy=" + strategy);
                    if (strategy == "automerge")
                    {
                        mergeSvc = GxMcp.Worker.Helpers.SdkServiceResolver.Resolve<IMergeService>();
                        if (mergeSvc == null)
                            return McpResponse.Err(code: "GxServerServiceUnavailable", message: "IMergeService is not registered in this worker session.", hint: "Restart the worker (genexus_worker_reload mode=hard) and retry.");
                    }

                    foreach (var (name, key) in matches)
                    {
                        var outcome = new JObject { ["object"] = name, ["strategy"] = strategy };
                        try
                        {
                            KBObject mine = KBObject.Get(model, key);
                            KBObject theirs = tdSvc.GetServerObject(new ClientTeamDev.ServerObjectData(model, mine)
                            {
                                Url = creds.Url,
                                User = creds.User,
                                Password = creds.Password,
#if !GXMCP_SDK_EV3
                                AuthenticationToken = creds.Token
#endif
                            });

                            if (theirs == null)
                            {
                                outcome["ok"] = false;
                                outcome["error"] = "Could not fetch the server version of the object.";
                            }
                            else if (strategy == "theirs")
                            {
                                theirs.EnsureSave();
                                WriteService.NotePerTargetWrite(name);
                                outcome["ok"] = true;
                            }
                            else // automerge
                            {
                                KBObject baseObj = SafeObj(() => tdSvc.GetLastSynchedObject(model, key));
                                KBObject merged = baseObj != null
                                    ? mergeSvc.MergeObjects(baseObj, mine, theirs, model)
                                    : mergeSvc.MergeObjects(mine, theirs, model, false);
                                if (merged == null)
                                {
                                    outcome["ok"] = false;
                                    outcome["error"] = "MergeObjects returned null.";
                                }
                                else
                                {
                                    merged.EnsureSave();
                                    WriteService.NotePerTargetWrite(name);
                                    outcome["ok"] = true;
                                    outcome["threeWay"] = baseObj != null;
                                }
                            }
                        }
                        catch (Exception exObj)
                        {
                            outcome["ok"] = false;
                            outcome["error"] = exObj.Message;
                        }
                        applied.Add(outcome);
                    }
                }
                else
                {
                    // mine: no object mutation — the local version already wins; just clear the flag.
                    foreach (var (name, _) in matches)
                        applied.Add(new JObject { ["object"] = name, ["strategy"] = "mine", ["ok"] = true });
                }

                // Clear the conflict flags for every matched object (idempotent per the SDK).
                bool ok = tdSvc.MarkAsResolved(model, matches.Select(m => m.key).ToList());
                if (!ok)
                    return McpResponse.Err(code: "ResolveFailed", message: "MarkAsResolved returned false.", hint: "Check the worker log for details.");

                int failed = applied.Count(o => o["ok"]?.ToObject<bool?>() == false);
                return McpResponse.Ok(
                    code: "GxServerConflictsResolved",
                    result: new JObject
                    {
                        ["resolved"] = true,
                        ["strategy"] = strategy,
                        ["count"] = matches.Count,
                        ["failedObjects"] = failed,
                        ["objects"] = applied,
                        ["source"] = "sdk:ITeamDevClientService"
                    });
            }
            catch (Exception ex)
            {
                return McpResponse.Err(code: "ResolveFailed", message: ex.Message, hint: "Check the worker log for details.");
            }
        }

        // --- Team-Dev credential + helper plumbing (mutating server ops) ------------------

        private struct Creds { public string Url; public string User; public string Password; public string Token; }

        // Server credentials for the mutating paths (update-apply, resolve theirs/automerge).
        // URL auto-resolves from the linked KB; user/password/token come from the call args or
        // GXMCP_TEAMDEV_{URL,USER,PASSWORD,TOKEN} env. Never logged (secrets — global rule §10).
        private static Creds ReadCreds(ITeamDevClientService tdSvc, KnowledgeBase kb, JObject args)
        {
            string url = args?["url"]?.ToString();
            if (string.IsNullOrWhiteSpace(url)) url = Environment.GetEnvironmentVariable("GXMCP_TEAMDEV_URL");
            if (string.IsNullOrWhiteSpace(url)) { try { url = tdSvc.GetServerUrl(kb); } catch { } }

            string user = args?["user"]?.ToString();
            if (string.IsNullOrWhiteSpace(user)) user = Environment.GetEnvironmentVariable("GXMCP_TEAMDEV_USER");
            string pass = args?["password"]?.ToString();
            if (string.IsNullOrWhiteSpace(pass)) pass = Environment.GetEnvironmentVariable("GXMCP_TEAMDEV_PASSWORD");
            string token = args?["token"]?.ToString();
            if (string.IsNullOrWhiteSpace(token)) token = Environment.GetEnvironmentVariable("GXMCP_TEAMDEV_TOKEN");

            return new Creds { Url = url, User = user, Password = pass, Token = token };
        }

        // The GeneXus SDK authenticates server ops from an OAuth token, NOT from the inline
        // User/Password on the data objects — the IDE obtains the token at login and the SDK
        // reuses it; headless never logged in, so every call goes out unauthenticated ("requer
        // credenciais de usuário"). The UI.Framework login path doesn't resolve headless, so we
        // acquire the token directly via TokenAuthorizationManager.GetToken (a static in the
        // TeamDevClient BL that performs the OAuth exchange), set it as the default token on the
        // Common service, and return it so the data objects carry it too. Returns null on
        // failure (bad creds / unreachable server).
        private static string AcquireAuthToken(ITeamDevClientService tdSvc, KnowledgeBase kb, KBModel model, Creds creds, out string authError)
        {
            authError = null;
#if GXMCP_SDK_EV3
            // GeneXus Server X Evolution 3 has no token endpoint: the SDK authenticates each
            // call from the User/Password carried on the data objects.
            return null;
#else
            try
            {
                // Pre-fill url / remote-KB name / instance from the KB's own server link, then
                // add the credentials.
                var td = new ClientTeamDev.TeamDevelopmentData(model);
                if (string.IsNullOrWhiteSpace(td.Url)) td.Url = creds.Url;
                if (string.IsNullOrWhiteSpace(td.KnowledgeBase)) td.KnowledgeBase = SafeStr(() => tdSvc.GetRemoteKBName(kb));
                // The OAuth token endpoint requires the username as "AuthType\user"
                // (TokenRequestBodyFields splits on '\\' and indexes [0]=authType, [1]=user —
                // a bare username throws IndexOutOfRange). This KB's Authentication Type is
                // "Local" (see the IDE's Team Development settings), so prefix it when the
                // caller passed a bare user. An explicit "Domain\user" is used as-is.
                string authUser = creds.User ?? string.Empty;
                if (!authUser.Contains("\\"))
                {
                    string authType = Environment.GetEnvironmentVariable("GXMCP_TEAMDEV_AUTHTYPE");
                    if (string.IsNullOrWhiteSpace(authType)) authType = "Local";
                    authUser = authType + "\\" + authUser;
                }
                td.User = authUser;
                td.Password = creds.Password;

                var comm = new Artech.Packages.TeamDevClient.BL.Data.CommunicationData(td);
                string token = Artech.Packages.TeamDevClient.BL.Connectivity.TokenAuthorizationManager.GetToken(comm, true);
                if (!string.IsNullOrEmpty(token))
                {
                    try { tdSvc.SetDefaultAuthenticationToken(token); } catch { }
                }
                return token;
            }
            catch (Exception ex)
            {
                var sb = new System.Text.StringBuilder();
                for (Exception e = ex; e != null; e = e.InnerException)
                    sb.Append(e.GetType().Name).Append(": ").Append(e.Message).Append(" || ");
                authError = sb.ToString();
                GxMcp.Worker.Helpers.Logger.Error("[GxServer] AcquireAuthToken failed: " + authError);
                return null;
            }
#endif
        }

        private static string CredentialsRequired(string op)
        {
            return McpResponse.Err(
                code: "CredentialsRequired",
                message: "Server credentials are required for " + op + " (this operation talks to the GeneXus Server).",
                hint: "Pass user + password (and optionally url) in the call, or set GXMCP_TEAMDEV_USER / GXMCP_TEAMDEV_PASSWORD (and GXMCP_TEAMDEV_URL if the KB link doesn't resolve it).");
        }

        private static List<string> CollectConflictNames(ITeamDevClientService tdSvc, KBModel model)
        {
            var names = new List<string>();
            try
            {
                foreach (var ct in new[] { UpdateConflict.YesMustOverwrite, UpdateConflict.YesWithAutoMerge })
                {
                    IEnumerable<Entity> raw = tdSvc.GetConflictEntities(model, ct);
                    if (raw == null) continue;
                    foreach (Entity e in raw)
                    {
                        string n = ResolveConflictName(model, e);
                        if (!string.IsNullOrEmpty(n) && !names.Contains(n)) names.Add(n);
                    }
                }
            }
            catch { /* best-effort */ }
            return names;
        }

        private static KBObject SafeObj(Func<KBObject> f)
        {
            try { return f(); } catch { return null; }
        }

        private static string ServerServiceUnavailable()
        {
            return McpResponse.Err(
                code: "GxServerServiceUnavailable",
                message: "The GeneXus SDK's IGXserverService is not registered in this worker session (self-heal retries were exhausted).",
                hint: "Restart the worker (genexus_worker_reload mode=hard) and retry.");
        }


        private static string SafeStr(Func<string> f)
        {
            try { return f(); } catch { return null; }
        }

        // The real object name behind a conflict entity (its own ToString/Name is the type,
        // not the object). Resolve via the KBObject at the entity key.
        private static string ResolveConflictName(KBModel model, Entity e)
        {
            try
            {
                var o = KBObject.Get(model, e.Key);
                if (o != null && !string.IsNullOrEmpty(o.Name)) return o.Name;
            }
            catch { }
            return SafeStr(() => e.Name);
        }

        private static bool? TryBool(Func<bool> f)
        {
            try { return f(); } catch { return null; }
        }
    }
}
