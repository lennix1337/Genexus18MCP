using System;
using System.IO;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    /// <summary>
    /// Opt-in (<c>inlineImage=true</c>) delivery of the <c>genexus_browser</c> preview screenshot as an MCP
    /// <c>image</c> content block. The Worker saves the PNG and returns its path in <c>captures.screenshot</c>;
    /// this reads that file, so the model sees the screen instead of a path. The block is appended after the
    /// text block and is never copied into <c>structuredContent</c>, so the bytes travel once.
    /// </summary>
    internal static class BrowserScreenshotImage
    {
        /// <summary>
        /// Hard cap on the PNG file. Every image costs the client tokens and base64 adds a third on the wire;
        /// a typical 1280-wide viewport capture is well under this. Over the cap the image is refused, not shrunk
        /// (there is no image library in the gateway to resize with).
        /// </summary>
        internal const int MaxImageBytes = 300 * 1024;

        private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        internal static void Attach(JObject toolResult, JToken? payload, string? toolName, JObject? args, bool isError)
        {
            if (isError || payload == null
                || !string.Equals(toolName, "genexus_browser", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(args?["action"]?.ToString(), "preview", StringComparison.OrdinalIgnoreCase)
                || args?["inlineImage"]?.Type != JTokenType.Boolean || !args["inlineImage"]!.Value<bool>()
                || toolResult["content"] is not JArray content)
                return;

            string? note = TryReadImage(payload, out JObject? image);
            content.Add(image ?? new JObject { ["type"] = "text", ["text"] = note });
        }

        // Returns null and sets image on success; otherwise returns the reason the image is not attached.
        private static string? TryReadImage(JToken payload, out JObject? image)
        {
            image = null;
            string? path = (payload.SelectToken("$..captures.screenshot") as JValue)?.Value as string;
            if (string.IsNullOrWhiteSpace(path))
                return "inlineImage: no screenshot in this result. Pass capture=[\"screenshot\"] with action=preview.";
            if (payload.SelectToken("$..captures.screenshotError") != null)
                return "inlineImage: the screenshot capture failed; see captures.screenshotError.";
            if (!string.Equals(Path.GetExtension(path), ".png", StringComparison.OrdinalIgnoreCase))
                return "inlineImage: only PNG screenshots are attached.";

            byte[] bytes;
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (stream.Length > MaxImageBytes)
                    return $"inlineImage: screenshot is {stream.Length / 1024} KB, over the {MaxImageBytes / 1024} KB cap, so it is not attached. "
                        + "Capture a smaller viewport (emulate=desktop1280 or a mobile profile); the file stays at captures.screenshot.";
                bytes = new byte[stream.Length];
                int read = 0;
                while (read < bytes.Length)
                {
                    int n = stream.Read(bytes, read, bytes.Length - read);
                    if (n == 0) break;
                    read += n;
                }
                if (read != bytes.Length) return "inlineImage: the screenshot file changed while reading.";
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return "inlineImage: the screenshot file could not be read (" + ex.GetType().Name + ").";
            }

            if (bytes.Length < PngSignature.Length || !bytes.AsSpan(0, PngSignature.Length).SequenceEqual(PngSignature))
                return "inlineImage: the screenshot file is not a PNG.";

            image = new JObject
            {
                ["type"] = "image",
                ["data"] = Convert.ToBase64String(bytes),
                ["mimeType"] = "image/png"
            };
            return null;
        }
    }
}
