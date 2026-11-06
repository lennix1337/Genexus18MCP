#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GxMcp.Common.SharedHost
{
    // Effective New_Settings after resolution, with the source of each value and every validation error.
    // When IsValid is false the effective values are not meant to be used: a rejected setting falls back to its
    // default only so the object stays well-formed for diagnostics.
    internal sealed class SharedHostSettingsResult
    {
        internal SharedHostSettingsResult(
            int queueWaitMs,
            int queueDepth,
            int sessionQueueCap,
            int maxWorkers,
            int memoryFloorMb,
            int coldStartConcurrency,
            int attachReadyTimeoutSeconds,
            IReadOnlyDictionary<string, string> sources,
            IReadOnlyList<string> errors)
        {
            QueueWaitMs = queueWaitMs;
            QueueDepth = queueDepth;
            SessionQueueCap = sessionQueueCap;
            MaxWorkers = maxWorkers;
            MemoryFloorMb = memoryFloorMb;
            ColdStartConcurrency = coldStartConcurrency;
            AttachReadyTimeoutSeconds = attachReadyTimeoutSeconds;
            Sources = sources;
            Errors = errors;
        }

        internal bool IsValid => Errors.Count == 0;

        // One message per problem, in setting order.
        internal IReadOnlyList<string> Errors { get; }

        // Keyed by the Server.SharedHost key name. Values: "env:<VARIABLE>", "config:Server.SharedHost.<Key>",
        // "legacy-env:GXMCP_BUSY_WAIT_MS", or "default".
        internal IReadOnlyDictionary<string, string> Sources { get; }

        internal int QueueWaitMs { get; }
        internal int QueueDepth { get; }
        internal int SessionQueueCap { get; }
        internal int MaxWorkers { get; }
        internal int MemoryFloorMb { get; }
        internal int ColdStartConcurrency { get; }
        internal int AttachReadyTimeoutSeconds { get; }
    }

    // Resolves and validates the seven New_Settings of Server.SharedHost.
    // Precedence per setting: environment variable, then Server.SharedHost.<Key>, then (QueueWaitMs only) the legacy
    // GXMCP_BUSY_WAIT_MS when it is an integer in 1000-600000, then the default. Every explicit value (environment or
    // file, including a file value the environment overrides) is validated on its own.
    internal static class SharedHostSettingsResolver
    {
        internal const string SectionPath = "Server.SharedHost";
        internal const string LegacyBusyWaitVariable = "GXMCP_BUSY_WAIT_MS";
        internal const string HttpSharedGatewayMode = "http-shared";
        internal const string DefaultSource = "default";

        internal const string QueueWaitMsKey = "QueueWaitMs";
        internal const string QueueDepthKey = "QueueDepth";
        internal const string SessionQueueCapKey = "SessionQueueCap";
        internal const string MaxWorkersKey = "MaxWorkers";
        internal const string MemoryFloorMbKey = "MemoryFloorMB";
        internal const string ColdStartConcurrencyKey = "ColdStartConcurrency";
        internal const string AttachReadyTimeoutSecondsKey = "AttachReadyTimeoutSeconds";

        internal const int DefaultQueueWaitMs = 30000;
        internal const int DefaultQueueDepth = 8;
        internal const int DefaultSessionQueueCapCeiling = 4;
        internal const int DefaultMaxWorkers = 4;
        internal const int DefaultMemoryFloorMb = 1024;
        internal const int DefaultColdStartConcurrency = 1;
        internal const int DefaultAttachReadyTimeoutSeconds = 120;

        private const int QueueWaitMin = 1000;
        private const int QueueWaitMax = 600000;

        private sealed class Spec
        {
            internal Spec(string key, string envName, int min, int max, int defaultValue)
            {
                Key = key;
                EnvName = envName;
                Min = min;
                Max = max;
                DefaultValue = defaultValue;
            }

            internal string Key { get; }
            internal string EnvName { get; }
            internal string ConfigName => SectionPath + "." + Key;
            internal int Min { get; }
            internal int Max { get; }
            internal int DefaultValue { get; }
        }

        // QueueDepth must precede SessionQueueCap: the cap's allowed range depends on the effective depth.
        private static readonly Spec[] Specs =
        {
            new Spec(QueueWaitMsKey, "GXMCP_SHARED_QUEUE_WAIT_MS", QueueWaitMin, QueueWaitMax, DefaultQueueWaitMs),
            new Spec(QueueDepthKey, "GXMCP_SHARED_QUEUE_DEPTH", 1, 64, DefaultQueueDepth),
            // Max is replaced by the effective queue depth; the default is derived from it.
            new Spec(SessionQueueCapKey, "GXMCP_SHARED_SESSION_QUEUE_CAP", 1, int.MaxValue, DefaultSessionQueueCapCeiling),
            new Spec(MaxWorkersKey, "GXMCP_SHARED_MAX_WORKERS", 1, 16, DefaultMaxWorkers),
            new Spec(MemoryFloorMbKey, "GXMCP_SHARED_MEMORY_FLOOR_MB", 256, 8192, DefaultMemoryFloorMb),
            new Spec(ColdStartConcurrencyKey, "GXMCP_SHARED_COLD_START_CONCURRENCY", 1, 4, DefaultColdStartConcurrency),
            new Spec(AttachReadyTimeoutSecondsKey, "GXMCP_SHARED_ATTACH_READY_TIMEOUT_S", 10, 600, DefaultAttachReadyTimeoutSeconds),
        };

        // section: the raw Server.SharedHost JObject (null when absent); env: environment lookup.
        internal static SharedHostSettingsResult Resolve(JObject? section, bool strictDocument, string gatewayMode, Func<string, string?> env)
        {
            if (env == null) throw new ArgumentNullException(nameof(env));

            var httpShared = string.Equals((gatewayMode ?? string.Empty).Trim(), HttpSharedGatewayMode, StringComparison.OrdinalIgnoreCase);
            var errors = new List<string>();
            var values = new Dictionary<string, int>(StringComparer.Ordinal);
            var sources = new Dictionary<string, string>(StringComparer.Ordinal);
            var explicitCap = false;

            // Effective queue depth, or null when the depth itself was rejected.
            int? effectiveDepth = DefaultQueueDepth;

            foreach (var spec in Specs)
            {
                var isCap = spec.Key == SessionQueueCapKey;
                var max = spec.Max;
                var depthKnown = true;
                if (isCap)
                {
                    depthKnown = effectiveDepth.HasValue;
                    max = effectiveDepth ?? int.MaxValue;
                }

                var hasProblem = false;

                var envRaw = env(spec.EnvName);
                int? envValue = null;
                if (!string.IsNullOrEmpty(envRaw))
                {
                    envValue = ParseEnvironment(envRaw!, spec.Min, max);
                    if (!envValue.HasValue)
                    {
                        hasProblem = true;
                        errors.Add(Rejection(spec.EnvName, envRaw!, spec.Min, max, depthKnown));
                    }
                    if (httpShared) errors.Add(HttpSharedConflict(spec.EnvName));
                }
                else
                {
                    envRaw = null;
                }

                JToken? fileToken = null;
                if (section != null && section.TryGetValue(spec.Key, StringComparison.Ordinal, out var token))
                {
                    fileToken = token;
                }

                int? fileValue = null;
                if (fileToken != null)
                {
                    fileValue = ParseFile(fileToken, spec.Min, max);
                    if (!fileValue.HasValue)
                    {
                        hasProblem = true;
                        errors.Add(Rejection(spec.ConfigName, FileText(fileToken), spec.Min, max, depthKnown));
                    }
                    if (httpShared) errors.Add(HttpSharedConflict(spec.ConfigName));
                }

                if (strictDocument && envValue.HasValue && fileValue.HasValue && envValue.Value != fileValue.Value)
                {
                    hasProblem = true;
                    // With no effective depth the configuration is already invalid, so a cap conflict is not reported.
                    if (depthKnown)
                    {
                        errors.Add(string.Format(CultureInfo.InvariantCulture,
                            "{0}={1} conflicts with {2}={3} in a strict configuration document; remove one or set both to the same value.",
                            spec.EnvName, envValue.Value, spec.ConfigName, fileValue.Value));
                    }
                }

                if (isCap) explicitCap = !hasProblem && (envValue.HasValue || fileValue.HasValue);

                int value;
                string source;
                if (hasProblem)
                {
                    value = spec.DefaultValue;
                    source = DefaultSource;
                }
                else if (envValue.HasValue)
                {
                    value = envValue.Value;
                    source = "env:" + spec.EnvName;
                }
                else if (fileValue.HasValue)
                {
                    value = fileValue.Value;
                    source = "config:" + spec.ConfigName;
                }
                else if (spec.Key == QueueWaitMsKey && TryLegacyBusyWait(env, out var legacy))
                {
                    value = legacy;
                    source = "legacy-env:" + LegacyBusyWaitVariable;
                }
                else
                {
                    value = spec.DefaultValue;
                    source = DefaultSource;
                }

                if (spec.Key == QueueDepthKey) effectiveDepth = hasProblem ? (int?)null : value;

                values[spec.Key] = value;
                sources[spec.Key] = source;
            }

            if (!explicitCap)
            {
                values[SessionQueueCapKey] = Math.Min(DefaultSessionQueueCapCeiling, values[QueueDepthKey]);
                sources[SessionQueueCapKey] = DefaultSource;
            }

            return new SharedHostSettingsResult(
                values[QueueWaitMsKey],
                values[QueueDepthKey],
                values[SessionQueueCapKey],
                values[MaxWorkersKey],
                values[MemoryFloorMbKey],
                values[ColdStartConcurrencyKey],
                values[AttachReadyTimeoutSecondsKey],
                sources,
                errors);
        }

        // The legacy variable never produces an error: anything but an in-range integer is ignored.
        private static bool TryLegacyBusyWait(Func<string, string?> env, out int value)
        {
            value = 0;
            var raw = env(LegacyBusyWaitVariable);
            if (string.IsNullOrEmpty(raw)) return false;
            var parsed = ParseEnvironment(raw!, QueueWaitMin, QueueWaitMax);
            if (!parsed.HasValue) return false;
            value = parsed.Value;
            return true;
        }

        private static int? ParseEnvironment(string raw, int min, int max)
        {
            if (!long.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed)) return null;
            return InRange(parsed, min, max);
        }

        // Only a JSON integer is accepted; strings, fractions, booleans, null, objects, and arrays are rejected.
        private static int? ParseFile(JToken token, int min, int max)
        {
            if (token.Type != JTokenType.Integer) return null;
            if (!(token is JValue jValue) || !(jValue.Value is long parsed)) return null; // BigInteger: out of range
            return InRange(parsed, min, max);
        }

        private static int? InRange(long value, int min, int max)
        {
            return value >= min && value <= max ? (int)value : (int?)null;
        }

        private static string FileText(JToken token)
        {
            if (token is JValue jValue)
            {
                if (jValue.Type == JTokenType.Null) return "null";
                return jValue.ToString(CultureInfo.InvariantCulture);
            }
            return token.ToString(Formatting.None);
        }

        private static string Rejection(string name, string rejected, int min, int max, bool rangeKnown)
        {
            if (!rangeKnown)
            {
                return string.Format(CultureInfo.InvariantCulture,
                    "Invalid {0} value '{1}': expected an integer of at least {2}.", name, rejected, min);
            }
            return string.Format(CultureInfo.InvariantCulture,
                "Invalid {0} value '{1}': expected an integer in the range {2}-{3}.", name, rejected, min, max);
        }

        private static string HttpSharedConflict(string name)
        {
            return name + " is set, but GatewayMode=\"http-shared\" does not support " + SectionPath
                + " settings; remove " + name + " or use a stdio GatewayMode.";
        }
    }
}
