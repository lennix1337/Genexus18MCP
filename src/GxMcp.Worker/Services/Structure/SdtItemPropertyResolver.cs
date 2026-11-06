using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace GxMcp.Worker.Services.Structure
{
    internal sealed class SdtPropertyMatch
    {
        public SdtPropertyInfo Info { get; set; }

        /// <summary>The properties a spelling matched when it matched more than one.</summary>
        public List<SdtPropertyInfo> Ambiguous { get; set; }

        /// <summary>Close spellings offered when nothing matched.</summary>
        public List<string> Suggestions { get; set; } = new List<string>();
    }

    /// <summary>
    /// Name and value resolution for SDT member properties. A caller can spell a property by its bag key, the SDK typed
    /// constant or the IDE name, and an enumerated value by its stored id, the typed enumeration member or the IDE name.
    /// </summary>
    internal static class SdtItemPropertyResolver
    {
        // Properties that carry the identity or the type of the member. They have dedicated, validated paths
        // (update_visual); a typed property write must not become a second, unchecked way to retype or rename.
        private static readonly HashSet<string> StructuralNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Name", "ATTCUSTOMTYPE", "Domain", "DomainKey", "idBasedOn", "VarBasedOn", "AttCollection",
            "idCollectionItemName", "Length", "Decimals", "Signed", "AttMaxLen", "AttNumDim", "GeneXusObject", "OBJ_TYPE", "Model",
            "Id", "Guid"
        };

        internal static bool IsStructural(SdtPropertyInfo info)
        {
            return info != null && !string.IsNullOrEmpty(info.NativeName) && StructuralNames.Contains(info.NativeName);
        }

        /// <summary>Lower-cases and keeps only letters and digits, so "Json Null Serialization" equals "JsonNullSerialization".</summary>
        internal static string Normalize(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            var builder = new StringBuilder(text.Length);
            foreach (char c in text)
                if (char.IsLetterOrDigit(c)) builder.Append(char.ToLowerInvariant(c));
            return builder.ToString();
        }

        // idJsonInclude -> JsonInclude: the SDK prefixes many bag keys with "id" followed by an upper-case letter.
        private static string StripIdPrefix(string nativeName)
        {
            if (!string.IsNullOrEmpty(nativeName) && nativeName.Length > 2
                && nativeName[0] == 'i' && nativeName[1] == 'd' && char.IsUpper(nativeName[2]))
                return nativeName.Substring(2);
            return nativeName;
        }

        private static bool Same(string left, string right)
        {
            return !string.IsNullOrEmpty(left) && !string.IsNullOrEmpty(right)
                && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }

        internal static SdtPropertyMatch Resolve(IList<SdtPropertyInfo> catalog, string requested)
        {
            var match = new SdtPropertyMatch();
            string text = requested?.Trim();
            if (string.IsNullOrEmpty(text) || catalog == null) return match;
            string normalized = Normalize(text);

            var tiers = new Func<SdtPropertyInfo, bool>[]
            {
                p => Same(p.NativeName, text),
                p => Same(p.ConstantName, text),
                p => Same(p.DisplayName, text),
                p => normalized.Length > 0 && (normalized == Normalize(p.NativeName)
                    || normalized == Normalize(StripIdPrefix(p.NativeName))
                    || normalized == Normalize(p.ConstantName)
                    || normalized == Normalize(p.DisplayName))
            };
            foreach (var tier in tiers)
            {
                var hits = catalog.Where(tier).GroupBy(p => p.NativeName, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First()).ToList();
                if (hits.Count == 1)
                {
                    match.Info = hits[0];
                    return match;
                }
                if (hits.Count > 1)
                {
                    match.Ambiguous = hits;
                    return match;
                }
            }

            var candidates = catalog.Where(p => p.Browsable).Select(p => p.CanonicalName)
                .Where(n => !string.IsNullOrEmpty(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            match.Suggestions = PropertyService.FindPropertySuggestions(text, candidates, 5) ?? new List<string>();
            return match;
        }

        /// <summary>
        /// Converts the caller's text into the typed value the SDK stores. For an enumeration the text may be the stored id,
        /// the typed enumeration member or the IDE name, compared ignoring case and separators.
        /// </summary>
        internal static bool TryResolveValue(SdtPropertyInfo info, string requested, out object typed, out string raw, out string error)
        {
            typed = null;
            raw = null;
            error = null;
            if (info == null) { error = "The property is unknown."; return false; }
            if (requested == null) { error = "A value is required."; return false; }

            if (info.IsEnumerated)
            {
                string text = requested.Trim();
                var hits = info.Options.Where(o => Same(o.Raw, text)).ToList();
                if (hits.Count == 0) hits = info.Options.Where(o => Same(o.Member, text) || Same(o.Label, text)).ToList();
                if (hits.Count == 0)
                {
                    string normalized = Normalize(text);
                    if (normalized.Length > 0)
                        hits = info.Options.Where(o => normalized == Normalize(o.Raw) || normalized == Normalize(StripIdPrefix(o.Raw))
                            || normalized == Normalize(o.Member) || normalized == Normalize(o.Label)).ToList();
                }
                var distinct = hits.GroupBy(o => o.Raw, StringComparer.Ordinal).Select(g => g.First()).ToList();
                if (distinct.Count == 1)
                {
                    raw = distinct[0].Raw;
                    // The object the SDK listed keeps the stored type (an integer enumeration stays an integer);
                    // the text of the value is only the fallback when the SDK listed none.
                    typed = distinct[0].Value ?? ConvertEnumerated(info, raw);
                    return typed != null;
                }
                error = distinct.Count > 1
                    ? "'" + requested + "' matches more than one allowed value: " + AllowedText(info) + "."
                    : "'" + requested + "' is not an allowed value. Allowed: " + AllowedText(info) + ".";
                return false;
            }

            Type type = info.ClrType == null ? typeof(string) : (Nullable.GetUnderlyingType(info.ClrType) ?? info.ClrType);
            if (type == typeof(string))
            {
                typed = requested;
                raw = requested;
                return true;
            }
            string value = requested.Trim();
            if (type == typeof(bool))
            {
                bool flag;
                if (value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1" || value.Equals("yes", StringComparison.OrdinalIgnoreCase)) flag = true;
                else if (value.Equals("false", StringComparison.OrdinalIgnoreCase) || value == "0" || value.Equals("no", StringComparison.OrdinalIgnoreCase)) flag = false;
                else { error = "'" + requested + "' is not a boolean. Use true or false."; return false; }
                typed = flag;
                raw = RenderRaw(flag);
                return true;
            }
            if (type == typeof(int) || type == typeof(long) || type == typeof(short))
            {
                long number;
                if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
                {
                    error = "'" + requested + "' is not an integer.";
                    return false;
                }
                try { typed = Convert.ChangeType(number, type, CultureInfo.InvariantCulture); }
                catch (OverflowException) { error = "'" + requested + "' is out of range for " + type.Name + "."; return false; }
                raw = RenderRaw(typed);
                return true;
            }
            if (type == typeof(decimal) || type == typeof(double) || type == typeof(float))
            {
                decimal number;
                if (!decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number))
                {
                    error = "'" + requested + "' is not a number.";
                    return false;
                }
                try { typed = Convert.ChangeType(number, type, CultureInfo.InvariantCulture); }
                catch (OverflowException) { error = "'" + requested + "' is out of range for " + type.Name + "."; return false; }
                raw = RenderRaw(typed);
                return true;
            }

            error = "Properties of type " + type.Name + " are not writable through this action; only text, boolean, numeric and enumerated properties are.";
            return false;
        }

        // The stored value of an enumerated property is a string unless the SDK declares a CLR enumeration.
        private static object ConvertEnumerated(SdtPropertyInfo info, string raw)
        {
            Type type = info.ClrType == null ? typeof(string) : (Nullable.GetUnderlyingType(info.ClrType) ?? info.ClrType);
            if (type == typeof(string)) return raw;
            if (type.IsEnum)
            {
                try { return Enum.Parse(type, raw, true); }
                catch (ArgumentException) { return null; }
            }
            return raw;
        }

        internal static string AllowedText(SdtPropertyInfo info)
        {
            return string.Join(", ", info.Options.Select(o =>
                string.Equals(o.Preferred, o.Raw, StringComparison.Ordinal) ? o.Raw : o.Preferred + " (" + o.Raw + ")"));
        }

        /// <summary>The canonical string of a stored value: invariant culture, and True/False for booleans.</summary>
        internal static string RenderRaw(object value)
        {
            if (value == null) return null;
            if (value is bool flag) return flag ? "True" : "False";
            if (value is Enum) return value.ToString();
            if (value is IFormattable formattable) return formattable.ToString(null, CultureInfo.InvariantCulture);
            return value.ToString();
        }
    }
}
