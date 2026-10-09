using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using Artech.Architecture.Common.Objects;
using Artech.Genexus.Common.Objects;
using Artech.Genexus.Common.Parts;
using Artech.Genexus.Common.Parts.ExternalObject;
using GxMcp.Worker.Helpers;

namespace GxMcp.Worker.Services.Structure
{
    // issue #39 batch 2: authoring for object types the structure DSL doesn't cover —
    // ExternalObject methods/properties and Menu options. Both follow the "build an item,
    // add it to the owning part's collection, EnsureSave" pattern.
    public class AuthoringService
    {
        private readonly ObjectService _objectService;

        public AuthoringService(ObjectService objectService)
        {
            _objectService = objectService;
        }

        private static T FindPart<T>(KBObject obj) where T : class
        {
            foreach (var p in obj.Parts) if (p is T tp) return tp;
            return null;
        }

        // Typed contract, duplicate checks and an independent read after saving.
        public string AddExternalMethod(string objName, string payload)
        {
            ExternalObjectMethod added = null;
            EXOStructurePart exo = null;
            KBObject obj = null;
            JObject before = null;
            bool saveAttempted = false;
            string ownedWriteVersion = null;
            try
            {
                var json = string.IsNullOrWhiteSpace(payload) ? new JObject() : JObject.Parse(payload);
                ExternalObjectContract.ValidatePayload(json);
                obj = _objectService.FindObjectFresh(objName, "ExternalObject");
                if (obj == null) return HealingService.FormatNotFoundError(objName, _objectService.GetKbService().GetIndexCache().GetIndex());
                if (!(obj is ExternalObject)) return Models.McpResponse.Err(code: "NotAnExternalObject", message: "Target is not an External Object.", target: objName);
                exo = FindPart<EXOStructurePart>(obj);
                if (exo == null) return Models.McpResponse.Err(code: "StructurePartNotFound", message: "External Object has no structure part.", target: objName);
                before = ExternalObjectContract.Read(exo);
                string version = ExternalObjectContract.Version(before);
                if (json["expectedVersion"] != null && json["expectedVersion"].ToString() != version)
                    return Models.McpResponse.Err(code: "VersionConflict", message: "External Object structure changed; read it again before adding a method.", target: objName);
                var candidate = ExternalObjectContract.BuildMethod(exo, json);
                var requested = ExternalObjectContract.DescribeMethod(candidate);
                var matches = exo.ExternalMethods.Where(m => string.Equals(m.Name, candidate.Name, StringComparison.OrdinalIgnoreCase)).ToList();
                if (matches.Count > 0)
                {
                    if (matches.Count != 1 || !ExternalObjectContract.SameSignature(ExternalObjectContract.DescribeMethod(matches[0]), requested))
                        return Models.McpResponse.Err(code: "ExternalMethodConflict", message: "A method with this name already exists with a different contract. Nothing was changed.", target: objName);
                    return Models.McpResponse.Ok(target: objName, code: "ExternalMethodAlreadyExists", result: new JObject {
                        ["changed"] = false, ["persisted"] = true, ["method"] = ExternalObjectContract.DescribeMethod(matches[0]), ["versionToken"] = version });
                }
                if (json["dryRun"]?.Value<bool>() == true)
                    return Models.McpResponse.Ok(target: objName, code: "ExternalMethodPreview", result: new JObject {
                        ["dryRun"] = true, ["persisted"] = false, ["method"] = requested, ["preservedMethods"] = exo.ExternalMethods.Count, ["versionToken"] = version });
                // Store the full native projection; generic EXOStructure XML omits its collections.
                string snapshotDir = System.IO.Path.Combine(_objectService.GetKbService().GetKbPath(), ".gx", "snapshots", "external-methods");
                System.IO.Directory.CreateDirectory(snapshotDir);
                string snapshot = System.IO.Path.Combine(snapshotDir, obj.Guid.ToString("N") + "-" + Guid.NewGuid().ToString("N") + ".json");
                System.IO.File.WriteAllText(snapshot, before.ToString(), new System.Text.UTF8Encoding(false));
                added = candidate;
                exo.ExternalMethods.Add(added);
                var expected = (JObject)before.DeepClone();
                ((JArray)expected["externalMethods"]).Add(ExternalObjectContract.DescribeMethod(added));
                saveAttempted = true;
                obj.EnsureSave();
                ownedWriteVersion = WriteService.ComputeVersionToken(obj);
                var persisted = _objectService.FindObjectFresh(obj.Guid.ToString("D"), "ExternalObject");
                var actual = ExternalObjectContract.Read(FindPart<EXOStructurePart>(persisted));
                if (!JToken.DeepEquals(expected, actual)) throw new InvalidOperationException("Persisted External Object contract differs from the requested contract or an existing member changed.");
                WriteService.NotePerTargetWrite(objName);
                _objectService.MarkReadCacheDirty(persisted, "EXOStructure");
                return Models.McpResponse.Ok(target: objName, code: "ExternalMethodAdded", result: new JObject {
                    ["object"] = obj.Name, ["member"] = added.Name, ["count"] = exo.ExternalMethods.Count,
                    ["persisted"] = true, ["verified"] = true, ["snapshot"] = snapshot,
                    ["method"] = actual["externalMethods"].Last, ["versionToken"] = ExternalObjectContract.Version(actual) });
            }
            catch (Exception ex)
            {
                bool restored = false;
                string rollbackError = null;
                if (added != null && exo != null)
                {
                    try
                    {
                        if (saveAttempted)
                        {
                            RemoveOwnedExternalMethod(obj, added, ownedWriteVersion);
                        }
                        else exo.ExternalMethods.Remove(added);
                        var reloaded = _objectService.FindObjectFresh(obj.Guid.ToString("D"), "ExternalObject");
                        restored = JToken.DeepEquals(before, ExternalObjectContract.Read(FindPart<EXOStructurePart>(reloaded)));
                    }
                    catch (Exception rollback) { rollbackError = rollback.Message; }
                    _objectService.MarkReadCacheDirty(obj, "EXOStructure");
                }
                return Models.McpResponse.Err(code: ex is ArgumentException || ex is Newtonsoft.Json.JsonException ? "InvalidPayload" : "ExternalMemberAddFailed",
                    message: ex.Message, target: objName, extra: new JObject {
                        ["saveAttempted"] = saveAttempted, ["rollbackVerified"] = restored, ["rollbackError"] = rollbackError });
            }
        }

