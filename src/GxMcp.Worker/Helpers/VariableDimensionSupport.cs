using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using Artech.Genexus.Common;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// Metadata read from the GeneXus variable dimension properties.
    /// </summary>
    public sealed class VariableDimensionInfo
    {
        public int Dimensions { get; set; }
        public List<int> DimensionSizes { get; } = new List<int>();
        public bool IsValid { get; set; } = true;
        public string Error { get; set; }

        public bool HasDimensions => Dimensions > 0;

        public JArray SizesToJson()
        {
            var result = new JArray();
            foreach (int size in DimensionSizes) result.Add(size);
            return result;
        }
    }

    /// <summary>
    /// GeneXus exposes fixed-size variable arrays through the ATT property bag
    /// (AttNumDim/AttRows/AttCols), rather than through stable CLR properties.
    /// The property names have been present in the GeneXus 16/17/18 ATT
    /// definitions, but the bag is intentionally accessed reflectively so a
    /// patch-level SDK build cannot make the writer depend on a particular CLR
    /// property shape.
    /// </summary>
    public static class VariableDimensionSupport
    {
        private static readonly string[] DimensionNames =
        {
            "AttNumDim", "Dimensions", "NumDimensions", "DimensionCount"
        };

        private static readonly string[] FirstSizeNames =
        {
            "AttRows", "Rows", "DimensionSize1", "Size1"
        };

        private static readonly string[] SecondSizeNames =
        {
            "AttCols", "Columns", "DimensionSize2", "Size2"
        };

        /// <summary>
        /// Validates the public MCP dimension shape without touching the SDK.
        /// </summary>
        public static bool TryValidate(
            int? dimensions,
            JArray dimensionSizes,
            bool? collection,
            out string code,
            out string message,
            out string hint,
            out JObject extra)
        {
            code = null;
            message = null;
            hint = null;
            extra = null;

            if (dimensions.HasValue && (dimensions.Value < 1 || dimensions.Value > 2))
            {
                code = "InvalidVariableDimensions";
                message = "Variable dimensions must be 1 (vector) or 2 (matrix).";
                hint = "Use dimensions=1 with one positive size, or dimensions=2 with two positive sizes.";
                extra = new JObject { ["dimensions"] = dimensions.Value };
                return false;
            }

            if (!dimensions.HasValue && dimensionSizes != null)
            {
                code = "InvalidVariableDimensions";
                message = "dimensionSizes requires dimensions to be set to 1 or 2.";
                hint = "Send dimensions together with one or two positive dimensionSizes values.";
                extra = new JObject { ["dimensionSizes"] = dimensionSizes?.DeepClone() };
                return false;
            }

            if (dimensions.HasValue)
            {
                if (dimensionSizes == null || dimensionSizes.Count != dimensions.Value)
                {
                    code = "InvalidVariableDimensions";
                    message = "dimensionSizes must contain exactly one size per dimension.";
                    hint = dimensions.Value == 1
                        ? "Send dimensions=1 and dimensionSizes=[size]."
                        : "Send dimensions=2 and dimensionSizes=[rows,columns].";
                    extra = new JObject
                    {
                        ["dimensions"] = dimensions.Value,
                        ["dimensionSizes"] = dimensionSizes?.DeepClone()
                    };
                    return false;
                }

                List<int> sizes;
                string sizeError;
                if (!TryParseSizes(dimensionSizes, out sizes, out sizeError))
                {
                    code = "InvalidVariableDimensions";
                    message = sizeError;
                    hint = "Every dimension size must be a positive integer.";
                    extra = new JObject { ["dimensionSizes"] = dimensionSizes.DeepClone() };
                    return false;
                }

                if (collection == true)
                {
                    code = "CollectionDimensionConflict";
                    message = "A GeneXus variable cannot be both a collection and a fixed-size array.";
                    hint = "Choose collection=true or dimensions/dimensionSizes, not both.";
                    extra = new JObject
                    {
                        ["collection"] = true,
                        ["dimensions"] = dimensions.Value,
                        ["dimensionSizes"] = new JArray(sizes.Cast<object>().ToArray())
                    };
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Parses a dimensionSizes JSON array and rejects fractional, zero,
        /// negative, string, and non-integer values.
        /// </summary>
        public static bool TryParseSizes(JArray values, out List<int> sizes, out string error)
        {
            sizes = new List<int>();
            error = null;
            if (values == null || values.Count == 0)
            {
                error = "dimensionSizes must contain at least one positive integer.";
                return false;
            }

            for (int i = 0; i < values.Count; i++)
            {
                JToken token = values[i];
                int value;
                if (token == null || token.Type != JTokenType.Integer ||
                    !int.TryParse(token.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) ||
                    value <= 0)
                {
                    error = "dimensionSizes[" + i + "] must be a positive integer.";
                    return false;
                }
                sizes.Add(value);
            }

            if (sizes.Count != 1 && sizes.Count != 2)
            {
                error = "dimensionSizes must contain one size for a vector or two sizes for a matrix.";
                return false;
            }

            return true;
        }

        /// <summary>
        /// Applies dimensions to a variable through the SDK property bag and
        /// verifies the values immediately. No caller should add the variable
        /// to a VariablesPart when this returns false.
        /// </summary>
        public static bool TryApply(
            Variable variable,
            int? dimensions,
            JArray dimensionSizes,
            out string error)
        {
            return TryApply((object)variable, dimensions, dimensionSizes, out error);
        }

        /// <summary>
        /// Object overload kept internal-friendly for compatibility tests and
        /// for SDK proxies whose concrete type is not the public Variable type.
        /// </summary>
        internal static bool TryApply(
            object variable,
            int? dimensions,
            JArray dimensionSizes,
            out string error)
        {
            error = null;
            if (variable == null)
            {
                error = "Variable is null; dimensions could not be applied.";
                return false;
            }

            string validationCode;
            string validationMessage;
            string validationHint;
            JObject validationExtra;
            if (!TryValidate(dimensions, dimensionSizes, null, out validationCode,
                out validationMessage, out validationHint, out validationExtra))
            {
                error = validationMessage;
                return false;
            }

            if (!dimensions.HasValue) return true;

            List<int> sizes;
            if (!TryParseSizes(dimensionSizes, out sizes, out error)) return false;

            VariableDimensionInfo before;
            bool hadBefore = TryRead(variable, out before);
            int oldDimensions = before.Dimensions;
            List<int> oldSizes = before.DimensionSizes == null
                ? new List<int>()
                : new List<int>(before.DimensionSizes);

            try
            {
                if (!TrySetDimensionCount(variable, dimensions.Value))
                {
                    RestoreDimensionSnapshot(variable, hadBefore, oldDimensions, oldSizes);
                    error = "The GeneXus SDK did not expose a writable variable Dimensions property.";
                    return false;
                }

                if (!TrySetProperty(variable, FirstSizeNames, sizes[0]))
                {
                    RestoreDimensionSnapshot(variable, hadBefore, oldDimensions, oldSizes);
                    error = "The GeneXus SDK did not expose a writable first dimension-size property.";
                    return false;
                }

                if (dimensions.Value == 2 && !TrySetProperty(variable, SecondSizeNames, sizes[1]))
                {
                    RestoreDimensionSnapshot(variable, hadBefore, oldDimensions, oldSizes);
                    error = "The GeneXus SDK did not expose a writable second dimension-size property.";
                    return false;
                }

                VariableDimensionInfo observed;
                if (!TryRead(variable, out observed) || !observed.IsValid ||
                    observed.Dimensions != dimensions.Value ||
                    !SameSizes(observed.DimensionSizes, sizes))
                {
                    RestoreDimensionSnapshot(variable, hadBefore, oldDimensions, oldSizes);
                    error = "The GeneXus SDK accepted the dimension properties but did not retain the requested values.";
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                error = "The GeneXus SDK rejected the dimension properties: " +
                    (ex.InnerException?.Message ?? ex.Message);

                // Best-effort restoration for callers that reuse an existing
                // variable. New variables are discarded by the caller.
                RestoreDimensionSnapshot(variable, hadBefore, oldDimensions, oldSizes);
                return false;
            }
        }

        private static void RestoreDimensionSnapshot(
            object variable, bool hadBefore, int oldDimensions, IList<int> oldSizes)
        {
            if (!hadBefore || variable == null) return;
            try
            {
                TrySetDimensionCount(variable, oldDimensions);
                if (oldDimensions > 0 && oldSizes != null && oldSizes.Count > 0)
                {
                    TrySetProperty(variable, FirstSizeNames, oldSizes[0]);
                    if (oldDimensions == 2 && oldSizes.Count > 1)
                        TrySetProperty(variable, SecondSizeNames, oldSizes[1]);
                }
            }
            catch { }
        }

        /// <summary>
        /// Clears fixed-size metadata when a text import explicitly changes an
        /// existing array back to a scalar. Older SDKs without a materialized
        /// dimension property are already scalar and remain a no-op.
        /// </summary>
        public static bool TryClear(Variable variable, out string error)
        {
            return TryClear((object)variable, out error);
        }

        internal static bool TryClear(object variable, out string error)
        {
            error = null;
            if (variable == null)
            {
                error = "Variable is null; dimensions could not be cleared.";
                return false;
            }

            VariableDimensionInfo before;
            if (!TryRead(variable, out before) || !before.IsValid)
            {
                error = before.Error ?? "The variable dimension metadata is malformed.";
                return false;
            }
            if (before.Dimensions <= 0) return true;
            if (!TrySetDimensionCount(variable, 0))
            {
                error = "The GeneXus SDK did not expose a writable scalar Dimensions property.";
                return false;
            }

            VariableDimensionInfo after;
            if (!TryRead(variable, out after) || !after.IsValid || after.Dimensions != 0)
            {
                error = "The GeneXus SDK did not clear the variable dimension metadata.";
                return false;
            }
            return true;
        }

        /// <summary>
        /// Reads the dimension metadata from a live SDK Variable.
        /// </summary>
        public static bool TryRead(Variable variable, out VariableDimensionInfo info)
        {
            return TryRead((object)variable, out info);
        }

        internal static bool TryRead(object variable, out VariableDimensionInfo info)
        {
            info = new VariableDimensionInfo();
            if (variable == null)
            {
                info.IsValid = false;
                info.Error = "Variable is null.";
                return false;
            }

            object rawDimensions = ReadProperty(variable, DimensionNames);
            if (rawDimensions == null)
            {
                // A scalar is the normal case when the optional dimension
                // property is not materialized by an older SDK build.
                return true;
            }

            if (!TryReadInt(rawDimensions, out int dimensions))
            {
                info.IsValid = false;
                info.Error = "The SDK returned a non-integer variable Dimensions value.";
                return false;
            }

            info.Dimensions = dimensions;
            if (dimensions == 0) return true;
            if (dimensions < 1 || dimensions > 2)
            {
                info.IsValid = false;
                info.Error = "The SDK returned an unsupported variable dimension count: " + dimensions + ".";
                return false;
            }

            object rawFirst = ReadProperty(variable, FirstSizeNames);
            if (!TryReadPositiveInt(rawFirst, out int first))
            {
                info.IsValid = false;
                info.Error = "The SDK variable is missing a positive first dimension size.";
                return false;
            }
            info.DimensionSizes.Add(first);

            if (dimensions == 2)
            {
                object rawSecond = ReadProperty(variable, SecondSizeNames);
                if (!TryReadPositiveInt(rawSecond, out int second))
                {
                    info.IsValid = false;
                    info.Error = "The SDK variable is missing a positive second dimension size.";
                    return false;
                }
                info.DimensionSizes.Add(second);
            }

            return true;
        }

        /// <summary>
        /// Adds dimensions/dimensionSizes to a read projection. Malformed SDK
        /// metadata is disclosed rather than silently rendered as a scalar.
        /// </summary>
        public static void AddMetadata(JObject target, Variable variable)
        {
            AddMetadata(target, (object)variable);
        }

        /// <summary>
        /// Object overload supports SDK proxy variable implementations used by
        /// K2B WebPanel parts while keeping the public typed entry point above.
        /// </summary>
        public static void AddMetadata(JObject target, object variable)
        {
            if (target == null || variable == null) return;

            VariableDimensionInfo info;
            bool read = TryRead(variable, out info);
            if (!read || !info.IsValid)
            {
                target["dimensionsMalformed"] = true;
                if (!string.IsNullOrWhiteSpace(info.Error)) target["dimensionsError"] = info.Error;
                return;
            }

            if (info.Dimensions <= 0) return;
            target["dimensions"] = info.Dimensions;
            target["dimensionSizes"] = info.SizesToJson();
        }

        /// <summary>
        /// Formats the GeneXus declaration suffix used by the Variables part
        /// text projection, e.g. (999) or (10,20).
        /// </summary>
        public static string FormatDeclarationSuffix(Variable variable)
        {
            return FormatDeclarationSuffix((object)variable);
        }

        public static string FormatDeclarationSuffix(object variable)
        {
            VariableDimensionInfo info;
            if (variable == null || !TryRead(variable, out info) || !info.IsValid || info.Dimensions <= 0)
                return string.Empty;
            if (info.Dimensions == 1 && info.DimensionSizes.Count == 1)
                return "(" + info.DimensionSizes[0].ToString(CultureInfo.InvariantCulture) + ")";
            if (info.Dimensions == 2 && info.DimensionSizes.Count == 2)
                return "(" + info.DimensionSizes[0].ToString(CultureInfo.InvariantCulture) + "," +
                    info.DimensionSizes[1].ToString(CultureInfo.InvariantCulture) + ")";
            return string.Empty;
        }

        /// <summary>
        /// Copies dimension metadata during the existing modify rollback path.
        /// </summary>
        public static bool TryCopy(Variable source, Variable target, out string error)
        {
            error = null;
            if (source == null || target == null)
            {
                error = "Cannot copy variable dimensions because a variable is null.";
                return false;
            }

            VariableDimensionInfo info;
            if (!TryRead(source, out info))
            {
                error = info.Error ?? "The source variable dimension metadata is malformed.";
                return false;
            }
            if (!info.IsValid)
            {
                error = info.Error ?? "The source variable dimension metadata is malformed.";
                return false;
            }
            if (info.Dimensions <= 0)
            {
                VariableDimensionInfo targetInfo;
                if (!TryRead(target, out targetInfo) || !targetInfo.IsValid)
                {
                    error = targetInfo.Error ?? "The target variable dimension metadata is malformed.";
                    return false;
                }
                if (targetInfo.Dimensions > 0 && !TryClear(target, out error)) return false;
                return true;
            }
            return TryApply(target, info.Dimensions, info.SizesToJson(), out error);
        }

        private static bool SameSizes(IList<int> left, IList<int> right)
        {
            if (left == null || right == null || left.Count != right.Count) return false;
            for (int i = 0; i < left.Count; i++)
                if (left[i] != right[i]) return false;
            return true;
        }

        private static bool TrySetDimensionCount(object target, int dimensions)
        {
            // ATT_NUM_DIM is an enum in the GeneXus property catalogue
            // (Scalar/Vector/Matrix), while a few SDK builds expose a numeric
            // CLR/property-bag member instead. Try the native enum spelling
            // first and retain the numeric compatibility fallback.
            string enumValue = dimensions == 1 ? "Vector" : dimensions == 2 ? "Matrix" : "Scalar";
            bool hasDefinitionApi = HasPropertyDefinitionApi(target);
            bool hasPropertyBagApi = HasPropertyBagApi(target);
            bool first = true;
            foreach (string name in DimensionNames)
            {
                bool canonical = IsCanonicalProperty(target, name, first,
                    hasDefinitionApi, hasPropertyBagApi);
                if (TrySetAndVerify(target, name, enumValue, dimensions)
                    || TrySetAndVerify(target, name, dimensions, dimensions))
                    return true;

                // Do not let a writable compatibility alias hide a failed write
                // to the SDK's canonical property.  In particular, a read-only
                // AttCols must not be considered persisted merely because a
                // fallback name accepted the value in a loose property bag.
                if (canonical) return false;
                first = false;
            }
            return false;
        }

        private static bool TrySetProperty(object target, IEnumerable<string> names, int value)
        {
            bool hasDefinitionApi = HasPropertyDefinitionApi(target);
            bool hasPropertyBagApi = HasPropertyBagApi(target);
            bool first = true;
            foreach (string name in names)
            {
                bool canonical = IsCanonicalProperty(target, name, first,
                    hasDefinitionApi, hasPropertyBagApi);
                if (TrySetAndVerify(target, name, value, value))
                    return true;
                if (canonical) return false;
                first = false;
            }
            return false;
        }

        private static bool TrySetAndVerify(object target, string name, object value, int expected)
        {
            if (ReflectionHelper.TrySetPropertyBagValue(target, name, value)
                && PropertyValueMatches(target, name, expected))
                return true;
            if (TrySetClrProperty(target, name, value)
                && PropertyValueMatches(target, name, expected))
                return true;
            return false;
        }

        private static bool PropertyValueMatches(object target, string name, int expected)
        {
            object raw = ReadProperty(target, name);
            return TryReadInt(raw, out int actual) && actual == expected;
        }

        private static bool IsCanonicalProperty(
            object target, string name, bool first,
            bool hasDefinitionApi, bool hasPropertyBagApi)
        {
            if (hasDefinitionApi)
                return HasPropertyDefinition(target, name) || HasClrProperty(target, name);

            // Older property-bag implementations do not expose a definition
            // catalogue.  Their first SDK spelling is the canonical one; CLR
            // only implementations can still use the later aliases.
            if (hasPropertyBagApi) return first;
            return first && HasClrProperty(target, name);
        }

        private static bool HasPropertyDefinitionApi(object target)
        {
            if (target == null) return false;
            return target.GetType().GetMethods(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Any(method => string.Equals(method.Name, "ContainsPropertyDefinition", StringComparison.OrdinalIgnoreCase)
                    && method.GetParameters().Length == 1
                    && method.GetParameters()[0].ParameterType == typeof(string));
        }

        private static bool HasPropertyBagApi(object target)
        {
            if (target == null) return false;
            return target.GetType().GetMethods(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Any(method =>
                    (string.Equals(method.Name, "GetPropertyValue", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(method.Name, "SetPropertyValue", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(method.Name, "SilentSetPropertyValue", StringComparison.OrdinalIgnoreCase))
                    && method.GetParameters().Length >= 1
                    && method.GetParameters()[0].ParameterType == typeof(string));
        }

        private static bool HasPropertyDefinition(object target, string name)
        {
            if (target == null || string.IsNullOrEmpty(name)) return false;
            try
            {
                var method = target.GetType().GetMethods(
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .FirstOrDefault(candidate =>
                        string.Equals(candidate.Name, "ContainsPropertyDefinition", StringComparison.OrdinalIgnoreCase)
                        && candidate.GetParameters().Length == 1
                        && candidate.GetParameters()[0].ParameterType == typeof(string));
                if (method == null) return false;
                object value = method.Invoke(target, new object[] { name });
                return value is bool result ? result : Convert.ToBoolean(value, CultureInfo.InvariantCulture);
            }
            catch { return false; }
        }

        private static bool HasClrProperty(object target, string name)
        {
            if (target == null || string.IsNullOrEmpty(name)) return false;
            try
            {
                Type current = target.GetType();
                while (current != null && current != typeof(object))
                {
                    var property = current.GetProperty(name,
                        BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.IgnoreCase);
                    if (property != null && property.GetIndexParameters().Length == 0)
                        return true;
                    current = current.BaseType;
                }
            }
            catch { }
            return false;
        }

        private static bool TryReadInt(object value, out int result)
        {
            result = 0;
            if (value == null) return false;
            if (value is int i) { result = i; return true; }
            if (value is short s) { result = s; return true; }
            if (value is long l && l >= int.MinValue && l <= int.MaxValue) { result = (int)l; return true; }
            if (value is Enum)
            {
                try
                {
                    result = Convert.ToInt32(value, CultureInfo.InvariantCulture);
                    return true;
                }
                catch { }
            }
            string text = Convert.ToString(value, CultureInfo.InvariantCulture);
            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out result)) return true;

            // GeneXus ATT_NUM_DIM is persisted as an enum label in the SDK
            // property bag (and in several XML/property projections).
            switch ((text ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "scalar":
                case "0":
                    result = 0;
                    return true;
                case "vector":
                case "one":
                case "1":
                    result = 1;
                    return true;
                case "matrix":
                case "two":
                case "2":
                    result = 2;
                    return true;
                default:
                    return false;
            }
        }

        private static bool TryReadPositiveInt(object value, out int result)
        {
            return TryReadInt(value, out result) && result > 0;
        }

        private static object ReadProperty(object target, IEnumerable<string> names)
        {
            foreach (string name in names)
            {
                object value = ReadProperty(target, name);
                if (value != null) return value;
            }
            return null;
        }

        private static object ReadProperty(object target, string name)
        {
            object value = ReflectionHelper.TryGetPropertyBagValue(target, name);
            if (value != null) return value;
            return ReflectionHelper.TryGetMember(target, name);
        }

        private static bool TrySetClrProperty(object target, string name, object value)
        {
            try
            {
                Type current = target.GetType();
                while (current != null && current != typeof(object))
                {
                    PropertyInfo property = current.GetProperty(name,
                        BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.IgnoreCase);
                    if (property != null && property.CanWrite)
                    {
                        object converted;
                        if (property.PropertyType.IsEnum)
                            converted = Enum.Parse(property.PropertyType, Convert.ToString(value, CultureInfo.InvariantCulture), true);
                        else
                            converted = Convert.ChangeType(value, property.PropertyType, CultureInfo.InvariantCulture);
                        property.SetValue(target, converted, null);
                        return true;
                    }
                    current = current.BaseType;
                }
            }
            catch { }
            return false;
        }
    }
}
