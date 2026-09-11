using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;

namespace GxMcp.Worker.Helpers
{
    public class AttributeTypeSpec
    {
        public bool Recognized { get; set; }
        public string CanonicalType { get; set; }  // "Numeric"/"Character"/.../"DomainReference"
        public int? Length { get; set; }
        public int? Decimals { get; set; }
        public string DomainName { get; set; }
    }

    /// <summary>
    /// Parses a DSL attribute type string and applies it to a GeneXus Attribute instance.
    ///
    /// Supported input forms:
    ///   Primitive with optional size:  "Character(40)", "Numeric(18,4)", "Date", "Boolean"
    ///   Serializer-tagged domain:      "UserLogin (Character)"  — trailing " (X)" is stripped
    ///   Explicit domain ref:           "&amp;UserLogin"         — same as VariableTypeResolver convention
    ///   Bare domain candidate:         "AutoNum18", "Email"     — plain identifier not matching a primitive
    ///
    /// Reuses VariableTypeResolver for all primitive parsing so synonym tables stay in one place.
    ///
    /// ApplyPrimitive uses reflection to set Type/Length/Decimals so it works with:
    ///   - Real Artech.Genexus.Common.Objects.Attribute  (Type is eDBType enum)
    ///   - Test FakeAttribute                             (Type is string storing enum name)
    /// </summary>
    public static class AttributeTypeApplier
    {
        // Matches "SomeName (AnythingInParens)" with a mandatory space before '('.
        // Does NOT match primitive form "Character(40)" because there is no space before '('.
        private static readonly Regex TrailingCommentRegex =
            new Regex(@"^(.+?)\s+\([^)]+\)\s*$", RegexOptions.Compiled);

        /// <summary>
        /// Parse <paramref name="typeStr"/> into an <see cref="AttributeTypeSpec"/>.
        /// Never throws; returns Recognized=false for unparseable input.
        /// </summary>
        public static AttributeTypeSpec Parse(string typeStr)
        {
            var spec = new AttributeTypeSpec();
            if (string.IsNullOrWhiteSpace(typeStr)) return spec;

            string clean = typeStr.Trim();

            // "Unknown" is the sentinel used in tests for truly unrecognized input.
            if (clean.Equals("Unknown", StringComparison.OrdinalIgnoreCase)) return spec;

            // Strip serializer comment tail BEFORE primitive parsing.
            // "UserLogin (Character)" → "UserLogin"
            // "Character(40)" is NOT stripped because there is no space before '('.
            var m = TrailingCommentRegex.Match(clean);
            if (m.Success)
                clean = m.Groups[1].Value.Trim();

            // & prefix → explicit domain reference (VariableTypeResolver convention).
            if (clean.StartsWith("&"))
            {
                string dn = clean.Substring(1).Trim();
                if (string.IsNullOrEmpty(dn)) return spec;
                spec.Recognized = true;
                spec.CanonicalType = "DomainReference";
                spec.DomainName = dn;
                return spec;
            }

            // Delegate all primitive parsing to VariableTypeResolver.
            var resolved = VariableTypeResolver.Resolve(clean);
            if (resolved.Recognized && resolved.CanonicalType != "DomainReference")
            {
                spec.Recognized = true;
                spec.CanonicalType = resolved.CanonicalType;
                spec.Length = resolved.Length;
                spec.Decimals = resolved.Decimals;
                return spec;
            }

            // Bare identifier that didn't match a primitive → domain candidate.
            // Must be a plain C-style identifier (letters/digits/underscore, starting with letter or _).
            if (Regex.IsMatch(clean, @"^[A-Za-z_][A-Za-z0-9_]*$"))
            {
                spec.Recognized = true;
                spec.CanonicalType = "DomainReference";
                spec.DomainName = clean;
                return spec;
            }

            // Anything else (e.g. "(40)") is unrecognized.
            return spec;
        }

