using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Artech.Architecture.Common.Objects;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Models;
using GxMcp.Worker.Helpers;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// Typed WorkWithPlus Action Group editor.  The WWP package owns the concrete
    /// element classes, so operation-specific adapters use the native PatternInstance
    /// tree and delegate persistence/projection to the existing typed write helpers.
    /// </summary>
    public sealed partial class WwpActionService
    {
        private readonly ObjectService _objects;
        private readonly PatternAnalysisService _patterns;
        private readonly WriteService _write;

        public WwpActionService(ObjectService objects, PatternAnalysisService patterns, WriteService write)
        {
            _objects = objects;
            _patterns = patterns;
            _write = write;
        }

        // Older gateway envelopes kept the name only inside params.
        internal static string ResolveTarget(string target, JObject args) =>
            !string.IsNullOrWhiteSpace(target) ? target : (string)args?["name"];

        private string BuildWwpInstanceNotFound(string target, KBObject requestedObject)
        {
            IReadOnlyList<PatternInstanceMatch> detected = new PatternInstanceMatch[0];
            try { detected = _patterns.FindPatternInstances(requestedObject); }
            catch { /* best-effort: the error stays actionable without the list */ }
            return BuildWwpInstanceNotFound(target, requestedObject?.Name, requestedObject?.TypeDescriptor?.Name, detected);
        }

        /// <summary>
        /// WWPInstanceNotFound for an existing object without an editable WorkWithPlus
        /// instance. Lists the pattern instances it does have: genexus_wwp only edits
        /// WorkWithPlus, other patterns go through genexus_read / genexus_edit.
        /// </summary>
        internal static string BuildWwpInstanceNotFound(string target, string objectName, string objectType, IReadOnlyList<PatternInstanceMatch> detected)
        {
            detected = detected ?? new PatternInstanceMatch[0];
            var others = detected.Where(m => !m.Pattern.IsWorkWithPlus).ToList();
            var detectedJson = new JArray(detected.Select(m => new JObject
            {
                ["name"] = m.Candidate.Name,
                ["pattern"] = m.Pattern.Name
            }));

            var wwp = detected.FirstOrDefault(m => m.Pattern.IsWorkWithPlus);
            string message = wwp != null
                ? "'" + objectName + "' is not a WorkWithPlus instance; name its WorkWithPlus instance '" + wwp.Candidate.Name + "'."
                : others.Count > 0
                ? "'" + objectName + "' has no editable WorkWithPlus PatternInstance; it has " +
                  string.Join(", ", others.Select(m => m.Pattern.Name + " instance '" + m.Candidate.Name + "'")) + "."
                : "No editable WorkWithPlus PatternInstance was resolved for this object.";
            JArray nextSteps = wwp != null
                ? new JArray(McpResponse.NextStep("genexus_wwp",
                    new JObject { ["action"] = "list", ["name"] = wwp.Candidate.Name },
                    "genexus_wwp edits the WorkWithPlus instance itself."))
                : others.Count > 0
                ? new JArray(McpResponse.NextStep("genexus_read",
                    new JObject { ["name"] = others[0].Candidate.Name, ["part"] = "PatternInstance" },
                    "Read the " + others[0].Pattern.Name + " instance; genexus_edit part=PatternInstance edits it."))
                : new JArray(McpResponse.NextStep("genexus_apply_pattern",
                    new JObject { ["name"] = objectName ?? target, ["pattern"] = "WorkWithPlus", ["mode"] = "diagnose" },
                    "Check whether WorkWithPlus can be applied to this object."));

            return McpResponse.Err(code: "WWPInstanceNotFound",
                message: message,
                hint: "genexus_wwp only handles WorkWithPlus instances. Instances of other patterns are read and edited with genexus_read / genexus_edit part=PatternInstance.",
                nextSteps: nextSteps,
                target: target,
                extra: new JObject
                {
                    ["objectName"] = objectName,
                    ["objectType"] = objectType,
                    ["detectedPatterns"] = detectedJson
                });
        }

        /// <summary>
        /// The fail-closed guard every mutating operation shares: the
        /// PatternInstance was re-resolved under the per-target lock, and the
        /// re-read produced nothing usable. Each operation refuses to mutate in
        /// that state rather than staging a change it cannot save, so the message
        /// is written once here - it is the operator's only clue about which stage
        /// refused, and five copies of it could drift apart.
        ///
        /// Distinct from <see cref="BuildWwpInstanceNotFound"/>, which fires when
        /// the object resolved but is not a WorkWithPlus instance at all and has to
        /// route the caller to a different tool.
        /// </summary>
        internal static string BuildWwpInstanceNotResolvable(string target)
        {
            return McpResponse.Err(code: "WWPInstanceNotFound",
                message: "The WorkWithPlus PatternInstance could not be re-resolved before save.", target: target);
        }

        /// <summary>
        /// The refusal a caller gets when the PatternInstance moved between their read
        /// and the write.
        ///
        /// <para>
        /// Six operations refuse this way - five under the per-target lock in the
        /// partials, plus the router path - and each wrote the whole envelope out. The
        /// <c>extra</c> is why that mattered: its two fields are the entire retry
        /// contract, since a caller re-runs with <c>expectedVersion</c> and compares
        /// against <c>currentVersion</c>. Six hand-written copies of a contract the
        /// caller reads programmatically is six chances for one of them to drop a
        /// field or rename it, and the result would be a caller with no way to
        /// recover except re-reading the whole pattern.
        /// </para>
        ///
        /// <para>
        /// Only the envelope is shared, never the comparison that triggers it. The
        /// condition genuinely differs: <c>add_grid_attribute</c> requires a version
        /// and refuses up front with <c>ExpectedVersionRequired</c> when none is given,
        /// so by the time it checks, comparing is unconditional; the other four treat an
        /// absent version as "caller did not ask for a check" and skip it. Grid's is
        /// therefore a stricter contract, not an oversight, and folding the condition
        /// in would silently make the four lenient ones strict.
        /// </para>
        ///
        /// <para>
        /// The read-resolve-compare prologue around this call stays inline in each
        /// partial, by the decision recorded in <c>WwpActionService.Grid.cs</c>: it is
        /// a declaration block whose locals are read through ~40 mutation lines, so
        /// sharing it would mean six out-parameters or a renamed result object, not
        /// fewer lines. This helper is the part that declares nothing.
        /// </para>
        /// </summary>
        /// <param name="target">The target whose instance moved.</param>
        /// <param name="expectedVersion">The version the caller last saw.</param>
        /// <param name="currentVersion">The version read back under the lock.</param>
        /// <param name="message">
        /// What this specific operation did not do. It stays an argument because the
        /// six differ - naming the attribute, the table, the tab, the form action or
        /// the component is what tells an operator which stage refused - and losing
        /// that would make the refusal less useful than five copies.
        /// </param>
        internal static string BuildWwpStaleObject(string target, string expectedVersion, string currentVersion, string message)
        {
            return McpResponse.Err(code: "StaleObject",
                message: message, target: target, extra: new JObject
                {
                    ["expectedVersion"] = expectedVersion,
                    ["currentVersion"] = currentVersion
                });
        }

        /// <summary>
        /// Whether a WWP mutation cannot prove the change it is about to make.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Five things are needed to claim a change was written: the object's own
        /// native bytes, its resolved parent, that parent's WebForm text, and the
        /// before-snapshots of both the PatternInstance and the WebForm. If any is
        /// missing, the operation refuses rather than stage a change it cannot verify
        /// or roll back - which is the whole reason this list exists.
        /// </para>
        ///
        /// <para>
        /// The five-term check was written out identically at all five call sites. What
        /// is shared here is the <em>condition</em> only, never the refusal that
        /// follows it, because the five refusals are three different contracts: two
        /// report only <c>snapshot</c> and <c>persisted</c>, three add four diagnostic
        /// booleans, and one of those reports them through <c>errorExtra</c> so they
        /// land inside <c>error</c> rather than at the envelope's top level. Unifying
        /// the envelopes would change where a client reads those fields from, so each
        /// site keeps its own and states which fields it has.
        /// </para>
        ///
        /// <para>
        /// <paramref name="snapshots"/> is dereferenced directly rather than
        /// null-guarded, because <c>CaptureSnapshots</c> always constructs one - a
        /// throw there happens before the call, not inside it. Adding <c>?.</c> would
        /// be noise that implies a case that cannot occur.
        /// </para>
        /// </remarks>
        internal static bool WwpSnapshotsIncomplete(
            byte[] nativeBytes, KBObject parent, string parentWebFormBefore, SnapshotBundle snapshots)
        {
            return nativeBytes == null || parent == null || parentWebFormBefore == null
                || snapshots.Pattern == null || snapshots.WebForm == null;
        }

        public string Run(string target, JObject args)
        {
            target = ResolveTarget(target, args);
            if (((string)args?["action"])?.StartsWith("settings_", StringComparison.Ordinal) == true)
                return new PatternSettingsService(_objects).Run(target, args);
            try
            {
                if (string.Equals((string)args?["action"], "list", StringComparison.OrdinalIgnoreCase)
                    && (!string.IsNullOrWhiteSpace((string)args?["guid"])
                        || !string.IsNullOrWhiteSpace((string)args?["entityKey"])))
                {
                    // Read-only list accepts the typed parent identity as well as an
                    // instance identity. Resolve the supplied GUID/EntityKey first;
                    // never infer the parent through a homonymous object name.
                    var parent = _objects.FindObject(
                        target,
                        guid: (string)args?["guid"],
                        entityKey: (string)args?["entityKey"]);
                    string parentType = parent?.TypeDescriptor?.Name;
                    if (parent != null && (string.Equals(parentType, "Transaction", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(parentType, "WebPanel", StringComparison.OrdinalIgnoreCase)))
                    {
                        string parentXml = _patterns.ReadPatternPartXml(parent, "PatternInstance",
                            PatternRegistry.WorkWithPlusPatternId, out KBObject parentInstance,
                            out _, out JObject resolutionDiagnostic);
                        if (parentInstance == null || string.IsNullOrWhiteSpace(parentXml))
                            return McpResponse.Err(
                                code: resolutionDiagnostic?["code"]?.ToString() ?? "PatternInstanceResolutionFailed",
                                message: resolutionDiagnostic?["message"]?.ToString() ?? "The WorkWithPlus PatternInstance could not be resolved for this parent identity.",
                                hint: resolutionDiagnostic?["hint"]?.ToString() ?? "No action data was read from an object selected by name.",
                                target: target,
                                errorExtra: resolutionDiagnostic ?? new JObject
                                {
                                    ["parentName"] = parent.Name,
                                    ["parentType"] = parentType,
                                    ["parentGuid"] = parent.Guid.ToString("D"),
                                    ["parentEntityKey"] = parent.Key?.ToString()
                                });

                        var parentCatalog = Project(XDocument.Parse(parentXml, LoadOptions.PreserveWhitespace));
                        return McpResponse.Ok(target: target, code: "WwpActionsRead", result: new JObject
                        {
                            ["instance"] = parentInstance.Name,
                            ["instanceGuid"] = parentInstance.Guid.ToString("D"),
                            ["instanceEntityKey"] = parentInstance.Key?.ToString(),
                            ["parentGuid"] = parent.Guid.ToString("D"),
                            ["catalog"] = parentCatalog
                        });
                    }
                }

                KBObject requestedObject = _objects.FindObject(
                    target,
                    typeFilter: "WorkWithPlus",
                    guid: (string)args?["guid"],
                    entityKey: (string)args?["entityKey"]);
                const string wwpPrefix = "WorkWithPlus";
                if (requestedObject == null && !string.IsNullOrEmpty(target) &&
                    target.StartsWith(wwpPrefix, StringComparison.OrdinalIgnoreCase) &&
                    target.Length > wwpPrefix.Length)
                {
                    // Some SDK builds do not expose pattern instances through
                    // GetByName. Resolve the owning object only through the typed
                    // WorkWithPlus lookup; an untyped homonym is never acceptable.
                    requestedObject = _objects.FindObject(
                        target.Substring(wwpPrefix.Length), typeFilter: wwpPrefix);
                    if (requestedObject != null && !string.Equals(
                        requestedObject.TypeDescriptor?.Name, wwpPrefix, StringComparison.OrdinalIgnoreCase))
                        requestedObject = null;
                }
                if (requestedObject == null)
                {
                    // A typed WorkWithPlus lookup is preferred, but GX16/17/18
                    // builds differ in whether the instance is exposed by name.
                    // Resolve an explicitly identified Transaction/WebPanel parent
                    // through the same PatternInstance resolver before declaring
                    // the target unsupported. Bare homonyms still fail closed.
                    var existing = _objects.FindObject(
                        target,
                        guid: (string)args?["guid"],
                        entityKey: (string)args?["entityKey"]);
                    if (existing != null)
                    {
                        if (K2bWebPanelDesignerService.TryRead(existing, out var k2bDesigner))
                            return K2bWebPanelDesignerService.BuildEditRejectionResponse(
                                k2bDesigner, existing.Name, "PatternInstance", "patternInstanceUnsupported");

                        string parentXml = _patterns.ReadPatternPartXml(
                            existing, "PatternInstance", PatternRegistry.WorkWithPlusPatternId,
                            out KBObject parentInstance, out _);
                        if (parentInstance != null && !string.IsNullOrWhiteSpace(parentXml))
                        {
                            requestedObject = existing;
                        }
                        else
                        {
                            return BuildWwpInstanceNotFound(target, existing);
                        }
                    }
                }
                if (requestedObject == null)
                    return McpResponse.Err(code: "ObjectNotFound", message: "Object not found.", target: target,
                        nextSteps: new JArray(McpResponse.NextStep("genexus_search",
                            new JObject { ["query"] = target }, "Find the WorkWithPlus parent or instance by name.")));

                string xml = _patterns.ReadPatternPartXml(requestedObject, "PatternInstance", PatternRegistry.WorkWithPlusPatternId,
                    out KBObject instance, out _);
                if (instance == null || string.IsNullOrWhiteSpace(xml))
                    return BuildWwpInstanceNotFound(target, requestedObject);
                string versionToken = WriteService.ComputeContentVersionToken(instance, xml);
                string expectedVersion = args?["baseVersion"]?.ToString()
                    ?? args?["expectedVersion"]?.ToString()
                    ?? args?["versionToken"]?.ToString();
                string operation = NormalizeOperation(args?["action"]?.ToString());
                 if (IsGridColumnOperation(operation) && string.IsNullOrWhiteSpace(expectedVersion))
                     return McpResponse.Err(code: "ExpectedVersionRequired",
                         message: "baseVersion is required for move_grid_column/add_grid_variable, including dryRun previews.",
                         target: target, extra: new JObject { ["currentVersion"] = versionToken });
                 if (operation == "add_grid_variable")
                 {
                     JObject identityError = ResolveGridVariableReference(args, out string verifiedReference);
                     if (identityError != null)
                         return McpResponse.Err(code: identityError["code"]?.ToString() ?? "GridVariableIdentityRequired",
                             message: identityError["error"]?.ToString() ?? identityError["message"]?.ToString()
                                 ?? "The presentation variable identity could not be verified.",
                             target: target, extra: new JObject
                             {
                                 ["variable"] = args?["variable"]?.ToString() ?? args?["variableName"]?.ToString(),
                                 ["variableReference"] = args?["variableReference"]?.ToString()
                             });
                     args["_variableReference"] = verifiedReference;
                 }
                 if (!IsExpectedVersion(expectedVersion, versionToken))
                    return BuildWwpStaleObject(target, expectedVersion, versionToken,
                        "The WorkWithPlus PatternInstance changed after the caller's read; no action mutation was applied.");
                _patterns.BuildPatternPartEnvelope(requestedObject, "PatternInstance", xml, PatternRegistry.WorkWithPlusPatternId,
                    out _, out KBObjectPart instancePart);

                if (IsTabRead(operation))
                    return RunTabRead(target, instance, instancePart, xml, operation, args);
                if (IsAddGridOperation(operation))
                    return RunAddGridOperation(target, requestedObject, instance, instancePart, xml, args);
                if (IsFormUserActionOperation(operation))
                    return RunFormUserActionOperation(target, requestedObject, instance, instancePart, xml, args);
                if (IsWebComponentReplacementOperation(operation))
                    return RunWebComponentReplacementOperation(target, requestedObject, instance, instancePart, xml, args);
                if (IsGridAttributeOperation(operation))
                    return RunGridAttributeOperation(target, requestedObject, instance, instancePart, xml, args);
                if (IsTableTypeOperation(operation))
                    return RunTableTypeOperation(target, requestedObject, instance, instancePart, xml, args);
                if (IsTabOperation(operation))
                    return RunTabOperation(target, requestedObject, instance, instancePart, xml, operation, args);

                XDocument beforeDocument = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
                JObject before = Project(beforeDocument);
                if (operation == "list_actions")
                    return McpResponse.Ok(target: target, code: "WwpActionsRead", result: new JObject
                    {
                        ["instance"] = instance.Name,
                        ["catalog"] = before
                    });

                XDocument afterDocument = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
                JObject mutation = Apply(afterDocument, operation, args, ResolveProcedure);
                if (mutation["error"] != null)
                    return McpResponse.Err(code: mutation["code"]?.ToString() ?? "WwpActionInvalid",
                        message: mutation["error"].ToString(), target: target, extra: mutation);

                JObject after = Project(afterDocument);
                var diff = new JObject { ["before"] = before, ["after"] = after };
                bool dryRun = args?["dryRun"]?.ToObject<bool?>() == true;
                bool rollbackOnFailure = args?["rollbackOnFailure"]?.ToObject<bool?>() ?? true;
                if (dryRun)
                    return McpResponse.Ok(target: target, code: "DryRun", result: new JObject
                    {
                        ["instance"] = instance.Name,
                        ["operation"] = operation,
                        ["diff"] = diff,
                        ["versionToken"] = versionToken,
                        ["saved"] = false,
                        ["rollbackOnFailure"] = rollbackOnFailure,
                        ["event"] = mutation["event"]?.ToString(),
                        ["containerName"] = mutation["containerName"]?.ToString()
                    });

                string writeRaw = _write.WriteObject(target, new JObject
                {
                    ["part"] = "PatternInstance",
                    ["mode"] = "full",
                    ["content"] = afterDocument.ToString(SaveOptions.DisableFormatting),
                    ["validate"] = true,
                    ["patternEditMode"] = "grid-column",
                    ["baseVersion"] = expectedVersion
                });
                JObject write;
                try
                {
                    write = JObject.Parse(writeRaw);
                }
                catch (Exception parseEx)
                {
                    JObject rollback = TryRollback(target, requestedObject, xml, before, rollbackOnFailure,
                        "write_response_not_json", after, null);
                    return BuildWriteFailure(target, operation, writeRaw, parseEx.Message, rollbackOnFailure, rollback);
                }
                if (!IsSuccess(write))
                {
                    JObject rollback = TryRollback(target, requestedObject, xml, before, rollbackOnFailure,
                        "write_failed", after, ExtractWriteVersionToken(write));
                    return BuildWriteFailure(target, operation, write, "The WorkWithPlus PatternInstance write was not accepted.", rollbackOnFailure, rollback);
                }

                KBObject refreshedTarget = _objects.FindObject(target) ?? requestedObject;
                string persistedXml = _patterns.ReadPatternPartXml(refreshedTarget, "PatternInstance", PatternRegistry.WorkWithPlusPatternId, out KBObject persistedInstance, out _);
                JObject persisted = string.IsNullOrWhiteSpace(persistedXml)
                    ? new JObject()
                    : Project(XDocument.Parse(persistedXml, LoadOptions.PreserveWhitespace));
                if (!JToken.DeepEquals(after, persisted))
                {
                    JObject rollback = TryRollback(target, refreshedTarget, xml, before, rollbackOnFailure,
                        "post_write_verification_failed", after, ExtractWriteVersionToken(write));
                    return McpResponse.Err(code: "WwpActionNotPersisted",
                        message: "The PatternInstance save completed, but the requested structural action state was not persisted.",
                        target: target, extra: new JObject
                        {
                            ["before"] = before,
                            ["requested"] = after,
                            ["persisted"] = persisted,
                            ["diff"] = new JObject { ["requested"] = after, ["persisted"] = persisted },
                            ["saved"] = false,
                            ["rollbackOnFailure"] = rollbackOnFailure,
                            ["rollback"] = rollback
                        });
                }

                if (IsGridColumnOperation(operation))
                {
                    string projectionError = VerifyGridProjection(refreshedTarget, after, args);
                    if (!string.IsNullOrWhiteSpace(projectionError) && !ReferenceEquals(refreshedTarget, requestedObject))
                        projectionError = VerifyGridProjection(requestedObject, after, args);
                    if (operation == "add_grid_variable")
                    {
                        string variableError = VerifyGridVariableDeclaration(
                            new[] { refreshedTarget, requestedObject, persistedInstance },
                            args?["variable"]?.ToString() ?? args?["variableName"]?.ToString(),
                            args?["_variableReference"]?.ToString());
                        if (string.IsNullOrWhiteSpace(projectionError)) projectionError = variableError;
                    }
                    if (!string.IsNullOrWhiteSpace(projectionError))
                    {
                        JObject rollback = TryRollback(target, refreshedTarget, xml, before, rollbackOnFailure,
                            "projection_verification_failed", after, ExtractWriteVersionToken(write));
                        return McpResponse.Err(
                            code: "WwpProjectionNotVerified",
                            message: projectionError,
                            target: target,
                            extra: new JObject
                            {
                                ["persisted"] = true,
                                ["verified"] = false,
                                ["rollback"] = rollback,
                                ["rollbackRequested"] = rollbackOnFailure,
                                ["requested"] = after,
                                ["persistedPattern"] = persisted
                            });
                    }
                }

                return McpResponse.Ok(target: target, code: "WwpActionUpdated", result: new JObject
                {
                    ["instance"] = persistedInstance?.Name ?? instance.Name,
                    ["operation"] = operation,
                    ["diff"] = diff,
                    ["versionToken"] = WriteService.ComputeContentVersionToken(persistedInstance, persistedXml),
                    ["persisted"] = persisted,
                    ["write"] = write,
                    ["saved"] = true,
                    ["rollbackOnFailure"] = rollbackOnFailure,
                    ["event"] = mutation["event"]?.ToString(),
                    ["containerName"] = mutation["containerName"]?.ToString(),
                    ["specified"] = false,
                    ["generatedImpacts"] = new JObject
                    {
                        ["patternInstance"] = persistedInstance?.Name ?? instance.Name,
                        ["parent"] = write["result"]?["projection"]?["parent"]?.DeepClone() ?? target,
                        ["projection"] = write["result"]?["projection"]?.DeepClone() ?? write["projection"]?.DeepClone()
                    },
                    ["securityPermissionsAdded"] = false,
                    ["note"] = "The PatternInstance was saved and re-read. No security permission was created automatically."
                });
            }
            catch (Exception ex)
            {
                // issue #332: this used to surface `ex.Message` verbatim, so a
                // NullReferenceException reached the caller as "Object reference not set
                // to an instance of an object" with no indication of what to do instead.
                // The underlying defect is fixed at its source (see
                // PatternAnalysisService.FindPatternPart) and every resolution failure
                // above already returns a typed error naming the host to use. What
                // remains is an unexpected failure, and it must name the stage that
                // produced it rather than repeat a framework message.
                string stage = (string)args?["action"] ?? "(none)";
                GxMcp.Worker.Helpers.Logger.Error($"[WWP] action='{stage}' target='{target}' failed: {ex.GetType().Name}: {ex.Message}");
                return McpResponse.Err(
                    code: "WwpActionFailed",
                    message: $"The WorkWithPlus '{stage}' request for '{target}' failed unexpectedly ({ex.GetType().Name}).",
                    hint: "Re-read the instance with genexus_read part=PatternInstance to confirm its identity and state, then retry naming the WorkWithPlus instance object directly (for example 'WorkWithPlus<Panel>') rather than its parent.",
                    target: target,
                    extra: new JObject
                    {
                        ["operation"] = stage,
                        ["exceptionType"] = ex.GetType().FullName,
                        ["detail"] = ex.Message
                    });
            }
        }

        private static string ExtractWriteVersionToken(JObject write)
        {
            if (write == null) return null;
            string token = write["versionToken"]?.ToString();
            if (!string.IsNullOrWhiteSpace(token)) return token;
            token = write["result"]?["versionToken"]?.ToString();
            if (!string.IsNullOrWhiteSpace(token)) return token;
            return write["result"]?["postSaveVerification"]?["versionToken"]?.ToString();
        }

        private JObject TryRollback(string target, KBObject fallbackTarget, string originalXml,
            JObject expectedProjection, bool rollbackOnFailure, string reason,
            JObject requestedProjection = null, string expectedPersistedVersion = null)
        {
            var result = new JObject
            {
                ["attempted"] = rollbackOnFailure,
                ["reason"] = reason,
                ["rolledBack"] = false
            };
            if (!rollbackOnFailure) return result;

            try
            {
                KBObject currentTarget = _objects.FindObject(target) ?? fallbackTarget;
                string currentXml = _patterns.ReadPatternPartXml(
                    currentTarget, "PatternInstance", PatternRegistry.WorkWithPlusPatternId,
                    out KBObject currentInstance, out _);
                if (string.IsNullOrWhiteSpace(currentXml))
                {
                    result["error"] = "The current PatternInstance could not be reread; rollback was refused.";
                    result["refused"] = true;
                    return result;
                }

                JObject currentProjection = Project(XDocument.Parse(currentXml, LoadOptions.PreserveWhitespace));
                string currentVersion = WriteService.ComputeContentVersionToken(currentInstance, currentXml);
                if (JToken.DeepEquals(currentProjection, expectedProjection))
                {
                    result["rolledBack"] = true;
                    result["noOp"] = true;
                    result["currentVersion"] = currentVersion;
                    return result;
                }

                bool matchesRequested = requestedProjection != null
                    && JToken.DeepEquals(currentProjection, requestedProjection);
                bool matchesVersionFence = !string.IsNullOrWhiteSpace(expectedPersistedVersion)
                    && string.Equals(currentVersion, expectedPersistedVersion, StringComparison.Ordinal);
                if (!matchesRequested && !matchesVersionFence)
                {
                    result["refused"] = true;
                    result["error"] = "The current PatternInstance is newer than this write; refusing a compensating write.";
                    result["currentVersion"] = currentVersion;
                    result["expectedVersion"] = expectedPersistedVersion;
                    return result;
                }

                var restoreArgs = new JObject
                {
                    ["part"] = "PatternInstance",
                    ["mode"] = "full",
                    ["content"] = originalXml,
                    ["validate"] = true
                };
                if (!string.IsNullOrWhiteSpace(currentVersion))
                    restoreArgs["baseVersion"] = currentVersion;
                string raw = _write.WriteObject(target, restoreArgs);
                JObject write = JObject.Parse(raw);
                result["write"] = write;
                if (!IsSuccess(write)) return result;

                KBObject refreshedTarget = _objects.FindObject(target) ?? fallbackTarget;
                string persistedXml = _patterns.ReadPatternPartXml(
                    refreshedTarget, "PatternInstance", PatternRegistry.WorkWithPlusPatternId, out _, out _);
                JObject persisted = string.IsNullOrWhiteSpace(persistedXml)
                    ? new JObject()
                    : Project(XDocument.Parse(persistedXml, LoadOptions.PreserveWhitespace));
                result["persisted"] = persisted;
                result["rolledBack"] = JToken.DeepEquals(expectedProjection, persisted);
            }
            catch (Exception ex)
            {
                result["error"] = ex.Message;
            }
            return result;
        }

        private static string BuildWriteFailure(string target, string operation, object write,
            string message, bool rollbackOnFailure, JObject rollback)
        {
            return McpResponse.Err(code: "WwpActionWriteFailed", message: message, target: target,
                extra: new JObject
                {
                    ["operation"] = operation,
                    ["write"] = write is JToken token ? token : JValue.CreateString(write?.ToString() ?? string.Empty),
                    ["saved"] = false,
                    ["rollbackOnFailure"] = rollbackOnFailure,
                    ["rollback"] = rollback
                });
        }

        internal static bool IsExpectedVersion(string expected, string current) =>
            string.IsNullOrWhiteSpace(expected) || string.Equals(expected, current, StringComparison.Ordinal);

        private KBObject ResolveProcedure(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            KBObject obj = _objects.FindObject(name, "Procedure");
            return obj != null && string.Equals(obj.TypeDescriptor?.Name, "Procedure", StringComparison.OrdinalIgnoreCase)
                ? obj : null;
        }

        internal static string NormalizeOperation(string operation)
        {
            string normalized = (operation ?? "list").Trim().ToLowerInvariant();
            if (normalized == "list") return "list_actions";
            if (normalized == "add_action") return "add_grid_action";
            if (normalized == "add_form_action") return "add_user_action";
            return normalized;
        }

        internal static JObject Apply(XDocument document, string operation, JObject args,
            Func<string, KBObject> procedureResolver)
        {
            if (operation == "add_user_action")
                return AddFormUserAction(document, args, procedureResolver);
            if (operation == "move_grid_column")
                return ApplyMoveGridColumnXml(document, args);
            if (operation == "add_grid_variable")
                return ApplyAddGridVariableXml(document, args);

            string groupName = args?["group"]?.ToString() ?? args?["fromGroup"]?.ToString();
            string actionName = args?["actionName"]?.ToString();
            XElement group = FindGroup(document, groupName);

            if (operation == "add_grid_action" && group == null)
            {
                if (string.IsNullOrWhiteSpace(groupName)) return Error("MissingActionGroup", "group is required.");
                XElement parent = document.Descendants().FirstOrDefault(e =>
                    e.Elements().Any(c => Is(c, "actionGroup")))
                    ?? document.Descendants().FirstOrDefault(e =>
                        e.Name.LocalName.IndexOf("action", StringComparison.OrdinalIgnoreCase) >= 0
                        || e.Name.LocalName.IndexOf("grid", StringComparison.OrdinalIgnoreCase) >= 0);
                if (parent == null) return Error("ActionGroupContainerNotFound", "The WorkWithPlus instance has no action-group/grid container where a group can be created safely.");
                group = new XElement(parent.GetDefaultNamespace() + "actionGroup",
                    new XAttribute("name", groupName), new XAttribute("caption", groupName));
                parent.Add(group);
            }

            if (group == null) return Error("ActionGroupNotFound", "Action group '" + groupName + "' was not found.");
            XElement action = group.Elements().FirstOrDefault(e => Is(e, "userAction") && Attr(e, "name").Equals(actionName ?? "", StringComparison.OrdinalIgnoreCase));

            switch (operation)
            {
                case "add_grid_action":
                    if (string.IsNullOrWhiteSpace(actionName)) return Error("MissingActionName", "actionName is required.");
                    if (action != null) return Error("ActionAlreadyExists", "Action '" + actionName + "' already exists in group '" + groupName + "'.");
                    action = new XElement(group.GetDefaultNamespace() + "userAction", new XAttribute("name", actionName));
                    group.Add(action);
                    ApplyProperties(action, args, procedureResolver);
                    Move(action, args?["position"]?.ToObject<int?>());
                    break;
                case "update_action":
                    if (action == null) return Error("ActionNotFound", "Action '" + actionName + "' was not found in group '" + groupName + "'.");
                    ApplyProperties(action, args, procedureResolver);
                    if (args?["newGroup"] != null || args?["toGroup"] != null)
                    {
                        string destinationName = args?["newGroup"]?.ToString() ?? args?["toGroup"]?.ToString();
                        XElement destination = FindGroup(document, destinationName);
                        if (destination == null) return Error("DestinationActionGroupNotFound", "Destination action group was not found.");
                        action.Remove(); destination.Add(action); group = destination;
                    }
                    Move(action, args?["position"]?.ToObject<int?>());
                    break;
                case "move_action":
                    if (action == null) return Error("ActionNotFound", "Action '" + actionName + "' was not found.");
                    string moveDestinationName = args?["newGroup"]?.ToString() ?? args?["toGroup"]?.ToString();
                    XElement moveDestination = string.IsNullOrWhiteSpace(moveDestinationName)
                        ? group : FindGroup(document, moveDestinationName);
                    if (moveDestination == null) return Error("DestinationActionGroupNotFound", "Destination action group was not found.");
                    action.Remove(); moveDestination.Add(action); Move(action, args?["position"]?.ToObject<int?>());
                    break;
                case "remove_action":
                    if (action == null) return Error("ActionNotFound", "Action '" + actionName + "' was not found.");
                    action.Remove();
                    break;
                default:
                    return Error("UnknownWwpActionOperation", "Unknown action operation '" + operation + "'.");
            }
            return new JObject { ["changed"] = true };
        }

        private static JObject AddFormUserAction(XDocument document, JObject args,
            Func<string, KBObject> procedureResolver)
        {
            string containerName = args?["containerName"]?.ToString();
            if (string.IsNullOrWhiteSpace(containerName))
                containerName = args?["container"]?.ToString();
            if (string.IsNullOrWhiteSpace(containerName))
                containerName = "TableActions";
            else
                containerName = containerName.Trim();
            string actionName = args?["actionName"]?.ToString()?.Trim();
            string caption = args?["caption"]?.ToString()
                ?? args?["description"]?.ToString();

            if (string.IsNullOrWhiteSpace(actionName))
                return Error("MissingActionName", "actionName is required for a form-level user action.");
            if (!Regex.IsMatch(actionName, "^[A-Za-z_][A-Za-z0-9_]*$"))
                return Error("InvalidActionName", "actionName must be a GeneXus event-safe identifier so the derived event is deterministic.");
            if (string.IsNullOrWhiteSpace(caption))
                return Error("MissingActionCaption", "caption is required for a form-level user action.");
            if (args?["procedure"] != null && !string.IsNullOrWhiteSpace(args["procedure"]?.ToString()))
                return Error("FormActionProcedureConflict", "A form-level user action derives its event as Do<actionName>; omit procedure when the action should fire that event.");
            JObject callObjectError = ValidateCallObjectArgs(args);
            if (callObjectError != null) return callObjectError;
            bool callsObject = HasCallObject(args);
            if (callsObject && string.IsNullOrWhiteSpace(args["_callObjectReference"]?.ToString()))
                return Error("CallObjectUnresolved", "callObject was not resolved to a KB object.");

            List<XElement> matchingContainers = FindFormContainers(document, containerName).ToList();
            if (matchingContainers.Count > 1)
                return new JObject
                {
                    ["code"] = "FormActionContainerAmbiguous",
                    ["error"] = "Form action container '" + containerName + "' matched more than one table.",
                    ["matchingContainers"] = new JArray(matchingContainers.Select(DescribeFormContainer))
                };

            XElement container = matchingContainers.SingleOrDefault();
            if (container == null && containerName.Equals("TableActions", StringComparison.OrdinalIgnoreCase))
            {
                List<XElement> parents = FindFormContainers(document, "TableMain").Where(e => Is(e, "table")).ToList();
                if (parents.Count != 1)
                    return Error("FormActionContainerNotFound", "Creating TableActions requires exactly one TableMain table; pass an existing containerName instead.");
                container = new XElement(parents[0].Name.Namespace + "table",
                    new XAttribute("name", "TableActions"), new XAttribute("type", "Responsive"));
                parents[0].Add(container);
            }
            if (container == null)
            {
                var available = new JArray();
                foreach (string availableName in GetFormActionContainers(document)
                    .Select(e => Attr(e, "name")).Where(n => !string.IsNullOrWhiteSpace(n)))
                    available.Add(availableName);
                var availablePaths = new JArray(GetFormActionContainers(document).Select(ContainerPath).Where(n => n.Length > 0).Distinct());
                return new JObject
                {
                    ["code"] = "FormActionContainerNotFound",
                    ["error"] = "Form action container '" + containerName + "' was not found.",
                    ["availableContainers"] = available,
                    ["availablePaths"] = availablePaths
                };
            }

            XElement existing = container.Elements().FirstOrDefault(e =>
                Is(e, "userAction") && Attr(e, "name").Equals(actionName, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
                return Error("ActionAlreadyExists", "Form action '" + actionName + "' already exists in container '" + containerName + "'.");

            XElement action = new XElement(container.GetDefaultNamespace() + "userAction",
                new XAttribute("name", actionName),
                new XAttribute("caption", caption));
            container.Add(action);
            ApplyProperties(action, args, procedureResolver);
            if (callsObject) ApplyCallObjectXml(action, args);

            return new JObject
            {
                ["changed"] = true,
                ["actionName"] = actionName,
                ["caption"] = caption,
                ["containerName"] = containerName,
                ["event"] = callsObject ? JValue.CreateNull() : new JValue("Do" + actionName),
                ["eventBinding"] = callsObject ? "opens-object" : "derived-from-user-action-name"
            };
        }

        private static void ApplyProperties(XElement action, JObject args, Func<string, KBObject> procedureResolver)
        {
            SetIfPresent(action, "caption", args?["caption"] ?? args?["description"]);
            SetIfPresent(action, "condition", args?["enabledWhen"]);
            SetIfPresent(action, "visibleCondition", args?["visibleWhen"]);
            if (args?["icon"] != null)
            {
                string icon = args["icon"].ToString();
                bool fontIcon = icon.IndexOf("fa-", StringComparison.OrdinalIgnoreCase) >= 0
                             || icon.StartsWith("fas ", StringComparison.OrdinalIgnoreCase)
                             || icon.StartsWith("far ", StringComparison.OrdinalIgnoreCase)
                             || icon.StartsWith("fab ", StringComparison.OrdinalIgnoreCase);
                action.SetAttributeValue(fontIcon ? "fontIcon" : "image", icon);
                action.SetAttributeValue("imageType", fontIcon ? "Font icon" : "Image");
            }
            SetIfPresent(action, "tooltip", args?["description"]);
            SetIfPresent(action, "buttonClass", args?["buttonClass"]);
            string selection = args?["selection"]?.ToString();
            if (!string.IsNullOrWhiteSpace(selection))
                action.SetAttributeValue("multiRowSelection", selection.Equals("multiple", StringComparison.OrdinalIgnoreCase) ? "True" : "False");
            if (args?["confirmation"] != null)
            {
                action.SetAttributeValue("confirm", "True");
                action.SetAttributeValue("confirmMessage", args["confirmation"].ToString());
            }
            SetIfPresent(action, "confirmTitle", args?["confirmTitle"]);
            if (!string.IsNullOrWhiteSpace(args?["procedure"]?.ToString()))
            {
                string procedure = args["procedure"].ToString();
                KBObject obj = procedureResolver?.Invoke(procedure);
                if (obj == null) throw new InvalidOperationException("Procedure '" + procedure + "' was not found.");
                action.SetAttributeValue("gxobject", GxObjectReference(obj));
            }
            // Do not set SecFuntionKey or call the WWP permission-creation services.
            // Editing the public PatternInstance contract alone has no permission side effect.
        }

        // The IDE stores gxobject as <type GUID>-<qualified name>, the type GUID being
        // shared by every object of the type, not the object's own GUID (#414).
        internal static string GxObjectReference(KBObject obj)
            => FormatGxObjectReference(KbEntityIdentity.TypeGuid(obj), obj.QualifiedName?.ToString(), obj.Name);

        internal static string FormatGxObjectReference(string typeGuid, string qualifiedName, string name)
            => typeGuid + "-" + (string.IsNullOrEmpty(qualifiedName) ? name : qualifiedName);

        private static JObject Project(XDocument document)
        {
            var groups = new JArray();
            foreach (XElement group in document.Descendants().Where(e => Is(e, "actionGroup")))
            {
                var actions = new JArray();
                foreach (XElement action in group.Elements().Where(e => Is(e, "userAction")))
                    actions.Add(ProjectAction(action, deriveEvent: false));
                groups.Add(new JObject { ["name"] = Attr(group, "name"), ["caption"] = Attr(group, "caption"), ["actions"] = actions });
            }

            var formContainers = new JArray();
            foreach (XElement container in GetFormActionContainers(document))
            {
                var actions = new JArray(container.Elements()
                    .Where(e => Is(e, "userAction") || Is(e, "standardAction"))
                    .Select(action => ProjectAction(action, deriveEvent: true)));
                formContainers.Add(new JObject
                {
                    ["name"] = Attr(container, "name"),
                    ["actions"] = actions
                });
            }
            var grids = new JArray();
            foreach (XElement grid in document.Descendants().Where(e =>
                Is(e, "grid") || Is(e, "simplegrid")))
                grids.Add(ProjectGridElement(grid));

            return new JObject
            {
                ["groups"] = groups,
                ["formContainers"] = formContainers,
                ["grids"] = grids
            };
        }

        private static JObject ProjectGridElement(XElement grid)
        {
            var columns = new JArray();
            foreach (XElement column in grid.Elements().Where(e =>
                Is(e, "gridAttribute") || Is(e, "gridVariable")))
            {
                var attrs = new JObject();
                foreach (var attribute in column.Attributes()
                    .OrderBy(a => a.Name.LocalName, StringComparer.OrdinalIgnoreCase))
                    attrs[attribute.Name.LocalName] = attribute.Value;
                columns.Add(new JObject
                {
                    ["kind"] = column.Name.LocalName,
                    ["attributes"] = attrs
                });
            }
            return new JObject
            {
                ["name"] = Attr(grid, "name"),
                ["controlName"] = Attr(grid, "controlName"),
                ["id"] = Attr(grid, "id"),
                ["columns"] = columns,
                ["customProperties"] = Attr(grid, K2bWebPanelDesignerService.CustomPropertiesMarker)
            };
        }

        private static JObject ProjectAction(XElement action, bool deriveEvent)
        {
            string name = Attr(action, "name");
            var result = new JObject
            {
                ["name"] = name,
                ["caption"] = Attr(action, "caption"),
                ["procedure"] = Attr(action, "gxobject"),
                ["condition"] = Attr(action, "condition"),
                ["visibleCondition"] = Attr(action, "visibleCondition"),
                ["icon"] = Attr(action, "image"),
                ["confirmation"] = Attr(action, "confirmMessage"),
                ["multipleSelection"] = Attr(action, "multiRowSelection")
            };
            if (deriveEvent && Is(action, "userAction"))
            {
                if (string.IsNullOrEmpty(Attr(action, "gxobject"))) result["event"] = "Do" + name;
                else
                {
                    // A form button that opens an object fires no derived event.
                    result["callsObject"] = Attr(action, "gxobject");
                    result["popup"] = Attr(action, "popup");
                    result["parameters"] = new JArray(action.Elements().Where(e => Is(e, "parameters"))
                        .SelectMany(p => p.Elements().Where(e => Is(e, "parameter"))).Select(e => Attr(e, "name")));
                }
            }
            return result;
        }

        // Every table and action group can hold a form action (#415); a bare name that
        // repeats across tabs is disambiguated by a path of named ancestors.
        private static IEnumerable<XElement> GetFormActionContainers(XDocument document) =>
            document?.Descendants().Where(e => Is(e, "table") || Is(e, "actionGroup"))
            ?? Enumerable.Empty<XElement>();

        private static string ContainerLabel(XElement e)
        {
            string name = Attr(e, "name");
            return string.IsNullOrWhiteSpace(name) ? Attr(e, "controlName") : name;
        }

        internal static List<string> ContainerPathSegments(XElement container) =>
            container.AncestorsAndSelf().Reverse().Select(ContainerLabel).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();

        internal static string ContainerPath(XElement container) => string.Join("/", ContainerPathSegments(container));

        // A bare name matches the container's own name/controlName; "A/B/C" matches when the
        // container's named ancestors end with exactly those segments.
        internal static bool ContainerPathMatches(IList<string> segments, string own, string controlName, string requested)
        {
            if (string.IsNullOrWhiteSpace(requested)) return false;
            if (requested.IndexOf('/') < 0)
                return string.Equals(own, requested, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(controlName, requested, StringComparison.OrdinalIgnoreCase);
            string[] wanted = requested.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (wanted.Length == 0 || wanted.Length > segments.Count) return false;
            for (int i = 0; i < wanted.Length; i++)
                if (!string.Equals(segments[segments.Count - wanted.Length + i], wanted[i].Trim(), StringComparison.OrdinalIgnoreCase))
                    return false;
            return true;
        }

        private static IEnumerable<XElement> FindFormContainers(XDocument document, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return Enumerable.Empty<XElement>();
            return GetFormActionContainers(document).Where(e =>
                ContainerPathMatches(ContainerPathSegments(e), Attr(e, "name"), Attr(e, "controlName"), name.Trim()));
        }

        private static JObject DescribeFormContainer(XElement container) => new JObject
        {
            ["name"] = Attr(container, "name"),
            ["controlName"] = Attr(container, "controlName"),
            ["path"] = ContainerPath(container)
        };

        private static XElement FindGroup(XDocument doc, string name) => string.IsNullOrWhiteSpace(name) ? null
            : doc.Descendants().FirstOrDefault(e => Is(e, "actionGroup") && Attr(e, "name").Equals(name, StringComparison.OrdinalIgnoreCase));
        private static bool Is(XElement e, string name) => e.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase);
        private static string Attr(XElement e, string name) => e.Attributes().FirstOrDefault(a => a.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value ?? string.Empty;
        private static void SetIfPresent(XElement e, string name, JToken value) { if (value != null) e.SetAttributeValue(name, value.ToString()); }
        private static void Move(XElement element, int? position)
        {
            if (!position.HasValue) return;
            XElement parent = element.Parent; if (parent == null) return;
            var peers = parent.Elements().Where(e => Is(e, "userAction") && e != element).ToList();
            element.Remove();
            int index = Math.Max(0, Math.Min(position.Value, peers.Count));
            if (index == peers.Count) parent.Add(element); else peers[index].AddBeforeSelf(element);
        }
        private static JObject Error(string code, string message) => new JObject { ["code"] = code, ["error"] = message };
        private static bool IsSuccess(JObject response) => string.Equals(response?["status"]?.ToString(), "ok", StringComparison.OrdinalIgnoreCase)
            || string.Equals(response?["status"]?.ToString(), "success", StringComparison.OrdinalIgnoreCase);
    }
}
