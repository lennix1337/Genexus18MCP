using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// The genexus_browser preview screenshot reaches the model as an MCP image block only when asked for
    /// (inlineImage=true), within a byte cap, and without copying the bytes into structuredContent. No browser:
    /// the "screenshot" is a PNG-signature file in a temp directory.
    /// </summary>
    public sealed class BrowserScreenshotImageTests : IDisposable
    {
        private static readonly byte[] PngHeader = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        private readonly string _dir = Directory.CreateTempSubdirectory("gx-shot-").FullName;

        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        private string WriteFile(string name, int totalBytes, bool png = true)
        {
            var bytes = new byte[totalBytes];
            new Random(7).NextBytes(bytes);
            if (png) Array.Copy(PngHeader, bytes, PngHeader.Length);
            string path = Path.Combine(_dir, name);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        private static JObject Payload(string path, string? error = null)
        {
            var captures = new JObject { ["screenshot"] = path };
            if (error != null) captures["screenshotError"] = error;
            return new JObject { ["status"] = "ok", ["captures"] = captures };
        }

        private static JObject Args(bool? inlineImage, string action = "preview")
        {
            var args = new JObject { ["action"] = action, ["capture"] = new JArray("screenshot") };
            if (inlineImage != null) args["inlineImage"] = inlineImage;
            return args;
        }

        private static JObject Build(JObject payload, JObject args)
        {
            var result = Program.BuildToolResultContent(payload, false, "genexus_browser", args);
            BrowserScreenshotImage.Attach(result, payload, "genexus_browser", args, false);
            return result;
        }

        [Theory]
        [InlineData(null)]
        [InlineData(false)]
        public void DefaultOff_ResultIsTextOnly(bool? inlineImage)
        {
            var result = Build(Payload(WriteFile("a.png", 2048)), Args(inlineImage));

            var content = (JArray)result["content"]!;
            Assert.Single(content);
            Assert.Equal("text", content[0]["type"]!.ToString());
        }

        [Fact]
        public void OptIn_AppendsImageBlockAfterText()
        {
            string path = WriteFile("a.png", 4096);
            var result = Build(Payload(path), Args(true));

            var content = (JArray)result["content"]!;
            Assert.Equal(2, content.Count);
            Assert.Equal("text", content[0]["type"]!.ToString());
            Assert.Equal("image", content[1]["type"]!.ToString());
            Assert.Equal("image/png", content[1]["mimeType"]!.ToString());
            Assert.Equal(File.ReadAllBytes(path), Convert.FromBase64String(content[1]["data"]!.ToString()));
        }

        [Fact]
        public void OptIn_BytesAppearOnce_NotInTextOrStructuredContent()
        {
            // structuredContent is off by default, so it is switched on here: the point of the test
            // is that the image bytes are not duplicated into it when it IS emitted.
            const string emitVariable = "GXMCP_EMIT_STRUCTURED_CONTENT";
            string? previous = Environment.GetEnvironmentVariable(emitVariable);
            Environment.SetEnvironmentVariable(emitVariable, "1");
            Program.InvalidateEnvProbeCache();
            try
            {
                var result = Build(Payload(WriteFile("a.png", 4096)), Args(true));

                Assert.NotNull(result["structuredContent"]);
                string data = result["content"]![1]!["data"]!.ToString();
                Assert.DoesNotContain(data, result["structuredContent"]!.ToString());
                Assert.DoesNotContain(data, result["content"]![0]!["text"]!.ToString());
                Assert.Equal(1, result.ToString().Split(data).Length - 1);
            }
            finally
            {
                Environment.SetEnvironmentVariable(emitVariable, previous);
                Program.InvalidateEnvProbeCache();
            }
        }

        [Fact]
        public void OverCap_RefusedWithHint_AndNoImage()
        {
            string path = WriteFile("big.png", BrowserScreenshotImage.MaxImageBytes + 1);
            var result = Build(Payload(path), Args(true));

            var content = (JArray)result["content"]!;
            Assert.Equal(2, content.Count);
            Assert.Equal("text", content[1]["type"]!.ToString());
            Assert.Contains("cap", content[1]["text"]!.ToString());
            Assert.Contains("emulate", content[1]["text"]!.ToString());
        }

        [Fact]
        public void AtCap_IsAttached()
        {
            var result = Build(Payload(WriteFile("edge.png", BrowserScreenshotImage.MaxImageBytes)), Args(true));
            Assert.Equal("image", result["content"]![1]!["type"]!.ToString());
        }

        [Fact]
        public void NotAPng_IsRefused()
        {
            var result = Build(Payload(WriteFile("x.png", 2048, png: false)), Args(true));
            Assert.Equal("text", result["content"]![1]!["type"]!.ToString());
            Assert.Contains("not a PNG", result["content"]![1]!["text"]!.ToString());
        }

        [Fact]
        public void CaptureFailure_NoImage()
        {
            var result = Build(Payload(WriteFile("a.png", 2048), error: "boom"), Args(true));
            Assert.Equal("text", result["content"]![1]!["type"]!.ToString());
            Assert.DoesNotContain(result["content"]!.OfType<JObject>(), c => c["type"]!.ToString() == "image");
        }

        [Fact]
        public void MissingFile_NoImage()
        {
            var result = Build(Payload(Path.Combine(_dir, "gone.png")), Args(true));
            Assert.DoesNotContain(result["content"]!.OfType<JObject>(), c => c["type"]!.ToString() == "image");
        }

        [Fact]
        public void NoScreenshotInResult_ExplainsInsteadOfFailing()
        {
            var payload = new JObject { ["status"] = "ok", ["captures"] = new JObject { ["html"] = "<p/>" } };
            var result = Build(payload, Args(true));
            Assert.Contains("capture=", result["content"]![1]!["text"]!.ToString());
        }

        [Fact]
        public void OtherActionsAndErrors_AreUntouched()
        {
            string path = WriteFile("a.png", 2048);
            var smoke = Program.BuildToolResultContent(Payload(path), false, "genexus_browser", Args(true, "smoke"));
            BrowserScreenshotImage.Attach(smoke, Payload(path), "genexus_browser", Args(true, "smoke"), false);
            Assert.Single((JArray)smoke["content"]!);

            var error = Program.BuildToolResultContent(Payload(path), true, "genexus_browser", Args(true));
            BrowserScreenshotImage.Attach(error, Payload(path), "genexus_browser", Args(true), true);
            Assert.Single((JArray)error["content"]!);
        }
    }
}
