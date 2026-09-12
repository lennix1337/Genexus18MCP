using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Artech.Architecture.Common.Objects;
using Artech.Genexus.Common.Parts;
using GxMcp.Worker.Models;
using GxMcp.Worker.Helpers;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    public class SourceSearchCriteria
    {
        public string Callee { get; set; }
        public Dictionary<int, string> ArgMatches { get; set; }
        public string Pattern { get; set; }
        public bool CaseSensitive { get; set; }
        public string TypeFilter { get; set; }
        public List<string> Scope { get; set; } = new List<string> { "source" };
        /// <summary>
        /// Item 22: wider field search. Values: source (default), caption,
        /// description, parmNames. When any non-source value is present the
        /// search scans that metadata field instead of / in addition to source.
        /// </summary>
        public List<string> Fields { get; set; } = null; // null = default [source]
        public int MaxResults { get; set; } = 50;
        public bool IncludeComments { get; set; }
        // v2.3.8 (Task 2.1): hard wall-clock cap. Distinct from the legacy
        // internal 25s budget — when exceeded we return a structured Timeout
        // envelope with partial hits, never a silently empty result.
        public int TimeoutMs { get; set; } = 30000;

        // Issue #27 item 4: scope the scan to specific object(s) by exact name
        // (comma/semicolon-separated, case-insensitive). When set, only those
        // objects are read — a search inside one known Procedure becomes
        // O(object) instead of O(KB), and bypasses the base type whitelist so any
        // searchable object type can be targeted directly.
        public string ObjectName { get; set; }
        public string ObjectGuid { get; set; }
        public string ObjectEntityKey { get; set; }
        public string ObjectPath { get; set; }

        // Issue #27 item 4: resume cursor. On Timeout the response returns a
        // nextCursor; passing it back as StartIndex resumes the scan where it
        // stopped instead of rescanning from the top.
        public int StartIndex { get; set; } = 0;

        // Preferred opaque continuation token. StartIndex remains accepted for
        // compatibility with callers that only need object-boundary paging.
        public string Cursor { get; set; }
    }

    public class SourceSearchService
    {
        // Metadata fields are evaluated together for one candidate. Keep the SDK
        // object and part values local to this request so a multi-field search does
        // not resolve or read the same candidate repeatedly, while avoiding any
        // cross-request SDK-object lifetime or invalidation concerns.
        internal sealed class MetadataCandidateCache<TObject> where TObject : class
        {
            private readonly Func<TObject> _resolve;
            private readonly Func<TObject, string, string> _readPart;
            private readonly Dictionary<string, string> _parts =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            private TObject _object;
            private bool _resolutionAttempted;

            internal MetadataCandidateCache(Func<TObject> resolve, Func<TObject, string, string> readPart)
            {
                _resolve = resolve;
                _readPart = readPart;
            }

            internal TObject Object
            {
                get
                {
                    if (!_resolutionAttempted)
                    {
                        _resolutionAttempted = true;
                        _object = _resolve();
                    }
                    return _object;
                }
            }

            internal string ReadPart(string part)
            {
                string normalizedPart = string.IsNullOrWhiteSpace(part) ? "source" : part.Trim();
                if (_parts.TryGetValue(normalizedPart, out string cached)) return cached;
                string value = Object == null ? string.Empty : (_readPart(Object, normalizedPart) ?? string.Empty);
                _parts[normalizedPart] = value;
                return value;
            }

            internal int ResolutionCount => _resolutionAttempted ? 1 : 0;
            internal int PartReadCount => _parts.Count;
        }

        private readonly IndexCacheService _index;
        private readonly ObjectService _objectService;

        // PERFORMANCE (perf round 4): compiled-regex cache. On net48,
        // RegexOptions.Compiled emits + JITs IL on EVERY new Regex(pattern,
        // Compiled) call — a flat ~15ms floor per search_source that no source
        // cache can remove (the benchmark showed 8/12 samples within 0.4ms of
        // 15.4ms regardless of hit rate). Bounded to the handful of patterns an
        // LLM session actually repeats; overflow simply stops caching new ones.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Regex> _compiledRegexCache =
            new System.Collections.Concurrent.ConcurrentDictionary<string, Regex>(StringComparer.Ordinal);
        private const int CompiledRegexCacheMaxEntries = 16;

        // AVAILABILITY: LLM-supplied patterns must not hang the single STA thread.
        // .NET Framework defaults to Regex.InfiniteMatchTimeout, so a catastrophic
        // back-tracking pattern (e.g. "(a+)+$") blocks every later KB call until the
        // 15-min wedged kill. Bound each match call; the caller maps the resulting
        // RegexMatchTimeoutException to a structured PatternTimeout envelope.
        internal static readonly TimeSpan RegexMatchTimeout = TimeSpan.FromSeconds(2);
        private static readonly List<string> DefaultScope = new List<string> { "source" };

        private static Regex GetCachedRegex(string pattern, RegexOptions opts)
        {
            string key = pattern + "\u0001" + ((int)opts).ToString();
            if (_compiledRegexCache.TryGetValue(key, out var cached)) return cached;
            var fresh = new Regex(pattern, opts, RegexMatchTimeout);
            if (_compiledRegexCache.Count < CompiledRegexCacheMaxEntries)
            {
                _compiledRegexCache.TryAdd(key, fresh);
            }
            return fresh;
        }

        // Keep the fast MatchCollection path semantically equivalent to the legacy
        // per-line path: search_source returns one hit per matching line, not one hit
        // per regex match on that line.
        internal static bool ShouldEmitRegexHitLine(int lineNo, ref int lastHitLine)
        {
            if (lineNo == lastHitLine) return false;
            lastHitLine = lineNo;
            return true;
        }

        private static bool AddSourceHit(JArray hits, JObject hit, ref int produced,
            ref int skipped, ref int consumed, int maxResults)
        {
            consumed++;
            if (skipped > 0)
            {
                skipped--;
                return false;
            }

            hits.Add(hit);
            produced++;
            return produced >= maxResults;
        }

        internal static string BuildResumeCursor(int entryIndex, int skippedHits, bool metadata)
        {
            var state = new JObject
            {
                ["v"] = 1,
                ["entry"] = Math.Max(0, entryIndex),
                ["skip"] = Math.Max(0, skippedHits),
                ["phase"] = metadata ? "metadata" : "source"
            };
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(state.ToString(Newtonsoft.Json.Formatting.None)))
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }

        internal static bool TryParseResumeCursor(string cursor, out int entryIndex,
            out int skippedHits, out bool metadata)
        {
            entryIndex = 0;
            skippedHits = 0;
            metadata = false;
            if (string.IsNullOrWhiteSpace(cursor)) return false;

            try
            {
                string padded = cursor.Trim().Replace('-', '+').Replace('_', '/');
                switch (padded.Length % 4)
                {
                    case 2: padded += "=="; break;
                    case 3: padded += "="; break;
                    case 1: return false;
                }

                var state = JObject.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(padded)));
                if (state["v"]?.Value<int>() != 1) return false;
                string phase = state["phase"]?.ToString() ?? string.Empty;
                if (!string.Equals(phase, "source", StringComparison.Ordinal)
                    && !string.Equals(phase, "metadata", StringComparison.Ordinal))
                {
                    return false;
                }
                entryIndex = state["entry"]?.Value<int>() ?? 0;
                skippedHits = state["skip"]?.Value<int>() ?? 0;
                metadata = string.Equals(phase, "metadata", StringComparison.Ordinal);
                return entryIndex >= 0 && skippedHits >= 0;
            }
            catch
            {
                return false;
            }
        }

        public SourceSearchService(IndexCacheService index, ObjectService objectService)
        {
            _index = index;
            _objectService = objectService;
        }

        public string SearchAsJson(SourceSearchCriteria c)
        {
            return SearchAsJson(c, System.Threading.CancellationToken.None);
        }

        // v2.3.8 (post-Task 7.2 fix): worker-side cancellation. The gateway's
        // BackgroundJobRegistry.RegisterCancellation gives us a token that the
        // assistant trips via lifecycle action=cancel + job_id. Plumbing it
        // here means a slow regex over 24k entries actually stops mid-loop
        // instead of running to completion while the gateway poller exits.
        public string SearchAsJson(SourceSearchCriteria c, System.Threading.CancellationToken ct)
        {
            // v2.3.8 (Task 2.1): surface index readiness as a structured envelope
            // BEFORE touching the body of SearchCore — keeping the envelope check
            // in a separate method that doesn't reference KBObject types means
            // unit tests with a Cold index never trigger JIT-time resolution of
            // Artech.Architecture.Common, so they can run without the GeneXus
            // install on the probing path.
            var state = _index != null ? _index.GetState() : null;
            string status = state?.Status ?? "Ready";
            // issue #25 #1 / #3: only a Cold/Reindexing index is genuinely
            // unsearchable (nothing stable in memory yet). The lite-walk states
            // (LiteReady/UltraLiteReady/Enriching) hold a growing-but-stable set
            // of entries whose full source we can read on demand — search them
            // and flag the result partial rather than hard-blocking. That keeps
            // search usable while the walk runs, and a zero result on a partial
            // index is reported with a DISTINCT code (never a plain empty-success)
            // so the caller can't read it as "token does not exist".
            bool cold = string.Equals(status, "Cold", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(status, "Reindexing", StringComparison.OrdinalIgnoreCase);
            if (cold)
            {
                string indexCode = string.Equals(status, "Reindexing", StringComparison.OrdinalIgnoreCase)
                    ? "Reindexing" : "IndexCold";
                var indexResult = new JObject { ["retryAfterMs"] = state?.EtaMs ?? 5000 };
                if (state?.Progress != null) indexResult["progress"] = state.Progress.Value;
                return Models.McpResponse.Ok(code: indexCode, result: indexResult);
            }
            bool partial = !string.Equals(status, "Ready", StringComparison.OrdinalIgnoreCase);
            return SearchCore(c, ct, partial, status);
        }

        private string SearchCore(SourceSearchCriteria c, System.Threading.CancellationToken ct = default(System.Threading.CancellationToken),
            bool partialIndex = false, string indexStatus = "Ready")
        {
            try
            {
                var searchSw = System.Diagnostics.Stopwatch.StartNew();
                if (string.IsNullOrEmpty(c.Callee) && string.IsNullOrEmpty(c.Pattern))
                    return Models.McpResponse.Err(code: "MissingCriteria", message: "Provide 'callee' (semantic) or 'pattern' (regex).");

                Regex rx = null;
                if (!string.IsNullOrEmpty(c.Pattern))
                {
                    var opts = RegexOptions.Compiled;
                    if (!c.CaseSensitive) opts |= RegexOptions.IgnoreCase;
                    rx = GetCachedRegex(c.Pattern, opts);
                }

                var hits = new JArray();
                var unresolvedObjects = new JArray();
                var index = _index.GetIndex();

                // Pre-filter by literal tokens against the index so we skip FindObject for
                // entries that demonstrably reference none of them.
                var literals = ExtractLiteralTokens(c.Pattern, c.Callee);

                // The literal pre-filter only sees indexed text (SourceSnippet/Name/Keywords),
                // which never contains the WebForm XML. A WebForm scope scan would therefore
                // be pre-filtered away before its part is ever read, so skip the pre-filter
                // when the caller asked for the webForm/layout part.
                bool scopeTouchesWebForm = (c.Scope ?? DefaultScope)
                    .Any(s => string.Equals(s, "webForm", StringComparison.OrdinalIgnoreCase)
                           || string.Equals(s, "layout", StringComparison.OrdinalIgnoreCase));
                bool indexedSourceScope = !scopeTouchesWebForm && IsIndexedSourceScope(c.Scope);
                if (indexedSourceScope && literals.Count > 0)
                {
                    // Test fixtures may load only Objects; production disk indexes
                    // build this derived posting map during hydration. Asking for the
                    // map here keeps both paths correct without changing the fixture's
                    // other secondary-index semantics.
                    var tokenIndexSw = System.Diagnostics.Stopwatch.StartNew();
                    try { _index.EnsureSourceTokenIndex(); }
                    finally
                    {
                        tokenIndexSw.Stop();
                        Logger.Info($"[SEARCH-SOURCE-PHASE] tokenIndexMs={tokenIndexSw.ElapsedMilliseconds} indexed={_index.GetIndex()?.SourceTokenIndex != null}");
                    }
                }

                // Issue #27 item 4: an explicit objectName scope restricts the scan to
                // those exact objects (bypassing both the base type whitelist and the
                // literal pre-filter), so a search inside one known object is O(object).
                var objectNameSet = ParseObjectNames(c.ObjectName);
                bool hasIdentityScope = !string.IsNullOrWhiteSpace(c.ObjectGuid)
                    || !string.IsNullOrWhiteSpace(c.ObjectEntityKey)
                    || !string.IsNullOrWhiteSpace(c.ObjectPath);

                IEnumerable<Models.SearchIndex.IndexEntry> query = index.Objects.Values;
                if (hasIdentityScope)
                {
                    string targetGuid = c.ObjectGuid?.Trim();
                    string targetKey = c.ObjectEntityKey?.Trim();
                    string normalizedPath = c.ObjectPath?.Trim().Replace('\\', '/');

                    if (!string.IsNullOrWhiteSpace(targetGuid) && index.GuidToKey != null && index.GuidToKey.TryGetValue(targetGuid, out var sKey) && index.Objects.TryGetValue(sKey, out var matchedEntry))
                    {
                        query = (string.IsNullOrWhiteSpace(targetKey) || string.Equals(matchedEntry.EntityKey, targetKey, StringComparison.OrdinalIgnoreCase))
                            && (string.IsNullOrWhiteSpace(normalizedPath) || ObjectPathMatches(matchedEntry, normalizedPath))
                            ? new[] { matchedEntry }
                            : Enumerable.Empty<Models.SearchIndex.IndexEntry>();
                    }
                    else
                    {
                        query = query.Where(e =>
                            (string.IsNullOrWhiteSpace(targetGuid) || string.Equals(e.Guid, targetGuid, StringComparison.OrdinalIgnoreCase))
                            && (string.IsNullOrWhiteSpace(targetKey) || string.Equals(e.EntityKey, targetKey, StringComparison.OrdinalIgnoreCase))
                            && (string.IsNullOrWhiteSpace(normalizedPath) || ObjectPathMatches(e, normalizedPath)));
                    }
                }
                else if (objectNameSet != null)
                {
                    // issue #36.7 — tolerate module-qualified vs bare names in EITHER direction
                    // so passing "Foo" finds "MyModule.Foo" and vice-versa (exact match was too
                    // strict and quietly yielded an empty set that looked like a full-KB scan).
                    query = query.Where(e => ObjectNameMatches(objectNameSet, e.Name));
                }
                else
                {
                    query = query
                        .Where(e => e.Type == "Procedure" || e.Type == "DataProvider" || e.Type == "WebPanel" || e.Type == "Transaction")
                        .Where(e => scopeTouchesWebForm || indexedSourceScope || MatchesAnyLiteral(e, literals));
                    if (indexedSourceScope && literals.Count > 0 && index.SourceTokenIndex != null)
                    {
                        var indexedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (string literal in literals)
                        {
                            if (!index.SourceTokenIndex.TryGetValue(literal, out var keys) || keys == null) continue;
                            lock (keys)
                            {
                                foreach (string key in keys) indexedKeys.Add(key);
                            }
                        }
                        // Entries without FullSource predate this index (or could not
                        // be read) and must stay in the conservative SDK fallback.
                        query = query.Where(e => e.FullSource == null || indexedKeys.Contains(GetEntryStorageKey(e)));
                    }
                }
                var entries = query
                    .Where(e => string.IsNullOrEmpty(c.TypeFilter) || string.Equals(e.Type, c.TypeFilter, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                // When complete source postings exist, visit them before the
                // conservative SDK fallback candidates. This preserves completeness
                // (the fallback tail is still scanned when the page is not full) while
                // avoiding needless STA/COM reads whenever indexed sources can fill it.
                if (indexedSourceScope && literals.Count > 0 && index.SourceTokenIndex != null
                    && objectNameSet == null && !hasIdentityScope)
                {
                    entries = PrioritizeCompleteSourceCandidates(entries, c.MaxResults);
                }
                Logger.Info($"[SEARCH-SOURCE-PHASE] candidateFilterMs={searchSw.ElapsedMilliseconds} candidates={entries.Count} literals={literals.Count} partial={partialIndex}");

                // issue #36.7 — an objectName scope that resolved to zero entries must say so
                // explicitly, not silently return "no hits" (indistinguishable from "found
                // nothing in the object", and the symptom reporters read as "filter ignored").
                if (objectNameSet != null && entries.Count == 0)
                {
                    return Models.McpResponse.Ok(code: "ObjectNameNoMatch", result: new JObject
                    {
                        ["hits"] = new JArray(),
                        ["totalObjects"] = 0,
                        ["requestedObjectNames"] = new JArray(objectNameSet.ToArray()),
                        ["note"] = "objectName matched no objects in the index. Names match module-qualified OR bare; if you expected a hit, confirm the object exists and the index is Ready (genexus_lifecycle action=status)."
                    });
                }

                // v2.3.8 (Task 2.1): hard wall-clock timeout — emits a Timeout
                // envelope with partial hits, replacing the legacy budgetExceeded
                // flag. The 25s internal cap is now driven by c.TimeoutMs (default
                // 30s) so callers can tune the budget per-call.
                int timeoutMs = c.TimeoutMs > 0 ? c.TimeoutMs : 0;
                var swBudget = System.Diagnostics.Stopwatch.StartNew();

                int produced = 0;
                int scanned = 0;
                int sourceCacheHits = 0;
                int sourceCacheMisses = 0;
                int sourceIndexPromotions = 0;
                int sdkResolutions = 0;
                long sdkResolutionTicks = 0;
                long sourceReadTicks = 0;
                // Issue #27 item 4: index-addressable loop so a Timeout/Cancel can report a
                // resumable nextCursor (the absolute entry index reached).
                int resumeEntry = -1;
                int resumeSkipped = 0;
                bool resumeMetadata = false;
                if (!string.IsNullOrWhiteSpace(c.Cursor)
                    && !TryParseResumeCursor(c.Cursor, out resumeEntry, out resumeSkipped, out resumeMetadata))
                {
                    return Models.McpResponse.Err(code: "InvalidCursor",
                        message: "cursor is not a valid genexus_search_source continuation token.");
                }
                int startIndex = c.StartIndex > 0 ? c.StartIndex : 0;
                if (resumeEntry >= 0) startIndex = resumeEntry;
                int cursor = resumeMetadata ? entries.Count : startIndex;
                bool sourcePageStoppedInsideEntry = false;
                int sourceNextEntry = -1;
                int sourceNextSkip = 0;
                for (cursor = startIndex; cursor < entries.Count; cursor++)
                {
                    if (resumeMetadata) break;
                    var e = entries[cursor];
                    if (produced >= c.MaxResults) break;
                    int skippedHits = resumeEntry == cursor ? resumeSkipped : 0;
                    int consumedHits = skippedHits;
                    bool entryReachedLimit = false;
                    if (ct.IsCancellationRequested)
                    {
                        return Models.McpResponse.Ok(code: "Cancelled", result: new JObject
                        {
                            ["partialHits"] = hits,
                            ["totalScanned"] = scanned,
                            ["totalObjects"] = entries.Count,
                            ["nextCursor"] = BuildResumeCursor(cursor, 0, metadata: false),
                            ["nextOffset"] = cursor,
                            ["resumeHint"] = "Pass cursor=nextCursor to resume this scan; legacy callers may pass startIndex=nextOffset."
                        });
                    }
                    if (timeoutMs == 0 || swBudget.ElapsedMilliseconds > timeoutMs)
                    {
                        int pct = entries.Count > 0 ? (int)(100L * cursor / entries.Count) : 100;
                        return Models.McpResponse.Ok(code: "Timeout", result: new JObject
                        {
                            ["partialHits"] = hits,
                            ["totalScanned"] = scanned,
                            ["totalObjects"] = entries.Count,
                            ["coveragePercent"] = pct,
                            ["timeoutMs"] = timeoutMs,
                            ["nextCursor"] = BuildResumeCursor(cursor, 0, metadata: false),
                            ["nextOffset"] = cursor,
                            // Full-source scan reads each object's source via the SDK (~tens of ms
                            // each), so a whole-KB scan spans many budget windows. Prefer scoping
                            // for an instant search; resume only for an exhaustive sweep.
                            ["resumeHint"] = $"Scanned {pct}% ({cursor}/{entries.Count}). FASTEST: narrow the scan — objectName=\"A,B\" (searches only those, O(objects)), or typeFilter / pathPrefix. To keep sweeping the whole KB, resume with cursor=nextCursor (legacy: startIndex={cursor}); optionally use a larger timeoutMs."
                        });
                    }
                    scanned++;
                    // PERFORMANCE (perf round 2): cache-first, per-part. The index entry
                    // carries the object's guid, so before paying the FindObject SDK call we
                    // probe the raw part-source cache by guid alone (TryGetPartSourceRaw). On a
                    // repeat search most candidates' source text is already cached (round 1), so
                    // this loop becomes dictionary lookups + regex over cached text with ZERO
                    // SDK round-trips. FindObject runs lazily only on the first cache miss and
                    // is reused for subsequent parts of the same candidate; the type is passed
                    // so it takes the O(1) typed fast path (Type:Name index hit →
                    // Objects.Get(guid)) instead of the global type-iterating search. The null
                    // service seam (unit tests) falls straight through to the local reader.
                    KBObject obj = null;
                    bool resolutionFailed = false;
                    foreach (var part in c.Scope ?? DefaultScope)
                    {
                        if (produced >= c.MaxResults || resolutionFailed) break;
                        string src = null;
                        bool haveSrc = false;
                        bool useIndexedSource = indexedSourceScope
                            && IsSourceAlias(part)
                            && e.FullSource != null;
                        if (useIndexedSource)
                        {
                            src = e.FullSource;
                            haveSrc = true;
                        }
                        else if (_objectService != null && !string.IsNullOrEmpty(e.Guid)
                            && _objectService.TryGetPartSourceRaw(e.Guid, part, out src))
                        {
                            haveSrc = true;
                            sourceCacheHits++;
                        }
                        if (!haveSrc)
                        {
                            sourceCacheMisses++;
                            if (obj == null)
                            {
                                // Resolve the object ONCE per candidate; on failure bail the whole
                                // candidate (review nit: a per-part `continue` here would re-attempt
                                // FindObject for every remaining part of the same candidate).
                                var resolveStart = System.Diagnostics.Stopwatch.GetTimestamp();
                                try { obj = _objectService.FindObject(e); }
                                catch { obj = null; }
                                sdkResolutions++;
                                sdkResolutionTicks += System.Diagnostics.Stopwatch.GetTimestamp() - resolveStart;
                                if (obj == null)
                                {
                                    var diagnostic = _objectService?.GetLastResolutionDiagnostic();
                                    if (diagnostic != null) unresolvedObjects.Add(diagnostic);
                                    resolutionFailed = true;
                                    break;
                                }
                            }
                            var sourceReadStart = System.Diagnostics.Stopwatch.GetTimestamp();
                            src = _objectService != null
                                ? _objectService.ReadPartSourceRaw(obj, part)
                                : TryGetPartSource(obj, part);
                            haveSrc = src != null;
                            sourceReadTicks += System.Diagnostics.Stopwatch.GetTimestamp() - sourceReadStart;
                        }
                        if (haveSrc && indexedSourceScope && IsSourceAlias(part) && e.FullSource == null)
                        {
                            if (_index.PromoteSourceForSearch(e, src)) sourceIndexPromotions++;
                        }
                        if (string.IsNullOrEmpty(src)) continue;

                        if (!string.IsNullOrEmpty(c.Callee))
                        {
                            var lines = src.Split('\n');
                            foreach (var call in SourceParser.ParseCalls(src, c.IncludeComments))
                            {
                                if (!CalleeMatches(call.Callee, c.Callee)) continue;
                                if (c.ArgMatches != null && !ArgsMatch(call.Args, c.ArgMatches)) continue;
                                if (rx != null)
                                {
                                    string ln = call.LineNumber - 1 < lines.Length ? lines[call.LineNumber - 1] : "";
                                    if (!rx.IsMatch(ln)) continue;
                                }
                                if (AddSourceHit(hits, BuildHit(e, useIndexedSource ? "Source" : part, lines, call.LineNumber, call),
                                    ref produced, ref skippedHits, ref consumedHits, c.MaxResults))
                                {
                                    entryReachedLimit = true;
                                    break;
                                }
                            }
                        }
                        else if (rx != null)
                        {
                            // PERFORMANCE (perf round 3): single-pass regex over the whole source
                            // when the pattern has no line anchors AND no newline-capable atoms —
                            // semantics identical to per-line IsMatch, but avoids the full
                            // line-array allocation + N regex calls for every scanned object
                            // (the common non-match case). Line numbers come from counting
                            // newlines up to each match index; the lines array is built lazily
                            // only on the first match (BuildHit needs it for context).
                            // Guarded fast path: HasLineAnchors keeps ^ $ \A \Z \z patterns on
                            // the legacy per-line loop (they anchor per line, not per file), and
                            // MayMatchAcrossLines keeps patterns whose atoms can match a newline
                            // (\s \S \n \r \v \f \D \W \p.., negated classes, (?s), literal
                            // newlines) on it too — those COULD match across lines, and the
                            // per-line loop is the only exact semantic for them. Both guards are
                            // conservative (a false positive only costs speed, never correctness).
                            if (HasLineAnchors(c.Pattern) || MayMatchAcrossLines(c.Pattern))
                            {
                                var lines = src.Split('\n');
                                for (int li = 0; li < lines.Length && produced < c.MaxResults; li++)
                                {
                                    if (rx.IsMatch(lines[li]))
                                    {
                                        if (AddSourceHit(hits, BuildHit(e, useIndexedSource ? "Source" : part, lines, li + 1, null),
                                            ref produced, ref skippedHits, ref consumedHits, c.MaxResults))
                                        {
                                            entryReachedLimit = true;
                                            break;
                                        }
                                    }
                                }
                            }
                            else
                            {
                                // Lazy single-pass: iterate the MatchCollection (net48 computes it
                                // lazily on enumeration), stop at MaxResults, and split the line
                                // array only when the first match actually exists.
                                var matches = rx.Matches(src);
                                string[] hitLines = null;
                                int pos = 0, lineNo = 1, lastHitLine = -1;
                                foreach (Match m in matches)
                                {
                                    if (produced >= c.MaxResults) break;
                                    if (hitLines == null) hitLines = src.Split('\n');
                                    int idx = m.Index;
                                    while (pos < idx)
                                    {
                                        if (src[pos] == '\n') lineNo++;
                                        pos++;
                                    }
                                    if (!ShouldEmitRegexHitLine(lineNo, ref lastHitLine)) continue;
                                    if (AddSourceHit(hits, BuildHit(e, useIndexedSource ? "Source" : part, hitLines, lineNo, null),
                                        ref produced, ref skippedHits, ref consumedHits, c.MaxResults))
                                    {
                                        entryReachedLimit = true;
                                        break;
                                    }
                                }
                            }
                        }
                    }
                    if (entryReachedLimit)
                    {
                        sourcePageStoppedInsideEntry = true;
                        sourceNextEntry = cursor;
                        sourceNextSkip = consumedHits;
                        break;
                    }
                }

                // Item 22: fields=[caption,description,parmNames] — metadata-only search.
                // Only runs when Fields contains non-source values AND a pattern is supplied.
                bool metadataPageStoppedInsideEntry = false;
                int metadataNextEntry = -1;
                int metadataNextSkip = 0;
                var extraFields = c.Fields != null
                    ? c.Fields.Where(f => !string.Equals(f, "source", StringComparison.OrdinalIgnoreCase)).ToList()
                    : new List<string>();
                if (extraFields.Count > 0 && rx != null)
                {
                    var allEntries = index.Objects.Values
                        .Where(e => objectNameSet == null || ObjectNameMatches(objectNameSet, e.Name))
                        .Where(e => string.IsNullOrEmpty(c.TypeFilter) || string.Equals(e.Type, c.TypeFilter, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    int metadataStart = resumeMetadata ? Math.Max(0, resumeEntry) : 0;
                    for (int metadataCursor = metadataStart; metadataCursor < allEntries.Count; metadataCursor++)
                    {
                        var e = allEntries[metadataCursor];
                        if (produced >= c.MaxResults) break;
                        if (ct.IsCancellationRequested) break;
                        if (swBudget.ElapsedMilliseconds > timeoutMs) break;
                        int metadataSkipped = resumeMetadata && metadataCursor == resumeEntry ? resumeSkipped : 0;
                        int metadataConsumed = metadataSkipped;
                        bool entryReachedLimit = false;

                        var metadataCache = new MetadataCandidateCache<KBObject>(
                            () =>
                            {
                                try { return _objectService?.FindObject(e); }
                                catch { return null; }
                            },
                            (obj, part) => _objectService != null
                                ? _objectService.ReadPartSourceRaw(obj, part)
                                : TryGetPartSource(obj, part));

                        foreach (var field in extraFields)
                        {
                            if (produced >= c.MaxResults) break;
                            string fieldValue = null;
                            if (string.Equals(field, "description", StringComparison.OrdinalIgnoreCase))
                                fieldValue = e.Description;
                            else if (string.Equals(field, "caption", StringComparison.OrdinalIgnoreCase) ||
                                     string.Equals(field, "parmNames", StringComparison.OrdinalIgnoreCase) ||
                                     string.Equals(field, "webForm", StringComparison.OrdinalIgnoreCase))
                            {
                                // Caption / parmNames / webForm require SDK access
                                KBObject obj2 = metadataCache.Object;
                                if (obj2 == null) continue;
                                if (string.Equals(field, "caption", StringComparison.OrdinalIgnoreCase))
                                {
                                    try
                                    {
                                        dynamic dyn = obj2;
                                        fieldValue = dyn?.Form?.Caption?.ToString() ?? dyn?.Caption?.ToString() ?? "";
                                    }
                                    catch { fieldValue = ""; }
                                }
                                else if (string.Equals(field, "webForm", StringComparison.OrdinalIgnoreCase))
                                {
                                    // webForm — scan the WebForm XML (WebPanel / Transaction layouts).
                                    // Opt-in via fields=[webForm] because the XML can be large; we
                                    // never load it on the default code-search path. Reuses the
                                    // same read path as WriteService / PatchService via
                                    // WebFormXmlHelper.ReadEditableXml.
                                    try { fieldValue = metadataCache.ReadPart("webForm"); }
                                    catch { fieldValue = ""; }
                                }
                                else // parmNames — scan Rules part for 'parm(' signature
                                {
                                    try
                                    {
                                        // PERFORMANCE (perf round 1): same cached accessor as the
                                        // main scan so the parmNames path doesn't re-read the SDK.
                                        string rulesSrc = metadataCache.ReadPart("rules");
                                        if (!string.IsNullOrEmpty(rulesSrc))
                                        {
                                            var parmMatch = System.Text.RegularExpressions.Regex.Match(
                                                rulesSrc, @"parm\s*\(([^)]+)\)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                                            fieldValue = parmMatch.Success ? parmMatch.Groups[1].Value : "";
                                        }
                                    }
                                    catch { fieldValue = ""; }
                                }
                            }
                            if (string.IsNullOrEmpty(fieldValue)) continue;
                            if (rx.IsMatch(fieldValue))
                            {
                                var metadataHit = new JObject
                                {
                                    ["objectName"] = e.Name,
                                    ["type"] = e.Type,
                                    ["guid"] = e.Guid,
                                    ["entityKey"] = e.EntityKey,
                                    ["path"] = e.Path,
                                    ["field"] = field,
                                    ["matchedValue"] = fieldValue
                                };
                                if (AddSourceHit(hits, metadataHit, ref produced, ref metadataSkipped,
                                    ref metadataConsumed, c.MaxResults))
                                {
                                    entryReachedLimit = true;
                                    break;
                                }
                            }
                        }
                        if (entryReachedLimit)
                        {
                            metadataPageStoppedInsideEntry = true;
                            metadataNextEntry = metadataCursor;
                            metadataNextSkip = metadataConsumed;
                            break;
                        }
                    }
                }

                bool truncated = produced >= c.MaxResults;
                // Issue #27 item 4: when maxResults truncated the scan mid-object, expose an
                // opaque cursor carrying the number of already-consumed hits. Replaying the
                // object and skipping that exact prefix prevents both duplicate and lost hits.
                bool hasMoreEntries = sourcePageStoppedInsideEntry
                    || metadataPageStoppedInsideEntry
                    || (truncated && cursor < entries.Count);
                int nextOffset = sourcePageStoppedInsideEntry ? sourceNextEntry
                    : metadataPageStoppedInsideEntry ? metadataNextEntry
                    : cursor;
                string nextCursor = sourcePageStoppedInsideEntry
                    ? BuildResumeCursor(sourceNextEntry, sourceNextSkip, metadata: false)
                    : metadataPageStoppedInsideEntry
                        ? BuildResumeCursor(metadataNextEntry, metadataNextSkip, metadata: true)
                        : null;
                var resultPayload = new JObject
                {
                    ["count"] = produced,
                    ["truncated"] = truncated,
                    ["hits"] = hits,
                    // issue #25 #1/#3: make index coverage explicit so a zero count is
                    // never mistaken for "does not exist" when the walk is still running.
                    ["indexStatus"] = indexStatus,
                    ["partial"] = partialIndex,
                    ["scannedObjects"] = scanned,
                    ["totalObjects"] = entries.Count,
                    // v2.8.0: canonical pagination block — total is now the scoped object count.
                    ["pagination"] = new JObject
                    {
                        ["offset"]     = startIndex,
                        ["limit"]      = c.MaxResults,
                        ["returned"]   = produced,
                        ["total"]      = entries.Count,
                        ["hasMore"]    = hasMoreEntries,
                        ["nextOffset"] = hasMoreEntries ? (JToken)nextOffset : JValue.CreateNull()
                    }
                };
                if (hasMoreEntries)
                {
                    resultPayload["nextCursor"] = nextCursor != null
                        ? (JToken)nextCursor
                        : (JToken)nextOffset;
                }
                if (partialIndex)
                {
                    resultPayload["partialHint"] = "Index walk is still in progress; this scan covered only the objects walked so far. A zero or small count does NOT mean the token is absent — re-run when whoami reports indexStatus=Ready.";
                }
                if (unresolvedObjects.Count > 0)
                {
                    resultPayload["unresolvedObjects"] = unresolvedObjects;
                    resultPayload["unresolvedHint"] = "The index listed these objects, but the active SDK could not resolve their native identity; no source was inferred for them.";
                }
                Logger.Info($"[SEARCH-SOURCE-PHASE] sdkResolveMs={(long)(sdkResolutionTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency)} sourceReadMs={(long)(sourceReadTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency)} resolutions={sdkResolutions} cacheHits={sourceCacheHits} cacheMisses={sourceCacheMisses} promotions={sourceIndexPromotions} scanned={scanned} totalMs={searchSw.ElapsedMilliseconds}");
                if (hits.Count > 0 && hits[0] is JObject topHit)
                {
                    resultPayload["_meta"] = new JObject
                    {
                        ["suggested_next"] = new JObject
                        {
                            ["tool"] = "genexus_read",
                            ["args"] = new JObject
                            {
                                ["name"] = topHit["objectName"]?.ToString(),
                                ["type"] = topHit["type"]?.ToString()
                            }
                        }
                    };
                }
                // A zero-hit result on a partial index gets its own code so callers
                // cannot read it as an authoritative "not found" empty-success.
                string completionCode = (partialIndex && produced == 0)
                    ? "PartialIndexNoMatch" : "SourceSearchCompleted";
                return Models.McpResponse.Ok(code: completionCode, result: resultPayload);
            }
            catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
            {
                return Models.McpResponse.Err(
                    code: "PatternTimeout",
                    message: "The regex pattern exceeded the " + RegexMatchTimeout.TotalSeconds
                        + "s match-timeout on one input and was aborted.",
                    hint: "Simplify the pattern — avoid nested quantifiers like (a+)+ or (\\w+\\s?)+ "
                        + "that can backtrack exponentially. Prefer literal tokens or atomic groups.");
            }
            catch (Exception ex)
            {
                return Models.McpResponse.Err(code: "SourceSearchFailed", message: ex.Message);
            }
        }

        // PERFORMANCE (perf round 3): true when the pattern contains a line anchor
        // (^ $ \A \Z \z) outside a character class or escape sequence, in which case
        // the legacy per-line loop is kept (^/$ must anchor per line, not per file).
        // A false positive here only falls back to the slower per-line path — never
        // a wrong result — so a simple conservative scan is sufficient.
        private static bool HasLineAnchors(string pattern)
        {
            if (string.IsNullOrEmpty(pattern)) return false;
            bool inClass = false;
            for (int i = 0; i < pattern.Length; i++)
            {
                char ch = pattern[i];
                if (ch == '\\')
                {
                    // Escaped: \A \Z \z are anchors; \^ \$ \\ are literals.
                    if (i + 1 < pattern.Length)
                    {
                        char n = pattern[i + 1];
                        if (n == 'A' || n == 'Z' || n == 'z') return true;
                    }
                    i++;
                    continue;
                }
                if (ch == '[') inClass = true;
                else if (ch == ']') inClass = false;
                else if (!inClass && (ch == '^' || ch == '$')) return true;
            }
            return false;
        }

        // PERFORMANCE (perf round 3): conservative detector for regex atoms that can
        // match a newline. If ANY is present the pattern could match across line
        // boundaries, where the single-pass fast path would produce different results
        // than the legacy per-line loop — so such patterns fall back to per-line.
        // False positives only cost speed (per-line path), never correctness.
        private static bool MayMatchAcrossLines(string pattern)
        {
            if (string.IsNullOrEmpty(pattern)) return false;
            // Literal newline/CR characters embedded in the pattern can match across lines.
            for (int i = 0; i < pattern.Length; i++)
            {
                if (pattern[i] == '\n' || pattern[i] == '\r') return true;
            }
            bool inClass = false;
            for (int i = 0; i < pattern.Length; i++)
            {
                char ch = pattern[i];
                if (ch == '\\')
                {
                    if (i + 1 >= pattern.Length) break;
                    char n = pattern[i + 1];
                    // \s \S \n \r \v \f \D \W can match a newline (\D/\W are
                    // negations that include it); \p{...}/\P{...} categories may too;
                    // \xNN/\uNNNN ranges could cover 0x0A; \cJ/\cM are LF/CR — all
                    // conservative.
                    if (n == 's' || n == 'S' || n == 'n' || n == 'r' || n == 'v' || n == 'f'
                        || n == 'D' || n == 'W' || n == 'p' || n == 'P' || n == 'x' || n == 'u'
                        || n == 'c')
                        return true;
                    i++;
                    continue;
                }
                if (ch == '[')
                {
                    // A negated class [^...] matches a newline (anything but the listed
                    // chars) — conservative fallback.
                    if (i + 1 < pattern.Length && pattern[i + 1] == '^') return true;
                    inClass = true;
                }
                else if (ch == ']') inClass = false;
                else if (!inClass && ch == '(' && i + 1 < pattern.Length && pattern[i + 1] == '?'
                         && i + 2 < pattern.Length)
                {
                    // Inline option group: (?s), (?is:...), (?i-s:...). If ANY option
                    // letter is 's', Singleline is active and '.' can match a newline.
                    // Scan the full option run (review nit: (?is)/(?si)/(?ms) put a
                    // non-'s' char at i+2 and used to slip past as a false negative).
                    for (int k = i + 2; k < pattern.Length; k++)
                    {
                        char oc = pattern[k];
                        if (oc == ')' || oc == ':') break;
                        if (oc == 's') return true;
                        if (oc != 'i' && oc != 'm' && oc != 'n' && oc != 'x' && oc != '-')
                            break; // not an option run; stop scanning
                    }
                }
            }
            return false;
        }

        private static readonly Regex LiteralTokenRegex = new Regex(@"[A-Za-z0-9_]{3,}", RegexOptions.Compiled);
        private static readonly char[] ObjectNameSeparators = { ',', ';', '\n', '\r' };

        // Alphanumeric runs >=3 chars; final regex.IsMatch still gates output so a
        // permissive pre-filter is safe.
        internal static System.Collections.Generic.List<string> ExtractLiteralTokens(string pattern, string callee)
        {
            var toks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(callee))
            {
                var matches = LiteralTokenRegex.Matches(callee);
                for (int i = 0; i < matches.Count; i++) toks.Add(matches[i].Value);
            }
            if (!string.IsNullOrEmpty(pattern))
            {
                var matches = LiteralTokenRegex.Matches(pattern);
                for (int i = 0; i < matches.Count; i++) toks.Add(matches[i].Value);
            }
            return toks.ToList();
        }

        // Issue #27 item 4: parse the objectName scope (comma/semicolon/newline-separated)
        // into a case-insensitive set, or null when no scope was supplied.
        private static HashSet<string> ParseObjectNames(string objectName)
        {
            if (string.IsNullOrWhiteSpace(objectName)) return null;
            var parts = objectName.Split(ObjectNameSeparators, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return null;
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < parts.Length; i++)
            {
                var s = parts[i].Trim();
                if (s.Length > 0) set.Add(s);
            }
            return set.Count > 0 ? set : null;
        }

        // issue #36.7 — match an object name against the requested set, tolerant of
        // module qualification on EITHER side (index-qualified vs user-bare and vice-versa).
        private static bool ObjectNameMatches(HashSet<string> wanted, string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (wanted.Contains(name)) return true;
            int dot = name.LastIndexOf('.');
            if (dot >= 0 && wanted.Contains(name.Substring(dot + 1))) return true; // index qualified, user bare
            foreach (var w in wanted)
            {
                int wd = w.LastIndexOf('.'); // user qualified, index bare
                if (wd >= 0 && string.Equals(w.Substring(wd + 1), name, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static bool ObjectPathMatches(Models.SearchIndex.IndexEntry entry, string wanted)
        {
            if (entry == null || string.IsNullOrWhiteSpace(wanted)) return false;
            string expected = wanted.Trim().Replace('\\', '/');
            string path = (entry.Path ?? string.Empty).Replace('\\', '/');
            string withoutRoot = path.StartsWith("Root Module/", StringComparison.OrdinalIgnoreCase)
                ? path.Substring("Root Module/".Length) : path;
            return string.Equals(path, expected, StringComparison.OrdinalIgnoreCase)
                || string.Equals(withoutRoot, expected, StringComparison.OrdinalIgnoreCase)
                || string.Equals(withoutRoot.Replace('/', '.'), expected, StringComparison.OrdinalIgnoreCase);
        }

        internal static bool MatchesAnyLiteral(Models.SearchIndex.IndexEntry e, System.Collections.Generic.List<string> literals)
        {
            if (literals == null || literals.Count == 0) return true;
            // issue #25 #3: the literal pre-filter is only SOUND when the index
            // actually holds this entry's body text. SourceSnippet/Keywords are
            // never populated for Procedure/DataProvider/WebPanel/Transaction
            // (the types searched here), so treating an empty snippet as
            // "no match" silently drops entries whose full source contains the
            // token — a false empty-success. When there is no indexed text to
            // prove absence, include the entry so its full source gets read.
            bool hasIndexedText = e.FullSource != null
                || !string.IsNullOrEmpty(e.SourceSnippet)
                || (e.Keywords != null && e.Keywords.Count > 0);
            if (!hasIndexedText) return true;
            string snip = e.FullSource ?? e.SourceSnippet ?? "";
            string nm = e.Name ?? "";
            for (int i = 0; i < literals.Count; i++)
            {
                var t = literals[i];
                if (snip.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (nm.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (e.Keywords != null)
                {
                    for (int k = 0; k < e.Keywords.Count; k++)
                        if (e.Keywords[k] != null && e.Keywords[k].IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                }
            }
            return false;
        }

        internal static List<Models.SearchIndex.IndexEntry> PrioritizeCompleteSourceCandidates(
            IEnumerable<Models.SearchIndex.IndexEntry> candidates, int maxResults)
        {
            var ordered = candidates?.ToList()
                ?? new List<Models.SearchIndex.IndexEntry>();
            if (maxResults <= 0) return ordered;

            int completeCount = ordered.Count(e => e != null && e.FullSource != null);
            if (completeCount == 0) return ordered;

            return ordered
                .OrderByDescending(e => e != null && e.FullSource != null)
                .ToList();
        }

        private static bool IsIndexedSourceScope(List<string> scope)
        {
            if (scope == null || scope.Count == 0) return true;
            foreach (string part in scope)
            {
                if (!IsSourceAlias(part)) return false;
            }
            return true;
        }

        private static bool IsSourceAlias(string part)
        {
            return string.IsNullOrWhiteSpace(part)
                || string.Equals(part, "source", StringComparison.OrdinalIgnoreCase)
                || string.Equals(part, "code", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetEntryStorageKey(SearchIndex.IndexEntry entry)
        {
            if (entry == null) return string.Empty;
            if (!string.IsNullOrEmpty(entry.StorageKey)) return entry.StorageKey;
            string type = entry.Type ?? string.Empty;
            string name = string.Equals(type, "Folder", StringComparison.OrdinalIgnoreCase)
                || string.Equals(type, "Module", StringComparison.OrdinalIgnoreCase)
                ? entry.Path ?? entry.Name ?? string.Empty
                : entry.Name ?? string.Empty;
            entry.StorageKey = type + ":" + name;
            return entry.StorageKey;
        }

        private static bool CalleeMatches(string actual, string wanted)
        {
            if (string.IsNullOrEmpty(actual) || string.IsNullOrEmpty(wanted)) return false;
            if (string.Equals(actual, wanted, StringComparison.OrdinalIgnoreCase)) return true;
            int dot = actual.LastIndexOf('.');
            if (dot >= 0 && string.Equals(actual.Substring(dot + 1), wanted, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static bool ArgsMatch(List<string> args, Dictionary<int, string> wanted)
        {
            foreach (var kv in wanted)
            {
                if (kv.Key < 0 || kv.Key >= args.Count) return false;
                if (!string.Equals(NormalizeLiteral(args[kv.Key]), NormalizeLiteral(kv.Value), StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            return true;
        }

        private static string NormalizeLiteral(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            s = s.Trim();
            if (s.Length >= 2 && (s[0] == '"' || s[0] == '\'') && s[s.Length - 1] == s[0])
                s = s.Substring(1, s.Length - 2);
            return s;
        }

        private static JObject BuildHit(SearchIndex.IndexEntry e, string part, string[] lines, int line, ParsedCall call)
        {
            const int ctx = 3;
            int idx = line - 1;
            string lineText = idx >= 0 && idx < lines.Length ? lines[idx] : "";

            var before = new JArray();
            for (int i = Math.Max(0, idx - ctx); i < idx; i++) before.Add(lines[i]);
            var after = new JArray();
            for (int i = idx + 1; i < Math.Min(lines.Length, idx + 1 + ctx); i++) after.Add(lines[i]);

            var hit = new JObject
            {
                ["objectName"] = e.Name,
                ["type"] = e.Type,
                ["guid"] = e.Guid,
                ["entityKey"] = e.EntityKey,
                ["path"] = e.Path,
                ["part"] = part,
                ["lineNumber"] = line,
                ["lineText"] = lineText,
                ["contextBefore"] = before,
                ["contextAfter"] = after
            };
            if (call != null)
            {
                var argsArr = new JArray();
                foreach (var a in call.Args) argsArr.Add(a);
                hit["matchedCall"] = new JObject { ["callee"] = call.Callee, ["args"] = argsArr };
            }
            return hit;
        }

        private static string TryGetPartSource(KBObject obj, string partName)
        {
            try
            {
                if (string.Equals(partName, "source", StringComparison.OrdinalIgnoreCase))
                {
                    // Procedure exposes its native source part directly. Avoiding Parts
                    // enumeration and dynamic dispatch is the cheapest SDK-only path;
                    // other object kinds retain the compatibility fallback below.
                    if (obj is Artech.Genexus.Common.Objects.Procedure procedure && procedure.ProcedurePart != null)
                        return procedure.ProcedurePart.Source ?? "";
                    dynamic sp = obj.Parts.Cast<KBObjectPart>().FirstOrDefault(p => p is ISource);
                    return sp?.Source ?? "";
                }
                if (string.Equals(partName, "rules", StringComparison.OrdinalIgnoreCase))
                {
                    if (obj is Artech.Genexus.Common.Objects.Procedure procedure)
                        return procedure.Rules?.Source ?? "";
                    if (obj is Artech.Genexus.Common.Objects.Transaction transaction)
                        return transaction.Rules?.Source ?? "";
                    if (obj is Artech.Genexus.Common.Objects.WebPanel webPanel)
                        return webPanel.Rules?.Source ?? "";
                    try { return ((dynamic)obj).Rules?.Source ?? ""; } catch { return ""; }
                }
                if (string.Equals(partName, "conditions", StringComparison.OrdinalIgnoreCase))
                {
                    try { return ((dynamic)obj).Conditions?.Source ?? ""; } catch { return ""; }
                }
                if (string.Equals(partName, "events", StringComparison.OrdinalIgnoreCase))
                {
                    if (obj is Artech.Genexus.Common.Objects.Transaction transaction)
                        return transaction.Events?.Source ?? "";
                    if (obj is Artech.Genexus.Common.Objects.WebPanel webPanel)
                        return webPanel.Events?.Source ?? "";
                    try { return ((dynamic)obj).Events?.Source ?? ""; } catch { return ""; }
                }
                // WebForm / Layout — the visual XML of WebPanels/Transactions. Not an
                // ISource part, so it has to be read through WebFormXmlHelper. Searching it
                // via scope lets callers grep control names, captions, classes and bindings
                // with the same line-numbered context as a source scan, instead of the
                // whole-blob matchedValue the fields=[webForm] metadata path returns.
                if (string.Equals(partName, "webForm", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(partName, "layout", StringComparison.OrdinalIgnoreCase))
                {
                    try { return GxMcp.Worker.Helpers.WebFormXmlHelper.ReadEditableXml(obj) ?? ""; } catch { return ""; }
                }
            }
            catch { }
            return "";
        }
    }
}
