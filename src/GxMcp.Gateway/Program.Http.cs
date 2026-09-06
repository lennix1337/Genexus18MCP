using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Diagnostics;

namespace GxMcp.Gateway
{
    partial class Program
    {
        // Guards the heartbeat/cleanup loops so they start once across all bind-retry attempts,
        // tied to the gateway process lifetime rather than a per-attempt WebApplication.
        private static int _backgroundLoopsStarted;
        private static readonly System.Threading.CancellationTokenSource _gatewayLifetime =
            new System.Threading.CancellationTokenSource();

        internal static bool IsOriginAllowed(string? origin, ServerConfig? serverConfig)
        {
            if (string.IsNullOrWhiteSpace(origin)) return true;

            if (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri)) return false;
            if (originUri.IsLoopback) return true;

            var allowedOrigins = serverConfig?.AllowedOrigins;
            if (allowedOrigins == null || allowedOrigins.Count == 0) return false;

            return allowedOrigins.Any(allowed => string.Equals(allowed, origin, StringComparison.OrdinalIgnoreCase));
        }

        internal static bool IsLoopbackHostAllowed(string? host, string? bindAddress)
        {
            string normalizedHost = (host ?? string.Empty).Trim().TrimEnd('.');
            if (normalizedHost.Length == 0) return false;

            if (string.Equals(normalizedHost, "localhost", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalizedHost, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalizedHost, "::1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalizedHost, "[::1]", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            string normalizedBind = (bindAddress ?? string.Empty).Trim().TrimEnd('.');
            return normalizedBind.Length > 0
                && !string.Equals(normalizedBind, "0.0.0.0", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(normalizedBind, "::", StringComparison.OrdinalIgnoreCase)
                && string.Equals(normalizedHost, normalizedBind, StringComparison.OrdinalIgnoreCase);
        }

        // P5: SSE delivery via bounded channels instead of polling
        // session.PendingMessages under a lock every 5 seconds. Every producer goes
        // through QueueSessionMessage below; the GET /mcp consumer awaits
        // reader.ReadAsync and a separate watchdog writes ": keepalive" only after
        // ~20s without traffic.
        private const int SseChannelCapacity = 64;
        private static readonly TimeSpan SseKeepaliveInterval = TimeSpan.FromSeconds(20);
        // Keyed by session Id so payloads queued before (or without) an attached SSE
        // reader buffer exactly like the old PendingMessages queue did — including
        // across client reconnects. Entries are completed+removed when the session
        // ends: DELETE /mcp or the orphan sweep in CreateHttpSession.
        private static readonly ConcurrentDictionary<string, Channel<string>> _sseChannels =
            new ConcurrentDictionary<string, Channel<string>>(StringComparer.Ordinal);
        // Modern 2026 subscriptions/listen streams are transport-scoped and do
        // not use MCP-Session-Id. Keep their handles separate from legacy HTTP
        // sessions so reconnects cannot inherit another client's subscriptions.
        private static readonly McpModernSubscriptionRegistry _modernSubscriptions =
            new McpModernSubscriptionRegistry();

        private static Channel<string> GetOrAddSseChannel(string sessionId)
        {
            return _sseChannels.GetOrAdd(sessionId, _ => Channel.CreateBounded<string>(
                new BoundedChannelOptions(SseChannelCapacity)
                {
                    // DropOldest keeps producers non-blocking (all call sites are sync
                    // fire-and-forget paths that cannot await WriteAsync) and sheds the
                    // oldest payload first — same overflow semantics the registry's
                    // Enqueue cap had.
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                    SingleWriter = false
                }));
        }

        private static HttpSessionState CreateHttpSession()
        {
            // Orphan sweep: complete and drop channels whose session the registry has
            // already expired. Done here (rather than in the shared cleanup loop in
            // Program.Notifications.cs) to keep this change confined to this file;
            // runs once per legacy-session creation, which bounds any leak to the
            // sessions created since the previous sweep.
            foreach (var pair in _sseChannels)
            {
                if (!_httpSessions.TryGet(pair.Key, out _)
                    && _sseChannels.TryRemove(pair.Key, out var orphan))
                {
                    orphan.Writer.TryComplete();
                }
            }

            return _httpSessions.Create();
        }

        private static void QueueSessionMessage(HttpSessionState session, string payload)
        {
            var channel = GetOrAddSseChannel(session.Id);
            if (!channel.Writer.TryWrite(payload))
            {
                Log($"[HTTP] Failed to queue SSE message for session {session.Id} (channel completed).");
            }
        }

        private static async Task<IResult> HandleMcpSseStream(HttpContext context)
        {
            if (McpRouter.IsModernProtocolVersion(context.Request.Headers["MCP-Protocol-Version"].FirstOrDefault()))
            {
                context.Response.Headers["Allow"] = "POST";
                return Results.StatusCode(StatusCodes.Status405MethodNotAllowed);
            }

            var headerError = McpHttpProtocol.ValidateSseHeaders(context.Request);
            if (headerError != null)
                return Results.Json(new { error = headerError.Value.Message }, statusCode: headerError.Value.StatusCode);

            string? sessionId = context.Request.Headers["MCP-Session-Id"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(sessionId))
                return Results.BadRequest(new { error = "Missing MCP-Session-Id header." });

            if (!_httpSessions.TryGet(sessionId, out var session))
                return Results.NotFound(new { error = "Unknown or expired MCP session." });

            if (session == null)
                return Results.NotFound(new { error = "Unknown or expired MCP session." });

            var protocolError = McpHttpProtocol.TryApplyProtocol(context.Request, context.Response.Headers, session.ProtocolVersion);
            if (protocolError != null)
                return Results.Json(new { error = protocolError.Value.Message }, statusCode: protocolError.Value.StatusCode);

            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.Headers["Content-Type"] = "text/event-stream";
            context.Response.Headers["Cache-Control"] = "no-cache";
            context.Response.Headers["Connection"] = "keep-alive";
            context.Response.Headers["X-Accel-Buffering"] = "no";
            context.Response.Headers["MCP-Session-Id"] = session.Id;

            await context.Response.WriteAsync("retry: 5000\n");
            await context.Response.WriteAsync($"event: session\ndata: {{\"sessionId\":\"{session.Id}\"}}\n\n");
            await context.Response.Body.FlushAsync();

            try
            {
                // Ironclad SSE: No deadline, keep alive indefinitely until client or server disconnects.
                ChannelReader<string> reader = GetOrAddSseChannel(session.Id).Reader;
                long lastWriteTicks = DateTime.UtcNow.Ticks;
                using var sseWriteLock = new SemaphoreSlim(1, 1);
                using var linkCts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);

                // Keepalive watchdog: writes ": keepalive" only after ~20s without
                // traffic (messages or prior keepalives), replacing the old poll
                // loop's fixed 5-second wake-up. Consumer and watchdog run on
                // separate tasks, so every response write goes through sseWriteLock.
                Task keepalive = Task.Run(async () =>
                {
                    while (!linkCts.Token.IsCancellationRequested)
                    {
                        await Task.Delay(SseKeepaliveInterval, linkCts.Token);
                        if (DateTime.UtcNow.Ticks - Interlocked.Read(ref lastWriteTicks) < SseKeepaliveInterval.Ticks)
                            continue;

                        await sseWriteLock.WaitAsync(linkCts.Token);
                        try
                        {
                            await context.Response.WriteAsync(": keepalive\n\n", linkCts.Token);
                            await context.Response.Body.FlushAsync(linkCts.Token);
                        }
                        finally { sseWriteLock.Release(); }
                        Interlocked.Exchange(ref lastWriteTicks, DateTime.UtcNow.Ticks);
                    }
                }, linkCts.Token);

                try
                {
                    // Signal-driven delivery: blocks until a producer writes or the
                    // client disconnects — no polling latency, no idle wake-ups.
                    // Frame format is byte-identical to the previous implementation.
                    while (true)
                    {
                        string payload = await reader.ReadAsync(context.RequestAborted);
                        string encodedPayload = payload.Replace("\r", "").Replace("\n", "\ndata: ");
                        await sseWriteLock.WaitAsync(context.RequestAborted);
                        try
                        {
                            await context.Response.WriteAsync($"event: message\ndata: {encodedPayload}\n\n", context.RequestAborted);
                            await context.Response.Body.FlushAsync(context.RequestAborted);
                        }
                        finally { sseWriteLock.Release(); }
                        Interlocked.Exchange(ref lastWriteTicks, DateTime.UtcNow.Ticks);
                    }
                }
                catch (OperationCanceledException) { }
                catch (ChannelClosedException) { } // session ended (DELETE /mcp or expiry sweep)
                finally
                {
                    linkCts.Cancel();
                    try { await keepalive; }
                    catch (OperationCanceledException) { }
                }
            }
            catch (Exception ex)
            {
                Log($"[HTTP] SSE stream error for session {session.Id}: {ex.Message}");
            }

            return Results.Empty;
        }

        private static async Task<IResult> HandleModernSubscriptionListen(
            HttpContext context,
            JObject requestObj)
        {
            if (!_modernSubscriptions.TryOpen(requestObj, out var subscription, out var error))
            {
                return Results.Content(
                    (error ?? new JObject
                    {
                        ["jsonrpc"] = "2.0",
                        ["id"] = requestObj["id"]?.DeepClone() ?? JValue.CreateNull(),
                        ["error"] = new JObject
                        {
                            ["code"] = -32025,
                            ["message"] = "The subscription stream could not be allocated."
                        }
                    }).ToString(Formatting.None),
                    "application/json; charset=utf-8",
                    Encoding.UTF8,
                    StatusCodes.Status429TooManyRequests);
            }

            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.Headers["Content-Type"] = "text/event-stream";
            context.Response.Headers["Cache-Control"] = "no-cache";
            context.Response.Headers["Connection"] = "keep-alive";
            context.Response.Headers["X-Accel-Buffering"] = "no";
            context.Response.Headers["MCP-Protocol-Version"] = McpRouter.ModernProtocolVersion;

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            using var writeLock = new SemaphoreSlim(1, 1);
            try
            {
                var acknowledgement = new JObject
                {
                    ["jsonrpc"] = "2.0",
                    ["method"] = "notifications/subscriptions/acknowledged",
                    ["params"] = new JObject
                    {
                        ["notifications"] = subscription!.GrantedNotifications,
                        ["_meta"] = new JObject
                        {
                            ["io.modelcontextprotocol/subscriptionId"] = subscription.Id
                        }
                    }
                };
                await WriteModernSubscriptionEvent(
                    context,
                    acknowledgement.ToString(Formatting.None),
                    writeLock,
                    linkedCts.Token);

                long lastWriteTicks = DateTime.UtcNow.Ticks;
                Task keepalive = Task.Run(async () =>
                {
                    while (!linkedCts.Token.IsCancellationRequested)
                    {
                        await Task.Delay(SseKeepaliveInterval, linkedCts.Token);
                        if (DateTime.UtcNow.Ticks - Interlocked.Read(ref lastWriteTicks) < SseKeepaliveInterval.Ticks)
                            continue;

                        await writeLock.WaitAsync(linkedCts.Token);
                        try
                        {
                            await context.Response.WriteAsync(": keepalive\n\n", linkedCts.Token);
                            await context.Response.Body.FlushAsync(linkedCts.Token);
                        }
                        finally { writeLock.Release(); }
                        Interlocked.Exchange(ref lastWriteTicks, DateTime.UtcNow.Ticks);
                    }
                }, linkedCts.Token);

                try
                {
                    while (true)
                    {
                        string payload = await subscription.Reader.ReadAsync(context.RequestAborted);
                        await WriteModernSubscriptionEvent(context, payload, writeLock, linkedCts.Token);
                        Interlocked.Exchange(ref lastWriteTicks, DateTime.UtcNow.Ticks);
                    }
                }
                catch (OperationCanceledException) { }
                catch (ChannelClosedException) { }
                finally
                {
                    linkedCts.Cancel();
                    try { await keepalive; }
                    catch (OperationCanceledException) { }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log($"[HTTP] Modern subscriptions/listen stream error for {subscription!.Id}: {ex.Message}");
            }
            finally
            {
                _modernSubscriptions.Remove(subscription!.Id, out _);
            }

            return Results.Empty;
        }

        private static async Task WriteModernSubscriptionEvent(
            HttpContext context,
            string payload,
            SemaphoreSlim writeLock,
            CancellationToken cancellationToken)
        {
            string encodedPayload = payload.Replace("\r", "").Replace("\n", "\ndata: ");
            await writeLock.WaitAsync(cancellationToken);
            try
            {
                await context.Response.WriteAsync($"event: message\ndata: {encodedPayload}\n\n", cancellationToken);
                await context.Response.Body.FlushAsync(cancellationToken);
            }
            finally { writeLock.Release(); }
        }

        // Plan 014: sensitive-key substrings (case-insensitive). Values under a
        // matching key — and any nested object/array value, sensitive or not — are
        // masked before the inbound request is summarized to the durable gateway log.
        private static readonly string[] SensitiveKeys = { "password", "passwd", "pass", "token", "secret", "key", "credential", "authorization", "apikey" };

        internal static string RedactBodyForLog(JObject requestObj)
        {
            try
            {
                var args = requestObj?["params"]?["arguments"] as JObject;
                if (args == null) return "(no arguments)";
                var parts = new List<string>();
                foreach (var prop in args.Properties())
                {
                    bool sensitive = SensitiveKeys.Any(k => prop.Name.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0);
                    string shown = sensitive || prop.Value.Type == JTokenType.Object || prop.Value.Type == JTokenType.Array
                        ? "***"
                        : Truncate(prop.Value.ToString(), 40);
                    parts.Add(prop.Name + "=" + shown);
                }
                return "{" + string.Join(", ", parts) + "}";
            }
            catch { return "(unparseable)"; }
        }

        private static string Truncate(string s, int n) => s == null ? "" : (s.Length > n ? s.Substring(0, n) + "…" : s);

        private static IResult JsonRpcHttpError(JObject requestObj, McpHttpError error)
        {
            var errorObj = new JObject
            {
                ["code"] = error.JsonRpcCode,
                ["message"] = error.Message
            };
            if (error.Data != null) errorObj["data"] = error.Data.DeepClone();

            return Results.Json(new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = requestObj["id"]?.DeepClone() ?? JValue.CreateNull(),
                ["error"] = errorObj
            }, statusCode: error.StatusCode);
        }

        internal static async Task<IResult> HandleJsonRpcHttpRequest(HttpRequest request)
        {
            var headerError = McpHttpProtocol.ValidatePostHeaders(request);
            if (headerError != null)
                return Results.Json(new { error = headerError.Value.Message }, statusCode: headerError.Value.StatusCode);

            var bodyLengthError = McpHttpProtocol.ValidateBodyLength(request.ContentLength);
            if (bodyLengthError != null)
            {
                return Results.Json(new JObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = JValue.CreateNull(),
                    ["error"] = new JObject
                    {
                        ["code"] = bodyLengthError.Value.JsonRpcCode,
                        ["message"] = bodyLengthError.Value.Message,
                        ["data"] = bodyLengthError.Value.Data?.DeepClone()
                    }
                }, statusCode: bodyLengthError.Value.StatusCode);
            }

            using (var reader = new StreamReader(request.Body))
            {
                string body = await reader.ReadToEndAsync();
                string id = "no-id";

                try
                {
                    var requestObj = JsonConvert.DeserializeObject<JObject>(body);
                    if (requestObj == null) return Results.Json(new { jsonrpc = "2.0", id = (string?)null, error = new { code = -32700, message = "Invalid JSON" } }, statusCode: 400);

                    id = requestObj["id"]?.ToString() ?? "no-id";
                    bool modern = McpHttpProtocol.IsModernRequest(request, requestObj);
                    if (modern)
                    {
                        var modernHeaderError = McpHttpProtocol.ValidateModernRequest(request, requestObj);
                        if (modernHeaderError != null)
                            return JsonRpcHttpError(requestObj, modernHeaderError.Value);

                        if (McpHttpProtocol.IsInitializeRequest(requestObj))
                        {
                            return Results.Json(new JObject
                            {
                                ["jsonrpc"] = "2.0",
                                ["id"] = requestObj["id"]?.DeepClone() ?? JValue.CreateNull(),
                                ["error"] = new JObject
                                {
                                    ["code"] = -32601,
                                    ["message"] = "Method not found: initialize is not part of the 2026-07-28 sessionless protocol. Use server/discover."
                                }
                            }, statusCode: StatusCodes.Status404NotFound);
                        }
                    }

                    var sessionError = McpHttpProtocol.TryGetValidSession(_httpSessions, request, requestObj, out var session, modern);
                    if (sessionError != null)
                        return Results.Json(new { jsonrpc = "2.0", id = id, error = new { code = -32001, message = sessionError.Value.Message } }, statusCode: sessionError.Value.StatusCode);

                    string expectedProtocolVersion = modern
                        ? McpRouter.ModernProtocolVersion
                        : McpHttpProtocol.IsInitializeRequest(requestObj)
                        ? McpRouter.NegotiateProtocolVersion((requestObj["params"] as JObject)?["protocolVersion"]?.ToString())
                        : session?.ProtocolVersion ?? McpRouter.SupportedProtocolVersion;
                    var protocolError = McpHttpProtocol.TryApplyProtocol(request, request.HttpContext.Response.Headers, expectedProtocolVersion);
                    if (protocolError != null)
                        return JsonRpcHttpError(requestObj, protocolError.Value);

                    id = requestObj["id"]?.ToString() ?? "no-id";
                    string method = requestObj["method"]?.ToString() ?? "unknown";
                    Log($"[HTTP] Received {method} (ID: {id}) - Args: {RedactBodyForLog(requestObj)}");

                    string? modernClientId = modern ? McpHttpProtocol.GetModernClientId(request) : null;
                    bool taskScopeEnabled = !modern || modernClientId != null;
                    string httpSessionId = modern
                        ? modernClientId == null
                            ? $"http-modern-unscoped:{Guid.NewGuid():N}"
                            : $"http-modern:{modernClientId}"
                        : session?.Id ?? request.Headers["MCP-Session-Id"].FirstOrDefault() ?? "http";

                    // Streamable HTTP is sessionless in the modern contract. A
                    // cancellation notification sent by a later POST has no
                    // transport identity that can safely own an earlier request;
                    // treating the shared pseudo-session as an owner would let
                    // clients with the same JSON-RPC id cancel one another. The
                    // modern binding uses response-stream closure for cancellation,
                    // so accept and ignore this notification here.
                    if (modern && McpHttpProtocol.IsCancellationNotification(requestObj))
                    {
                        Log($"[HTTP] Ignoring sessionless modern cancellation notification for id={id}.");
                        return Results.StatusCode(StatusCodes.Status202Accepted);
                    }

                    if (modern && McpModernSubscriptionProtocol.IsListenRequest(requestObj))
                    {
                        Log($"[HTTP] Opening modern subscriptions/listen stream for {id}.");
                        return await HandleModernSubscriptionListen(request.HttpContext, requestObj);
                    }

                    // The 2026-07-28 transport is explicitly sessionless. Do not
                    // reuse a shared server-side KB selection between independent
                    // modern clients; they use explicit kb or the persisted fallback.
                    var response = await ProcessMcpRequest(
                        requestObj,
                        httpSessionId,
                        sessionContextEnabled: !modern,
                        transportCancellation: request.HttpContext.RequestAborted,
                        taskScopeEnabled: taskScopeEnabled);

                    bool notification = requestObj["id"] == null
                        || requestObj["id"]!.Type == JTokenType.Null;
                    if (notification)
                    {
                        if (modern && response?["error"] != null)
                        {
                            // A rejected modern notification may carry a JSON-RPC
                            // error body, but it must still be an HTTP error rather
                            // than a successful 202 acknowledgement.
                            request.HttpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
                            return Results.Content(
                                response.ToString(Formatting.None),
                                "application/json; charset=utf-8",
                                Encoding.UTF8);
                        }

                        Log($"[HTTP] Notification {method} completed without response body.");
                        return modern ? Results.StatusCode(StatusCodes.Status202Accepted) : Results.NoContent();
                    }

                    if (!modern && McpHttpProtocol.IsInitializeRequest(requestObj))
                    {
                        var newSession = CreateHttpSession();
                        newSession.ProtocolVersion = expectedProtocolVersion;
                        request.HttpContext.Response.Headers["MCP-Session-Id"] = newSession.Id;
                        QueueSessionMessage(newSession, JsonConvert.SerializeObject(new
                        {
                            jsonrpc = "2.0",
                            method = "notifications/message",
                            @params = new
                            {
                                level = "info",
                                logger = "transport",
                                data = "HTTP MCP session initialized."
                            }
                        }));
                    }

                    if (response != null)
                    {
                        Log($"[HTTP] Serializing response for {id}...");
                        string jsonResponse = response.ToString(Formatting.None);
                        Log($"[HTTP] Sending {jsonResponse.Length} bytes to {id}");
                        if (modern && response["error"]?["code"]?.ToObject<int?>() == -32601)
                        {
                            request.HttpContext.Response.StatusCode = StatusCodes.Status404NotFound;
                        }
                        if (method == "tools/list" || method == "resources/list" || method == "prompts/list")
                        {
                            request.HttpContext.Response.Headers["Cache-Control"] = "public, max-age=3600";
                        }
                        return Results.Content(jsonResponse, "application/json; charset=utf-8", Encoding.UTF8);
                    }

                    return Results.BadRequest(new { error = "No response generated" });
                }
                catch (OperationCanceledException)
                {
                    Log($"[HTTP] Request aborted by client: {id}");
                    return Results.StatusCode(499); // Client Closed Request
                }
                catch (Exception ex)
                {
                    Log($"[HTTP] Error processing {id}: {ex.Message}");
                    return Results.Json(new { jsonrpc = "2.0", id = id, error = new { code = -32603, message = $"Gateway Error: {ex.Message}" } });
                }
            }
        }

        // SECURITY: the Origin header only defends against browser-issued cross-site
        // requests (curl/another local process/a port-forward omit it and sail past
        // IsOriginAllowed). The /mcp surface grants full tool access (SDK writes, the
        // `gh` shell-out, the AI-completion proxy that holds a live key), so gate it
        // with an optional shared secret (env GXMCP_HTTP_TOKEN). Contract:
        //   token set          -> every /mcp request must present it (Bearer / X-GXMCP-Token)
        //   no token + loopback -> allowed (preserves the default 127.0.0.1 dev workflow)
        //   no token + non-loopback bind -> refused (don't silently expose to the network)
        internal static bool IsLoopbackBind(string bindAddress)
        {
            if (string.IsNullOrWhiteSpace(bindAddress)) return false; // blank -> 0.0.0.0, not loopback
            var b = bindAddress.Trim();
            return b == "127.0.0.1" || b == "::1" || b.Equals("localhost", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool ConstantTimeEquals(string a, string b)
        {
            if (a == null || b == null) return false;
            var ba = System.Text.Encoding.UTF8.GetBytes(a);
            var bb = System.Text.Encoding.UTF8.GetBytes(b);
            int diff = ba.Length ^ bb.Length;
            for (int i = 0; i < ba.Length && i < bb.Length; i++) diff |= ba[i] ^ bb[i];
            return diff == 0;
        }

        internal static bool IsHttpTokenValid(HttpContext context, string expected)
        {
            string presented = null;
            var auth = context.Request.Headers["Authorization"].FirstOrDefault();
            if (!string.IsNullOrEmpty(auth) && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                presented = auth.Substring("Bearer ".Length).Trim();
            if (string.IsNullOrEmpty(presented))
                presented = context.Request.Headers["X-GXMCP-Token"].FirstOrDefault();
            return !string.IsNullOrEmpty(presented) && ConstantTimeEquals(presented, expected);
        }

        static Task StartHttpServer(Configuration config)
        {
            var serverConfig = config.Server ?? new ServerConfig();
            string bindAddress = string.IsNullOrWhiteSpace(serverConfig.BindAddress) ? "0.0.0.0" : serverConfig.BindAddress;
            string httpToken = Environment.GetEnvironmentVariable("GXMCP_HTTP_TOKEN");
            bool loopbackBind = IsLoopbackBind(serverConfig.BindAddress);
            if (string.IsNullOrEmpty(httpToken) && !loopbackBind)
                Log($"[HTTP] WARNING: binding to non-loopback '{bindAddress}' with no GXMCP_HTTP_TOKEN — /mcp requests will be refused. Set GXMCP_HTTP_TOKEN or bind to 127.0.0.1.");
            Log($"[HTTP] Starting server on {bindAddress}:{serverConfig.HttpPort}...");
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls($"http://{bindAddress}:{serverConfig.HttpPort}");
            // P4: MCP payloads are small JSON-RPC envelopes; cap request bodies
            // explicitly at 2MB instead of relying on Kestrel's ~30MB default.
            builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = McpHttpProtocol.MaxRequestBodyBytes);
            builder.Logging.ClearProviders();
            builder.Services.AddResponseCompression(options => { options.EnableForHttps = true; });
            var app = builder.Build();
            app.UseResponseCompression();
            // Start the heartbeat/cleanup loops exactly once, tied to the gateway's own
            // lifetime — NOT app.Lifetime. StartHttpServer runs once per bind-retry attempt
            // (up to 5×), so starting them here per-call leaked a set of loops per failed
            // attempt, each bound to a WebApplication whose ApplicationStopping may never fire
            // (it never fully started). One guarded set, cancelled on process exit, avoids the
            // orphaned-loop churn during a bind-recovery storm.
            if (System.Threading.Interlocked.Exchange(ref _backgroundLoopsStarted, 1) == 0)
            {
                var ct = _gatewayLifetime.Token;
                _ = Task.Run(() => RunSessionCleanupLoop(ct));
                _ = Task.Run(() => RunLeaseHeartbeatLoop(ct));
            }

            app.Use(async (context, next) =>
            {
                if (context.Request.Path.StartsWithSegments("/mcp"))
                {
                    if (loopbackBind && !IsLoopbackHostAllowed(context.Request.Host.Host, serverConfig.BindAddress))
                    {
                        context.Response.StatusCode = StatusCodes.Status403Forbidden;
                        await context.Response.WriteAsync("Host not allowed for loopback MCP binding.");
                        return;
                    }

                    string? origin = context.Request.Headers["Origin"].FirstOrDefault();
                    if (!IsOriginAllowed(origin, serverConfig))
                    {
                        context.Response.StatusCode = StatusCodes.Status403Forbidden;
                        await context.Response.WriteAsync("Origin not allowed.");
                        return;
                    }

                    if (!string.IsNullOrEmpty(httpToken))
                    {
                        if (!IsHttpTokenValid(context, httpToken))
                        {
                            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                            await context.Response.WriteAsync("Missing or invalid GXMCP_HTTP_TOKEN.");
                            return;
                        }
                    }
                    else if (!loopbackBind)
                    {
                        context.Response.StatusCode = StatusCodes.Status403Forbidden;
                        await context.Response.WriteAsync("Non-loopback bind requires GXMCP_HTTP_TOKEN.");
                        return;
                    }
                }

                await next();
            });

            app.MapPost("/mcp", async (HttpRequest request) => await HandleJsonRpcHttpRequest(request));
            app.MapGet("/mcp", async (HttpContext context) => await HandleMcpSseStream(context));
            app.MapDelete("/mcp", (HttpRequest request) =>
            {
                if (McpRouter.IsModernProtocolVersion(request.Headers["MCP-Protocol-Version"].FirstOrDefault()))
                {
                    request.HttpContext.Response.Headers["Allow"] = "POST";
                    return Results.StatusCode(StatusCodes.Status405MethodNotAllowed);
                }

                string? sessionId = request.Headers["MCP-Session-Id"].FirstOrDefault();
                if (string.IsNullOrWhiteSpace(sessionId))
                    return Results.BadRequest(new { error = "Missing MCP-Session-Id header." });

                if (!_httpSessions.TryGet(sessionId, out var session) || session == null)
                    return Results.NotFound(new { error = "Unknown or expired MCP session." });

                var protocolError = McpHttpProtocol.TryApplyProtocol(request, request.HttpContext.Response.Headers, session.ProtocolVersion);
                if (protocolError != null)
                    return Results.Json(new { error = protocolError.Value.Message }, statusCode: protocolError.Value.StatusCode);

                _httpSessions.Remove(sessionId);
                if (_sseChannels.TryRemove(sessionId, out var ended))
                {
                    ended.Writer.TryComplete(); // unblocks the SSE consumer, if attached
                }

                Log($"[HTTP] Session {sessionId} terminated by client.");
                return Results.NoContent();
            });

            return app.RunAsync();
        }

        private static void TryKillProcessOnPort(int port)
        {
            try {
               Log($"[PortRecovery] Attempting to find process on port {port}...");
               var process = new Process();
               process.StartInfo.FileName = "netstat";
               process.StartInfo.Arguments = "-ano";
               process.StartInfo.RedirectStandardOutput = true;
               process.StartInfo.UseShellExecute = false;
               process.StartInfo.CreateNoWindow = true;
               process.Start();
               string output = process.StandardOutput.ReadToEnd();
               process.WaitForExit();

               var lines = output.Split('\n');
               foreach (var line in lines)
               {
                   if (line.Contains($":{port}") && line.Contains("LISTENING"))
                   {
                       var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                       var pidStr = parts.Last().Trim();
                       if (int.TryParse(pidStr, out int pid) && pid != Environment.ProcessId)
                       {
                           try {
                               var zombie = Process.GetProcessById(pid);
                               // Only reclaim the port from one of OUR OWN processes (a prior
                               // gateway or its dotnet host). Blindly Kill(true)-ing whatever
                               // holds the port could nuke an unrelated app — or, in the
                               // split-brain case, a still-live master gateway's whole tree
                               // (its GeneXus worker included). If it isn't ours, leave it be.
                               string pname = zombie.ProcessName;
                               bool ours = pname.Equals("GxMcp.Gateway", StringComparison.OrdinalIgnoreCase)
                                        || pname.Equals("dotnet", StringComparison.OrdinalIgnoreCase);
                               if (!ours)
                               {
                                   Log($"[PortRecovery] Process {pid} ({pname}) holds port {port} but is not a GxMcp gateway — not killing. Configure a different HttpPort.");
                                   continue;
                               }
                               Log($"[PortRecovery] Found stale gateway {pid} ({pname}) on port {port}. Killing it...");
                               zombie.Kill(true);
                               zombie.WaitForExit(3000);
                           } catch { } // Process might already be gone
                       }
                   }
               }
            } catch (Exception ex) { Log($"[PortRecovery] Error: {ex.Message}"); }
        }
    }
}
