using System;
using System.Collections.Generic;
using System.Linq;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services.Structure
{
    /// <summary>
    /// The protocol of get_item_property / set_item_property over an <see cref="ISdtPropertyStore"/>: resolve the property,
    /// check the version, preview, apply in one transaction, verify the persisted state with a fresh read, and undo the
    /// write when anything but the requested property changed. It never touches the SDK itself.
    /// </summary>
    internal static class SdtItemPropertyEngine
    {
        // ------------------------------------------------------------------ read

        internal static string Get(ISdtPropertyStore store, string sdtName, string property)
        {
            SdtPropertyState state;
            try { state = store.ReadFresh(); }
            catch (SdtUserFacingException ex) { return ex.ToEnvelope(sdtName); }
            catch (Exception ex)
            {
                return McpResponse.Err(code: "StructureReadFailed", message: ex.Message, target: sdtName);
            }

            var result = Header(state, sdtName);
            if (string.IsNullOrWhiteSpace(property))
            {
                var listing = new JArray();
                foreach (var info in state.Catalog.Where(p => p.Browsable)
                    .OrderBy(p => p.Category ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(p => p.CanonicalName, StringComparer.OrdinalIgnoreCase))
                    listing.Add(Describe(info, state, detailed: false));
                result["properties"] = listing;
            }
            else
            {
                var match = SdtItemPropertyResolver.Resolve(state.Catalog, property);
                if (match.Info == null) return ResolutionError(sdtName, state, property, match);
                foreach (var entry in Describe(match.Info, state, detailed: true).Properties())
                    result[entry.Name] = entry.Value;
            }
            result["versionToken"] = state.Version;
            return McpResponse.Ok(target: sdtName, code: "SdtItemProperty", result: result);
        }

        // ----------------------------------------------------------------- write

        internal static string Set(ISdtPropertyStore store, string sdtName, string property, string valueText,
            bool dryRun, string expectedVersion)
        {
            SdtPropertyState before;
            try { before = store.ReadFresh(); }
            catch (SdtUserFacingException ex) { return ex.ToEnvelope(sdtName); }
            catch (Exception ex)
            {
                return McpResponse.Err(code: "StructureReadFailed", message: ex.Message, target: sdtName,
                    extra: new JObject { ["persisted"] = false });
            }

            if (string.IsNullOrWhiteSpace(property))
                return Refuse(sdtName, "PropertyRequired", "set_item_property requires 'property'.",
                    "Read the item with get_item_property (without 'property') to list its properties.");

            var match = SdtItemPropertyResolver.Resolve(before.Catalog, property);
            if (match.Info == null) return ResolutionError(sdtName, before, property, match);
            SdtPropertyInfo info = match.Info;

            string blocked = WriteBlock(info);
            if (blocked != null)
                return Refuse(sdtName, blocked, BlockMessage(blocked, info, before), BlockHint(blocked));

            object typed;
            string raw;
            string valueError;
            if (valueText == null)
                return Refuse(sdtName, "ValueRequired", "set_item_property requires 'value'.",
                    info.IsEnumerated ? "Allowed values: " + SdtItemPropertyResolver.AllowedText(info) + "." : null);
            if (!SdtItemPropertyResolver.TryResolveValue(info, valueText, out typed, out raw, out valueError))
            {
                var extra = new JObject { ["persisted"] = false };
                if (info.IsEnumerated) extra["allowedValues"] = AllowedValues(info);
                return McpResponse.Err(code: "InvalidPropertyValue", message: valueError,
                    hint: "Property '" + info.CanonicalName + "' of '" + before.TargetPathKey + "'.", target: sdtName, extra: extra);
            }

            SdtEffectiveValue current;
            if (!before.Effective.TryGetValue(info.NativeName, out current))
                return Refuse(sdtName, "PropertyReadFailed",
                    "The current value of '" + info.CanonicalName + "' could not be read, so the change cannot be previewed or verified.", null);

            if (!string.IsNullOrWhiteSpace(expectedVersion)
                && !string.Equals(expectedVersion, before.Version, StringComparison.Ordinal))
                return VersionConflict(sdtName, expectedVersion, before.Version);
            if (!dryRun && string.IsNullOrWhiteSpace(expectedVersion))
                return McpResponse.Err(code: "ExpectedVersionRequired",
                    message: "An SDT member property write requires expectedVersion from get_item_property or get_visual.",
                    hint: "Pass versionToken as expectedVersion.", target: sdtName,
                    extra: new JObject { ["persisted"] = false, ["currentVersion"] = before.Version });

            bool willChange = !string.Equals(current.Raw, raw, StringComparison.Ordinal);
            var change = new SdtPropertyChange { NativeName = info.NativeName, Value = typed, Raw = raw };
            var inverse = new SdtPropertyChange
            {
                NativeName = info.NativeName,
                Reset = current.IsDefault,
                Value = current.Value,
                Raw = current.Raw
            };
            var plan = new JArray();
            if (willChange) plan.Add(PlannedChange(info, before, current, raw));

            if (dryRun)
            {
                var preview = Header(before, sdtName);
                preview["property"] = info.CanonicalName;
                preview["nativeName"] = info.NativeName;
                preview["dryRun"] = true;
                preview["persisted"] = false;
                preview["willChange"] = willChange;
                preview["before"] = ValuePair(info, current.Raw, current.Value, current.IsDefault);
                preview["after"] = ValuePair(info, raw, typed, null);
                preview["changes"] = plan;
                preview["membersChecked"] = before.Members.Count;
                preview["versionToken"] = before.Version;
                preview["implicitLifecycleActions"] = new JArray();
                return McpResponse.Ok(target: sdtName, code: "SdtItemPropertyPreview", result: preview);
            }

            if (!willChange)
            {
                var unchanged = Header(before, sdtName);
                unchanged["property"] = info.CanonicalName;
                unchanged["nativeName"] = info.NativeName;
                unchanged["persisted"] = false;
                unchanged["unchanged"] = true;
                unchanged["value"] = ValueToken(info, current.Value, current.Raw);
                unchanged["rawValue"] = current.Raw;
                unchanged["isDefault"] = current.IsDefault;
                unchanged["membersChecked"] = before.Members.Count;
                unchanged["versionToken"] = before.Version;
                unchanged["implicitLifecycleActions"] = new JArray();
                return McpResponse.Ok(target: sdtName, code: "SdtItemPropertyUnchanged", result: unchanged);
            }

            SdtPropertyState staged = null;
            Exception failure = null;
            SdtWritePhase phase = SdtWritePhase.NotStarted;
            try
            {
                staged = store.ApplyAndStage(change, before.Version, observed => CheckObserved(before, change, observed));
            }
            catch (SdtVersionConflictException conflict)
            {
                return VersionConflict(sdtName, expectedVersion, conflict.CurrentVersion);
            }
            catch (SdtStageRejectedException rejected)
            {
                failure = rejected;
                phase = SdtWritePhase.Saved;
            }
            catch (SdtWriteFailure writeFailure)
            {
                failure = writeFailure.InnerException ?? writeFailure;
                phase = writeFailure.Phase;
            }
            catch (Exception ex)
            {
                failure = ex;
                phase = SdtWritePhase.Mutating;
            }
            if (failure != null)
                return Reconcile(store, sdtName, before, change, inverse, failure, phase,
                    failure is SdtStageRejectedException ? "SdtItemPropertyNotPersisted" : "SdtItemPropertyUpdateFailed");

            // The staged read happened inside the SDK transaction. The persisted state is confirmed with a fresh read.
            SdtPropertyState persisted = TryReadFresh(store);
            string reason = persisted == null
                ? "A fresh read after the save could not be confirmed."
                : CheckObserved(before, change, persisted);
            if (reason != null)
            {
                bool restored = store.Restore(inverse, before);
                SdtPropertyState after = restored ? TryReadFresh(store) : persisted;
                bool verified = restored && after != null && SameStored(before, after) && after.OtherPartsEqual;
                SdtPropertyState last = after ?? persisted;
                var extra = new JObject
                {
                    ["persisted"] = verified ? new JValue(false) : PersistedFlag(before, last, change),
                    ["persistedVerified"] = false,
                    ["rollback"] = verified ? "verified" : "unverified",
                    ["beforeVersion"] = before.Version,
                    ["versionToken"] = last?.Version,
                    ["mismatchDetail"] = reason,
                    ["implicitLifecycleActions"] = new JArray()
                };
                if (!verified) AddStoredDifferences(extra, before, last);
                return McpResponse.Err(
                    code: persisted == null ? "FreshReadUnavailable" : "SdtItemPropertyNotPersisted",
                    message: verified
                        ? "The persisted SDT member did not match the request; the prior value was restored and verified."
                        : "The persisted SDT member did not match the request and the rollback could not be confirmed. Stop writing this object.",
                    hint: "Read get_item_property again through a fresh SDK instance before another write.",
                    target: sdtName, extra: extra);
            }

            try { store.NoteVerifiedWrite(); }
            catch (Exception ex) { Logger.Warn("SDT member property cache refresh failed after a verified save: " + ex.Message); }

            SdtEffectiveValue now = persisted.Effective[info.NativeName];
            var done = Header(persisted, sdtName);
            done["property"] = info.CanonicalName;
            done["nativeName"] = info.NativeName;
            done["before"] = ValuePair(info, current.Raw, current.Value, current.IsDefault);
            done["after"] = ValuePair(info, now.Raw, now.Value, now.IsDefault);
            done["changes"] = plan;
            done["storedChanges"] = Diff(before.Members, persisted.Members);
            done["persisted"] = true;
            done["persistedVerified"] = true;
            done["beforeVersion"] = before.Version;
            done["versionToken"] = persisted.Version;
            done["membersChecked"] = persisted.Members.Count;
            done["otherMembersUnchanged"] = true;
            done["otherPartsUnchanged"] = true;
            done["implicitLifecycleActions"] = new JArray();
            return McpResponse.Ok(target: sdtName, code: "SdtItemPropertyUpdated", result: done);
        }

        // ---------------------------------------------------------- verification

        /// <summary>
        /// Null when the observed state is exactly the planned one: the requested value is in effect, the only stored
        /// difference is the requested property of the addressed member, and no other object part moved.
        /// </summary>
        internal static string CheckObserved(SdtPropertyState before, SdtPropertyChange change, SdtPropertyState observed)
        {
            if (observed == null) return "The state after the write could not be read.";
            if (!observed.OtherPartsEqual) return "The save changed another object part.";
            SdtEffectiveValue effective;
            if (!observed.Effective.TryGetValue(change.NativeName, out effective))
                return "The property '" + change.NativeName + "' is not readable after the write.";
            if (!string.Equals(effective.Raw, change.Raw, StringComparison.Ordinal))
                return "The persisted value is '" + effective.Raw + "' but '" + change.Raw + "' was requested.";
            JArray differences = Diff(before.Members, observed.Members);
            if (differences.Count != 1)
                return differences.Count == 0
                    ? "The save left the stored properties unchanged."
                    : "The save changed more than the requested property: " + differences.ToString(Newtonsoft.Json.Formatting.None);
            var only = (JObject)differences[0];
            if (!string.Equals(only["path"]?.ToString(), before.TargetPathKey, StringComparison.Ordinal)
                || !string.Equals(only["property"]?.ToString(), change.NativeName, StringComparison.Ordinal))
                return "The save changed a different property than requested: " + only.ToString(Newtonsoft.Json.Formatting.None);
            return null;
        }

        internal static bool SameStored(SdtPropertyState expected, SdtPropertyState actual)
        {
            return expected != null && actual != null && JToken.DeepEquals(expected.Members, actual.Members);
        }

        /// <summary>The stored differences between two member snapshots, member by member and property by property.</summary>
        internal static JArray Diff(JObject before, JObject after)
        {
            var changes = new JArray();
            var paths = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var p in before.Properties()) paths.Add(p.Name);
            foreach (var p in after.Properties()) paths.Add(p.Name);
            foreach (string path in paths)
            {
                var left = before[path] as JObject;
                var right = after[path] as JObject;
                if (left == null) { changes.Add(new JObject { ["path"] = path, ["change"] = "memberAdded" }); continue; }
                if (right == null) { changes.Add(new JObject { ["path"] = path, ["change"] = "memberRemoved" }); continue; }
                if (!string.Equals(left["kind"]?.ToString(), right["kind"]?.ToString(), StringComparison.Ordinal))
                    changes.Add(new JObject { ["path"] = path, ["change"] = "kindChanged" });
                if (!string.Equals(left["index"]?.ToString(), right["index"]?.ToString(), StringComparison.Ordinal))
                    changes.Add(new JObject
                    {
                        ["path"] = path,
                        ["change"] = "moved",
                        ["from"] = left["index"] ?? JValue.CreateNull(),
                        ["to"] = right["index"] ?? JValue.CreateNull()
                    });
                var leftProps = left["properties"] as JObject ?? new JObject();
                var rightProps = right["properties"] as JObject ?? new JObject();
                var names = new SortedSet<string>(StringComparer.Ordinal);
                foreach (var p in leftProps.Properties()) names.Add(p.Name);
                foreach (var p in rightProps.Properties()) names.Add(p.Name);
                foreach (string name in names)
                {
                    string from = leftProps[name]?.ToString();
                    string to = rightProps[name]?.ToString();
                    if (string.Equals(from, to, StringComparison.Ordinal)) continue;
                    changes.Add(new JObject
                    {
                        ["path"] = path,
                        ["property"] = name,
                        ["from"] = from == null ? JValue.CreateNull() : new JValue(from),
                        ["to"] = to == null ? JValue.CreateNull() : new JValue(to)
                    });
                }
            }
            return changes;
        }

        // ---------------------------------------------------------- reconciliation

        // "persisted" answers one question: is the requested value in the KB? A different stored state (another writer's work,
        // or an effect the restore could not undo) is reported beside it as storedChanges, never as a persisted write of
        // this request. JSON null when the state could not be read.
        internal static JToken PersistedFlag(SdtPropertyState before, SdtPropertyState observed, SdtPropertyChange change)
        {
            bool? visible = RequestedValueVisible(before, observed, change);
            return visible.HasValue ? (JToken)new JValue(visible.Value) : JValue.CreateNull();
        }

        internal static bool? RequestedValueVisible(SdtPropertyState before, SdtPropertyState observed, SdtPropertyChange change)
        {
            if (observed == null) return null;
            SdtEffectiveValue after;
            if (!observed.Effective.TryGetValue(change.NativeName, out after)) return null;
            SdtEffectiveValue was;
            bool existed = before.Effective.TryGetValue(change.NativeName, out was);
            return string.Equals(after.Raw, change.Raw, StringComparison.Ordinal)
                && (!existed || !string.Equals(was.Raw, change.Raw, StringComparison.Ordinal));
        }

        private const int MaxReportedStoredChanges = 20;

        // What the KB holds now against what it held before this request: the evidence behind an unverified rollback.
        private static void AddStoredDifferences(JObject extra, SdtPropertyState before, SdtPropertyState observed)
        {
            if (observed == null) return;
            JArray all = Diff(before.Members, observed.Members);
            if (all.Count > 0)
            {
                extra["storedChanges"] = new JArray(all.Take(MaxReportedStoredChanges));
                if (all.Count > MaxReportedStoredChanges) extra["storedChangesTotal"] = all.Count;
            }
            if (!observed.OtherPartsEqual) extra["otherPartsChanged"] = true;
        }

        private static string Reconcile(ISdtPropertyStore store, string sdtName, SdtPropertyState before, SdtPropertyChange change,
            SdtPropertyChange inverse, Exception failure, SdtWritePhase phase, string code)
        {
            string message = failure.InnerException?.Message ?? failure.Message;
            if (phase == SdtWritePhase.NotStarted)
                return McpResponse.Err(code: code, message: message, target: sdtName, extra: new JObject
                {
                    ["persisted"] = false,
                    ["rollback"] = "not-needed",
                    ["implicitLifecycleActions"] = new JArray()
                });

            SdtPropertyState fresh = TryReadFresh(store);
            bool unchanged = fresh != null && SameStored(before, fresh) && fresh.OtherPartsEqual;
            string rollback;
            JToken persisted;
            SdtPropertyState last = fresh;
            if (unchanged)
            {
                rollback = "transaction-verified";
                persisted = new JValue(false);
            }
            else
            {
                bool restored = store.Restore(inverse, before);
                last = restored ? TryReadFresh(store) : fresh;
                bool verified = restored && last != null && SameStored(before, last) && last.OtherPartsEqual;
                rollback = verified ? "verified" : "unverified";
                persisted = verified ? new JValue(false) : PersistedFlag(before, last, change);
            }

            var extra = new JObject
            {
                ["persisted"] = persisted,
                ["rollback"] = rollback,
                ["beforeVersion"] = before.Version,
                ["versionToken"] = last?.Version,
                ["implicitLifecycleActions"] = new JArray()
            };
            if (failure is SdtStageRejectedException) extra["mismatchDetail"] = failure.Message;
            if (rollback == "unverified") AddStoredDifferences(extra, before, last);
            return McpResponse.Err(code: code, message: message,
                hint: rollback == "unverified"
                    ? "The persisted state could not be reconciled. Read get_item_property before another write."
                    : "The prior SDT member state was verified after the failure.",
                target: sdtName, extra: extra);
        }

        private static SdtPropertyState TryReadFresh(ISdtPropertyStore store)
        {
            try { return store.ReadFresh(); }
            catch (Exception ex)
            {
                Logger.Warn("SDT member property fresh read failed: " + ex.Message);
                return null;
            }
        }

        // -------------------------------------------------------------- guards

        internal static string WriteBlock(SdtPropertyInfo info)
        {
            if (SdtItemPropertyResolver.IsStructural(info)) return "StructuralPropertyNotSupported";
            // A property read in part is never written: the defaults of the missing facts (writable, visible, applicable, free
            // text) would let through values the SDK itself would refuse or silently rewrite.
            if (info.CatalogIncomplete) return "PropertyCatalogIncomplete";
            if (info.ReadOnly) return "PropertyReadOnly";
            if (!info.Browsable) return "PropertyHidden";
            if (!info.Applicable) return "PropertyNotApplicable";
            if (!IsSupportedType(info)) return "UnsupportedPropertyType";
            return null;
        }

        private static bool IsSupportedType(SdtPropertyInfo info)
        {
            if (info.IsEnumerated) return true;
            if (info.ClrType == null) return false;
            Type type = Nullable.GetUnderlyingType(info.ClrType) ?? info.ClrType;
            return type == typeof(string) || type == typeof(bool) || type == typeof(int) || type == typeof(long)
                || type == typeof(short) || type == typeof(decimal) || type == typeof(double) || type == typeof(float);
        }

        private static string BlockMessage(string code, SdtPropertyInfo info, SdtPropertyState state)
        {
            string name = "'" + info.CanonicalName + "' (" + info.NativeName + ")";
            string member = "the " + state.TargetKind + " '" + state.TargetPathKey + "'";
            switch (code)
            {
                case "StructuralPropertyNotSupported":
                    return "Property " + name + " defines the identity or the type of " + member + " and is not written through this action.";
                case "PropertyCatalogIncomplete":
                    return "Property " + name + " of " + member + " was not read completely from the SDK (" + info.IncompleteReason
                        + "), so it is not written: a missing fact must not stand in for the SDK's answer.";
                case "PropertyReadOnly":
                    return "Property " + name + " is read-only for " + member + ".";
                case "PropertyHidden":
                    return "Property " + name + " is not shown by the IDE for " + member + ", so it is not written through this action.";
                case "PropertyNotApplicable":
                    return "Property " + name + " does not apply to " + member + " in its current configuration.";
                default:
                    return "Property " + name + " has a type this action does not write (" + (info.TypeName ?? "unknown") + ").";
            }
        }

        private static string BlockHint(string code)
        {
            switch (code)
            {
                case "StructuralPropertyNotSupported":
                    return "Use genexus_structure action=update_visual to rename or retype an SDT member.";
                case "PropertyNotApplicable":
                    return "Read the item with get_item_property: 'applicable' tells which properties the SDK exposes for it.";
                case "PropertyHidden":
                    return "Only the properties the IDE shows for a member are written; set it in the IDE if it is really needed.";
                case "PropertyCatalogIncomplete":
                    return "This SDK build exposes the property differently from the builds this action was verified against; change it in the IDE.";
                default:
                    return null;
            }
        }

        private static string Refuse(string sdtName, string code, string message, string hint)
        {
            return McpResponse.Err(code: code, message: message, hint: hint, target: sdtName,
                extra: new JObject { ["persisted"] = false });
        }

        private static string VersionConflict(string sdtName, string expected, string current)
        {
            return McpResponse.Err(code: "VersionConflict",
                message: "The SDT changed after expectedVersion was captured.", target: sdtName,
                hint: "Read the member again with get_item_property and retry with the new versionToken.",
                extra: new JObject { ["persisted"] = false, ["expectedVersion"] = expected, ["currentVersion"] = current });
        }

        private static string ResolutionError(string sdtName, SdtPropertyState state, string requested, SdtPropertyMatch match)
        {
            if (match.Ambiguous != null)
            {
                var names = new JArray(match.Ambiguous.Select(p => p.NativeName));
                return McpResponse.Err(code: "AmbiguousProperty",
                    message: "'" + requested + "' matches more than one property of the " + state.TargetKind + " '" + state.TargetPathKey + "': "
                        + string.Join(", ", match.Ambiguous.Select(p => p.NativeName)) + ".",
                    hint: "Pass the nativeName.", target: sdtName,
                    extra: new JObject { ["persisted"] = false, ["candidates"] = names });
            }
            string suggestion = match.Suggestions.Count > 0 ? " Did you mean " + string.Join(", ", match.Suggestions) + "?" : string.Empty;
            return McpResponse.Err(code: "PropertyNotFound",
                message: "Property '" + requested + "' is not exposed by the " + state.TargetKind + " '" + state.TargetPathKey + "'." + suggestion,
                hint: "Call get_item_property without 'property' to list the properties of this member.",
                target: sdtName,
                extra: new JObject { ["persisted"] = false, ["suggestions"] = new JArray(match.Suggestions) });
        }

        // ------------------------------------------------------------- shaping

        private static JObject Header(SdtPropertyState state, string sdtName)
        {
            return new JObject
            {
                ["name"] = sdtName,
                ["itemPath"] = new JArray(state.TargetPath),
                ["kind"] = state.TargetKind
            };
        }

        private static JObject Describe(SdtPropertyInfo info, SdtPropertyState state, bool detailed)
        {
            SdtEffectiveValue effective;
            state.Effective.TryGetValue(info.NativeName, out effective);
            string blocked = WriteBlock(info);
            var item = new JObject
            {
                ["property"] = info.CanonicalName,
                ["nativeName"] = info.NativeName,
                ["displayName"] = info.DisplayName,
                ["type"] = TypeLabel(info),
                ["value"] = ValueToken(info, effective?.Value, effective?.Raw),
                ["rawValue"] = effective?.Raw,
                ["isDefault"] = effective == null ? JValue.CreateNull() : new JValue(effective.IsDefault),
                ["writable"] = blocked == null
            };
            if (blocked != null) item["notWritableReason"] = blocked;
            if (!detailed) return item;

            item["category"] = info.Category;
            item["description"] = info.Description;
            item["readOnly"] = info.ReadOnly;
            item["applicable"] = info.Applicable;
            if (info.IsEnumerated)
            {
                SdtPropertyOption option = effective == null ? null : info.Options.FirstOrDefault(o => string.Equals(o.Raw, effective.Raw, StringComparison.Ordinal));
                if (option?.Label != null) item["label"] = option.Label;
                item["allowedValues"] = AllowedValues(info);
            }
            if (effective != null && effective.DefaultRaw != null)
            {
                item["defaultValue"] = ValueToken(info, effective.DefaultValue, effective.DefaultRaw);
                item["defaultRawValue"] = effective.DefaultRaw;
            }
            return item;
        }

        private static JArray AllowedValues(SdtPropertyInfo info)
        {
            return new JArray(info.Options.Select(o => new JObject
            {
                ["value"] = o.Preferred,
                ["label"] = o.Label,
                ["rawValue"] = o.Raw
            }));
        }

        private static JObject ValuePair(SdtPropertyInfo info, string raw, object value, bool? isDefault)
        {
            var pair = new JObject
            {
                ["value"] = ValueToken(info, value, raw),
                ["rawValue"] = raw
            };
            if (isDefault.HasValue) pair["isDefault"] = isDefault.Value;
            return pair;
        }

        private static JObject PlannedChange(SdtPropertyInfo info, SdtPropertyState state, SdtEffectiveValue current, string raw)
        {
            return new JObject
            {
                ["path"] = state.TargetPathKey,
                ["property"] = info.CanonicalName,
                ["nativeName"] = info.NativeName,
                ["from"] = ValuePair(info, current.Raw, current.Value, current.IsDefault),
                ["to"] = ValuePair(info, raw, null, null)
            };
        }

        private static JToken ValueToken(SdtPropertyInfo info, object value, string raw)
        {
            if (raw == null) return JValue.CreateNull();
            if (info.IsEnumerated)
            {
                SdtPropertyOption option = info.Options.FirstOrDefault(o => string.Equals(o.Raw, raw, StringComparison.Ordinal));
                return new JValue(option != null ? option.Preferred : raw);
            }
            if (value is bool) return new JValue((bool)value);
            if (value is int || value is long || value is short) return new JValue(Convert.ToInt64(value));
            return new JValue(raw);
        }

        private static string TypeLabel(SdtPropertyInfo info)
        {
            if (info.IsEnumerated) return "enum";
            Type type = info.ClrType == null ? null : (Nullable.GetUnderlyingType(info.ClrType) ?? info.ClrType);
            if (type == null) return "unknown";
            if (type == typeof(string)) return "string";
            if (type == typeof(bool)) return "boolean";
            if (type == typeof(int) || type == typeof(long) || type == typeof(short)) return "integer";
            if (type == typeof(decimal) || type == typeof(double) || type == typeof(float)) return "number";
            return type.Name;
        }
    }
}
