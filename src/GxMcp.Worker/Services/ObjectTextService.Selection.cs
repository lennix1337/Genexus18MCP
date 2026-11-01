using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Xml.Linq;
using GxMcp.Worker.Models;
using GxMcp.Worker.Utils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using XmlReader = System.Xml.XmlReader;
using XmlReaderSettings = System.Xml.XmlReaderSettings;
using DtdProcessing = System.Xml.DtdProcessing;
using XmlException = System.Xml.XmlException;

namespace GxMcp.Worker.Services
{
    public sealed partial class ObjectTextService
    {
        internal bool TrySelectEntries(string target, JObject args, bool allowAll,
            out List<SearchIndex.IndexEntry> selected, out string error, bool includeChildren = false)
            => _selectionService.TrySelectEntries(target, args, allowAll, out selected, out error, includeChildren);

        private static List<TextSelector> ReadSelectors(string target, JObject args)
        {
            JToken token = args?["targets"] ?? args?["names"] ?? args?["objects"];
            return token != null
                ? TextSelectorParser.ParseMany(token)
                : TextSelectorParser.ParseMany((JToken)(args?["name"]?.ToString() ?? target));
        }

        private static List<TextSelector> ReadSelectors(JToken token)
            => TextSelectorParser.ParseMany(token);

        private static TextSelector ParseSelector(JToken token)
            => TextSelectorParser.ParseOne(token);

        internal static string SanitizeFilePart(string value)
            => TextTreePath.SanitizeFilePart(value);

        private bool TryLoadTextFiles(string inputPath, string target, JObject args,
            out string root, out List<TextFileEntry> files, out string error)
        {
            root = null;
            files = new List<TextFileEntry>();
            error = null;
            string full;
            try { full = Path.GetFullPath(inputPath); }
            catch (Exception ex) { error = ex.Message; return false; }

            string manifestPath = null;
            if (Directory.Exists(full))
            {
                root = full;
                manifestPath = Path.Combine(root, ManifestFileName);
                if (!File.Exists(manifestPath))
                {
                    error = "Manifest not found in input directory: " + ManifestFileName;
                    return false;
                }
            }
            else if (File.Exists(full))
            {
                root = Path.GetDirectoryName(full);
                if (string.Equals(Path.GetExtension(full), ".gxtext", StringComparison.OrdinalIgnoreCase))
                {
                    TextFileEntry standalone;
                    if (!TryBuildStandaloneEntry(full, target, args, out standalone, out error)) return false;
                    files.Add(standalone);
                    return true;
                }
                manifestPath = full;
            }
            else
            {
                error = "Input path does not exist.";
                return false;
            }

            try
            {
                JObject manifest = JObject.Parse(File.ReadAllText(manifestPath));
                string kind = manifest["kind"]?.ToString();
                if (!string.IsNullOrWhiteSpace(kind) && !string.Equals(kind, ManifestKind, StringComparison.OrdinalIgnoreCase))
                {
                    error = "Unsupported Object Text manifest kind: " + kind;
                    return false;
                }

                JArray items = manifest["objects"] as JArray ?? manifest["files"] as JArray;
                if (items == null)
                {
                    error = "Manifest must contain an objects[] array.";
                    return false;
                }
                foreach (JToken token in items)
                {
                    string name = token["name"]?.ToString() ?? token["target"]?.ToString();
                    string file = token["file"]?.ToString() ?? token["path"]?.ToString();
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(file))
                    {
                        error = "Every manifest entry requires name and file.";
                        return false;
                    }
                    files.Add(new TextFileEntry
                    {
                        Name = name,
                        Type = token["type"]?.ToString(),
                        Part = token["part"]?.ToString() ?? manifest["part"]?.ToString() ?? "Source",
                        File = file,
                        Module = token["module"]?.ToString(),
                        Path = token["objectPath"]?.ToString() ?? token["pathPrefix"]?.ToString()
                    });
                }
            }
            catch (Exception ex)
            {
                error = "Could not read Object Text manifest: " + ex.Message;
                return false;
            }
            return true;
        }

