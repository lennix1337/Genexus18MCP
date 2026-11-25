using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class McpFileUploadsContractTests
    {
        private const string Files = "io.modelcontextprotocol/files";

        [Fact]
        public void Discover_AdvertisesOnlyInlineToOptedInModernClients()
        {
            var discovery = Route(Request("server/discover", new JObject()));
            var files = Assert.IsType<JObject>(discovery["capabilities"]?["extensions"]?[Files]);
            Assert.Equal(1_048_576, files["maxInlineBytes"]?.Value<int>());
            Assert.Null(files["upload"]);
            Assert.Null(files["download"]);
            Assert.Equal("private", discovery["cacheScope"]?.Value<string>());

            Assert.Null(Route(Request("server/discover"))["capabilities"]?["extensions"]?[Files]);
            Assert.Null(Route(Request("server/discover", new JObject(), "2025-11-25"))["capabilities"]?["extensions"]?[Files]);
            Assert.Null(Route(Request("server/discover", new JObject { ["revision"] = "unknown" }))["capabilities"]?["extensions"]?[Files]);
        }

        [Fact]
        public void ToolsList_AddsFileInputAlongsidePath_WithoutChangingBaselineResponse()
        {
            var baseline = Route(Request("tools/list"));
            string wire = baseline.ToString(Formatting.None);
            var optedIn = Route(Request("tools/list", new JObject { ["futureSetting"] = true }));
            var alternatives = Assert.IsType<JArray>(TransferFileSchema(optedIn)["anyOf"]);
            Assert.True(JToken.DeepEquals(TransferFileSchema(baseline), alternatives[0]));
            var upload = alternatives[1]!;
            Assert.Equal("object", upload["type"]?.Value<string>());
            Assert.Equal(new[] { ".xpz" }, upload["x-mcp-file"]?["accept"]?.Values<string>());
            Assert.Equal(1_048_576, upload["x-mcp-file"]?["maxSize"]?.Value<int>());
            Assert.Equal(new[] { "id", "mimeType", "size" }, upload["required"]?.Values<string>());
            Assert.Equal("private", optedIn["cacheScope"]?.Value<string>());
            Assert.Equal(wire, Route(Request("tools/list")).ToString(Formatting.None));
            Assert.Equal(wire, Route(Request("tools/list", new JObject { ["revision"] = "unknown" })).ToString(Formatting.None));
        }

        [Theory]
        [InlineData("input.xpz", 2, "YQ==")]
        [InlineData("input.xpz", 4, "AAAA!!!!")]
        [InlineData("input.xpz", 4, "bm90IQ==")]
        [InlineData("input.xml", 4, "UEsDBA==")]
        [InlineData("input.xpz", 1, null)]
        [InlineData("input.xpz", 1_048_577, "YQ==")]
        [InlineData("input.txt", 1, "YQ==")]
        public async Task ToolsCall_RejectsInvalidInlineFileBeforeDispatch(string name, int size, string? data)
        {
            var file = new JObject { ["id"] = "f1", ["name"] = name, ["mimeType"] = "application/octet-stream", ["size"] = size };
            if (data != null) file["data"] = data;
            var request = Request("tools/call");
            var parameters = (JObject)request["params"]!;
            parameters["name"] = "genexus_transfer";
            parameters["arguments"] = new JObject { ["action"] = "import", ["file"] = file };

            var response = await Program.ProcessMcpRequest(request, sessionContextEnabled: false);
            Assert.Null(response?["error"]);
            Assert.True(response?["result"]?["isError"]?.Value<bool>());
            Assert.Contains("Invalid uploaded file", response?["result"]?["content"]?[0]?["text"]?.Value<string>());
            Assert.DoesNotContain("YQ==", response!.ToString(Formatting.None));
            if (data != null) Assert.DoesNotContain(data, response.ToString(Formatting.None));
        }

        [Fact]
        public void ValidFile_IsStagedVerbatim_WithServerOwnedPath_AndRemovedOnDispose()
        {
            using var stream = new MemoryStream();
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            using (var writer = new StreamWriter(archive.CreateEntry("object.xml").Open()))
                writer.Write("<ExportFile />");
            var bytes = stream.ToArray();
            string digest = Convert.ToBase64String(SHA256.HashData(bytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var file = new JObject
            {
                ["id"] = "f1", ["name"] = "../../client.xpz", ["mimeType"] = "application/octet-stream",
                ["size"] = bytes.Length, ["data"] = Convert.ToBase64String(bytes),
                ["digest"] = new JObject { ["algorithm"] = "sha-256", ["value"] = digest }
            };
            var request = Request("tools/call"); // The draft allows inline calls without a capability declaration.
            var parameters = (JObject)request["params"]!;
            parameters["name"] = "genexus_transfer";
            parameters["arguments"] = new JObject { ["action"] = "import", ["file"] = file };
            var args = (JObject?)parameters["arguments"];
            var upload = McpFileUploadsProtocol.ParseToolArguments(request, "genexus_transfer", ref args);
            Assert.False(upload.Invalid);
            Assert.Null(args?["file"]);
            var dispatch = upload.ForDispatch(parameters, args);
            Assert.Null(dispatch["arguments"]?["file"]);
            Assert.Equal(file["data"], parameters["arguments"]?["file"]?["data"]);

            string stagedPath;
            var inline = Assert.IsType<McpFileUploadsProtocol.InlineFile>(dispatch.Annotation<McpFileUploadsProtocol.InlineFile>());
            using (var stage = Assert.IsType<McpFileUploadsProtocol.StagedFile>(inline.TryStage()))
            {
                stagedPath = stage.Path;
                Assert.Equal("upload.xpz", Path.GetFileName(stagedPath));
                Assert.Equal(bytes, File.ReadAllBytes(stagedPath));
            }
            Assert.False(File.Exists(stagedPath));
            Assert.False(Directory.Exists(Path.GetDirectoryName(stagedPath)));

            ((JObject)file["digest"]!)["value"] = "tampered";
            Assert.Null(McpFileUploadsProtocol.ParseInlineFile(file));
        }

        private static JObject Request(string method, JObject? settings = null, string version = "2026-07-28")
        {
            var meta = new JObject { ["io.modelcontextprotocol/protocolVersion"] = version };
            if (settings != null)
            {
                meta["io.modelcontextprotocol/clientCapabilities"] = new JObject
                {
                    ["extensions"] = new JObject { [Files] = settings }
                };
            }
            return new JObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = method, ["params"] = new JObject { ["_meta"] = meta } };
        }

        private static JObject Route(JObject request) => Assert.IsType<JObject>(McpRouter.Handle(request));

        private static JToken TransferFileSchema(JObject listing) => listing["tools"]!
            .Single(tool => tool["name"]?.Value<string>() == "genexus_transfer")["inputSchema"]!["properties"]!["file"]!;
    }
}
