using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;

namespace GxMcp.Worker.Services
{
    // Semantic-ops (structured) and JSON-Patch write entry points extracted from
    // WriteService.cs (plan 007). Pure move, no logic changes — see
    // plans/007-decompose-writeservice.md.
    public partial class WriteService
    {
        /// <summary>
        /// Effective <c>verifyMode</c> for a semantic-ops write. An omitted mode on a
        /// Structure part is <c>semantic</c>, so the attribute description the SDK
        /// derives from the attribute name does not fail the comparison (#525, #557);
        /// an explicit value, <c>normalized</c> included, is honoured as sent.
        /// </summary>
        internal static string ResolveOpsVerifyMode(string partName, string requestedVerifyMode)
        {
            if (string.IsNullOrWhiteSpace(requestedVerifyMode)
                && string.Equals(partName, "Structure", StringComparison.OrdinalIgnoreCase))
                return "semantic";
            return requestedVerifyMode;
        }

        public string ApplySemanticOps(JObject req)
        {
            string target = req?["target"]?.ToString();
            string partName = req?["part"]?.ToString();
            if (string.IsNullOrEmpty(partName)) partName = "Structure";
            return WrapWithPersistedState(ApplySemanticOpsImpl(req), target, partName, GxMcp.Worker.Helpers.WriteResultMeta.Ops);
        }