        private void RemoveOwnedExternalMethod(KBObject owner, ExternalObjectMethod added, string ownedWriteVersion)
        {
            using (var transaction = owner.Model.KB.BeginTransaction())
            {
                var current = _objectService.FindObjectFreshByIdentity(owner);
                if (current == null || string.IsNullOrWhiteSpace(ownedWriteVersion)
                    || !string.Equals(ownedWriteVersion, WriteService.ComputeVersionToken(current), StringComparison.Ordinal))
                    throw new InvalidOperationException("External Object rollback refused: the current persisted version cannot be attributed to this write.");
                var currentPart = FindPart<EXOStructurePart>(current);
                var owned = currentPart?.ExternalMethods.SingleOrDefault(m => string.Equals(m.Name, added.Name, StringComparison.OrdinalIgnoreCase));
                if (owned == null || !ExternalObjectContract.SameSignature(ExternalObjectContract.DescribeMethod(owned), ExternalObjectContract.DescribeMethod(added)))
                    throw new InvalidOperationException("External Object rollback refused: the added member changed or cannot be identified.");
                currentPart.ExternalMethods.Remove(owned);
                current.EnsureSave();
                transaction.Commit();
            }
        }

        // payload = { name, type? }
        public string AddExternalProperty(string objName, string payload)
        {
            return AddExternalMember(objName, payload, isMethod: false);
        }

