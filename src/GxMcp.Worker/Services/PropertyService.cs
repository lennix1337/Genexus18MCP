using System;
using System.Collections.Generic;
using System.Linq;
using Artech.Architecture.Common.Objects;
using Artech.Genexus.Common.Objects;
using Artech.Common.Properties;
using Artech.Genexus.Common.Parts;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Helpers;

namespace GxMcp.Worker.Services
{
    public class PropertyService
    {
        private readonly ObjectService _objectService;

        // Small TTL cache for GetProperties — cold path on certain object kinds
        // (Domain, External Object) hits 3s the first time because the SDK lazily
        // hydrates property definitions via reflection. Subsequent reads of the
        // same object usually want the same envelope, so we cache by GUID for a
        // few seconds. SetProperty invalidates the entry explicitly.
        private static readonly Dictionary<string, (DateTime expiresAt, string json)> _propertyCache
            = new Dictionary<string, (DateTime, string)>(StringComparer.OrdinalIgnoreCase);
        private static readonly object _propertyCacheLock = new object();
        private const int PropertyCacheTtlSeconds = 30;

        public PropertyService(ObjectService objectService)
        {
            _objectService = objectService;
        }

        private static string CacheKey(KBObject obj, string controlName)
        {
            string guid;
            try { guid = obj.Guid.ToString(); } catch { guid = obj.Name ?? "?"; }
            return guid + "|" + (controlName ?? "");
        }

        internal static void InvalidatePropertyCache(KBObject obj)
        {
            if (obj == null) return;
            string guid;
            try { guid = obj.Guid.ToString(); } catch { return; }
            lock (_propertyCacheLock)
            {
                var keys = _propertyCache.Keys.Where(k => k.StartsWith(guid + "|", StringComparison.OrdinalIgnoreCase)).ToList();
                foreach (var k in keys) _propertyCache.Remove(k);
            }
        }

