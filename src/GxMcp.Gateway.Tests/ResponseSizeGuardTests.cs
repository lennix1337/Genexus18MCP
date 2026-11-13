using System;
using System.Linq;
using System.Text;
using GxMcp.Gateway;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class ResponseSizeGuardTests
    {
        // ── helpers ──────────────────────────────────────────────────────────

        private static JObject SmallPayload() =>
            JObject.Parse("""{"result":"ok","data":"hello"}""");

        private static JObject OversizedPayload(int targetBytes = 250_000)
        {
            // Build a payload whose serialized UTF-8 length exceeds targetBytes
            var padding = new string('x', targetBytes);
            return JObject.Parse($$$"""{"result":"ok","data":"{{{padding}}}"}""");
        }

        private static JObject SomeArgs() =>
            JObject.Parse("""{"object_name":"Invoice","type":"Transaction"}""");

        [Fact]
        public void ByteSize_StringOverload_CountsUtf8Bytes()
        {
            var serialized = """{"text":"café ✓"}""";

            Assert.Equal(Encoding.UTF8.GetByteCount(serialized), ResponseSizeGuard.ByteSize(serialized));
        }

        // ── small payload passes through unchanged ────────────────────────────

        [Fact]
        public void SmallPayload_PassesThrough_Unchanged()
        {
            var guard = new ResponseSizeGuard(maxBytes: 220_000);
            var payload = SmallPayload();
            var originalJson = payload.ToString(Newtonsoft.Json.Formatting.None);

            var (result, truncated) = guard.Apply(payload, "genexus_read", SomeArgs());

            Assert.False(truncated);
            Assert.Equal(originalJson, result.ToString(Newtonsoft.Json.Formatting.None));
        }

        [Fact]
        public void SmallPayload_DoesNotMutateInput()
        {
            var guard = new ResponseSizeGuard(maxBytes: 220_000);
            var payload = SmallPayload();
            var before = payload.ToString(Newtonsoft.Json.Formatting.None);

            guard.Apply(payload, "genexus_read", SomeArgs());

            Assert.Equal(before, payload.ToString(Newtonsoft.Json.Formatting.None));
        }

        [Fact]
        public void ExactCap_SerialisedPayload_DoesNotTruncate()
        {
            var payload = JObject.Parse("""{"result":"ok","data":"hello"}""");
            var exactCap = checked((int)ResponseSizeGuard.ByteSize(payload));
            var guard = new ResponseSizeGuard(maxBytes: exactCap);

            var (result, truncated) = guard.Apply(payload, "genexus_read", SomeArgs());

            Assert.False(truncated);
            Assert.Equal(payload.ToString(Newtonsoft.Json.Formatting.None), result.ToString(Newtonsoft.Json.Formatting.None));
        }

        // ── oversized payload returns sentinel ───────────────────────────────

        [Fact]
        public void OversizedPayload_ReturnsTruncatedTrue()
        {
            var guard = new ResponseSizeGuard(maxBytes: 220_000);

            var (_, truncated) = guard.Apply(OversizedPayload(), "genexus_read", SomeArgs());

            Assert.True(truncated);
        }

        [Fact]
        public void OversizedPayload_SentinelHas_MetaTruncated()
        {
            var guard = new ResponseSizeGuard(maxBytes: 220_000);

            var (result, _) = guard.Apply(OversizedPayload(), "genexus_read", SomeArgs());

            Assert.IsType<JObject>(result["_meta"]);
            Assert.IsType<JObject>(result["_meta"]!["truncated"]);
            Assert.IsType<JObject>(result["_meta"]!["truncated"]!["follow_up"]);
        }

        [Fact]
        public void OversizedPayload_Sentinel_HasReason()
        {
            var guard = new ResponseSizeGuard(maxBytes: 220_000);

            var (result, _) = guard.Apply(OversizedPayload(), "genexus_read", SomeArgs());

            var reason = result["_meta"]!["truncated"]!["reason"]?.ToString();
            Assert.Equal("response_exceeded_cap", reason);
        }

        [Fact]
        public void OversizedPayload_Sentinel_HasOriginalSize()
        {
            var guard = new ResponseSizeGuard(maxBytes: 220_000);
            var payload = OversizedPayload(250_000);
            long expectedSize = ResponseSizeGuard.ByteSize(payload);

            var (result, _) = guard.Apply(payload, "genexus_read", SomeArgs());

            var originalSize = result["_meta"]!["truncated"]!["original_size"]?.Value<long>();
            Assert.Equal(expectedSize, originalSize);
        }

        [Fact]
        public void OversizedPayload_Sentinel_HasCapBytes()
        {
            var guard = new ResponseSizeGuard(maxBytes: 220_000);

            var (result, _) = guard.Apply(OversizedPayload(), "genexus_read", SomeArgs());

            var capBytes = result["_meta"]!["truncated"]!["cap_bytes"]?.Value<int>();
            Assert.Equal(220_000, capBytes);
        }

        [Fact]
        public void OversizedPayload_Sentinel_HasFollowUpToolName()
        {
            var guard = new ResponseSizeGuard(maxBytes: 220_000);

            var (result, _) = guard.Apply(OversizedPayload(), "genexus_read", SomeArgs());

            var followUpTool = result["_meta"]!["truncated"]!["follow_up"]!["tool"]?.ToString();
            Assert.Equal("genexus_read", followUpTool);
        }

        [Fact]
        public void OversizedPayload_ReadFollowUp_UsesOffsetAndLimitNotPageArgs()
        {
            var guard = new ResponseSizeGuard(maxBytes: 220_000);

            var (result, _) = guard.Apply(OversizedPayload(), "genexus_read", SomeArgs());

            var followArgs = (JObject)result["_meta"]!["truncated"]!["follow_up"]!["args"]!;
            Assert.Equal(0, followArgs["offset"]?.Value<int>());
            Assert.Equal(200, followArgs["limit"]?.Value<int>());
            Assert.Null(followArgs["page"]);
            Assert.Null(followArgs["page_size"]);
        }

        [Fact]
        public void OversizedPayload_LifecycleFollowUp_KeepsPageAndPageSize()
        {
            var guard = new ResponseSizeGuard(maxBytes: 220_000);

            var (result, _) = guard.Apply(OversizedPayload(), "genexus_lifecycle", JObject.Parse("""{"action":"result"}"""));

            var followArgs = (JObject)result["_meta"]!["truncated"]!["follow_up"]!["args"]!;
            Assert.Equal(1, followArgs["page"]?.Value<int>());
            Assert.Equal(25, followArgs["page_size"]?.Value<int>());
        }

        [Fact]
        public void OversizedPayload_ToolWithoutPagination_OmitsArgsAndGivesProseHint()
        {
            var guard = new ResponseSizeGuard(maxBytes: 220_000);

            var (result, _) = guard.Apply(OversizedPayload(), "genexus_security", SomeArgs());

            var followUp = (JObject)result["_meta"]!["truncated"]!["follow_up"]!;
            Assert.Null(followUp["args"]);
            Assert.Contains("Narrow", followUp["hint"]?.ToString());
        }

        [Fact]
        public void OversizedPayload_FollowUp_AddsOnlyArgumentsThePublishedSchemaDeclares()
        {
            var guard = new ResponseSizeGuard(maxBytes: 220_000);
            var tools = JArray.Parse(GxMcp.TestSupport.RepoSource.Read("src", "GxMcp.Gateway", "tool_definitions.json"));
            var offenders = new System.Collections.Generic.List<string>();

            foreach (var tool in tools.OfType<JObject>())
            {
                string name = tool.Value<string>("name")!;
                var declared = (JObject?)tool["inputSchema"]?["properties"] ?? new JObject();
                var original = JObject.Parse("""{"object_name":"Invoice"}""");
                var (result, _) = guard.Apply(OversizedPayload(), name, original);
                var followArgs = result["_meta"]!["truncated"]!["follow_up"]!["args"] as JObject;
                if (followArgs == null) continue;
                foreach (var prop in followArgs.Properties())
                    if (original[prop.Name] == null && declared[prop.Name] == null)
                        offenders.Add(name + "." + prop.Name);
            }

            Assert.True(offenders.Count == 0, "Follow-up args not in the tool schema: " + string.Join(", ", offenders));
        }

        [Fact]
        public void OversizedPayload_FollowUpArgs_PreservesOriginalArgs()
        {
            var guard = new ResponseSizeGuard(maxBytes: 220_000);
            var args = SomeArgs();

            var (result, _) = guard.Apply(OversizedPayload(), "genexus_read", args);

            var followArgs = (JObject)result["_meta"]!["truncated"]!["follow_up"]!["args"]!;
            Assert.Equal("Invoice", followArgs["object_name"]?.ToString());
            Assert.Equal("Transaction", followArgs["type"]?.ToString());
        }

        [Fact]
        public void OversizedPayload_FollowUpArgs_DoesNotMutateOriginalArgs()
        {
            var guard = new ResponseSizeGuard(maxBytes: 220_000);
            var args = SomeArgs();
            var argsBefore = args.ToString(Newtonsoft.Json.Formatting.None);

            guard.Apply(OversizedPayload(), "genexus_read", args);

            Assert.Equal(argsBefore, args.ToString(Newtonsoft.Json.Formatting.None));
        }

        // ── null args handled gracefully ─────────────────────────────────────

        [Fact]
        public void OversizedPayload_NullArgs_FollowUpArgsHasOffsetAndLimit()
        {
            var guard = new ResponseSizeGuard(maxBytes: 220_000);

            var (result, truncated) = guard.Apply(OversizedPayload(), "genexus_list_objects", null);

            Assert.True(truncated);
            var followArgs = (JObject)result["_meta"]!["truncated"]!["follow_up"]!["args"]!;
            Assert.Equal(0, followArgs["offset"]?.Value<int>());
            Assert.Equal(25, followArgs["limit"]?.Value<int>());
            Assert.Null(followArgs["page"]);
        }

        // ── custom cap respected ─────────────────────────────────────────────

        [Fact]
        public void CustomCap_SmallCapTriggersOnSmallPayload()
        {
            // Cap set to 10 bytes — any real JSON will exceed it
            var guard = new ResponseSizeGuard(maxBytes: 10);

            var (result, truncated) = guard.Apply(SmallPayload(), "genexus_read", SomeArgs());

            Assert.True(truncated);
            var capBytes = result["_meta"]!["truncated"]!["cap_bytes"]?.Value<int>();
            Assert.Equal(10, capBytes);
        }

        // ── default constant ─────────────────────────────────────────────────

        [Fact]
        public void DefaultMaxBytes_Is220000()
        {
            Assert.Equal(220_000, ResponseSizeGuard.DefaultMaxBytes);
        }

        // ── oversize telemetry ───────────────────────────────────────────────

        /// <summary>
        /// When Apply truncates a payload, it must emit an OVERSIZE log line.
        /// </summary>
        [Fact]
        public void OversizedPayload_EmitsOversizeLogLine()
        {
            string? logged = null;
            var guard = new ResponseSizeGuard(220_000, message => logged = message);
            guard.Apply(OversizedPayload(), "genexus_inspect", SomeArgs());

            Assert.Contains("OVERSIZE tool=genexus_inspect", logged);
        }
    }
}
