using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace GxMcp.Gateway
{
    /// <summary>
    /// Read-only access to the official Nexa Markdown pack embedded in the
    /// Gateway. The package's scripts and JSON catalog are intentionally not
    /// exposed or executed through MCP.
    /// </summary>
    internal static class NexaSkillPack
    {
        private const string RootResourceName = "GxMcp.Gateway.Nexa.SKILL.md";
        private const string ReferenceResourcePrefix = "GxMcp.Gateway.Nexa.references.";

        private static readonly IReadOnlyDictionary<string, string> Resources = LoadResources();

        internal static IEnumerable<KeyValuePair<string, string>> References => Resources.Where(file =>
            file.Key.StartsWith("references/", StringComparison.OrdinalIgnoreCase));

        internal static int ReferenceCount => References.Count();

        internal static string ReadRoot()
        {
            if (!TryRead("SKILL.md", out var body))
            {
                throw new InvalidOperationException("The embedded Nexa SKILL.md resource is missing.");
            }

            return body;
        }

        internal static bool TryRead(string path, out string body)
        {
            body = string.Empty;
            string normalized = path.Replace('\\', '/');

            if (string.Equals(normalized, "SKILL.md", StringComparison.OrdinalIgnoreCase))
            {
                return Resources.TryGetValue("SKILL.md", out body!);
            }

            const string referencesPrefix = "references/";
            if (!normalized.StartsWith(referencesPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string referenceName = normalized.Substring(referencesPrefix.Length);
            if (!IsSafeReferenceName(referenceName))
            {
                return false;
            }

            return Resources.TryGetValue(referencesPrefix + referenceName, out body!);
        }

        private static bool IsSafeReferenceName(string name)
        {
            return name.Length > 0
                && name.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                && name.IndexOf('/') < 0
                && name.IndexOf('\\') < 0
                && !string.Equals(name, ".", StringComparison.Ordinal)
                && !string.Equals(name, "..", StringComparison.Ordinal);
        }

        private static IReadOnlyDictionary<string, string> LoadResources()
        {
            var assembly = typeof(NexaSkillPack).Assembly;
            var resources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            AddResource(assembly, RootResourceName, "SKILL.md", resources);

            foreach (var resourceName in assembly.GetManifestResourceNames()
                .Where(name => name.StartsWith(ReferenceResourcePrefix, StringComparison.Ordinal)))
            {
                string referenceName = resourceName.Substring(ReferenceResourcePrefix.Length);
                if (referenceName.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                {
                    AddResource(assembly, resourceName, "references/" + referenceName, resources);
                }
            }

            return resources;
        }

        private static void AddResource(
            Assembly assembly,
            string resourceName,
            string path,
            IDictionary<string, string> resources)
        {
            using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"The embedded Nexa resource '{resourceName}' is missing.");
            using var reader = new StreamReader(
                stream,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                detectEncodingFromByteOrderMarks: true);
            resources.Add(path, reader.ReadToEnd());
        }
    }
}
