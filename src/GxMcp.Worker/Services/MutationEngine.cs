using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;

namespace GxMcp.Worker.Services
{
    public enum MutationMode
    {
        Xml,
        Patch,
        SemanticOps,
        JsonPatch,
        BulkWrite,
        AtomicCreate
    }

    public interface ISdkObjectWriter
    {
        string WriteObject(string target, JObject args);
        string ApplySemanticOps(JObject args);
        string ApplyJsonPatch(JObject args);
        string BulkWrite(JObject args);
        string ReadObjectSource(string target, string part);
    }

    public sealed class DefaultSdkObjectWriter : ISdkObjectWriter
    {
        private readonly WriteService _writeService;
        private readonly ObjectService _objectService;

        public DefaultSdkObjectWriter(WriteService writeService, ObjectService objectService = null)
        {
            _writeService = writeService;
            _objectService = objectService;
        }

        public string WriteObject(string target, JObject args)
        {
            return _writeService != null
                ? _writeService.WriteObject(target, args)
                : McpResponse.Err("WriteServiceUnavailable", "WriteService is unavailable.");
        }

        public string ApplySemanticOps(JObject args)
        {
            return _writeService != null
                ? _writeService.ApplySemanticOps(args)
                : McpResponse.Err("WriteServiceUnavailable", "WriteService is unavailable.");
        }

        public string ApplyJsonPatch(JObject args)
        {
            return _writeService != null
                ? _writeService.ApplyJsonPatch(args)
                : McpResponse.Err("WriteServiceUnavailable", "WriteService is unavailable.");
        }

        public string BulkWrite(JObject args)
        {
            return _writeService != null
                ? _writeService.BulkWrite(args)
                : McpResponse.Err("WriteServiceUnavailable", "WriteService is unavailable.");
        }

        public string ReadObjectSource(string target, string part)
        {
            if (_objectService == null) return null;
            try
            {
                string json = _objectService.ReadObjectSource(target, part);
                if (string.IsNullOrEmpty(json)) return null;
                var obj = JObject.Parse(json);
                return obj["source"]?.ToString() ?? obj["content"]?.ToString() ?? obj["xml"]?.ToString();
            }
            catch
            {
                return null;
            }
        }
    }

    public sealed class MutationRequest
    {
        public string Target { get; set; }
        public string Part { get; set; }
        public MutationMode Mode { get; set; } = MutationMode.Xml;
        public string Content { get; set; }
        public string Payload { get; set; }
        public JArray SemanticOps { get; set; }
        public JArray JsonPatch { get; set; }
        public string Find { get; set; }
        public string Replace { get; set; }
        public bool DryRun { get; set; }
        public string ExpectedVersion { get; set; }
        public bool AutoDeclareVariables { get; set; }
        public bool RollbackOnFailure { get; set; } = true;
        public JObject RawArgs { get; set; }
        public JArray Targets { get; set; }
        public Func<string, string, string> CurrentVersionResolver { get; set; }
    }

    public sealed class MutationPlan
    {
        public int TotalObjects { get; set; }
        public JArray Mutations { get; set; } = new JArray();
        public List<string> Warnings { get; set; } = new List<string>();
        public bool IsValid { get; set; } = true;
        public List<string> ValidationErrors { get; set; } = new List<string>();

        public JObject ToJson()
        {
            var obj = new JObject
            {
                ["totalObjects"] = TotalObjects,
                ["mutations"] = Mutations,
                ["isValid"] = IsValid
            };
            if (Warnings != null && Warnings.Count > 0)
                obj["warnings"] = new JArray(Warnings);
            if (ValidationErrors != null && ValidationErrors.Count > 0)
                obj["validationErrors"] = new JArray(ValidationErrors);
            return obj;
        }
    }

    public sealed class MutationResult
    {
        public bool Success { get; set; }
        public string ResponseJson { get; set; }
        public string ErrorCode { get; set; }
        public string ErrorMessage { get; set; }
        public JObject Plan { get; set; }
        public bool RolledBack { get; set; }
        public string RollbackOutcome { get; set; }
        public string DiagnosticDeltaPath { get; set; }

