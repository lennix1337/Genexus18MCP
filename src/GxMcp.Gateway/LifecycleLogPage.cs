using System;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    /// <summary>
    /// #830 item 2: a line-addressed page of a finished build's log for lifecycle status and result.
    /// The full output is the Worker's <c>build-&lt;taskId&gt;.log</c>; when that file cannot be read,
    /// the lines the task kept (<c>TailLines</c>) are paged instead and the page names its source.
    /// Only the log named for this task's own Worker task is opened, so a stored path cannot make
    /// the Gateway read another task's output (#656).
    /// </summary>
    internal static class LifecycleLogPage
    {
        public const int DefaultLimit = 40;
        public const int MaxLimit = 200;

        public static bool IsRequested(JObject? args)
            => args?["log_offset"] != null || args?["log_limit"] != null;

        public static JObject Build(JobEntry job, JObject? result, JObject? args)
        {
            if (!TryReadNonNegative(args, "log_offset", 0, out int offset))
                return Unavailable("log_offset must be a non-negative integer.");
            if (!TryReadNonNegative(args, "log_limit", DefaultLimit, out int limit) || limit < 1)
                return Unavailable("log_limit must be an integer from 1 to " + MaxLimit + ".");
            limit = Math.Min(limit, MaxLimit);
            if (string.Equals(job.Status, "running", StringComparison.OrdinalIgnoreCase)
                || string.Equals(job.Status, "queued", StringComparison.OrdinalIgnoreCase))
                return Unavailable("The task has not finished; its log is written when it ends.");

            string? fullLogPath = (result?["FullLogPath"] ?? result?["fullLogPath"])?.ToString();
            bool fullLogMissing = false;
            if (!string.IsNullOrWhiteSpace(fullLogPath) && IsLogOfWorkerTask(fullLogPath, job.WorkerTaskId))
            {
                if (File.Exists(fullLogPath))
                    return ReadFullLog(fullLogPath, offset, limit);
                fullLogMissing = true;
            }

            if (result?["TailLines"] is JArray tail)
            {
                var page = PageOf(tail, offset, limit, "tailLines");
                if (fullLogMissing) page["fullLogAvailable"] = false;
                return page;
            }
            return Unavailable("No log is stored for this task.");
        }

        internal static bool IsLogOfWorkerTask(string path, string? workerTaskId)
            => !string.IsNullOrWhiteSpace(workerTaskId)
               && string.Equals(Path.GetFileName(path), "build-" + workerTaskId + ".log", StringComparison.OrdinalIgnoreCase);

        private static JObject ReadFullLog(string path, int offset, int limit)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                var lines = new JArray();
                int total = 0;
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (total >= offset && lines.Count < limit)
                        lines.Add(LifecycleResponseShaper.CapLine(line));
                    total++;
                }
                return PageEnvelope("fullLog", offset, limit, lines, total);
            }
            catch (IOException ex)
            {
                return Unavailable("The full build log could not be read: " + ex.Message);
            }
        }

        private static JObject PageOf(JArray source, int offset, int limit, string sourceName)
        {
            var lines = new JArray();
            for (int i = offset; i < source.Count && lines.Count < limit; i++)
            {
                string text = source[i]?.ToString() ?? string.Empty;
                lines.Add(LifecycleResponseShaper.CapLine(text));
            }
            return PageEnvelope(sourceName, offset, limit, lines, source.Count);
        }

        private static JObject PageEnvelope(string source, int offset, int limit, JArray lines, int totalLines)
        {
            int next = offset + lines.Count;
            return new JObject
            {
                ["available"] = true,
                ["source"] = source,
                ["offset"] = offset,
                ["limit"] = limit,
                ["returned"] = lines.Count,
                ["totalLines"] = totalLines,
                ["nextOffset"] = next < totalLines ? (JToken)next : JValue.CreateNull(),
                ["lines"] = lines
            };
        }

        private static JObject Unavailable(string reason)
            => new JObject
            {
                ["available"] = false,
                ["reason"] = reason
            };

        private static bool TryReadNonNegative(JObject? args, string name, int fallback, out int value)
        {
            value = fallback;
            JToken? token = args?[name];
            if (token == null || token.Type == JTokenType.Null) return true;
            if (token.Type == JTokenType.Integer)
            {
                long raw = token.Value<long>();
                if (raw < 0 || raw > int.MaxValue) return false;
                value = (int)raw;
                return true;
            }
            return int.TryParse(token.ToString(), out value) && value >= 0;
        }
    }
}
