using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Models;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// Narrow change-set contract for existing object text parts. Preview and
    /// validate read the exact target set; apply recomputes the same revision,
    /// injects per-target version fences, and delegates persistence to the
    /// MutationEngine so the receipt has one safety semantics.
    /// </summary>
    public sealed class ChangeSetService
    {
        private static readonly HashSet<string> SupportedParts = new HashSet<string>(
            new[] { "Source", "Rules", "Variables" },
            StringComparer.OrdinalIgnoreCase);

        private readonly MutationEngine _mutationEngine;

        public ChangeSetService(MutationEngine mutationEngine)
        {
            _mutationEngine = mutationEngine ?? throw new ArgumentNullException(nameof(mutationEngine));
        }

        public string Run(JObject args)
        {
            var changeSet = args?["changeSet"] as JObject ?? args ?? new JObject();
            string action = (changeSet["action"]?.ToString() ?? "preview").Trim().ToLowerInvariant();
            if (action != "preview" && action != "validate" && action != "apply")
            {
                return McpResponse.Err(
                    "ChangeSetActionInvalid",
                    $"Unsupported changeSet action '{action}'.",
                    "Use action=preview, validate, or apply.");
            }

            var targets = NormalizeTargets(changeSet["changes"] as JArray ?? changeSet["targets"] as JArray, out string inputError);
            if (targets == null)
                return McpResponse.Err("ChangeSetInvalid", inputError ?? "A non-empty changes array is required.");

            var request = new MutationRequest
            {
                Targets = targets,
                RollbackOnFailure = changeSet["rollbackOnFailure"]?.ToObject<bool?>()
                    ?? args?["rollbackOnFailure"]?.ToObject<bool?>()
                    ?? true
            };
            MutationPlan plan;
            try
            {
                plan = _mutationEngine.Plan(request);
            }
            catch (Exception ex)
            {
                return McpResponse.Err("ChangeSetStateUnavailable", ex.Message,
                    "Re-read every target and retry preview against the active KB.");
            }

            JObject planJson = plan.ToJson();
            string changeSetId = ComputeChangeSetId(targets);
            string currentRevision = ComputeRevision(plan.Mutations);
            planJson["changeSetId"] = changeSetId;
            planJson["baseRevision"] = currentRevision;
            planJson["action"] = action;

            if (action == "preview")
            {
                return McpResponse.Ok(code: "ChangeSetPreview", result: planJson);
            }

            if (plan.Mutations.Any(item => item["currentVersion"] == null))
            {
                return McpResponse.Err(
                    "ChangeSetStateUnavailable",
                    "At least one change-set target could not be read; no write was attempted.",
                    "Use genexus_read for every target and retry preview.",
                    extra: new JObject { ["plan"] = planJson });
            }

            if (!plan.IsValid)
            {
                return McpResponse.Err(
                    "ChangeSetInvalid",
                    "The change-set version fence is not valid; no write was attempted.",
                    "Refresh the target versions and run preview again.",
                    extra: new JObject { ["plan"] = planJson });
            }

            if (action == "validate")
            {
                return McpResponse.Ok(
                    code: "ChangeSetValidated",
                    result: new JObject
                    {
                        ["valid"] = true,
                        ["changeSetId"] = changeSetId,
                        ["baseRevision"] = currentRevision,
                        ["plan"] = planJson
                    });
            }

            string expectedRevision = changeSet["baseRevision"]?.ToString();
            string expectedChangeSetId = changeSet["changeSetId"]?.ToString();
            if (string.IsNullOrWhiteSpace(expectedChangeSetId))
            {
                return McpResponse.Err(
                    "ChangeSetIdRequired",
                    "apply requires the changeSetId returned by preview or validate.",
                    "Pass the unchanged changeSetId and baseRevision from the validated plan.",
                    extra: new JObject { ["changeSetId"] = changeSetId });
            }
            if (!string.Equals(expectedChangeSetId, changeSetId, StringComparison.OrdinalIgnoreCase))
            {
                return McpResponse.Err(
                    "ChangeSetIdConflict",
                    "The supplied changeSetId does not match the requested changes; no write was attempted.",
                    "Use the exact changes and changeSetId returned by preview or validate.",
                    extra: new JObject { ["expectedChangeSetId"] = expectedChangeSetId, ["currentChangeSetId"] = changeSetId });
            }
            if (string.IsNullOrWhiteSpace(expectedRevision))
            {
                return McpResponse.Err(
                    "ChangeSetBaseRevisionRequired",
                    "apply requires the baseRevision returned by preview or validate.",
                    "Pass the unchanged changeSetId and baseRevision from the validated plan.",
                    extra: new JObject { ["changeSetId"] = changeSetId, ["currentRevision"] = currentRevision });
            }

            if (!string.Equals(expectedRevision, currentRevision, StringComparison.OrdinalIgnoreCase))
            {
                return McpResponse.Err(
                    "ChangeSetConflict",
                    "One or more change-set targets changed after preview; no write was attempted.",
                    "Run preview again and apply only the new baseRevision.",
                    extra: new JObject
                    {
                        ["changeSetId"] = changeSetId,
                        ["expectedRevision"] = expectedRevision,
                        ["currentRevision"] = currentRevision,
                        ["plan"] = planJson
                    });
            }

            // A preview without explicit expectedVersion fields is still safe:
            // pin each target to the freshly-read version before the write gate.
            for (int i = 0; i < targets.Count && i < plan.Mutations.Count; i++)
            {
                if (targets[i]["expectedVersion"] == null && targets[i]["baseVersion"] == null)
                    targets[i]["expectedVersion"] = plan.Mutations[i]["currentVersion"];
            }

            MutationResult applied;
            try
            {
                applied = _mutationEngine.Execute(request);
            }
            catch (Exception ex)
            {
                return McpResponse.Err("ChangeSetFailed", ex.Message,
                    "Inspect the affected targets before retrying this change set.",
                    extra: new JObject { ["changeSetId"] = changeSetId, ["baseRevision"] = currentRevision });
            }

            JObject response;
            try
            {
                response = JObject.Parse(applied.ResponseJson ?? McpResponse.Err("ChangeSetFailed", "The mutation engine returned no response."));
            }
            catch
            {
                return McpResponse.Err("ChangeSetFailed", "The mutation engine returned an invalid response.",
                    extra: new JObject { ["changeSetId"] = changeSetId, ["baseRevision"] = currentRevision });
            }

            response["changeSetId"] = changeSetId;
            response["baseRevision"] = currentRevision;
            var rollbackOutcome = response["rollback"]?["outcome"]?.ToString();
            response["atomicity"] = string.Equals(response["status"]?.ToString(), "ok", StringComparison.OrdinalIgnoreCase)
                ? "compensated"
                : string.Equals(rollbackOutcome, "confirmed", StringComparison.OrdinalIgnoreCase)
                    ? "compensated"
                    : rollbackOutcome ?? "unknown";
            if (string.Equals(response["status"]?.ToString(), "ok", StringComparison.OrdinalIgnoreCase)
                && response["code"] == null)
                response["code"] = "ChangeSetApplied";
            return response.ToString(Formatting.None);
        }

        private static JArray NormalizeTargets(JArray raw, out string error)
        {
            error = null;
            if (raw == null || raw.Count == 0)
            {
                error = "changeSet.changes must be a non-empty array.";
                return null;
            }

            var normalized = new JArray();
            for (int i = 0; i < raw.Count; i++)
            {
                if (!(raw[i] is JObject item))
                {
                    error = $"changeSet.changes[{i}] must be an object.";
                    return null;
                }

                string target = item["name"]?.ToString() ?? item["target"]?.ToString();
                string part = item["part"]?.ToString() ?? "Source";
                string content = item["content"]?.ToString() ?? item["source"]?.ToString();
                if (string.IsNullOrWhiteSpace(target) || content == null)
                {
                    error = $"changeSet.changes[{i}] requires name/target and content.";
                    return null;
                }
                if (!SupportedParts.Contains(part))
                {
                    error = $"changeSet.changes[{i}].part '{part}' is outside the certified Source/Rules/Variables slice.";
                    return null;
                }

                var entry = new JObject
                {
                    ["target"] = target,
                    ["part"] = part,
                    ["content"] = content
                };
                if (item["expectedVersion"] != null)
                    entry["expectedVersion"] = item["expectedVersion"];
                else if (item["baseVersion"] != null)
                    entry["baseVersion"] = item["baseVersion"];
                normalized.Add(entry);
            }
            return normalized;
        }

        private static string ComputeChangeSetId(JArray targets)
        {
            var canonical = targets
                .OfType<JObject>()
                .Select(item => new JObject
                {
                    ["target"] = item["target"],
                    ["part"] = item["part"],
                    ["content"] = item["content"],
                    ["expectedVersion"] = item["expectedVersion"] ?? item["baseVersion"]
                })
                .ToArray();
            return Hash(new JArray(canonical).ToString(Formatting.None));
        }

        private static string ComputeRevision(JArray mutations)
        {
            var rows = mutations
                .OfType<JObject>()
                .Select(item => new
                {
                    Target = item["target"]?.ToString() ?? string.Empty,
                    Part = item["part"]?.ToString() ?? string.Empty,
                    Version = item["currentVersion"]?.ToString() ?? string.Empty
                })
                .OrderBy(row => row.Target, StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => row.Part, StringComparer.OrdinalIgnoreCase)
                .Select(row => row.Target + "\u001f" + row.Part + "\u001f" + row.Version);
            return Hash(string.Join("\u001e", rows));
        }

        private static string Hash(string value)
        {
            using (var sha = SHA256.Create())
            {
                byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty));
                return BitConverter.ToString(bytes).Replace("-", string.Empty).Substring(0, 16).ToLowerInvariant();
            }
        }
    }
}
