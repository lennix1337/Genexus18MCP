using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// Deep authoritative object reading engine.
    /// Unifies target resolution, virtual DSL synthesis, uniform pagination,
    /// read-through caching, and batch reads behind a cohesive interface.
    /// </summary>
    public class ObjectReader : IObjectReader
    {
        private sealed class CacheEntry
        {
            public string Payload { get; set; } = string.Empty;
            public DateTime UpdatedUtc { get; set; }
        }

        private static readonly ConcurrentDictionary<string, CacheEntry> _cache =
            new ConcurrentDictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);

        private static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(60);

        private readonly ObjectService _objectService;
        private readonly BatchService _batchService;

        public ObjectReader(ObjectService objectService, BatchService batchService = null)
        {
            _objectService = objectService;
            _batchService = batchService;
        }

        public string Read(ObjectReadRequest request)
        {
            if (request == null)
            {
                return McpResponse.Err(code: "InvalidRequest", message: "ObjectReadRequest cannot be null.");
            }

            // 1. Batch read delegation if multiple targets provided
            if (request.BatchTargets != null && request.BatchTargets.Count > 0)
            {
                if (_batchService != null)
                {
                    string partFilter = !string.IsNullOrWhiteSpace(request.PartName) ? request.PartName : "Source";
                    var partsArr = request.RequestedParts != null ? new JArray(request.RequestedParts) : null;
                    return _batchService.BatchRead(request.BatchTargets, partFilter, partsArr);
                }
            }

            if (string.IsNullOrWhiteSpace(request.Target))
            {
                request.Target = ObjectService.ResolveTargetForIdentity(request.Target, request.Guid, request.EntityKey, request.Path);
            }

            if (string.IsNullOrWhiteSpace(request.Target))
            {
                return McpResponse.Err(code: "MissingTarget", message: "Target object name or GUID is required.");
            }

            string cacheKey = BuildCacheKey(request);

            // 2. Read-through cache check
            if (!IsPatternSettingsRead(request) && TryGetCachedEntry(cacheKey, out string cachedResult))
            {
                return cachedResult;
            }

            if (_objectService == null)
            {
                return McpResponse.Err(code: "ServiceUnavailable", message: "ObjectService is not configured.");
            }

            string result;

            // 3. Full object vs Parts vs Single part read
            if (request.FullObject)
            {
                result = _objectService.ReadFullObject(request.Target, request.TypeFilter, request.Guid, request.EntityKey, request.Path);
            }
            else if (request.RequestedParts != null && request.RequestedParts.Any())
            {
                result = _objectService.ReadObjectSourceParts(request.Target, request.RequestedParts, request.TypeFilter, request.Guid, request.EntityKey, request.Path);
            }
            else
            {
                result = _objectService.ReadObjectSource(
                    request.Target,
                    request.PartName,
                    request.Offset,
                    request.Limit,
                    request.ClientFormat ?? "mcp",
                    request.Minimize,
                    request.TypeFilter,
                    request.Guid,
                    request.EntityKey,
                    request.Path);
            }

            // 4. Cache successful result
            if (!IsPatternSettingsRead(request) && IsCacheable(result))
            {
                _cache[cacheKey] = new CacheEntry
                {
                    Payload = result,
                    UpdatedUtc = DateTime.UtcNow
                };
            }

            return result;
        }

        public void Invalidate(string target, string part = null)
        {
            if (string.IsNullOrWhiteSpace(target)) return;

            string normalizedTarget = target.Trim();
            string prefix = normalizedTarget + "|";

            foreach (var key in _cache.Keys.ToList())
            {
                if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrWhiteSpace(part) || key.IndexOf("|" + part.Trim() + "|", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        _cache.TryRemove(key, out _);
                    }
                }
            }

            _objectService?.MarkReadCacheDirty(null, part);
        }

        public bool TryGetCached(string target, string part, int? offset, int? limit, string client, out string cachedJson)
        {
            var req = new ObjectReadRequest
            {
                Target = target,
                PartName = part,
                Offset = offset,
                Limit = limit,
                ClientFormat = client
            };
            return TryGetCachedEntry(BuildCacheKey(req), out cachedJson);
        }

        private static bool TryGetCachedEntry(string key, out string payload)
        {
            payload = string.Empty;
            if (string.IsNullOrWhiteSpace(key)) return false;

            if (_cache.TryGetValue(key, out var entry) && entry != null)
            {
                if (DateTime.UtcNow - entry.UpdatedUtc <= DefaultTtl)
                {
                    payload = entry.Payload;
                    return !string.IsNullOrWhiteSpace(payload);
                }
                _cache.TryRemove(key, out _);
            }
            return false;
        }

        internal static string BuildCacheKey(ObjectReadRequest req)
        {
            string target = req.Target?.Trim() ?? string.Empty;
            string part = req.PartName?.Trim().ToLowerInvariant() ?? "source";
            string client = req.ClientFormat?.Trim().ToLowerInvariant() ?? "mcp";
            int offset = req.Offset ?? -1;
            int limit = req.Limit ?? -1;
            int min = req.Minimize ? 1 : 0;
            // Type and response shape are part of the identity: homonyms and
            // full/multi-part reads must never reuse a different request's result.
            string parts = req.RequestedParts == null ? string.Empty : new JArray(req.RequestedParts).ToString(Newtonsoft.Json.Formatting.None);
            return $"{target}|{req.Guid}|{req.EntityKey}|{req.Path}|{part}|{offset}|{limit}|{client}|{min}|{req.TypeFilter?.Trim()}|{req.FullObject}|{parts}";
        }

        internal static bool IsPatternSettingsRead(ObjectReadRequest request)
        {
            bool IsSettings(string value) => string.Equals(value?.Trim().Replace(" ", ""), "PatternSettings", StringComparison.OrdinalIgnoreCase);
            bool untypedIdentity = string.IsNullOrWhiteSpace(request.TypeFilter)
                && (!string.IsNullOrWhiteSpace(request.Guid) || !string.IsNullOrWhiteSpace(request.EntityKey)
                    || Guid.TryParse(request.Target, out _));
            return IsSettings(request.TypeFilter) || IsSettings(request.PartName)
                || (request.RequestedParts?.Any(IsSettings) ?? false)
                || IsSettings(request.Target?.Split(':')[0]) || untypedIdentity;
        }

        private static bool IsCacheable(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return false;
            try
            {
                var obj = JObject.Parse(json);
                return obj["error"] == null;
            }
            catch
            {
                return false;
            }
        }
    }
}
