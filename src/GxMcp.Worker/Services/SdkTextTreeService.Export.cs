using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using GxMcp.Worker.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    public sealed partial class SdkTextTreeService
    {
        private string Export(string target, JObject args, CancellationToken ct)
            => RunNativeExport(target, args ?? new JObject(), ct);

        private void WriteModuleMetadata(
            List<ExportPlan> plan,
            string root,
            string mode,
            bool overwrite,
            Dictionary<string, JObject> previous,
            JArray metadataItems,
            HashSet<string> emitted,
            ref int filesWritten,
            ref long bytesWritten,
            ref int metadataFilesWritten,
            ref int metadataFilesSkipped,
            ref int failed,
            ref bool stoppedOnError,
            bool stopOnError)
        {
            var modules = new Dictionary<string, ModuleMetadataPlan>(StringComparer.OrdinalIgnoreCase);
            var moduleEntries = new Dictionary<string, SearchIndex.IndexEntry>(StringComparer.OrdinalIgnoreCase);
            SearchIndex index = _indexCacheService?.TryGetLoadedIndex();
            foreach (ExportPlan item in plan)
            {
                string module = item.Entry?.Module;
                if (string.IsNullOrWhiteSpace(module)
                    || string.Equals(module, "Root Module", StringComparison.OrdinalIgnoreCase)) continue;
                // The index entry of a module is looked up once per module, not once per object.
                if (!moduleEntries.TryGetValue(module, out SearchIndex.IndexEntry moduleEntry))
                {
                    moduleEntry = index?.Objects?.Values.FirstOrDefault(candidate =>
                        candidate != null
                        && string.Equals(candidate.Type, "Module", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(candidate.Name, module, StringComparison.OrdinalIgnoreCase));
                    moduleEntries[module] = moduleEntry;
                }
                string directory = BuildModuleDirectory(item.Entry, module, moduleEntry);
                if (string.IsNullOrWhiteSpace(directory)) continue;
                string relativeFile = NormalizePath(Path.Combine(item.Role, directory, "module.toml"));
                if (modules.ContainsKey(relativeFile)) continue;
                string identifier = ResolveModuleIdentifier(moduleEntry, module);
                modules[relativeFile] = new ModuleMetadataPlan
                {
                    Module = module,
                    RelativeFile = relativeFile,
                    Identifier = identifier,
                    Content = BuildModuleMetadataDocument(module, identifier)
                };
            }

            foreach (ModuleMetadataPlan item in modules.Values.OrderBy(value => value.RelativeFile, StringComparer.OrdinalIgnoreCase))
            {
                string fullPath = Path.Combine(root, item.RelativeFile.Replace('/', Path.DirectorySeparatorChar));
                JObject old = previous.TryGetValue(item.RelativeFile, out JObject prior) ? prior : null;
                if (string.IsNullOrWhiteSpace(item.Identifier))
                {
                    // A module.toml without the module's identity is never written. An earlier file is kept as it is.
                    if (File.Exists(fullPath))
                    {
                        metadataItems.Add(old ?? BuildMetadataItem(item, File.ReadAllText(fullPath)));
                        emitted.Add(item.RelativeFile);
                    }
                    continue;
                }
                if (File.Exists(fullPath) && mode != "all")
                {
                    metadataItems.Add(old ?? BuildMetadataItem(item, File.ReadAllText(fullPath)));
                    emitted.Add(item.RelativeFile);
                    metadataFilesSkipped++;
                    continue;
                }
                if (File.Exists(fullPath) && !overwrite)
                {
                    failed++;
                    if (old != null) metadataItems.Add((JObject)old.DeepClone());
                    emitted.Add(item.RelativeFile);
                    if (stopOnError) { stoppedOnError = true; break; }
                    continue;
                }
                try
                {
                    WriteTextAtomically(fullPath, item.Content, true);
                    long bytes = new FileInfo(fullPath).Length;
                    filesWritten++;
                    bytesWritten += bytes;
                    metadataFilesWritten++;
                    metadataItems.Add(BuildMetadataItem(item, item.Content));
                    emitted.Add(item.RelativeFile);
                }
                catch (Exception ex)
                {
                    failed++;
                    if (old != null) metadataItems.Add((JObject)old.DeepClone());
                    emitted.Add(item.RelativeFile);
                    if (stopOnError)
                    {
                        stoppedOnError = true;
                        break;
                    }
                    _ = ex;
                }
            }
        }

        // module.toml belongs to the module root. The directory ends at the module's own segment, so the
        // folders under it (WWPTemplates/Selection under WWPBaseObjects) never become modules. A path that
        // does not show the module keeps its whole folder path, as it did before.
        internal static string BuildModuleDirectory(SearchIndex.IndexEntry entry, string module)
        {
            string raw = entry?.ParentFolderPath ?? entry?.ParentPath;
            var parts = string.IsNullOrWhiteSpace(raw)
                ? new List<string>()
                : raw.Replace('\\', '/').Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries)
                    .Where(value => !string.Equals(value, "Root Module", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(value, "Root", StringComparison.OrdinalIgnoreCase))
                    .Select(ObjectTextService.SanitizeFilePart)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .ToList();
            int moduleSegment = string.IsNullOrWhiteSpace(module)
                ? -1
                : parts.FindIndex(value => string.Equals(value, ObjectTextService.SanitizeFilePart(module), StringComparison.OrdinalIgnoreCase));
            if (moduleSegment >= 0) parts = parts.Take(moduleSegment + 1).ToList();
            if (parts.Count == 0 && !string.IsNullOrWhiteSpace(module))
                parts.Add(ObjectTextService.SanitizeFilePart(module));
            return parts.Count == 0 ? string.Empty : Path.Combine(parts.ToArray());
        }

        // The folder of a module is the path its own index entry holds. The path of an object does not decide it: a
        // folder above the module can carry the module's name. Without the module entry, the object's path decides.
        internal static string BuildModuleDirectory(SearchIndex.IndexEntry entry, string module, SearchIndex.IndexEntry moduleEntry)
        {
            if (string.IsNullOrWhiteSpace(module) || string.IsNullOrWhiteSpace(moduleEntry?.Path)) return BuildModuleDirectory(entry, module);
            List<string> parts = moduleEntry.Path.Replace('\\', '/').Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(value => !string.Equals(value, "Root Module", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(value, "Root", StringComparison.OrdinalIgnoreCase))
                .Select(ObjectTextService.SanitizeFilePart)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToList();
            if (parts.Count == 0
                || !string.Equals(parts[parts.Count - 1], ObjectTextService.SanitizeFilePart(module), StringComparison.OrdinalIgnoreCase))
                return BuildModuleDirectory(entry, module);
            return Path.Combine(parts.ToArray());
        }

        // The identifier written to module.toml is the module's GUID, or its EntityKey. The index may not hold the module,
        // so the SDK names it by its own name, as genexus_read does. Empty when neither gives one: no file is written then.
        private string ResolveModuleIdentifier(SearchIndex.IndexEntry moduleEntry, string module)
        {
            string identifier = moduleEntry?.Guid ?? moduleEntry?.EntityKey;
            if (!string.IsNullOrWhiteSpace(identifier)) return identifier;
            try
            {
                return _objectService?.FindObject(module, "Module")?.Guid.ToString() ?? string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        internal static string BuildModuleMetadataDocument(string module, string identifier)
        {
            return "ModuleIdentifier = " + TomlString(identifier) + Environment.NewLine
                + "Name = " + TomlString(module) + Environment.NewLine
                + "IsDefault = \"False\"";
        }

        private static string TomlString(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        private static JObject BuildMetadataItem(ModuleMetadataPlan item, string content)
        {
            return new JObject
            {
                ["module"] = item.Module,
                ["identifier"] = item.Identifier,
                ["file"] = item.RelativeFile,
                ["bytes"] = Encoding.UTF8.GetByteCount(content ?? string.Empty),
                ["sha256"] = HashText(content ?? string.Empty)
            };
        }

        private static Dictionary<string, JObject> LoadManifestMetadata(string root)
        {
            var result = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
            string path = Path.Combine(root, ManifestFileName);
            if (!File.Exists(path)) return result;
            try
            {
                JArray metadata = JObject.Parse(File.ReadAllText(path))["metadata"] as JArray;
                if (metadata == null) return result;
                foreach (JObject item in metadata.OfType<JObject>())
                {
                    string file = NormalizePath(item["file"]?.ToString());
                    if (!string.IsNullOrWhiteSpace(file)) result[file] = item;
                }
            }
            catch { }
            return result;
        }

        private static Dictionary<string, JObject> LoadManifestArray(string root, string propertyName)
        {
            var result = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
            string path = Path.Combine(root, ManifestFileName);
            if (!File.Exists(path)) return result;
            try
            {
                JArray items = JObject.Parse(File.ReadAllText(path))[propertyName] as JArray;
                if (items == null) return result;
                foreach (JObject item in items.OfType<JObject>())
                {
                    string file = NormalizePath(item["file"]?.ToString());
                    if (!string.IsNullOrWhiteSpace(file)) result[file] = item;
                }
            }
            catch { }
            return result;
        }

        private void WriteModulePackages(
            List<ExportPlan> plan,
            string root,
            string mode,
            bool overwrite,
            Dictionary<string, JObject> previous,
            JArray packageItems,
            HashSet<string> emitted,
            ref int filesWritten,
            ref long bytesWritten,
            ref int packageFilesWritten,
            ref int packageFilesSkipped,
            ref int failed,
            ref bool stoppedOnError,
            bool stopOnError)
        {
            string sdkRoot = Environment.GetEnvironmentVariable("GX_PROGRAM_DIR")
                ?? Environment.GetEnvironmentVariable("GX_PATH");
            if (string.IsNullOrWhiteSpace(sdkRoot)) return;
            string modulesRoot;
            try { modulesRoot = Path.GetFullPath(Path.Combine(sdkRoot, "Modules")); }
            catch { return; }
            if (!Directory.Exists(modulesRoot)) return;

            var moduleNames = new HashSet<string>(
                plan.Select(item => item.Entry?.Module)
                    .Where(value => !string.IsNullOrWhiteSpace(value)
                        && !string.Equals(value, "Root Module", StringComparison.OrdinalIgnoreCase)),
                StringComparer.OrdinalIgnoreCase);
            foreach (string module in moduleNames.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
            {
                string packageSource = Directory.EnumerateFiles(modulesRoot, "*.opc", SearchOption.TopDirectoryOnly)
                    .Where(file => string.Equals(Path.GetFileNameWithoutExtension(file), module, StringComparison.OrdinalIgnoreCase)
                        || (Path.GetFileNameWithoutExtension(file) ?? string.Empty).StartsWith(module + "_", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(file => Path.GetFileName(file), StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                if (packageSource == null) continue;

                string relativeFile = NormalizePath(Path.Combine("ref", Path.GetFileName(packageSource)));
                if (emitted.Contains(relativeFile)) continue;
                string destination = Path.Combine(root, relativeFile.Replace('/', Path.DirectorySeparatorChar));
                JObject old = previous.TryGetValue(relativeFile, out JObject prior) ? prior : null;
                if (File.Exists(destination) && mode != "all")
                {
                    packageItems.Add(old ?? BuildPackageItem(module, relativeFile, destination));
                    emitted.Add(relativeFile);
                    packageFilesSkipped++;
                    continue;
                }
                if (File.Exists(destination) && !overwrite)
                {
                    failed++;
                    if (old != null) packageItems.Add((JObject)old.DeepClone());
                    emitted.Add(relativeFile);
                    if (stopOnError) { stoppedOnError = true; break; }
                    continue;
                }
                try
                {
                    CopyFileAtomically(packageSource, destination, true);
                    long bytes = new FileInfo(destination).Length;
                    filesWritten++;
                    bytesWritten += bytes;
                    packageFilesWritten++;
                    packageItems.Add(BuildPackageItem(module, relativeFile, destination));
                    emitted.Add(relativeFile);
                }
                catch
                {
                    failed++;
                    if (old != null) packageItems.Add((JObject)old.DeepClone());
                    emitted.Add(relativeFile);
                    if (stopOnError) { stoppedOnError = true; break; }
                }
            }
        }

        private static JObject BuildPackageItem(string module, string relativeFile, string fullPath)
        {
            return new JObject
            {
                ["module"] = module,
                ["file"] = relativeFile,
                ["bytes"] = new FileInfo(fullPath).Length,
                ["sha256"] = HashFile(fullPath)
            };
        }

        private static int RemoveStaleMetadata(string root, Dictionary<string, JObject> previous, HashSet<string> emitted)
        {
            int removed = 0;
            foreach (JObject item in previous.Values)
            {
                string relative = NormalizePath(item["file"]?.ToString());
                if (string.IsNullOrWhiteSpace(relative) || emitted.Contains(relative)) continue;
                if (!relative.EndsWith("/module.toml", StringComparison.OrdinalIgnoreCase)) continue;
                if (MirrorRootFile.TryResolve(root, relative, out string full) && MirrorRootFile.TryDelete(full)) removed++;
            }
            return removed;
        }

        private static int RemoveStalePackages(string root, Dictionary<string, JObject> previous, HashSet<string> emitted)
        {
            int removed = 0;
            foreach (JObject item in previous.Values)
            {
                string relative = NormalizePath(item["file"]?.ToString());
                if (string.IsNullOrWhiteSpace(relative) || emitted.Contains(relative)) continue;
                if (!relative.StartsWith("ref/", StringComparison.OrdinalIgnoreCase)
                    || !relative.EndsWith(".opc", StringComparison.OrdinalIgnoreCase)) continue;
                if (MirrorRootFile.TryResolve(root, relative, out string full) && MirrorRootFile.TryDelete(full)) removed++;
            }
            return removed;
        }

        private sealed class ModuleMetadataPlan
        {
            public string Module { get; set; }
            public string Identifier { get; set; }
            public string RelativeFile { get; set; }
            public string Content { get; set; }
        }

        private void ExportVisualCompanion(
            ExportPlan item,
            string root,
            string mode,
            bool overwrite,
            JArray companions,
            ref int filesWritten,
            ref int companionFilesWritten,
            ref long bytesWritten)
        {
            string[] candidateParts = item.Entry.Type.Equals("Report", StringComparison.OrdinalIgnoreCase)
                ? new[] { "Layout", "WebForm" }
                : new[] { "WebForm", "Layout" };
            foreach (string candidatePart in candidateParts)
            {
                if (!TryReadPart(item.Entry, candidatePart, out string visual, out _)
                    || string.IsNullOrWhiteSpace(visual)
                    || visual.TrimStart().Length == 0
                    || visual.TrimStart()[0] != '<') continue;

                string extension = item.Entry.Type.Equals("Report", StringComparison.OrdinalIgnoreCase) ? ".report.xml" : ".web.xml";
                string companionPath = Path.ChangeExtension(item.FilePath, null) + extension;
                if (mode == "newOnly" && File.Exists(companionPath))
                {
                    companions.Add(new JObject { ["part"] = candidatePart, ["file"] = MakeRelative(root, companionPath), ["status"] = "skipped" });
                    return;
                }
                if (mode == "newAndModified" && File.Exists(companionPath) && !overwrite)
                {
                    // The main object's version token gates the companion as well. A later
                    // import can still edit the companion explicitly and force a fresh export.
                    companions.Add(new JObject { ["part"] = candidatePart, ["file"] = MakeRelative(root, companionPath), ["status"] = "skipped" });
                    return;
                }

                WriteTextAtomically(companionPath, visual, overwrite || mode != "all");
                long bytes = new FileInfo(companionPath).Length;
                companions.Add(new JObject
                {
                    ["part"] = candidatePart,
                    ["file"] = MakeRelative(root, companionPath),
                    ["bytes"] = bytes,
                    ["status"] = "ok"
                });
                filesWritten++;
                companionFilesWritten++;
                bytesWritten += bytes;
                return;
            }
        }

    }
}
