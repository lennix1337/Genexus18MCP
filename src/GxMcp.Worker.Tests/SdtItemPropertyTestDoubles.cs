using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using GxMcp.Worker.Services.Structure;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// An in-memory SDT member behind <see cref="ISdtPropertyStore"/>: the stored properties of the addressed member
    /// ("debug"), one untouched neighbour and a version. The failure switches reproduce the ways a real SDK write can go wrong.
    /// </summary>
    internal sealed class FakeSdtStore : ISdtPropertyStore
    {
        public const string Native = "idJsonInclude";
        public const string TargetKey = "debug";

        public readonly List<string> Log = new List<string>();
        public string Version = "100:root";
        public bool OtherPartsEqual = true;

        // stored (explicit) properties of the addressed member
        public Dictionary<string, string> Target = new Dictionary<string, string>(StringComparer.Ordinal);
        // stored properties of the neighbour member
        public Dictionary<string, string> Neighbour = new Dictionary<string, string>(StringComparer.Ordinal) { { "idJsonName", "keep" } };
        public Dictionary<string, string> Defaults = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { Native, "idJsonNoProperty" }, { "idJsonName", "" }, { "Required", "False" }
        };
        public IList<SdtPropertyInfo> Catalog = DefaultCatalog();
        public string Kind = "item";
        // GeneXus 18 U16 keeps a value explicitly stored even when it equals the default (measured on a live KB);
        // set this to model an SDK that drops a stored value equal to the default.
        public bool DropsDefaultValues;
        private int _versionCounter = 100;

        // ---- failure injection
        public int ReadsBeforeFailing = int.MaxValue;
        public bool ConflictUnderLock;
        public Exception FailBeforeSave;           // thrown in the mutating phase, nothing persisted
        public SdtWritePhase FailPhase = SdtWritePhase.Mutating;   // the phase FailBeforeSave reports
        public Exception FailAtSave;               // the save fails and the SDK transaction rolls back
        public Exception FailAfterPersist;         // the save lands, the failure comes after and the rollback did not undo it
        public Action<FakeSdtStore> SideEffect;    // an extra change made by the same save
        public Action<FakeSdtStore> AfterCommit;   // what a fresh read finds after the transaction committed
        public bool RestoreSucceeds = true;
        public string StoreUnderKey;               // the SDK keeps the value under another key than the one written
        public bool SwallowWrites;                 // the save stores nothing for the written property
        public Action<FakeSdtStore> AfterTransactionRollback;   // another writer touches the SDT right after our transaction was undone

        private int _reads;

        public static IList<SdtPropertyInfo> DefaultCatalog()
        {
            var json = new SdtPropertyInfo
            {
                NativeName = Native, ConstantName = "JsonNullSerialization", DisplayName = "Json Null Serialization",
                Category = "Serialization", ClrType = typeof(string)
            };
            json.Options.Add(new SdtPropertyOption { Raw = "idJsonEmpty", Label = "Empty", Member = "Empty" });
            json.Options.Add(new SdtPropertyOption { Raw = "idJsonJsonNull", Label = "JSON null", Member = "JsonNull" });
            json.Options.Add(new SdtPropertyOption { Raw = "idJsonNoProperty", Label = "No Property", Member = "NoProperty" });
            return new List<SdtPropertyInfo>
            {
                json,
                new SdtPropertyInfo { NativeName = "idJsonName", ConstantName = "JsonName", DisplayName = "Json Name", Category = "Serialization", ClrType = typeof(string) },
                new SdtPropertyInfo { NativeName = "Required", ConstantName = "Required", DisplayName = "Required", Category = "Interface", ClrType = typeof(bool) },
                new SdtPropertyInfo { NativeName = "Name", DisplayName = "Name", Category = "General", ClrType = typeof(string) },
                new SdtPropertyInfo { NativeName = "Locked", DisplayName = "Locked", Category = "General", ClrType = typeof(string), ReadOnly = true },
                new SdtPropertyInfo { NativeName = "CollectionOnly", DisplayName = "Collection Only", Category = "General", ClrType = typeof(string), Applicable = false },
                new SdtPropertyInfo { NativeName = "Opaque", DisplayName = "Opaque", Category = "General", ClrType = typeof(DateTime) },
                new SdtPropertyInfo { NativeName = "Hidden", DisplayName = "Hidden", Category = "General", ClrType = typeof(string), Browsable = false },
                new SdtPropertyInfo { NativeName = "NoType", DisplayName = "No Type", Category = "General", ClrType = null },
                Incomplete()
            };
        }

        private static SdtPropertyInfo Incomplete()
        {
            var info = new SdtPropertyInfo { NativeName = "Mystery", DisplayName = "Mystery", Category = "General", ClrType = typeof(string) };
            info.MarkIncomplete("the allowed values could not be read");
            return info;
        }

        public SdtPropertyState ReadFresh()
        {
            Log.Add("read");
            if (++_reads > ReadsBeforeFailing)
                throw new SdtUserFacingException("FreshReadUnavailable", "a fresh read could not be confirmed");
            return Build();
        }

        public SdtPropertyState ApplyAndStage(SdtPropertyChange change, string expectedVersion, Func<SdtPropertyState, string> accept)
        {
            Log.Add("apply:" + (change.Reset ? "reset" : change.Raw));
            if (ConflictUnderLock) throw new SdtVersionConflictException("999:root");
            if (!string.Equals(expectedVersion, Version, StringComparison.Ordinal)) throw new SdtVersionConflictException(Version);
            var snapshot = Clone();
            if (FailBeforeSave != null) throw new SdtWriteFailure(FailPhase, FailBeforeSave);
            Mutate(change);
            SideEffect?.Invoke(this);
            if (FailAtSave != null)
            {
                Adopt(snapshot);
                throw new SdtWriteFailure(SdtWritePhase.Saving, FailAtSave);
            }
            Version = NextVersion();
            SdtPropertyState staged = Build();
            string reason = accept(staged);
            if (reason != null)
            {
                Adopt(snapshot);
                throw new SdtStageRejectedException(reason, staged);
            }
            if (FailAfterPersist != null) throw new SdtWriteFailure(SdtWritePhase.Saved, FailAfterPersist);
            AfterCommit?.Invoke(this);
            return staged;
        }

        public bool Restore(SdtPropertyChange inverse, SdtPropertyState expected)
        {
            Log.Add("restore:" + (inverse.Reset ? "reset" : inverse.Raw));
            if (!RestoreSucceeds) return false;
            Mutate(inverse);
            Version = NextVersion();
            return SdtItemPropertyEngine.SameStored(expected, Build());
        }

        public void NoteVerifiedWrite() { Log.Add("note"); }

        // ---- model

        public void Mutate(SdtPropertyChange change)
        {
            if (change.Reset) { Target.Remove(change.NativeName); return; }
            if (SwallowWrites) return;
            if (StoreUnderKey != null && change.NativeName == Native) { Target[StoreUnderKey] = change.Raw; return; }
            string raw = change.Raw;
            if (DropsDefaultValues && Defaults.TryGetValue(change.NativeName, out string def) && def == raw) Target.Remove(change.NativeName);
            else Target[change.NativeName] = raw;
        }

        public string NextVersion() { _versionCounter++; return _versionCounter + ":root"; }

        public SdtPropertyState Build()
        {
            var effective = new Dictionary<string, SdtEffectiveValue>(StringComparer.OrdinalIgnoreCase);
            foreach (var info in Catalog)
            {
                string def = Defaults.TryGetValue(info.NativeName, out string d) ? d : null;
                bool stored = Target.TryGetValue(info.NativeName, out string raw);
                if (!stored && StoreUnderKey != null && info.NativeName == Native) stored = Target.TryGetValue(StoreUnderKey, out raw);
                string value = stored ? raw : def;
                object typed = info.ClrType == typeof(bool) && value != null ? (object)bool.Parse(value) : value;
                effective[info.NativeName] = new SdtEffectiveValue
                {
                    Value = typed, Raw = value, IsDefault = !stored, DefaultValue = def, DefaultRaw = def
                };
            }
            return new SdtPropertyState
            {
                Version = Version,
                Members = Members(),
                OtherPartsEqual = OtherPartsEqual,
                TargetKind = Kind,
                TargetPath = new List<string> { TargetKey },
                Catalog = Catalog,
                Effective = effective
            };
        }

        public JObject Members()
        {
            return new JObject
            {
                [TargetKey] = new JObject { ["kind"] = Kind, ["properties"] = Props(Target) },
                ["other"] = new JObject { ["kind"] = "item", ["properties"] = Props(Neighbour) }
            };
        }

        private static JObject Props(Dictionary<string, string> source)
        {
            var o = new JObject();
            foreach (var pair in source.OrderBy(p => p.Key, StringComparer.Ordinal)) o[pair.Key] = pair.Value;
            return o;
        }

        private Tuple<Dictionary<string, string>, Dictionary<string, string>, string> Clone()
        {
            return Tuple.Create(new Dictionary<string, string>(Target), new Dictionary<string, string>(Neighbour), Version);
        }

        private void Adopt(Tuple<Dictionary<string, string>, Dictionary<string, string>, string> snapshot)
        {
            Target = new Dictionary<string, string>(snapshot.Item1);
            Neighbour = new Dictionary<string, string>(snapshot.Item2);
            Version = snapshot.Item3;
            AfterTransactionRollback?.Invoke(this);
        }
    }

    // ---- a duck-typed SDK object graph for SdtSdkReader (members must be public: the reader binds them dynamically)

    public sealed class FakeStringProperty
    {
        public string Name { get; set; }
        public string Value { get; set; }
    }

    public sealed class FakeDefinition
    {
        public string Name { get; set; }
        public string DisplayName { get; set; }
        public string Category { get; set; }
        public string Description { get; set; }
        public Type PropertyType { get; set; } = typeof(string);
        public object DefaultValue { get; set; }
        public List<object> Standard { get; } = new List<object>();
        public Dictionary<string, string> StandardNames { get; } = new Dictionary<string, string>();
        public bool ThrowOnStandardValues { get; set; }
        public bool HasStandardValues(object bag) { return Standard.Count > 0 || ThrowOnStandardValues; }
        public IEnumerable GetStandardValues(object bag)
        {
            if (ThrowOnStandardValues) throw new InvalidOperationException("the SDK build exposes the values differently");
            return Standard;
        }
        public string GetStandardNameFromValue(object value) { string label; return StandardNames.TryGetValue(value?.ToString() ?? "", out label) ? label : null; }
        public object GetDefaultValue(object manager) { return DefaultValue; }
    }

    public sealed class FakeDescriptor
    {
        public bool IsReadOnly { get; set; }
        public bool IsBrowsable { get; set; } = true;
        public bool IsApplicable { get; set; } = true;
        public FakeDefinition Definition { get; set; }
        public object Manager { get; } = new object();
    }

    public sealed class FakeEntity
    {
        public List<FakeDefinition> Definitions { get; } = new List<FakeDefinition>();
        public Dictionary<string, object> Stored { get; } = new Dictionary<string, object>();
        public Dictionary<string, bool> ReadOnly { get; } = new Dictionary<string, bool>();
        public Dictionary<string, bool> NotApplicable { get; } = new Dictionary<string, bool>();
        public HashSet<string> MissingDescriptor { get; } = new HashSet<string>();
        public HashSet<string> ThrowingDescriptor { get; } = new HashSet<string>();

        public IEnumerable<FakeDefinition> GetPropertiesDefinition() { return Definitions; }

        public FakeDescriptor GetPropertyDescriptor(string name)
        {
            if (ThrowingDescriptor.Contains(name)) throw new InvalidOperationException("descriptor unavailable");
            if (MissingDescriptor.Contains(name)) return null;
            var definition = Definitions.FirstOrDefault(d => d.Name == name);
            if (definition == null) return null;
            return new FakeDescriptor
            {
                Definition = definition,
                IsReadOnly = ReadOnly.ContainsKey(name),
                IsApplicable = !NotApplicable.ContainsKey(name)
            };
        }

        public object GetPropertyValue(string name)
        {
            object value;
            if (Stored.TryGetValue(name, out value)) return value;
            return Definitions.First(d => d.Name == name).DefaultValue;
        }

        public bool IsPropertyDefault(string name) { return !Stored.ContainsKey(name); }
        public void SetPropertyValue(string name, object value) { Stored[name] = value; }
        public void ResetProperty(string name) { Stored.Remove(name); }

        public IEnumerable<FakeStringProperty> SerializedProperties()
        {
            return Stored.Select(p => new FakeStringProperty { Name = p.Key, Value = Convert.ToString(p.Value) });
        }
    }

    public sealed class FakeMember
    {
        public string Name { get; set; }
        public bool IsLeafItem { get; set; } = true;
        public FakeEntity ItemEntity { get; } = new FakeEntity();
        public List<FakeMember> Items { get; } = new List<FakeMember>();
    }
}
