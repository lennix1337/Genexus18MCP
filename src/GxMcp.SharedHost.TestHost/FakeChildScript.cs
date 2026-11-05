using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using GxMcp.Common;
using Newtonsoft.Json.Linq;

namespace GxMcp.SharedHost.TestHost
{
    // The fake child's behavior, read from --fake-script or GXMCP_TESTHOST_SCRIPT.
    // The format is documented in README.md; unknown keys are rejected so a typo in a
    // harness script fails loudly instead of silently running the defaults.
    internal sealed class FakeChildScript
    {
        internal const string EnvironmentVariable = "GXMCP_TESTHOST_SCRIPT";
        internal const string ArgumentName = "--fake-script";

        internal const int MaxDelayMs = 600000;
        internal const int MaxDurationMs = 3600000;
        internal const int MaxWorkingSetMb = 1024;

        internal int ReadyDelayMs { get; private set; }
        internal bool Handshake { get; private set; } = true;
        internal int WorkingSetMb { get; private set; }
        internal int DefaultDurationMs { get; private set; }
        internal IReadOnlyDictionary<string, int> Durations { get; private set; } = new Dictionary<string, int>(StringComparer.Ordinal);

        // Resolution order: the --fake-script argument, then the environment variable,
        // then the defaults. A value starting with '@' names a UTF-8 JSON file.
        internal static bool TryResolve(string[] args, Func<string, string> environment, out FakeChildScript script, out string error)
        {
            script = null;
            error = string.Empty;
            string raw = null;
            string source = "defaults";
            args = args ?? new string[0];
            for (int i = 0; i < args.Length; i++)
            {
                if (!string.Equals(args[i], ArgumentName, StringComparison.OrdinalIgnoreCase)) continue;
                if (i + 1 >= args.Length)
                {
                    error = ArgumentName + " requires a JSON object or @<path>.";
                    return false;
                }
                raw = args[i + 1];
                source = ArgumentName;
                break;
            }
            if (raw == null)
            {
                string fromEnvironment = environment?.Invoke(EnvironmentVariable);
                if (!string.IsNullOrWhiteSpace(fromEnvironment))
                {
                    raw = fromEnvironment;
                    source = EnvironmentVariable;
                }
            }
            if (raw == null)
            {
                script = new FakeChildScript();
                return true;
            }

            string json = raw.Trim();
            if (json.StartsWith("@", StringComparison.Ordinal))
            {
                string path = json.Substring(1);
                try { json = File.ReadAllText(path); }
                catch (Exception ex)
                {
                    error = source + ": cannot read script file '" + path + "': " + ex.Message;
                    return false;
                }
            }

            JObject document;
            try { document = JsonIngress.ParseObject(json); }
            catch (Exception ex)
            {
                error = source + ": script is not a JSON object: " + ex.Message;
                return false;
            }
            return TryParse(document, source, out script, out error);
        }

        internal static bool TryParse(JObject document, string source, out FakeChildScript script, out string error)
        {
            script = null;
            error = string.Empty;
            var parsed = new FakeChildScript();
            var durations = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (JProperty property in document.Properties())
            {
                switch (property.Name)
                {
                    case "readyDelayMs":
                        if (!TryReadInt(property, 0, MaxDelayMs, out int ready, out error)) { error = source + ": " + error; return false; }
                        parsed.ReadyDelayMs = ready;
                        break;
                    case "handshake":
                        if (property.Value.Type != JTokenType.Boolean) { error = source + ": handshake must be true or false."; return false; }
                        parsed.Handshake = property.Value.Value<bool>();
                        break;
                    case "workingSetMb":
                        if (!TryReadInt(property, 0, MaxWorkingSetMb, out int mb, out error)) { error = source + ": " + error; return false; }
                        parsed.WorkingSetMb = mb;
                        break;
                    case "defaultDurationMs":
                        if (!TryReadInt(property, 0, MaxDurationMs, out int fallback, out error)) { error = source + ": " + error; return false; }
                        parsed.DefaultDurationMs = fallback;
                        break;
                    case "durations":
                        if (!(property.Value is JObject table)) { error = source + ": durations must be an object of method -> milliseconds."; return false; }
                        foreach (JProperty entry in table.Properties())
                        {
                            if (string.IsNullOrWhiteSpace(entry.Name)) { error = source + ": durations has an empty method name."; return false; }
                            if (!TryReadInt(entry, 0, MaxDurationMs, out int ms, out error)) { error = source + ": durations." + error; return false; }
                            durations[entry.Name] = ms;
                        }
                        break;
                    default:
                        error = source + ": unknown script key '" + property.Name + "'.";
                        return false;
                }
            }
            parsed.Durations = durations;
            script = parsed;
            return true;
        }

        // The scripted duration for one request: params.fakeDurationMs, then the method
        // entry in durations, then defaultDurationMs.
        internal int ResolveDuration(string method, JObject parameters)
        {
            JToken perRequest = parameters?["fakeDurationMs"];
            if (perRequest != null && perRequest.Type == JTokenType.Integer)
            {
                long value = perRequest.Value<long>();
                if (value >= 0 && value <= MaxDurationMs) return (int)value;
            }
            if (method != null && Durations.TryGetValue(method, out int scripted)) return scripted;
            return DefaultDurationMs;
        }

        private static bool TryReadInt(JProperty property, int min, int max, out int value, out string error)
        {
            value = 0;
            error = string.Empty;
            if (property.Value.Type == JTokenType.Integer)
            {
                long raw = property.Value.Value<long>();
                if (raw >= min && raw <= max)
                {
                    value = (int)raw;
                    return true;
                }
            }
            error = property.Name + " must be an integer in [" + min.ToString(CultureInfo.InvariantCulture) + ", "
                + max.ToString(CultureInfo.InvariantCulture) + "]; got " + property.Value.ToString(Newtonsoft.Json.Formatting.None) + ".";
            return false;
        }
    }
}
