using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Helpers
{
    internal static class SdtStructurePlan
    {
        internal static JArray Project(JArray before, JArray requested, bool add)
        {
            if (requested == null) throw new ArgumentException("children must be an array.");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var oldNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = add ? (JArray)(before ?? new JArray()).DeepClone() : new JArray();
            foreach (JObject item in before ?? new JArray())
                oldNames.Add(item["name"]?.ToString() ?? string.Empty);
            if (add)
                foreach (JObject item in result)
                    names.Add(item["name"]?.ToString() ?? string.Empty);
            foreach (JToken token in requested)
            {
                if (!(token is JObject child) || string.IsNullOrWhiteSpace(child["name"]?.ToString()))
                    throw new ArgumentException("Every child must be an object with a nonempty name.");
                string name = child["name"].ToString();
                if (!names.Add(name)) throw new ArgumentException("Duplicate SDT member: " + name);
                bool level = child["isLevel"]?.ToObject<bool>() == true || child["children"] is JArray;
                if (!oldNames.Contains(name) && !level && child["type"] == null && child["basedOnDomain"] == null
                    && child["basedOnAttribute"] == null && child["basedOn"] == null)
                    throw new ArgumentException("A new SDT member needs a type or basedOn binding: " + name);
                if (child["children"] is JArray nested) Project(new JArray(), nested, add);
                result.Add(child.DeepClone());
            }
            return result;
        }

        internal static JArray Diff(JArray before, JArray after)
        {
            var result = new JArray();
            DiffInto(before, after, "children", result);
            return result;
        }

        private static void DiffInto(JArray before, JArray after, string path, JArray result)
        {
            var oldItems = Index(before);
            var newItems = Index(after);
            foreach (var item in oldItems)
                if (!newItems.ContainsKey(item.Key))
                    result.Add(new JObject { ["path"] = path + "/" + item.Key, ["change"] = "removed", ["before"] = item.Value.DeepClone() });
            foreach (var item in newItems)
            {
                if (!oldItems.TryGetValue(item.Key, out JToken old))
                    result.Add(new JObject { ["path"] = path + "/" + item.Key, ["change"] = "added", ["after"] = item.Value.DeepClone() });
                else if (!JToken.DeepEquals(old, item.Value))
                    result.Add(new JObject { ["path"] = path + "/" + item.Key, ["change"] = "changed", ["before"] = old.DeepClone(), ["after"] = item.Value.DeepClone() });
                if (old?["children"] is JArray oldChildren && item.Value["children"] is JArray newChildren)
                    DiffInto(oldChildren, newChildren, path + "/" + item.Key + "/children", result);
            }
        }

        private static Dictionary<string, JToken> Index(JArray items)
        {
            var result = new Dictionary<string, JToken>(StringComparer.OrdinalIgnoreCase);
            foreach (JToken item in items ?? new JArray())
            {
                string name = item["name"]?.ToString();
                if (!string.IsNullOrWhiteSpace(name)) result[name] = item;
            }
            return result;
        }
    }
}
