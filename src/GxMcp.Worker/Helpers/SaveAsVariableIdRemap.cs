using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// Rebinds the controls of a cloned WebForm to the variables that carry the same names.
    /// </summary>
    /// <remarks>
    /// A WebForm binds each control to a variable by the variable's internal id, written as the attribute value
    /// <c>var:N</c>. The id is assigned when the variable is declared in its object, so the same name can carry a
    /// different id in another object. A text clone declares the variables in the order it reads them, and the
    /// target numbers them in that order, so a control keeps its number and reaches whatever variable now has it,
    /// with no error. The remap changes only the number of each <c>var:N</c> attribute value, to the id of the variable
    /// with the same name in the target. A number that no source variable carries is kept and reported. A source
    /// variable that the target does not declare is refused, because no binding to it would be right.
    /// </remarks>
    internal static class SaveAsVariableIdRemap
    {
        // A tag, with its quoted attribute values read whole, so a '>' inside a value does not end the tag early.
        private static readonly Regex TagPattern = new Regex(
            "<(?:[^<>\"']|\"[^\"]*\"|'[^']*')*>", RegexOptions.CultureInvariant);

        // One attribute value whose text is exactly var:N. Group 1 is the attribute name and '=', group 2 the quote,
        // group 3 the id.
        private static readonly Regex VarAttributePattern = new Regex(
            "(\\s[^\\s=<>\"']+\\s*=\\s*)([\"'])var:(\\d+)\\2", RegexOptions.CultureInvariant);

        /// <summary>A variable's name and its internal id within one object.</summary>
        internal sealed class NamedId
        {
            public NamedId(string name, int id)
            {
                Name = name;
                Id = id;
            }

            public string Name { get; private set; }

            public int Id { get; private set; }
        }

        /// <summary>The result of a remap.</summary>
        internal sealed class Outcome
        {
            /// <summary>The text to write. The input itself when nothing changed or when the remap is refused.</summary>
            public string Xml { get; set; }

            /// <summary>How many bindings changed their id.</summary>
            public int Remapped { get; set; }

            /// <summary>Source ids that no source variable carries. Their bindings are kept as they were.</summary>
            public List<int> Unmapped { get; } = new List<int>();

            /// <summary>The source variable that the target does not declare, or null when the remap is accepted.</summary>
            public string MissingInTarget { get; set; }

            /// <summary>Why the remap did not run, or null when it ran. The text is then written as it is.</summary>
            public string Skipped { get; set; }
        }

        /// <summary>The skip reason when a variable's id cannot be read on its primary path.</summary>
        public const string IdUnavailable = "IdUnavailable";

        /// <summary>
        /// The variable's id as its primary path reads it: the <c>Id</c> property, and nothing else. A variable whose id
        /// cannot be read, or reads as zero or less, gives -1. Its position in an enumeration is never an id, so it is
        /// not given in its place.
        /// </summary>
        public static int PrimaryVariableId(object variable)
        {
            if (variable == null) return -1;
            try
            {
                PropertyInfo idProperty = variable.GetType().GetProperty("Id", BindingFlags.Public | BindingFlags.Instance);
                if (idProperty == null || !idProperty.CanRead) return -1;

                object raw = idProperty.GetValue(variable);
                if (raw is int value) return value > 0 ? value : -1;
                if (raw is short shortValue) return shortValue > 0 ? shortValue : -1;
                if (raw is long longValue) return longValue > 0 && longValue <= int.MaxValue ? (int)longValue : -1;
                return -1;
            }
            catch
            {
                return -1;
            }
        }

        /// <summary>
        /// The source's names by id. An id that two variables share is left out, so a binding to it is reported as
        /// unmapped instead of being rebound to one of them.
        /// </summary>
        public static IDictionary<int, string> SourceNamesById(IEnumerable<NamedId> source)
        {
            var names = new Dictionary<int, string>();
            var shared = new HashSet<int>();
            foreach (NamedId variable in source ?? Enumerable.Empty<NamedId>())
            {
                if (variable == null || variable.Id <= 0 || string.IsNullOrEmpty(variable.Name)) continue;
                if (names.ContainsKey(variable.Id)) shared.Add(variable.Id);
                else names[variable.Id] = variable.Name;
            }

            foreach (int id in shared) names.Remove(id);
            return names;
        }

        /// <summary>The target's ids by name. Names compare without regard to case, as the object model does.</summary>
        public static IDictionary<string, int> TargetIdsByName(IEnumerable<NamedId> target)
        {
            var ids = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (NamedId variable in target ?? Enumerable.Empty<NamedId>())
            {
                if (variable == null || variable.Id <= 0 || string.IsNullOrEmpty(variable.Name)) continue;
                if (!ids.ContainsKey(variable.Name)) ids[variable.Name] = variable.Id;
            }

            return ids;
        }

        /// <summary>
        /// The remap of a clone's WebForm, from the variables of the source object and of the target object as the
        /// target holds them after its Variables were written.
        /// </summary>
        public static Outcome ForClone(
            string webFormXml,
            IEnumerable<NamedId> sourceVariables,
            IEnumerable<NamedId> targetVariables)
        {
            // A binding can only be matched to a variable through its id. When one id of either object cannot be
            // read, nothing is rebound: the text is written as it is and the outcome says why.
            List<NamedId> source = sourceVariables?.ToList();
            List<NamedId> target = targetVariables?.ToList();
            if (source == null || target == null || source.Any(IsUnreadable) || target.Any(IsUnreadable))
                return new Outcome { Xml = webFormXml, Skipped = IdUnavailable };

            return Apply(
                webFormXml,
                SourceNamesById(source),
                TargetIdsByName(target));
        }

        private static bool IsUnreadable(NamedId variable)
        {
            return variable == null || variable.Id <= 0;
        }

        /// <summary>
        /// Replaces each <c>var:N</c> attribute value of the WebForm by <c>var:M</c>, where N is the source id and M the
        /// target id of the variable with the same name. Nothing else in the text changes.
        /// </summary>
        public static Outcome Apply(
            string webFormXml,
            IDictionary<int, string> sourceNamesById,
            IDictionary<string, int> targetIdsByName)
        {
            var outcome = new Outcome { Xml = webFormXml };
            if (string.IsNullOrEmpty(webFormXml)) return outcome;

            int remapped = 0;
            string missing = null;
            var unmapped = new List<int>();
            string rewritten = TagPattern.Replace(webFormXml, tag => VarAttributePattern.Replace(tag.Value, binding =>
            {
                int sourceId;
                if (!int.TryParse(binding.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out sourceId))
                    return binding.Value;

                string name;
                if (!sourceNamesById.TryGetValue(sourceId, out name))
                {
                    if (!unmapped.Contains(sourceId)) unmapped.Add(sourceId);
                    return binding.Value;
                }

                int targetId;
                if (!targetIdsByName.TryGetValue(name, out targetId))
                {
                    if (missing == null) missing = name;
                    return binding.Value;
                }

                if (targetId == sourceId) return binding.Value;

                remapped++;
                return binding.Groups[1].Value + binding.Groups[2].Value
                    + "var:" + targetId.ToString(CultureInfo.InvariantCulture) + binding.Groups[2].Value;
            }));

            outcome.Unmapped.AddRange(unmapped);
            outcome.MissingInTarget = missing;
            // A refused remap writes nothing, so it reports no rebinding either.
            outcome.Remapped = missing == null ? remapped : 0;
            outcome.Xml = missing == null && remapped > 0 ? rewritten : webFormXml;
            return outcome;
        }
    }
}
