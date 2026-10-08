using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Artech.Genexus.Common.Parts;
using Artech.Genexus.Common.Parts.ExternalObject;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Helpers
{
    internal static class ExternalObjectContract
    {
        internal static bool IsStructureAlias(string part) =>
            string.Equals(part, "Source", StringComparison.OrdinalIgnoreCase)
            || string.Equals(part, "Structure", StringComparison.OrdinalIgnoreCase)
            || string.Equals(part, "EXOStructure", StringComparison.OrdinalIgnoreCase);

        internal static ParameterInOut ParseDirection(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return ParameterInOut.In;
            switch (value.Trim().ToLowerInvariant())
            {
                case "in": return ParameterInOut.In;
                case "out": return ParameterInOut.Out;
                case "inout": return ParameterInOut.InOut;
                default: throw new ArgumentException("Parameter direction must be in, out or inout.");
            }
        }

        internal static void ValidatePayload(JObject json)
        {
            RequireName(json?["name"]?.ToString());
            if (json["parameters"] != null && !(json["parameters"] is JArray))
                throw new ArgumentException("parameters must be an array.");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var token in json["parameters"] as JArray ?? new JArray())
            {
                if (!(token is JObject p)) throw new ArgumentException("Each parameter must be an object.");
                string name = p["name"]?.ToString();
                RequireName(name);
                if (!names.Add(name)) throw new ArgumentException("Duplicate parameter name: " + name);
                ParseDirection(p["inout"]?.ToString() ?? p["direction"]?.ToString());
                if (p["inout"] != null && p["direction"] != null &&
                    ParseDirection(p["inout"].ToString()) != ParseDirection(p["direction"].ToString()))
                    throw new ArgumentException("inout and direction disagree.");
                ParseType(p["type"]?.ToString(), p, out _, out _, out _);
            }
            ParseType(json["returnType"]?.ToString(), json, out _, out _, out _);
        }

        private static void RequireName(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || !Regex.IsMatch(name, @"^[A-Za-z][A-Za-z0-9_]*$"))
                throw new ArgumentException("Member and parameter names must start with a letter and contain only letters, digits or underscores.");
        }

        internal static bool ParseType(string value, JObject dimensions,
            out Artech.Genexus.Common.eDBType type, out int? length, out int? decimals)
        {
            type = default;
            length = dimensions["length"]?.Value<int?>();
            decimals = dimensions["decimals"]?.Value<int?>();
            if (length <= 0 || decimals < 0) throw new ArgumentException("length must be positive and decimals nonnegative.");
            if (string.IsNullOrWhiteSpace(value)) return false;
            var match = Regex.Match(value.Trim(), @"^(\w+)\s*\(\s*(\d+)\s*(?:,\s*(\d+)\s*)?\)$");
            string name = match.Success ? match.Groups[1].Value : value.Trim();
            if (match.Success)
            {
                int parsedLength = int.Parse(match.Groups[2].Value);
                int parsedDecimals = match.Groups[3].Success ? int.Parse(match.Groups[3].Value) : 0;
                if (length.HasValue && length != parsedLength || decimals.HasValue && decimals != parsedDecimals)
                    throw new ArgumentException("Type dimensions disagree with length/decimals.");
                length = parsedLength; decimals = parsedDecimals;
            }
            bool basic = VariableInjector.TryParseDbType(name, out type) && Enum.IsDefined(typeof(Artech.Genexus.Common.eDBType), type);
            if (!basic && (match.Success || length.HasValue || decimals.HasValue))
                throw new ArgumentException("Dimensions require a supported GeneXus basic type.");
            if (length <= 0 || decimals < 0 || decimals > length)
                throw new ArgumentException("Invalid type dimensions.");
            return basic;
        }

        private static void ApplyType(ExternalObjectItem item, string value, JObject json)
        {
            if (ParseType(value, json, out var type, out var length, out var decimals)) item.Type = type;
            else if (!string.IsNullOrWhiteSpace(value)) item.ExternalType = value; // legacy external type mappings
            if (length.HasValue) item.Length = length.Value;
            if (decimals.HasValue) item.Decimals = decimals.Value;
            if (json["externalType"] != null) item.ExternalType = json["externalType"].ToString();
        }

        internal static ExternalObjectMethod BuildMethod(EXOStructurePart part, JObject json)
        {
            ValidatePayload(json);
            var method = new ExternalObjectMethod(part) { Name = json["name"].ToString() };
            ApplyType(method, json["returnType"]?.ToString(), json);
            if (json["description"] != null) method.Description = json["description"].ToString();
            if (json["externalName"] != null) method.SetPropertyValue("ExoMethodExternalName", json["externalName"].ToString());
            foreach (JObject p in json["parameters"] as JArray ?? new JArray())
            {
                var parameter = new ExternalObjectParameter(method)
                {
                    Name = p["name"].ToString(),
                    InOut = ParseDirection(p["inout"]?.ToString() ?? p["direction"]?.ToString())
                };
                ApplyType(parameter, p["type"]?.ToString(), p);
                if (p["description"] != null) parameter.Description = p["description"].ToString();
                if (p["externalName"] != null) parameter.SetPropertyValue("ExoParamExternalName", p["externalName"].ToString());
                method.AddParameter(parameter);
            }
            return method;
        }

        internal static JObject DescribeItem(ExternalObjectItem item, string externalNameProperty)
        {
            var result = new JObject
            {
                ["name"] = item.Name, ["description"] = item.Description,
                ["type"] = item.Type.ToString(), ["length"] = item.Length,
                ["decimals"] = item.Decimals, ["signed"] = item.Signed,
                ["isCollection"] = item.IsCollection, ["basedOn"] = item.BasedOn?.ToString(),
                ["externalType"] = item.ExternalType,
                ["externalName"] = item.GetPropertyValue(externalNameProperty)?.ToString(),
                ["propertiesXml"] = item.SerializeToXml()
            };
            return result;
        }

        internal static JObject DescribeMethod(ExternalObjectMethod method)
        {
            var result = DescribeItem(method, "ExoMethodExternalName");
            var parameters = new JArray();
            foreach (var p in method.Parameters)
            {
                var parameter = DescribeItem(p, "ExoParamExternalName");
                parameter["inout"] = ReadDirection(p.GetPropertyValue("ExoParamAccessType")?.ToString(), p.InOut);
                parameter["order"] = parameters.Count + 1;
                parameters.Add(parameter);
            }
            result["parameters"] = parameters;
            return result;
        }

        // Stored Procedure access uses this native property. The generic SDK getter
        // can report In while the stored ExoParamAccessType is Out (GX18 U16).
        internal static string ReadDirection(string nativeAccess, ParameterInOut fallback) =>
            (string.IsNullOrWhiteSpace(nativeAccess) ? fallback : ParseDirection(nativeAccess)).ToString().ToLowerInvariant();

        internal static JObject Read(EXOStructurePart part)
        {
            return new JObject
            {
                ["propertiesXml"] = part.SerializeToXml(),
                ["externalMethods"] = new JArray(part.ExternalMethods.Select(DescribeMethod)),
                ["externalProperties"] = new JArray(part.ExternalProperties.Select(p => DescribeItem(p, "ExoPropertyExternalName"))),
                ["externalEvents"] = new JArray(part.ExternalEvents.Select(e => new JObject {
                    ["propertiesXml"] = e.SerializeToXml(),
                    ["parameters"] = new JArray(e.Parameters.Select(p => DescribeItem(p, "ExoParamExternalName"))) })),
                ["genericTypes"] = new JArray(part.ExternalGenericTypes.Select(t => t.SerializeToXml()))
            };
        }

        internal static string Version(JObject contract)
        {
            using (var hash = SHA256.Create())
                return "exo:" + BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(contract.ToString(Formatting.None)))).Replace("-", "").ToLowerInvariant();
        }

        internal static bool SameSignature(JObject left, JObject right)
        {
            var a = (JObject)left.DeepClone(); var b = (JObject)right.DeepClone();
            foreach (var item in new[] { a, b })
            {
                item.Remove("propertiesXml");
                foreach (JObject p in item["parameters"] as JArray ?? new JArray()) p.Remove("propertiesXml");
            }
            return JToken.DeepEquals(a, b);
        }
    }
}
