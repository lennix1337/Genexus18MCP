using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using GxMcp.Worker.Utils;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    public class AssetService
    {
        private readonly BuildService _buildService;

        public AssetService(BuildService buildService)
        {
            _buildService = buildService;
        }

        public string Find(string pattern, string relativeRoot = null, int limit = 20)
        {
            try
            {
                string kbRoot = RequireKbRoot();
                string rootPath = ResolvePath(kbRoot, relativeRoot, false);
                string normalizedPattern = string.IsNullOrWhiteSpace(pattern) ? "*.*" : pattern.Trim();
                int cappedLimit = Math.Max(1, Math.Min(limit, 200));

                if (!Directory.Exists(rootPath))
                {
                    return Models.McpResponse.Err(
                        code: "AssetRootNotFound",
                        message: "Asset root not found.",
                        hint: "The requested relativeRoot does not exist inside the active Knowledge Base.",
                        nextSteps: new JArray(
                            Models.McpResponse.NextStep(
                                tool: "genexus_asset",
                                args: new JObject { ["action"] = "find" },
                                why: "Retry without relativeRoot to search the entire KB root.")),
                        target: relativeRoot);
                }

                var results = Directory.EnumerateFiles(rootPath, normalizedPattern, SearchOption.AllDirectories)
                    .Take(cappedLimit)
                    .Select(path => DescribeForSearch(kbRoot, path))
                    .ToArray();

                return Models.McpResponse.Ok(
                    target: relativeRoot,
                    code: "AssetSearchOk",
                    result: new JObject
                    {
                        ["pattern"] = normalizedPattern,
                        ["relativeRoot"] = GetRelativePath(kbRoot, rootPath),
                        ["count"] = results.Length,
                        ["results"] = new JArray(results)
                    });
            }
            catch (Exception ex)
            {
                return Models.McpResponse.Err(
                    code: "AssetSearchFailed",
                    message: ex.Message,
                    hint: "Verify the pattern and relativeRoot are valid paths inside the active KB.",
                    nextSteps: new JArray(
                        Models.McpResponse.NextStep(
                            tool: "genexus_asset",
                            args: new JObject { ["action"] = "find" },
                            why: "Retry without relativeRoot to confirm the KB root is accessible.")),
                    target: pattern);
            }
        }

        public string Read(string path, bool includeContent = false, int? maxBytes = null)
        {
            try
            {
                string kbRoot = RequireKbRoot();
                string fullPath = ResolvePath(kbRoot, path, true);

                if (!File.Exists(fullPath))
                {
                    return Models.McpResponse.Err(
                        code: "AssetNotFound",
                        message: "Asset not found.",
                        hint: "The requested asset path does not exist inside the active Knowledge Base.",
                        nextSteps: new JArray(
                            Models.McpResponse.NextStep(
                                tool: "genexus_asset",
                                args: new JObject { ["action"] = "find", ["pattern"] = "*.*" },
                                why: "Lists available assets so you can pick the correct path.")),
                        target: path);
                }

                long fileSize = new FileInfo(fullPath).Length;
                int effectiveMaxBytes = Math.Max(1024, Math.Min(maxBytes ?? 131072, 524288));
                if (includeContent && fileSize > effectiveMaxBytes)
                {
                    return Models.McpResponse.Err(
                        code: "AssetExceedsReadLimit",
                        message: string.Format("The asset is {0} bytes and exceeds maxBytes={1}.", fileSize, effectiveMaxBytes),
                        hint: "Read metadata only (includeContent=false) or request a smaller file.",
                        // no-nextStep: the caller already knows the path and must decide whether to increase maxBytes or skip content
                        target: path);
                }

                var asset = BuildAssetDescriptor(kbRoot, fullPath, includeContent);
                asset["includeContent"] = includeContent;
                if (includeContent)
                {
                    asset["maxBytes"] = effectiveMaxBytes;
                }

                return Models.McpResponse.Ok(target: path, code: "AssetRead", result: asset);
            }
            catch (Exception ex)
            {
                return Models.McpResponse.Err(
                    code: "AssetReadFailed",
                    message: ex.Message,
                    hint: "Verify the path is valid and the file is accessible.",
                    nextSteps: new JArray(
                        Models.McpResponse.NextStep(
                            tool: "genexus_asset",
                            args: new JObject { ["action"] = "find", ["pattern"] = "*.*" },
                            why: "Lists available assets to confirm the correct path.")),
                    target: path);
            }
        }

        public string Write(string path, string contentBase64)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(contentBase64))
                {
                    return Models.McpResponse.Err(
                        code: "AssetContentRequired",
                        message: "Asset content is required.",
                        hint: "Provide contentBase64 for write operations.",
                        // no-nextStep: the caller must supply content; no tool can infer it
                        target: path);
                }

                string kbRoot = RequireKbRoot();
                string fullPath = ResolvePath(kbRoot, path, true);
                byte[] content = Convert.FromBase64String(contentBase64);

                string existingHash = File.Exists(fullPath) ? ComputeSha256(File.ReadAllBytes(fullPath)) : string.Empty;

                string directory = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllBytes(fullPath, content);

                byte[] persistedBytes = File.ReadAllBytes(fullPath);
                string persistedHash = ComputeSha256(persistedBytes);
                string expectedHash = ComputeSha256(content);
                if (!string.Equals(persistedHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                {
                    return Models.McpResponse.Err(
                        code: "AssetWriteVerificationFailed",
                        message: "Asset write verification failed.",
                        hint: "The asset was written but the persisted hash does not match the provided content. Check disk space and file permissions.",
                        nextSteps: new JArray(
                            Models.McpResponse.NextStep(
                                tool: "genexus_asset",
                                args: new JObject { ["action"] = "read", ["path"] = path },
                                why: "Read the persisted file to diagnose the hash mismatch.")),
                        target: path);
                }

                var resultObj = BuildAssetDescriptor(kbRoot, fullPath, includeContent: false);
                resultObj["previousSha256"] = existingHash;
                resultObj["sha256"] = persistedHash;
                resultObj["bytesWritten"] = persistedBytes.Length;

                return Models.McpResponse.Ok(target: path, code: "AssetWritten", result: resultObj);
            }
            catch (FormatException)
            {
                return Models.McpResponse.Err(
                    code: "AssetInvalidBase64",
                    message: "Invalid base64 content.",
                    hint: "contentBase64 is not valid Base64. Re-encode the file content before sending.",
                    // no-nextStep: the caller must fix their encoding; no tool can do it on their behalf
                    target: path);
            }
            catch (Exception ex)
            {
                return Models.McpResponse.Err(
                    code: "AssetWriteFailed",
                    message: ex.Message,
                    hint: "Verify the path is within the KB and the process has write permissions.",
                    nextSteps: new JArray(
                        Models.McpResponse.NextStep(
                            tool: "genexus_asset",
                            args: new JObject { ["action"] = "find", ["pattern"] = "*.*" },
                            why: "Confirms the KB root is accessible before retrying the write.")),
                    target: path);
            }
        }

        private string RequireKbRoot()
        {
            string kbRoot = _buildService.GetKBPath();
            if (string.IsNullOrWhiteSpace(kbRoot))
            {
                throw new InvalidOperationException("KB Path not found in Environment (GX_KB_PATH).");
            }

            string fullRoot = Path.GetFullPath(kbRoot);
            if (!Directory.Exists(fullRoot))
            {
                throw new DirectoryNotFoundException("The configured KB path does not exist.");
            }

            return fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static string ResolvePath(string kbRoot, string path, bool requireFilePath)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                if (requireFilePath)
                {
                    throw new ArgumentException("Path is required.");
                }

                return kbRoot;
            }

            if (!PathSafety.TryResolveWithinRoot(kbRoot, path, out string candidate))
            {
                throw new InvalidOperationException("The requested asset path points outside the active Knowledge Base.");
            }

            return candidate;
        }

        /// <summary>
        /// Metadata for one search hit. A KB folder holds files another process keeps open
        /// exclusively (the KB's own SQL Server .mdf/.ldf), so a file that cannot be read is
        /// listed with readable=false and no hash instead of failing the whole search.
        /// </summary>
        internal static JObject DescribeForSearch(string kbRoot, string fullPath)
        {
            var info = new FileInfo(fullPath);
            string sha256 = null;
            try
            {
                using (var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var sha = SHA256.Create())
                {
                    sha256 = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            return new JObject
            {
                ["path"] = fullPath,
                ["relativePath"] = GetRelativePath(kbRoot, fullPath),
                ["fileName"] = info.Name,
                ["size"] = info.Length,
                ["mimeType"] = GuessMimeType(fullPath),
                ["sha256"] = sha256 != null ? new JValue(sha256) : JValue.CreateNull(),
                ["readable"] = sha256 != null
            };
        }

        private static JObject BuildAssetDescriptor(string kbRoot, string fullPath, bool includeContent)
        {
            byte[] bytes = File.ReadAllBytes(fullPath);
            var descriptor = new JObject
            {
                ["path"] = fullPath,
                ["relativePath"] = GetRelativePath(kbRoot, fullPath),
                ["fileName"] = Path.GetFileName(fullPath),
                ["size"] = bytes.LongLength,
                ["mimeType"] = GuessMimeType(fullPath),
                ["sha256"] = ComputeSha256(bytes)
            };

            if (includeContent)
            {
                descriptor["contentBase64"] = Convert.ToBase64String(bytes);
            }

            return descriptor;
        }

        private static string GetRelativePath(string kbRoot, string fullPath)
        {
            return PathSafety.MakeRelative(kbRoot, fullPath);
        }

        private static string ComputeSha256(byte[] bytes)
        {
            using (var sha = SHA256.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
            }
        }

        private static string GuessMimeType(string path)
        {
            string extension = Path.GetExtension(path)?.ToLowerInvariant() ?? string.Empty;
            switch (extension)
            {
                case ".xlsx":
                    return "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
                case ".xls":
                    return "application/vnd.ms-excel";
                case ".csv":
                    return "text/csv";
                case ".json":
                    return "application/json";
                case ".xml":
                    return "application/xml";
                case ".txt":
                case ".log":
                    return "text/plain";
                default:
                    return "application/octet-stream";
            }
        }

    }
}