        public static MutationResult FromJson(string json)
        {
            var res = new MutationResult { ResponseJson = json };
            try
            {
                if (!string.IsNullOrWhiteSpace(json))
                {
                    var obj = JObject.Parse(json);
                    string status = obj["status"]?.ToString();
                    res.Success = string.Equals(status, "Success", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase);
                    if (!res.Success)
                    {
                        res.ErrorCode = obj["code"]?.ToString() ?? obj["error"]?.ToString();
                        res.ErrorMessage = obj["message"]?.ToString() ?? obj["details"]?.ToString();
                    }
                    res.Plan = obj["plan"] as JObject;
                }
            }
            catch
            {
                res.Success = false;
            }
            return res;
        }

        public static MutationResult Error(string code, string message, JObject plan = null)
        {
            var extra = plan != null ? new JObject { ["plan"] = plan } : null;

            return new MutationResult
            {
                Success = false,
                ErrorCode = code,
                ErrorMessage = message,
                ResponseJson = McpResponse.Err(code, message, extra: extra)
            };
        }
    }

    public interface IMutationEngine
    {
        MutationResult Execute(MutationRequest request);
        MutationPlan Plan(MutationRequest request);
        string Mutate(string mode, string target, JObject args, string payload = null);
    }

    /// <summary>
    /// Deep Authoritative Mutation Engine for GeneXus KB objects.
    /// Encapsulates pre-flight validation, concurrency guards, in-memory patch execution,
    /// DryRun previews, multi-object unit-of-work staging, and automated LIFO rollback guards.
    /// </summary>
    public sealed class MutationEngine : IMutationEngine
    {
        private readonly ISdkObjectWriter _writer;
        private readonly PatchService _patchService;
        private readonly ObjectService _objectService;

        public MutationEngine()
            : this((ISdkObjectWriter)null, null, null)
        {
        }

        public MutationEngine(ISdkObjectWriter writer, PatchService patchService = null, ObjectService objectService = null)
        {
            _writer = writer;
            _patchService = patchService;
            _objectService = objectService;
        }

        public MutationEngine(WriteService writeService, PatchService patchService, ObjectService objectService)
            : this(new DefaultSdkObjectWriter(writeService, objectService), patchService, objectService)
        {
        }

        public MutationPlan Plan(MutationRequest request)
        {
            var plan = new MutationPlan();
            if (request == null) return plan;

            if (request.Targets != null && request.Targets.Count > 0)
            {
                plan.TotalObjects = request.Targets.Count;
                foreach (JObject item in request.Targets)
                {
                    string t = item["target"]?.ToString() ?? item["name"]?.ToString();
                    string p = item["part"]?.ToString() ?? "Source";
                    string c = item["content"]?.ToString() ?? item["source"]?.ToString();
                    string expected = item["expectedVersion"]?.ToString() ?? item["baseVersion"]?.ToString();
                    var preview = BuildMutationPreview(t, p, c, expected);
                    plan.Mutations.Add(preview);
                    AddVersionValidation(plan, preview, expected);
                }
            }
            else
            {
                plan.TotalObjects = 1;
                var preview = BuildMutationPreview(
                    request.Target,
                    request.Part ?? "Source",
                    request.Content,
                    request.ExpectedVersion);
                plan.Mutations.Add(preview);
                AddVersionValidation(plan, preview, request.ExpectedVersion);
            }

            return plan;
        }

        private static void AddVersionValidation(MutationPlan plan, JObject preview, string expectedVersion)
        {
            if (plan == null || preview == null || string.IsNullOrWhiteSpace(expectedVersion)) return;
            string check = preview["versionCheck"]?.ToString();
            if (string.Equals(check, "match", StringComparison.OrdinalIgnoreCase)) return;

            plan.IsValid = false;
            string target = preview["target"]?.ToString() ?? "target";
            string part = preview["part"]?.ToString() ?? "Source";
            string reason = string.Equals(check, "conflict", StringComparison.OrdinalIgnoreCase)
                ? "version conflict"
                : "current version unavailable";
            plan.ValidationErrors.Add($"{target} ({part}): {reason}; no write is authorized.");
        }

