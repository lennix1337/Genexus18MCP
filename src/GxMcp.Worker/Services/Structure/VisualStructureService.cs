using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Artech.Architecture.Common.Descriptors;
using Artech.Architecture.Common.Objects;
using Artech.Genexus.Common.Objects;
using Artech.Genexus.Common.Parts;
using GxMcp.Worker.Helpers;

namespace GxMcp.Worker.Services.Structure
{
    public class VisualStructureService
    {
        private readonly ObjectService _objectService;
        private readonly HashSet<KBObject> _modifiedObjects = new HashSet<KBObject>();
        private readonly List<string> _problems = new List<string>();
        private readonly List<string> _createdAttributes = new List<string>();
        private KBModel _model;

        /// <summary>
        /// Members of the last SyncVisualStructure call the SDK did not accept (issue #955).
        /// A requested member that cannot be applied is reported with its reason, never skipped.
        /// </summary>
        public IReadOnlyList<string> Problems => _problems;

        /// <summary>Global Attributes the last SyncVisualStructure call created.</summary>
        public IReadOnlyList<string> CreatedAttributes => _createdAttributes;

        public VisualStructureService(ObjectService objectService)
        {
            _objectService = objectService;
        }

        public JArray SerializeVisualLevel(TransactionLevel level)
        {
            var children = new JArray();
            if (level == null) return children;

            try {
                Logger.Info($"[VisualStructureService] Serializing level: {level.Name}");
                
                var attributes = level.Attributes;
                if (attributes != null) { 
                    Logger.Info($"[VisualStructureService] Processing {attributes.Count} attributes in {level.Name}");
                    foreach (TransactionAttribute attr in attributes) {
                        try {
                            children.Add(VisualStructureMapper.MapAttribute(attr));
                        } catch (Exception ex) {
                            Logger.Error($"[VisualStructureService] Error mapping attribute in {level.Name}: {ex.Message}");
                        }
                    }
                }

                var subLevels = level.Levels;
                if (subLevels != null)
                {
                    Logger.Info($"[VisualStructureService] Processing {subLevels.Count} sub-levels in {level.Name}");
                    foreach (TransactionLevel subLevel in subLevels) {
                        try {
                            var levelItem = new JObject { 
                                ["name"] = subLevel.Name, 
                                ["isLevel"] = true, 
                                ["children"] = SerializeVisualLevel(subLevel) 
                            };
                            children.Add(levelItem);
                        } catch (Exception ex) {
                            Logger.Error($"[VisualStructureService] Error mapping sub-level {subLevel.Name} in {level.Name}: {ex.Message}");
                        }
                    }
                }
            } catch (Exception ex) {
                Logger.Error($"[VisualStructureService] Fatal error serializing level: {ex.Message}");
            }
            return children;
        }

        public void SyncVisualStructure(Transaction trn, JArray visualItems)
        {
            _modifiedObjects.Clear();
            _problems.Clear();
            _createdAttributes.Clear();
            _model = trn.Model;
            SyncVisualLevel(trn.Structure.Root, visualItems);
            
            // Batch Save: Salva cada objeto modificado apenas uma vez
            foreach (var obj in _modifiedObjects) {
                try { obj.Save(); }
                catch (Exception ex)
                {
                    Logger.Error($"Failed to save modified object {obj.Name}: {ex.Message}");
                    _problems.Add($"attribute '{obj.Name}' changes were not saved: {(ex.InnerException ?? ex).Message}");
                    continue;
                }
                WriteService.NotePerTargetWrite(obj.Name);
            }
        }

        // The Transaction structure can only reference an existing global Attribute, so a
        // requested member that does not exist yet is created first, typed from the request
        // (same order as the DSL path: type before Save).
        private Artech.Genexus.Common.Objects.Attribute CreateGlobalAttribute(string name, JToken vItem)
        {
            string type = vItem["type"]?.ToString();
            if (string.IsNullOrWhiteSpace(type))
            {
                _problems.Add($"attribute '{name}' does not exist in the KB and the request gives no 'type' to create it.");
                return null;
            }
            try
            {
                var created = KBObject.Create(_model, KBObjectDescriptor.Get<Artech.Genexus.Common.Objects.Attribute>().Id)
                    as Artech.Genexus.Common.Objects.Attribute;
                if (created == null)
                {
                    _problems.Add($"attribute '{name}' could not be created: the SDK returned no Attribute.");
                    return null;
                }
                created.Name = name;
                if (!AttributeTypeApplier.ApplyFromDslType(created, type, _model))
                {
                    _problems.Add($"attribute '{name}' could not be created: type '{type}' was not applied.");
                    return null;
                }
                string description = vItem["description"]?.ToString();
                if (!string.IsNullOrWhiteSpace(description)) created.Description = description;
                created.Save();
                _createdAttributes.Add(name);
                return created;
            }
            catch (Exception ex)
            {
                Logger.Error($"[VisualStructureService] creating attribute '{name}' failed: {ex.Message}");
                _problems.Add($"attribute '{name}' could not be created: {(ex.InnerException ?? ex).Message}");
                return null;
            }
        }

