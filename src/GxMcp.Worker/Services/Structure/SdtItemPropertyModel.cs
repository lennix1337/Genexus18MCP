using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using GxMcp.Worker.Models;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services.Structure
{
    /// <summary>One value an enumerated SDT member property accepts.</summary>
    internal sealed class SdtPropertyOption
    {
        /// <summary>The stored identifier the SDK keeps in the property bag (for example idJsonEmpty).</summary>
        public string Raw { get; set; }

        /// <summary>The SDK display name. It can be localized, so it is never the only accepted spelling.</summary>
        public string Label { get; set; }

        /// <summary>The language-neutral member name of the SDK typed enumeration (for example Empty).</summary>
        public string Member { get; set; }

        /// <summary>
        /// The object the SDK itself lists for this value. It is what gets stored, so a property whose stored type is not
        /// text keeps its type instead of receiving the text of the value.
        /// </summary>
        public object Value { get; set; }

        /// <summary>The spelling returned to callers: the neutral member, else the display name, else the stored id.</summary>
        public string Preferred => !string.IsNullOrEmpty(Member) ? Member : !string.IsNullOrEmpty(Label) ? Label : Raw;
    }

    /// <summary>One property an SDT member (leaf item or sub-level) exposes through the SDK property bag.</summary>
    internal sealed class SdtPropertyInfo
    {
        /// <summary>The bag key (for example idJsonInclude).</summary>
        public string NativeName { get; set; }

        /// <summary>The SDK typed constant name (for example JsonNullSerialization), when the SDK declares one.</summary>
        public string ConstantName { get; set; }

        /// <summary>The IDE name (for example "Json Null Serialization"); localized by the SDK resources.</summary>
        public string DisplayName { get; set; }

        public string Category { get; set; }
        public string Description { get; set; }

        /// <summary>The CLR type of the stored value; null when it could not be read.</summary>
        public Type ClrType { get; set; }

        public bool ReadOnly { get; set; }
        public bool Applicable { get; set; } = true;
        public bool Browsable { get; set; } = true;
        public List<SdtPropertyOption> Options { get; } = new List<SdtPropertyOption>();

        /// <summary>
        /// True when something the write depends on could not be read from the SDK (the descriptor with its read-only,
        /// hidden and applicable flags, the stored type, or the allowed values). A property read in part is never written:
        /// the defaults of the missing facts (free text, writable, applicable) would accept values the SDK does not.
        /// </summary>
        public bool CatalogIncomplete { get; private set; }

        public string IncompleteReason { get; private set; }

        public void MarkIncomplete(string reason)
        {
            CatalogIncomplete = true;
            if (string.IsNullOrEmpty(IncompleteReason)) IncompleteReason = reason;
        }

        public bool IsEnumerated => Options.Count > 0;
        public string TypeName => ClrType == null ? null : (Nullable.GetUnderlyingType(ClrType) ?? ClrType).Name;

        /// <summary>The spelling documented to callers: the SDK constant, else the IDE name without separators, else the bag key.</summary>
        public string CanonicalName
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(ConstantName)) return ConstantName;
                if (!string.IsNullOrWhiteSpace(DisplayName))
                {
                    string compact = Regex.Replace(DisplayName, "[^A-Za-z0-9]", string.Empty);
                    if (compact.Length > 0) return compact;
                }
                return NativeName;
            }
        }
    }

    /// <summary>The value a property reads today, and whether it is only the default.</summary>
    internal sealed class SdtEffectiveValue
    {
        public object Value { get; set; }

        /// <summary>The canonical string of Value: the unit of every comparison and of what is stored.</summary>
        public string Raw { get; set; }

        /// <summary>True when the property has no explicit value and reads its default.</summary>
        public bool IsDefault { get; set; }

        public object DefaultValue { get; set; }
        public string DefaultRaw { get; set; }
    }

    /// <summary>What a fresh read of the SDT says about the addressed member.</summary>
    internal sealed class SdtPropertyState
    {
        /// <summary>The object-level version token (the same one get_visual returns).</summary>
        public string Version { get; set; }

        /// <summary>
        /// Every member of the SDT keyed by its slash-joined path: { kind, properties:{ bagKey: stored string } }.
        /// Only explicitly stored properties are present, so it is exactly what a save persists.
        /// </summary>
        public JObject Members { get; set; } = new JObject();

        /// <summary>True when no object part other than the SDT structure differs from the baseline taken before the write.</summary>
        public bool OtherPartsEqual { get; set; } = true;

        /// <summary>"item" for a leaf member, "level" for a sub-structure.</summary>
        public string TargetKind { get; set; }

        /// <summary>The addressed member path with the names as the SDT stores them.</summary>
        public IList<string> TargetPath { get; set; } = new List<string>();

        public string TargetPathKey => string.Join("/", TargetPath);
        public IList<SdtPropertyInfo> Catalog { get; set; } = new List<SdtPropertyInfo>();
        public IDictionary<string, SdtEffectiveValue> Effective { get; set; } = new Dictionary<string, SdtEffectiveValue>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The single property change a write applies, or the inverse that undoes it.</summary>
    internal sealed class SdtPropertyChange
    {
        public string NativeName { get; set; }

        /// <summary>True to reset the property to its default instead of storing Value.</summary>
        public bool Reset { get; set; }

        /// <summary>The typed value for SetPropertyValue; ignored when Reset is true.</summary>
        public object Value { get; set; }

        /// <summary>The canonical string of the value this change leaves in effect (the default's string for a reset).</summary>
        public string Raw { get; set; }
    }

    internal enum SdtWritePhase
    {
        NotStarted = 0,
        Mutating = 1,
        Saving = 2,
        Saved = 3,
        Committed = 4
    }

    /// <summary>A refusal the caller can act on; carries the error envelope fields.</summary>
    internal sealed class SdtUserFacingException : Exception
    {
        public string Code { get; }
        public string Hint { get; }
        public JObject Extra { get; }

        /// <summary>A prebuilt error envelope (for example the not-found healing message); wins over Code/Hint.</summary>
        public string Envelope { get; }

        public SdtUserFacingException(string code, string message, string hint = null, JObject extra = null)
            : base(message)
        {
            Code = code;
            Hint = hint;
            Extra = extra;
        }

        public SdtUserFacingException(string envelope) : base("prebuilt envelope")
        {
            Envelope = envelope;
        }

        public string ToEnvelope(string target)
        {
            if (Envelope != null) return Envelope;
            var extra = new JObject { ["persisted"] = false };
            if (Extra != null) foreach (var property in Extra.Properties()) extra[property.Name] = property.Value;
            return McpResponse.Err(code: Code, message: Message, hint: Hint, target: target, extra: extra);
        }
    }

    internal sealed class SdtVersionConflictException : Exception
    {
        public string CurrentVersion { get; }

        public SdtVersionConflictException(string currentVersion)
            : base("The SDT changed after expectedVersion was captured.")
        {
            CurrentVersion = currentVersion;
        }
    }

    /// <summary>The staged state read back inside the SDK transaction failed the acceptance check; the transaction was rolled back.</summary>
    internal sealed class SdtStageRejectedException : Exception
    {
        public SdtPropertyState Staged { get; }

        public SdtStageRejectedException(string reason, SdtPropertyState staged) : base(reason)
        {
            Staged = staged;
        }
    }

    /// <summary>A failure in the SDK write, with the last phase that began.</summary>
    internal sealed class SdtWriteFailure : Exception
    {
        public SdtWritePhase Phase { get; }

        public SdtWriteFailure(SdtWritePhase phase, Exception inner) : base(inner?.Message ?? "SDT member property write failed.", inner)
        {
            Phase = phase;
        }
    }

    /// <summary>The decisions of the SDK write that need no SDK, kept apart so they can be tested.</summary>
    internal static class SdtWriteGuards
    {
        /// <summary>True when the object still has the version the caller read (compared again inside the SDK transaction).</summary>
        internal static bool VersionStillCurrent(string expectedVersion, string currentVersion)
        {
            return string.Equals(expectedVersion, currentVersion, StringComparison.Ordinal);
        }

        /// <summary>
        /// Null when the write may be undone; otherwise why it may not. The object stamp has one-second resolution, so two saves in
        /// the same second share a version: the persisted state must also hold exactly the stored properties this write saved,
        /// or the restore would overwrite another writer's work.
        /// </summary>
        internal static string RestoreRefusal(string ownedVersion, JObject ownedMembers, string currentVersion, JObject currentMembers)
        {
            if (string.IsNullOrWhiteSpace(ownedVersion))
                return "the write never reached a save";
            if (!string.Equals(ownedVersion, currentVersion, StringComparison.Ordinal))
                return "the current persisted version cannot be attributed to this write";
            if (ownedMembers != null && !JToken.DeepEquals(ownedMembers, currentMembers))
                return "the stored properties are not the ones this write saved";
            return null;
        }
    }

    /// <summary>
    /// The SDK-facing half of an SDT member property write. The engine owns the protocol (version checks,
    /// acceptance, reconciliation, rollback); an implementation owns the SDK calls.
    /// </summary>
    internal interface ISdtPropertyStore
    {
        /// <summary>A fresh read of the SDT; throws <see cref="SdtUserFacingException"/> when the SDT or the member cannot be read.</summary>
        SdtPropertyState ReadFresh();

        /// <summary>
        /// In one SDK transaction: re-check expectedVersion, apply the change, save, read the staged state back, and commit only
        /// when accept returns null. Throws <see cref="SdtVersionConflictException"/>, <see cref="SdtStageRejectedException"/>
        /// (transaction rolled back) or <see cref="SdtWriteFailure"/>.
        /// </summary>
        SdtPropertyState ApplyAndStage(SdtPropertyChange change, string expectedVersion, Func<SdtPropertyState, string> accept);

        /// <summary>Undoes this store's own write in a second transaction; true only when the prior stored state is verified again.</summary>
        bool Restore(SdtPropertyChange inverse, SdtPropertyState expected);

        /// <summary>Called once after a verified write so caches follow the persisted object.</summary>
        void NoteVerifiedWrite();
    }
}