        public MutationResult Execute(MutationRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            // 1. Pre-flight Validation: TextPayloadGuard
            if (!string.IsNullOrEmpty(request.Content))
            {
                string guardedPart = request.Part ?? "Source";
                if (TextPayloadGuard.AppliesToPart(guardedPart))
                {
                    var issue = TextPayloadGuard.Analyze(request.Content);
                    if (issue != null)
                    {
                        return MutationResult.Error(
                            TextPayloadGuard.ErrorCode,
                            $"Detected literal line break escape sequences in {guardedPart}. Pass real line breaks instead of literal \\r\\n."
                        );
                    }
                }
            }

            if (request.Targets != null && request.Targets.Count > 0)
            {
                var versionConflict = ValidateTargetVersions(request.Targets);
                if (versionConflict != null) return versionConflict;

                foreach (JObject item in request.Targets)
                {
                    string targetName = item["target"]?.ToString() ?? item["name"]?.ToString();
                    string itemContent = item["content"]?.ToString() ?? item["source"]?.ToString();
                    string itemPart = item["part"]?.ToString() ?? "Source";
                    if (!string.IsNullOrEmpty(itemContent) && TextPayloadGuard.AppliesToPart(itemPart))
                    {
                        var issue = TextPayloadGuard.Analyze(itemContent);
                        if (issue != null)
                        {
                            return MutationResult.Error(
                                TextPayloadGuard.ErrorCode,
                                $"Detected literal line break escape sequences in {targetName} ({itemPart}). Pass real line breaks instead of literal \\r\\n."
                            );
                        }
                    }
                }
            }

            // 2. Optimistic Concurrency Guard: ExpectedVersion
            if (!string.IsNullOrEmpty(request.ExpectedVersion))
            {
                string currentVersion = request.CurrentVersionResolver != null
                    ? request.CurrentVersionResolver(request.Target, request.Part)
                    : ResolveCurrentVersion(request.Target, request.Part);

                if (string.IsNullOrEmpty(currentVersion))
                {
                    return MutationResult.Error(
                        "ConcurrencyStateUnavailable",
                        $"The current version of {request.Target} ({request.Part ?? "Source"}) could not be read; no write was attempted.");
                }

                if (!string.Equals(currentVersion, request.ExpectedVersion, StringComparison.OrdinalIgnoreCase))
                {
                    return MutationResult.Error(
                        "ConcurrencyConflict",
                        $"Expected version '{request.ExpectedVersion}' does not match current version '{currentVersion}' of {request.Target}."
                    );
                }
            }

            // 3. DryRun Simulation
            if (request.DryRun)
            {
                var plan = Plan(request);
                var previewJson = new JObject
                {
                    ["status"] = "ok",
                    ["_meta"] = new JObject
                    {
                        ["dryRun"] = true,
                        ["schemaVersion"] = "2.0"
                    },
                    ["plan"] = plan.ToJson()
                };
                return new MutationResult
                {
                    Success = true,
                    Plan = plan.ToJson(),
                    ResponseJson = previewJson.ToString(Newtonsoft.Json.Formatting.None)
                };
            }

            // 4. Multi-Object Unit-of-Work Staging & Automated LIFO Rollback
            if (request.Targets != null && request.Targets.Count > 0)
            {
                return ExecuteUnitOfWork(request);
            }

            // 5. Single Object Mutation Dispatch
            var rawArgs = request.RawArgs != null ? (JObject)request.RawArgs.DeepClone() : new JObject();
            if (!string.IsNullOrEmpty(request.Part)) rawArgs["part"] = request.Part;
            if (!string.IsNullOrEmpty(request.Content)) rawArgs["content"] = request.Content;
            if (!string.IsNullOrEmpty(request.ExpectedVersion)) rawArgs["expectedVersion"] = request.ExpectedVersion;
            if (request.AutoDeclareVariables) rawArgs["autoDeclareVariables"] = true;
            if (request.SemanticOps != null) rawArgs["ops"] = request.SemanticOps;
            if (request.JsonPatch != null) rawArgs["patch"] = request.JsonPatch;

            string modeStr = request.Mode.ToString().ToLowerInvariant();
            string jsonResp = MutateInternal(modeStr, request.Target, rawArgs, request.Payload ?? request.Content);
            return MutationResult.FromJson(jsonResp);
        }