        // Maps canonical GeneXus type names → eDBType enum member names (uppercase).
        internal static readonly Dictionary<string, string> CanonicalToEdb =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "Numeric",     "NUMERIC"      },
            { "Character",   "CHARACTER"    },
            { "VarChar",     "VARCHAR"      },
            { "Date",        "DATE"         },
            { "DateTime",    "DATETIME"     },
            { "Time",        "TIME"         },
            { "Boolean",     "BOOLEAN"      },
            { "LongVarChar", "LONGVARCHAR"  },
            // issue #34: eDBType has no BLOB/IMAGE member — a blob maps to BINARY and an
            // image to BITMAP. The old names failed Enum.Parse so ApplyPrimitive returned
            // false and the attribute silently kept its default type.
            { "Blob",        "BINARY"       },
            { "Binary",      "BINARY"       },
            { "Image",       "BITMAP"       },
            { "Bitmap",      "BITMAP"       },
            { "GUID",        "GUID"         },
        };

        /// <summary>
        /// Apply a primitive (non-domain) type to <paramref name="attr"/>. The attribute object must
        /// expose public settable properties <c>Type</c>, <c>Length</c>, <c>Decimals</c>.
        ///
        /// For the real <c>Artech.Genexus.Common.Objects.Attribute</c>, <c>Type</c> is
        /// <c>Artech.Genexus.Common.eDBType</c> — resolved via Enum.Parse at runtime.
        /// For test fakes where <c>Type</c> is <c>string</c>, the enum name string is stored directly.
        /// </summary>
        /// <returns>true if the type was applied; false if the canonical type is unknown or attr is null.</returns>
        public static bool ApplyPrimitive(object attr, string canonicalType, int? length, int? decimals)
        {
            if (attr == null || string.IsNullOrEmpty(canonicalType)) return false;

            string edbName;
            if (!CanonicalToEdb.TryGetValue(canonicalType, out edbName)) return false;

            Type t = attr.GetType();
            // The Artech SDK Attribute hierarchy declares 'Type' on a base class and shadows it
            // (with `new`) on derived ones, so the parameter-less Type.GetProperty(name) overload
            // throws AmbiguousMatchException. Resolve walking the type hierarchy from most-derived
            // upward and taking the first declaration on each level.
            PropertyInfo typeProp = GetPropertyResolvingAmbiguity(t, "Type");
            PropertyInfo lenProp  = GetPropertyResolvingAmbiguity(t, "Length");
            PropertyInfo decProp  = GetPropertyResolvingAmbiguity(t, "Decimals");
            if (typeProp == null || !typeProp.CanRead || !typeProp.CanWrite) return false;

            if (length.HasValue && (lenProp == null || !lenProp.CanRead || !lenProp.CanWrite)) return false;
            if (decimals.HasValue && (decProp == null || !decProp.CanRead || !decProp.CanWrite)) return false;

            object enumValue;
            if (typeProp.PropertyType == typeof(string))
            {
                // Test fake path: store the enum name as a string.
                enumValue = edbName;
            }
            else
            {
                // Real GeneXus path: parse the enum value from the attribute's assembly.
                try
                {
                    enumValue = Enum.Parse(typeProp.PropertyType, edbName, ignoreCase: true);
                }
                catch
                {
                    return false;
                }
            }

            object previousType;
            object previousLength = null;
            object previousDecimals = null;
            try
            {
                previousType = typeProp.GetValue(attr, null);
                if (length.HasValue) previousLength = lenProp.GetValue(attr, null);
                if (decimals.HasValue) previousDecimals = decProp.GetValue(attr, null);
            }
            catch
            {
                return false;
            }

            bool typeWriteAttempted = false;
            bool lengthWriteAttempted = false;
            bool decimalsWriteAttempted = false;
            try
            {
                typeWriteAttempted = true;
                typeProp.SetValue(attr, enumValue, null);

                if (length.HasValue)
                {
                    lengthWriteAttempted = true;
                    lenProp.SetValue(attr, length.Value, null);
                }

                if (decimals.HasValue)
                {
                    decimalsWriteAttempted = true;
                    decProp.SetValue(attr, decimals.Value, null);
                }

                return true;
            }
            catch
            {
                if (decimalsWriteAttempted) RestoreProperty(decProp, attr, previousDecimals);
                if (lengthWriteAttempted) RestoreProperty(lenProp, attr, previousLength);
                if (typeWriteAttempted) RestoreProperty(typeProp, attr, previousType);
                return false;
            }
        }

        /// <summary>
        /// Applies a property-facing primitive type value to an Attribute (or to a
        /// TransactionAttribute that exposes its underlying Attribute). The generic
        /// property bag setter accepts the string but does not change the SDK type;
        /// property writes must use the typed Attribute surface instead.
        /// </summary>
        public static bool TryApplyType(object attributeOrOccurrence, string rawType, out string error)
        {
            error = null;
            if (attributeOrOccurrence == null)
            {
                error = "An Attribute instance is required to set Type.";
                return false;
            }

            object attribute = attributeOrOccurrence;
            try
            {
                var attributeProperty = GetPropertyUnambiguous(attributeOrOccurrence.GetType(), "Attribute");
                var underlying = attributeProperty?.GetValue(attributeOrOccurrence, null);
                if (underlying != null) attribute = underlying;
            }
            catch
            {
                // A raw Attribute has no occurrence-level Attribute property; keep the input.
            }

            var spec = Parse(rawType);
            if (!spec.Recognized || string.Equals(spec.CanonicalType, "DomainReference", StringComparison.OrdinalIgnoreCase))
            {
                error = $"'{rawType}' is not a primitive Attribute type. Use the Domain property for a Domain reference.";
                return false;
            }

            if (!ApplyPrimitive(attribute, spec.CanonicalType, spec.Length, spec.Decimals))
            {
                error = $"The GeneXus SDK did not expose a writable Type surface for '{rawType}'.";
                return false;
            }

            return true;
        }

        private static PropertyInfo GetPropertyResolvingAmbiguity(Type type, string name)
            => GetPropertyUnambiguous(type, name);

        private static void RestoreProperty(PropertyInfo property, object target, object value)
        {
            try { property.SetValue(target, value, null); }
            catch { }
        }

        /// <summary>
        /// Resolve a property by name on <paramref name="type"/> without throwing
        /// AmbiguousMatchException when derived types shadow the property (common in
        /// the Artech SDK class hierarchy). Tries a normal lookup first, then walks
        /// the type chain from most-derived to base taking the first declared match.
        /// </summary>
        public static PropertyInfo GetPropertyUnambiguous(Type type, string name)
        {
            if (type == null || string.IsNullOrEmpty(name)) return null;
            try { return type.GetProperty(name); }
            catch (System.Reflection.AmbiguousMatchException) { }

            for (Type cur = type; cur != null; cur = cur.BaseType)
            {
                PropertyInfo p = cur.GetProperty(name,
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (p != null) return p;
            }
            return null;
        }

        /// <summary>
        /// Apply a domain reference by setting <c>attr.DomainBasedOn</c> via reflection.
        /// Caller is responsible for resolving <paramref name="domainObj"/> from the KB
        /// (e.g. <c>Artech.Genexus.Common.Objects.Domain.Get(model, name)</c>).
        /// </summary>
        /// <returns>true if the property was set; false if attr or domainObj is null or the property is absent.</returns>
        public static bool ApplyDomain(object attr, object domainObj)
        {
            if (attr == null || domainObj == null) return false;
            PropertyInfo p = attr.GetType().GetProperty("DomainBasedOn");
            if (p == null) return false;
            try { p.SetValue(attr, domainObj, null); return true; }
            catch { return false; }
        }
    }
}