        private void SyncVisualLevel(TransactionLevel sdkLevel, JArray visualItems)
        {
            var visualNames = new HashSet<string>(visualItems.Select(v => v["name"]?.ToString()), StringComparer.OrdinalIgnoreCase);
            
            if (sdkLevel.Attributes != null)
            {
                var toRemove = new List<dynamic>();
                foreach (dynamic attr in sdkLevel.Attributes) { if (!visualNames.Contains(attr.Name)) toRemove.Add(attr); }
                foreach (dynamic dead in toRemove) { try { sdkLevel.Attributes.Remove(dead); } catch { } }
            }
            if (sdkLevel.Levels != null)
            {
                var toRemove = new List<dynamic>();
                foreach (dynamic lvl in sdkLevel.Levels) { if (!visualNames.Contains(lvl.Name)) toRemove.Add(lvl); }
                foreach (dynamic dead in toRemove) { try { sdkLevel.Levels.Remove(dead); } catch { } }
            }

            foreach (var vItem in visualItems)
            {
                string name = vItem["name"]?.ToString();
                if (string.IsNullOrEmpty(name)) continue;
                bool isLevel = (bool?)vItem["isLevel"] ?? false;

                if (isLevel) {
                    TransactionLevel targetLevel = null;
                    if (sdkLevel.Levels != null) {
                        foreach (dynamic lvl in sdkLevel.Levels) { if (lvl.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) { targetLevel = lvl; break; } }
                    }
                    if (targetLevel == null) {
                        // Use the (TransactionLevel parent) ctor + typed AddLevel(level) so the SDK
                        // wires the new level into the structure with the bookkeeping EnsureSave honors.
                        // The previous `new TransactionLevel(sdkLevel.Structure)` + Levels.Add(...) pattern
                        // silently dropped the new sub-level (mirror of commit 8c8f433's attribute fix).
                        targetLevel = new TransactionLevel(sdkLevel);
                        targetLevel.Name = name;
                        try { sdkLevel.AddLevel(targetLevel); }
                        catch (Exception ex)
                        {
                            Logger.Error($"[VisualStructureService] AddLevel('{name}') failed: {ex.Message}");
                            _problems.Add($"level '{name}' was not added: {(ex.InnerException ?? ex).Message}");
                            continue;
                        }
                    }
                    var children = vItem["children"] as JArray;
                    if (children != null) SyncVisualLevel(targetLevel, children);
                } else {
                    TransactionAttribute targetAttr = null;
                    if (sdkLevel.Attributes != null) {
                        foreach (dynamic attr in sdkLevel.Attributes) { if (attr.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) { targetAttr = attr; break; } }
                    }
                    if (targetAttr == null) {
                        var attrObj = _objectService.FindObject(name) as Artech.Genexus.Common.Objects.Attribute
                            ?? CreateGlobalAttribute(name, vItem);
                        if (attrObj != null) {
                            // Use the typed sdkLevel.AddAttribute(globalAttr) overload — it is the only
                            // path that links the new TransactionAttribute into the structure with the
                            // bookkeeping EnsureSave honors. See commit 8c8f433.
                            try { targetAttr = sdkLevel.AddAttribute(attrObj); }
                            catch (Exception ex)
                            {
                                Logger.Error($"[VisualStructureService] AddAttribute('{name}') failed: {ex.Message}");
                                _problems.Add($"attribute '{name}' was not added to the level '{sdkLevel.Name}': {(ex.InnerException ?? ex).Message}");
                                continue;
                            }
                        }
                        else continue;
                    }
                    targetAttr.IsKey = (bool?)vItem["isKey"] ?? false;
                    
                    // Passamos o HashSet para coletar modificações globais no Atributo
                    VisualStructureMapper.SyncAttributeProperties(targetAttr, vItem, _modifiedObjects);
                }
            }
        }
    }
}
