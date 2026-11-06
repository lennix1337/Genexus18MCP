using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    internal static class LargePartReadLiveAssertions
    {
        internal static async Task Verify(Func<string, JObject, Task<JObject>> call, string type, string part)
        {
            string name = "Mcp881" + Guid.NewGuid().ToString("N").Substring(0, 10);
            string[] lines = Enumerable.Range(0, 450)
                .Select(i => "// Mcp881Line" + i.ToString("D4") + " " + new string('x', 70)).ToArray();
            bool created = false;
            bool uncertain = false;
            try
            {
                uncertain = true;
                await call("genexus_create", new JObject { ["action"] = "object", ["name"] = name, ["type"] = type });
                created = true;
                uncertain = false;
                var edit = await EditArguments(call, name, type, part, lines);
                uncertain = true;
                await call("genexus_edit", edit);
                uncertain = false;
                await VerifyPages(call, name, type, part, lines);
            }
            finally
            {
                if (created && !uncertain)
                    await call("genexus_delete_object", new JObject { ["name"] = name, ["type"] = type, ["confirm"] = true });
                if (uncertain) Console.Error.WriteLine("Uncertain mutation; reconcile owned fixture before cleanup: " + name);
            }
        }

        private static async Task<JObject> EditArguments(Func<string, JObject, Task<JObject>> call,
            string name, string type, string part, string[] lines)
        {
            var edit = new JObject
            {
                ["name"] = name, ["type"] = type, ["part"] = part, ["mode"] = "full",
                ["content"] = string.Join("\n", lines), ["autoDeclareVariables"] = false,
                ["requireObjectSave"] = true
            };
            if (part == "Events")
            {
                var initial = await call("genexus_read", new JObject
                    { ["name"] = name, ["type"] = type, ["part"] = part, ["limit"] = 0 });
                edit["mode"] = "patch";
                edit["operation"] = "Append";
                edit["baseVersion"] = Body(initial)["versionToken"];
            }
            return edit;
        }

        private static async Task VerifyPages(Func<string, JObject, Task<JObject>> call,
            string name, string type, string part, string[] expected)
        {
            var full = await call("genexus_read", new JObject
                { ["name"] = name, ["type"] = type, ["part"] = part, ["limit"] = 0, ["refresh"] = true });
            string source = Body(full)["source"]!.Value<string>()!;
            Assert.Contains(expected[expected.Length - 1], source);
            var plural = await call("genexus_read", new JObject
                { ["name"] = name, ["type"] = type, ["parts"] = new JArray(part), ["limit"] = 0 });
            Assert.Equal(source, PartSource(plural, part));
            var collected = new List<string>();
            int totalLines = source.Replace("\r\n", "\n").Split('\n').Length;
            for (int offset = 0; offset < totalLines; offset += 100)
            {
                var args = new JObject { ["name"] = name, ["type"] = type, ["part"] = part, ["offset"] = offset, ["limit"] = 100 };
                string singular = Body(await call("genexus_read", args))["source"]!.Value<string>()!;
                args.Remove("part");
                args["parts"] = new JArray(part);
                Assert.Equal(singular, PartSource(await call("genexus_read", args), part));
                collected.AddRange(singular.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Where(line => line.StartsWith("// Mcp881Line", StringComparison.Ordinal)));
            }
            Assert.Equal(expected, collected);
        }

        private static JObject Body(JObject payload) => payload["result"] as JObject ?? payload;

        private static string PartSource(JObject payload, string part)
        {
            var parts = Body(payload)["parts"] as JObject;
            Assert.NotNull(parts);
            Assert.NotNull(parts![part]);
            return parts[part]!.Value<string>()!;
        }
    }
}
