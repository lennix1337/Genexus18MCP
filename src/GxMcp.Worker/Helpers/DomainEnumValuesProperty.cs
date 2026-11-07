using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Artech.Genexus.Common.Objects;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// Marks a stand-in for an SDK Domain. A test double implements it so the property paths that act on a Domain only can
    /// be driven without a Knowledge Base; the SDK's own <see cref="Domain"/> needs no marker.
    /// </summary>
    internal interface IDomainEnumValuesTarget
    {
    }

    /// <summary>
    /// EnumValues of a Domain as a property of <c>genexus_properties</c>: one JSON list of {name, value, description}, the
    /// text <c>action=get</c> already returns. The SDK keeps the enumerated values in the property bag through
    /// <c>Properties.ATT.SetEnumValues</c> and does not list them among the descriptors a Domain exposes, so the generic
    /// descriptor lookup answers PropertyNotFound for a name that <c>get</c> offered as writable. This class is the typed
    /// adapter beside that lookup. A write replaces the whole list; the read, the write and the verification all use the same
    /// canonical text, so a value the SDK did not keep is a mismatch and not a success.
    ///
    /// A write is accepted for a Domain that already lists enumerated values, which is exactly where <c>get</c> lists the
    /// property (<see cref="Applies"/>). A Domain without enumerated values keeps answering PropertyNotFound, and so does
    /// every other object. Reading, applying and verifying go by <see cref="Targets"/> instead: a rollback must still be
    /// able to put a list back into a Domain whose list the SDK emptied.
    /// </summary>
    internal static class DomainEnumValuesProperty
    {
        internal const string PropertyName = "EnumValues";

        internal static bool IsName(string propName)
            => string.Equals(propName?.Trim(), PropertyName, StringComparison.OrdinalIgnoreCase);

        internal static bool IsDomain(object container)
            => container is Domain || container is IDomainEnumValuesTarget;

        /// <summary>True for the EnumValues of a Domain, whether the Domain holds values or not.</summary>
        internal static bool Targets(object container, string propName)
            => IsName(propName) && IsDomain(container);

        /// <summary>
        /// True for the EnumValues of a Domain that lists enumerated values: the case where a write is accepted. Only the
        /// validation of a write asks for it, so a Domain without values keeps answering PropertyNotFound.
        /// </summary>
        internal static bool Applies(object container, string propName)
            => Targets(container, propName) && DomainPropertyApplier.ReadEnumValues(container).Count > 0;

        /// <summary>
        /// The enumerated values the Domain holds, as the canonical JSON list (<c>[]</c> when it holds none, so a list the
        /// SDK emptied is a mismatch and not an unreadable value); null when the container is not a Domain.
        /// </summary>
        internal static string Read(object container)
            => IsDomain(container) ? DomainPropertyApplier.ReadEnumValues(container).ToString(Formatting.None) : null;

        /// <summary>
        /// Checks a requested list and turns it into the values to write. Returns null when the list can be written, else
        /// the refusal, which starts with <c>InvalidPropertyValue</c> like every refused value. The rules are the ones the
        /// SDK enforces by dropping the whole list without an error (measured on GeneXus 18): names and descriptions are
        /// compared without regard to case, values with it.
        /// </summary>
        internal static string Parse(string raw, out List<DomainEnumValueSpec> specs)
        {
            specs = null;
            if (string.IsNullOrWhiteSpace(raw))
                return Invalid("a JSON list of {name, value, description} items is required.");

            JToken token;
            try { token = ParseJson(raw); }
            catch (JsonException ex) { return Invalid("the value is not valid JSON (" + FirstLine(ex.Message) + ")."); }

            if (!(token is JArray array))
                return Invalid("a JSON list is required, e.g. [{\"name\":\"Active\",\"value\":\"A\",\"description\":\"Active\"}].");
            if (array.Count == 0)
                return Invalid("the list is empty; a Domain keeps at least one enumerated value.");

            var result = new List<DomainEnumValueSpec>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var values = new HashSet<string>(StringComparer.Ordinal);
            var descriptions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < array.Count; i++)
            {
                string position = "item " + (i + 1);
                if (!(array[i] is JObject item))
                    return Invalid(position + " is not an object with name, value and description.");

                string name = ScalarText(item["name"]);
                if (string.IsNullOrEmpty(name)) return Invalid(position + " has no name.");
                string nameProblem = NameProblem(name);
                if (nameProblem != null) return Invalid(position + " ('" + name + "') " + nameProblem);
                string value = ScalarText(item["value"]);
                if (value == null) return Invalid(position + " ('" + name + "') has no value.");
                // The same default the creation path uses: an empty description is the name, which is unique by itself.
                string description = ScalarText(item["description"]);
                if (string.IsNullOrEmpty(description)) description = name;

                if (!names.Add(name)) return Invalid("the name '" + name + "' appears more than once (names are compared without regard to case).");
                if (!values.Add(value)) return Invalid("the value '" + value + "' appears more than once.");
                if (!descriptions.Add(description))
                    return Invalid("the description '" + description + "' appears more than once (descriptions are compared without regard to case); the SDK refuses the whole list when two values share a description.");

                result.Add(new DomainEnumValueSpec { Name = name, Value = value, Description = description });
            }

            specs = result;
            return null;
        }

        /// <summary>The canonical text of a list of values: the form <see cref="Read"/> returns for the same values.</summary>
        internal static string Canonical(IEnumerable<DomainEnumValueSpec> specs)
        {
            var array = new JArray();
            foreach (DomainEnumValueSpec spec in specs)
            {
                var item = new JObject { ["name"] = spec.Name };
                if (spec.Value != null) item["value"] = spec.Value;
                if (spec.Description != null) item["description"] = spec.Description;
                array.Add(item);
            }
            return array.ToString(Formatting.None);
        }

        /// <summary>
        /// The requested text in the canonical form, so it can be compared with what the SDK holds after the save. Text that
        /// is not a writable list is returned as it came: the write was refused before it got here.
        /// </summary>
        internal static string CanonicalOrSame(string raw)
            => Parse(raw, out List<DomainEnumValueSpec> specs) == null ? Canonical(specs) : raw;

        /// <summary>
        /// True when the requested list is exactly what the SDK holds. The comparison is exact on purpose: the one the other
        /// properties use ignores case, and a description corrected from "Estado de conservação" to "Estado de Conservação" must not be
        /// reported as saved when the SDK kept the old text. A request that could not be written never matches.
        /// </summary>
        internal static bool Matches(string requested, string persisted)
            => Parse(requested, out List<DomainEnumValueSpec> specs) == null
               && string.Equals(Canonical(specs), persisted, StringComparison.Ordinal);

        /// <summary>
        /// Replaces the enumerated values of the Domain with the requested list and marks the object changed. Throws when the
        /// list is refused or the SDK builds fewer values than were sent. <paramref name="applier"/> is the SDK helper; a test
        /// passes its own.
        /// </summary>
        internal static void Apply(object container, string raw, Func<object, IList<DomainEnumValueSpec>, int> applier = null)
        {
            string refusal = Parse(raw, out List<DomainEnumValueSpec> specs);
            if (refusal != null) throw new InvalidOperationException(refusal);
            if (!IsDomain(container)) throw new InvalidOperationException("EnumValues can only be written on a Domain.");

            int applied = (applier ?? DomainPropertyApplier.ApplyEnumValues)(container, specs);
            if (applied != specs.Count)
                throw new InvalidOperationException(applied < 0
                    ? "Could not write EnumValues: the SDK helper is not resolvable."
                    : "Could not write EnumValues: the SDK built " + applied + " of " + specs.Count + " values.");

            // The property bag does not always mark the object changed, and an object that is not changed is not saved.
            MarkChanged(container);
        }

        private static void MarkChanged(object container)
        {
            try
            {
                var dirty = container.GetType().GetProperty("Dirty");
                if (dirty != null && dirty.CanWrite) dirty.SetValue(container, true, null);
            }
            catch (Exception ex)
            {
                Logger.Debug("[ENUMVALUES] The object could not be marked changed: " + ex.Message);
            }
        }

        // The request is read as plain text: a string that looks like a date stays the string it is, and a number keeps its
        // digits (1.50 stays 1.50), so the text compared after the save is the text the caller sent.
        private static JToken ParseJson(string raw)
        {
            using (var reader = new JsonTextReader(new StringReader(raw))
            {
                DateParseHandling = DateParseHandling.None,
                FloatParseHandling = FloatParseHandling.Decimal
            })
            {
                JToken token = JToken.ReadFrom(reader);
                // Reading on makes the reader refuse any text after the value; only comments may follow it.
                while (reader.Read())
                {
                }
                return token;
            }
        }

        // What the SDK refuses in the name of an item, measured on GeneXus 18.0.16: a name is an identifier. It starts with a
        // letter and holds ASCII letters, digits and underscores; a space, a hyphen, a dot, an accented letter, a leading digit and
        // a leading underscore each made the SDK keep the previous list, while "Ativo_1" and a name of 60 letters were kept.
        private static string NameProblem(string name)
        {
            if (!IsAsciiLetter(name[0])) return "is not a valid name: a name starts with a letter.";
            foreach (char c in name)
                if (!IsAsciiLetter(c) && !(c >= '0' && c <= '9') && c != '_')
                    return "is not a valid name: a name holds only letters, digits and underscores.";
            return null;
        }

        private static bool IsAsciiLetter(char c) => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');

        private static string Invalid(string reason) => "InvalidPropertyValue: EnumValues: " + reason;

        // The text of a JSON scalar, null for a missing member, a JSON null or a list or object.
        private static string ScalarText(JToken token)
        {
            if (!(token is JValue scalar) || scalar.Type == JTokenType.Null) return null;
            return Convert.ToString(scalar.Value, CultureInfo.InvariantCulture);
        }

        private static string FirstLine(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            int end = text.IndexOfAny(new[] { '\r', '\n' });
            return end < 0 ? text : text.Substring(0, end);
        }
    }
}
