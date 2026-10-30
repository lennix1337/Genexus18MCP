using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// One object as the whole-KB inventory of the protected Events save read it. <see cref="Fingerprint"/> is what
    /// the audit has always compared to decide that an object differs; the other members say what differs.
    /// </summary>
    internal sealed class EventsInventoryEntry
    {
        internal EventsInventoryEntry(
            int version, long stampTicks, string parent, string name, string typeId, string typeName, string lastModifiedBy)
        {
            Version = version;
            StampTicks = stampTicks;
            Parent = parent;
            Name = name;
            TypeId = typeId;
            TypeName = typeName;
            LastModifiedBy = lastModifiedBy;
        }

        internal int Version { get; }

        /// <summary>The SDK's <c>LastUpdate</c> as <see cref="SdkTimestampNormalizer.NormalizeUtc"/> labels it, in ticks.</summary>
        internal long StampTicks { get; }

        internal string Parent { get; }
        internal string Name { get; }
        internal string TypeId { get; }
        internal string TypeName { get; }
        internal string LastModifiedBy { get; }

        internal string Fingerprint => Version.ToString(CultureInfo.InvariantCulture) + ":"
            + StampTicks.ToString(CultureInfo.InvariantCulture) + ":" + Parent + ":" + Name + ":" + TypeId;

        internal bool SameStructure(EventsInventoryEntry other)
            => string.Equals(Parent, other.Parent, StringComparison.Ordinal)
                && string.Equals(Name, other.Name, StringComparison.Ordinal)
                && string.Equals(TypeId, other.TypeId, StringComparison.Ordinal);
    }

    /// <summary>
    /// The interval, in the clock the SDK stamps <c>LastUpdate</c> with, in which a write made by one protected save
    /// can carry its stamp. It is derived from the save itself, not assumed: the target's own new stamp was assigned
    /// by this process somewhere in the span in which it was writing, so it fixes the offset between that clock and
    /// the Worker's. An object whose new stamp lies outside the interval was not written by this save.
    ///
    /// The interval is only trusted when that offset is one of the two a clock can have (the Worker's own UTC clock,
    /// or its local wall clock, which the SDK labels as UTC). Anything else, and every other doubt, returns an
    /// unavailable window, and an unavailable window clears no object.
    /// </summary>
    internal sealed class EventsSaveWindow
    {
        /// <summary>Slack on both ends for stamps that are rounded or taken a moment before the clock is read.</summary>
        internal static readonly TimeSpan Margin = TimeSpan.FromSeconds(2);

        /// <summary>How far the measured offset may sit from a clock the Worker knows.</summary>
        internal static readonly TimeSpan ClockTolerance = TimeSpan.FromSeconds(5);

        private EventsSaveWindow(bool calibrated, string reason, long lowTicks, long? highTicks, long offsetTicks)
        {
            Calibrated = calibrated;
            Reason = reason;
            LowTicks = lowTicks;
            HighTicks = highTicks;
            OffsetTicks = offsetTicks;
        }

        internal bool Calibrated { get; }

        /// <summary>Why the window is unavailable; null when it is calibrated.</summary>
        internal string Reason { get; }

        internal long LowTicks { get; }

        /// <summary>The end of the interval; null while the transaction is still open.</summary>
        internal long? HighTicks { get; }

        /// <summary>The middle of the measured offset between the stamp clock and the Worker's UTC clock.</summary>
        internal long OffsetTicks { get; }

        internal static EventsSaveWindow Unavailable(string reason) => new EventsSaveWindow(false, reason, 0, null, 0);

        /// <param name="transactionStartTicks">Worker UTC clock when the transaction opened.</param>
        /// <param name="transactionEndTicks">Worker UTC clock when it closed; null while it is still open.</param>
        /// <param name="writeSpanStartTicks">Worker UTC clock before the first call that can write the target.</param>
        /// <param name="writeSpanEndTicks">Worker UTC clock after the last call that was made before the stamp was read.</param>
        /// <param name="targetStampTicks">The target's new stamp, read at <paramref name="writeSpanEndTicks"/>.</param>
        /// <param name="localUtcOffsetTicks">The Worker's local offset from UTC.</param>
        internal static EventsSaveWindow Calibrate(
            long transactionStartTicks, long? transactionEndTicks,
            long writeSpanStartTicks, long writeSpanEndTicks,
            long targetStampTicks, long localUtcOffsetTicks)
        {
            if (writeSpanEndTicks < writeSpanStartTicks
                || writeSpanStartTicks < transactionStartTicks
                || (transactionEndTicks.HasValue && transactionEndTicks.Value < writeSpanEndTicks))
                return Unavailable("SaveTimelineInvalid");

            long offsetLow = targetStampTicks - writeSpanEndTicks;
            long offsetHigh = targetStampTicks - writeSpanStartTicks;
            long tolerance = ClockTolerance.Ticks;
            if (!Covers(offsetLow, offsetHigh, 0, tolerance) && !Covers(offsetLow, offsetHigh, localUtcOffsetTicks, tolerance))
                return Unavailable("StampClockNotComparable");

            return new EventsSaveWindow(
                true, null,
                transactionStartTicks + offsetLow - Margin.Ticks,
                transactionEndTicks.HasValue ? transactionEndTicks.Value + offsetHigh + Margin.Ticks : (long?)null,
                offsetLow + (offsetHigh - offsetLow) / 2);
        }

        private static bool Covers(long offsetLow, long offsetHigh, long clockOffset, long tolerance)
            => offsetLow - tolerance <= clockOffset && clockOffset <= offsetHigh + tolerance;

        internal JObject Describe()
        {
            var window = new JObject { ["calibrated"] = Calibrated };
            if (!Calibrated)
            {
                window["reason"] = Reason;
                return window;
            }
            window["stampClockOffsetSeconds"] = Math.Round(TimeSpan.FromTicks(OffsetTicks).TotalSeconds, 1);
            window["from"] = Iso(LowTicks);
            if (HighTicks.HasValue)
            {
                window["to"] = Iso(HighTicks.Value);
                window["seconds"] = Math.Round(TimeSpan.FromTicks(HighTicks.Value - LowTicks).TotalSeconds, 1);
            }
            else window["open"] = true;
            return window;
        }

        internal static string Iso(long ticks)
        {
            if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks) return null;
            return new DateTime(ticks, DateTimeKind.Utc).ToString("o", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>One other object whose inventory entry differs, and what the save window says about it.</summary>
    internal sealed class EventsOtherObjectChange
    {
        internal EventsOtherObjectChange(Guid guid, EventsInventoryEntry before, EventsInventoryEntry after, bool concurrent, string reason)
        {
            Guid = guid;
            Before = before;
            After = after;
            Concurrent = concurrent;
            Reason = reason;
        }

        internal Guid Guid { get; }
        internal EventsInventoryEntry Before { get; }
        internal EventsInventoryEntry After { get; }

        /// <summary>True only when the save cannot have written it: another process did.</summary>
        internal bool Concurrent { get; }

        internal string Reason { get; }

        internal string Kind => Before == null ? "created" : After == null ? "deleted" : "modified";

        internal JObject ToJson()
        {
            EventsInventoryEntry named = After ?? Before;
            var json = new JObject
            {
                ["guid"] = Guid.ToString(),
                ["name"] = named.Name,
                ["type"] = named.TypeName,
                ["change"] = Kind,
                ["verdict"] = Concurrent ? "concurrent" : "blocking",
                ["reason"] = Reason
            };
            if (Before != null)
            {
                json["versionBefore"] = Before.Version;
                json["lastUpdateBefore"] = EventsSaveWindow.Iso(Before.StampTicks);
            }
            if (After != null)
            {
                json["versionAfter"] = After.Version;
                json["lastUpdateAfter"] = EventsSaveWindow.Iso(After.StampTicks);
            }
            string author = After?.LastModifiedBy ?? Before?.LastModifiedBy;
            if (!string.IsNullOrEmpty(author)) json["lastModifiedBy"] = author;
            return json;
        }
    }

    /// <summary>
    /// The other objects that differ between two whole-KB inventories, split into the ones the protected save can have
    /// written or cannot be shown not to have written (<see cref="Blocking"/>) and the ones another process wrote
    /// outside the save window (<see cref="Concurrent"/>). What counts as different is unchanged: the inventory
    /// fingerprint, exactly as <see cref="EventsSaveIsolation.ChangedOthers"/> compares it.
    ///
    /// An object is concurrent only when every one of these holds: it exists in both inventories at the same place
    /// with the same name and type; its version advanced by exactly one (so a single save by someone else, not that
    /// plus one of ours); its stamp advanced; the save window is calibrated; and the new stamp lies before the window
    /// or, once the transaction has closed, after it. Everything else blocks, so a doubt never clears an object.
    /// </summary>
    internal sealed class EventsOtherObjectChanges
    {
        /// <summary>Largest number of changes described in full per verdict; the counts are always complete.</summary>
        internal const int MaxDescribed = 50;

        private EventsOtherObjectChanges(IReadOnlyList<EventsOtherObjectChange> blocking, IReadOnlyList<EventsOtherObjectChange> concurrent)
        {
            Blocking = blocking;
            Concurrent = concurrent;
        }

        internal IReadOnlyList<EventsOtherObjectChange> Blocking { get; }
        internal IReadOnlyList<EventsOtherObjectChange> Concurrent { get; }

        internal static EventsOtherObjectChanges Classify(
            IReadOnlyDictionary<Guid, EventsInventoryEntry> before,
            IReadOnlyDictionary<Guid, EventsInventoryEntry> after,
            Guid target,
            EventsSaveWindow window)
        {
            var blocking = new List<EventsOtherObjectChange>();
            var concurrent = new List<EventsOtherObjectChange>();
            JArray differing = EventsSaveIsolation.ChangedOthers(Fingerprints(before), Fingerprints(after), target);
            foreach (JToken token in differing)
            {
                Guid guid = Guid.Parse(token.ToString());
                before.TryGetValue(guid, out EventsInventoryEntry was);
                after.TryGetValue(guid, out EventsInventoryEntry now);
                string reason = Judge(was, now, window, out bool isConcurrent);
                (isConcurrent ? concurrent : blocking).Add(new EventsOtherObjectChange(guid, was, now, isConcurrent, reason));
            }
            return new EventsOtherObjectChanges(Ordered(blocking), Ordered(concurrent));
        }

        private static string Judge(EventsInventoryEntry was, EventsInventoryEntry now, EventsSaveWindow window, out bool concurrent)
        {
            concurrent = false;
            if (was == null) return "Created";
            if (now == null) return "Deleted";
            if (!was.SameStructure(now)) return "MovedRenamedOrRetyped";
            if (now.Version != was.Version + 1) return "VersionNotAdvancedByOne";
            if (now.StampTicks <= was.StampTicks) return "StampNotAdvanced";
            if (window == null || !window.Calibrated) return "SaveWindowUnavailable";
            if (now.StampTicks < window.LowTicks)
            {
                concurrent = true;
                return "BeforeSaveWindow";
            }
            if (window.HighTicks.HasValue && now.StampTicks > window.HighTicks.Value)
            {
                concurrent = true;
                return "AfterSaveWindow";
            }
            return "InsideSaveWindow";
        }

        private static Dictionary<Guid, string> Fingerprints(IReadOnlyDictionary<Guid, EventsInventoryEntry> inventory)
            => inventory.ToDictionary(entry => entry.Key, entry => entry.Value.Fingerprint);

        private static IReadOnlyList<EventsOtherObjectChange> Ordered(List<EventsOtherObjectChange> changes)
            => changes.OrderBy(c => (c.After ?? c.Before).Name, StringComparer.Ordinal).ThenBy(c => c.Guid).ToList();

        /// <summary>The GUIDs of the blocking changes: the <c>unexpectedChangedObjects</c> field of the receipt.</summary>
        internal JArray BlockingGuids() => new JArray(Blocking.Select(c => c.Guid.ToString()));

        internal JArray BlockingDetails() => Describe(Blocking);

        internal JArray ConcurrentDetails() => Describe(Concurrent);

        private static JArray Describe(IReadOnlyList<EventsOtherObjectChange> changes)
            => new JArray(changes.Take(MaxDescribed).Select(c => c.ToJson()));
    }
}
