using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public sealed class SharedWorkerHostProtocolTests
    {
        [Fact]
        public void ChildStdinWriter_EncodesUtf8WithoutBomAndUsesLf()
        {
            using (var stream = new System.IO.MemoryStream())
            {
                using (var writer = SharedWorkerHost.CreateChildStdinWriter(stream))
                {
                    writer.WriteLine("ação");
                    writer.Flush();
                }

                Assert.Equal(new byte[] { 0x61, 0xC3, 0xA7, 0xC3, 0xA3, 0x6F, 0x0A }, stream.ToArray());
            }
        }

        [Fact]
        public void AttachEnvelope_ParsesAndValidatesAgainstExactIdentity()
        {
            string identityKey = new string('a', 64);
            var frame = new JObject
            {
                ["type"] = "attach",
                ["protocolVersion"] = SharedWorkerHostProtocol.ProtocolVersion,
                ["identityKey"] = identityKey,
                ["gatewayPid"] = 1234,
                ["gatewayStartTimeUtcTicks"] = 638000000000000000L,
                ["attachNonce"] = "nonce-1234567890"
            }.ToString(Newtonsoft.Json.Formatting.None);

            SharedWorkerEnvelope envelope;
            string error;
            Assert.True(SharedWorkerHostProtocol.TryParseEnvelope(frame, out envelope, out error), error);
            Assert.True(SharedWorkerHostProtocol.TryValidateAttach(envelope, identityKey, out error), error);
            Assert.Equal("attach", envelope.Type);
            Assert.Equal(identityKey, envelope.IdentityKey);
        }

        [Fact]
        public void EnvelopeParser_RejectsMalformedOversizedAndUnauthorizedFrames()
        {
            SharedWorkerEnvelope envelope;
            string error;
            Assert.False(SharedWorkerHostProtocol.TryParseEnvelope("{", out envelope, out error));
            Assert.False(SharedWorkerHostProtocol.TryParseEnvelope(
                new string('x', SharedWorkerHostProtocol.MaxFrameBytes + 1),
                out envelope,
                out error));

            var heartbeat = new SharedWorkerEnvelope
            {
                Type = "heartbeat",
                ProtocolVersion = SharedWorkerHostProtocol.ProtocolVersion,
                IdentityKey = new string('a', 64)
            };
            Assert.False(SharedWorkerHostProtocol.TryValidateSessionEnvelope(heartbeat, false, out error));

            var unknown = new JObject
            {
                ["type"] = "not-a-protocol-frame",
                ["protocolVersion"] = SharedWorkerHostProtocol.ProtocolVersion
            }.ToString(Newtonsoft.Json.Formatting.None);
            Assert.False(SharedWorkerHostProtocol.TryParseEnvelope(unknown, out envelope, out error));
        }

        [Fact]
        public void RequestIds_AreRewrittenPerAttachmentAndRestoredOnResponse()
        {
            var request = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 41,
                ["method"] = "tools/call",
                ["_meta"] = new JObject { ["progressToken"] = "same-token" }
            };

            SharedWorkerRequestRoute route;
            JObject childRequest;
            string error;
            Assert.True(SharedWorkerHostProtocol.TryRewriteRequestForChild(
                request,
                "attach-a",
                1,
                out childRequest,
                out route,
                out error), error);

            Assert.NotEqual("41", childRequest["id"]?.ToString());
            Assert.NotEqual("same-token", childRequest["_meta"]?["progressToken"]?.ToString());
            Assert.Equal("attach-a", route.AttachmentId);
            Assert.Equal("attach-a", childRequest["_meta"]?["attachmentId"]?.ToString());
            Assert.Equal(41, route.ClientRequestId.Value<int>());
            Assert.Equal("same-token", route.ClientProgressToken.Value<string>());

            var childResponse = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = childRequest["id"],
                ["result"] = new JObject { ["ok"] = true }
            };
            JObject restored;
            Assert.True(SharedWorkerHostProtocol.TryRestoreResponse(childResponse, route, out restored, out error), error);
            Assert.Equal(41, restored["id"]?.Value<int>());
        }

        [Fact]
        public void ProgressNotifications_RouteByChildTokenAndRestoreClientToken()
        {
            var requestA = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = "request-a",
                ["method"] = "long_operation",
                ["_meta"] = new JObject { ["progressToken"] = "same-token" }
            };
            var requestB = (JObject)requestA.DeepClone();
            requestB["id"] = "request-b";

            SharedWorkerRequestRoute routeA;
            SharedWorkerRequestRoute routeB;
            JObject childA;
            JObject childB;
            string error;
            Assert.True(SharedWorkerHostProtocol.TryRewriteRequestForChild(requestA, "attach-a", 1, out childA, out routeA, out error), error);
            Assert.True(SharedWorkerHostProtocol.TryRewriteRequestForChild(requestB, "attach-b", 2, out childB, out routeB, out error), error);
            Assert.NotEqual(childA["_meta"]?["progressToken"]?.ToString(), childB["_meta"]?["progressToken"]?.ToString());

            var owners = new Dictionary<string, SharedWorkerRequestRoute>(StringComparer.Ordinal)
            {
                [routeA.ChildProgressToken] = routeA,
                [routeB.ChildProgressToken] = routeB
            };
            var childProgress = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = "notifications/progress",
                ["params"] = new JObject
                {
                    ["progressToken"] = routeB.ChildProgressToken,
                    ["progress"] = 2
                }
            };

            string attachmentId;
            JObject restored;
            bool broadcast;
            Assert.True(SharedWorkerHostProtocol.TryRouteChildNotification(
                childProgress,
                owners,
                out attachmentId,
                out restored,
                out broadcast,
                out error), error);
            Assert.False(broadcast);
            Assert.Equal("attach-b", attachmentId);
            Assert.Equal("same-token", restored["params"]?["progressToken"]?.ToString());
        }

        [Fact]
        public void CancellationNotifications_RouteOnlyToTheOwningAttachmentAndPreserveIdType()
        {
            var routeA = new SharedWorkerRequestRoute
            {
                AttachmentId = "attach-a",
                ClientRequestId = new JValue(7),
                ChildRequestId = "child-a"
            };
            var routeB = new SharedWorkerRequestRoute
            {
                AttachmentId = "attach-b",
                ClientRequestId = new JValue(7),
                ChildRequestId = "child-b"
            };
            var cancellation = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = "notifications/cancelled",
                ["params"] = new JObject { ["requestId"] = 7 }
            };

            JObject rewritten;
            string error;
            Assert.True(SharedWorkerHostProtocol.TryRewriteCancellationForChild(
                cancellation, new[] { routeA, routeB }, "attach-b", out rewritten, out error), error);
            Assert.Equal("child-b", rewritten["params"]?["requestId"]?.Value<string>());

            Assert.False(SharedWorkerHostProtocol.TryRewriteCancellationForChild(
                cancellation, new[] { routeA, routeB }, "attach-c", out rewritten, out error));
            Assert.Contains("not owned", error, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void ResourceUpdates_AreExplicitBroadcastsAndUnknownProgressIsDropped()
        {
            var owners = new Dictionary<string, SharedWorkerRequestRoute>(StringComparer.Ordinal);
            string attachmentId;
            JObject routed;
            bool broadcast;
            string error;

            var resourcesUpdated = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = "notifications/resources/updated",
                ["params"] = new JObject { ["uri"] = "gx://kb/object" }
            };
            Assert.True(SharedWorkerHostProtocol.TryRouteChildNotification(
                resourcesUpdated, owners, out attachmentId, out routed, out broadcast, out error), error);
            Assert.True(broadcast);
            Assert.Null(attachmentId);

            var unknownProgress = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = "notifications/progress",
                ["params"] = new JObject { ["progressToken"] = "not-owned", ["progress"] = 1 }
            };
            Assert.False(SharedWorkerHostProtocol.TryRouteChildNotification(
                unknownProgress, owners, out attachmentId, out routed, out broadcast, out error));

            var sdkReady = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = "notifications/worker/sdk_ready",
                ["params"] = new JObject { ["kb"] = "main" }
            };
            Assert.True(SharedWorkerHostProtocol.TryRouteChildNotification(
                sdkReady, owners, out attachmentId, out routed, out broadcast, out error), error);
            Assert.True(broadcast);
            Assert.Null(attachmentId);

            var indexActive = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = "notifications/worker/index_active",
                ["params"] = new JObject { ["processed"] = 100, ["total"] = 1000 }
            };
            Assert.True(SharedWorkerHostProtocol.TryRouteChildNotification(
                indexActive, owners, out attachmentId, out routed, out broadcast, out error), error);
            Assert.True(broadcast);
            Assert.Null(attachmentId);

            var unknown = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = "notifications/message",
                ["params"] = new JObject { ["data"] = "client-local" }
            };
            Assert.False(SharedWorkerHostProtocol.TryRouteChildNotification(
                unknown, owners, out attachmentId, out routed, out broadcast, out error));
        }

        [Fact]
        public void HostOptions_DoesNotConsumeBareSharedHostMarker()
        {
            var options = SharedWorkerHostOptions.Parse(new[]
            {
                "--shared-host",
                "--worker-executable", @"C:\Worker\GxMcp.Worker.exe",
                "--kb", @"C:\KBs\Shared",
                "--installation", @"C:\GeneXus18",
                "--driver", "native-sdk",
                "--major", "18"
            });

            Assert.Equal(@"C:\KBs\Shared", options.KbPath);
            Assert.Equal("native-sdk", options.Driver);
            Assert.Equal("18", options.Major);
        }

        [Fact]
        public void IdentityKey_NormalizesPathsAndPipeNamesFailClosed()
        {
            string first;
            string second;
            string error;
            Assert.True(SharedWorkerHostProtocol.TryCreateIdentityKey(
                @"C:\Worker\GxMcp.Worker.exe\",
                @"C:\KBs\Shared\",
                @"C:\GeneXus18\",
                "Native-SDK",
                "18",
                out first,
                out error), error);
            Assert.True(SharedWorkerHostProtocol.TryCreateIdentityKey(
                @"c:\worker\GxMcp.Worker.exe",
                @"c:\kbs\shared",
                @"c:\genexus18",
                "native-sdk",
                "18",
                out second,
                out error), error);
            Assert.Equal(first, second);

            string pipeName;
            Assert.True(SharedWorkerHostProtocol.TryBuildPipeName(first, out pipeName, out error), error);
            Assert.True(SharedWorkerHostProtocol.IsValidPipeName(pipeName));
            Assert.False(SharedWorkerHostProtocol.TryBuildPipeName("not-a-sha256-key", out pipeName, out error));
            Assert.False(SharedWorkerHostProtocol.IsValidPipeName("GxMcpShared\\unauthorized"));
            Assert.False(SharedWorkerHostProtocol.IsValidPipeName(""));

            string differentMajor;
            Assert.True(SharedWorkerHostProtocol.TryCreateIdentityKey(
                @"c:\worker\GxMcp.Worker.exe",
                @"c:\kbs\shared",
                @"c:\genexus18",
                "native-sdk",
                "17",
                out differentMajor,
                out error), error);
            Assert.NotEqual(first, differentMajor);
        }
    }
}
