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
    /// <c>var:N</c>. A user control property binds to one the same way, as a <c>&lt;Value&gt;var:N&lt;/Value&gt;</c>
    /// inside its escaped payload. The id is assigned when the variable is declared in its object, so the same name can
    /// carry a different id in another object. A text clone declares the variables in the order it reads them, and the
    /// target numbers them in that order, so a binding keeps its number and reaches whatever variable now has it, with
    /// no error. The remap changes only the number of those two forms, to the id of the variable with the same name in
    /// the target. A number that no source variable carries is kept and reported. A source variable that the target does
    /// not declare is refused, because no binding to it would be right.
    /// </remarks>
    internal static class SaveAsVariableIdRemap
    {
        // A tag, with its quoted attribute values read whole, so a '>' inside a value does not end the tag early.
        private static readonly Regex TagPattern = new Regex(
            "<(?:[^<>\"']|\"[^\"]*\"|'[^']*')*>", RegexOptions.CultureInvariant);

        // One binding of the two forms the remap rebinds, matched in one pass inside a tag: an attribute value that is
        // exactly var:N (group 1 is the attribute name and '=', group 2 the quote, group 3 the id), or the <Value> of a
        // user control property in its escaped payload, when that <Value> is exactly var:N (group 4 is the id). A number
        // of more than nine digits is not a variable id.
        private static readonly Regex BindingPattern = new Regex(
            "(\\s[^\\s=<>\"']+\\s*=\\s*)([\"'])var:(\\d{1,9})\\2|&lt;Value&gt;var:(\\d{1,9})&lt;/Value&gt;",
            RegexOptions.CultureInvariant);

        // Any var:N token of a text, so that a binding the remap does not rebind is still reported.
        private static readonly Regex VarTokenPattern = new Regex(
            "(?<![A-Za-z0-9_])var:(\\d{1,9})(?![A-Za-z0-9_])", RegexOptions.CultureInvariant);

        /// <summary>The most other references a remap reports.</summary>
        public const int MaxOtherReferences = 20;

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

            /// <summary>
            /// The var:N numbers of the text that the remap does not rebind, distinct and in the order they appear, at most
            /// <see cref="MaxOtherReferences"/>. Their text is kept as it is.
            /// </summary>
            public List<int> OtherReferences { get; } = new List<int>();

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
        /// Replaces each binding of the WebForm, an attribute value <c>var:N</c> or a payload <c>&lt;Value&gt;var:N&lt;/Value&gt;</c>,
        /// by <c>var:M</c>, where N is the source id and M the target id of the variable with the same name. Nothing else
        /// in the text changes.
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
            // One pass: a replacement is never matched again, so a chain of ids is not followed.
            string rewritten = TagPattern.Replace(webFormXml, tag => BindingPattern.Replace(tag.Value, binding =>
            {
                // Group 3 is set for an attribute value, group 4 for a payload <Value>.
                bool attribute = binding.Groups[3].Success;
                int sourceId = int.Parse(attribute ? binding.Groups[3].Value : binding.Groups[4].Value, CultureInfo.InvariantCulture);

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
                string id = targetId.ToString(CultureInfo.InvariantCulture);
                return attribute
                    ? binding.Groups[1].Value + binding.Groups[2].Value + "var:" + id + binding.Groups[2].Value
                    : "&lt;Value&gt;var:" + id + "&lt;/Value&gt;";
            }));

            // The bindings the remap handled are blanked out first, so each var:N left in the text is one it does not know.
            string unhandled = TagPattern.Replace(webFormXml, tag => BindingPattern.Replace(tag.Value, " "));
            var other = new List<int>();
            foreach (Match token in VarTokenPattern.Matches(unhandled))
            {
                int id = int.Parse(token.Groups[1].Value, CultureInfo.InvariantCulture);
                if (other.Count < MaxOtherReferences && !other.Contains(id)) other.Add(id);
            }

            // A refused remap writes nothing, so it reports nothing either.
            if (missing != null) return new Outcome { Xml = webFormXml, MissingInTarget = missing };

            outcome.Remapped = remapped;
            outcome.Xml = remapped > 0 ? rewritten : webFormXml;
            outcome.Unmapped.AddRange(unmapped);
            outcome.OtherReferences.AddRange(other);
            return outcome;
        }
    }
}
