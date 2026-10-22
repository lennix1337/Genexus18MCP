using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// The rules of the regeneration unit that need no SDK object: which derived objects are
    /// helpers, whether every helper agrees with its callers, and whether the caller's version
    /// tokens still match what is in the KB. They work on <see cref="WwpObjectState"/> captures so
    /// they are covered by unit tests and behave the same on every GeneXus major.
    /// </summary>
    internal static class WwpDerivedUnit
    {
        private static readonly string[] CallerPartNames = { "Events", "Source", "Rules" };

        /// <summary>The parts of an object that can contain a call to a helper, with that object's Variables.</summary>
        internal static IEnumerable<WwpDerivedContract.CallerPart> CallerParts(WwpObjectState state)
        {
            state.Parts.TryGetValue("Variables", out string variables);
            foreach (string part in CallerPartNames)
                if (state.Parts.TryGetValue(part, out string text))
                    yield return new WwpDerivedContract.CallerPart
                    {
                        Object = state.Name, Part = part, Text = text, Variables = variables
                    };
        }

        /// <summary>
        /// A helper is a derived Procedure/DataProvider that something else in the unit calls. It is
        /// found by use rather than by name: the family names are a Settings value of the pattern.
        /// </summary>
        internal static bool IsHelper(WwpObjectState candidate, IEnumerable<WwpObjectState> unit)
        {
            if (!string.Equals(candidate.Type, "Procedure", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(candidate.Type, "DataProvider", StringComparison.OrdinalIgnoreCase))
                return false;
            return unit.Where(other => !ReferenceEquals(other, candidate)).SelectMany(CallerParts)
                .Any(part => WwpDerivedContract.FindCalls(part.Text, part.Object, part.Part, candidate.Name).Count > 0);
        }

        /// <summary>
        /// The call contract of every helper in <paramref name="helpers"/> against every other object
        /// in <paramref name="unit"/> (the parent included). <c>consistent</c> holds only when every
        /// helper agrees with every one of its callers.
        /// </summary>
        internal static JObject EvaluateContract(IEnumerable<WwpObjectState> helpers, IEnumerable<WwpObjectState> unit)
        {
            List<WwpObjectState> everyone = unit.ToList();
            var evaluations = new JArray();
            bool consistent = true;
            foreach (WwpObjectState helper in helpers)
            {
                helper.Parts.TryGetValue("Rules", out string rules);
                helper.Parts.TryGetValue("Variables", out string variables);
                JObject evaluation = WwpDerivedContract.Evaluate(helper.Name, rules, variables,
                    everyone.Where(other => !ReferenceEquals(other, helper)).SelectMany(CallerParts));
                consistent &= (bool)evaluation["consistent"];
                evaluations.Add(evaluation);
            }
            return new JObject { ["consistent"] = consistent, ["helpers"] = evaluations };
        }

        internal sealed class VersionedObject
        {
            internal string Key;
            internal string Name;
            internal string Current;
            /// <summary>The token the caller passed outside <c>expectedVersions</c> (the host's <c>baseVersion</c>).</summary>
            internal string Fallback;
        }

        /// <summary>
        /// Compares the tokens the caller supplied with the tokens read now. A token is looked up by its
        /// <c>Type:Name</c> key and, when the name is unambiguous in the unit, by the bare name. Returns the
        /// objects whose token no longer matches; <paramref name="missing"/> receives the keys with no token
        /// at all, which the caller must treat as a refusal rather than as "do not check".
        /// </summary>
        internal static List<JObject> CompareVersions(IReadOnlyList<VersionedObject> objects, JObject supplied,
            out List<string> missing)
        {
            var stale = new List<JObject>();
            var absent = new List<string>();
            Dictionary<string, int> nameCount = objects.GroupBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
            foreach (VersionedObject item in objects)
            {
                string expected = supplied?[item.Key]?.ToString();
                if (string.IsNullOrWhiteSpace(expected) && nameCount[item.Name] == 1)
                    expected = supplied?[item.Name]?.ToString();
                if (string.IsNullOrWhiteSpace(expected)) expected = item.Fallback;
                if (string.IsNullOrWhiteSpace(expected)) { absent.Add(item.Key); continue; }
                if (!string.Equals(expected, item.Current, StringComparison.Ordinal))
                    stale.Add(new JObject { ["object"] = item.Key, ["expected"] = expected, ["current"] = item.Current });
            }
            missing = absent;
            return stale;
        }
    }
}
