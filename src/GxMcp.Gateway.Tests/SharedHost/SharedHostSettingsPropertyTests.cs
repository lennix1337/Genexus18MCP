#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using GxMcp.Common.SharedHost;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests.SharedHost
{
    // Feature: concurrent-multi-session-workers, Property 13: Settings resolution and validation
    //
    // Property-based test for SharedHostSettingsResolver. It uses the contract pinned by
    // SharedHostSettingsResolverTests (Resolve(section, strictDocument, gatewayMode, env), IsValid, Errors,
    // the seven effective values, Sources, the source strings, error naming, and "<min>-<max>" ranges) and
    // compares every generated configuration with an independent reference oracle written below.
    //
    // Oracle rules:
    // - Every explicitly set value (environment or file) of a New_Setting other than SessionQueueCap is validated on
    //   its own: it must be an integer within the setting's fixed range. The legacy GXMCP_BUSY_WAIT_MS is never an error.
    // - SessionQueueCap has no fixed upper bound. Its allowed range is 1 to the effective queue depth (Requirement 3
    //   criteria 12 and 15), so the QueueDepth spec is resolved first and every explicit cap value (environment or
    //   file, including a file value that the environment overrides) is validated on its own like any other setting,
    //   against the range "1-<effective depth>": a non-integer value, a value below 1, or a value above the effective
    //   depth is rejected with an error naming the source, the value, and that range. With the default depth 8 this
    //   gives "1-8" for caps 0, 9, "abc", and 2.5; with depth 12 a cap of 12 is accepted. Both match the unit tests.
    //   When the effective depth is itself unresolvable (rejected), the configuration is already invalid, so the
    //   oracle expects a cap error, naming only the source and the value, solely for a non-integer value or a value
    //   below 1, and expects no cap range, no cap above-depth error, and no cap conflict error.
    // - Strict documents reject an environment value that differs from an explicit file value when both are valid on
    //   their own; equal values pass.
    // - Precedence: environment, then Server.SharedHost.<Key>, then (QueueWaitMs only) GXMCP_BUSY_WAIT_MS when it is
    //   an integer in 1000-600000, then the default. SessionQueueCap defaults to the lower of 4 and the effective depth.
    // - GatewayMode "http-shared" (any letter case) with any explicit New_Setting is rejected, naming GatewayMode and
    //   each explicitly set source.
    // The environment is injected through the delegate; the process environment is never read or mutated.
    public class SharedHostSettingsPropertyTests
    {
        private const string PropertyName = "Feature: concurrent-multi-session-workers, Property 13: Settings resolution and validation";
        private const int Iterations = 500;
        private const string LegacyBusyWait = "GXMCP_BUSY_WAIT_MS";
        private const int LegacyMin = 1000;
        private const int LegacyMax = 600000;

        private sealed class SettingSpec
        {
            internal SettingSpec(string key, string envName, int min, int max, int? defaultValue, bool depthBounded = false)
            {
                Key = key;
                EnvName = envName;
                Min = min;
                Max = max;
                DefaultValue = defaultValue;
                DepthBounded = depthBounded;
            }

            // True for SessionQueueCap: the upper bound is the effective queue depth, and Max (the largest valid depth)
            // only feeds the generators.
            internal bool DepthBounded { get; }

            internal string Key { get; }
            internal string EnvName { get; }
            internal string ConfigName => "Server.SharedHost." + Key;
            internal int Min { get; }
            internal int Max { get; }
            internal int? DefaultValue { get; } // null: derived (SessionQueueCap)
            internal string Range => Min.ToString(CultureInfo.InvariantCulture) + "-" + Max.ToString(CultureInfo.InvariantCulture);
        }

        private static readonly IReadOnlyList<SettingSpec> Specs = new[]
        {
            new SettingSpec("QueueWaitMs", "GXMCP_SHARED_QUEUE_WAIT_MS", 1000, 600000, 30000),
            new SettingSpec("QueueDepth", "GXMCP_SHARED_QUEUE_DEPTH", 1, 64, 8),
            new SettingSpec("SessionQueueCap", "GXMCP_SHARED_SESSION_QUEUE_CAP", 1, 64, null, depthBounded: true),
            new SettingSpec("MaxWorkers", "GXMCP_SHARED_MAX_WORKERS", 1, 16, 4),
            new SettingSpec("MemoryFloorMB", "GXMCP_SHARED_MEMORY_FLOOR_MB", 256, 8192, 1024),
            new SettingSpec("ColdStartConcurrency", "GXMCP_SHARED_COLD_START_CONCURRENCY", 1, 4, 1),
            new SettingSpec("AttachReadyTimeoutSeconds", "GXMCP_SHARED_ATTACH_READY_TIMEOUT_S", 10, 600, 120),
        };

        private static readonly IReadOnlyList<string> HttpSharedModes = new[] { "http-shared", "HTTP-SHARED", "Http-Shared" };
        private static readonly IReadOnlyList<string> NonIntegerEnvValues = new[] { "abc", "2.5", "12ms", "x1" };
        private static readonly IReadOnlyList<string> NonIntegerLegacyValues = new[] { "abc", "1500.5", "15s" };

        // One generated input for one setting: the raw environment string (null when unset) and the file token
        // (null when the key is absent from Server.SharedHost).
        private sealed class SettingInput
        {
            internal SettingInput(SettingSpec spec, string? envRaw, JToken? fileToken)
            {
                Spec = spec;
                EnvRaw = envRaw;
                FileToken = fileToken;
            }

            internal SettingSpec Spec { get; }
            internal string? EnvRaw { get; }
            internal JToken? FileToken { get; }
        }

        // A problem the oracle expects: some error must contain every fragment.
        private sealed class ExpectedProblem
        {
            internal ExpectedProblem(string description, params string[] fragments)
            {
                Description = description;
                Fragments = fragments;
            }

            internal string Description { get; }
            internal IReadOnlyList<string> Fragments { get; }
        }

        private sealed class OracleResult
        {
            internal List<ExpectedProblem> Problems { get; } = new List<ExpectedProblem>();
            internal Dictionary<string, int> Values { get; } = new Dictionary<string, int>(StringComparer.Ordinal);
            internal Dictionary<string, string> Sources { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
            internal bool IsValid => Problems.Count == 0;
        }

        [Fact]
        public void Resolve_AnyCombinationOfSources_MatchesReferenceOracle()
        {
            SeededInterleavingRunner.Run(PropertyName, Iterations, c =>
            {
                var strict = c.Bool();
                // Most cases use stdio-isolated so the accepted path is reachable; the rest cover http-shared casings.
                var gatewayMode = c.Chance(0.6) ? "stdio-isolated" : c.Pick(HttpSharedModes);
                // About 40% of cases use only valid values so the accepted path is exercised often.
                var cleanCase = c.Chance(0.4);
                var legacyRaw = GenerateLegacy(c);

                var inputs = new List<SettingInput>();
                // Likely effective depth (valid environment value, else valid file value) so caps can track it.
                int? depthHint = null;
                foreach (var spec in Specs)
                {
                    var input = GenerateSetting(c, spec, strict, cleanCase, depthHint);
                    if (spec.Key == "QueueDepth")
                    {
                        depthHint = (input.EnvRaw != null ? ParseEnvInRange(input.EnvRaw, spec.Min, spec.Max) : null)
                            ?? (input.FileToken != null && input.FileToken.Type == JTokenType.Integer
                                ? InRange(input.FileToken.Value<long>(), spec.Min, spec.Max)
                                : null);
                    }
                    inputs.Add(input);
                }

                var section = BuildSection(c, inputs);
                var envMap = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var input in inputs.Where(i => i.EnvRaw != null)) envMap[input.Spec.EnvName] = input.EnvRaw!;
                if (legacyRaw != null) envMap[LegacyBusyWait] = legacyRaw;

                c.Log("strictDocument=" + strict + " gatewayMode=" + gatewayMode + " cleanCase=" + cleanCase);
                c.Log("section=" + (section == null ? "null" : section.ToString(Newtonsoft.Json.Formatting.None)));
                c.Log("env=" + (envMap.Count == 0 ? "{}" : string.Join(", ", envMap.Select(p => p.Key + "=" + p.Value))));

                var expected = Oracle(inputs, strict, gatewayMode, legacyRaw);
                c.Log("oracle: valid=" + expected.IsValid + " problems=[" + string.Join("; ", expected.Problems.Select(p => p.Description)) + "]");

                var result = SharedHostSettingsResolver.Resolve(section, strict, gatewayMode,
                    name => envMap.TryGetValue(name, out var value) ? value : null);
                var errorsText = string.Join(" | ", result.Errors);
                c.Log("resolver: valid=" + result.IsValid + " errors=[" + errorsText + "]");

                Assert.True(expected.IsValid == result.IsValid,
                    "IsValid expected " + expected.IsValid + " but was " + result.IsValid + "; errors: " + errorsText);

                if (expected.IsValid)
                {
                    Assert.Empty(result.Errors);
                    foreach (var spec in Specs)
                    {
                        Assert.True(expected.Values[spec.Key] == EffectiveValue(result, spec.Key),
                            spec.Key + ": expected value " + expected.Values[spec.Key] + " but was " + EffectiveValue(result, spec.Key));
                        Assert.True(expected.Sources[spec.Key] == result.Sources[spec.Key],
                            spec.Key + ": expected source " + expected.Sources[spec.Key] + " but was " + result.Sources[spec.Key]);
                    }
                }
                else
                {
                    Assert.NotEmpty(result.Errors);
                    foreach (var problem in expected.Problems)
                    {
                        Assert.True(result.Errors.Any(e => problem.Fragments.All(f => e.Contains(f))),
                            "No error names every fragment of the expected problem '" + problem.Description + "' ["
                            + string.Join(", ", problem.Fragments) + "]; errors: " + errorsText);
                    }
                }
            });
        }

        // ---- Generators ----

        private static SettingInput GenerateSetting(PbtCase c, SettingSpec spec, bool strict, bool cleanCase, int? depthHint)
        {
            string? envRaw = null;
            JToken? fileToken = null;

            if (c.Chance(0.4))
            {
                fileToken = cleanCase || c.Chance(0.75)
                    ? new JValue(ValidValue(c, spec, depthHint))
                    : InvalidFileToken(c, spec);
            }

            if (c.Chance(0.35))
            {
                if (cleanCase || c.Chance(0.75))
                {
                    // In strict documents with a valid file value, often reuse it so equal values are exercised.
                    if (strict && fileToken != null && fileToken.Type == JTokenType.Integer && (cleanCase || c.Chance(0.6)))
                    {
                        envRaw = fileToken.Value<int>().ToString(CultureInfo.InvariantCulture);
                    }
                    else if (!cleanCase || !strict || fileToken == null)
                    {
                        envRaw = ValidValue(c, spec, depthHint).ToString(CultureInfo.InvariantCulture);
                    }
                }
                else
                {
                    envRaw = InvalidEnvValue(c, spec);
                }
            }

            return new SettingInput(spec, envRaw, fileToken);
        }

        private static int ValidValue(PbtCase c, SettingSpec spec, int? depthHint)
        {
            if (spec.Key == "QueueDepth") return c.SmallInt(spec.Min, spec.Max);
            if (spec.DepthBounded)
            {
                // A well-formed cap (integer >= 1). Half the time it fits the likely depth, which covers accepted caps
                // above 8 when the depth is larger; otherwise it ranges up to a few above max(8, depth) and may exceed it.
                var depth = depthHint ?? 8;
                if (c.Chance(0.5)) return c.Int(spec.Min, depth);
                return c.Int(spec.Min, Math.Max(8, depth) + (c.Chance(0.3) ? 4 : 0));
            }
            return c.Int(spec.Min, spec.Max);
        }

        private static JToken InvalidFileToken(PbtCase c, SettingSpec spec)
        {
            switch (c.Int(0, 3))
            {
                case 0: return new JValue(spec.Min - 1 - c.SmallInt(0, 50));
                case 1: return new JValue(spec.Max + 1 + c.SmallInt(0, 50));
                case 2: return new JValue("abc");
                default: return new JValue(c.Int(1, 20) + 0.5);
            }
        }

        private static string InvalidEnvValue(PbtCase c, SettingSpec spec)
        {
            switch (c.Int(0, 2))
            {
                case 0: return (spec.Min - 1 - c.SmallInt(0, 50)).ToString(CultureInfo.InvariantCulture);
                case 1: return (spec.Max + 1 + c.SmallInt(0, 50)).ToString(CultureInfo.InvariantCulture);
                default: return c.Pick(NonIntegerEnvValues);
            }
        }

        private static string? GenerateLegacy(PbtCase c)
        {
            switch (c.Int(0, 4))
            {
                case 0:
                case 1: return null;
                case 2: return c.Int(LegacyMin, LegacyMax).ToString(CultureInfo.InvariantCulture);
                case 3: return c.Pick(new[] { "999", "600001", "0", "-5" });
                default: return c.Pick(NonIntegerLegacyValues);
            }
        }

        private static JObject? BuildSection(PbtCase c, IReadOnlyList<SettingInput> inputs)
        {
            var withFile = inputs.Where(i => i.FileToken != null).ToList();
            if (withFile.Count == 0 && c.Bool()) return null;
            var section = new JObject();
            foreach (var input in withFile) section[input.Spec.Key] = input.FileToken!.DeepClone();
            return section;
        }

        // ---- Reference oracle ----

        private static OracleResult Oracle(IReadOnlyList<SettingInput> inputs, bool strict, string gatewayMode, string? legacyRaw)
        {
            var oracle = new OracleResult();
            var httpShared = string.Equals(gatewayMode, "http-shared", StringComparison.OrdinalIgnoreCase);
            var effective = new Dictionary<string, (int Value, string Source, string NameInErrors, bool Explicit)?>(StringComparer.Ordinal);

            foreach (var input in inputs)
            {
                var spec = input.Spec;
                var hasProblem = false;

                // The allowed upper bound and range text of this setting. For SessionQueueCap they come from the
                // effective depth, which Specs resolves first; with no effective depth the cap is checked only for
                // being an integer of at least 1, and its errors carry no range (range == null).
                var max = spec.Max;
                string? range = spec.Range;
                if (spec.DepthBounded)
                {
                    if (!effective.TryGetValue("QueueDepth", out var depthEntry))
                    {
                        throw new InvalidOperationException("Specs must list QueueDepth before SessionQueueCap.");
                    }
                    max = depthEntry.HasValue ? depthEntry.Value.Value : int.MaxValue;
                    range = depthEntry.HasValue ? spec.Min.ToString(CultureInfo.InvariantCulture) + "-" + max.ToString(CultureInfo.InvariantCulture) : null;
                }

                int? envValue = null;
                if (input.EnvRaw != null)
                {
                    envValue = ParseEnvInRange(input.EnvRaw, spec.Min, max);
                    if (envValue == null)
                    {
                        hasProblem = true;
                        oracle.Problems.Add(Rejection(spec.EnvName, input.EnvRaw, range));
                    }
                    if (httpShared)
                    {
                        oracle.Problems.Add(new ExpectedProblem("http-shared with " + spec.EnvName, "GatewayMode", spec.EnvName));
                    }
                }

                int? fileValue = null;
                if (input.FileToken != null)
                {
                    fileValue = input.FileToken.Type == JTokenType.Integer ? InRange(input.FileToken.Value<long>(), spec.Min, max) : null;
                    if (fileValue == null)
                    {
                        hasProblem = true;
                        oracle.Problems.Add(Rejection(spec.ConfigName, FileText(input.FileToken), range));
                    }
                    if (httpShared)
                    {
                        oracle.Problems.Add(new ExpectedProblem("http-shared with " + spec.ConfigName, "GatewayMode", spec.ConfigName));
                    }
                }

                if (strict && envValue.HasValue && fileValue.HasValue && envValue.Value != fileValue.Value)
                {
                    hasProblem = true;
                    // With no effective depth the configuration is already invalid; no cap conflict error is expected.
                    if (range != null) oracle.Problems.Add(new ExpectedProblem(spec.EnvName + " conflicts with " + spec.ConfigName,
                        spec.EnvName, spec.ConfigName,
                        envValue.Value.ToString(CultureInfo.InvariantCulture), fileValue.Value.ToString(CultureInfo.InvariantCulture)));
                }

                if (hasProblem)
                {
                    effective[spec.Key] = null;
                }
                else if (envValue.HasValue)
                {
                    effective[spec.Key] = (envValue.Value, "env:" + spec.EnvName, spec.EnvName, true);
                }
                else if (fileValue.HasValue)
                {
                    effective[spec.Key] = (fileValue.Value, "config:" + spec.ConfigName, spec.ConfigName, true);
                }
                else if (spec.Key == "QueueWaitMs" && legacyRaw != null && ParseEnvInRange(legacyRaw, LegacyMin, LegacyMax) is int legacy)
                {
                    effective[spec.Key] = (legacy, "legacy-env:" + LegacyBusyWait, LegacyBusyWait, false);
                }
                else if (spec.DefaultValue.HasValue)
                {
                    effective[spec.Key] = (spec.DefaultValue.Value, "default", "default", false);
                }
                else
                {
                    effective[spec.Key] = (0, "default", "default", false); // derived below
                }
            }

            // An unset Session_Queue_Cap derives from the effective queue depth. An explicit cap was already validated
            // against "1-<effective depth>" in the loop.
            var depth = effective["QueueDepth"];
            var cap = effective["SessionQueueCap"];
            if (depth.HasValue && cap.HasValue && !cap.Value.Explicit)
            {
                effective["SessionQueueCap"] = (Math.Min(4, depth.Value.Value), "default", "default", false);
            }

            if (oracle.IsValid)
            {
                foreach (var pair in effective)
                {
                    oracle.Values[pair.Key] = pair.Value!.Value.Value;
                    oracle.Sources[pair.Key] = pair.Value!.Value.Source;
                }
            }
            return oracle;
        }

        // A rejected explicit value: the error names the source and the rejected text and, when known, the range.
        private static ExpectedProblem Rejection(string name, string text, string? range) => range == null
            ? new ExpectedProblem(name + " invalid (no effective depth)", name, text)
            : new ExpectedProblem(name + " invalid", name, text, range);

        // Generated environment strings are canonical: an optional minus sign and digits, or a clearly
        // non-integer text, so a plain invariant integer parse decides them unambiguously.
        private static int? ParseEnvInRange(string raw, int min, int max)
        {
            if (!long.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed)) return null;
            return InRange(parsed, min, max);
        }

        private static int? InRange(long value, int min, int max) => value >= min && value <= max ? (int)value : (int?)null;

        private static string FileText(JToken token)
        {
            if (token.Type == JTokenType.Float) return token.Value<double>().ToString(CultureInfo.InvariantCulture);
            if (token.Type == JTokenType.Integer) return token.Value<long>().ToString(CultureInfo.InvariantCulture);
            return token.Value<string>() ?? token.ToString();
        }

        private static int EffectiveValue(SharedHostSettingsResult result, string key)
        {
            switch (key)
            {
                case "QueueWaitMs": return result.QueueWaitMs;
                case "QueueDepth": return result.QueueDepth;
                case "SessionQueueCap": return result.SessionQueueCap;
                case "MaxWorkers": return result.MaxWorkers;
                case "MemoryFloorMB": return result.MemoryFloorMb;
                case "ColdStartConcurrency": return result.ColdStartConcurrency;
                case "AttachReadyTimeoutSeconds": return result.AttachReadyTimeoutSeconds;
                default: throw new ArgumentOutOfRangeException(nameof(key), key, "Unknown New_Setting key.");
            }
        }
    }
}
