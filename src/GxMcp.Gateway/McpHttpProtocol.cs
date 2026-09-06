using System;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.AspNetCore.Http;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    internal readonly record struct McpHttpError(int StatusCode, string Message, int JsonRpcCode = -32002, JObject? Data = null);

    internal static class McpHttpProtocol
    {
        internal const int MaxRequestBodyBytes = 2 * 1024 * 1024;
        internal const string ModernClientIdHeader = "Mcp-Client-Id";

        public static bool IsInitializeRequest(JObject requestObj)
        {
            return string.Equals(requestObj["method"]?.ToString(), "initialize", StringComparison.Ordinal);
        }

        internal static bool IsCancellationNotification(JObject requestObj)
        {
            return string.Equals(requestObj?["method"]?.ToString(), "notifications/cancelled", StringComparison.Ordinal);
        }

        public static bool IsModernRequest(HttpRequest request, JObject requestObj)
        {
            return McpRouter.IsModernProtocolVersion(request.Headers["MCP-Protocol-Version"].FirstOrDefault())
                || McpRouter.IsModernRequest(requestObj);
        }

        public static string? GetRequestProtocolVersion(JObject requestObj)
        {
            return McpRouter.GetRequestProtocolVersion(requestObj);
        }

        private static string? GetModernBodyProtocolVersion(JObject requestObj)
        {
            var parameters = requestObj["params"] as JObject;
            return (parameters?["_meta"] as JObject)?["io.modelcontextprotocol/protocolVersion"]?.ToString();
        }

        /// <summary>
        /// Validate the per-request metadata and routing headers introduced by the
        /// sessionless 2026-07-28 HTTP transport. The body remains the source of
        /// truth; mirrored headers are accepted only when they agree exactly.
        /// </summary>
        public static McpHttpError? ValidateModernRequest(HttpRequest request, JObject requestObj)
        {
            string? headerVersion = request.Headers["MCP-Protocol-Version"].FirstOrDefault();
            string? bodyVersion = GetModernBodyProtocolVersion(requestObj);
            if (!McpRouter.IsModernProtocolVersion(headerVersion) ||
                !McpRouter.IsModernProtocolVersion(bodyVersion))
            {
                return HeaderMismatch(
                    "MCP-Protocol-Version must be 2026-07-28 and must match params._meta.io.modelcontextprotocol/protocolVersion.");
            }

            // RequestMetaObject requires clientCapabilities for request/response
            // messages. Notifications have no defined metadata requirements in
            // this transport revision, so only enforce it for id-bearing requests.
            bool hasRequestId = requestObj["id"] != null && requestObj["id"]!.Type != JTokenType.Null;
            if (hasRequestId)
            {
                var metadata = (requestObj["params"] as JObject)?["_meta"] as JObject;
                if (metadata?["io.modelcontextprotocol/clientCapabilities"]?.Type != JTokenType.Object)
                {
                    return new McpHttpError(
                        StatusCodes.Status400BadRequest,
                        "Modern request metadata must include an object at params._meta.io.modelcontextprotocol/clientCapabilities.",
                        -32602,
                        new JObject { ["field"] = "params._meta.io.modelcontextprotocol/clientCapabilities" });
                }
            }

            string method = requestObj["method"]?.ToString() ?? string.Empty;
            string? methodHeader = request.Headers["Mcp-Method"].FirstOrDefault();
            if (string.IsNullOrEmpty(methodHeader) || !string.Equals(methodHeader, method, StringComparison.Ordinal))
            {
                return HeaderMismatch($"Mcp-Method header does not match request method '{method}'.");
            }

            string? expectedName = GetStandardHeaderName(requestObj);
            if (expectedName != null)
            {
                string? nameHeader = request.Headers["Mcp-Name"].FirstOrDefault();
                string? decodedName = DecodeHeaderValue(nameHeader);
                if (decodedName == null || !string.Equals(decodedName, expectedName, StringComparison.Ordinal))
                {
                    return HeaderMismatch($"Mcp-Name header does not match request name '{expectedName}'.");
                }
            }

            return null;
        }

        internal static string? GetStandardHeaderName(JObject requestObj)
        {
            string method = requestObj["method"]?.ToString() ?? string.Empty;
            var parameters = requestObj["params"] as JObject;
            if (string.Equals(method, "tools/call", StringComparison.Ordinal) ||
                string.Equals(method, "prompts/get", StringComparison.Ordinal))
            {
                return parameters?["name"]?.ToString();
            }

            if (string.Equals(method, "resources/read", StringComparison.Ordinal))
            {
                return parameters?["uri"]?.ToString();
            }

            if (string.Equals(method, "resources/subscribe", StringComparison.Ordinal)
                || string.Equals(method, "resources/unsubscribe", StringComparison.Ordinal))
            {
                return parameters?["uri"]?.ToString();
            }

            if (string.Equals(method, "tasks/get", StringComparison.Ordinal) ||
                string.Equals(method, "tasks/update", StringComparison.Ordinal) ||
                string.Equals(method, "tasks/cancel", StringComparison.Ordinal))
            {
                return parameters?["taskId"]?.ToString();
            }

            return null;
        }

        /// <summary>
        /// Returns the stable client identity used to scope sessionless task
        /// handles. The identity is deliberately explicit: a TCP connection is
        /// not a reliable client boundary for HTTP clients that reconnect or
        /// use a proxy.
        /// </summary>
        internal static string? GetModernClientId(HttpRequest request)
        {
            string? value = request.Headers[ModernClientIdHeader].FirstOrDefault()?.Trim();
            if (string.IsNullOrWhiteSpace(value) || value.Length > 128) return null;
            foreach (char ch in value)
            {
                if (!(char.IsLetterOrDigit(ch) || ch == '.' || ch == '_' || ch == '-' || ch == '~'))
                    return null;
            }
            return value;
        }

        internal static string EncodeHeaderValue(string value)
        {
            if (value != null && value.Length > 0 && value.Trim() == value &&
                !LooksLikeBase64Sentinel(value) && value.All(ch => ch >= 0x20 && ch <= 0x7e))
            {
                return value;
            }

            byte[] bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
            return "=?base64?" + Convert.ToBase64String(bytes) + "?=";
        }

        private static string? DecodeHeaderValue(string? value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            if (!LooksLikeBase64Sentinel(value))
            {
                return value.Any(ch => ch < 0x20 || ch > 0x7e) ? null : value;
            }

            string encoded = value.Substring("=?base64?".Length, value.Length - "=?base64?".Length - 2);
            try
            {
                return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                    .GetString(Convert.FromBase64String(encoded));
            }
            catch (FormatException) { return null; }
            catch (DecoderFallbackException) { return null; }
        }

        private static bool LooksLikeBase64Sentinel(string value)
        {
            return value.StartsWith("=?base64?", StringComparison.Ordinal)
                && value.EndsWith("?=", StringComparison.Ordinal)
                && value.Length >= "=?base64??=".Length;
        }

        private static McpHttpError HeaderMismatch(string message)
        {
            return new McpHttpError(
                StatusCodes.Status400BadRequest,
                message,
                -32020,
                new JObject { ["reason"] = message });
        }

        public static McpHttpError? ValidatePostHeaders(HttpRequest request)
        {
            if (!ContainsMediaType(request.ContentType, "application/json"))
            {
                return new McpHttpError(StatusCodes.Status415UnsupportedMediaType,
                    "MCP POST requests must use Content-Type: application/json.");
            }

            string accept = request.Headers["Accept"].ToString();
            if (!ContainsMediaType(accept, "application/json") ||
                !ContainsMediaType(accept, "text/event-stream"))
            {
                return new McpHttpError(StatusCodes.Status406NotAcceptable,
                    "MCP POST requests must accept both application/json and text/event-stream.");
            }

            return null;
        }

        internal static McpHttpError? ValidateBodyLength(long? contentLength)
        {
            if (contentLength.HasValue && contentLength.Value > MaxRequestBodyBytes)
            {
                return new McpHttpError(
                    StatusCodes.Status413PayloadTooLarge,
                    $"MCP request body exceeds the {MaxRequestBodyBytes} byte limit.",
                    -32600,
                    new JObject { ["maxBytes"] = MaxRequestBodyBytes });
            }

            return null;
        }

        public static McpHttpError? ValidateSseHeaders(HttpRequest request)
        {
            if (!ContainsMediaType(request.Headers["Accept"].ToString(), "text/event-stream"))
            {
                return new McpHttpError(StatusCodes.Status406NotAcceptable,
                    "MCP SSE requests must include Accept: text/event-stream.");
            }

            return null;
        }

        private static bool ContainsMediaType(string? headerValue, string expectedMediaType)
        {
            if (string.IsNullOrWhiteSpace(headerValue)) return false;

            foreach (string item in headerValue.Split(','))
            {
                string[] parameters = item.Split(';');
                if (!string.Equals(parameters[0].Trim(), expectedMediaType, StringComparison.OrdinalIgnoreCase))
                    continue;

                double quality = 1.0;
                bool validQuality = true;
                foreach (string parameter in parameters.Skip(1))
                {
                    string[] pair = parameter.Split(new[] { '=' }, 2);
                    if (pair.Length != 2 || !string.Equals(pair[0].Trim(), "q", StringComparison.OrdinalIgnoreCase))
                        continue;

                    string rawQuality = pair[1].Trim().Trim('"');
                    if (!double.TryParse(rawQuality, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
                        || parsed < 0 || parsed > 1)
                    {
                        validQuality = false;
                        break;
                    }
                    quality = parsed;
                }

                if (validQuality && quality > 0) return true;
            }

            return false;
        }

        public static McpHttpError? TryApplyProtocol(HttpRequest request, IHeaderDictionary responseHeaders, string? expectedProtocolVersion = null)
        {
            string? requestedProtocolVersion = request.Headers["MCP-Protocol-Version"].FirstOrDefault();
            if (!string.IsNullOrEmpty(requestedProtocolVersion) &&
                Array.IndexOf(McpRouter.KnownProtocolVersions, requestedProtocolVersion) < 0)
            {
                return new McpHttpError(StatusCodes.Status400BadRequest,
                    $"Unsupported MCP protocol version '{requestedProtocolVersion}'. Supported versions: {string.Join(", ", McpRouter.KnownProtocolVersions)}.",
                    -32022,
                    new JObject
                    {
                        ["supported"] = new JArray(McpRouter.KnownProtocolVersions),
                        ["requested"] = requestedProtocolVersion
                    });
            }

            if (!string.IsNullOrEmpty(expectedProtocolVersion) &&
                !string.IsNullOrEmpty(requestedProtocolVersion) &&
                !string.Equals(expectedProtocolVersion, requestedProtocolVersion, StringComparison.Ordinal))
            {
                return new McpHttpError(StatusCodes.Status400BadRequest,
                    $"MCP protocol version '{requestedProtocolVersion}' does not match the negotiated session version '{expectedProtocolVersion}'.");
            }

            responseHeaders["MCP-Protocol-Version"] = expectedProtocolVersion
                ?? requestedProtocolVersion
                ?? McpRouter.SupportedProtocolVersion;
            return null;
        }

        public static McpHttpError? TryGetValidSession(HttpSessionRegistry sessionRegistry, HttpRequest request, JObject requestObj, out HttpSessionState? session, bool modern = false)
        {
            session = null;

            if (modern) return null;
            if (IsInitializeRequest(requestObj)) return null;

            string? sessionId = request.Headers["MCP-Session-Id"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return new McpHttpError(StatusCodes.Status400BadRequest, "Missing MCP-Session-Id header.");
            }

            if (!sessionRegistry.TryGet(sessionId, out session))
            {
                return new McpHttpError(StatusCodes.Status404NotFound, "Unknown or expired MCP session.");
            }

            return null;
        }
    }
}