        private static bool TryBuildStandaloneEntry(string fullPath, string target, JObject args,
            out TextFileEntry entry, out string error)
        {
            entry = null;
            error = null;
            string requested = args["name"]?.ToString() ?? target;
            TextSelector selector = string.IsNullOrWhiteSpace(requested) ? null : ParseSelector(requested);
            string type = selector?.Type ?? args["type"]?.ToString();
            string name = selector?.Name;

            if (string.IsNullOrWhiteSpace(name) || name == "*")
            {
                string stem = Path.GetFileNameWithoutExtension(fullPath) ?? string.Empty;
                int marker = stem.IndexOf("__", StringComparison.Ordinal);
                if (marker > 0 && marker + 2 < stem.Length)
                {
                    string left = stem.Substring(0, marker);
                    int separator = left.IndexOf('_');
                    if (separator >= 0 && separator + 1 < left.Length)
                    {
                        if (string.IsNullOrWhiteSpace(type)) type = left.Substring(separator + 1);
                    }
                    else if (string.IsNullOrWhiteSpace(type))
                    {
                        type = left;
                    }
                    name = stem.Substring(marker + 2);
                }
            }

            if (string.IsNullOrWhiteSpace(name) || name == "*")
            {
                error = "A standalone .gxtext file needs name (or target), or a deterministic export filename such as 00000_Procedure__MyProc.gxtext.";
                return false;
            }

            entry = new TextFileEntry
            {
                Name = name,
                Type = type,
                Part = args["part"]?.ToString() ?? "Source",
                File = Path.GetFileName(fullPath),
                Module = args["module"]?.ToString(),
                Path = args["objectPath"]?.ToString() ?? args["pathPrefix"]?.ToString()
            };
            return true;
        }

        private static bool ApplyManifestSelector(List<TextFileEntry> files, JObject args, string target)
        {
            var selectors = ReadSelectors(target, args);
            string type = args["type"]?.ToString();
            string module = args["module"]?.ToString();
            string prefix = TextTreePath.NormalizePath(args["pathPrefix"]?.ToString());
            bool explicitSelection = HasExplicitSelector(args, target);
            files.RemoveAll(item => !MatchesManifestSelector(item, selectors, explicitSelection, type, module, prefix));
            var ignored = ReadSelectors(args?["ignore"]);
            if (ignored.Any(TextSelectorParser.IsAll)) files.Clear();
            else if (ignored.Count > 0)
                files.RemoveAll(item => ignored.Any(selector => SelectorMatches(selector, item)));
            int limit = args["limit"]?.ToObject<int?>() ?? 0;
            if (limit > 0 && files.Count > limit)
                files.RemoveRange(limit, files.Count - limit);
            return files.Count > 0;
        }

        private static bool MatchesManifestSelector(TextFileEntry item, List<TextSelector> selectors,
            bool explicitSelection, string type, string module, string prefix)
        {
            if (item == null) return false;
            if ((explicitSelection && selectors.Count == 0)
                || (selectors.Count > 0 && !selectors.Any(s => SelectorMatches(s, item))))
                return false;
            if (!string.IsNullOrWhiteSpace(type)
                && !string.Equals(type, item.Type, StringComparison.OrdinalIgnoreCase))
                return false;
            if (!string.IsNullOrWhiteSpace(module)
                && !string.Equals(module, item.Module, StringComparison.OrdinalIgnoreCase))
                return false;
            return string.IsNullOrWhiteSpace(prefix)
                || TextTreePath.NormalizePath(item.Path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasExplicitSelector(JObject args, string target)
        {
            if (!string.IsNullOrWhiteSpace(target)) return true;
            if (args == null) return false;
            if (args["targets"] != null || args["names"] != null || args["objects"] != null) return true;
            return !string.IsNullOrWhiteSpace(args["name"]?.ToString());
        }

        private static bool SelectorMatches(TextSelector selector, TextFileEntry item)
            => item != null && TextSelectorParser.Matches(selector, item.Name, item.Type);

        private static string ValidateTextFile(string path, string part)
        {
            try
            {
                string text = File.ReadAllText(path);
                if (text == null) return "File could not be read.";
                if (string.Equals(part, "WebForm", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(part, "Layout", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(part, "PatternInstance", StringComparison.OrdinalIgnoreCase))
                {
                    try { XDocument.Parse(text, LoadOptions.PreserveWhitespace); }
                    catch (Exception ex) { return "Invalid XML: " + ex.Message; }
                }
                return null;
            }
            catch (Exception ex) { return ex.Message; }
        }

    }
}
