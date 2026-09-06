using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    /// <summary>
    /// SOTA LLM-UX: every state-changing tool response carries a
    /// <c>next_legal_actions</c> array listing the most-likely useful next
    /// tool calls. Reduces cross-turn guessing for the LLM client.
    ///
    /// Pure function — given the tool name, request args, response payload,
    /// and whether the call was an error, returns an array of suggestion
    /// objects of the shape:
    /// <code>
    /// { "tool": "...", "args": {...}, "why": "...", "priority": "high|medium|low" }
    /// </code>
    /// Returns null when no suggestions apply (the gateway just doesn't
    /// attach the field). Capped at 3 suggestions per call (~80-120B each)
    /// to keep payloads small. Read-only tools (whoami / query / list /
    /// read / inspect / analyze) never produce suggestions.
    /// </summary>
    public static class NextLegalActionsBuilder
    {
        /// <summary>
        /// Build the suggestion array for a single tool response. Returns
        /// null when no suggestions apply.
        /// </summary>
        public static JArray? BuildFor(string toolName, JObject? args, JObject? responsePayload, bool isError)
        {
            if (string.IsNullOrWhiteSpace(toolName)) return null;

            // Resolve legacy aliases once, before both policy checks and the
            // action-specific switch. This keeps follow-up suggestions on the
            // canonical tool/action contract without removing compatibility for
            // older clients.
            args = OperationClassifier.NormalizeArguments(toolName, args, out var canonicalTool);
            toolName = canonicalTool;

            // Tools with explicit next-legal-action builders (genexus_read, genexus_query)
            // are allowed to produce suggestions even though they are read-only.
            if (!string.Equals(toolName, "genexus_read", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(toolName, "genexus_query", StringComparison.OrdinalIgnoreCase))
            {
                if (OperationClassifier.IsReadOnly(toolName, args)) return null;
            }

            args ??= new JObject();
            responsePayload ??= new JObject();

            JArray? suggestions = toolName.ToLowerInvariant() switch
            {
                "genexus_read" => isError ? null : BuildForRead(args, responsePayload),
                "genexus_query" => isError ? null : BuildForQuery(args, responsePayload),
                "genexus_apply_pattern" => BuildForApplyPattern(args, responsePayload, isError),
                "genexus_create" => isError ? null : (S(args["action"])?.ToLowerInvariant() switch
                {
                    "object" => BuildForCreateObject(args, responsePayload),
                    "popup" => BuildForCreatePopup(args, responsePayload),
                    "save_as" => BuildForSaveAs(args, responsePayload),
                    _ => null,
                }),
                "genexus_edit" => isError ? null : BuildForEdit(args, responsePayload),
                "genexus_lifecycle" => BuildForLifecycle(args, responsePayload, isError),
                "genexus_versioning" => isError ? null : BuildForVersioning(args, responsePayload),
                _ => null,
            };

            if (suggestions == null || suggestions.Count == 0) return null;

            // Cap at 3 suggestions per the token budget.
            while (suggestions.Count > 3) suggestions.RemoveAt(suggestions.Count - 1);
            return suggestions;
        }

        private static JObject Suggest(string tool, JObject args, string why, string priority)
            => new JObject
            {
                ["tool"] = tool,
                ["args"] = args,
                ["why"] = why,
                ["priority"] = priority,
            };

        private static string? S(JToken? t) => t?.Type == JTokenType.Null || t == null ? null : t.ToString();

        // 1 & 10. apply_pattern
        private static JArray? BuildForApplyPattern(JObject args, JObject payload, bool isError)
        {
            string? target = S(args["target"]) ?? S(payload["target"]) ?? S(payload["object"]);

            // Case 10: error with validParentTypes — guide the LLM to inspect
            // the target's actual type or create one of the valid types.
            if (isError)
            {
                if (payload["validParentTypes"] is JArray validTypes && validTypes.Count > 0)
                {
                    var arr = new JArray();
                    if (!string.IsNullOrEmpty(target))
                    {
                        arr.Add(Suggest(
                            "genexus_inspect",
                            new JObject { ["target"] = target },
                            "Confirm the target's actual GeneXus type before retrying apply_pattern",
                            "high"));
                    }
                    string firstValid = validTypes[0]?.ToString() ?? "Transaction";
                    arr.Add(Suggest(
                        "genexus_create",
                        new JObject { ["action"] = "object", ["type"] = firstValid, ["name"] = "NewHost" },
                        $"Create an object of a supported parent type (e.g. {firstValid}) and apply the pattern to it",
                        "medium"));
                    return arr;
                }
                return null;
            }

            // Case 1: success — host typically named WorkWithPlus{Target}, but
            // the worker may surface it as `hostName` / `host` / `created`.
            string? host = S(payload["hostName"]) ?? S(payload["host"]) ?? S(payload["created"]);
            if (string.IsNullOrEmpty(host) && !string.IsNullOrEmpty(target))
            {
                host = "WorkWithPlus" + target;
            }
            if (string.IsNullOrEmpty(host)) return null;

            var arr2 = new JArray
            {
                Suggest(
                    "genexus_lifecycle",
                    new JObject { ["action"] = "build", ["target"] = host },
                    "Verify the freshly-attached pattern compiles",
                    "high"),
                Suggest(
                    "genexus_edit",
                    new JObject { ["name"] = host, ["part"] = "PatternInstance" },
                    "Customize the WWP host's PatternInstance (do not edit the parent WebForm directly)",
                    "medium"),
            };
            if (!string.IsNullOrEmpty(target))
            {
                arr2.Add(Suggest(
                    "genexus_versioning",
                    new JObject { ["action"] = "history_restore", ["discard"] = true, ["target"] = target! },
                    "Revert if the pattern apply was wrong",
                    "low"));
            }
            return arr2;
        }

        // 2. create_object
        private static JArray? BuildForCreateObject(JObject args, JObject payload)
        {
            string? name = S(args["name"]) ?? S(payload["name"]) ?? S(payload["created"]);
            string? type = S(args["type"]) ?? S(payload["type"]);
            if (string.IsNullOrEmpty(name)) return null;

            string defaultPart = string.Equals(type, "Transaction", StringComparison.OrdinalIgnoreCase) ? "Structure"
                : string.Equals(type, "WebPanel", StringComparison.OrdinalIgnoreCase) ? "WebForm"
                : string.Equals(type, "Procedure", StringComparison.OrdinalIgnoreCase) ? "Source"
                : "Source";

            var arr = new JArray
            {
                Suggest(
                    "genexus_edit",
                    new JObject { ["name"] = name, ["part"] = defaultPart },
                    $"Edit the new {(type ?? "object")}'s {defaultPart} to add real content",
                    "high"),
            };
            bool patternable = string.Equals(type, "Transaction", StringComparison.OrdinalIgnoreCase)
                || string.Equals(type, "WebPanel", StringComparison.OrdinalIgnoreCase);
            if (patternable)
            {
                arr.Add(Suggest(
                    "genexus_apply_pattern",
                    new JObject { ["target"] = name, ["pattern"] = "WorkWithPlus" },
                    "Attach a WorkWithPlus / WorkWith pattern for a generated UI",
                    "medium"));
            }
            arr.Add(Suggest(
                "genexus_lifecycle",
                new JObject { ["action"] = "build", ["target"] = name },
                "Build the new object to surface any structural errors early",
                "low"));
            return arr;
        }

        // 3. create_popup
        private static JArray? BuildForCreatePopup(JObject args, JObject payload)
        {
            string? parent = S(args["parent"]) ?? S(args["caller"]) ?? S(payload["parent"]);
            string? popup = S(args["name"]) ?? S(payload["created"]) ?? S(payload["popup"]);
            if (string.IsNullOrEmpty(popup)) return null;

            var arr = new JArray();
            if (!string.IsNullOrEmpty(parent))
            {
                arr.Add(Suggest(
                    "genexus_edit",
                    new JObject
                    {
                        ["name"] = parent!,
                        ["part"] = "WebForm",
                        ["op"] = "add_button",
                        ["caption"] = "Open " + popup,
                        ["onClick"] = popup + ".Show()",
                    },
                    "Wire a button on the parent WebForm that opens the new popup",
                    "high"));
            }
            arr.Add(Suggest(
                "genexus_lifecycle",
                new JObject { ["action"] = "build", ["target"] = popup },
                "Build the popup target to confirm it compiles",
                "medium"));
            return arr;
        }

        // 4. edit (patch success)
        private static JArray? BuildForEdit(JObject args, JObject payload)
        {
            string? name = S(args["name"]) ?? S(payload["name"]) ?? S(payload["object"]);
            if (string.IsNullOrEmpty(name)) return null;

            // Treat any non-"No change" success as a real patch worth verifying.
            bool noChange = payload["noChange"]?.Value<bool>() == true;
            if (noChange) return null;

            var arr = new JArray
            {
                Suggest(
                    "genexus_lifecycle",
                    new JObject { ["action"] = "build", ["target"] = name },
                    "Verify the patch compiles",
                    "high"),
                Suggest(
                    "genexus_browser",
                    new JObject { ["action"] = "preview", ["mode"] = "run", ["target"] = name },
                    "Render the edited object in the headless browser to spot regressions",
                    "medium"),
                Suggest(
                    "genexus_versioning",
                    new JObject { ["action"] = "undo", ["target"] = name },
                    "Undo the patch if the build or preview shows it was wrong",
                    "low"),
            };
            return arr;
        }

        // 5 & 6. lifecycle build (Success and Failed)
        private static JArray? BuildForLifecycle(JObject args, JObject payload, bool isError)
        {
            string? action = S(args["action"]) ?? S(payload["action"]);
            if (!string.Equals(action, "build", StringComparison.OrdinalIgnoreCase)) return null;

            string? target = S(args["target"]) ?? S(payload["target"]);
            string? status = S(payload["status"]) ?? S(payload["result"]);

            bool failed = isError
                || string.Equals(status, "Failed", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "Error", StringComparison.OrdinalIgnoreCase);

            if (failed)
            {
                var arr = new JArray
                {
                    Suggest(
                        "genexus_telemetry",
                        new JObject { ["action"] = "logs", ["tail"] = 200 },
                        "Read the build log tail to find the first compile error",
                        "high"),
                };
                if (!string.IsNullOrEmpty(target))
                {
                    arr.Add(Suggest(
                        "genexus_versioning",
                        new JObject { ["action"] = "history_restore", ["discard"] = true, ["target"] = target! },
                        "Discard recent edits on the failing target if the regression came from this turn",
                        "medium"));
                    arr.Add(Suggest(
                        "genexus_analyze",
                        new JObject { ["mode"] = "impact", ["target"] = target! },
                        "Inspect downstream impact before re-attempting the build",
                        "low"));
                }
                return arr;
            }

            // partial_success → ask for status
            bool partial = string.Equals(status, "partial_success", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "PartialSuccess", StringComparison.OrdinalIgnoreCase);

            var ok = new JArray();
            if (!string.IsNullOrEmpty(target))
            {
                ok.Add(Suggest(
                    "genexus_browser",
                    new JObject { ["action"] = "preview", ["mode"] = "run", ["target"] = target! },
                    "Run the built object to verify it behaves correctly",
                    "high"));
            }
            else
            {
                ok.Add(Suggest(
                    "genexus_browser",
                    new JObject { ["action"] = "preview", ["mode"] = "run" },
                    "Run the KB's startup object to verify it behaves correctly",
                    "high"));
            }
            if (partial)
            {
                string? jobId = S(payload["jobId"]) ?? S(payload["job"]);
                var statusArgs = new JObject { ["action"] = "status" };
                if (!string.IsNullOrEmpty(jobId)) statusArgs["jobId"] = jobId;
                ok.Add(Suggest(
                    "genexus_lifecycle",
                    statusArgs,
                    "Build reported partial_success — poll lifecycle status for the failed sub-targets",
                    "medium"));
            }
            return ok.Count == 0 ? null : ok;
        }

        // 7. save_as
        private static JArray? BuildForSaveAs(JObject args, JObject payload)
        {
            string? newName = S(args["newName"]) ?? S(args["targetName"]) ?? S(payload["created"]) ?? S(payload["newName"]);
            if (string.IsNullOrEmpty(newName)) return null;

            var arr = new JArray
            {
                Suggest(
                    "genexus_edit",
                    new JObject { ["name"] = newName, ["part"] = "Source" },
                    "Edit the cloned object's parts so it diverges from the original",
                    "high"),
                Suggest(
                    "genexus_lifecycle",
                    new JObject { ["action"] = "build", ["target"] = newName },
                    "Build the cloned object to confirm it compiles standalone",
                    "medium"),
            };
            return arr;
        }

        // 8/9. versioning umbrella: history_restore + undo follow-ups.
        private static JArray? BuildForVersioning(JObject args, JObject payload)
        {
            string? action = S(args["action"]);
            string? target = S(args["target"]) ?? S(args["name"]) ?? S(payload["target"]);
            if (string.IsNullOrEmpty(target)) return null;

            if (string.Equals(action, "history_restore", StringComparison.OrdinalIgnoreCase))
            {
                return new JArray
                {
                    Suggest(
                        "genexus_lifecycle",
                        new JObject { ["action"] = "build", ["target"] = target! },
                        "Build the restored object to verify the rollback compiles",
                        "high"),
                };
            }

            if (string.Equals(action, "undo", StringComparison.OrdinalIgnoreCase))
            {
                return new JArray
                {
                    Suggest(
                        "genexus_lifecycle",
                        new JObject { ["action"] = "build", ["target"] = target! },
                        "Build to confirm the undo left the KB in a valid state",
                        "high"),
                    Suggest(
                        "genexus_inspect",
                        new JObject { ["target"] = target! },
                        "Inspect the object to confirm the expected pre-edit shape was restored",
                        "medium"),
                };
            }

            return null;
        }

        // 11. read (success)
        private static JArray? BuildForRead(JObject args, JObject payload)
        {
            string? name = S(args["name"]) ?? S(payload["name"]) ?? S(payload["target"]);
            if (string.IsNullOrEmpty(name)) return null;

            string? type = S(payload["type"]) ?? S(args["type"]);
            string defaultPart = string.Equals(type, "Transaction", StringComparison.OrdinalIgnoreCase) ? "Structure"
                : string.Equals(type, "WebPanel", StringComparison.OrdinalIgnoreCase) ? "Events"
                : "Source";

            string currentPart = S(args["part"]) ?? defaultPart;

            var arr = new JArray
            {
                Suggest(
                    "genexus_edit",
                    new JObject { ["name"] = name, ["part"] = currentPart },
                    $"Edit {name}'s {currentPart}",
                    "high"),
                Suggest(
                    "genexus_analyze",
                    new JObject { ["mode"] = "linter", ["target"] = name },
                    $"Run static analysis / linter on {name}",
                    "medium"),
                Suggest(
                    "genexus_navigation",
                    new JObject { ["target"] = name },
                    $"Inspect table navigation / For Each loops for {name}",
                    "low"),
            };
            return arr;
        }

        // 12. query (success)
        private static JArray? BuildForQuery(JObject args, JObject payload)
        {
            JArray? results = payload["results"] as JArray;
            if (results == null || results.Count == 0)
            {
                string q = S(args["query"]) ?? string.Empty;
                var tokens = q.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length > 1)
                {
                    return new JArray
                    {
                        Suggest(
                            "genexus_query",
                            new JObject { ["query"] = tokens[0], ["limit"] = 10 },
                            $"Broaden search using single keyword '{tokens[0]}'",
                            "high"),
                        Suggest(
                            "genexus_list_objects",
                            new JObject { ["limit"] = 50 },
                            "List KB objects to explore available names",
                            "medium"),
                    };
                }
                return null;
            }

            if (results[0] is JObject topMatch)
            {
                string? topName = S(topMatch["name"]);
                string? topType = S(topMatch["type"]);
                if (!string.IsNullOrEmpty(topName))
                {
                    var arr = new JArray
                    {
                        Suggest(
                            "genexus_read",
                            new JObject { ["name"] = topName },
                            $"Read full 360° content of top match '{topName}' ({topType ?? "Object"})",
                            "high"),
                        Suggest(
                            "genexus_inspect",
                            new JObject { ["target"] = topName },
                            $"Inspect metadata, properties, and dependencies for '{topName}'",
                            "medium"),
                    };
                    return arr;
                }
            }

            return null;
        }
    }
}
