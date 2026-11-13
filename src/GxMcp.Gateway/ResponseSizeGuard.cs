using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Text;

namespace GxMcp.Gateway
{
    public sealed class ResponseSizeGuard
    {
        public const int DefaultMaxBytes = 220_000; // ~55k tokens

        private readonly int _maxBytes;
        private readonly Action<string> _log;

        public ResponseSizeGuard(int maxBytes = DefaultMaxBytes) : this(maxBytes, Program.Log) { }

        internal ResponseSizeGuard(int maxBytes, Action<string> log)
        {
            _maxBytes = maxBytes;
            _log = log;
        }

        public (JObject result, bool truncated) Apply(JObject payload, string toolName, JObject? args)
        {
            long size = ByteSize(payload);
            if (size <= _maxBytes) return (payload, false);

            int originalSize = size > int.MaxValue ? int.MaxValue : (int)size;

            var sentinel = new JObject
            {
                ["_meta"] = new JObject
                {
                    ["truncated"] = new JObject
                    {
                        ["reason"] = "response_exceeded_cap",
                        ["original_size"] = originalSize,
                        ["cap_bytes"] = _maxBytes,
                        ["follow_up"] = BuildFollowUp(toolName, args)
                    }
                }
            };

            _log($"[Gateway] OVERSIZE tool={toolName} size={size}");
            return (sentinel, true);
        }

        private static JObject BuildFollowUp(string tool, JObject? args)
        {
            var followArgs = args != null ? (JObject)args.DeepClone() : new JObject();
            // issue #25 follow-up (A3): the retry hint must use args the tool actually
            // understands. inspect/analyze don't paginate (page/page_size were a no-op
            // that looped the agent back to the same oversize call) — steer them to the
            // levers that shrink the payload instead.
            string t = tool ?? string.Empty;
            if (t.Equals("genexus_inspect", StringComparison.OrdinalIgnoreCase))
            {
                followArgs["include"] = new JArray("signature", "variables", "metadata");
                return new JObject { ["tool"] = tool, ["args"] = followArgs,
                    ["hint"] = "Response too large. Re-request a subset with include=[...], or read a single part with genexus_read (paginated)." };
            }
            if (t.Equals("genexus_analyze", StringComparison.OrdinalIgnoreCase)
                || t.Equals("genexus_navigation", StringComparison.OrdinalIgnoreCase)
                || t.Equals("genexus_structure", StringComparison.OrdinalIgnoreCase))
            {
                return new JObject { ["tool"] = tool, ["args"] = followArgs,
                    ["hint"] = "Response too large. Narrow the request (a specific target/mode/part) or read incrementally with genexus_read." };
            }
            // Only genexus_lifecycle reads page/page_size; every other tool silently ignores
            // them and the agent would repeat the same oversize call. Emit the arguments the
            // tool's schema really has, or no args plus a prose hint to narrow the request.
            if (t.Equals("genexus_lifecycle", StringComparison.OrdinalIgnoreCase))
            {
                followArgs["page"] = 1;
                followArgs["page_size"] = 25;
                return new JObject { ["tool"] = tool, ["args"] = followArgs };
            }
            if (t.Equals("genexus_read", StringComparison.OrdinalIgnoreCase))
            {
                followArgs["offset"] = followArgs["offset"] ?? 0;
                followArgs["limit"] = SmallerLimit(followArgs["limit"], 200);
                return new JObject { ["tool"] = tool, ["args"] = followArgs,
                    ["hint"] = "Response too large. Read the part in windows: repeat with offset advanced by limit until the part ends." };
            }
            if (t.Equals("genexus_list_objects", StringComparison.OrdinalIgnoreCase)
                || t.Equals("genexus_kb_version", StringComparison.OrdinalIgnoreCase)
                || t.Equals("genexus_wwp", StringComparison.OrdinalIgnoreCase))
            {
                followArgs["offset"] = followArgs["offset"] ?? 0;
                followArgs["limit"] = SmallerLimit(followArgs["limit"], 25);
                return new JObject { ["tool"] = tool, ["args"] = followArgs,
                    ["hint"] = "Response too large. Page with offset/limit (use nextOffset or nextCursor from the result until hasMore=false)." };
            }
            if (t.Equals("genexus_query", StringComparison.OrdinalIgnoreCase))
            {
                followArgs["limit"] = SmallerLimit(followArgs["limit"], 25);
                return new JObject { ["tool"] = tool, ["args"] = followArgs,
                    ["hint"] = "Response too large. Lower limit and resume with the cursor from nextCursor, or narrow the query." };
            }
            if (t.Equals("genexus_search_source", StringComparison.OrdinalIgnoreCase))
            {
                followArgs["maxResults"] = SmallerLimit(followArgs["maxResults"], 25);
                return new JObject { ["tool"] = tool, ["args"] = followArgs,
                    ["hint"] = "Response too large. Lower maxResults and resume with the cursor from nextCursor, or narrow the scan to an object." };
            }
            return new JObject { ["tool"] = tool,
                ["hint"] = "Response too large and this tool has no pagination arguments. Narrow the request (a specific name/target/part/action) and call again." };
        }

        private static JToken SmallerLimit(JToken? current, int fallback)
        {
            int value = current != null && current.Type == JTokenType.Integer ? current.Value<int>() : 0;
            return value > 1 ? value / 2 : fallback;
        }

        // PERFORMANCE (G-B2): existing path streams the JSON through a CountingStream so we
        // never allocate the full string just to measure its size — that's still the right
        // call for unknown payloads. Two micro-tweaks below:
        //  - buffer bumped from 1KB to 32KB so a 220KB payload triggers ~7 internal flushes
        //    instead of ~220;
        //  - overload that takes an already-serialised string uses Encoding.UTF8.GetByteCount
        //    directly, which is the fast path for callers that have the JSON in hand already.
        internal static long ByteSize(string serializedJson)
        {
            if (string.IsNullOrEmpty(serializedJson)) return 0;
            return Encoding.UTF8.GetByteCount(serializedJson);
        }

        [ThreadStatic]
        private static CountingTextWriter? t_countingWriter;

        private sealed class CountingTextWriter : TextWriter
        {
            public override Encoding Encoding => Encoding.UTF8;
            public long Count;

            public void Reset() => Count = 0;

            public override void Write(char value)
            {
                if (value <= 0x7F) Count++;
                else if (value <= 0x7FF) Count += 2;
                else if (char.IsSurrogate(value)) Count += 2;
                else Count += 3;
            }

            public override void Write(string? value)
            {
                if (!string.IsNullOrEmpty(value))
                    Count += Encoding.UTF8.GetByteCount(value);
            }

            public override void Write(char[] buffer, int index, int count)
            {
                if (buffer != null && count > 0)
                    Count += Encoding.UTF8.GetByteCount(buffer, index, count);
            }

            public override void Write(ReadOnlySpan<char> buffer)
            {
                if (!buffer.IsEmpty)
                    Count += Encoding.UTF8.GetByteCount(buffer);
            }
        }

        internal static long ByteSize(JToken token)
        {
            if (token == null) return 0;
            var writer = t_countingWriter ??= new CountingTextWriter();
            writer.Reset();
            using (var jw = new JsonTextWriter(writer) { Formatting = Formatting.None, CloseOutput = false })
            {
                token.WriteTo(jw);
                jw.Flush();
            }
            return writer.Count;
        }
    }
}
