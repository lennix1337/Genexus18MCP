using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    // The decision a genexus_properties read or write takes for the names and values it was given, before any value is
    // read from or written to the SDK. PropertyService takes it on its three paths that can touch a WorkWithPlus
    // template (get, single set and batch set); a dry run takes the same decision as the write it previews. Only the
    // template object type takes aliases or the template checks. Every other object type gets its names back unchanged.
    internal static class WwpTemplateWiring
    {
        // A read: each requested alias gets an item of its own, named as asked, with the SDK property as nativeName.
        internal static void ApplyReadAliases(Guid objectType, JObject fullPropsResult, IEnumerable<string> requested)
            => WwpTemplatePropertyRules.AddAliasReadItems(objectType, fullPropsResult, requested);

        // A write: the SDK name of each requested property, then the two refusals a template can get. Two names that
        // stand for one property are refused (PropertyAliasConflict). A write that changes the type or the XML is refused
        // when the template it would leave could not be listed and opened (WwpTemplateInvalid). readStored supplies the
        // stored value of the property the call does not send.
        internal static WriteWiring Write(Guid objectType, IEnumerable<KeyValuePair<string, string>> requested, Func<string, string> readStored)
        {
            var pairs = requested.ToList();
            var nativeNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in pairs)
                nativeNames[pair.Key] = WwpTemplatePropertyRules.NativeName(objectType, pair.Key);

            string conflict = WwpTemplatePropertyRules.FindConflict(objectType, pairs.Select(p => p.Key));
            if (conflict != null)
                return WriteWiring.Refused("PropertyAliasConflict", conflict,
                    "Send each property once, under its alias or under its SDK name.", nativeNames, null);

            var nativeValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in pairs)
                nativeValues[nativeNames[pair.Key]] = pair.Value ?? string.Empty;
            if (objectType != WwpTemplateXml.ObjectType || !nativeValues.Keys.Any(WwpTemplatePropertyRules.TouchesFinalState))
                return WriteWiring.Allowed(nativeNames, nativeValues);

            (string type, string xml) = WwpTemplatePropertyRules.FinalState(nativeValues, readStored);
            JArray violations = WwpTemplatePropertyRules.Check(type, xml);
            if (violations.Count == 0)
                return WriteWiring.Allowed(nativeNames, nativeValues);
            return WriteWiring.Refused("WwpTemplateInvalid",
                "The WorkWithPlus template was not saved. " + string.Join(" ", violations.Select(v => v["message"]?.ToString())),
                "WWPTemplate_Type (TemplateType) must be one of the template types known for WorkWithPlus 16.2, and WWPTemplate_TemplateXml (TemplateXml) must parse with the root of that type. Nothing was written.",
                nativeNames, violations);
        }
    }

    // What WwpTemplateWiring.Write decided: the SDK name of each requested property and, when the write may not go on,
    // the refusal to return instead of writing.
    internal sealed class WriteWiring
    {
        private WriteWiring() { }

        internal string RefusalCode { get; private set; }
        internal string RefusalMessage { get; private set; }
        internal string RefusalHint { get; private set; }
        internal JArray Violations { get; private set; }
        internal IDictionary<string, string> NativeNames { get; private set; }
        internal IDictionary<string, string> NativeValues { get; private set; }

        internal static WriteWiring Allowed(IDictionary<string, string> nativeNames, IDictionary<string, string> nativeValues)
            => new WriteWiring { NativeNames = nativeNames, NativeValues = nativeValues };

        internal static WriteWiring Refused(string code, string message, string hint, IDictionary<string, string> nativeNames, JArray violations)
            => new WriteWiring { RefusalCode = code, RefusalMessage = message, RefusalHint = hint, Violations = violations, NativeNames = nativeNames };
    }
}
