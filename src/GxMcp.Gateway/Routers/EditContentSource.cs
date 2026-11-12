using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway.Routers
{
    /// <summary>
    /// A full-part <c>genexus_edit</c> carries the new text inline, in <c>content</c>. A caller that reaches the tool
    /// through a command line (a script, a one-shot call) cannot do that for a large part: a 290 KB Design System
    /// style source does not fit in the roughly 32 KB a Windows command line takes. <c>contentPath</c> names a UTF-8
    /// text file instead, and its text becomes <c>content</c> before the call is routed, so everything downstream
    /// (preview, version check, write, verification) sees exactly what an inline <c>content</c> would have been.
    /// </summary>
    internal static class EditContentSource
    {
        /// <summary>
        /// 1 MiB. The routed command carries the text twice (<c>payload</c> and <c>content</c>), and with a shared-host
        /// Worker the whole command travels in one frame that the connection refuses above 4 MiB: a limit above this one
        /// would promise what that transport cannot carry. A larger part goes through <c>genexus_io import_part</c>, whose
        /// file the Worker reads itself.
        /// </summary>
        internal const long MaxBytes = 1024L * 1024;

        // Arguments that make the call something other than "replace this one part with this text": a path is the
        // text of ONE part, so it can neither feed several writes nor be a fragment of one.
        private static readonly string[] NotAWholePartWrite = { "changeSet", "targets", "parts", "ops", "patch", "operation", "context" };

        /// <summary>
        /// When <paramref name="args"/> has a <c>contentPath</c>, reads the file and stores its text in
        /// <c>args.content</c>. Does nothing when there is none. Every refusal says that no write was attempted.
        /// </summary>
        internal static void Resolve(JObject? args, long maxBytes = MaxBytes)
        {
            if (args == null) return;
            RejectNestedContentPath(args);

            JToken? pathToken = args["contentPath"];
            if (pathToken == null || pathToken.Type == JTokenType.Null) return;

            string path = pathToken.Type == JTokenType.String ? pathToken.ToString().Trim() : string.Empty;
            if (path.Length == 0)
                throw new UsageException("ContentPathInvalid",
                    "`contentPath` must be the absolute path of a UTF-8 text file. No write was attempted.");

            string? mode = args["mode"]?.ToString();
            string? conflicting = string.Equals(mode, "patch", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(mode, "ops", StringComparison.OrdinalIgnoreCase)
                ? "mode=" + mode
                : NotAWholePartWrite.FirstOrDefault(key => IsPresent(args[key]));
            if (conflicting != null)
                throw new UsageException("ContentPathNotAWholePartWrite",
                    "`contentPath` is the text of a whole part, so it cannot be combined with `" + conflicting + "`. "
                    + "Use mode=full (or no mode) with one name+part; to change part of the text send mode=patch with patch={find,replace}. "
                    + "No write was attempted.");

            JToken? inline = args["content"];
            if (inline != null && inline.Type != JTokenType.Null)
                throw new UsageException("ContentConflict",
                    "Send either `content` or `contentPath`, not both. No write was attempted.");

            args["content"] = ReadUtf8(path, maxBytes);
        }

        /// <summary>
        /// The text of the file exactly as stored: nothing is normalised (line endings and a final newline stay as they
        /// are); only a UTF-8 byte order mark is dropped, since it is not part of the text. An empty file is refused:
        /// clearing a part is something to ask for on purpose, with <c>content=""</c>.
        /// </summary>
        internal static string ReadUtf8(string path, long maxBytes = MaxBytes)
        {
            if (!Path.IsPathFullyQualified(path))
                throw new UsageException("ContentPathNotAbsolute",
                    "`contentPath` must be a fully qualified absolute path (a drive and folder, or a UNC path); a relative one would depend on the Gateway's working directory: "
                    + path + ". No write was attempted.");

            byte[] bytes = ReadBounded(path, maxBytes);
            int skip = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
            if (bytes.Length - skip == 0)
                throw new UsageException("ContentPathEmpty",
                    "`contentPath` is an empty file: " + path + ". To clear the part on purpose send content=\"\". No write was attempted.");
            try { return new UTF8Encoding(false, true).GetString(bytes, skip, bytes.Length - skip); }
            catch (DecoderFallbackException)
            {
                throw new UsageException("ContentPathNotUtf8",
                    "`contentPath` is not valid UTF-8 text: " + path + ". Save the file as UTF-8. No write was attempted.");
            }
        }

        /// <summary>
        /// SHA-256 (hex) of the file's bytes, or null when it cannot be read within the limit - which includes a path that is
        /// not an ordinary absolute file path (a relative path, a device, a pipe: see <see cref="IsDeviceOrPipePath"/>), since
        /// <see cref="Resolve"/> refuses those too. The idempotency check hashes the request BEFORE the router turns the path
        /// into text, so a rewritten file under the same key and the same path would otherwise be taken for the request it
        /// already answered.
        /// </summary>
        internal static string? TryFingerprint(string? path, long maxBytes = MaxBytes)
        {
            string trimmed = path?.Trim() ?? string.Empty;
            if (trimmed.Length == 0) return null;
            try { return Convert.ToHexString(SHA256.HashData(ReadBounded(trimmed, maxBytes))); }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// The fingerprint a call adds to its idempotency hash: the file's, for a call whose text will come from a file (a
        /// <c>contentPath</c> and no inline <c>content</c>; an explicit <c>content: null</c> counts as none, as it does in
        /// <see cref="Resolve"/>), and null for any other call.
        /// </summary>
        internal static string? TryFingerprintOfPathArgument(JObject? args, long maxBytes = MaxBytes)
        {
            if (args == null || IsPresent(args["content"])) return null;
            JToken? pathToken = args["contentPath"];
            if (pathToken == null || pathToken.Type != JTokenType.String) return null;
            return TryFingerprint(pathToken.ToString(), maxBytes);
        }

        /// <summary>
        /// True unless the path has one of the ordinary shapes of a file path: a drive path (<c>C:\dir\file.txt</c>), a UNC path
        /// (<c>\\server\share\file.txt</c>), or either of them under the extended-length prefix (<c>\\?\C:\dir\file.txt</c>,
        /// <c>\\?\UNC\server\share\file.txt</c>). What else counts as fully qualified is a way into the device namespace -
        /// <c>\\.\pipe\name</c>, <c>\\.\COM1</c>, <c>\\?\pipe\name</c>, <c>\\?\GLOBALROOT\...</c>, the NT spelling
        /// <c>\??\pipe\name</c> - and so is the UNC form of a pipe or a mailslot (<c>\\server\pipe\name</c>). Opening one
        /// connects to whatever listens there, which could be a Worker's own pipe, before any later check could refuse it. A
        /// real file under a shape this refuses (<c>\\?\Volume{...}\file</c>, <c>\??\C:\file</c>) is refused too: the safe side.
        /// </summary>
        internal static bool IsDeviceOrPipePath(string path)
        {
            string normalized = path.Replace('/', '\\');
            if (normalized.StartsWith(@"\\?\", StringComparison.Ordinal))
            {
                string rest = normalized.Substring(4);
                if (IsDriveRooted(rest)) return false;
                if (!rest.StartsWith(@"UNC\", StringComparison.OrdinalIgnoreCase)) return true;
                normalized = @"\\" + rest.Substring(4);
            }
            if (IsDriveRooted(normalized)) return false;
            if (!normalized.StartsWith(@"\\", StringComparison.Ordinal)) return true;
            string[] segments = normalized.Substring(2).Split('\\');
            if (segments[0] == ".") return true;
            return segments.Length >= 2
                && (segments[1].Equals("pipe", StringComparison.OrdinalIgnoreCase) || segments[1].Equals("mailslot", StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsDriveRooted(string path)
            => path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\';

        // One handle for the length check and the read, so the limit holds for what is actually read: a file that grows
        // after the check is refused, and anything that is not a seekable disk file (a pipe, a device) is not read at all.
        private static byte[] ReadBounded(string path, long maxBytes)
        {
            try
            {
                if (IsDeviceOrPipePath(path))
                    throw new UsageException("ContentPathInvalid",
                        "`contentPath` is a device or named-pipe path, not a file: " + path + ". No write was attempted.");
                if (Directory.Exists(path))
                    throw new UsageException("ContentPathNotFound",
                        "`contentPath` is not an existing file: " + path + ". No write was attempted.");

                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan))
                    return ReadBoundedStream(stream, maxBytes, path);
            }
            catch (UsageException) { throw; }
            catch (FileNotFoundException)
            {
                throw new UsageException("ContentPathNotFound", "`contentPath` is not an existing file: " + path + ". No write was attempted.");
            }
            catch (DirectoryNotFoundException)
            {
                throw new UsageException("ContentPathNotFound", "`contentPath` is not an existing file: " + path + ". No write was attempted.");
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException)
            {
                throw new UsageException("ContentPathInvalid", "`contentPath` is not a valid path: " + ex.Message + " No write was attempted.");
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
            {
                throw new UsageException("ContentPathUnreadable", "`contentPath` could not be read: " + ex.Message + " No write was attempted.");
            }
        }

        /// <summary>
        /// Reads the whole stream when it is a seekable file within the limit: the length is checked on the stream being
        /// read, the read takes no more than that length, and one more byte after it means the file grew past the check.
        /// </summary>
        internal static byte[] ReadBoundedStream(Stream stream, long maxBytes, string path)
        {
            if (!stream.CanSeek)
                throw new UsageException("ContentPathInvalid",
                    "`contentPath` is not a regular file: " + path + ". No write was attempted.");
            if (stream.Length > maxBytes)
                throw TooLarge(stream.Length, maxBytes);

            var buffer = new byte[stream.Length];
            int total = 0;
            while (total < buffer.Length)
            {
                int read = stream.Read(buffer, total, buffer.Length - total);
                if (read == 0) break;
                total += read;
            }
            if (stream.ReadByte() >= 0)
                throw TooLarge(null, maxBytes);
            if (total != buffer.Length) Array.Resize(ref buffer, total);
            return buffer;
        }

        private static UsageException TooLarge(long? knownLength, long maxBytes)
            => new UsageException("ContentPathTooLarge",
                "`contentPath` " + (knownLength.HasValue ? "is " + knownLength.Value + " bytes" : "grew past the limit while it was being read")
                + "; the limit is " + maxBytes + " bytes (the routed command carries the text twice). "
                + "For a larger part use genexus_io action=import_part with inputPath. No write was attempted.");

        // `contentPath` belongs to a single-part full write. Inside a batch item it would be ignored without a word.
        private static void RejectNestedContentPath(JObject args)
        {
            foreach (string key in new[] { "targets", "parts" })
                if (args[key] is JArray items && items.OfType<JObject>().Any(item => IsPresent(item["contentPath"])))
                    throw NestedContentPath(key + "[]");
            if (args["changeSet"] is JObject changeSet)
            {
                JArray? changes = changeSet["changes"] as JArray ?? changeSet["targets"] as JArray;
                if (changes != null && changes.OfType<JObject>().Any(item => IsPresent(item["contentPath"])))
                    throw NestedContentPath("changeSet");
            }
        }

        private static UsageException NestedContentPath(string where)
            => new UsageException("ContentPathNotAWholePartWrite",
                "`contentPath` is accepted only on a single-part full write (name + part + mode=full), not inside `" + where
                + "`: send `content` in each item. No write was attempted.");

        private static bool IsPresent(JToken? token) => token != null && token.Type != JTokenType.Null;
    }
}
