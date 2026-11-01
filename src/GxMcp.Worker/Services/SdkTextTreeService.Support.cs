using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using GxMcp.Worker.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    public sealed partial class SdkTextTreeService
    {
        private static JObject ParseEnvelope(string raw)
        {
            try { return JObject.Parse(raw ?? "{}"); }
            catch (Exception ex) { return new JObject { ["status"] = "error", ["message"] = ex.Message }; }
        }

        private static bool IsSuccess(JObject response)
        {
            string status = response?["status"]?.ToString();
            if (string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "success", StringComparison.OrdinalIgnoreCase)) return true;
            return response?["error"] == null
                && !string.Equals(status, "error", StringComparison.OrdinalIgnoreCase)
                && response?["result"] != null;
        }

        private static JObject BuildResult(ExportPlan item, string status, long bytes, string message, string code = null, string hint = null)
        {
            var result = new JObject
            {
                ["name"] = item.Entry.Name,
                ["type"] = item.Entry.Type,
                ["role"] = item.Role,
                ["part"] = item.Part,
                ["parts"] = BuildPartArray(item.Parts ?? new List<string> { item.Part }),
                ["tableProjection"] = item.IsTableProjection,
                ["file"] = item.RelativePath,
                ["status"] = status,
                ["bytes"] = bytes
            };
            if (item.IsTableProjection && item.ReadEntry != null)
                result["sourceObject"] = new JObject { ["name"] = item.ReadEntry.Name, ["type"] = item.ReadEntry.Type };
            // The message is plain text; the code and the hint are fields, so a client branches on them directly.
            if (message != null) result["message"] = message;
            if (code != null) result["code"] = code;
            if (hint != null) result["hint"] = hint;
            return result;
        }

        private static JObject BuildImportError(string name, string type, string file, JObject response)
        {
            return new JObject
            {
                ["name"] = name,
                ["type"] = type,
                ["file"] = file,
                ["status"] = "error",
                ["message"] = response?["error"]?.ToString() ?? response?["message"]?.ToString() ?? "SDK import failed."
            };
        }

        private static JObject BuildManifestItem(ExportPlan item, JObject previous, string document, JArray companions)
        {
            string hash = document == null ? previous?["sha256"]?.ToString() : HashText(document);
            long bytes = document == null ? previous?["bytes"]?.ToObject<long?>() ?? new FileInfo(item.FilePath).Length : Encoding.UTF8.GetByteCount(document);
            JArray parts = document == null && previous?["parts"] is JArray previousParts
                ? CloneArray(previousParts)
                : BuildPartArray(item.Parts ?? new List<string> { item.Part });
            var result = new JObject
            {
                ["name"] = item.Entry.Name,
                ["type"] = item.Entry.Type,
                ["guid"] = item.Entry.Guid,
                ["entityKey"] = item.Entry.EntityKey,
                ["module"] = item.Entry.Module,
                ["role"] = item.Role,
                ["part"] = item.Part,
                ["parts"] = parts,
                ["tableProjection"] = item.IsTableProjection,
                ["file"] = item.RelativePath,
                ["sha256"] = hash,
                ["bytes"] = bytes,
                ["versionToken"] = item.VersionToken,
                ["companions"] = companions ?? new JArray()
            };
            if (item.IsTableProjection && item.ReadEntry != null)
                result["sourceObject"] = new JObject { ["name"] = item.ReadEntry.Name, ["type"] = item.ReadEntry.Type };
            return result;
        }

        private static JArray CloneArray(JArray source)
        {
            var result = new JArray();
            if (source == null) return result;
            foreach (JToken token in source) result.Add(token?.DeepClone());
            return result;
        }

        private static JArray BuildPartArray(IEnumerable<string> parts)
        {
            var result = new JArray();
            if (parts == null) return result;
            foreach (string part in parts)
                if (!string.IsNullOrWhiteSpace(part)) result.Add(part);
            return result;
        }

        private static Dictionary<string, JObject> LoadManifest(string root)
        {
            var result = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
            string path = Path.Combine(root, ManifestFileName);
            if (!File.Exists(path)) return result;
            try
            {
                JArray objects = JObject.Parse(File.ReadAllText(path))["objects"] as JArray;
                if (objects == null) return result;
                foreach (JToken token in objects)
                {
                    JObject item = token as JObject;
                    if (item == null) continue;
                    string key = ManifestKey(item["type"]?.ToString(), item["name"]?.ToString());
                    if (key != null) result[key] = item;
                }
            }
            catch { }
            return result;
        }

        private static bool ShouldSkip(ExportPlan item, JObject previous, string mode)
        {
            if (mode == "newOnly") return File.Exists(item.FilePath);
            return mode == "newAndModified"
                && previous != null
                && File.Exists(item.FilePath)
                && !string.IsNullOrWhiteSpace(item.VersionToken)
                && string.Equals(item.VersionToken, previous["versionToken"]?.ToString(), StringComparison.Ordinal);
        }

        private static string NormalizeMode(string mode)
        {
            if (string.IsNullOrWhiteSpace(mode)) return "all";
            if (string.Equals(mode, "all", StringComparison.OrdinalIgnoreCase)) return "all";
            if (string.Equals(mode, "newAndModified", StringComparison.OrdinalIgnoreCase)) return "newAndModified";
            if (string.Equals(mode, "newOnly", StringComparison.OrdinalIgnoreCase)) return "newOnly";
            return null;
        }

        private static readonly string[] NativeAllParts =
        {
            "Source", "Rules", "Events", "Variables", "Conditions", "Documentation", "Help",
            "Methods", "Properties", "Structure", "Indexes", "WebForm", "Layout", "PatternInstance"
        };

        private static List<string> ResolveRequestedParts(JObject args)
        {
            var result = new List<string>();
            JToken token = args?["parts"];
            if (token is JArray array)
            {
                foreach (JToken item in array)
                {
                    string part = item?.ToString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(part) && !result.Contains(part, StringComparer.OrdinalIgnoreCase))
                        result.Add(part);
                }
            }
            else if (token != null && token.Type != JTokenType.Null)
            {
                foreach (string part in token.ToString().Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string value = part.Trim();
                    if (!string.IsNullOrWhiteSpace(value) && !result.Contains(value, StringComparer.OrdinalIgnoreCase))
                        result.Add(value);
                }
            }

            string requested = args?["part"]?.ToString()?.Trim();
            bool allParts = string.Equals(requested, "all", StringComparison.OrdinalIgnoreCase);
            if (result.Any(value => string.Equals(value, "all", StringComparison.OrdinalIgnoreCase)))
            {
                result.Clear();
                allParts = true;
            }
            if (allParts) result.Clear();
            else if (result.Count == 0 && !string.IsNullOrWhiteSpace(requested)) result.Add(requested);
            if (result.Count == 0)
            {
                if (allParts) result.AddRange(NativeAllParts);
                else result.Add("Source");
            }
            return result;
        }

        private static string BuildVersionToken(SearchIndex.IndexEntry entry, string part)
        {
            if (entry == null || entry.LastUpdate == DateTime.MinValue) return null;
            return (entry.Guid ?? entry.Type + ":" + entry.Name) + "|" + entry.LastUpdate.ToUniversalTime().Ticks + "|" + part;
        }

        private static string BuildRelativeFolder(SearchIndex.IndexEntry entry)
        {
            string raw = entry?.ParentFolderPath ?? entry?.ParentPath;
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
            string[] parts = raw.Replace('\\', '/').Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            var safe = new List<string>();
            foreach (string part in parts)
            {
                if (string.Equals(part, "Root Module", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(part, "Root", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(part, ".", StringComparison.Ordinal)) continue;
                string value = ObjectTextService.SanitizeFilePart(part);
                if (!string.IsNullOrWhiteSpace(value)) safe.Add(value);
            }
            return safe.Count == 0 ? string.Empty : Path.Combine(safe.ToArray());
        }

        private static bool IsVisualCandidate(string type)
        {
            return string.Equals(type, "WebPanel", StringComparison.OrdinalIgnoreCase)
                || string.Equals(type, "WebComponent", StringComparison.OrdinalIgnoreCase)
                || string.Equals(type, "Transaction", StringComparison.OrdinalIgnoreCase)
                || string.Equals(type, "Report", StringComparison.OrdinalIgnoreCase)
                || string.Equals(type, "SDPanel", StringComparison.OrdinalIgnoreCase);
        }

        private static List<string> CollectNativeObjectFiles(string input, string root, bool includeReferences, bool includeChildren)
        {
            var files = new List<string>();
            if (File.Exists(input) && string.Equals(Path.GetExtension(input), ".gx", StringComparison.OrdinalIgnoreCase))
            {
                files.Add(input);
                return files;
            }
            SearchOption searchOption = includeChildren ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            foreach (string file in Directory.EnumerateFiles(root, "*.gx", searchOption))
            {
                string relative = NormalizePath(MakeRelative(root, file));
                if (!includeReferences && relative.StartsWith("ref/", StringComparison.OrdinalIgnoreCase)) continue;
                files.Add(file);
            }
            files.Sort(StringComparer.OrdinalIgnoreCase);
            return files;
        }

        private static bool MatchesImportSelector(string type, string name, string target, JObject args)
        {
            string typeFilter = args["type"]?.ToString();
            if (!string.IsNullOrWhiteSpace(typeFilter) && !string.Equals(typeFilter, type, StringComparison.OrdinalIgnoreCase)) return false;
            var selectors = new List<Tuple<string, string>>();
            JArray array = args["targets"] as JArray ?? args["names"] as JArray ?? args["objects"] as JArray;
            if (array != null)
            {
                foreach (JToken token in array)
                {
                    string selectorName = token.Type == JTokenType.Object ? token["name"]?.ToString() ?? token["target"]?.ToString() : token.ToString();
                    string selectorType = token.Type == JTokenType.Object ? token["type"]?.ToString() : null;
                    if (!string.IsNullOrWhiteSpace(selectorName)) selectors.Add(Tuple.Create(selectorType, selectorName));
                }
            }
            else
            {
                string names = args["name"]?.ToString() ?? target;
                if (!string.IsNullOrWhiteSpace(names))
                {
                    foreach (string raw in names.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        string value = raw.Trim();
                        int colon = value.IndexOf(':');
                        if (colon > 0 && colon + 1 < value.Length) selectors.Add(Tuple.Create(value.Substring(0, colon), value.Substring(colon + 1)));
                        else selectors.Add(Tuple.Create<string, string>(null, value));
                    }
                }
            }
            if (selectors.Count == 0) return true;
            return selectors.Any(s => (string.IsNullOrWhiteSpace(s.Item1) || string.Equals(s.Item1, type, StringComparison.OrdinalIgnoreCase))
                && (s.Item2 == "*" || string.Equals(s.Item2, name, StringComparison.OrdinalIgnoreCase)));
        }

        private static string ManifestKey(SearchIndex.IndexEntry entry)
        {
            return ManifestKey(entry?.Type, entry?.Name);
        }

        private static string ManifestKey(string type, string name)
        {
            return string.IsNullOrWhiteSpace(name) ? null : (type ?? string.Empty) + ":" + name;
        }

        internal static bool IsFullSelection(string target, JObject args)
        {
            if (!string.IsNullOrWhiteSpace(target)) return false;
            foreach (string key in new[] { "name", "targets", "names", "objects", "type", "module", "pathPrefix" })
            {
                JToken value = args?[key];
                if (value == null || value.Type == JTokenType.Null) continue;
                if (value.Type == JTokenType.Array && !value.HasValues) continue;
                if ((key == "targets" || key == "names" || key == "objects") && IsAllSelectorValue(value)) continue;
                if (!string.IsNullOrWhiteSpace(value.ToString())) return false;
            }
            // A limit, an ignore list or a skip leaves KB objects outside this export, so it is not the whole KB.
            if ((args?["limit"]?.ToObject<int?>() ?? 0) > 0) return false;
            if (TextSelectorParser.ParseMany(args?["ignore"]).Count > 0) return false;
            if ((args?["skip"]?.ToObject<int?>() ?? 0) > 0) return false;
            return true;
        }

        private static bool IsAllSelectorValue(JToken value)
        {
            if (value == null) return false;
            if (value.Type == JTokenType.String)
            {
                string text = value.ToString().Trim();
                return string.Equals(text, "[all]", StringComparison.OrdinalIgnoreCase) || text == "*";
            }
            if (value is JArray array && array.Count == 1)
                return IsAllSelectorValue(array[0]);
            return false;
        }

        private static int RemoveStaleFiles(string root, Dictionary<string, JObject> previous, HashSet<string> planKeys)
        {
            int removed = 0;
            foreach (JObject item in previous.Values)
            {
                if (planKeys.Contains(ManifestKey(item["type"]?.ToString(), item["name"]?.ToString()))) continue;
                if (MirrorRootFile.TryResolve(root, item["file"]?.ToString(), out string file) && MirrorRootFile.TryDelete(file)) removed++;
                if (item["companions"] is JArray companions)
                {
                    foreach (JObject companion in companions.OfType<JObject>())
                        if (MirrorRootFile.TryResolve(root, companion["file"]?.ToString(), out string companionFile) && MirrorRootFile.TryDelete(companionFile)) removed++;
                }
            }
            return removed;
        }

        private static string NormalizePath(string value)
        {
            return (value ?? string.Empty).Replace('\\', '/').TrimStart('/');
        }

        private static string MakeRelative(string root, string file)
        {
            try
            {
                string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                string fullFile = Path.GetFullPath(file);
                if (fullFile.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) return NormalizePath(fullFile.Substring(fullRoot.Length));
            }
            catch { }
            return NormalizePath(Path.GetFileName(file));
        }

        private static void WriteTextAtomically(string path, string content, bool overwrite)
        {
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory)) Directory.CreateDirectory(directory);
            string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(temporary, content ?? string.Empty, new UTF8Encoding(false));
                if (File.Exists(path))
                {
                    if (!overwrite) throw new IOException("Output file already exists: " + path);
                    try { File.Replace(temporary, path, null); }
                    catch
                    {
                        File.Delete(path);
                        File.Move(temporary, path);
                    }
                }
                else File.Move(temporary, path);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            }
        }

        private static void CopyFileAtomically(string source, string path, bool overwrite)
        {
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory)) Directory.CreateDirectory(directory);
            string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.Copy(source, temporary, true);
                if (File.Exists(path))
                {
                    if (!overwrite) throw new IOException("Output file already exists: " + path);
                    try { File.Replace(temporary, path, null); }
                    catch
                    {
                        File.Delete(path);
                        File.Move(temporary, path);
                    }
                }
                else File.Move(temporary, path);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            }
        }

        private static string HashText(string value)
        {
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty));
                return BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        private static string HashFile(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
        }

        private static string BuildCancelled(string root, JArray results, JArray manifestItems, int planned, int exported, int skipped, int noTextParts, int failed, int filesWritten, long bytesWritten)
        {
            return McpResponse.Ok(code: "Cancelled", result: new JObject
            {
                ["format"] = "native",
                ["root"] = root,
                ["planned"] = planned,
                ["exported"] = exported,
                ["skipped"] = skipped,
                ["noTextParts"] = noTextParts,
                ["failed"] = failed,
                ["filesWritten"] = filesWritten,
                ["bytesWritten"] = bytesWritten,
                ["cancelled"] = true,
                ["objects"] = results,
                ["manifestObjects"] = manifestItems
            });
        }

        private static string BuildImportCancelled(string root, JArray results, int planned, int succeeded, int failed, bool dryRun)
        {
            return McpResponse.Ok(code: "Cancelled", result: new JObject
            {
                ["format"] = "native",
                ["root"] = root,
                ["planned"] = planned,
                ["succeeded"] = succeeded,
                ["failed"] = failed,
                ["dryRun"] = dryRun,
                ["cancelled"] = true,
                ["objects"] = results
            });
        }

        private sealed class ExportPlan
        {
            public SearchIndex.IndexEntry Entry { get; set; }
            public SearchIndex.IndexEntry ReadEntry { get; set; }
            public string Role { get; set; }
            public string Part { get; set; }
            public string FilePath { get; set; }
            public string RelativePath { get; set; }
            public string VersionToken { get; set; }
            public List<string> Parts { get; set; } = new List<string>();
            public bool IsTableProjection { get; set; }
        }

        private sealed class NativeImportItem
        {
            public string Name { get; set; }
            public string Type { get; set; }
            public string File { get; set; }
        }

    }
}
