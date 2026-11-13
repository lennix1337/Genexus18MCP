using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    /// <summary>
    /// <c>genexus_impact whatIf=true</c>: one bounded, read-only answer to "what happens if I change
    /// this object?", composed in the Gateway from commands the Worker already serves. The Worker runs
    /// one command at a time, so the legs run sequentially, cheapest first: the caller graph (search
    /// index), the build-time history (Gateway memory), then the schema diff (SDK model walk, never the
    /// build-heavy deep analysis). Each section reports complete / partial / skipped with its reason; a
    /// failed or late leg degrades its section and never the call.
    /// </summary>
    internal static class ImpactWhatIf
    {
        internal const int DefaultBudgetMs = 20000;
        internal const int TopN = 10;
        /// <summary>Callers at or above this count make the change "wide-impact".</summary>
        internal const int WideImpactCallers = 25;
        internal const string BuildHistoryKind = "lifecycle/build";

        internal const string Complete = "complete";
        internal const string Partial = "partial";
        internal const string Skipped = "skipped";

        internal const string Safe = "safe";
        internal const string NeedsReorg = "needs-reorg";
        internal const string WideImpact = "wide-impact";
        internal const string Unknown = "unknown";

        // The target could not be identified, so no section can be composed: the leg's own refusal is the answer.
        private static readonly HashSet<string> TargetErrors = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "MissingTarget", "AmbiguousName", "ObjectNotFound", "IndexNotReady" };

        private static readonly string[] SelectorKeys =
            { "name", "guid", "entityKey", "path", "type", "kb", "maxDepth", "maxNodes", "typeFilter" };

        internal sealed class LegResult
        {
            internal bool IsError;
            internal string? Code;
            internal string? Message;
            internal JObject? Payload;
        }

        internal delegate Task<LegResult> LegRunner(string tool, JObject args, CancellationToken cancellationToken);

        internal sealed class Outcome
        {
            internal JObject Payload = new JObject();
            internal bool IsError;
        }

        private sealed class Section
        {
            internal string Status = Skipped;
            internal string? Reason;
            internal bool NotApplicable;
            internal JObject Body = new JObject();

            internal JObject ToJson()
            {
                var json = new JObject { ["status"] = Status };
                if (Reason != null) json["reason"] = Reason;
                foreach (var property in Body.Properties()) json[property.Name] = property.Value;
                return json;
            }
        }

        internal static async Task<Outcome> ComposeAsync(
            JObject args, LegRunner run, Func<int?> buildSeconds, TimeSpan budget, CancellationToken cancellationToken)
        {
            var clock = Stopwatch.StartNew();
            var callers = new Section();
            var regeneration = new Section();
            var build = new Section();
            var database = new Section();

            // 1. Caller graph: index only. Callers are what an interface change reaches, so direction is always up.
            var impactArgs = new JObject();
            foreach (string key in SelectorKeys)
                if (args[key] != null && args[key]!.Type != JTokenType.Null) impactArgs[key] = args[key]!.DeepClone();
            impactArgs["direction"] = "up";
            var impactLeg = await RunLegAsync(run, "genexus_impact", impactArgs, budget, clock, cancellationToken);
            if (impactLeg.Result is { IsError: true } refusal && refusal.Code != null && TargetErrors.Contains(refusal.Code))
                return new Outcome { Payload = refusal.Payload ?? new JObject { ["error"] = refusal.Message }, IsError = true };

            JObject? rootIdentity = null;
            JObject? impact = null;
            if (impactLeg.Result is { IsError: false, Payload: not null } ok)
            {
                impact = ok.Payload["result"] as JObject ?? ok.Payload;
                rootIdentity = impact["root"] as JObject;
            }
            if (impact != null && rootIdentity != null)
            {
                FillCallers(callers, regeneration, impact);
            }
            else
            {
                string reason = impactLeg.Failure ?? "the impact leg returned no result";
                callers.Reason = regeneration.Reason = reason;
            }

            // 2. Build time: Gateway memory, no Worker round trip.
            int? seconds = null;
            try { seconds = buildSeconds(); } catch { }
            if (seconds.HasValue)
            {
                build.Status = Complete;
                build.Body["typicalSeconds"] = seconds.Value;
                build.Body["basis"] = "median of recent builds in this Gateway session, not scaled to the regeneration count";
            }
            else
            {
                build.Reason = "no build has completed in this Gateway session, so there is no history to estimate from";
            }

            // 3. Schema consequences: SDK model walk (shallow; the build-heavy deep analysis is never run here).
            string? type = rootIdentity?["type"]?.ToString();
            string? rootName = rootIdentity?["name"]?.ToString();
            string dbKind = DatabaseLegFor(type);
            if (rootIdentity == null)
            {
                database.Reason = "target type unknown because the impact leg did not resolve the object";
            }
            else if (dbKind == "none")
            {
                database.NotApplicable = true;
                database.Reason = "a " + (type ?? "object of this type") + " is not stored in the database";
            }
            else
            {
                var dbArgs = dbKind == "preview"
                    ? new JObject { ["action"] = "reorg_preview", ["name"] = rootName, ["type"] = type }
                    : new JObject { ["action"] = "reorg_impact" };
                if (args["kb"] != null) dbArgs["kb"] = args["kb"]!.DeepClone();
                var dbLeg = await RunLegAsync(run, "genexus_db", dbArgs, budget, clock, cancellationToken);
                if (dbLeg.Result is { IsError: false, Payload: not null } dbOk)
                    FillDatabase(database, dbOk.Payload["result"] as JObject ?? dbOk.Payload, dbKind, rootName!);
                else
                    database.Reason = dbLeg.Failure ?? "the database leg returned no result";
            }

            string name = rootName ?? args["name"]?.ToString() ?? string.Empty;
            callers.Body["followUp"] = "genexus_impact name=" + name + " (without whatIf): full affected set, rebuild order, cycles; raise maxDepth/maxNodes";
            regeneration.Body["followUp"] = "genexus_impact name=" + name + " (without whatIf): `order` is the rebuild order";
            build.Body["followUp"] = "genexus_lifecycle action=build starts the build; action=status reads its progress";
            if (!database.NotApplicable)
                database.Body["followUp"] = "genexus_db action=reorg_preview name=" + name + " deep=true: authoritative verdict (build-heavy); action=sql_ddl: DDL";

            var (verdict, verdictReason) = DeriveVerdict(callers, database);
            var sections = new[] { callers, regeneration, build, database };
            var payload = new JObject
            {
                ["target"] = rootIdentity != null
                    ? new JObject { ["name"] = rootName, ["type"] = type, ["guid"] = rootIdentity["guid"] }
                    : (JToken)name,
                ["verdict"] = verdict,
                ["verdictReason"] = verdictReason,
                ["complete"] = sections.All(s => s.NotApplicable || s.Status == Complete),
                ["budgetMs"] = (int)budget.TotalMilliseconds,
                ["elapsedMs"] = (int)clock.ElapsedMilliseconds,
                ["callers"] = callers.ToJson(),
                ["regeneration"] = regeneration.ToJson(),
                ["database"] = database.ToJson(),
                ["build"] = build.ToJson(),
                ["note"] = "State of the KB now, not a simulation of a proposed edit: genexus_edit dryRun=true plans the edit itself."
            };
            return new Outcome { Payload = payload };
        }

        /// <summary>"preview" (Transaction/Table, target scoped), "kb" (Attribute/Domain, KB-wide heuristic) or "none".</summary>
        private static string DatabaseLegFor(string? type)
        {
            if (string.Equals(type, "Transaction", StringComparison.OrdinalIgnoreCase)
                || string.Equals(type, "Table", StringComparison.OrdinalIgnoreCase)) return "preview";
            if (string.Equals(type, "Attribute", StringComparison.OrdinalIgnoreCase)
                || string.Equals(type, "Domain", StringComparison.OrdinalIgnoreCase)) return "kb";
            return "none";
        }

        private sealed class LegOutcome
        {
            internal LegResult? Result;
            /// <summary>Why the leg produced no usable result (budget, timeout, error), or null.</summary>
            internal string? Failure;
        }

        private static async Task<LegOutcome> RunLegAsync(
            LegRunner run, string tool, JObject legArgs, TimeSpan budget, Stopwatch clock, CancellationToken cancellationToken)
        {
            TimeSpan remaining = budget - clock.Elapsed;
            if (remaining <= TimeSpan.Zero)
                return new LegOutcome { Failure = "skipped: the " + (int)budget.TotalMilliseconds + " ms budget was spent before this section ran" };

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task<LegResult> leg;
            try { leg = run(tool, legArgs, cts.Token); }
            catch (Exception ex) { return new LegOutcome { Failure = "error: " + ex.Message }; }

            using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task finished = await Task.WhenAny(leg, Task.Delay(remaining, delayCts.Token));
            delayCts.Cancel();
            if (finished != leg)
            {
                cts.Cancel();
                // The Worker call cannot be recalled; observe its outcome so a late failure is not unobserved.
                _ = leg.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                cancellationToken.ThrowIfCancellationRequested();
                return new LegOutcome { Failure = "skipped: still running when the " + (int)budget.TotalMilliseconds + " ms budget ran out" };
            }

            try
            {
                LegResult result = await leg;
                if (result.IsError)
                    return new LegOutcome { Result = result, Failure = "error " + (result.Code ?? "Unknown") + ": " + result.Message };
                return new LegOutcome { Result = result };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                return new LegOutcome { Failure = "error: " + ex.Message };
            }
        }

        private static void FillCallers(Section callers, Section regeneration, JObject impact)
        {
            var stats = impact["stats"] as JObject;
            var affected = impact["affected"] as JArray ?? new JArray();
            int total = stats?["affectedCount"]?.ToObject<int?>() ?? affected.Count;
            bool truncated = stats?["truncated"]?.ToObject<bool?>() ?? false;
            string trust = impact["fieldTrust"]?.ToString() ?? "complete";
            var truncatedBy = stats?["truncatedBy"] as JArray;
            bool complete = !truncated && string.Equals(trust, "complete", StringComparison.Ordinal);

            // The Worker already orders the affected set by depth, then name: direct callers come first.
            var shown = new JArray(affected.Take(TopN).Select(n => new JObject
            {
                ["name"] = n["name"], ["type"] = n["type"], ["depth"] = n["depth"]
            }));
            callers.Body["total"] = total;
            callers.Body["shown"] = shown;
            callers.Body["omitted"] = Math.Max(0, total - shown.Count);
            callers.Body["countsByType"] = impact["countsByType"] ?? new JObject();
            callers.Body["trust"] = trust;
            if (truncatedBy != null && truncatedBy.Count > 0) callers.Body["truncatedBy"] = truncatedBy;

            string? reason = null;
            if (!string.Equals(trust, "complete", StringComparison.Ordinal))
                reason = impact["coverageNote"]?.ToString() ?? "the caller graph was only partly read (" + trust + ")";
            else if (truncated)
                reason = "traversal stopped at " + string.Join("/", (truncatedBy ?? new JArray()).Select(t => t.ToString())) + "; there are more callers than counted";
            callers.Status = complete ? Complete : Partial;
            callers.Reason = reason;

            // The root plus every caller reached: those are the objects whose generated code changes.
            regeneration.Status = callers.Status;
            regeneration.Reason = callers.Reason == null ? null : "lower bound: " + callers.Reason;
            regeneration.Body["objects"] = total + 1;
            regeneration.Body["basis"] = "the object plus its callers within the depth/node caps";
        }

        private static void FillDatabase(Section database, JObject result, string dbKind, string name)
        {
            if (dbKind == "kb")
            {
                bool? pending = result["reorgLikelyNeeded"]?.ToObject<bool?>();
                database.Status = Partial;
                database.Reason = "KB-wide timestamp heuristic: a change to this object can alter tables, but the reorganisation check does not name them";
                database.Body["scope"] = "kb";
                database.Body["reorgPending"] = pending.HasValue ? (JToken)pending.Value : JValue.CreateNull();
                database.Body["changes"] = 0;
                database.Body["destructiveWarnings"] = 0;
                return;
            }

            var tableEntries = (result["tables"] as JArray ?? new JArray()).OfType<JObject>().ToList();
            var summary = result["summary"] as JObject;
            int changes = summary?["changes"]?.ToObject<int?>() ?? 0;
            int destructive = summary?["destructiveWarnings"]?.ToObject<int?>() ?? 0;
            var tables = new JArray(tableEntries.Take(TopN).Select(t =>
            {
                var kinds = new JObject();
                foreach (var change in (t["changes"] as JArray ?? new JArray()).OfType<JObject>())
                {
                    string kind = change["field"]?.ToString() ?? "changed";
                    kinds[kind] = (kinds[kind]?.ToObject<int>() ?? 0) + 1;
                }
                return new JObject
                {
                    ["name"] = t["tableName"],
                    ["changes"] = (t["changes"] as JArray)?.Count ?? 0,
                    ["kinds"] = kinds,
                    ["destructive"] = (t["warnings"] as JArray)?.Count ?? 0
                };
            }));
            bool? requires = result["requiresReorganization"]?.Type == JTokenType.Boolean
                ? result["requiresReorganization"]!.ToObject<bool>()
                : (bool?)null;
            database.Status = Complete;
            database.Body["scope"] = "target";
            database.Body["depth"] = "shallow model diff (logical vs recorded physical structure); the deep verdict is not run";
            database.Body["tablesTotal"] = tableEntries.Count;
            database.Body["tables"] = tables;
            database.Body["omitted"] = Math.Max(0, tableEntries.Count - tables.Count);
            database.Body["changes"] = changes;
            database.Body["destructiveWarnings"] = destructive;
            database.Body["reorgPending"] = requires.HasValue ? (JToken)requires.Value : JValue.CreateNull();
        }

        /// <summary>
        /// Derived only from the section facts. needs-reorg and wide-impact are asserted from what was read;
        /// safe additionally requires every applicable section to be complete, so a missing fact reads as unknown.
        /// </summary>
        private static (string Verdict, string Reason) DeriveVerdict(Section callers, Section database)
        {
            int total = callers.Body["total"]?.ToObject<int?>() ?? 0;
            bool callersKnown = callers.Status != Skipped;
            bool truncatedByNodes = (callers.Body["truncatedBy"] as JArray)?.Any(t => t.ToString() == "maxNodes") == true;
            bool wide = callersKnown && (total >= WideImpactCallers || truncatedByNodes);

            bool dbRead = database.Status != Skipped;
            int changes = database.Body["changes"]?.ToObject<int?>() ?? 0;
            int destructive = database.Body["destructiveWarnings"]?.ToObject<int?>() ?? 0;
            bool kbPending = string.Equals(database.Body["scope"]?.ToString(), "kb", StringComparison.Ordinal)
                && database.Body["reorgPending"]?.Type == JTokenType.Boolean
                && database.Body["reorgPending"]!.ToObject<bool>();
            bool needsReorg = dbRead && (changes > 0 || destructive > 0 || kbPending);

            if (needsReorg)
            {
                string why = kbPending
                    ? "the KB already has a pending reorganisation"
                    : changes + " pending schema change(s)" + (destructive > 0 ? ", " + destructive + " destructive" : string.Empty)
                        + " across " + (database.Body["tablesTotal"]?.ToString() ?? "?") + " table(s)";
                return (NeedsReorg, why + (wide ? "; " + total + (truncatedByNodes ? "+" : string.Empty) + " callers also affected" : string.Empty));
            }
            if (wide)
                return (WideImpact, total + (truncatedByNodes ? "+" : string.Empty) + " callers reached (threshold " + WideImpactCallers + ")");

            var unconfirmed = new List<string>();
            if (callers.Status != Complete) unconfirmed.Add("callers (" + callers.Status + ")");
            if (!database.NotApplicable && database.Status != Complete) unconfirmed.Add("database (" + database.Status + ")");
            if (unconfirmed.Count > 0)
                return (Unknown, "not confirmed: " + string.Join(", ", unconfirmed));
            return (Safe, total + " caller(s), no pending schema change");
        }
    }
}