        public string Mutate(string mode, string target, JObject args, string payload = null)
        {
            if (args == null) args = new JObject();

            var req = new MutationRequest
            {
                Target = target,
                Part = args["part"]?.ToString(),
                Content = payload ?? args["content"]?.ToString(),
                Payload = payload,
                DryRun = args["dryRun"]?.ToObject<bool?>() ?? false,
                ExpectedVersion = args["expectedVersion"]?.ToString() ?? args["baseVersion"]?.ToString(),
                AutoDeclareVariables = args["autoDeclareVariables"]?.ToObject<bool?>() ?? false,
                RollbackOnFailure = args["rollbackOnFailure"]?.ToObject<bool?>() ?? true,
                RawArgs = args,
                Targets = args["targets"] as JArray,
                SemanticOps = args["ops"] as JArray,
                JsonPatch = args["patch"] as JArray
            };

            if (string.Equals(mode, "patch", StringComparison.OrdinalIgnoreCase))
                req.Mode = MutationMode.Patch;
            else if (string.Equals(mode, "ops", StringComparison.OrdinalIgnoreCase) || string.Equals(mode, "semanticops", StringComparison.OrdinalIgnoreCase))
                req.Mode = MutationMode.SemanticOps;
            else if (string.Equals(mode, "jsonpatch", StringComparison.OrdinalIgnoreCase))
                req.Mode = MutationMode.JsonPatch;
            else if (string.Equals(mode, "bulk", StringComparison.OrdinalIgnoreCase) || string.Equals(mode, "bulkwrite", StringComparison.OrdinalIgnoreCase))
                req.Mode = MutationMode.BulkWrite;
            else
                req.Mode = MutationMode.Xml;

            var result = Execute(req);
            return result.ResponseJson;
        }

