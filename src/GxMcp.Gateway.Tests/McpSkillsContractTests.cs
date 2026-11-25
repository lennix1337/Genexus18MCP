using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class McpSkillsContractTests
    {
        [Fact]
        public void Discover_AdvertisesSkillsWithoutDirectoryRead()
        {
            var result = Result(ModernRequest("server/discover"));
            var extension = Assert.IsType<JObject>(result["capabilities"]?["extensions"]?["io.modelcontextprotocol/skills"]);
            Assert.Empty(extension);
            Assert.IsType<JObject>(result["capabilities"]?["resources"]);
        }

        [Fact]
        public void ListAndGet_PublishTheCatalogAndVerifiableSkillFiles()
        {
            var listing = Result(ModernRequest("skills/list"));
            AssertCacheable(listing);
            Assert.Null(listing["nextCursor"]);
            var skills = Assert.IsType<JArray>(listing["skills"]);
            Assert.Equal(SkillCatalog.All.Count, skills.Count);

            foreach (var entry in SkillCatalog.All)
            {
                string uri = $"skill://{entry.Key}/SKILL.md";
                var advertised = Assert.Single(skills, skill => skill["uri"]?.Value<string>() == uri);
                var frontmatter = Assert.IsType<JObject>(advertised["frontmatter"]);
                Assert.Equal(entry.Key, frontmatter["name"]?.Value<string>());
                Assert.False(string.IsNullOrWhiteSpace(frontmatter["description"]?.Value<string>()));
                var get = Result(ModernRequest("skills/get", uri));
                AssertCacheable(get);
                Assert.True(JToken.DeepEquals(advertised, get["skill"]));

                var manifest = Assert.IsType<JArray>(advertised["resources"]);
                Assert.Equal(entry.Key == "nexa" ? NexaSkillPack.ReferenceCount + 1 : 1, manifest.Count);
                Assert.Equal(manifest.Count, manifest.Select(file => file["uri"]?.Value<string>()).Distinct().Count());
                Assert.Contains(manifest, file => file["uri"]?.Value<string>() == uri);

                foreach (var file in manifest)
                {
                    string fileUri = Assert.IsType<JValue>(file["uri"]).Value<string>()!;
                    Assert.StartsWith($"skill://{entry.Key}/", fileUri);
                    var read = Result(ModernRequest("resources/read", fileUri));
                    var content = Assert.Single(Assert.IsType<JArray>(read["contents"]));
                    Assert.Equal(fileUri, content["uri"]?.Value<string>());
                    Assert.Equal("text/markdown", content["mimeType"]?.Value<string>());
                    string text = Assert.IsType<JValue>(content["text"]).Value<string>()!;
                    byte[] bytes = Encoding.UTF8.GetBytes(text);
                    Assert.Equal(bytes.Length, file["size"]?.Value<int>());
                    Assert.Equal("sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes)), file["digest"]?.Value<string>());

                    if (fileUri == uri)
                    {
                        var lines = text.Replace("\r\n", "\n").Split('\n');
                        Assert.Equal("---", lines[0]);
                        Assert.Equal($"name: {entry.Key}", lines[1]);
                        Assert.Equal("---", lines[3]);
                        Assert.StartsWith("description: ", lines[2]);
                        string description = lines[2]["description: ".Length..];
                        if (entry.Key == "nexa")
                        {
                            Assert.Equal(entry.Body, text);
                        }
                        else
                        {
                            description = JToken.Parse(description).Value<string>()!;
                            Assert.Equal(entry.Description, description);
                            Assert.EndsWith(entry.Body, text);
                        }
                        Assert.Equal(description, frontmatter["description"]?.Value<string>());
                    }
                    else
                    {
                        var reference = fileUri[$"skill://{entry.Key}/".Length..];
                        Assert.StartsWith("references/", reference);
                        Assert.True(NexaSkillPack.TryRead(reference, out var embedded));
                        Assert.Equal(embedded, text);
                    }
                }
            }
        }

        [Theory]
        [InlineData("skills/get", "uri", null)]
        [InlineData("skills/get", "uri", "skill://nexa/references/common-events.md")]
        [InlineData("skills/get", "uri", "skill://missing/SKILL.md")]
        [InlineData("resources/read", "uri", "skill://nexa/references/../SKILL.md")]
        [InlineData("skills/list", "cursor", "invalid")]
        public void InvalidSkillRequest_ReturnsInvalidParams(string method, string field, string? value)
        {
            var request = ModernRequest(method);
            if (value != null) request["params"]![field] = value;
            var error = Assert.IsType<McpRouterError>(McpRouter.Handle(request));
            Assert.Equal(-32602, error.Code);
        }

        [Fact]
        public void LegacyInitializeAndResources_AreUnchanged()
        {
            var initialized = Result(JObject.Parse("""
                {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{}}}
                """));
            Assert.Equal("2025-11-25", initialized["protocolVersion"]?.Value<string>());
            Assert.Null(initialized["capabilities"]?["extensions"]);

            var listing = Result(JObject.Parse("""{"jsonrpc":"2.0","id":2,"method":"resources/list"}"""));
            const string uri = "genexus://kb/skills/nexa";
            var resources = Assert.IsType<JArray>(listing["resources"]);
            Assert.Contains(resources, resource => resource["uri"]?.Value<string>() == uri);
            Assert.DoesNotContain(resources, resource => resource["uri"]!.Value<string>()!.StartsWith("skill://", StringComparison.Ordinal));
            var read = Result(JObject.Parse("""{"jsonrpc":"2.0","id":3,"method":"resources/read","params":{"uri":"genexus://kb/skills/nexa"}}"""));
            Assert.Equal(NexaSkillPack.ReadRoot(), Assert.Single(Assert.IsType<JArray>(read["contents"]))["text"]?.Value<string>());
            Assert.Null(McpRouter.Handle(JObject.Parse("""{"jsonrpc":"2.0","id":4,"method":"skills/list"}""")));
            Assert.Null(McpRouter.Handle(JObject.Parse("""{"jsonrpc":"2.0","id":5,"method":"resources/read","params":{"uri":"skill://nexa/SKILL.md"}}""")));
        }

        private static JObject ModernRequest(string method, string? uri = null)
        {
            var request = JObject.Parse("""
                {"jsonrpc":"2.0","id":1,"params":{"_meta":{
                  "io.modelcontextprotocol/protocolVersion":"2026-07-28",
                  "io.modelcontextprotocol/clientCapabilities":{}
                }}}
                """);
            request["method"] = method;
            if (uri != null) request["params"]!["uri"] = uri;
            return request;
        }

        private static JObject Result(JObject request)
        {
            var result = McpRouter.Handle(request);
            Assert.True(result != null, $"No result for {request["method"]}.");
            return JObject.FromObject(result!);
        }

        private static void AssertCacheable(JObject result)
        {
            Assert.Equal("complete", result["resultType"]?.Value<string>());
            Assert.Equal("public", result["cacheScope"]?.Value<string>());
            Assert.True(result["ttlMs"]?.Value<int>() > 0);
        }
    }
}
