using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// The text of one KB object as the regeneration unit sees it: every part that was read, by
    /// part name. Plain data - the SDK object it came from is not referenced - so two captures of
    /// the same object can be compared, hashed and turned into a restore plan without the SDK.
    /// </summary>
    internal sealed class WwpObjectState
    {
        internal string Name;
        internal string Type;
        internal Guid Guid;
        internal string QualifiedName;
        internal readonly SortedDictionary<string, string> Parts = new SortedDictionary<string, string>(StringComparer.Ordinal);

        internal string Key => Type + ":" + Name;

        /// <summary>All parts as one deterministic text, the input of an object's content fingerprint.</summary>
        internal string Fingerprint()
        {
            var text = new StringBuilder();
            foreach (KeyValuePair<string, string> part in Parts)
                text.Append('[').Append(part.Key).Append("]\n").Append(part.Value ?? string.Empty).Append('\n');
            return text.ToString();
        }

        internal JArray PartSummary() => new JArray(Parts.Select(p => new JObject
        {
            ["part"] = p.Key,
            ["length"] = p.Value?.Length ?? 0,
            ["sha256"] = SdkReflection.Sha256Hex(p.Value ?? string.Empty)
        }));
    }

    /// <summary>What happened to the derived objects between two captures.</summary>
    internal sealed class WwpDerivedDiff
    {
        internal readonly List<WwpObjectState> Created = new List<WwpObjectState>();
        internal readonly List<WwpObjectState> Removed = new List<WwpObjectState>();
        internal readonly List<ChangedObject> Changed = new List<ChangedObject>();
        internal int Unchanged;

        internal sealed class ChangedObject
        {
            internal WwpObjectState Before;
            internal WwpObjectState After;
            internal List<string> Parts = new List<string>();
        }

        internal static WwpDerivedDiff Compare(IEnumerable<WwpObjectState> before, IEnumerable<WwpObjectState> after)
        {
            var diff = new WwpDerivedDiff();
            Dictionary<Guid, WwpObjectState> previous = before.ToDictionary(o => o.Guid);
            var seen = new HashSet<Guid>();
            foreach (WwpObjectState current in after)
            {
                seen.Add(current.Guid);
                if (!previous.TryGetValue(current.Guid, out WwpObjectState old)) { diff.Created.Add(current); continue; }
                List<string> parts = ChangedParts(old, current);
                if (parts.Count == 0) diff.Unchanged++;
                else diff.Changed.Add(new ChangedObject { Before = old, After = current, Parts = parts });
            }
            diff.Removed.AddRange(previous.Values.Where(o => !seen.Contains(o.Guid)));
            return diff;
        }

        /// <summary>
        /// The parts whose text differs. A part present on one side only counts: an absent part reads as
        /// null, and a captured part is never null (an empty part is the empty string), so the two differ.
        /// </summary>
        internal static List<string> ChangedParts(WwpObjectState before, WwpObjectState after)
        {
            return before.Parts.Keys.Union(after.Parts.Keys, StringComparer.Ordinal)
                .Where(part =>
                {
                    before.Parts.TryGetValue(part, out string a);
                    after.Parts.TryGetValue(part, out string b);
                    return !string.Equals(a, b, StringComparison.Ordinal);
                })
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>
        /// The changed objects that fall outside <paramref name="allowed"/> (matched by
        /// <see cref="WwpObjectState.Key"/>), plus every object created or removed: those are not a
        /// "helpers only" outcome whichever objects they are.
        /// </summary>
        internal List<string> OutOfScope(ISet<string> allowed)
        {
            var names = Changed.Where(c => !allowed.Contains(c.After.Key)).Select(c => c.After.Key).ToList();
            names.AddRange(Created.Select(o => o.Key));
            names.AddRange(Removed.Select(o => o.Key));
            return names;
        }
    }
}