        private string AddExternalMember(string objName, string payload, bool isMethod)
        {
            try
            {
                var obj = _objectService.FindObject(objName);
                if (obj == null) return HealingService.FormatNotFoundError(objName, _objectService.GetKbService().GetIndexCache().GetIndex());
                if (!(obj is ExternalObject)) return Models.McpResponse.Err(
                    code: "NotAnExternalObject",
                    message: $"'{objName}' is not an External Object.",
                    target: objName);

                var exo = FindPart<EXOStructurePart>(obj);
                if (exo == null) return Models.McpResponse.Err(
                    code: "StructurePartNotFound",
                    message: "External Object has no structure part.",
                    target: objName);

                var json = string.IsNullOrWhiteSpace(payload) ? new JObject() : JObject.Parse(payload);
                string memberName = json["name"]?.ToString();
                if (string.IsNullOrWhiteSpace(memberName)) return Models.McpResponse.Err(
                    code: "InvalidPayload", message: "payload.name is required.", target: objName);

                try
                {
                    if (isMethod)
                    {
                        var m = new ExternalObjectMethod(exo) { Name = memberName };
                        string rt = json["returnType"]?.ToString();
                        if (!string.IsNullOrWhiteSpace(rt)) m.ExternalType = rt;

                        if (json["parameters"] is JArray pars)
                        {
                            foreach (var p in pars.OfType<JObject>())
                            {
                                string pn = p["name"]?.ToString();
                                if (string.IsNullOrWhiteSpace(pn)) continue;
                                var par = new ExternalObjectParameter(m) { Name = pn };
                                string pt = p["type"]?.ToString();
                                if (!string.IsNullOrWhiteSpace(pt)) par.ExternalType = pt;
                                m.AddParameter(par);
                            }
                        }
                        exo.ExternalMethods.Add(m);
                    }
                    else
                    {
                        var prop = new ExternalObjectProperty(exo) { Name = memberName };
                        string pt = json["type"]?.ToString();
                        if (!string.IsNullOrWhiteSpace(pt)) prop.ExternalType = pt;
                        exo.ExternalProperties.Add(prop);
                    }

                    obj.EnsureSave();
                    WriteService.NotePerTargetWrite(objName);
                    int count = isMethod ? exo.ExternalMethods.Count : exo.ExternalProperties.Count;
                    return Models.McpResponse.Ok(
                        target: objName,
                        code: isMethod ? "ExternalMethodAdded" : "ExternalPropertyAdded",
                        result: new JObject { ["object"] = obj.Name, ["member"] = memberName, ["count"] = count });
                }
                catch (Exception ex)
                {
                    return Models.McpResponse.Err(
                        code: "ExternalMemberAddFailed",
                        message: ex.Message,
                        hint: "Check the worker log for the SDK stack trace.",
                        target: objName,
                        extra: new JObject { ["stackTrace"] = ex.StackTrace });
                }
            }
            catch (Exception ex)
            {
                return Models.McpResponse.Err(
                    code: "ExternalMemberAddFailed", message: ex.Message,
                    hint: "Ensure the object is an External Object and payload is valid JSON.",
                    target: objName);
            }
        }

