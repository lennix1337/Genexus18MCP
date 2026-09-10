using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Artech.Architecture.Common.Objects;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Models;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// Typed WorkWithPlus Action Group editor.  The WWP package owns the concrete
    /// element classes, so this adapter deliberately edits its public PatternInstance
    /// XML contract and delegates persistence/projection to WriteService.
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

        public string Run(string target, JObject args)
        {
            target = ResolveTarget(target, args);
            if (((string)args?["action"])?.StartsWith("settings_", StringComparison.Ordinal) == true)
                return new PatternSettingsService(_objects).Run(target, args);
            try
            {
                KBObject requestedObject = _objects.FindObject(target);
                const string wwpPrefix = "WorkWithPlus";
                if (requestedObject == null && !string.IsNullOrEmpty(target) &&
                    target.StartsWith(wwpPrefix, StringComparison.OrdinalIgnoreCase) &&
                    target.Length > wwpPrefix.Length)
                {
                    // Some SDK builds do not expose pattern instances through
                    // GetByName. Resolve the owning object and follow its typed child.
                    requestedObject = _objects.FindObject(target.Substring(wwpPrefix.Length));
                }
                if (requestedObject == null)
                    return McpResponse.Err(code: "ObjectNotFound", message: "Object not found.", target: target,
                        nextSteps: new JArray(McpResponse.NextStep("genexus_search",
                            new JObject { ["query"] = target }, "Find the WorkWithPlus parent or instance by name.")));

                string xml = _patterns.ReadPatternPartXml(requestedObject, "PatternInstance",
                    out KBObject instance, out _);
                if (instance == null || string.IsNullOrWhiteSpace(xml))
                    return McpResponse.Err(code: "WWPInstanceNotFound",
                        message: "No editable WorkWithPlus PatternInstance was resolved for this object.", target: target);
                _patterns.BuildPatternPartEnvelope(requestedObject, "PatternInstance", xml,
                    out _, out KBObjectPart instancePart);

                string operation = NormalizeOperation(args?["action"]?.ToString());
                if (IsGridAttributeOperation(operation))
                    return RunGridAttributeOperation(target, requestedObject, instance, instancePart, xml, args);
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
                if (args?["dryRun"]?.ToObject<bool?>() == true)
                    return McpResponse.Ok(target: target, code: "DryRun", result: new JObject
                    {
                        ["instance"] = instance.Name,
                        ["operation"] = operation,
                        ["diff"] = diff,
                        ["saved"] = false
                    });

                string writeRaw = _write.WriteObject(target, new JObject
                {
                    ["part"] = "PatternInstance",
                    ["mode"] = "full",
                    ["content"] = afterDocument.ToString(SaveOptions.DisableFormatting),
                    ["validate"] = true
                });
                JObject write = JObject.Parse(writeRaw);
                if (!IsSuccess(write)) return writeRaw;

                KBObject refreshedTarget = _objects.FindObject(target) ?? requestedObject;
                string persistedXml = _patterns.ReadPatternPartXml(refreshedTarget, "PatternInstance", out KBObject persistedInstance, out _);
                JObject persisted = string.IsNullOrWhiteSpace(persistedXml)
                    ? new JObject()
                    : Project(XDocument.Parse(persistedXml, LoadOptions.PreserveWhitespace));
                if (!JToken.DeepEquals(after, persisted))
                    return McpResponse.Err(code: "WwpActionNotPersisted",
                        message: "The PatternInstance save completed, but the requested action-group state was not persisted.",
                        target: target, extra: new JObject
                        {
                            ["before"] = before,
                            ["requested"] = after,
                            ["persisted"] = persisted,
                            ["diff"] = new JObject { ["requested"] = after, ["persisted"] = persisted },
                            ["saved"] = false
                        });

                return McpResponse.Ok(target: target, code: "WwpActionUpdated", result: new JObject
                {
                    ["instance"] = persistedInstance?.Name ?? instance.Name,
                    ["operation"] = operation,
                    ["diff"] = diff,
                    ["persisted"] = persisted,
                    ["write"] = write,
                    ["saved"] = true,
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
            return normalized;
        }

        internal static JObject Apply(XDocument document, string operation, JObject args,
            Func<string, KBObject> procedureResolver)
        {
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
            if (args?["procedure"] != null)
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
                    actions.Add(new JObject
                    {
                        ["name"] = Attr(action, "name"), ["caption"] = Attr(action, "caption"),
                        ["procedure"] = Attr(action, "gxobject"), ["condition"] = Attr(action, "condition"),
                        ["visibleCondition"] = Attr(action, "visibleCondition"), ["icon"] = Attr(action, "image"),
                        ["confirmation"] = Attr(action, "confirmMessage"), ["multipleSelection"] = Attr(action, "multiRowSelection")
                    });
                groups.Add(new JObject { ["name"] = Attr(group, "name"), ["caption"] = Attr(group, "caption"), ["actions"] = actions });
            }
            return new JObject { ["groups"] = groups };
        }

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
