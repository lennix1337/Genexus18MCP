using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using GxMcp.Worker.Models;
using GxMcp.Worker.Helpers;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// genexus_types — surfaces Domain + SDT type info so the LLM can dry-check
    /// values before writing. Three sub-actions: list, describe, validate_value.
    /// Most of the work is in the pure helpers (range math, value validation) so
    /// they remain unit-testable without a KB. Run(args) is the dispatcher entry.
    /// </summary>
    public class TypeIntrospectService
    {
        private readonly KbService _kbService;
        private readonly ObjectService _objectService;

        public TypeIntrospectService(KbService kbService, ObjectService objectService)
        {
            _kbService = kbService;
            _objectService = objectService;
        }

        public string Run(JObject args)
        {
            try
            {
                string action = (args?["action"]?.ToString() ?? "list").ToLowerInvariant();
                switch (action)
                {
                    case "list":
                        return RunList((args?["kind"]?.ToString() ?? "all").ToLowerInvariant());
                    case "describe":
                        return RunDescribe(args?["name"]?.ToString());
                    case "validate_value":
                        return RunValidateValue(args?["type"]?.ToString(), args?["value"]?.ToString());
                    default:
                        return ErrorJson("InvalidAction", $"Unknown action '{action}'. Expected list|describe|validate_value.");
                }
            }
            catch (Exception ex)
            {
                Logger.Error("TypeIntrospectService.Run: " + ex.Message);
                return ErrorJson("TypeIntrospectFailed", ex.Message);
            }
        }

        private string RunList(string kind)
        {
            var items = new JArray();
            int count = 0;
            try
            {
                var idx = _kbService?.GetIndexCache()?.GetIndex();
                if (idx != null)
                {
                    foreach (var entry in idx.Objects.Values)
                    {
                        string t = entry.Type ?? string.Empty;
                        bool isDomain = t.Equals("Domain", StringComparison.OrdinalIgnoreCase);
                        bool isSdt = t.Equals("SDT", StringComparison.OrdinalIgnoreCase)
                                     || t.Equals("StructuredDataType", StringComparison.OrdinalIgnoreCase);
                        if (!isDomain && !isSdt) continue;
                        if (kind == "domain" && !isDomain) continue;
                        if (kind == "sdt" && !isSdt) continue;

                        var row = new JObject
                        {
                            ["name"] = entry.Name,
                            ["kind"] = isDomain ? "Domain" : "SDT"
                        };
                        // Best-effort baseType/length/decimals from the indexed object.
                        if (isDomain && _objectService != null)
                        {
                            try
                            {
                                var info = ReadDomainInfo(entry.Name);
                                if (info != null)
                                {
                                    if (info.BaseType != null) row["baseType"] = info.BaseType;
                                    if (info.Length.HasValue) row["length"] = info.Length.Value;
                                    if (info.Decimals.HasValue) row["decimals"] = info.Decimals.Value;
                                }
                            }
                            catch { }
                        }
                        items.Add(row);
                        count++;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error("TypeIntrospectService.RunList: " + ex.Message);
            }

            return McpResponse.Ok(code: "TypeIntrospected", result: new JObject
            {
                ["count"] = count,
                ["items"] = items
            });
        }

        private string RunDescribe(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return ErrorJson("InvalidArgument", "name required for action=describe.");
            var info = ReadDomainInfo(name);
            if (info == null) return ErrorJson("TypeNotFound", $"Domain '{name}' was not found.");

            var payload = new JObject
            {
                ["name"] = info.Name,
                ["kind"] = info.Kind
            };
            if (info.BaseType != null) payload["baseType"] = info.BaseType;
            if (info.Length.HasValue) payload["length"] = info.Length.Value;
            if (info.Decimals.HasValue) payload["decimals"] = info.Decimals.Value;
            if (info.Signed.HasValue) payload["signed"] = info.Signed.Value;

            if (string.Equals(info.BaseType, "Numeric", StringComparison.OrdinalIgnoreCase) && info.Length.HasValue)
            {
                ComputeNumericRange(info.Length.Value, info.Decimals ?? 0, info.Signed ?? true,
                    out decimal min, out decimal max);
                payload["rangeMin"] = min;
                payload["rangeMax"] = max;
            }

            if (info.AllowedValues != null && info.AllowedValues.Count > 0)
            {
                var av = new JArray();
                foreach (var v in info.AllowedValues)
                {
                    av.Add(new JObject
                    {
                        ["name"] = v.Name,
                        ["value"] = v.Value,
                        ["description"] = v.Description
                    });
                }
                payload["allowedValues"] = av;
            }

            return McpResponse.Ok(code: "TypeIntrospected", result: payload);
        }

        private string RunValidateValue(string typeName, string value)
        {
            if (string.IsNullOrWhiteSpace(typeName)) return ErrorJson("InvalidArgument", "type required for action=validate_value.");
            var info = ReadDomainInfo(typeName);
            if (info == null) return ErrorJson("TypeNotFound", $"Domain '{typeName}' was not found.");
            var r = ValidateValue(info, value);
            return McpResponse.Ok(code: "TypeIntrospected", result: r);
        }

        // ---- Pure helpers (unit-testable) ----

        /// <summary>
        /// Numeric range: rangeMax = 10^(N-D) - 10^(-D); rangeMin = -rangeMax if signed else 0.
        /// </summary>
        public static void ComputeNumericRange(int length, int decimals, bool signed,
            out decimal rangeMin, out decimal rangeMax)
        {
            if (length < 0) length = 0;
            if (decimals < 0) decimals = 0;
            if (decimals > length) decimals = length;
            int intDigits = length - decimals;
            decimal intMax = 0m;
            for (int i = 0; i < intDigits; i++) intMax = intMax * 10m + 9m;
            decimal fracMax = 0m;
            decimal scale = 1m;
            for (int i = 0; i < decimals; i++)
            {
                scale /= 10m;
                fracMax += 9m * scale;
            }
            rangeMax = intMax + fracMax;
            rangeMin = signed ? -rangeMax : 0m;
        }

        public static JObject ValidateValue(DomainInfo info, string value)
        {
            var r = new JObject();
            if (info == null) { r["valid"] = false; r["reason"] = "type info missing"; return r; }
            string bt = info.BaseType ?? "";

            // Domain allowed-values constraint (any base type).
            if (info.AllowedValues != null && info.AllowedValues.Count > 0)
            {
                bool ok = info.AllowedValues.Any(v =>
                    string.Equals(v.Value, value, StringComparison.Ordinal) ||
                    string.Equals(v.Name, value, StringComparison.Ordinal));
                if (!ok)
                {
                    r["valid"] = false;
                    r["reason"] = $"value '{value}' is not in allowedValues.";
                    r["hint"] = "Expected one of: " +
                                string.Join(", ", info.AllowedValues.Select(v => v.Value ?? v.Name));
                    return r;
                }
            }

            if (bt.Equals("Numeric", StringComparison.OrdinalIgnoreCase))
            {
                if (!decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal d))
                {
                    r["valid"] = false;
                    r["reason"] = $"value '{value}' is not a valid decimal.";
                    return r;
                }
                int len = info.Length ?? 0;
                int dec = info.Decimals ?? 0;
                bool signed = info.Signed ?? true;
                ComputeNumericRange(len, dec, signed, out decimal min, out decimal max);
                if (d < min || d > max)
                {
                    r["valid"] = false;
                    r["reason"] = $"value {d} outside range [{min}, {max}] for Numeric({len},{dec}).";
                    r["hint"] = signed ? "signed Numeric" : "unsigned (>=0).";
                    return r;
                }
                r["valid"] = true;
                return r;
            }

            if (bt.Equals("Character", StringComparison.OrdinalIgnoreCase)
                || bt.Equals("VarChar", StringComparison.OrdinalIgnoreCase)
                || bt.Equals("LongVarChar", StringComparison.OrdinalIgnoreCase))
            {
                int maxLen = info.Length ?? 0;
                if (maxLen > 0 && (value?.Length ?? 0) > maxLen)
                {
                    r["valid"] = false;
                    r["reason"] = $"value length {(value?.Length ?? 0)} exceeds maxLength {maxLen}.";
                    return r;
                }
                r["valid"] = true;
                return r;
            }

            if (bt.Equals("Boolean", StringComparison.OrdinalIgnoreCase))
            {
                if (bool.TryParse(value, out _)) { r["valid"] = true; return r; }
                r["valid"] = false; r["reason"] = $"value '{value}' is not a boolean."; return r;
            }

            if (bt.Equals("Date", StringComparison.OrdinalIgnoreCase)
                || bt.Equals("DateTime", StringComparison.OrdinalIgnoreCase))
            {
                if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                { r["valid"] = true; return r; }
                r["valid"] = false; r["reason"] = $"value '{value}' is not a valid {bt}."; return r;
            }

            // Unknown base type — graceful.
            r["valid"] = true;
            r["note"] = $"constraints not modeled for baseType={bt}";
            return r;
        }

        // ---- KB-touching helpers ----

        private DomainInfo ReadDomainInfo(string name)
        {
            if (_objectService == null) return null;
            try
            {
                dynamic obj = _objectService.FindObject(name, "Domain");
                if (obj == null) return null;
                string td = "";
                try { td = (string)obj.TypeDescriptor.Name; } catch { }
                if (!string.Equals(td, "Domain", StringComparison.OrdinalIgnoreCase))
                    return null;

                var info = new DomainInfo { Name = name, Kind = "Domain" };
                try
                {
                    string typeStr = obj.Type?.ToString();
                    info.BaseType = EdbToCanonical(typeStr) ?? typeStr;
                }
                catch { }
                try { info.Length = (int?)obj.Length; } catch { }
                try { info.Decimals = (int?)obj.Decimals; } catch { }
                try { info.Signed = (bool?)obj.Signed; } catch { }

                var enumValues = ReadDomainEnumValues((object)obj);
                if (enumValues.Count > 0) info.AllowedValues = enumValues;

                return info;
            }
            catch (Exception ex)
            {
                Logger.Error("TypeIntrospectService.ReadDomainInfo: " + ex.Message);
                return null;
            }
        }

        public static List<DomainEnumValueRecord> ReadDomainEnumValues(object domain)
        {
            var result = new List<DomainEnumValueRecord>();
            if (domain == null) return result;

            try
            {
                dynamic valueSource = domain;
                foreach (dynamic value in valueSource.EnumValues)
                {
                    result.Add(new DomainEnumValueRecord
                    {
                        Name = value.Name?.ToString(),
                        Value = value.Value?.ToString(),
                        Description = value.Description?.ToString()
                    });
                }
            }
            catch { }

            if (result.Count > 0) return result;
            return DomainPropertyApplier.ReadEnumValues(domain)
                .OfType<JObject>()
                .Select(value => new DomainEnumValueRecord
                {
                    Name = value["name"]?.ToString(),
                    Value = value["value"]?.ToString(),
                    Description = value["description"]?.ToString()
                })
                .ToList();
        }

        private static string ErrorJson(string code, string message)
        {
            return McpResponse.Err(code: code, message: message);
        }

        private static string EdbToCanonical(string edb)
        {
            if (string.IsNullOrEmpty(edb)) return null;
            foreach (var kv in AttributeTypeApplier.CanonicalToEdb)
            {
                if (string.Equals(kv.Value, edb, StringComparison.OrdinalIgnoreCase)) return kv.Key;
                if (string.Equals(kv.Key, edb, StringComparison.OrdinalIgnoreCase)) return kv.Key;
            }
            return null;
        }

        public class DomainInfo
        {
            public string Name;
            public string Kind;            // "Domain" or "SDT"
            public string BaseType;        // canonical: Numeric / Character / VarChar / Date / DateTime / Boolean ...
            public int? Length;
            public int? Decimals;
            public bool? Signed;
            public List<DomainEnumValueRecord> AllowedValues;
        }

        public class DomainEnumValueRecord
        {
            public string Name;
            public string Value;
            public string Description;
        }
    }
}
