using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    // Inline subset of the experimental MCP File Uploads draft (8a859b8f).
    // https://github.com/modelcontextprotocol/experimental-ext-file-uploads/blob/8a859b8f268926fe4beb84d78565e7cc6f9fa617/specification/draft/file-uploads.md
    internal static class McpFileUploadsProtocol
    {
        internal const string Capability = "io.modelcontextprotocol/files";
        internal const int MaxInlineBytes = 1_048_576;
        private const string TransferTool = "genexus_transfer";

        private static JToken? ClientSettings(JObject request)
            => request.SelectToken($"params._meta['io.modelcontextprotocol/clientCapabilities'].extensions['{Capability}']");

        internal static bool DeclaresSupport(JObject request)
            => ClientSettings(request) is JObject settings && settings["revision"] == null;

        internal static JObject ProjectToolsList(JObject request, JObject baseline)
        {
            if (!McpRouter.IsModernRequest(request) || !DeclaresSupport(request)) return baseline;

            var listing = (JObject)baseline.DeepClone();
            var transfer = ((JArray)listing["tools"]!).FirstOrDefault(tool => tool["name"]?.Value<string>() == TransferTool);
            if (transfer?["inputSchema"]?["properties"] is not JObject properties || properties["file"] is not JObject path)
                return baseline;

            properties["file"] = new JObject { ["anyOf"] = new JArray(path.DeepClone(), FileInputSchema()) };
            listing["ttlMs"] = 0;
            listing["cacheScope"] = "private";
            return listing;
        }

        private static JObject FileInputSchema() => new JObject
        {
            ["type"] = "object",
            ["description"] = "Inline GeneXus XPZ file for action=import (experimental MCP file uploads).",
            ["x-mcp-file"] = new JObject { ["accept"] = new JArray(".xpz"), ["maxSize"] = MaxInlineBytes },
            ["properties"] = new JObject
            {
                ["id"] = new JObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 128 },
                ["name"] = new JObject { ["type"] = "string" },
                ["mimeType"] = new JObject { ["type"] = "string" },
                ["size"] = new JObject { ["type"] = "integer", ["minimum"] = 0 },
                ["digest"] = new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject
                    {
                        ["algorithm"] = new JObject { ["const"] = "sha-256" },
                        ["value"] = new JObject { ["type"] = "string" }
                    },
                    ["required"] = new JArray("algorithm", "value")
                },
                ["data"] = new JObject { ["type"] = "string" }
            },
            ["required"] = new JArray("id", "mimeType", "size")
        };

        internal static ToolArguments ParseToolArguments(JObject request, string toolName, ref JObject? args)
        {
            if (toolName != TransferTool || !McpRouter.IsModernRequest(request)
                || args?["action"]?.Type != JTokenType.String || args["action"]!.Value<string>() != "import"
                || args["file"] is not JObject file)
                return default;

            // Keep the original request for idempotency hashing and confirmation replay.
            // Do not pass file bytes to the generic validator, tracker, or Worker logs.
            args = new JObject(args.Properties().Where(property => property.Name != "file"));
            var upload = ClientSettings(request) is JObject settings && settings["revision"] != null
                ? null : ParseInlineFile(file);
            return new ToolArguments(upload, upload == null);
        }

        internal readonly record struct ToolArguments(InlineFile? File, bool Invalid)
        {
            internal JObject ForDispatch(JObject parameters, JObject? sanitizedArguments)
            {
                if (File == null) return parameters;
                var sanitized = new JObject(parameters.Properties().Where(property => property.Name != "arguments"));
                sanitized["arguments"] = sanitizedArguments;
                sanitized.AddAnnotation(File);
                return sanitized;
            }
        }

        internal static JObject InvalidFile() => Error("InvalidUploadedFile",
            "Invalid uploaded file. Send a non-empty .xpz file as inline base64, up to 1 MiB, with its matching decoded size.");

        internal static JObject Error(string code, string message)
            => JObject.FromObject(new { status = "error", error = new { code, message } });

        internal static InlineFile? ParseInlineFile(JObject file)
        {
            if (StringValue(file["id"]) is not { Length: > 0 and <= 128 }
                || string.IsNullOrWhiteSpace(StringValue(file["mimeType"]))
                || file["size"]?.Type != JTokenType.Integer
                || !int.TryParse(file["size"]!.ToString(), out int size) || size <= 0 || size > MaxInlineBytes)
                return null;

            // The filename is optional and is never used as a server-side path.
            if (file["name"] != null && StringValue(file["name"])?.EndsWith(".xpz", StringComparison.OrdinalIgnoreCase) != true)
                return null;

            string? data = StringValue(file["data"]);
            if (data == null || data.Length != ((size + 2) / 3) * 4 || data.Any(char.IsWhiteSpace)) return null;

            var bytes = new byte[size];
            if (!Convert.TryFromBase64String(data, bytes, out int written) || written != size
                || !bytes.AsSpan().StartsWith("PK\u0003\u0004"u8) || !MatchesDigest(file["digest"], bytes))
                return null;

            return new InlineFile(bytes);
        }

        private static bool MatchesDigest(JToken? token, byte[] bytes)
        {
            if (token == null) return true;
            if (token is not JObject digest || StringValue(digest["algorithm"]) != "sha-256") return false;
            string expected = Convert.ToBase64String(SHA256.HashData(bytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            return StringValue(digest["value"]) == expected;
        }

        private static string? StringValue(JToken? token) => token?.Type == JTokenType.String ? token.Value<string>() : null;

        internal sealed class InlineFile(byte[] bytes)
        {
            internal StagedFile? TryStage()
            {
                StagedFile? stage = null;
                try
                {
                    stage = new StagedFile(Directory.CreateTempSubdirectory("gxmcp-upload-"));
                    using var stream = new FileStream(stage.Path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    stream.Write(bytes);
                    return stage;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    stage?.Dispose();
                    Program.Log("[Gateway] Could not stage an inline XPZ upload.");
                    return null;
                }
            }
        }

        internal sealed class StagedFile(DirectoryInfo directory) : IDisposable
        {
            internal string Path { get; } = System.IO.Path.Combine(directory.FullName, "upload.xpz");
            private int _owners = 1;

            // The MCP wait can end before the Worker reads the package. Keep a second
            // owner for the actual delivery, not the cancellable client-side waiter.
            internal Action HoldForWorker(WorkerProcess worker, string requestId)
            {
                Interlocked.Increment(ref _owners);
                var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                worker.OnRpcResponse += OnResponse;
                _ = ReleaseAfterResponseAsync();
                return () => completed.TrySetResult();

                void OnResponse(string _, JObject response)
                {
                    if (response?["id"] is JValue id && id.Value<string>() == requestId)
                        completed.TrySetResult();
                }

                async Task ReleaseAfterResponseAsync()
                {
                    await Task.WhenAny(completed.Task, worker.ExitNotifiedTask).ConfigureAwait(false);
                    worker.OnRpcResponse -= OnResponse;
                    Dispose();
                }
            }

            public void Dispose()
            {
                if (Interlocked.Decrement(ref _owners) != 0) return;
                try
                {
                    File.Delete(Path);
                    directory.Delete();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Program.Log("[Gateway] Could not clean up an inline XPZ upload.");
                }
            }
        }
    }
}