        // payload = { description, target?, optionCode? }. target = a KB object to call from the option.
        public string AddMenuOption(string objName, string payload)
        {
            try
            {
                var obj = _objectService.FindObject(objName);
                if (obj == null) return HealingService.FormatNotFoundError(objName, _objectService.GetKbService().GetIndexCache().GetIndex());
                if (!(obj is Menu menu)) return Models.McpResponse.Err(
                    code: "NotAMenu", message: $"'{objName}' is not a Menu.", target: objName);

                var menuPart = FindPart<MenuPart>(obj);
                if (menuPart == null) return Models.McpResponse.Err(
                    code: "MenuPartNotFound", message: "Menu has no part.", target: objName);

                var json = string.IsNullOrWhiteSpace(payload) ? new JObject() : JObject.Parse(payload);
                string description = json["description"]?.ToString();
                if (string.IsNullOrWhiteSpace(description)) return Models.McpResponse.Err(
                    code: "InvalidPayload", message: "payload.description is required.", target: objName);

                KBObject targetObj = null;
                string targetName = json["target"]?.ToString();
                if (!string.IsNullOrWhiteSpace(targetName))
                {
                    targetObj = _objectService.FindObject(targetName);
                    if (targetObj == null) return Models.McpResponse.Err(
                        code: "TargetNotFound",
                        message: $"Menu option target '{targetName}' not found.",
                        target: objName);
                }

                // OptionCode is an int; auto-assign next free code when not supplied.
                int optionCode;
                if (json["optionCode"] != null) optionCode = json["optionCode"].ToObject<int>();
                else optionCode = menuPart.Options.Count == 0 ? 1 : menuPart.Options.Max(o => o.OptionCode) + 1;

                try
                {
                    var opt = new MenuOption(menu) { OptionCode = optionCode, Description = description };
                    if (targetObj != null) opt.GenexusObject = new KBObjectReference(targetObj);
                    menuPart.Options.Add(opt);

                    obj.EnsureSave();
                    WriteService.NotePerTargetWrite(objName);
                    return Models.McpResponse.Ok(
                        target: objName,
                        code: "MenuOptionAdded",
                        result: new JObject
                        {
                            ["menu"] = obj.Name,
                            ["optionCode"] = optionCode,
                            ["description"] = description,
                            ["target"] = targetObj?.Name
                        });
                }
                catch (Exception ex)
                {
                    return Models.McpResponse.Err(
                        code: "MenuOptionAddFailed",
                        message: ex.Message,
                        hint: "Check the worker log for the SDK stack trace.",
                        target: objName,
                        extra: new JObject { ["stackTrace"] = ex.StackTrace });
                }
            }
            catch (Exception ex)
            {
                return Models.McpResponse.Err(
                    code: "MenuOptionAddFailed", message: ex.Message,
                    hint: "Ensure the object is a Menu and payload is valid JSON.",
                    target: objName);
            }
        }

        // issue #39 batch 3: add a filter condition to a Data Selector. A condition is just a
        // GeneXus source expression (e.g. "CategoryId = &CategoryId"). payload = { source }.
        public string AddDataSelectorCondition(string objName, string payload)
        {
            try
            {
                var obj = _objectService.FindObject(objName);
                if (obj == null) return HealingService.FormatNotFoundError(objName, _objectService.GetKbService().GetIndexCache().GetIndex());
                if (!(obj is DataSelector)) return Models.McpResponse.Err(
                    code: "NotADataSelector", message: $"'{objName}' is not a Data Selector.", target: objName);

                var part = FindPart<DataSelectorStructurePart>(obj);
                if (part == null) return Models.McpResponse.Err(
                    code: "StructurePartNotFound", message: "Data Selector has no structure part.", target: objName);

                var json = string.IsNullOrWhiteSpace(payload) ? new JObject() : JObject.Parse(payload);
                string source = json["source"]?.ToString();
                if (string.IsNullOrWhiteSpace(source)) return Models.McpResponse.Err(
                    code: "InvalidPayload", message: "payload.source is required.",
                    hint: "e.g. { \"source\": \"CategoryId = &CategoryId\" }.", target: objName);

                try
                {
                    if (part.Root == null) part.Root = new DataSelectorLevel(part);
                    part.Root.AddCondition(source);
                    obj.EnsureSave();
                    WriteService.NotePerTargetWrite(objName);
                    int count = part.Root.Conditions?.Count() ?? 0;
                    return Models.McpResponse.Ok(
                        target: objName,
                        code: "DataSelectorConditionAdded",
                        result: new JObject { ["dataSelector"] = obj.Name, ["source"] = source, ["count"] = count });
                }
                catch (Exception ex)
                {
                    return Models.McpResponse.Err(
                        code: "DataSelectorConditionAddFailed", message: ex.Message,
                        hint: "Check the worker log. Verify the source expression references valid attributes/variables.",
                        target: objName, extra: new JObject { ["stackTrace"] = ex.StackTrace });
                }
            }
            catch (Exception ex)
            {
                return Models.McpResponse.Err(
                    code: "DataSelectorConditionAddFailed", message: ex.Message,
                    hint: "Ensure the object is a Data Selector and payload is valid JSON.", target: objName);
            }
        }

    }
}
