using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    // The find/replace form of the template XML of a WorkWithPlus template, sent in the properties of genexus_properties set:
    //   properties: { "TemplateXml": { "find": "...", "replace": "...", "replaceAll": false, "expectedCount": 1 } }
    // The find text is matched exactly, byte for byte, against the XML the call reads from the object. The XML the patch
    // produces goes through the template checks of WwpTemplateWiring as a whole XML would, and nothing is written when they
    // refuse. Only the template XML of the template object type takes this form: a JSON value on any other name or type
    // keeps the meaning it always had.
    internal static class WwpTemplateXmlPatch
    {
        private const int SnippetRadius = 40;
        private const int SnippetMaxLength = 200;
        private static readonly string[] PatchKeys = { "find", "replace", "replaceAll", "expectedCount" };

        // The batch once its patches are applied, or the refusal that stops the write.
        internal sealed class Outcome
        {
            internal string RefusalCode;
            internal string RefusalMessage;
            internal string RefusalHint;
            internal JObject RefusalExtra;

            // The batch as the write sees it: each patch replaced by the XML it produces, the other values unchanged.
            internal JObject Properties = new JObject();

            // Per requested name that was a patch: the occurrences it matched and what it changed.
            internal JObject Patches = new JObject();

            // True when every requested property is a patch and none of them changes the XML: nothing needs writing.
            internal bool Unchanged;
        }

        // True when the name is the template XML, under its alias or its SDK name, and the value is a patch rather than text.
        internal static bool IsPatch(Guid objectType, string requested, JToken value)
            => objectType == WwpTemplateXml.ObjectType && value is JObject
                && WwpTemplatePropertyRules.NativeName(objectType, requested) == WwpTemplatePropertyRules.XmlProperty;

        internal static bool HasPatch(Guid objectType, JObject properties)
            => properties.Properties().Any(p => IsPatch(objectType, p.Name, p.Value));

        // A response names a patch in its own block; the XML it wrote is not echoed back, so the batch it reports drops the patched names.
        internal static JObject Omit(JObject values, JObject patches)
        {
            var copy = (JObject)values.DeepClone();
            foreach (string name in patches.Properties().Select(p => p.Name).ToList())
                copy.Remove(name);
            return copy;
        }

        // The receipt of a write whose every property was read back. A patch is left out: its XML is named by its summary and not
        // echoed. persistedVerified holds because the read-back ran for every property before the receipt is built.
        internal static JObject VerifiedReceipt(IEnumerable<(string Name, string Before, string Requested, string Persisted)> checks, JObject patches)
        {
            var before = new JObject();
            var requested = new JObject();
            var persisted = new JObject();
            foreach (var check in checks)
            {
                if (patches[check.Name] != null) continue;
                before[check.Name] = check.Before;
                requested[check.Name] = check.Requested;
                persisted[check.Name] = check.Persisted;
            }
            return new JObject
            {
                ["before"] = before,
                ["requested"] = requested,
                ["persisted"] = persisted,
                ["persistedVerified"] = true
            };
        }

        // The decision for a batch. readCurrent supplies the template XML stored now, and is called only when the batch holds a patch.
        internal static Outcome Apply(Guid objectType, JObject properties, Func<string> readCurrent)
        {
            var outcome = new Outcome { Properties = (JObject)properties.DeepClone() };
            if (!HasPatch(objectType, properties)) return outcome;

            string conflict = WwpTemplatePropertyRules.FindConflict(objectType, properties.Properties().Select(p => p.Name));
            if (conflict != null)
                return Refused(outcome, "PropertyAliasConflict", conflict, "Send each property once, under its alias or under its SDK name.");

            string current = readCurrent();
            if (current == null)
                return Refused(outcome, "FreshReadUnavailable", "The stored TemplateXml could not be read; no patch was applied.",
                    "Repeating the same patch gives the same result. Read the template with genexus_properties action=get propertyName=TemplateXml to see whether its XML can be read, or send the whole XML with genexus_properties set.");

            var effective = new JObject();
            int patches = 0;
            int unchanged = 0;
            foreach (JProperty property in properties.Properties())
            {
                if (!IsPatch(objectType, property.Name, property.Value))
                {
                    effective[property.Name] = property.Value.DeepClone();
                    continue;
                }

                if (!TryParse((JObject)property.Value, out PatchSpec spec, out string problem))
                    return Refused(outcome, "InvalidPropertyBatch", "The TemplateXml patch is not valid: " + problem,
                        "Send properties.TemplateXml as { find, replace, replaceAll, expectedCount }. Nothing was written.");

                List<int> hits = Occurrences(current, spec.Find);
                if (hits.Count == 0)
                    return Refused(outcome, "NoMatch", "The find text was not found in TemplateXml. Nothing was written.",
                        "Read the current TemplateXml with genexus_properties action=get propertyName=TemplateXml and copy the find text exactly, tabs, quotes and spaces included.",
                        new JObject
                        {
                            ["suggested_next_step"] = new JObject
                            {
                                ["action"] = "read_template_xml",
                                ["hint"] = "genexus_properties action=get propertyName=TemplateXml"
                            }
                        });

                int expected = spec.ReplaceAll ? spec.ExpectedCount ?? hits.Count : spec.ExpectedCount ?? 1;
                if (hits.Count != expected)
                    return Refused(outcome, "Ambiguous",
                        $"The find text matched {hits.Count} location(s) but expectedCount is {expected}. Nothing was written.",
                        spec.ReplaceAll
                            ? "Correct expectedCount to the number of matches, or drop it to replace every match."
                            : "Add surrounding text to make the find unique, or pass replaceAll=true to apply to every match.");

                string patched = ReplaceAt(current, hits, spec);
                effective[property.Name] = patched;
                outcome.Patches[property.Name] = new JObject
                {
                    ["occurrences"] = hits.Count,
                    ["replaced"] = hits.Count,
                    ["beforeLength"] = current.Length,
                    ["afterLength"] = patched.Length,
                    ["context"] = Snippet(patched, hits[0], spec.Replace.Length)
                };
                patches++;
                if (patched == current) unchanged++;
            }

            outcome.Properties = effective;
            outcome.Unchanged = patches == properties.Count && unchanged == patches;
            return outcome;
        }

        private static Outcome Refused(Outcome outcome, string code, string message, string hint, JObject extra = null)
        {
            outcome.RefusalCode = code;
            outcome.RefusalMessage = message;
            outcome.RefusalHint = hint;
            outcome.RefusalExtra = extra;
            return outcome;
        }

        private sealed class PatchSpec
        {
            internal string Find;
            internal string Replace;
            internal bool ReplaceAll;
            internal int? ExpectedCount;
        }

        // The keys are exact, so a misspelt key is refused instead of ignored. find must not be empty: an empty find matches everywhere.
        private static bool TryParse(JObject value, out PatchSpec spec, out string problem)
        {
            spec = null;
            problem = null;
            string unknown = value.Properties().Select(p => p.Name).FirstOrDefault(name => !PatchKeys.Contains(name));
            if (unknown != null)
            {
                problem = $"'{unknown}' is not a patch key.";
                return false;
            }

            string find = StringValue(value["find"]);
            if (find == null)
            {
                problem = "find must be the exact text to replace, as a string.";
                return false;
            }
            if (find.Length == 0)
            {
                problem = "find must not be empty.";
                return false;
            }

            string replace = StringValue(value["replace"]);
            if (replace == null)
            {
                problem = "replace must be a string; use \"\" to delete the find text.";
                return false;
            }

            JToken replaceAll = Sent(value["replaceAll"]);
            if (replaceAll != null && replaceAll.Type != JTokenType.Boolean)
            {
                problem = "replaceAll must be true or false.";
                return false;
            }

            JToken count = Sent(value["expectedCount"]);
            int? expectedCount = null;
            if (count != null)
            {
                if (count.Type != JTokenType.Integer || !int.TryParse(count.ToString(), out int parsed) || parsed < 1)
                {
                    problem = "expectedCount must be a whole number of 1 or more.";
                    return false;
                }
                expectedCount = parsed;
            }

            spec = new PatchSpec
            {
                Find = find,
                Replace = replace,
                ReplaceAll = replaceAll?.Value<bool>() ?? false,
                ExpectedCount = expectedCount
            };
            return true;
        }

        private static string StringValue(JToken token) => token?.Type == JTokenType.String ? token.ToString() : null;

        // A key sent as JSON null is not sent: an optional key takes its default, as it does when the key is absent.
        private static JToken Sent(JToken token) => token == null || token.Type == JTokenType.Null ? null : token;

        // Ordinal and non-overlapping: the XML is matched byte for byte, with no culture rules. An empty find would match at every
        // position, so it has no occurrences here; the caller refuses it first, and this guard keeps a regression from looping.
        internal static List<int> Occurrences(string text, string find)
        {
            var hits = new List<int>();
            if (string.IsNullOrEmpty(find)) return hits;
            for (int at = text.IndexOf(find, StringComparison.Ordinal); at >= 0; at = text.IndexOf(find, at + find.Length, StringComparison.Ordinal))
                hits.Add(at);
            return hits;
        }

        private static string ReplaceAt(string current, IReadOnlyList<int> hits, PatchSpec spec)
        {
            var text = new StringBuilder();
            int from = 0;
            foreach (int at in hits)
            {
                text.Append(current, from, at - from).Append(spec.Replace);
                from = at + spec.Find.Length;
            }
            return text.Append(current, from, current.Length - from).ToString();
        }

        // The text around the first replacement, in the patched XML: the part before it is unchanged, so the match position holds there.
        private static string Snippet(string patched, int firstAt, int replaceLength)
        {
            int from = Math.Max(0, firstAt - SnippetRadius);
            int to = Math.Min(patched.Length, Math.Min(firstAt + replaceLength + SnippetRadius, from + SnippetMaxLength));
            return patched.Substring(from, to - from);
        }
    }
}