        private string MutateInternal(string mode, string target, JObject args, string payload)
        {
            if (string.Equals(mode, "patch", StringComparison.OrdinalIgnoreCase))
            {
                if (_patchService == null)
                    return Models.McpResponse.Err(code: "PatchServiceUnavailable", message: "Patch service is not available.");

                string validateMode = args["validate"]?.ToString();
                bool dryRunArg = args["dryRun"]?.ToObject<bool?>() ?? false;
                bool validateOnly = string.Equals(validateMode, "only", StringComparison.OrdinalIgnoreCase)
                                    || string.Equals(validateMode, "validate-only", StringComparison.OrdinalIgnoreCase);

                return _patchService.ApplyPatch(
                    target,
                    args["part"]?.ToString(),
                    args["operation"]?.ToString(),
                    payload,
                    args["context"]?.ToString(),
                    args["expectedCount"]?.ToObject<int?>() ?? 1,
                    args["type"]?.ToString(),
                    dryRunArg || validateOnly,
                    args["verifyRollback"]?.ToObject<bool?>() ?? false,
                    args["return_post_state"]?.ToObject<bool?>() ?? true,
                    args["verbose"]?.ToObject<bool?>() ?? false,
                    args["replaceAll"]?.ToObject<bool?>() ?? false,
                    args["verifyMode"]?.ToString(),
                    args["baseVersion"]?.ToString(),
                    args["rollbackOnFailure"]?.ToObject<bool?>() ?? false,
                    args["autoDeclareVariables"]?.ToObject<bool?>() ?? args["autoInjectVariables"]?.ToObject<bool?>() ?? false);
            }

            if (string.Equals(mode, "semanticops", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(mode, "ops", StringComparison.OrdinalIgnoreCase))
            {
                return _writer != null
                    ? _writer.ApplySemanticOps(args)
                    : McpResponse.Err("WriterUnavailable", "Writer is unavailable.");
            }

            if (string.Equals(mode, "jsonpatch", StringComparison.OrdinalIgnoreCase))
            {
                return _writer != null
                    ? _writer.ApplyJsonPatch(args)
                    : McpResponse.Err("WriterUnavailable", "Writer is unavailable.");
            }

            if (string.Equals(mode, "bulk", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(mode, "bulkwrite", StringComparison.OrdinalIgnoreCase) ||
                args["objects"] != null)
            {
                return _writer != null
                    ? _writer.BulkWrite(args)
                    : McpResponse.Err("WriterUnavailable", "Writer is unavailable.");
            }

            // Default: full XML write
            return _writer != null
                ? _writer.WriteObject(target, args)
                : McpResponse.Err("WriterUnavailable", "Writer is unavailable.");
        }

        private MutationResult ExecuteUnitOfWork(MutationRequest request)
        {
            var applied = new List<AppliedTargetRecord>();
            bool anyFailed = false;
            string failureError = null;

            foreach (JObject item in request.Targets)
            {
                string target = item["target"]?.ToString() ?? item["name"]?.ToString();
                string part = item["part"]?.ToString() ?? "Source";
                string content = item["content"]?.ToString() ?? item["source"]?.ToString();

                string original = _writer?.ReadObjectSource(target, part);

                var writeArgs = (JObject)item.DeepClone();
                writeArgs["part"] = part;
                writeArgs["content"] = content;

                string resJson = _writer != null ? _writer.WriteObject(target, writeArgs) : null;
                var resObj = !string.IsNullOrEmpty(resJson) ? JObject.Parse(resJson) : null;
                string status = resObj?["status"]?.ToString();
                bool isSuccess = string.Equals(status, "Success", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase);

                if (!isSuccess)
                {
                    anyFailed = true;
                    string innerErr = resObj?["message"]?.ToString() ?? resObj?["error"]?.ToString();
                    failureError = !string.IsNullOrEmpty(innerErr) ? $"Failed writing {target}: {innerErr}" : $"Failed writing {target}";
                    break;
                }

                string persisted = _writer?.ReadObjectSource(target, part);
                applied.Add(new AppliedTargetRecord
                {
                    Target = target,
                    Part = part,
                    OriginalContent = original,
                    RequestedContent = content,
                    Saved = true,
                    Verified = persisted != null && string.Equals(persisted, content, StringComparison.Ordinal)
                });
            }

            if (anyFailed)
            {
                bool rolledBack = false;
                bool rollbackIndeterminate = false;
                bool rollbackFailed = false;
                int rollbackAttempts = 0;
                var rollbackTargets = new JArray();
                if (request.RollbackOnFailure && applied.Count > 0)
                {
                    applied.Reverse();
                    foreach (var record in applied)
                    {
                        rollbackAttempts++;
                        var targetRollback = new JObject
                        {
                            ["target"] = record.Target,
                            ["part"] = record.Part,
                            ["attempted"] = true
                        };
                        try
                        {
                            var rollbackArgs = new JObject
                            {
                                ["part"] = record.Part,
                                ["content"] = record.OriginalContent ?? string.Empty,
                                ["isRollback"] = true
                            };
                            string rollbackJson = _writer?.WriteObject(record.Target, rollbackArgs);
                            if (!IsSuccessResponse(rollbackJson))
                            {
                                rollbackFailed = true;
                                targetRollback["outcome"] = "write_failed";
                                rollbackTargets.Add(targetRollback);
                                continue;
                            }

                            string restored = _writer?.ReadObjectSource(record.Target, record.Part);
                            if (restored == null || !string.Equals(restored, record.OriginalContent, StringComparison.Ordinal))
                            {
                                rollbackIndeterminate = true;
                                targetRollback["outcome"] = "indeterminate";
                                targetRollback["verified"] = false;
                            }
                            else
                            {
                                targetRollback["outcome"] = "confirmed";
                                targetRollback["verified"] = true;
                            }
                        }
                        catch (Exception ex)
                        {
                            rollbackFailed = true;
                            targetRollback["outcome"] = "write_failed";
                            Logger.Error($"[MUTATION-ENGINE] Compensation rollback failure on {record.Target}: {ex.Message}");
                        }
                        rollbackTargets.Add(targetRollback);
                    }
                    rolledBack = rollbackAttempts > 0 && !rollbackFailed && !rollbackIndeterminate;
                }

                string rollbackOutcome = !request.RollbackOnFailure || applied.Count == 0
                    ? "not_attempted"
                    : rolledBack ? "confirmed"
                    : rollbackIndeterminate ? "indeterminate" : "partial";
                var rollback = new JObject
                {
                    ["attempted"] = rollbackAttempts > 0,
                    ["outcome"] = rollbackOutcome,
                    ["confirmed"] = rolledBack,
                    ["targetCount"] = rollbackAttempts,
                    ["targets"] = rollbackTargets
                };

                return new MutationResult
                {
                    Success = false,
                    ErrorCode = "MutationFailed",
                    ErrorMessage = failureError,
                    RolledBack = rolledBack,
                    RollbackOutcome = rollbackOutcome,
                    ResponseJson = McpResponse.Err("MutationFailed", failureError,
                        extra: new JObject { ["rolledBack"] = rolledBack, ["rollback"] = rollback })
                };
            }

            var successResult = new JObject
            {
                ["totalObjects"] = applied.Count,
                ["outcome"] = applied.All(item => item.Verified) ? "confirmed" : "saved_unverified",
                ["targets"] = new JArray(applied.Select(item => new JObject
                {
                    ["target"] = item.Target,
                    ["part"] = item.Part,
                    ["saved"] = item.Saved,
                    ["verified"] = item.Verified
                }))
            };

            return new MutationResult
            {
                Success = true,
                ResponseJson = McpResponse.Ok(result: successResult)
            };
        }

        private string ResolveCurrentVersion(string target, string part)
        {
            if (_writer == null) return null;
            string src = _writer.ReadObjectSource(target, part);
            if (src == null) return null;
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(src));
                return BitConverter.ToString(hash).Replace("-", "").Substring(0, 16);
            }
        }

