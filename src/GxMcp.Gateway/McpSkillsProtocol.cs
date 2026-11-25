using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    /// <summary>Publishes the embedded catalog as MCP Skills manifests and Markdown resources.</summary>
    internal static class McpSkillsProtocol
    {
        internal const string Capability = "io.modelcontextprotocol/skills";
        private const string UriPrefix = "skill://";
        private const int TtlMs = 300_000;
        private static readonly Lazy<Publication> Published = new(BuildPublication);

        internal static object List(JObject request)
        {
            // This catalog fits on one page, so no valid continuation cursor exists.
            if ((request["params"] as JObject)?["cursor"] != null)
                return new McpRouterError(-32602, "Invalid skill cursor.");

            return new { resultType = "complete", skills = Published.Value.Skills.Values, ttlMs = TtlMs, cacheScope = "public" };
        }

        internal static object Get(JObject request)
        {
            string? uri = ((request["params"] as JObject)?["uri"] as JValue)?.Value as string;
            if (uri == null || !Published.Value.Skills.TryGetValue(uri, out var skill))
                return new McpRouterError(-32602, "Expected the URI of a published SKILL.md.");

            return new { resultType = "complete", skill, ttlMs = TtlMs, cacheScope = "public" };
        }

        internal static bool IsSkillUri(string uri) => uri.StartsWith(UriPrefix, StringComparison.OrdinalIgnoreCase);

        internal static bool TryRead(string uri, out string text) => Published.Value.Files.TryGetValue(uri, out text!);

        private sealed record Publication(Dictionary<string, JObject> Skills, Dictionary<string, string> Files);

        private static Publication BuildPublication()
        {
            var publication = new Publication(new(StringComparer.Ordinal), new(StringComparer.Ordinal));
            foreach (var entry in SkillCatalog.All)
            {
                bool nexa = entry.Key == "nexa";
                string description = nexa ? ReadNexaDescription(entry.Body) : entry.Description;
                string text = nexa ? entry.Body :
                    $"---\nname: {entry.Key}\ndescription: {JsonConvert.ToString(description)}\n---\n\n{entry.Body}";
                string directory = $"{UriPrefix}{entry.Key}/";
                string uri = directory + "SKILL.md";
                var manifest = new JArray(AddFile(publication.Files, uri, text));
                if (nexa)
                {
                    foreach (var reference in NexaSkillPack.References.OrderBy(file => file.Key, StringComparer.Ordinal))
                        manifest.Add(AddFile(publication.Files, directory + reference.Key, reference.Value));
                }

                publication.Skills.Add(uri, new JObject
                {
                    ["uri"] = uri,
                    ["frontmatter"] = new JObject { ["name"] = entry.Key, ["description"] = description },
                    ["resources"] = manifest
                });
            }
            return publication;
        }

        private static string ReadNexaDescription(string text)
        {
            // The embedded Nexa skill has a two-field plain-scalar header. Reject
            // a changed header rather than silently dropping metadata from the manifest.
            using var reader = new StringReader(text);
            if (reader.ReadLine() != "---" || reader.ReadLine() != "name: nexa")
                throw new InvalidOperationException("Unexpected embedded Nexa skill header.");
            const string prefix = "description: ";
            string? description = reader.ReadLine();
            if (description == null || !description.StartsWith(prefix, StringComparison.Ordinal) ||
                reader.ReadLine() != "---" || string.IsNullOrWhiteSpace(description[prefix.Length..]))
                throw new InvalidOperationException("Unexpected embedded Nexa skill frontmatter.");
            return description[prefix.Length..];
        }

        private static JObject AddFile(Dictionary<string, string> files, string uri, string text)
        {
            files.Add(uri, text);
            var bytes = Encoding.UTF8.GetBytes(text);
            return new JObject
            {
                ["uri"] = uri,
                ["digest"] = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes)),
                ["size"] = bytes.Length
            };
        }
    }
}
