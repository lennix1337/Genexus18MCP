using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace GxMcp.Gateway
{
    public sealed class KbResolutionException : Exception
    {
        public string Code { get; }
        public KbResolutionException(string code, string message) : base(message) { Code = code; }
    }

    /// <summary>
    /// The KB-selection decision expressed as data: which handle was chosen, why, and
    /// — when nothing was chosen — the error code <see cref="KbResolver.Resolve"/>
    /// would have thrown.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This exists because <c>genexus_whoami</c> used to re-implement the resolution
    /// policy as a second tree, and the two copies drifted: whoami built an
    /// alias→handle map declared→open→known so a <c>known</c> entry overwrote an
    /// <c>open</c> one, and in legacy mode it fell through to
    /// <c>single-open</c>/<c>declared-first</c> when the configured default did not
    /// resolve. Both made whoami report a selection the next call would not honour.
    /// With the decision expressed once, a caller that reports it cannot disagree.
    /// </para>
    /// <para>
    /// <c>SelectionState</c> uses the published whoami vocabulary and adds nothing to
    /// it: <c>valid</c> (a handle was chosen), <c>invalid</c> (an explicit alias was
    /// given and does not resolve), <c>conflicting</c> (a configured default contradicts
    /// the sole open KB) and <c>absent</c> (nothing was selected, so the caller must
    /// supply context). <c>KB_NOT_FOUND</c> on an unresolvable configured default is
    /// <c>absent</c>: nothing is selected, which is exactly what it means.
    /// </para>
    /// </remarks>
    public sealed class KbSelectionResult
    {
        private KbSelectionResult(
            KbHandle? handle,
            string selectionSource,
            string selectionState,
            string? errorCode,
            string? errorMessage)
        {
            Handle = handle;
            SelectionSource = selectionSource;
            SelectionState = selectionState;
            ErrorCode = errorCode;
            ErrorMessage = errorMessage;
        }

        /// <summary>The selected KB, or <c>null</c> when nothing was selected.</summary>
        public KbHandle? Handle { get; }

        /// <summary>
        /// Published whoami vocabulary: <c>explicit-arg</c>, <c>session-select</c>,
        /// <c>single-open</c>, <c>config-default</c>, <c>declared-first</c>, <c>none</c>.
        /// </summary>
        public string SelectionSource { get; }

        /// <summary>
        /// Published whoami vocabulary: <c>valid</c>, <c>absent</c>, <c>invalid</c>,
        /// <c>conflicting</c>.
        /// </summary>
        public string SelectionState { get; }

        /// <summary>
        /// True when no handle was selected, so the caller must pass KB context. A
        /// caller with extra context of its own (whoami's session lease) may tighten
        /// this; nothing here can loosen it.
        /// </summary>
        public bool ContextRequired => Handle == null;

        /// <summary>The code <see cref="KbResolver.Resolve"/> throws; null when resolved.</summary>
        public string? ErrorCode { get; }

        /// <summary>The message <see cref="KbResolver.Resolve"/> throws; null when resolved.</summary>
        public string? ErrorMessage { get; }

        public bool IsResolved => Handle != null;

        internal static KbSelectionResult Resolved(KbHandle handle, string selectionSource)
            => new KbSelectionResult(handle, selectionSource, "valid", null, null);

        internal static KbSelectionResult Unresolved(
            string selectionSource,
            string selectionState,
            string errorCode,
            string errorMessage)
            => new KbSelectionResult(null, selectionSource, selectionState, errorCode, errorMessage);
    }

    public sealed class KbResolver
    {
        private readonly Configuration _config;

        public KbResolver(Configuration config) { _config = config; }

        public KbHandle Resolve(string? kbArg, IReadOnlyCollection<KbHandle> openKbs)
            => Resolve(kbArg, openKbs, null, null, out _);

        // issue #26 P3: `knownKbs` (optional) is the durable set of aliases the user has
        // opened this session — it survives worker recycles, unlike `openKbs`.
        public KbHandle Resolve(string? kbArg, IReadOnlyCollection<KbHandle> openKbs, IReadOnlyCollection<KbHandle>? knownKbs)
            => Resolve(kbArg, openKbs, knownKbs, null, out _);

        public KbHandle Resolve(
            string? kbArg,
            IReadOnlyCollection<KbHandle> openKbs,
            IReadOnlyCollection<KbHandle>? knownKbs,
            string? sessionDefaultAlias)
            => Resolve(kbArg, openKbs, knownKbs, sessionDefaultAlias, out _);

        public KbHandle Resolve(
            string? kbArg,
            IReadOnlyCollection<KbHandle> openKbs,
            IReadOnlyCollection<KbHandle>? knownKbs,
            string? sessionDefaultAlias,
            bool sessionContextInitialized)
            => Resolve(kbArg, openKbs, knownKbs, sessionDefaultAlias, out _);

        /// <summary>
        /// Resolves the KB for a call, throwing the code and message the decision
        /// carries when nothing was selected. It adds no decision of its own: the tree
        /// lives in <see cref="Describe"/>, so a caller that reports the selection
        /// (whoami) and a caller that enforces it cannot disagree.
        /// </summary>
        public KbHandle Resolve(
            string? kbArg,
            IReadOnlyCollection<KbHandle> openKbs,
            IReadOnlyCollection<KbHandle>? knownKbs,
            string? sessionDefaultAlias,
            out string selectionSource)
        {
            var selection = Describe(kbArg, openKbs, knownKbs, sessionDefaultAlias);
            selectionSource = selection.SelectionSource;
            if (selection.Handle != null) return selection.Handle;

            throw new KbResolutionException(selection.ErrorCode!, selection.ErrorMessage!);
        }

        /// <summary>
        /// The single KB-selection policy: explicit <c>kb</c> argument → MCP-session
        /// selection → strict or legacy fallback. It has no throwing path, so a
        /// diagnostic surface can report the decision instead of re-deriving it.
        /// </summary>
        /// <remarks>
        /// Order matters and is the published contract: an explicit alias wins, then the
        /// session selection, then — in strict mode — the sole open KB unless a
        /// configured default contradicts it, and in legacy mode — the configured
        /// default (open before declared before known), else the sole open KB, else the
        /// first declared KB.
        /// </remarks>
        public KbSelectionResult Describe(
            string? kbArg,
            IReadOnlyCollection<KbHandle> openKbs,
            IReadOnlyCollection<KbHandle>? knownKbs,
            string? sessionDefaultAlias)
        {
            if (!string.IsNullOrWhiteSpace(kbArg))
                return ExplicitSelection(kbArg!, openKbs, knownKbs, "explicit-arg", fromSession: false);

            if (!string.IsNullOrWhiteSpace(sessionDefaultAlias))
                return ExplicitSelection(sessionDefaultAlias!, openKbs, knownKbs, "session-select", fromSession: true);

            bool isStrict = !string.Equals(_config.Environment?.ResolutionPolicy, "legacy", StringComparison.OrdinalIgnoreCase);

            if (isStrict)
            {
                // Strict mode (Issue #146):
                // 1. DefaultKb / ActiveKb in config do NOT auto-seed sessions.
                // 2. Exactly 1 KB open without conflicting configured default -> resolve as single-open.
                // 3. Exactly 1 KB open (B) but configured default is A (A != B) -> KB_CONTEXT_REQUIRED (DefaultConflict).
                // 4. >1 KBs open without selection -> KB_AMBIGUOUS.
                // 5. 0 KBs open -> never auto-open declared KB; KB_CONTEXT_REQUIRED (<=1 declared) or KB_AMBIGUOUS (>1 declared).
                // Issue #692: an idle-timeout exit drops the live Worker but not the KB the
                // user opened (only an explicit close forgets it). With no live Worker, the
                // durably opened KBs stand in for the open set, so the next call respawns
                // the Worker instead of failing with "No Knowledge Base is open".
                if (openKbs.Count == 0 && knownKbs != null && knownKbs.Count > 0)
                    openKbs = knownKbs;

                if (openKbs.Count == 1)
                {
                    var sole = openKbs.First();
                    string? configuredDefault = _config.Environment?.RawDefaultKb ?? _config.Environment?.DefaultKb;
                    if (!string.IsNullOrWhiteSpace(configuredDefault) && !string.Equals(configuredDefault, sole.Alias, StringComparison.OrdinalIgnoreCase))
                    {
                        return KbSelectionResult.Unresolved(
                            "none",
                            "conflicting",
                            "KB_CONTEXT_REQUIRED",
                            $"DefaultConflict: single open KB '{sole.Alias}' conflicts with configured default '{configuredDefault}'. Explicit KB context is required for this session.");
                    }

                    return KbSelectionResult.Resolved(sole, "single-open");
                }

                if (openKbs.Count > 1)
                {
                    return KbSelectionResult.Unresolved(
                        "none",
                        "absent",
                        "KB_AMBIGUOUS",
                        $"Multiple KBs open ({string.Join(",", openKbs.Select(k => k.Alias))}); 'kb' parameter is required.");
                }

                // openKbs.Count == 0
                var declared = _config.Environment?.KBs ?? new List<KbEntry>();
                if (declared.Count <= 1)
                {
                    return KbSelectionResult.Unresolved(
                        "none",
                        "absent",
                        "KB_CONTEXT_REQUIRED",
                        "No Knowledge Base is open. Open a KB with 'genexus_kb action=open' or pass 'kb'.");
                }

                var aliases = string.Join(", ", declared.Select(k => k.Alias));
                return KbSelectionResult.Unresolved(
                    "none",
                    "absent",
                    "KB_AMBIGUOUS",
                    $"Multiple Knowledge Bases are declared ({aliases}); no KB is currently open. Pass 'kb' or open a KB with 'genexus_kb action=open'.");
            }

            // Legacy mode (ResolutionPolicy == "legacy"):
            string? legacyDefault = _config.Environment?.DefaultKb;
            if (string.IsNullOrWhiteSpace(legacyDefault))
                legacyDefault = _config.Environment?.ActiveKb;

            if (!string.IsNullOrWhiteSpace(legacyDefault))
            {
                var openDefault = openKbs.FirstOrDefault(
                    k => string.Equals(k.Alias, legacyDefault, StringComparison.OrdinalIgnoreCase));
                if (openDefault != null) return KbSelectionResult.Resolved(openDefault, "config-default");

                if (openKbs.Count == 1) return KbSelectionResult.Resolved(openKbs.First(), "single-open");

                var declaredDefault = _config.Environment?.KBs?.FirstOrDefault(
                    k => string.Equals(k.Alias, legacyDefault, StringComparison.OrdinalIgnoreCase));
                if (declaredDefault != null)
                    return KbSelectionResult.Resolved(KbHandle.FromEntry(declaredDefault), "config-default");

                var knownDefault = knownKbs?.FirstOrDefault(
                    k => string.Equals(k.Alias, legacyDefault, StringComparison.OrdinalIgnoreCase));
                if (knownDefault != null) return KbSelectionResult.Resolved(knownDefault, "config-default");

                // Nothing here substitutes single-open or declared-first for a default
                // that cannot resolve: the next call would raise KB_NOT_FOUND, so
                // selecting something else now would make whoami's answer a lie.
                return KbSelectionResult.Unresolved(
                    "none",
                    "absent",
                    "KB_NOT_FOUND",
                    $"Configured default KB '{legacyDefault}' is not declared, open, or known in this session.");
            }

            if (openKbs.Count == 1) return KbSelectionResult.Resolved(openKbs.First(), "single-open");

            if (openKbs.Count == 0)
            {
                var first = _config.Environment?.KBs?.FirstOrDefault();
                if (first != null) return KbSelectionResult.Resolved(KbHandle.FromEntry(first), "declared-first");

                return KbSelectionResult.Unresolved(
                    "none",
                    "absent",
                    "KB_AMBIGUOUS",
                    "No 'kb' parameter, no DefaultKb configured, and no KB currently open.");
            }

            return KbSelectionResult.Unresolved(
                "none",
                "absent",
                "KB_AMBIGUOUS",
                $"Multiple KBs open ({string.Join(",", openKbs.Select(k => k.Alias))}); 'kb' parameter is required.");
        }

        private static bool SamePhysicalPath(string? candidate, string kbPath)
        {
            if (string.IsNullOrWhiteSpace(candidate)) return false;
            try
            {
                return string.Equals(
                    Path.GetFullPath(candidate).TrimEnd('\\', '/'),
                    Path.GetFullPath(kbPath).TrimEnd('\\', '/'),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                return false;
            }
        }

        // An explicit alias is matched declared → open → known (issue #26 P3: the known
        // set survives worker recycles) → an existing absolute path. A miss is a state,
        // not an exception, so the caller can report it; the code differs by caller
        // because a bad session selection and a bad per-call `kb` are different faults.
        private KbSelectionResult ExplicitSelection(
            string kbArg,
            IReadOnlyCollection<KbHandle> openKbs,
            IReadOnlyCollection<KbHandle>? knownKbs,
            string selectionSource,
            bool fromSession)
        {
            var declared = _config.Environment?.KBs?.FirstOrDefault(
                k => string.Equals(k.Alias, kbArg, StringComparison.OrdinalIgnoreCase));
            if (declared != null) return KbSelectionResult.Resolved(KbHandle.FromEntry(declared), selectionSource);

            var openMatch = openKbs.FirstOrDefault(
                k => string.Equals(k.Alias, kbArg, StringComparison.OrdinalIgnoreCase));
            if (openMatch != null) return KbSelectionResult.Resolved(openMatch, selectionSource);

            if (knownKbs != null)
            {
                var knownMatch = knownKbs.FirstOrDefault(
                    k => string.Equals(k.Alias, kbArg, StringComparison.OrdinalIgnoreCase));
                if (knownMatch != null) return KbSelectionResult.Resolved(knownMatch, selectionSource);
            }

            var configuredOnly = KbAccessPolicy.TryBuildOpenOnlyConfiguredKbsUnresolved(_config, kbArg, selectionSource);
            if (configuredOnly != null) return configuredOnly;

            if (Path.IsPathRooted(kbArg) && Directory.Exists(kbArg))
            {
                // A path that names a KB this Gateway already knows is that KB, under its
                // own alias. Minting an ad-hoc alias from the folder name instead split
                // one physical KB into two scopes: a recovery fence recorded under the
                // handle's alias could not be found, nor cleared, by a call that
                // addressed the same KB by its path.
                var samePhysicalKb = openKbs.FirstOrDefault(k => SamePhysicalPath(k.Path, kbArg))
                    ?? knownKbs?.FirstOrDefault(k => SamePhysicalPath(k.Path, kbArg));
                if (samePhysicalKb == null)
                {
                    var declaredByPath = _config.Environment?.KBs?.FirstOrDefault(
                        k => SamePhysicalPath(k.Path, kbArg));
                    if (declaredByPath != null) samePhysicalKb = KbHandle.FromEntry(declaredByPath);
                }
                if (samePhysicalKb != null) return KbSelectionResult.Resolved(samePhysicalKb, selectionSource);

                string alias = Path.GetFileName(kbArg.TrimEnd('\\', '/')).ToLowerInvariant();
                if (string.IsNullOrEmpty(alias)) alias = "adhoc";
                return KbSelectionResult.Resolved(new KbHandle(alias, kbArg), selectionSource);
            }

            return fromSession
                ? KbSelectionResult.Unresolved(
                    selectionSource,
                    "invalid",
                    "KB_SELECTION_INVALID",
                    $"Session-selected KB '{kbArg}' is not declared, open, or known. Declare an alias in config.Environment.KBs[] or pass an absolute path to an existing directory.")
                : KbSelectionResult.Unresolved(
                    selectionSource,
                    "invalid",
                    "KB_NOT_FOUND",
                    $"Unknown KB '{kbArg}' is not declared, open, or known. Declare an alias in config.Environment.KBs[] or pass an absolute path to an existing directory.");
        }
    }
}
