using System;
using System.Collections.Generic;
using System.Linq;
using GxMcp.Worker.Models;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// Wave 3 — IDE "Save As..." parity. Clones a KBObject's parts under a new
    /// name (same type, same module/folder). A linked WorkWithPlus pattern
    /// instance is not cloned: includePatternInstance is refused for a source that
    /// has one, and the answer names the instance a clone did not get (issue #800).
    ///
    /// All SDK access is hidden behind <see cref="IObjectCloner"/> so the
    /// service can be unit-tested without a live KB. The production cloner
    /// (<see cref="SdkObjectCloner"/>) wires through the existing
    /// ObjectService / WriteService / PatternApplyService code paths so we
    /// preserve every IDE-compatibility fix already living there.
    /// </summary>
    public class SaveAsService
    {
        /// <summary>
        /// Seam used by tests. Production code uses <see cref="SdkObjectCloner"/>.
        /// All methods return a JSON string (Success/Error envelope) to mirror
        /// the rest of the worker service surface.
        /// </summary>
        public interface IObjectCloner
        {
            /// <summary>Resolve a source object by name (+ optional type). Returns null when not found.</summary>
            SourceDescriptor FindSource(string name, string typeFilter);

            /// <summary>Returns true when an object with that name already exists in the KB.</summary>
            bool TargetExists(string newName);

            /// <summary>Create a new empty object of the given type and host name. Returns Success/Error envelope JSON.</summary>
            string CreateObject(string type, string newName);

            /// <summary>Clone a single part's content from source → target. Returns Success/Error envelope JSON.</summary>
            string ClonePart(string sourceName, string newName, string partName, string typeFilter);

            /// <summary>Remove an incomplete target after a clone failure. Returns Success/Error envelope JSON.</summary>
            string DeleteTarget(string newName, string typeFilter);

            /// <summary>
            /// Return the WorkWithPlus instance linked to the source, or null when none. The instance is an object of
            /// its own, not a part of the host (issue #800). Throws when the lookup cannot tell.
            /// </summary>
            PatternInstanceDescriptor FindWwpInstance(string sourceName, string typeFilter);

            /// <summary>
            /// Apply the WorkWithPlus pattern to the new host with default settings. Returns Success/Error envelope JSON.
            /// SaveAs does not call it: an instance with default settings is not a copy of the source's (issue #800).
            /// </summary>
            string ApplyWwpPattern(string newName, PatternInstanceDescriptor sourceInstance);
        }

        /// <summary>
        /// Optional seam: a cloner that also carries the object-level properties the parts do not (a Data Provider's
        /// OutputSDT). The answer is an Ok envelope whose <c>result.properties</c> names what was carried, an Ok
        /// <c>Skipped</c> when there was nothing to carry, or an error, which rolls the clone back.
        /// </summary>
        public interface IObjectPropertyCloner
        {
            string CloneObjectProperties(string sourceName, string newName, string typeFilter);
        }

        public sealed class SourceDescriptor
        {
            public string Name;
            public string Type;
            public IList<string> Parts;
        }

        public sealed class PatternInstanceDescriptor
        {
            public string PatternKey;   // e.g. "WorkWithPlus"
            public string HostName;     // source host name
            public string InstanceName; // the instance object linked to the host, e.g. "WorkWithPlus<Host>"
        }

        private readonly IObjectCloner _cloner;

        public SaveAsService(IObjectCloner cloner)
        {
            if (cloner == null) throw new ArgumentNullException("cloner");
            _cloner = cloner;
        }

        public string SaveAs(JObject args)
        {
            string sourceName = args?["name"]?.ToString();
            string newName = args?["newName"]?.ToString();
            string typeFilter = args?["type"]?.ToString();
            bool includePattern = args?["includePatternInstance"]?.ToObject<bool?>() ?? false;
            bool overwrite = args?["overwrite"]?.ToObject<bool?>() ?? false;
            bool dryRun = args?["dryRun"]?.ToObject<bool?>() ?? false;

            if (string.IsNullOrWhiteSpace(sourceName))
                return Err("usage_error", "'name' is required (source object).", null);
            if (string.IsNullOrWhiteSpace(newName))
                return Err("usage_error", "'newName' is required (target object).", null);
            if (string.Equals(sourceName, newName, StringComparison.OrdinalIgnoreCase))
                return Err("usage_error", "newName must differ from source name.", null);

            var src = _cloner.FindSource(sourceName, typeFilter);
            if (src == null)
            {
                return McpResponse.Err(
                    code: "NotFound",
                    message: "Source object not found: " + sourceName,
                    hint: "Check the object name and type filter, or use genexus_list_objects to enumerate available objects.",
                    nextSteps: new JArray {
                        McpResponse.NextStep("genexus_list_objects", null, "Enumerate available objects to verify the source name.")
                    },
                    extra: new JObject { ["sourceName"] = sourceName });
            }

            if (_cloner.TargetExists(newName))
            {
                // v1: refuse even with overwrite=true — deleting an existing
                // object is destructive enough that we want the agent to call
                // genexus_delete_object explicitly. overwrite=true currently
                // just surfaces a clearer hint.
                string hint = overwrite
                    ? "overwrite=true is reserved for a future revision. For now, delete the existing object first via genexus_delete_object name=" + newName + " confirm=true, then re-run genexus_save_as."
                    : "Pick a different newName, or delete the existing object via genexus_delete_object name=" + newName + " confirm=true.";
                return McpResponse.Err(
                    code: "TargetExists",
                    message: "An object named '" + newName + "' already exists.",
                    hint: hint,
                    nextSteps: new JArray {
                        McpResponse.NextStep("genexus_delete_object", new JObject { ["name"] = newName, ["confirm"] = true }, "Delete the existing target before cloning."),
                        McpResponse.NextStep("genexus_save_as", new JObject { ["name"] = sourceName, ["newName"] = "<differentName>" }, "Use a different target name.")
                    },
                    extra: new JObject { ["sourceName"] = sourceName, ["newName"] = newName });
            }

            // #800: the WorkWithPlus instance is an object of its own, linked to the host, and save_as does not clone it
            // (an instance made with default settings is not a copy of the source's). It is looked up before anything is
            // created: asked to include it, the call is refused here, the dry run too. Without the flag the answer says
            // that the clone did not get the instance, or that the lookup could not tell: a failed lookup never reads
            // as "no instance".
            PatternInstanceDescriptor sourceInstance = null;
            string instanceLookupFailure = null;
            try { sourceInstance = _cloner.FindWwpInstance(sourceName, typeFilter); }
            catch (Exception ex) { instanceLookupFailure = OneLine(ex.Message, ex.GetType().Name); }
            if (includePattern && instanceLookupFailure != null)
                return PatternInstanceLookupFailed(sourceName, newName, instanceLookupFailure);
            if (includePattern && sourceInstance != null)
                return PatternInstanceNotCloneable(sourceName, newName, sourceInstance);
            JObject patternNotice = sourceInstance != null
                ? PatternInstanceNotice(sourceName, newName, sourceInstance)
                : instanceLookupFailure != null ? PatternInstanceLookupNotice(sourceName, instanceLookupFailure) : null;

            // ---- dryRun: build the plan, never touch the SDK. ----
            if (dryRun)
            {
                var plan = new JObject
                {
                    ["createType"] = src.Type,
                    ["newName"] = newName,
                    ["partsToClone"] = new JArray(src.Parts ?? new List<string>()),
                    ["includePatternInstance"] = includePattern
                };
                if (patternNotice != null) plan["patternInstance"] = patternNotice;
                return McpResponse.Ok(
                    target: sourceName,
                    code: "DryRun",
                    result: new JObject
                    {
                        ["sourceName"] = sourceName,
                        ["plan"] = plan
                    });
            }

            // ---- Live path. Track progress so a partial failure surfaces a
            //      clear "where it stopped" + undo hint envelope. ----
            var completedSteps = new JArray();
            var partsCloned = new JArray();
            var partsSkipped = new JArray();

            // Step 1: create empty target object of the same type.
            string createResult = _cloner.CreateObject(src.Type, newName);
            if (!IsSuccess(createResult))
            {
                return Partial(sourceName, newName, completedSteps, "create:" + newName, createResult);
            }
            completedSteps.Add("create:" + newName);

            // Step 2: clone each part. Explicitly unsupported/empty parts are skipped (issue #45),
            // but a real write failure means the target is not a functional clone. Roll it back
            // immediately instead of returning SavedAs over a partial object (issue #118).
            JObject variableIdRemap = null;
            string deferredWebForm = null;
            foreach (var part in (src.Parts ?? new List<string>()))
            {
                string r = _cloner.ClonePart(sourceName, newName, part, typeFilter);
                if (IsSkipped(r))
                {
                    partsSkipped.Add(new JObject { ["part"] = part, ["detail"] = SafeParseOrString(r) });
                    continue;
                }
                if (!IsSuccess(r))
                {
                    // #746: the WebForm validator can refuse a form while the parts it depends on are missing from the
                    // target. Retry it once after them instead of failing at once.
                    if (string.Equals(part, "WebForm", StringComparison.OrdinalIgnoreCase) && deferredWebForm == null)
                    {
                        deferredWebForm = part;
                        continue;
                    }
                    return RollbackFailedClone(sourceName, newName, src.Type, completedSteps,
                        partsCloned, partsSkipped, part, r);
                }
                completedSteps.Add("clonePart:" + part);
                partsCloned.Add(part);
                if (string.Equals(part, "WebForm", StringComparison.OrdinalIgnoreCase)) variableIdRemap = VariableIdRemapOf(r);
            }

            // An object with no cloned part is not a clone. Remove the empty target too.
            if (partsCloned.Count == 0 && deferredWebForm == null)
            {
                return RollbackFailedClone(sourceName, newName, src.Type, completedSteps,
                    partsCloned, partsSkipped, "<all>", "No cloneable parts on source.");
            }

            // Step 2b: carry the object-level properties the parts do not hold (a Data Provider's OutputSDT).
            JArray propertiesCloned;
            string propertiesFailure = CarryObjectProperties(sourceName, newName, typeFilter, src.Type,
                completedSteps, partsCloned, partsSkipped, out propertiesCloned);
            if (propertiesFailure != null) return propertiesFailure;

            // Step 3: the instance is not cloned (see the lookup above); the answer says so.
            JObject patternBlock = patternNotice;

            if (deferredWebForm != null)
            {
                string retried = _cloner.ClonePart(sourceName, newName, deferredWebForm, typeFilter);
                if (IsSkipped(retried))
                {
                    partsSkipped.Add(new JObject { ["part"] = deferredWebForm, ["detail"] = SafeParseOrString(retried) });
                }
                else if (!IsSuccess(retried))
                {
                    return RollbackFailedClone(sourceName, newName, src.Type, completedSteps,
                        partsCloned, partsSkipped, deferredWebForm, retried);
                }
                else
                {
                    completedSteps.Add("clonePart:" + deferredWebForm);
                    partsCloned.Add(deferredWebForm);
                    variableIdRemap = VariableIdRemapOf(retried);
                }
            }

            var payload = new JObject
            {
                ["sourceName"] = sourceName,
                ["created"] = new JObject
                {
                    ["name"] = newName,
                    ["type"] = src.Type,
                    ["partsCloned"] = partsCloned
                }
            };
            AttachOptionalResults(payload, partsSkipped, propertiesCloned, patternBlock, variableIdRemap);
            return McpResponse.Ok(target: newName, code: "SavedAs", result: payload);
        }

        /// <summary>
        /// The rebinding report that the WebForm write carried, or null when it carried none (no control changed its
        /// variable id, or the answer is not JSON).
        /// </summary>
        private static JObject VariableIdRemapOf(string writeAnswer)
        {
            try { return JObject.Parse(writeAnswer)["variableIdRemap"] as JObject; }
            catch { return null; }
        }

        private string RollbackFailedClone(string sourceName, string newName, string type,
            JArray completedSteps, JArray partsCloned, JArray partsSkipped, string failedPart, string innerResult)
        {
            string cleanupResult;
            try { cleanupResult = _cloner.DeleteTarget(newName, type); }
            catch (Exception ex) { cleanupResult = ex.Message; }
            bool removed = IsSuccess(cleanupResult);

            var nextSteps = new JArray();
            if (!removed)
            {
                nextSteps.Add(McpResponse.NextStep("genexus_delete_object",
                    new JObject { ["name"] = newName, ["type"] = type, ["confirm"] = true },
                    "Remove the incomplete clone before retrying."));
            }

            // The reason the part was refused is the only thing that tells the caller what to fix. It used to
            // travel only under `extra`, the envelope's top level, which the client-facing error shape drops
            // (only the fields inside `error` are kept), so the answer said which part failed and nothing about
            // why (#702). Keep the full detail where it was and put the reason inside `error` and in the message.
            // The default client-facing error keeps only the first line of a message and the SDK lists one validation
            // error per line, so the message carries them joined on one line; `error.cause` keeps the SDK's own lines.
            string causeCode, cause;
            SummarizeFailure(innerResult, out causeCode, out cause);
            string reason = string.IsNullOrEmpty(cause) ? "." : ": " + JoinLines(cause).TrimEnd('.', ' ') + ".";

            var errorExtra = new JObject
            {
                ["sourceName"] = sourceName,
                ["newName"] = newName,
                ["failedPart"] = failedPart,
                ["completedSteps"] = completedSteps,
                ["partsCloned"] = partsCloned,
                ["partsSkipped"] = partsSkipped,
                ["cleanup"] = new JObject
                {
                    ["attempted"] = true,
                    ["removed"] = removed,
                    ["detail"] = SafeParseOrString(cleanupResult)
                }
            };
            if (!string.IsNullOrEmpty(cause)) errorExtra["cause"] = cause;
            if (!string.IsNullOrEmpty(causeCode)) errorExtra["causeCode"] = causeCode;

            return McpResponse.Err(
                code: removed ? "SaveAsPartFailed" : "PartialFailure",
                message: removed
                    ? "Save-as failed while cloning part '" + failedPart + "'" + reason + " The incomplete target was removed."
                    : "Save-as failed while cloning part '" + failedPart + "'" + reason + " Automatic cleanup also failed.",
                hint: removed
                    ? "Fix the source-part validation error and retry; no partial target remains."
                    : "Delete the incomplete target before retrying.",
                nextSteps: nextSteps,
                target: newName,
                extra: new JObject
                {
                    ["sourceName"] = sourceName,
                    ["newName"] = newName,
                    ["completedSteps"] = completedSteps,
                    ["partsCloned"] = partsCloned,
                    ["partsSkipped"] = partsSkipped,
                    ["failedPart"] = failedPart,
                    ["detail"] = SafeParseOrString(innerResult),
                    ["cleanup"] = new JObject
                    {
                        ["attempted"] = true,
                        ["removed"] = removed,
                        ["detail"] = SafeParseOrString(cleanupResult)
                    }
                },
                errorExtra: errorExtra);
        }

        /// <summary>
        /// The code and the message of a failed part write, from the envelope the writer answered with (an
        /// <c>error</c> object, a bare <c>error</c> string, or a top-level <c>message</c>). The message is the SDK's
        /// own validation text when the SDK refused the part. Bounded, so one long diagnostic cannot flood the answer.
        /// </summary>
        internal static void SummarizeFailure(string innerResult, out string code, out string message)
        {
            code = null;
            message = null;
            if (string.IsNullOrWhiteSpace(innerResult)) return;
            try
            {
                var inner = JObject.Parse(innerResult);
                var error = inner["error"];
                if (error is JObject errorObject)
                {
                    code = errorObject["code"]?.ToString();
                    message = errorObject["message"]?.ToString();
                }
                else if (error != null && error.Type != JTokenType.Null)
                {
                    message = error.ToString();
                }
                if (string.IsNullOrWhiteSpace(code)) code = inner["code"]?.ToString();
                if (string.IsNullOrWhiteSpace(message)) message = inner["message"]?.ToString();
                // The WebForm writer's message only says that the validator refused the form; the
                // errors themselves travel in its top-level validationDiagnostics list (#746).
                string diagnostics = ValidationErrorLines(inner["validationDiagnostics"] as JArray, message);
                if (diagnostics != null)
                    message = string.IsNullOrWhiteSpace(message) ? diagnostics : message.TrimEnd() + "\n" + diagnostics;
            }
            catch
            {
                message = innerResult;
            }
            if (message != null)
            {
                message = message.Trim();
                if (message.Length > 1200) message = message.Substring(0, 1200) + "...";
            }
        }

        // The errors the message does not already state: the validator's refusal now quotes its first errors itself
        // (issue #830), and repeating them would spend the bounded answer on the same text twice.
        internal static string ValidationErrorLines(JArray diagnostics, string stated = null)
        {
            if (diagnostics == null) return null;
            // The refusal's summary squeezes the white space of what it quotes onto one line, so compare squeezed text.
            string squeezedStated = string.IsNullOrEmpty(stated) ? null : SqueezeWhiteSpace(stated);
            var lines = diagnostics.OfType<JObject>()
                .Where(d => GxMcp.Worker.Helpers.WebFormPreSaveValidator.IsErrorSeverity(d["severity"]?.ToString()))
                .Select(d => new { Text = d["message"]?.ToString()?.Trim(), Source = d["source"]?.ToString()?.Trim() })
                .Where(d => !string.IsNullOrEmpty(d.Text)
                    && (squeezedStated == null || squeezedStated.IndexOf(SqueezeWhiteSpace(d.Text), StringComparison.Ordinal) < 0))
                .Select(d => string.IsNullOrEmpty(d.Source) ? d.Text : d.Source + ": " + d.Text)
                .ToList();
            return lines.Count == 0 ? null : string.Join("\n", lines);
        }

        private static string SqueezeWhiteSpace(string text) => System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();

        private static string JoinLines(string text)
        {
            return string.Join(" | ", text
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0));
        }

        private static string Partial(string sourceName, string newName, JArray completedSteps, string failedStep, string innerResult)
        {
            return McpResponse.Err(
                code: "PartialFailure",
                message: "Save-as failed at step '" + failedStep + "'; " + completedSteps.Count + " step(s) already completed.",
                hint: "Use genexus_delete_object name=" + newName + " confirm=true to remove the half-cloned target, then retry.",
                nextSteps: new JArray {
                    McpResponse.NextStep("genexus_delete_object", new JObject { ["name"] = newName, ["confirm"] = true }, "Remove the partially cloned object before retrying."),
                    McpResponse.NextStep("genexus_save_as", new JObject { ["name"] = sourceName, ["newName"] = newName }, "Retry the clone after cleanup.")
                },
                extra: new JObject
                {
                    ["sourceName"] = sourceName,
                    ["newName"] = newName,
                    ["completedSteps"] = completedSteps,
                    ["failedStep"] = failedStep,
                    ["detail"] = SafeParseOrString(innerResult)
                });
        }

        private static string Err(string code, string message, string sourceName)
        {
            return McpResponse.Err(
                code: code,
                message: message,
                nextSteps: new JArray {
                    McpResponse.NextStep("genexus_save_as", new JObject { ["name"] = sourceName ?? "<name>", ["newName"] = "<newName>" }, "Retry with valid arguments.")
                },
                extra: sourceName != null ? new JObject { ["sourceName"] = sourceName } : null);
        }

        // "the WorkWithPlus instance 'WorkWithPlusCustomer'", or "a WorkWithPlus instance" when the lookup gave no name.
        private static string InstanceDescription(PatternInstanceDescriptor instance)
        {
            string pattern = string.IsNullOrWhiteSpace(instance?.PatternKey) ? "WorkWithPlus" : instance.PatternKey;
            return string.IsNullOrWhiteSpace(instance?.InstanceName)
                ? "a " + pattern + " instance"
                : "the " + pattern + " instance '" + instance.InstanceName + "'";
        }

        // includePatternInstance=true on a source that has an instance: nothing is created, and the answer says how to go on.
        private static string PatternInstanceNotCloneable(string sourceName, string newName, PatternInstanceDescriptor instance)
        {
            string pattern = string.IsNullOrWhiteSpace(instance?.PatternKey) ? "WorkWithPlus" : instance.PatternKey;
            bool named = !string.IsNullOrWhiteSpace(instance?.InstanceName);
            var nextSteps = new JArray
            {
                McpResponse.NextStep("genexus_create",
                    new JObject { ["action"] = "save_as", ["name"] = sourceName, ["newName"] = newName, ["includePatternInstance"] = false },
                    "Clone the parts without the instance.")
            };
            if (named)
            {
                nextSteps.Add(McpResponse.NextStep("genexus_read",
                    new JObject { ["name"] = instance.InstanceName, ["part"] = "PatternInstance" },
                    "Read the settings of the source instance."));
            }
            return McpResponse.Err(
                code: "PatternInstanceNotCloneable",
                message: "'" + sourceName + "' has " + InstanceDescription(instance) + ", which save_as cannot clone yet; nothing was created.",
                hint: "Call save_as without includePatternInstance to clone the parts only (the answer names the instance that was not cloned). "
                    + "genexus_apply_pattern name=" + newName + " pattern=" + pattern + " then makes a new instance with default settings, not a copy of the source's, "
                    + "and projects it onto the object, which can regenerate the cloned form."
                    + (named ? " The source settings are readable with genexus_read name=" + instance.InstanceName + " part=PatternInstance." : string.Empty),
                nextSteps: nextSteps,
                extra: new JObject
                {
                    ["sourceName"] = sourceName,
                    ["newName"] = newName,
                    ["patternInstance"] = new JObject
                    {
                        ["pattern"] = pattern,
                        ["sourceInstance"] = named ? (JToken)instance.InstanceName : JValue.CreateNull()
                    }
                });
        }

        // The lookup of the source instance failed: with includePatternInstance=true that is not an answer of "none".
        private static string PatternInstanceLookupFailed(string sourceName, string newName, string reason)
        {
            return McpResponse.Err(
                code: "PatternInstanceLookupFailed",
                message: "Could not check whether '" + sourceName + "' has a WorkWithPlus instance (" + reason + "); nothing was created.",
                hint: "Retry the call, or leave includePatternInstance out to clone the parts without checking for an instance.",
                nextSteps: new JArray {
                    McpResponse.NextStep("genexus_create", new JObject { ["action"] = "save_as", ["name"] = sourceName, ["newName"] = newName }, "Clone the parts without checking for an instance.")
                },
                extra: new JObject { ["sourceName"] = sourceName, ["newName"] = newName });
        }

        // What an answer tells about a source that has an instance the clone does not get.
        private static JObject PatternInstanceNotice(string sourceName, string newName, PatternInstanceDescriptor instance)
        {
            string pattern = string.IsNullOrWhiteSpace(instance?.PatternKey) ? "WorkWithPlus" : instance.PatternKey;
            var notice = new JObject { ["cloned"] = false, ["pattern"] = pattern };
            if (!string.IsNullOrWhiteSpace(instance?.InstanceName)) notice["sourceInstance"] = instance.InstanceName;
            notice["hint"] = "'" + sourceName + "' has " + InstanceDescription(instance) + " that save_as does not clone: '" + newName
                + "' gets the parts but no instance. genexus_apply_pattern name=" + newName + " pattern=" + pattern
                + " makes a new instance with default settings, not a copy of the source's, and projects it onto the object, "
                + "which can regenerate the cloned form; includePatternInstance=true is refused for this source.";
            return notice;
        }

        // Without the flag, a lookup that could not tell: the answer says so instead of looking like "no instance".
        private static JObject PatternInstanceLookupNotice(string sourceName, string reason)
        {
            return new JObject
            {
                ["cloned"] = false,
                ["checked"] = false,
                ["lookupError"] = reason,
                ["hint"] = "Could not check whether '" + sourceName + "' has a WorkWithPlus instance (" + reason + "). "
                    + "save_as does not clone an instance: if it has one, the new object has the parts but no instance."
            };
        }

        // One bounded line: the client-facing error keeps only the first line of a message (#702).
        private static string OneLine(string text, string fallback)
        {
            string line = string.IsNullOrWhiteSpace(text) ? string.Empty : JoinLines(text);
            if (line.Length == 0) return fallback;
            return line.Length > 300 ? line.Substring(0, 300) + "..." : line;
        }

        private static bool IsSuccess(string envelope)
        {
            if (string.IsNullOrEmpty(envelope)) return false;
            try
            {
                var j = JObject.Parse(envelope);
                string status = j["status"]?.ToString();
                // Accept both v2.8.0 canonical "ok" and legacy "Success".
                if (string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(status, "Success", StringComparison.OrdinalIgnoreCase)) return true;
                // Some services return no status on success and only set "error" on failure.
                return j["error"] == null && j["code"] == null;
            }
            catch { return false; }
        }

        /// <summary>
        /// Carries the object-level properties through the cloner when it supports them. Returns the rolled-back
        /// answer when the clone cannot take them, or null when it can (<paramref name="carried"/> names what was
        /// carried, if anything).
        /// </summary>
        private string CarryObjectProperties(string sourceName, string newName, string typeFilter, string type,
            JArray completedSteps, JArray partsCloned, JArray partsSkipped, out JArray carried)
        {
            carried = null;
            var propertyCloner = _cloner as IObjectPropertyCloner;
            if (propertyCloner == null) return null;

            string answer = propertyCloner.CloneObjectProperties(sourceName, newName, typeFilter);
            if (!IsSuccess(answer))
                return RollbackFailedClone(sourceName, newName, type, completedSteps, partsCloned, partsSkipped, "ObjectProperties", answer);
            if (IsSkipped(answer)) return null;

            carried = JObject.Parse(answer)["result"]?["properties"] as JArray;
            completedSteps.Add("cloneProperties");
            return null;
        }

        private static void AttachOptionalResults(JObject payload, JArray partsSkipped, JArray propertiesCloned,
            JObject patternBlock, JObject variableIdRemap)
        {
            var created = (JObject)payload["created"];
            if (partsSkipped.Count > 0) created["partsSkipped"] = partsSkipped;
            if (propertiesCloned != null && propertiesCloned.Count > 0) created["propertiesCloned"] = propertiesCloned;
            if (patternBlock != null) payload["patternInstance"] = patternBlock;
            if (variableIdRemap != null) payload["variableIdRemap"] = variableIdRemap;
        }

        private static bool IsSkipped(string envelope)
        {
            if (!IsSuccess(envelope)) return false;
            try
            {
                var j = JObject.Parse(envelope);
                return string.Equals(j["code"]?.ToString(), "Skipped", StringComparison.OrdinalIgnoreCase)
                    || (j["result"]?["skipped"]?.ToObject<bool?>() ?? false);
            }
            catch { return false; }
        }

        private static JToken SafeParseOrString(string s)
        {
            if (s == null) return JValue.CreateNull();
            try { return JToken.Parse(s); } catch { return new JValue(s); }
        }
    }

    /// <summary>
    /// Production cloner — wires <see cref="SaveAsService.IObjectCloner"/> to
    /// the existing worker services so every IDE-compat fix already living in
    /// ObjectService/WriteService applies on the cloned target too.
    ///
    /// Note: clone-via-source-text is the safe, type-agnostic strategy that
    /// works for every object type the SDK exposes a textual Source for
    /// (Transaction, Procedure, WebPanel, SDPanel, SDT, DataProvider, Domain,
    /// Dashboard, Theme, MasterPage). Binary-only parts (assets, theme
    /// images) are not cloned — same limitation as genexus_export_unified.
    /// </summary>
    public sealed class SdkObjectCloner : SaveAsService.IObjectCloner, SaveAsService.IObjectPropertyCloner
    {
        private readonly ObjectService _objects;
        private readonly WriteService _writes;
        private readonly PatternApplyService _patterns;
        private readonly PropertyService _properties;

        public SdkObjectCloner(ObjectService objects, WriteService writes, PatternApplyService patterns)
            : this(objects, writes, patterns, null)
        {
        }

        public SdkObjectCloner(ObjectService objects, WriteService writes, PatternApplyService patterns, PropertyService properties)
        {
            _objects = objects;
            _writes = writes;
            _patterns = patterns;
            _properties = properties;
        }

        /// <summary>
        /// A Data Provider's OutputSDT lives on the object, not in a part, so a clone made part by part lost it and
        /// no longer produced its collection. It is read from the source and set on the clone through the typed
        /// output API. Other types, and a source without an output, have nothing to carry.
        /// </summary>
        public string CloneObjectProperties(string sourceName, string newName, string typeFilter)
        {
            string skipped = McpResponse.Ok(code: "Skipped", result: new JObject { ["skipped"] = true });
            if (_properties == null) return skipped;
            var source = _objects.FindObject(sourceName, typeFilter);
            if (source == null || !string.Equals(source.TypeDescriptor?.Name, "DataProvider", StringComparison.OrdinalIgnoreCase))
                return skipped;

            string output = _properties.ReadDataProviderOutputSdt(source);
            if (string.IsNullOrWhiteSpace(output)) return skipped;

            string set = _properties.SetProperty(newName, "OutputSDT", output, null, "DataProvider");
            JObject setAnswer;
            try { setAnswer = JObject.Parse(set); } catch { return set; }
            if (!string.Equals(setAnswer["status"]?.ToString(), "ok", StringComparison.OrdinalIgnoreCase)) return set;
            return McpResponse.Ok(code: "PropertiesCloned", result: new JObject { ["properties"] = new JArray("OutputSDT") });
        }

        public SaveAsService.SourceDescriptor FindSource(string name, string typeFilter)
        {
            var obj = _objects.FindObject(name, typeFilter);
            if (obj == null) return null;
            string[] parts;
            try { parts = GxMcp.Worker.Structure.PartAccessor.GetAvailableParts(obj); }
            catch { parts = new[] { "Source" }; }
            return new SaveAsService.SourceDescriptor
            {
                Name = obj.Name,
                Type = obj.TypeDescriptor?.Name ?? typeFilter ?? "Unknown",
                Parts = OrderPartsForClone(parts)
            };
        }

        /// <summary>
        /// The order the parts are cloned in, which is also the order of the dry-run plan. The parts come back
        /// alphabetically, so a WebPanel cloned Events before Variables and WebForm. Each part is written through
        /// the same validated path as a normal edit, and the SDK refuses code that names a control or a variable
        /// the empty target does not have yet (<c>src0265: invalid attribute 'Ctrl'</c> on the first event that
        /// uses one), so every clone of such a panel failed at Events and was rolled back (#702). Declarations come
        /// first, then the layout, then the code that uses both; any other part keeps its alphabetical place at the
        /// end. The order within a rank is the order the parts arrived in.
        /// </summary>
        internal static string[] OrderPartsForClone(IEnumerable<string> parts)
        {
            return (parts ?? Enumerable.Empty<string>())
                .Select((name, index) => new { name, index })
                .OrderBy(part => ClonePartRank(part.name))
                .ThenBy(part => part.index)
                .Select(part => part.name)
                .ToArray();
        }

        private static int ClonePartRank(string part)
        {
            switch ((part ?? string.Empty).Trim().ToLowerInvariant())
            {
                // What the object is made of: other parts name its members.
                case "structure":
                case "sdtstructure":
                case "dataselectorstructure":
                    return 0;
                case "variables":
                    return 1;
                // The layout: events and rules name its controls.
                case "webform":
                case "layout":
                case "sdlayout":
                    return 2;
                case "rules":
                case "conditions":
                case "sdrules":
                    return 3;
                // The code that uses everything above.
                case "events":
                case "sdevents":
                case "source":
                    return 4;
                default:
                    return 5;
            }
        }

        public bool TargetExists(string newName)
        {
            try { return _objects.FindObject(newName, null) != null; }
            catch { return false; }
        }

        public string CreateObject(string type, string newName)
        {
            return _objects.CreateObject(type, newName);
        }

        public string ClonePart(string sourceName, string newName, string partName, string typeFilter)
        {
            // issue #51: the SDT structure part cannot be cloned through the textual DSL without
            // dropping the root Collection flag, the collection item name, and Domain/SDT-typed
            // members. Clone it natively (SerializeToXml -> DeserializeFromXml) instead. Returns
            // null when not applicable (non-SDT, or the native round-trip didn't stick), in which
            // case we fall through to the text path below.
            bool isStructureAlias = partName != null &&
                (partName.Equals("SDTStructure", StringComparison.OrdinalIgnoreCase) ||
                 partName.Equals("DataSelectorStructure", StringComparison.OrdinalIgnoreCase) ||
                 partName.Equals("Structure", StringComparison.OrdinalIgnoreCase));
            if (isStructureAlias)
            {
                string native = _objects.CloneSdtStructurePart(sourceName, newName);
                if (native != null) return native;

                string dsNative = _objects.CloneDataSelectorStructurePart(sourceName, newName);
                if (dsNative != null) return dsNative;
            }

            // issue #406: a report Procedure's Layout is cloned natively, not through the WebForm write path.
            if (string.Equals(partName, "Layout", StringComparison.OrdinalIgnoreCase))
            {
                string layout = _objects.CloneReportLayoutPart(sourceName, newName, typeFilter);
                if (layout != null) return layout;
            }

            // Read source-part as text, write to new object via the same
            // WriteService pipeline a normal genexus_edit goes through. The read is explicitly a full one
            // (limit 0): an "mcp" read with no limit pages at ~200 lines / 16 KB, which cut every larger part
            // short here, so the target got a truncated source or, more often, the SDK refused it (#702).
            string readJson = _objects.ReadObjectSource(sourceName, partName, null, 0, "mcp", false, typeFilter);
            JObject readObj;
            try { readObj = JObject.Parse(readJson); }
            catch { return readJson; }

            var srcToken = readObj["source"];
            if (srcToken == null)
            {
                // Nothing to clone for this part (binary / unsupported) — that's not an error.
                return McpResponse.Ok(code: "Skipped", result: new JObject { ["skipped"] = true, ["part"] = partName });
            }

            // A clone must never write a page of the source as if it were the whole part.
            string truncated = RefuseTruncatedRead(readObj, partName);
            if (truncated != null) return truncated;

            string code = srcToken.ToString();
            if (string.Equals(partName, "WebForm", StringComparison.OrdinalIgnoreCase))
            {
                // The controls of a WebForm are bound to variables by id, and the target has just written its
                // Variables and numbered them in the order it read them. Each binding is rebound to the variable of
                // the same name, from a fresh read of the target, before the WebForm is written (#746).
                return WriteRemappedWebForm(
                    newName,
                    code,
                    VariableIdsOf(_objects.FindObject(sourceName, typeFilter)),
                    VariableIdsOf(_objects.FindObject(newName, null)),
                    text => _writes.WriteObject(newName, partName, text, typeFilter));
            }

            return _writes.WriteObject(newName, partName, code, typeFilter);
        }

        /// <summary>
        /// The WebForm written with each binding rebound by name to the target's variables, or refused when the target
        /// does not declare a variable the WebForm binds: a refused part writes nothing. The variables are read from the
        /// source and from the target as the target holds them after its Variables were written, so the ids are the ones
        /// the WebForm will meet. The answer is the write's answer, with the rebinding report attached when there is one.
        /// </summary>
        internal static string WriteRemappedWebForm(
            string newName,
            string code,
            IEnumerable<GxMcp.Worker.Helpers.SaveAsVariableIdRemap.NamedId> sourceVariables,
            IEnumerable<GxMcp.Worker.Helpers.SaveAsVariableIdRemap.NamedId> targetVariables,
            Func<string, string> write)
        {
            GxMcp.Worker.Helpers.SaveAsVariableIdRemap.Outcome remap =
                GxMcp.Worker.Helpers.SaveAsVariableIdRemap.ForClone(code, sourceVariables, targetVariables);
            if (remap.MissingInTarget != null)
            {
                return McpResponse.Err(
                    code: "WebFormVariableNotInTarget",
                    message: "The variable '" + remap.MissingInTarget + "' of the source WebForm is not declared in the target, " +
                        "so its controls cannot be bound to it.",
                    hint: "The clone of the Variables part did not bring this variable: nothing was written for this part. " +
                        "Try again, or give the object name.",
                    target: newName);
            }

            code = remap.Xml;
            return AttachVariableIdRemap(write(code), remap);
        }

        /// <summary>
        /// The variables of an object with the id each one carries on its primary path, or null when the object was not
        /// found. An id that cannot be read is -1, and a list that cannot be enumerated is one unreadable variable: either
        /// way the remap is skipped. A position in the enumeration is never taken as an id.
        /// </summary>
        private static IEnumerable<GxMcp.Worker.Helpers.SaveAsVariableIdRemap.NamedId> VariableIdsOf(global::Artech.Architecture.Common.Objects.KBObject obj)
        {
            if (obj == null) return null;
            try
            {
                var variables = new List<GxMcp.Worker.Helpers.SaveAsVariableIdRemap.NamedId>();
                foreach (object variable in GxMcp.Worker.Structure.PartAccessor.GetVariableObjects(obj))
                {
                    string name = GxMcp.Worker.Structure.PartAccessor.GetVariableName(variable);
                    variables.Add(new GxMcp.Worker.Helpers.SaveAsVariableIdRemap.NamedId(
                        name, GxMcp.Worker.Helpers.SaveAsVariableIdRemap.PrimaryVariableId(variable)));
                }
                return variables;
            }
            catch
            {
                return new[] { new GxMcp.Worker.Helpers.SaveAsVariableIdRemap.NamedId(string.Empty, -1) };
            }
        }

        /// <summary>
        /// The clone's WebForm write answer, with the rebinding report attached when there is something to report: a
        /// control rebound, a binding kept as unmapped or as another reference, or a remap skipped with its reason. The
        /// answer is returned as it was otherwise, and as it was when it cannot be read.
        /// </summary>
        internal static string AttachVariableIdRemap(string written, GxMcp.Worker.Helpers.SaveAsVariableIdRemap.Outcome remap)
        {
            if (remap == null || !ReportsRebinding(remap)) return written;
            try
            {
                var answer = JObject.Parse(written);
                answer["variableIdRemap"] = ReceiptOf(remap);
                return answer.ToString(Newtonsoft.Json.Formatting.None);
            }
            catch { return written; }
        }

        private static bool ReportsRebinding(GxMcp.Worker.Helpers.SaveAsVariableIdRemap.Outcome remap)
        {
            return remap.Skipped != null || remap.Remapped > 0 || remap.Unmapped.Count > 0 || remap.OtherReferences.Count > 0;
        }

        private static JObject ReceiptOf(GxMcp.Worker.Helpers.SaveAsVariableIdRemap.Outcome remap)
        {
            if (remap.Skipped != null) return new JObject { ["skipped"] = remap.Skipped };

            var receipt = new JObject
            {
                ["remapped"] = remap.Remapped,
                ["unmapped"] = new JArray(remap.Unmapped),
            };
            if (remap.OtherReferences.Count > 0) receipt["otherReferences"] = new JArray(remap.OtherReferences);
            return receipt;
        }

        /// <summary>
        /// The refusal for a source read that came back as a page rather than the whole part, or null when the
        /// read is complete. Writing such a read would give the clone a truncated part.
        /// </summary>
        internal static string RefuseTruncatedRead(JObject readObj, string partName)
        {
            bool truncated = readObj?["truncated"]?.ToObject<bool?>() == true
                || readObj?["isTruncatedByWorker"]?.ToObject<bool?>() == true;
            if (!truncated) return null;
            return McpResponse.Err(
                code: "SourcePartTruncated",
                message: "The source of part '" + partName + "' was read as a partial page (" +
                    (readObj["limit"]?.ToString() ?? "?") + " of " + (readObj["totalLines"]?.ToString() ?? "?") +
                    " lines), so it cannot be cloned faithfully.",
                hint: "Nothing was written for this part. Report the object name and part: a full read of this part did not return the whole source.");
        }

        public string DeleteTarget(string newName, string typeFilter)
        {
            return _objects.DeleteObject(newName, typeFilter, confirm: true);
        }

        public SaveAsService.PatternInstanceDescriptor FindWwpInstance(string sourceName, string typeFilter)
        {
            // The WorkWithPlus instance is an object of its own (WorkWithPlus<Host>), not a part of the host, so a walk
            // of the host's parts never finds it (issue #800). It is resolved the way genexus_wwp resolves it. A
            // lookup that cannot tell throws: SaveAs must not read it as "no instance".
            if (_patterns == null) throw new InvalidOperationException("PatternApplyService is not wired in this worker build.");
            var obj = _objects.FindObject(sourceName, typeFilter);
            if (obj == null) return null;
            var instance = _patterns.FindLinkedWorkWithPlusInstance(obj);
            if (instance == null) return null;
            return new SaveAsService.PatternInstanceDescriptor
            {
                PatternKey = "WorkWithPlus",
                HostName = sourceName,
                InstanceName = instance.Name
            };
        }

        public string ApplyWwpPattern(string newName, SaveAsService.PatternInstanceDescriptor sourceInstance)
        {
            // Direct call to the canonical PatternApplyService.ApplyPattern signature.
            // settings=null means "use defaults": a new instance, not a copy of the source's. SaveAs does not call
            // this since issue #800; it stays as the seam for a clone that carries the instance's settings.
            if (_patterns == null)
            {
                return McpResponse.Err(
                    code: "PatternServiceUnavailable",
                    message: "PatternApplyService is not wired in this worker build.",
                    nextSteps: new JArray { McpResponse.NextStep("genexus_apply_pattern", new JObject { ["name"] = newName }, "Apply the pattern manually after the object is created.") });
            }
            try
            {
                string key = sourceInstance?.PatternKey ?? "WorkWithPlus";
                string result = _patterns.ApplyPattern(newName, key, settings: null);
                return string.IsNullOrEmpty(result) ? McpResponse.Ok(target: newName, code: "PatternApplied") : result;
            }
            catch (Exception ex)
            {
                return McpResponse.Err(
                    code: "PatternApplyFailed",
                    message: ex.Message,
                    nextSteps: new JArray { McpResponse.NextStep("genexus_apply_pattern", new JObject { ["name"] = newName }, "Apply the pattern manually.") });
            }
        }
    }
}
