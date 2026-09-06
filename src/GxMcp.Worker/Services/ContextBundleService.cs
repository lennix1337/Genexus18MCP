using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// Keeps the object context contract useful when a 360-degree read is too
    /// large for one model turn. The service operates on the already resolved
    /// SDK result, so it never reads a second KB or invents a persistence path.
    /// Large parts become stable, addressable read hints; collections are
    /// paged at item boundaries and every page carries the hash of the full
    /// context used to produce it.
    /// </summary>
    public sealed class ContextBundleService
    {
        public const string SchemaVersion = "genexus-context-bundle/1";
        public const int DefaultBudgetBytes = 60000;
        private const int MinimumBudgetBytes = 4096;
        private const int MaximumBudgetBytes = 1000000;
        private const int LargePartBytes = 4096;
        private const int DefaultPageSize = 20;

        private static readonly string[] CollectionNames =
        {
            "calledSignatures",
            "referencedTables",
            "referencedSDTs",
            "callers"
        };

        public string Apply(string envelopeJson, string target, int? maxBytes = null, string cursor = null)
        {
            if (string.IsNullOrWhiteSpace(envelopeJson)) return envelopeJson;

            JObject envelope;
            try
            {
                envelope = JObject.Parse(envelopeJson);
            }
            catch
            {
                // The context service is a projection layer. Do not turn a
                // valid legacy error/debug payload into a second error.
                return envelopeJson;
            }

            var result = envelope["result"] as JObject;
            if (result == null) return envelope.ToString(Formatting.None);

            int budget = NormalizeBudget(maxBytes);
            var fullResult = (JObject)result.DeepClone();
            string fullHash = Hash(fullResult.ToString(Formatting.None));
            var projected = (JObject)fullResult.DeepClone();
            var context = new JObject
            {
                ["schemaVersion"] = SchemaVersion,
                ["contentHash"] = fullHash,
                ["revision"] = fullHash,
                ["budgetBytes"] = budget
            };

            var resources = new JArray();
            var omitted = new JArray();
            ExternalizeLargeParts(projected, target, resources, omitted);

            int cursorOffset;
            string cursorSection;
            ParseCursor(cursor, out cursorSection, out cursorOffset);
            if (!string.IsNullOrEmpty(cursorSection))
            {
                PageCollection(projected, cursorSection, cursorOffset, DefaultPageSize, context, omitted);
            }

            projected["context"] = context;
            if (resources.Count > 0) context["resources"] = resources;
            if (omitted.Count > 0) context["omittedSections"] = omitted;

            // Remove the least useful collection rows first until the result
            // fits. Rows are never sliced into invalid JSON or partial strings.
            foreach (string name in CollectionNames.Reverse())
            {
                if (Fits(projected, budget)) break;
                var array = projected[name] as JArray;
                if (array == null || array.Count == 0) continue;

                int originalCount = array.Count;
                int keep = Math.Max(1, originalCount / 2);
                while (array.Count > keep) array.RemoveAt(array.Count - 1);
                int nextOffset = Math.Max(keep, cursorOffset);
                SetNextCursor(context, name, nextOffset, originalCount);
                AddOmitted(omitted, name, originalCount - array.Count);
            }

            // Parts are the dominant payload in real Procedures. If the
            // metadata still exceeds the budget, externalize every remaining
            // inline part before dropping optional object fields.
            if (!Fits(projected, budget))
            {
                ExternalizeAllParts(projected, target, resources, omitted);
            }

            if (!Fits(projected, budget))
            {
                var objectNode = projected["object"] as JObject;
                if (objectNode != null)
                {
                    foreach (string optional in new[] { "variables", "structure", "controls", "events", "parts" })
                    {
                        if (objectNode[optional] != null)
                        {
                            objectNode.Remove(optional);
                            AddOmitted(omitted, "object." + optional, 1);
                            if (Fits(projected, budget)) break;
                        }
                    }
                }
            }

            if (!Fits(projected, budget))
            {
                // Last resort: preserve the identity/signature needed to make
                // the next read safe and expose the omitted sections clearly.
                var originalObject = fullResult["object"] as JObject;
                var minimalObject = new JObject();
                foreach (string key in new[] { "name", "type", "signature" })
                {
                    if (originalObject?[key] != null) minimalObject[key] = originalObject[key].DeepClone();
                }
                projected["object"] = minimalObject;
                AddOmitted(omitted, "object", 1);
            }

            if (resources.Count > 0) context["resources"] = resources;
            if (omitted.Count > 0) context["omittedSections"] = omitted;
            context["cursor"] = string.IsNullOrWhiteSpace(cursor) ? JValue.CreateNull() : cursor;
            context["returnedBytes"] = Measure(projected);
            context["complete"] = omitted.Count == 0 && context["nextCursor"] == null;

            envelope["result"] = projected;
            return envelope.ToString(Formatting.None);
        }

        private static void ExternalizeLargeParts(JObject result, string target, JArray resources, JArray omitted)
        {
            var objectNode = result["object"] as JObject;
            var parts = objectNode?["parts"] as JObject;
            if (parts == null) return;

            foreach (var property in parts.Properties().ToList())
            {
                string content = property.Value.Type == JTokenType.String ? property.Value.ToString() : null;
                if (content == null || Encoding.UTF8.GetByteCount(content) <= LargePartBytes) continue;

                resources.Add(CreateResource(target, property.Name, content));
                parts.Remove(property.Name);
                AddOmitted(omitted, "object.parts." + property.Name, 1);
            }
        }

        private static void ExternalizeAllParts(JObject result, string target, JArray resources, JArray omitted)
        {
            var objectNode = result["object"] as JObject;
            var parts = objectNode?["parts"] as JObject;
            if (parts == null) return;

            foreach (var property in parts.Properties().ToList())
            {
                string content = property.Value.Type == JTokenType.String ? property.Value.ToString() : null;
                if (content == null) continue;
                resources.Add(CreateResource(target, property.Name, content));
                parts.Remove(property.Name);
                AddOmitted(omitted, "object.parts." + property.Name, 1);
            }
        }

        private static JObject CreateResource(string target, string part, string content)
        {
            string safeTarget = Uri.EscapeDataString(target ?? string.Empty);
            string safePart = Uri.EscapeDataString(part ?? "Source");
            return new JObject
            {
                ["section"] = "object.parts." + part,
                ["uri"] = "genexus://objects/" + safeTarget + "/part/" + safePart,
                ["sha256"] = Hash(content),
                ["sizeBytes"] = Encoding.UTF8.GetByteCount(content),
                ["read"] = new JObject
                {
                    ["tool"] = "genexus_read",
                    ["arguments"] = new JObject
                    {
                        ["name"] = target ?? string.Empty,
                        ["part"] = part,
                        ["offset"] = 0,
                        ["limit"] = 200
                    }
                }
            };
        }

        private static void PageCollection(JObject result, string section, int offset, int pageSize, JObject context, JArray omitted)
        {
            var array = result[section] as JArray;
            if (array == null || offset < 0 || offset >= array.Count) return;

            int originalCount = array.Count;
            var page = new JArray(array.Skip(offset).Take(pageSize).Select(item => item.DeepClone()));
            result[section] = page;
            AddOmitted(omitted, section, offset);
            if (offset + page.Count < originalCount)
            {
                SetNextCursor(context, section, offset + page.Count, originalCount);
            }
        }

        private static void SetNextCursor(JObject context, string section, int offset, int total)
        {
            if (offset < total)
            {
                context["nextCursor"] = section + ":" + offset;
                context["nextTotal"] = total;
            }
        }

        private static void AddOmitted(JArray omitted, string section, int count)
        {
            if (count <= 0 || omitted.Any(item => string.Equals(item["section"]?.ToString(), section, StringComparison.OrdinalIgnoreCase))) return;
            omitted.Add(new JObject
            {
                ["section"] = section,
                ["count"] = count,
                ["reason"] = "budget_or_page"
            });
        }

        private static bool Fits(JObject result, int budget)
        {
            return Measure(result) <= budget;
        }

        private static int Measure(JObject value)
        {
            return Encoding.UTF8.GetByteCount(value.ToString(Formatting.None));
        }

        private static int NormalizeBudget(int? requested)
        {
            int value = requested.GetValueOrDefault(DefaultBudgetBytes);
            if (value < MinimumBudgetBytes) return MinimumBudgetBytes;
            if (value > MaximumBudgetBytes) return MaximumBudgetBytes;
            return value;
        }

        private static void ParseCursor(string cursor, out string section, out int offset)
        {
            section = null;
            offset = 0;
            if (string.IsNullOrWhiteSpace(cursor)) return;

            string[] parts = cursor.Split(new[] { ':' }, 2);
            if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || !int.TryParse(parts[1], out offset) || offset < 0) return;
            if (CollectionNames.Any(name => string.Equals(name, parts[0], StringComparison.OrdinalIgnoreCase)))
            {
                section = CollectionNames.First(name => string.Equals(name, parts[0], StringComparison.OrdinalIgnoreCase));
            }
        }

        public static string Hash(string value)
        {
            using (var sha = SHA256.Create())
            {
                byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty));
                return "sha256:" + BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant();
            }
        }
    }
}