        public string GetProperties(string target, string controlName = null, string typeFilter = null)
        {
            try
            {
                var obj = _objectService.FindObject(target, typeFilter);
                if (obj == null) return Models.McpResponse.Err(code: "ObjectNotFound", message: "Object not found.", hint: "Check the target name and that the KB is open.", nextSteps: new JArray(Models.McpResponse.NextStep("genexus_list_objects", null, "Lists available objects to verify the target name.")), target: target);

                string ck = CacheKey(obj, controlName);
                lock (_propertyCacheLock)
                {
                    if (_propertyCache.TryGetValue(ck, out var hit) && hit.expiresAt > DateTime.UtcNow)
                        return hit.json;
                }

                dynamic container = obj;
                if (!string.IsNullOrEmpty(controlName))
                {
                    container = FindControl(obj, controlName);
                    if (container == null) return Models.McpResponse.Err(code: "ControlNotFound", message: $"Control '{controlName}' not found in {obj.Name}.", hint: "Use genexus_inspect to list controls available in this object's layout.", nextSteps: new JArray(Models.McpResponse.NextStep("genexus_inspect", new JObject { ["name"] = target }, "Returns the layout controls for this object.")), target: target);
                }

                var propsResult = SerializeProperties(container);
                string json = Models.McpResponse.Ok(target: target, code: "PropertiesRead", result: propsResult);
                lock (_propertyCacheLock)
                {
                    _propertyCache[ck] = (DateTime.UtcNow.AddSeconds(PropertyCacheTtlSeconds), json);
                }
                return json;
            }
            catch (Exception ex)
            {
                return "{\"status\":\"Error\",\"message\": \"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        public string SetProperty(string target, string propName, string value, string controlName = null, string typeFilter = null)
        {
            try
            {
                var obj = _objectService.FindObject(target, typeFilter);
                if (obj == null) return Models.McpResponse.Err(code: "ObjectNotFound", message: "Object not found.", hint: "Check the target name and that the KB is open.", nextSteps: new JArray(Models.McpResponse.NextStep("genexus_list_objects", null, "Lists available objects to verify the target name.")), target: target);

                // Folder/module placement is not a scalar property write — it's a parent
                // move. Route it to ObjectService.MoveObject (create-in-Root then reparent
                // via the Udm EntityManager). The old "not writable — Parent/Module setters
                // are no-ops" verdict was a facade-DLL decompilation artefact; see ObjectMover.
                if (string.IsNullOrEmpty(controlName) && IsObjectPlacementProperty(propName))
                {
                    string kind = propName.Trim().StartsWith("Module", StringComparison.OrdinalIgnoreCase) ? "Module"
                        : propName.Trim().StartsWith("Folder", StringComparison.OrdinalIgnoreCase) ? "Folder" : null;
                    return _objectService.MoveObject(target, value, typeFilter, kind);
                }

                // issue #49: renaming an object by setting its "Name" via the generic property
                // setter half-works — obj.Name = newName persists to the KB, but the index cache
                // is never re-keyed, so the object is unreachable under the new name afterwards
                // (get returns ObjectNotFound) and a subsequent rename can orphan the old state.
                // Renaming is a refactor (it must also patch every caller), not a scalar write.
                // Reject it and route to the operation that does it correctly.
                if (string.IsNullOrEmpty(controlName) && string.Equals(propName?.Trim(), "Name", StringComparison.OrdinalIgnoreCase))
                {
                    return Models.McpResponse.Err(
                        code: "RenameNotViaProperties",
                        message: $"Cannot rename '{target}' by setting the 'Name' property: this only re-keys the object in memory, leaving the index stale (the object becomes unreachable under the new name) and does not update references from other objects.",
                        hint: "Use genexus_refactor action=RenameObject, which renames the object, patches its call-sites, and refreshes the index.",
                        nextSteps: new JArray(Models.McpResponse.NextStep("genexus_refactor", new JObject { ["action"] = "RenameObject", ["target"] = target, ["newName"] = value, ["type"] = typeFilter }, "Renames the object and patches every reference to it.")),
                        target: target);
                }

                // issue #41: ControlValues is a STRUCTURED property (the static combo's
                // list of value/description pairs), not a scalar. The generic string setter
                // can't represent it, so the SDK writes an empty collection — silently
                // WIPING the existing values while the call still "succeeds". Reject up
                // front instead of destroying data and reporting PropertyApplied.
                if (IsNonScalarProperty(propName))
                {
                    return Models.McpResponse.Err(
                        code: "PropertyNotScalarWritable",
                        message: $"'{propName}' is a structured property (a list of value/description pairs), not a scalar. Setting it through genexus_properties would write an empty collection and WIPE the existing values.",
                        hint: "Edit the static values in the GeneXus IDE (the control/attribute's Control Info > Values editor). This property is not writable as a plain string through the SDK.",
                        nextSteps: new JArray(Models.McpResponse.NextStep("genexus_properties", new JObject { ["action"] = "get", ["name"] = target, ["control"] = controlName }, "Read the current ControlValues so you don't lose them.")),
                        target: target);
                }

                // issue #48: a Data Provider's OutputSDT is a READ-ONLY derived string
                // ("OutputSDT:String {get;}") — the real output is a DataProviderOutputReference
                // set via Properties.DPRV.SetOutput. The generic string setter can't write the
                // get-only property, so it silently cleared the output while reporting success.
                // Route to the typed SDK API instead.
                if (string.IsNullOrEmpty(controlName)
                    && string.Equals(propName?.Trim(), "OutputSDT", StringComparison.OrdinalIgnoreCase))
                {
                    return SetDataProviderOutputSdt(obj, target, value);
                }

                // issue #117: Domain assignment on Attribute/Domain/Variable routes through
                // DomainBasedOn. Validate domain existence up front if non-empty.
                if (string.IsNullOrEmpty(controlName) && IsDomainPropertyName(propName) && !string.IsNullOrWhiteSpace(value))
                {
                    var domCheck = _objectService.FindObject(value.Trim(), "Domain");
                    if (domCheck == null)
                    {
                        return Models.McpResponse.Err(
                            code: "DomainNotFound",
                            message: $"Cannot set {propName}: no Domain named '{value}' was found in the Knowledge Base.",
                            hint: "Pass the name of an existing Domain (or an empty value to clear).",
                            nextSteps: new JArray(Models.McpResponse.NextStep("genexus_list_objects", new JObject { ["type"] = "Domain", ["query"] = value }, "Lists Domains matching the name.")),
                            target: target);
                    }
                }

                dynamic container = obj;
                if (!string.IsNullOrEmpty(controlName))
                {
                    container = FindControl(obj, controlName);
                    if (container == null) return Models.McpResponse.Err(code: "ControlNotFound", message: $"Control '{controlName}' not found in {obj.Name}.", hint: "Use genexus_inspect to list controls available in this object's layout.", nextSteps: new JArray(Models.McpResponse.NextStep("genexus_inspect", new JObject { ["name"] = target }, "Returns the layout controls for this object.")), target: target);
                }

                string propertyValidation = ValidatePropertyWrite(container, propName, value);
                if (propertyValidation != null)
                    return Models.McpResponse.Err(
                        code: propertyValidation.StartsWith("PropertyReadOnly", StringComparison.Ordinal)
                            ? "PropertyReadOnly"
                            : propertyValidation.StartsWith("PropertyNotFound", StringComparison.Ordinal)
                                ? "PropertyNotFound"
                                : "InvalidPropertyValue",
                        message: propertyValidation,
                        target: target);

                string beforeVal = null;
                using (var trans = obj.Model.KB.BeginTransaction())
                {
                    bool committed = false;
                    try
                    {
                        // issue #41 (general safety net): capture the prior value so a silent
                        // wipe (non-empty → empty for a non-empty request) is caught even for
                        // properties not on the explicit non-scalar list, and rolled back.
                        beforeVal = TryReadPropertyString(container, propName);

                        ApplyPropertyValue(container, propName, value, controlName, obj);

                        string afterVal = TryReadPropertyString(container, propName);
                        if (!string.IsNullOrEmpty(value)
                            && !string.IsNullOrEmpty(beforeVal)
                            && string.IsNullOrEmpty(afterVal))
                        {
                            throw new PropertyWipeException(propName, beforeVal);
                        }

                        try { if (container != obj) container.Dirty = true; } catch { }
                        obj.EnsureSave();
                        trans.Commit();
                        committed = true;
                        InvalidatePropertyCache(obj);
                    }
                    finally
                    {
                        if (!committed)
                        {
                            try { trans.Rollback(); } catch (Exception rbEx) { Logger.Warn("[PROPERTY] Rollback failed: " + rbEx.Message); }
                        }
                    }
                }

                // issue #59 — post-save persistence verification. Re-resolve the object and
                // the target container FRESH (not the mutated in-memory instance) and compare
                // the persisted value against what was requested. A mismatch returns the
                // structured PropertyNotPersisted envelope; a confirmed match attaches the
                // before/requested/persisted diff block to the success response.
                string verified = VerifyPropertyPersisted(target, propName, value, controlName, typeFilter, beforeVal, obj);
                if (verified != null) return verified;

                return Models.McpResponse.Ok(target: target, code: "PropertyApplied", result: new JObject { ["property"] = propName, ["value"] = value });
            }
            catch (PropertyWipeException pwe)
            {
                // issue #41: the write was rolled back — the property still has its prior value.
                return Models.McpResponse.Err(
                    code: "PropertyWriteWipedValue",
                    message: $"Setting '{pwe.PropertyName}' emptied a previously non-empty value; the write was rolled back to avoid data loss. This property is likely structured (not a scalar string) and can't be set through genexus_properties.",
                    hint: "The prior value is preserved. Edit this property in the GeneXus IDE. If it should be scalar-writable, report the property name.",
                    target: target);
            }
            catch (Exception ex)
            {
                return "{\"status\":\"Error\",\"message\": \"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        public string SetProperties(string target, JObject properties, string controlName = null, string typeFilter = null)
        {
            try
            {
                if (properties == null || properties.Count == 0)
                    return Models.McpResponse.Ok(target: target, code: "WriteNoChange", result: new JObject { ["details"] = "No properties provided." });

                var obj = _objectService.FindObject(target, typeFilter);
                if (obj == null) return Models.McpResponse.Err(code: "ObjectNotFound", message: "Object not found.", hint: "Check the target name and that the KB is open.", nextSteps: new JArray(Models.McpResponse.NextStep("genexus_list_objects", null, "Lists available objects to verify the target name.")), target: target);

                dynamic container = obj;
                if (!string.IsNullOrEmpty(controlName))
                {
                    container = FindControl(obj, controlName);
                    if (container == null) return Models.McpResponse.Err(code: "ControlNotFound", message: $"Control '{controlName}' not found in {obj.Name}.", hint: "Use genexus_inspect to list controls available in this object's layout.", nextSteps: new JArray(Models.McpResponse.NextStep("genexus_inspect", new JObject { ["name"] = target }, "Returns the layout controls for this object.")), target: target);
                }

                var beforeValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                using (var trans = obj.Model.KB.BeginTransaction())
                {
                    bool committed = false;
                    try
                    {
                        foreach (var p in properties.Properties())
                        {
                            string propName = p.Name;
                            string val = p.Value?.ToString();

                            if (string.IsNullOrEmpty(controlName) && IsObjectPlacementProperty(propName))
                                continue;
                            if (string.IsNullOrEmpty(controlName) && string.Equals(propName?.Trim(), "Name", StringComparison.OrdinalIgnoreCase))
                                throw new InvalidOperationException($"Cannot rename '{target}' via property batch setter.");
                            if (IsNonScalarProperty(propName))
                                throw new InvalidOperationException($"'{propName}' is a structured property and cannot be set as a scalar string.");

                            string propertyValidation = ValidatePropertyWrite(container, propName, val);
                            if (propertyValidation != null)
                                throw new InvalidOperationException(propertyValidation);

                            string before = TryReadPropertyString(container, propName);
                            if (before != null) beforeValues[propName] = before;

                            ApplyPropertyValue(container, propName, val, controlName, obj);

                            string after = TryReadPropertyString(container, propName);
                            if (!string.IsNullOrEmpty(val) && !string.IsNullOrEmpty(before) && string.IsNullOrEmpty(after))
                            {
                                throw new PropertyWipeException(propName, before);
                            }
                        }

                        try { if (container != obj) container.Dirty = true; } catch { }
                        obj.EnsureSave();
                        trans.Commit();
                        committed = true;
                        InvalidatePropertyCache(obj);
                    }
                    finally
                    {
                        if (!committed)
                        {
                            try { trans.Rollback(); } catch (Exception rbEx) { Logger.Warn("[PROPERTY] Rollback failed: " + rbEx.Message); }
                        }
                    }
                }

                return Models.McpResponse.Ok(target: target, code: "PropertiesApplied", result: new JObject { ["properties"] = properties });
            }
            catch (PropertyWipeException pwe)
            {
                return Models.McpResponse.Err(
                    code: "PropertyWriteWipedValue",
                    message: $"Setting '{pwe.PropertyName}' emptied a previously non-empty value; the write was rolled back to avoid data loss.",
                    target: target);
            }
            catch (Exception ex)
            {
                return "{\"status\":\"Error\",\"message\": \"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        internal static void ApplyPropertiesDirect(KBObject obj, JObject properties, dynamic container = null)
        {
            if (obj == null || properties == null || properties.Count == 0) return;
            dynamic targetContainer = container ?? obj;
            foreach (var p in properties.Properties())
            {
                string pName = p.Name;
                string pVal = p.Value?.ToString();
                string propertyValidation = ValidatePropertyWrite(targetContainer, pName, pVal);
                if (propertyValidation != null) throw new InvalidOperationException(propertyValidation);
                ApplyPropertyValue(targetContainer, pName, pVal, null, obj);
            }
        }

        /// <summary>
        /// Resolve a property descriptor before opening an SDK transaction. This
        /// keeps read-only properties and malformed scalar values at zero saves;
        /// the older setter path only discovered both failures after invoking SDK
        /// conversion and could return an opaque error envelope.
        /// </summary>
        internal static string ValidatePropertyWrite(dynamic container, string propName, string rawValue)
        {
            if (container == null || string.IsNullOrWhiteSpace(propName))
                return "PropertyNotFound: a property name is required.";
            // These names have dedicated typed SDK adapters below and are not
            // scalar descriptor conversions.
            if (IsDomainPropertyName(propName)
                || IsNullablePropertyName(propName)
                || string.Equals(propName.Trim(), "OutputSDT", StringComparison.OrdinalIgnoreCase))
                return null;

            dynamic descriptor = null;
            bool enumerated = false;
            try
            {
                foreach (dynamic property in container.Properties)
                {
                    enumerated = true;
                    string name = null;
                    try { name = property.Name?.ToString(); } catch { }
                    if (string.Equals(name, propName, StringComparison.OrdinalIgnoreCase))
                    {
                        descriptor = property;
                        break;
                    }
                }
            }
            catch
            {
                // Some SDK bags expose only SetPropertyValue and no enumerable
                // descriptor collection. Leave those to the proven setter path.
                return null;
            }

            if (descriptor == null)
                return enumerated ? $"PropertyNotFound: '{propName}' is not exposed by this object or control." : null;

            try
            {
                if (descriptor.Definition?.ReadOnly == true)
                    return $"PropertyReadOnly: '{propName}' is read-only for this object or control.";
            }
            catch { }

            Type targetType = null;
            try
            {
                if (descriptor.Definition?.Type is Type definitionType) targetType = definitionType;
            }
            catch { }
            if (targetType == null)
            {
                try
                {
                    object current = descriptor.Value;
                    if (current != null) targetType = current.GetType();
                }
                catch { }
            }

            if (targetType != null && targetType != typeof(string)
                && !TryConvertToType(rawValue, targetType, out _))
                return $"InvalidPropertyValue: '{rawValue}' cannot be converted to {targetType.Name} for '{propName}'.";

            return null;
        }

        // issue #59 — re-read the property from a freshly-resolved object after the SDK
        // commit and confirm the requested value persisted. Returns either:
        //   - an error envelope (PropertyNotPersisted) when the re-read disagrees,
        //   - the success envelope decorated with before/requested/persisted when confirmed,
        //   - null when the fresh re-read is impossible (unverifiable → keep the original
        //     response; never falsely accuse a write that can't be checked).
        private string VerifyPropertyPersisted(string target, string propName, string requested, string controlName, string typeFilter, string beforeVal, KBObject original)
        {
            try
            {
                var fresh = _objectService.FindObject(target, typeFilter);
                if (fresh == null) return null;

                // Circularity guard: if the SDK hands back the SAME in-memory instance we
                // just mutated (rather than a fresh object from the KB store), a re-read
                // trivially matches the requested value and proves nothing. Treat identity
                // as "unverifiable" — never claim a confirmation we didn't obtain.
                if (original != null && object.ReferenceEquals(fresh, original)) return null;

                dynamic container = fresh;
                if (!string.IsNullOrEmpty(controlName))
                {
                    container = FindControl(fresh, controlName);
                    if (container == null) return null;
                }

                string persisted = null;
                // Nullable/ALLOWNULL family (issue #57): the property-bag entry may be named
                // IsNullable even when the caller used Nullable — read the typed getter.
                if (IsNullablePropertyName(propName))
                {
                    try
                    {
                        dynamic inl = container.IsNullable;
                        if (inl != null) persisted = inl.ToString();
                    }
                    catch { /* fall through to the bag read */ }
                }
                // issue #117: Domain assignment on Attribute/Domain/Variable routes through DomainBasedOn
                if (IsDomainPropertyName(propName))
                {
                    try
                    {
                        string domName = DomainPropertyApplier.GetDomainBasedOnName((object)container);
                        persisted = domName ?? string.Empty;
                    }
                    catch { }
                }
                if (persisted == null) persisted = TryReadPropertyString(container, propName);
                if (persisted == null) return null; // unverifiable

                if (!PersistenceVerifier.ValuesMatch(requested, persisted, IsNullablePropertyName(propName)))
                {
                    return PersistenceVerifier.BuildNotPersistedError(
                        code: "PropertyNotPersisted",
                        target: target,
                        property: propName,
                        requestedValue: requested,
                        previousValue: beforeVal ?? string.Empty,
                        persistedValue: persisted);
                }

                string success = Models.McpResponse.Ok(
                    target: target,
                    code: "PropertyApplied",
                    result: new JObject { ["property"] = propName, ["value"] = requested });
                return PersistenceVerifier.AttachPersistedDiff(success, beforeVal ?? string.Empty, requested, persisted);
            }
            catch (Exception ex)
            {
                Logger.Debug("[PROPERTY-VERIFY] " + ex.Message);
                return null;
            }
        }

        // issue #41: structured properties the generic scalar setter can't represent —
        // writing them as a string wipes the underlying collection. Reject up front.
        private static readonly HashSet<string> _nonScalarProps = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ControlValues"
        };

        private static bool IsNonScalarProperty(string propName)
            => !string.IsNullOrEmpty(propName) && _nonScalarProps.Contains(propName.Trim());

        // Best-effort read of a property's current value as a string (for the wipe check).
        private static string TryReadPropertyString(dynamic container, string propName)
        {
            try
            {
                if (IsDomainPropertyName(propName))
                {
                    try
                    {
                        string domName = DomainPropertyApplier.GetDomainBasedOnName((object)container);
                        if (domName != null) return domName;
                    }
                    catch { }
                }

                dynamic existing = null;
                try { existing = container.Properties?[propName]; } catch { }
                if (existing == null)
                {
                    try
                    {
                        foreach (dynamic p in container.Properties)
                        {
                            string n = null;
                            try { n = (string)p.Name; } catch { }
                            if (string.Equals(n, propName, StringComparison.OrdinalIgnoreCase)) { existing = p; break; }
                        }
                    }
                    catch { }
                }
                object val = null;
                try { val = existing?.Value; } catch { }
                return val?.ToString();
            }
            catch { return null; }
        }

        private sealed class PropertyWipeException : Exception
        {
            public string PropertyName { get; }
            public PropertyWipeException(string propName, string priorValue)
                : base($"Property '{propName}' would be wiped (prior value: '{priorValue}').")
            { PropertyName = propName; }
        }

        // Object-level "properties" that actually mean "reparent this object". They are not
        // scalar property writes, so a request naming one is routed to ObjectService.MoveObject
        // (which persists via the Udm EntityManager and re-reads to confirm) rather than being
        // pushed through SetPropertyValue. See ObjectMover for why the direct setters look inert.
        private static readonly HashSet<string> _placementProps = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Folder", "FolderId", "FolderGuid", "Module", "ModuleId", "Parent", "ParentKey", "ParentId"
        };

        private static bool IsObjectPlacementProperty(string propName)
            => !string.IsNullOrEmpty(propName) && _placementProps.Contains(propName.Trim());

        // issue #48: set a Data Provider's output SDT through the typed SDK API. OutputSDT is a
        // read-only derived string; the writable output is a DataProviderOutputReference applied
        // via the static helper Artech.Genexus.Common.Properties+DPRV.SetOutput(IPropertyBag,
        // DataProviderOutputReference). Passing an empty value clears the output.
        private string SetDataProviderOutputSdt(KBObject obj, string target, string value)
        {
            try
            {
                if (!string.Equals(obj.TypeDescriptor?.Name, "DataProvider", StringComparison.OrdinalIgnoreCase))
                    return Models.McpResponse.Err(
                        code: "OutputSdtNotADataProvider",
                        message: $"OutputSDT applies to Data Providers; '{target}' is a {obj.TypeDescriptor?.Name}.",
                        hint: "Set OutputSDT only on a DataProvider object.",
                        target: target);

                KBObject sdt = null;
                if (!string.IsNullOrWhiteSpace(value))
                {
                    sdt = _objectService.FindObject(value.Trim(), "SDT");
                    if (sdt == null)
                        return Models.McpResponse.Err(
                            code: "OutputSdtNotFound",
                            message: $"Cannot set OutputSDT: no SDT named '{value}' was found in the Knowledge Base.",
                            hint: "Pass the name of an existing SDT (or an empty value to clear the output).",
                            nextSteps: new JArray(Models.McpResponse.NextStep("genexus_list_objects", new JObject { ["type"] = "SDT", ["query"] = value }, "Lists SDTs matching the name.")),
                            target: target);
                }

                var commonAsm = typeof(global::Artech.Genexus.Common.Objects.Transaction).Assembly;
                var refType = commonAsm.GetType("Artech.Genexus.Common.CustomTypes.DataProviderOutputReference");
                var dprvType = commonAsm.GetType("Artech.Genexus.Common.Properties+DPRV");
                if (refType == null || dprvType == null)
                    return Models.McpResponse.Err(
                        code: "OutputSdtApiUnavailable",
                        message: "The SDK types for setting a Data Provider output (DataProviderOutputReference / Properties.DPRV) were not found.",
                        hint: "This GeneXus build may differ; report the version from genexus_whoami.",
                        target: target);

                var setOutput = dprvType.GetMethod("SetOutput",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (setOutput == null)
                    return Models.McpResponse.Err(
                        code: "OutputSdtApiUnavailable",
                        message: "Properties.DPRV.SetOutput was not found in this SDK build.",
                        target: target);

                // Build the reference: ctor(KBObject) for a target SDT, or a null reference to clear.
                object outputRef = null;
                if (sdt != null)
                {
                    var ctor = refType.GetConstructor(new[] { typeof(KBObject) })
                               ?? refType.GetConstructor(new[] { typeof(Artech.Architecture.Common.Objects.KBObjectReference) });
                    if (ctor == null)
                        return Models.McpResponse.Err(
                            code: "OutputSdtApiUnavailable",
                            message: "DataProviderOutputReference has no (KBObject) constructor in this SDK build.",
                            target: target);
                    outputRef = ctor.GetParameters()[0].ParameterType == typeof(KBObject)
                        ? ctor.Invoke(new object[] { sdt })
                        : ctor.Invoke(new object[] { new Artech.Architecture.Common.Objects.KBObjectReference(sdt) });
                }

                using (var trans = obj.Model.KB.BeginTransaction())
                {
                    bool committed = false;
                    try
                    {
                        // The IPropertyBag is the KBObject itself (PropertiesObject) — obj.Properties
                        // is only an enumerable projection of descriptors, not the bag.
                        setOutput.Invoke(null, new object[] { obj, outputRef });
                        obj.EnsureSave();
                        trans.Commit();
                        committed = true;
                        InvalidatePropertyCache(obj);
                    }
                    finally
                    {
                        if (!committed) { try { trans.Rollback(); } catch { } }
                    }
                }

                return Models.McpResponse.Ok(target: target, code: "PropertyApplied",
                    result: new JObject { ["property"] = "OutputSDT", ["value"] = sdt?.Name ?? "" });
            }
            catch (Exception ex)
            {
                return Models.McpResponse.Err(
                    code: "OutputSdtWriteFailed",
                    message: ex.InnerException?.Message ?? ex.Message,
                    hint: "Check the worker log for the SDK stack trace.",
                    target: target);
            }
        }

        // Properties on Transaction (and other KBObjects) have heterogeneous underlying CLR types:
        // bool, int, enum, or string. SetPropertyValue(string, object) does not coerce, so passing
        // the raw "True"/"1" string fails with InvalidCastException on non-string properties
        // (e.g. idISBUSINESSCOMPONENT, idISBCEJB). Coerce by inspecting the existing value's type
        // or the property Definition before delegating, then fall back to the string-overload setter
        // (SetPropertyValueString) which the SDK provides for textual input.
        internal static void ApplyPropertyValue(dynamic container, string propName, string rawValue, string controlName, KBObject obj)
        {
            Exception lastError = null;

            // issue #57: Nullable / ALLOWNULL on a Transaction or Table attribute occurrence
            // is the typed TableAttribute.IsNullableValue (False/True/Compatible). The generic
            // string setter can't represent the enum ("Yes" is the converter's display string,
            // not an enum member) and an int assignment to the dynamic property throws a
            // runtime binder error — write the typed value directly. The ALLOWNULL alias is
            // accepted for IDE parity (the KB's own DDL-driven property name).
            if (IsNullablePropertyName(propName)
                && (container is Artech.Genexus.Common.Parts.TableAttribute
                    || container is Artech.Genexus.Common.Parts.TransactionAttribute))
            {
                // TableAttribute.IsNullableValue: False=0, True=1, Compatible=2.
                container.IsNullable = (Artech.Genexus.Common.Parts.TableAttribute.IsNullableValue)ParseIsNullableValue(rawValue);
                return;
            }

            // issue #117: Domain assignment on Attribute/Domain/Variable routes through DomainBasedOn
            if (IsDomainPropertyName(propName))
            {
                if (!string.IsNullOrWhiteSpace(rawValue))
                {
                    object domainObj = null;
                    try
                    {
                        if (obj?.Model != null)
                        {
                            var qName = new Artech.Architecture.Common.Objects.QualifiedName(rawValue.Trim());
                            domainObj = Artech.Genexus.Common.Objects.Domain.Get(obj.Model, qName);
                        }
                    }
                    catch { }
                    if (domainObj != null)
                    {
                        if (DomainPropertyApplier.ApplyDomainBasedOn((object)container, domainObj)) return;
                        if (AttributeTypeApplier.ApplyDomain((object)container, domainObj)) return;
                    }
                }
                else
                {
                    if (DomainPropertyApplier.ClearDomainBasedOn((object)container)) return;
                }
            }

            // 1) Try a coerced typed value via SetPropertyValue(string, object).
            object coerced;
            if (TryCoercePropertyValue(container, propName, rawValue, out coerced))
            {
                try { container.SetPropertyValue(propName, coerced); return; }
                catch (Exception ex) { lastError = ex; }
            }

            // 2) Try the SDK's string-overload setter, which converts strings internally.
            try
            {
                var t = (Type)container.GetType();
                var mi = t.GetMethod("SetPropertyValueString",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance,
                    null, new[] { typeof(string), typeof(string) }, null);
                if (mi != null)
                {
                    mi.Invoke((object)container, new object[] { propName, rawValue });
                    return;
                }
            }
            catch (Exception ex) { lastError = ex; }

            // 3) Last resort: untyped passthrough (original behavior).
            try { container.SetPropertyValue(propName, rawValue); return; }
            catch (Exception ex) { lastError = ex; }

            // 4) Last-last resort: reflection on a public CLR property of the same name.
            try
            {
                var pInfo = ((Type)container.GetType()).GetProperty(propName);
                if (pInfo != null && pInfo.CanWrite)
                {
                    object refValue = TryConvertToType(rawValue, pInfo.PropertyType, out var conv) ? conv : (object)rawValue;
                    pInfo.SetValue((object)container, refValue);
                    return;
                }
            }
            catch (Exception ex) { lastError = ex; }

            throw new Exception($"Property '{propName}' not found or not writable on {controlName ?? obj.Name}. Underlying error: {lastError?.Message}");
        }

        internal static bool IsNullablePropertyName(string propName)
        {
            if (string.IsNullOrEmpty(propName)) return false;
            return propName.Equals("ALLOWNULL", StringComparison.OrdinalIgnoreCase)
                || propName.Equals("Nullable", StringComparison.OrdinalIgnoreCase)
                || propName.Equals("IsNullable", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsDomainPropertyName(string propName)
        {
            if (string.IsNullOrEmpty(propName)) return false;
            string norm = propName.Trim();
            return norm.Equals("Domain", StringComparison.OrdinalIgnoreCase)
                || norm.Equals("DomainBasedOn", StringComparison.OrdinalIgnoreCase)
                || norm.Equals("BasedOn", StringComparison.OrdinalIgnoreCase)
                || norm.Equals("DomainDefinition", StringComparison.OrdinalIgnoreCase);
        }

        // TableAttribute.IsNullableValue: False=0, True=1, Compatible=2. Accepts the
        // canonical strings, the JSON-boolean forms, and numeric values.
        internal static int ParseIsNullableValue(string rawValue)
        {
            string norm = (rawValue ?? "").Trim();
            if (norm.Equals("Yes", StringComparison.OrdinalIgnoreCase)
                || norm.Equals("True", StringComparison.OrdinalIgnoreCase)
                || norm.Equals("Y", StringComparison.OrdinalIgnoreCase)
                || norm == "1")
                return 1;
            if (norm.Equals("Managed", StringComparison.OrdinalIgnoreCase)
                || norm.Equals("Compatible", StringComparison.OrdinalIgnoreCase)
                || norm == "2")
                return 2;
            return 0;
        }

        private static bool TryCoercePropertyValue(dynamic container, string propName, string rawValue, out object coerced)
        {
            coerced = rawValue;
            Type targetType = null;

            // Prefer the Definition.Type from the existing property entry; fall back to the
            // runtime type of the current Value.
            try
            {
                dynamic existing = null;
                try { existing = container.Properties?[propName]; } catch { }
                if (existing == null)
                {
                    try
                    {
                        foreach (dynamic p in container.Properties)
                        {
                            string n = null;
                            try { n = (string)p.Name; } catch { }
                            if (string.Equals(n, propName, StringComparison.OrdinalIgnoreCase)) { existing = p; break; }
                        }
                    }
                    catch { }
                }
                if (existing != null)
                {
                    try
                    {
                        var defType = existing.Definition?.Type;
                        if (defType is Type t1) targetType = t1;
                    }
                    catch { }
                    if (targetType == null)
                    {
                        try
                        {
                            object curVal = existing.Value;
                            if (curVal != null) targetType = curVal.GetType();
                        }
                        catch { }
                    }
                }
            }
            catch { }

            if (targetType == null || targetType == typeof(string)) return false;

            return TryConvertToType(rawValue, targetType, out coerced);
        }

        private static bool TryConvertToType(string raw, Type targetType, out object converted)
        {
            converted = raw;
            if (targetType == null) return false;

            try
            {
                if (targetType == typeof(string)) { converted = raw ?? string.Empty; return true; }
                if (targetType == typeof(bool) || targetType == typeof(bool?))
                {
                    if (string.IsNullOrWhiteSpace(raw)) { converted = false; return true; }
                    string t = raw.Trim();
                    if (t.Equals("true", StringComparison.OrdinalIgnoreCase) || t == "1" ||
                        t.Equals("yes", StringComparison.OrdinalIgnoreCase) || t.Equals("y", StringComparison.OrdinalIgnoreCase))
                    { converted = true; return true; }
                    if (t.Equals("false", StringComparison.OrdinalIgnoreCase) || t == "0" ||
                        t.Equals("no", StringComparison.OrdinalIgnoreCase) || t.Equals("n", StringComparison.OrdinalIgnoreCase))
                    { converted = false; return true; }
                    return false;
                }
                if (targetType.IsEnum)
                {
                    converted = Enum.Parse(targetType, raw, ignoreCase: true);
                    return true;
                }
                Type underlying = Nullable.GetUnderlyingType(targetType);
                if (underlying != null && underlying.IsEnum)
                {
                    if (string.IsNullOrWhiteSpace(raw)) { converted = null; return true; }
                    converted = Enum.Parse(underlying, raw, ignoreCase: true);
                    return true;
                }
                if (targetType == typeof(int) || targetType == typeof(int?))
                {
                    if (int.TryParse(raw, out int iv)) { converted = iv; return true; }
                    return false;
                }
                if (targetType == typeof(long) || targetType == typeof(long?))
                {
                    if (long.TryParse(raw, out long lv)) { converted = lv; return true; }
                    return false;
                }
                if (targetType == typeof(Guid) || targetType == typeof(Guid?))
                {
                    if (Guid.TryParse(raw, out Guid gv)) { converted = gv; return true; }
                    return false;
                }
                converted = Convert.ChangeType(raw, underlying ?? targetType);
                return true;
            }
            catch
            {
                converted = raw;
                return false;
            }
        }

        private dynamic FindControl(KBObject obj, string name)
        {
            if (string.IsNullOrEmpty(name)) return null;

            // FR#4 + FR#5 (friction-report 2026-05-14): accept three scope forms in `control`:
            //   1. Layout control name (e.g. "BtnConfirmar") — existing behavior.
            //   2. Variable reference with & prefix (e.g. "&Alu2RegProf") — new.
            //   3. Plain variable name when starting with '&' is stripped.
            // The variable form returns the SDK Variable instance so its properties
            // (ControlType, ControlValues, Enabled, Visible, …) can be read/set.
            string trimmed = name.Trim();
            if (trimmed.StartsWith("&"))
            {
                return FindVariable(obj, trimmed.Substring(1));
            }

            // Support qualified paths: "Documento.DocCod" or "Documento/DocCod"
            var segments = name.Split(new[] { '.', '/' }, StringSplitOptions.RemoveEmptyEntries);
            string leaf = segments[segments.Length - 1];

            var webFormPart = obj.Parts.Cast<KBObjectPart>().FirstOrDefault(p => p.TypeDescriptor.Name == "WebForm");
            if (webFormPart != null)
            {
                dynamic dPart = webFormPart;

                dynamic root = null;
                try { if (dPart.Form != null) root = dPart.Form; } catch { }
                if (root == null) { try { if (dPart.WebForm != null && dPart.WebForm.Form != null) root = dPart.WebForm.Form; } catch { } }

                if (root != null)
                {
                    if (segments.Length > 1)
                    {
                        var qualified = FindByPath(root, segments);
                        if (qualified != null) return qualified;
                    }
                    var ctrl = FindInControlCollection(root, leaf);
                    if (ctrl != null) return ctrl;
                }
            }

            // FR#4 + FR#5 last-resort: if the user passed a bare name that matches a Variable,
            // accept it. This is mostly to keep error messages sane when the agent forgets the
            // `&` prefix; explicit `&Name` is still the recommended form.
            var fallbackVar = FindVariable(obj, leaf);
            if (fallbackVar != null) return fallbackVar;

            // issue #57: a Transaction structure attribute ("ProcessamentoQtd") is not a
            // layout control or variable — resolve it from the Transaction's structure so
            // its occurrence properties (IsNullable, …) are reachable via genexus_properties.
            var trn = obj as Artech.Genexus.Common.Objects.Transaction;
            if (trn != null)
            {
                var structureAttr = FindStructureAttribute(trn.Structure.Root, leaf);
                if (structureAttr != null) return structureAttr;
            }

            return null;
        }

        // issue #57: recursive name lookup of a TransactionAttribute occurrence in the
        // Transaction structure (all levels).
        private dynamic FindStructureAttribute(Artech.Genexus.Common.Parts.TransactionLevel level, string attrName)
        {
            if (level == null) return null;
            try
            {
                foreach (dynamic attr in level.Attributes)
                {
                    string n = null;
                    try { n = (string)attr.Name; } catch { }
                    if (!string.IsNullOrEmpty(n) && string.Equals(n, attrName, StringComparison.OrdinalIgnoreCase)) return attr;
                }
                foreach (dynamic sub in level.Levels)
                {
                    var hit = FindStructureAttribute(sub, attrName);
                    if (hit != null) return hit;
                }
            }
            catch (Exception ex) { Logger.Debug("FindStructureAttribute: " + ex.Message); }
            return null;
        }

        // FR#4 + FR#5: resolve a Variable from the VariablesPart by name.
        private dynamic FindVariable(KBObject obj, string varName)
        {
            if (string.IsNullOrEmpty(varName)) return null;
            try
            {
                var vPart = obj.Parts.Cast<KBObjectPart>().FirstOrDefault(p => p.GetType().Name.Equals("VariablesPart"));
                if (vPart == null) return null;
                dynamic dPart = vPart;
                foreach (dynamic v in dPart.Variables)
                {
                    string n = null;
                    try { n = (string)v.Name; } catch { }
                    if (!string.IsNullOrEmpty(n) && string.Equals(n, varName, StringComparison.OrdinalIgnoreCase))
                    {
                        return v;
                    }
                }
            }
            catch (Exception ex) { Logger.Debug("FindVariable: " + ex.Message); }
            return null;
        }

        private dynamic FindByPath(dynamic root, string[] segments)
        {
            dynamic current = root;
            foreach (var seg in segments)
            {
                if (current == null) return null;
                current = FindInControlCollection(current, seg);
                if (current == null) return null;
            }
            return current;
        }

        private dynamic FindInControlCollection(dynamic root, string name)
        {
            if (root == null) return null;
            try { if (string.Equals(root.Name, name, StringComparison.OrdinalIgnoreCase)) return root; } catch {}

            try {
                if (root.Controls != null) {
                    foreach (dynamic child in root.Controls) {
                        var found = FindInControlCollection(child, name);
                        if (found != null) return found;
                    }
                }
            } catch {}
            return null;
        }

        private JObject SerializeProperties(dynamic container)
        {
            var result = new JObject();
            var props = new JArray();

            try
            {
                if (container != null && container.Properties != null)
                {
                    foreach (dynamic prop in container.Properties)
                    {
                        try {
                            var pObj = new JObject();
                            pObj["name"] = prop.Name.ToString();
                            pObj["value"] = prop.Value?.ToString() ?? "";
                            
                            try {
                                if (prop.Definition != null) {
                                    pObj["type"] = prop.Definition.Type.ToString();
                                    pObj["readOnly"] = prop.Definition.ReadOnly;
                                }
                            } catch {}

                            props.Add(pObj);
                        } catch { }
                    }
                }
            }
            catch (Exception ex) { Logger.Debug($"General error in SerializeProperties: {ex.Message}"); }

            result["properties"] = props;
            return result;
        }
    }
}
