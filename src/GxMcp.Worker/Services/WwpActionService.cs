using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Artech.Architecture.Common.Objects;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Models;

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
                    return McpResponse.Err(code: "StaleObject",
                        message: "The WorkWithPlus PatternInstance changed after the caller's read; no action mutation was applied.",
                        target: target, extra: new JObject
                        {
                            ["expectedVersion"] = expectedVersion,
                            ["currentVersion"] = versionToken
                        });
                _patterns.BuildPatternPartEnvelope(requestedObject, "PatternInstance", xml, PatternRegistry.WorkWithPlusPatternId,
                    out _, out KBObjectPart instancePart);

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
                return McpResponse.Err(code: "WwpActionFailed", message: ex.Message, target: target);
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

            List<XElement> matchingContainers = FindFormContainers(document, containerName).ToList();
            if (matchingContainers.Count > 1)
                return new JObject
                {
                    ["code"] = "FormActionContainerAmbiguous",
                    ["error"] = "Form action container '" + containerName + "' matched more than one table.",
                    ["matchingContainers"] = new JArray(matchingContainers.Select(DescribeFormContainer))
                };

            XElement container = matchingContainers.SingleOrDefault();
            if (container == null)
            {
                var available = new JArray();
                foreach (string availableName in GetFormActionContainers(document)
                    .Select(e => Attr(e, "name")).Where(n => !string.IsNullOrWhiteSpace(n)))
                    available.Add(availableName);
                return new JObject
                {
                    ["code"] = "FormActionContainerNotFound",
                    ["error"] = "Form action container '" + containerName + "' was not found.",
                    ["availableContainers"] = available
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

            return new JObject
            {
                ["changed"] = true,
                ["actionName"] = actionName,
                ["caption"] = caption,
                ["containerName"] = containerName,
                ["event"] = "Do" + actionName,
                ["eventBinding"] = "derived-from-user-action-name"
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
                action.SetAttributeValue("gxobject", obj.Guid + "-" + obj.Name);
            }
            // Do not set SecFuntionKey or call the WWP permission-creation services.
            // Editing the public PatternInstance contract alone has no permission side effect.
        }

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
            if (deriveEvent && Is(action, "userAction")) result["event"] = "Do" + name;
            return result;
        }

        private static IEnumerable<XElement> GetFormActionContainers(XDocument document) =>
            document?.Descendants().Where(e => Is(e, "table") &&
                (Attr(e, "name").Equals("TableActions", StringComparison.OrdinalIgnoreCase) ||
                 e.Elements().Any(child => Is(child, "userAction") || Is(child, "standardAction"))))
            ?? Enumerable.Empty<XElement>();

        private static IEnumerable<XElement> FindFormContainers(XDocument document, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return Enumerable.Empty<XElement>();
            return GetFormActionContainers(document).Where(e =>
                (!string.IsNullOrWhiteSpace(Attr(e, "name")) &&
                 Attr(e, "name").Equals(name, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrWhiteSpace(Attr(e, "controlName")) &&
                 Attr(e, "controlName").Equals(name, StringComparison.OrdinalIgnoreCase)));
        }

        private static JObject DescribeFormContainer(XElement container) => new JObject
        {
            ["name"] = Attr(container, "name"),
            ["controlName"] = Attr(container, "controlName")
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