        private JObject BuildMutationPreview(string target, string part, string desiredContent, string expectedVersion = null)
        {
            var preview = new JObject
            {
                ["target"] = target,
                ["part"] = part,
                ["hasChanges"] = true,
                ["verification"] = "unavailable"
            };

            if (!string.IsNullOrWhiteSpace(expectedVersion))
                preview["expectedVersion"] = expectedVersion;

            if (_writer == null || string.IsNullOrWhiteSpace(target)) return preview;
            string current = null;
            try { current = _writer.ReadObjectSource(target, part); } catch { }
            if (current == null)
            {
                if (!string.IsNullOrWhiteSpace(expectedVersion))
                    preview["versionCheck"] = "unavailable";
                return preview;
            }

            preview["verification"] = "readable";
            preview["currentVersion"] = ComputeVersionToken(current);
            if (!string.IsNullOrWhiteSpace(expectedVersion))
                preview["versionCheck"] = string.Equals(
                    expectedVersion,
                    preview["currentVersion"]?.ToString(),
                    StringComparison.OrdinalIgnoreCase)
                    ? "match"
                    : "conflict";
            if (desiredContent != null)
            {
                preview["requestedVersion"] = ComputeVersionToken(desiredContent);
                preview["hasChanges"] = !string.Equals(current, desiredContent, StringComparison.Ordinal);
                preview["currentLength"] = current.Length;
                preview["requestedLength"] = desiredContent.Length;
            }
            return preview;
        }

        private MutationResult ValidateTargetVersions(JArray targets)
        {
            if (targets == null) return null;
            foreach (JObject item in targets)
            {
                string expected = item["expectedVersion"]?.ToString() ?? item["baseVersion"]?.ToString();
                if (string.IsNullOrWhiteSpace(expected)) continue;

                string target = item["target"]?.ToString() ?? item["name"]?.ToString();
                string part = item["part"]?.ToString() ?? "Source";
                string current = null;
                try { current = _writer.ReadObjectSource(target, part); } catch { }
                string currentVersion = current == null ? null : ComputeVersionToken(current);
                if (_writer == null || string.IsNullOrEmpty(currentVersion))
                {
                    return MutationResult.Error(
                        "ConcurrencyStateUnavailable",
                        $"The current version of {target} ({part}) could not be read; no write was attempted.");
                }

                if (!string.Equals(currentVersion, expected, StringComparison.OrdinalIgnoreCase))
                {
                    return MutationResult.Error(
                        "ConcurrencyConflict",
                        $"Expected version '{expected}' does not match current version '{currentVersion}' of {target}.");
                }
            }
            return null;
        }

        private static bool IsSuccessResponse(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return false;
            try
            {
                var obj = JObject.Parse(json);
                string status = obj["status"]?.ToString();
                return string.Equals(status, "Success", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static string ComputeVersionToken(string content)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(content ?? string.Empty));
                return BitConverter.ToString(hash).Replace("-", string.Empty).Substring(0, 16);
            }
        }

        private sealed class AppliedTargetRecord
        {
            public string Target { get; set; }
            public string Part { get; set; }
            public string OriginalContent { get; set; }
            public string RequestedContent { get; set; }
            public bool Saved { get; set; }
            public bool Verified { get; set; }
        }
    }
}