        private string ApplySemanticOpsImpl(JObject req)
        {
            // Validation runs here — no GeneXus types referenced in this method body.
            // GeneXus SDK types are isolated in ApplySemanticOpsCore so JIT can load
            // this method even when GeneXus assemblies are absent (unit-test environment).
            try
            {
                if (req == null)
                    throw new UsageException("usage_error", "request required");

                string target = req["target"]?.ToString();
                string partName = req["part"]?.ToString();
                string typeFilter = req["type"]?.ToString();
                JArray opsRaw = req["ops"] as JArray;
                bool dryRun = req["dryRun"]?.ToObject<bool?>() ?? false;
                bool returnPostState = req["return_post_state"]?.ToObject<bool?>() ?? true;
                bool verbose = req["verbose"]?.ToObject<bool?>() ?? false;
                // v2.6.6 FR#13 — validate mode plumbing. Default "strict" preserves
                // the v2.6.5 abort-on-first-failure semantics so existing callers are
                // unaffected.
                string validate = req["validate"]?.ToString();
                string baseVersion = req["baseVersion"]?.ToString()
                    ?? req["expectedVersion"]?.ToString()
                    ?? req["versionToken"]?.ToString();
                bool rollbackOnFailure = req["rollbackOnFailure"]?.ToObject<bool?>() ?? true;
                string transactionModule = req["transactionModule"]?.ToString();
                string verifyMode = req["verifyMode"]?.ToString();

                if (string.IsNullOrEmpty(target))
                    throw new UsageException("usage_error", "target required");
                if (opsRaw == null || opsRaw.Count == 0)
                    throw new UsageException("usage_error", "ops[] required");
                if (string.IsNullOrEmpty(partName))
                    partName = "Structure";

                verifyMode = ResolveOpsVerifyMode(partName, verifyMode);

                // Pre-flight: reject immediately when no KB is open, before JIT-loading GeneXus types.
                if (!_objectService.GetKbService().IsOpen)
                    throw new UsageException("usage_error", "object '" + target + "' not found");

                return ApplySemanticOpsCore(target, partName, opsRaw, dryRun, returnPostState, verbose,
                    validate, typeFilter, baseVersion, rollbackOnFailure, transactionModule, verifyMode);
            }
            catch (UsageException ux)
            {
                return BuildUsageErrorResponse(ux);
            }
            catch (Exception ex)
            {
                return new JObject
                {
                    ["isError"] = true,
                    ["error"] = new JObject
                    {
                        ["code"] = "internal_error",
                        ["message"] = ex.Message
                    }
                }.ToString(Newtonsoft.Json.Formatting.None);
            }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private string ApplySemanticOpsCore(string target, string partName, JArray opsRaw, bool dryRun,
            bool returnPostState = true, bool verbose = false, string validate = null,
            string typeFilter = null, string baseVersion = null, bool rollbackOnFailure = true,
            string transactionModule = null, string verifyMode = null)
        {
            var obj = _objectService.FindObject(target, typeFilter);
            if (obj == null)
                throw new UsageException("usage_error", "object '" + target + "' not found");

            string kind = obj.TypeDescriptor?.Name ?? "";

            var ops = opsRaw.OfType<JObject>().Select(SemanticOp.From).ToList();

            // issue #34: Transaction Structure attribute ops go through the DSL path, not the
            // XML-ops path. The Structure part does not serialize to a <Structure>-rooted XML
            // document, so the XML handlers failed with "<Structure> not found" on a real KB.
            bool isTrnStructure = kind.Equals("Transaction", StringComparison.OrdinalIgnoreCase)
                && (partName.Equals("Structure", StringComparison.OrdinalIgnoreCase));
            if (isTrnStructure && ops.Count > 0)
            {
                // Native GX18 persistent removal. TransactionLevel.Attributes has no public
                // Remove API in v2.40.1; the StructureService detaches the exact
                // TransactionAttribute from TransactionLevel.Items, snapshots first, re-reads,
                // and verifies that the global Attribute/SubType Group remain untouched.
                if (ops.Count == 1 && string.Equals(ops[0].Op, "remove_attribute", StringComparison.OrdinalIgnoreCase))
                {
                    var removeArgs = (JObject)ops[0].Args.DeepClone();
                    removeArgs["attribute"] = ops[0].Args["name"]?.ToString();
                    removeArgs["dryRun"] = dryRun || string.Equals(validate, "only", StringComparison.OrdinalIgnoreCase);
                    removeArgs["rollbackOnFailure"] = rollbackOnFailure;
                    if (!string.IsNullOrWhiteSpace(baseVersion)) removeArgs["baseVersion"] = baseVersion;
                    if (!string.IsNullOrWhiteSpace(transactionModule)) removeArgs["transactionModule"] = transactionModule;
                    return (_structureService ?? new StructureService(_objectService)).RemoveAttribute(target, removeArgs);
                }

                // B11: a Transaction Structure does NOT serialize to a <Structure>-rooted XML
                // document, so ANY op that reaches the XML path below fails with the cryptic
                // "<Structure> not found". Route attribute ops to the DSL path; if the batch
                // mixes in a non-attribute op, reject it here with an actionable message
                // instead of letting it fall through to that misleading error.
                if (ops.All(o => SemanticOpsService.IsTransactionStructureAttrOp(o.Op)))
                {
                    return ApplyTransactionStructureOpsViaDsl(target, obj, ops, dryRun, returnPostState, verbose, validate, typeFilter, baseVersion, rollbackOnFailure, verifyMode);
                }
                var badOps = ops.Where(o => !SemanticOpsService.IsTransactionStructureAttrOp(o.Op))
                                .Select(o => o.Op).Distinct().ToList();
                throw new UsageException("usage_error",
                    "Transaction Structure ops must all be attribute ops (add_attribute, set_attribute, remove_attribute); unsupported op(s): "
                    + string.Join(", ", badOps)
                    + ". The Structure part is not XML-addressable — split these into a separate patch against the right part, or use the attribute ops only. Op args go under args:{name,type,...} (nested) or at the op top level.");
            }

            var part = GxMcp.Worker.Structure.PartAccessor.GetPart(obj, partName);
            if (part == null)
                throw new UsageException("usage_error",
                    "part '" + partName + "' not found in " + kind);

            string currentXml = part.SerializeToXml();
            if (string.IsNullOrEmpty(currentXml))
                throw new UsageException("usage_error",
                    "part '" + partName + "' produced empty XML");

            // v2.6.6 FR#13 — validate mode dispatch. The legacy Apply() path is
            // preserved when validate is unset (or "strict") AND every op succeeds,
            // so the resulting XML is byte-identical to v2.6.5.
            string mode = SemanticOpsService.NormalizeMode(validate);
            SemanticOpsService.OpsApplyOutcome outcome;
            try
            {
                outcome = new SemanticOpsService().ApplyWithResults(currentXml, kind, ops, mode);
            }
            catch (UsageException) when (mode != "strict")
            {
                outcome = new SemanticOpsService.OpsApplyOutcome
                {
                    Xml = currentXml,
                    Results = new System.Collections.Generic.List<SemanticOpsService.OpResult>(),
                    Aborted = true,
                    Mode = mode
                };
            }
            string newXml = outcome.Xml;
            int okCount = outcome.Results.Count(r => r.Ok);

            // strict + aborted → bubble the original failure for backwards compat.
            if (mode == "strict" && outcome.Aborted)
            {
                var failed = outcome.Results.FirstOrDefault(r => !r.Ok);
                throw new UsageException(failed?.Code ?? "usage_error",
                    failed?.Reason ?? "op failed",
                    failed?.Extra);
            }

            var opResultsJson = new JArray();
            foreach (var r in outcome.Results) opResultsJson.Add(r.ToJson());

            // validate=only → never persist; return diagnostics only.
            if (mode == "only" || dryRun)
            {
                var envelope = DryRunPlanBuilder.BuildEnvelope(target, currentXml, newXml, "ops", _kbValidationService);
                JObject env;
                try { env = JObject.Parse(envelope.ToString()); }
                catch { env = new JObject { ["raw"] = envelope.ToString() }; }
                env["validate"] = mode;
                env["opResults"] = opResultsJson;
                env["opsApplied"] = okCount;
                env["opsTotal"] = ops.Count;
                if (returnPostState)
                    env["post_state"] = JsonPatchService.BuildPostState(currentXml, newXml, verbose);
                return env.ToString(Newtonsoft.Json.Formatting.None);
            }

            string writeResult = WriteObject(target, partName, newXml, typeFilter, false, false, false, false);
            JObject writeJson;
            try { writeJson = JObject.Parse(writeResult); }
            catch { writeJson = new JObject { ["raw"] = writeResult }; }

            // v2.6.6 FR#12 — re-read persisted bytes AFTER the SDK commits so
            // return_post_state slices reflect on-disk reality, not the
            // in-memory write buffer that the v2.6.4 regression captured.
            var writeStatus = writeJson["status"]?.ToString();
            bool writeOk = string.Equals(writeStatus, "Success", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(writeStatus, "Ok", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(writeStatus, "ok", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(writeStatus, "partial", StringComparison.OrdinalIgnoreCase);
            string persistedAfter = writeOk ? ReadPersistedPartSafely(target, partName) : null;

            var resp = new JObject
            {
                ["isError"] = false,
                ["target"] = target,
                ["part"] = partName,
                ["mode"] = "ops",
                ["validate"] = mode,
                ["opsApplied"] = okCount,
                ["opsTotal"] = ops.Count,
                ["opResults"] = opResultsJson,
                ["write"] = writeJson
            };
            if (returnPostState)
                resp["post_state"] = JsonPatchService.BuildPostState(currentXml, newXml, verbose, persistedAfter);
            return resp.ToString(Newtonsoft.Json.Formatting.None);
        }

        // issue #34: apply Transaction Structure attribute ops (add/set/remove_attribute)
        // through the Structure DSL — read the DSL, mutate it, persist via WriteObject which
        // routes Structure writes through the DSL parser + EnsureSave. This is the same code
        // path the working mode=patch Structure edits use, so it disambiguates a homonym
        // Transaction/Table via typeFilter and actually persists (the XML-ops path did not).
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private string ApplyTransactionStructureOpsViaDsl(
            string target, global::Artech.Architecture.Common.Objects.KBObject obj,
            IList<SemanticOp> ops, bool dryRun, bool returnPostState, bool verbose, string validate, string typeFilter,
            string baseVersion = null, bool rollbackOnFailure = true, string verifyMode = null)
        {
            string currentDsl = GxMcp.Worker.Helpers.StructureParser.SerializeToText(obj);

            string mode = SemanticOpsService.NormalizeMode(validate);
            var outcome = new SemanticOpsService().ApplyTransactionStructureDsl(currentDsl, ops, mode);
            string newDsl = outcome.Text;
            int okCount = outcome.Results.Count(r => r.Ok);

            if (mode == "strict" && outcome.Aborted)
            {
                var failed = outcome.Results.FirstOrDefault(r => !r.Ok);
                throw new UsageException(failed?.Code ?? "usage_error", failed?.Reason ?? "op failed", failed?.Extra);
            }

            var opResultsJson = new JArray();
            foreach (var r in outcome.Results) opResultsJson.Add(r.ToJson());

            if (mode == "only" || dryRun)
            {
                var envelope = DryRunPlanBuilder.BuildEnvelope(target, currentDsl, newDsl, "ops", _kbValidationService);
                JObject env;
                try { env = JObject.Parse(envelope.ToString()); }
                catch { env = new JObject { ["raw"] = envelope.ToString() }; }
                env["validate"] = mode;
                env["opResults"] = opResultsJson;
                env["opsApplied"] = okCount;
                env["opsTotal"] = ops.Count;

                // Bug #1: the ops above are a text-only projection of the Structure DSL — they
                // do NOT execute the SDK write, so a dryRun/validate that reports ok:true can
                // still be refused at persist time. Consult the one deterministic capability we
                // can read straight from the DSL: GeneXus refuses removing a key attribute.
                // Surface predicted refusals so dryRun stops silently promising a write the SDK
                // will reject.
                var capabilityRisks = new JArray();
                foreach (var op in ops)
                {
                    if (!string.Equals(op.Op, "remove_attribute", StringComparison.OrdinalIgnoreCase)) continue;
                    string an = op.Args?["name"]?.ToString();
                    if (string.IsNullOrEmpty(an)) continue;
                    if (SemanticOpsService.IsKeyAttributeInDsl(currentDsl, an))
                        capabilityRisks.Add(new JObject
                        {
                            ["op"] = op.Op,
                            ["name"] = an,
                            ["risk"] = "remove_key_attribute",
                            ["note"] = "'" + an + "' is a KEY attribute; GeneXus will refuse removing it from the transaction level. The real write will fail even though this dryRun shows the edit applied."
                        });
                }
                if (capabilityRisks.Count > 0)
                {
                    env["capabilityRisks"] = capabilityRisks;
                    env["willLikelyFail"] = true;
                }
                env["dryRunCaveat"] = "dryRun/validate is an in-memory projection of the Structure DSL edit; it does not execute the SDK persist, so a green opResults list is NOT a guarantee the SDK will accept the write (e.g. removing a key or still-referenced attribute is refused). Run without dryRun for the authoritative result; 'capabilityRisks' lists refusals detectable up-front.";

                if (returnPostState)
                    env["post_state"] = JsonPatchService.BuildPostState(currentDsl, newDsl, verbose);
                return env.ToString(Newtonsoft.Json.Formatting.None);
            }

            string writeResult = WriteObject(target, "Structure", newDsl, typeFilter, false, false, false, false, false, true, rollbackOnFailure, false, baseVersion, verifyMode);
            JObject writeJson;
            try { writeJson = JObject.Parse(writeResult); }
            catch { writeJson = new JObject { ["raw"] = writeResult }; }

            var writeStatus = writeJson["status"]?.ToString();
            bool writeOk = string.Equals(writeStatus, "Success", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(writeStatus, "ok", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(writeStatus, "partial", StringComparison.OrdinalIgnoreCase);

            // issue #36.1/#36.2 — the DSL text ops all "succeeded" (opResults ok:true), but the
            // PERSIST step can still fail (e.g. the SDK refused a remove_attribute of a key →
            // StructureAttributeNotRemoved). Previously the response reported isError:false with a
            // green opResults list regardless, hiding the failure. Surface the write error as the
            // envelope so callers never see ok:true on a persist no-op.
            if (!writeOk)
            {
                writeJson["opResults"] = opResultsJson;
                writeJson["opsAttempted"] = ops.Count;
                writeJson["note"] = BuildOpsPersistFailureNote(writeJson);
                return writeJson.ToString(Newtonsoft.Json.Formatting.None);
            }

            string persistedAfter = ReadPersistedPartSafely(target, "Structure");

            var resp = new JObject
            {
                ["isError"] = false,
                ["target"] = target,
                ["part"] = "Structure",
                ["mode"] = "ops",
                ["validate"] = mode,
                ["opsApplied"] = okCount,
                ["opsTotal"] = ops.Count,
                ["opResults"] = opResultsJson,
                ["write"] = writeJson
            };
            // Issue #97 guard-rail: after a successful write that added or re-typed
            // attributes, flag subtype attributes the SDK left classified as stored
            // (SECONDARY) while their same-supertype siblings are derived (INFERRED) —
            // a silent physical-column / supertype-propagation bug. Re-read the
            // persisted Transaction so the check reflects what the SDK actually saved.
            if (writeOk && ops.Any(o => string.Equals(o.Op, "add_attribute", StringComparison.OrdinalIgnoreCase)
                                        || string.Equals(o.Op, "set_attribute", StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    // Re-read with the resolved object's own type so a null request
                    // typeFilter can't resolve to a homonym of another kind (an untyped
                    // FindObject searches all types).
                    string reReadType = typeFilter ?? obj.TypeDescriptor?.Name;
                    var verifyObj = _objectService.FindObject(target, reReadType);
                    if (verifyObj != null)
                    {
                        _objectService.MarkReadCacheDirty(verifyObj, "Structure");
                        var fresh = _objectService.FindObject(target, reReadType) as global::Artech.Genexus.Common.Objects.Transaction;
                        if (fresh != null)
                        {
                            var issues = _structureService?.TryComputeSubtypeClassificationIssues(fresh) ?? new JArray();
                            if (issues.Count > 0)
                            {
                                resp["subtypeClassification"] = new JObject
                                {
                                    ["check"] = "subtype_inferred_mismatch",
                                    ["status"] = "warning",
                                    ["issues"] = issues,
                                    ["hint"] = "Subtype attribute(s) are classified as stored (SECONDARY) instead of derived (INFERRED) — this creates a physical column and breaks supertype propagation. The SDK only recomputes the class through the IDE's SubtypeGroup editor; via MCP, remove the attribute (genexus_structure action=remove_attribute or a single remove_attribute op) and re-add it, then re-run genexus_structure action=check_subtypes."
                                };
                            }
                        }
                    }
                }
                catch { /* guard-rail must never break the write response */ }
            }

            if (returnPostState)
                resp["post_state"] = JsonPatchService.BuildPostState(currentDsl, newDsl, verbose, persistedAfter);
            return resp.ToString(Newtonsoft.Json.Formatting.None);
        }

        /// <summary>
        /// The note for a mode=ops envelope whose write did not succeed. Once the
        /// SDK save happened, the failure lies in verification or rollback and the
        /// changes are in the KB, so the note must not say nothing was persisted
        /// (issue #558).
        /// </summary>
        internal static string BuildUsageErrorResponse(UsageException ux)
        {
            var payload = new JObject
            {
                ["isError"] = true,
                ["error"] = new JObject
                {
                    ["code"] = ux.Code,
                    ["message"] = ux.Message
                }
            };
            if (ux.Extra != null)
            {
                foreach (var prop in ux.Extra.Properties())
                {
                    if (payload[prop.Name] == null)
                        payload[prop.Name] = prop.Value?.DeepClone();
                }
            }
            return payload.ToString(Newtonsoft.Json.Formatting.None);
        }

        internal static string BuildOpsPersistFailureNote(JObject writeJson)
        {
            bool saved = writeJson["saved"]?.ToObject<bool?>() == true
                || writeJson["sdkSaveCompleted"]?.ToObject<bool?>() == true
                || writeJson["partialPersistenceDetected"]?.ToObject<bool?>() == true;
            return saved
                ? "The ops parsed cleanly and changes were saved, but post-write verification or rollback failed. See details above. If attribute removal was refused, try genexus_structure action=remove_attribute."
                : "The ops parsed cleanly but the persist step failed — nothing was persisted. See code/message above; opResults reflect the in-memory DSL edit only. If attribute removal was refused, try genexus_structure action=remove_attribute.";
        }

        public string ApplyJsonPatch(JObject req)
        {
            string target = req?["target"]?.ToString();
            string partName = req?["part"]?.ToString();
            return WrapWithPersistedState(ApplyJsonPatchImpl(req), target, partName, GxMcp.Worker.Helpers.WriteResultMeta.Ops);
        }

        private string ApplyJsonPatchImpl(JObject req)
        {
            // Validation runs here — no GeneXus types referenced in this method body.
            // GeneXus SDK types are isolated in ApplyJsonPatchCore so JIT can load
            // this method even when GeneXus assemblies are absent (unit-test environment).
            try
            {
                if (req == null)
                    throw new UsageException("usage_error", "request required");

                string target = req["target"]?.ToString();
                string partName = req["part"]?.ToString();
                string typeFilter = req["type"]?.ToString();
                JArray patchArr = req["patch"] as JArray;
                bool dryRun = req["dryRun"]?.ToObject<bool?>() ?? false;
                bool returnPostState = req["return_post_state"]?.ToObject<bool?>() ?? true;
                bool verbose = req["verbose"]?.ToObject<bool?>() ?? false;

                if (string.IsNullOrEmpty(target))
                    throw new UsageException("usage_error", "target required");
                if (string.IsNullOrEmpty(partName))
                    throw new UsageException("usage_error", "part required for mode:patch");
                if (patchArr == null)
                    throw new UsageException("usage_error", "patch[] required");

                // Pre-flight: reject immediately when no KB is open, before JIT-loading GeneXus types.
                if (!_objectService.GetKbService().IsOpen)
                    throw new UsageException("usage_error", "object '" + target + "' not found");

                return ApplyJsonPatchCore(target, partName, patchArr, dryRun, returnPostState, verbose, typeFilter);
            }
            catch (UsageException ux)
            {
                return BuildUsageErrorResponse(ux);
            }
            catch (Exception ex)
            {
                return new JObject
                {
                    ["isError"] = true,
                    ["error"] = new JObject
                    {
                        ["code"] = "internal_error",
                        ["message"] = ex.Message
                    }
                }.ToString(Newtonsoft.Json.Formatting.None);
            }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private string ApplyJsonPatchCore(string target, string partName, JArray patchArr, bool dryRun, bool returnPostState = true, bool verbose = false, string typeFilter = null)
        {
            var obj = _objectService.FindObject(target, typeFilter);
            if (obj == null)
                throw new UsageException("usage_error", "object '" + target + "' not found");

            string kind = obj.TypeDescriptor?.Name ?? "";

            var part = GxMcp.Worker.Structure.PartAccessor.GetPart(obj, partName);
            if (part == null)
                throw new UsageException("usage_error",
                    "part '" + partName + "' not found in " + kind);

            string currentXml = part.SerializeToXml();
            if (string.IsNullOrEmpty(currentXml))
                throw new UsageException("usage_error",
                    "part '" + partName + "' produced empty XML");

            string newXml = new JsonPatchService().Apply(currentXml, kind, patchArr);

            if (dryRun)
                return DryRunPlanBuilder.BuildEnvelope(target, currentXml, newXml, "patch", _kbValidationService).ToString(Newtonsoft.Json.Formatting.None);

            string writeResult = WriteObject(target, partName, newXml, typeFilter, false, false, false, false);
            JObject writeJson;
            try { writeJson = JObject.Parse(writeResult); }
            catch { writeJson = new JObject { ["raw"] = writeResult }; }

            // v2.6.6 FR#12 — see ApplySemanticOpsCore for the rationale.
            var patchWriteStatus = writeJson["status"]?.ToString();
            bool writeOk = string.Equals(patchWriteStatus, "Success", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(patchWriteStatus, "Ok", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(patchWriteStatus, "ok", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(patchWriteStatus, "partial", StringComparison.OrdinalIgnoreCase);
            string persistedAfter = writeOk ? ReadPersistedPartSafely(target, partName) : null;

            var resp = new JObject
            {
                ["isError"] = false,
                ["target"] = target,
                ["part"] = partName,
                ["mode"] = "patch",
                ["opsApplied"] = patchArr.Count,
                ["write"] = writeJson
            };
            if (returnPostState)
                resp["post_state"] = JsonPatchService.BuildPostState(currentXml, newXml, verbose, persistedAfter);
            return resp.ToString(Newtonsoft.Json.Formatting.None);
        }

        /// <summary>
        /// v2.6.6 FR#12 — read the persisted part bytes from the SDK with cache
        /// drop so callers see post-commit reality. Returns null on any failure
        /// (logged); callers fall back to the in-memory <c>after</c> value.
        /// </summary>
        private string ReadPersistedPartSafely(string target, string partName)
        {
            if (string.IsNullOrWhiteSpace(target)) return null;
            try
            {
                var obj = _objectService.FindObject(target, null);
                if (obj != null) _objectService.MarkReadCacheDirty(obj, partName);
                string readJson = _objectService.ReadObjectSource(target, partName, null, null, "mcp", true, null);
                if (string.IsNullOrWhiteSpace(readJson)) return null;
                var parsed = JObject.Parse(readJson);
                return parsed["source"]?.ToString()
                    ?? parsed["content"]?.ToString()
                    ?? parsed["parts"]?[partName ?? "Source"]?.ToString();
            }
            catch (Exception ex)
            {
                Logger.Debug("[POST-STATE] persisted re-read failed for " + target + " (" + partName + "): " + ex.Message);
                return null;
            }
        }
    }
}
