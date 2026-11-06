#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GxMcp.Common.SharedHost
{
    // The design's registry v2 state values, written verbatim into the record's "state" property.
    internal static class SharedHostRecordState
    {
        internal const string Admitting = "admitting";
        internal const string Held = "held";
        internal const string Starting = "starting";
        internal const string Ready = "ready";
        internal const string Replacing = "replacing";
        internal const string Recovering = "recovering";
        internal const string NoWorker = "no-worker";
        internal const string Refused = "refused";
        internal const string Stopping = "stopping";

        internal static readonly IReadOnlyList<string> All = new[]
        {
            Admitting, Held, Starting, Ready, Replacing, Recovering, NoWorker, Refused, Stopping
        };
    }

    // Physical KB identity components stored in a registry v2 record.
    [JsonObject(MemberSerialization.OptIn)]
    internal sealed class SharedHostRecordIdentity
    {
        [JsonProperty("key", Required = Required.DisallowNull)]
        internal string Key { get; set; } = string.Empty;

        [JsonProperty("kbPath", Required = Required.DisallowNull)]
        internal string KbPath { get; set; } = string.Empty;

        [JsonProperty("workerExecutable", Required = Required.DisallowNull)]
        internal string WorkerExecutable { get; set; } = string.Empty;

        [JsonProperty("installationPath", Required = Required.DisallowNull)]
        internal string InstallationPath { get; set; } = string.Empty;

        [JsonProperty("driver", Required = Required.DisallowNull)]
        internal string Driver { get; set; } = string.Empty;

        [JsonProperty("major", Required = Required.DisallowNull)]
        internal string Major { get; set; } = string.Empty;

        [JsonProperty("profileConfigPath")]
        internal string? ProfileConfigPath { get; set; }
    }

    // A typed admission refusal persisted in a "refused" record. Measured values are null when they do not apply
    // to the refusal reason (for example a worker-limit refusal carries no memory reading).
    [JsonObject(MemberSerialization.OptIn)]
    internal sealed class SharedHostRefusal
    {
        [JsonProperty("code", Required = Required.DisallowNull)]
        internal string Code { get; set; } = string.Empty;

        [JsonProperty("reason", Required = Required.DisallowNull)]
        internal string Reason { get; set; } = string.Empty;

        [JsonProperty("availableMb")]
        internal long? AvailableMb { get; set; }

        [JsonProperty("floorMb")]
        internal long? FloorMb { get; set; }
    }

    // Registry v2 record (%LOCALAPPDATA%\GxMcp\shared-workers\v2\<kbKey>.json). Mutable so the owning host can
    // update it in place; every property is always written, absent optional values as explicit JSON nulls.
    [JsonObject(MemberSerialization.OptIn)]
    internal sealed class SharedHostRecord
    {
        internal const int CurrentSchema = 2;

        [JsonProperty("schema")]
        internal int Schema { get; set; } = CurrentSchema;

        [JsonProperty("kbKey", Required = Required.DisallowNull)]
        internal string KbKey { get; set; } = string.Empty;

        [JsonProperty("identity", Required = Required.DisallowNull)]
        internal SharedHostRecordIdentity Identity { get; set; } = new SharedHostRecordIdentity();

        [JsonProperty("hostPid")]
        internal int HostPid { get; set; }

        [JsonProperty("hostStartTimeUtcTicks")]
        internal long HostStartTimeUtcTicks { get; set; }

        [JsonProperty("launchedByGatewayPid")]
        internal int LaunchedByGatewayPid { get; set; }

        // 0 while no Worker child exists (admitting, held, refused, no-worker).
        [JsonProperty("workerPid")]
        internal int WorkerPid { get; set; }

        [JsonProperty("workerStartTimeUtcTicks")]
        internal long WorkerStartTimeUtcTicks { get; set; }

        [JsonProperty("pipeName", Required = Required.DisallowNull)]
        internal string PipeName { get; set; } = string.Empty;

        [JsonProperty("generation")]
        internal int Generation { get; set; }

        [JsonProperty("state", Required = Required.DisallowNull)]
        internal string State { get; set; } = string.Empty;

        [JsonProperty("heldSinceUtc")]
        internal DateTime? HeldSinceUtc { get; set; }

        [JsonProperty("coldStartStartedUtc")]
        internal DateTime? ColdStartStartedUtc { get; set; }

        [JsonProperty("coldStartConcurrency")]
        internal int ColdStartConcurrency { get; set; }

        [JsonProperty("attachments")]
        internal int Attachments { get; set; }

        [JsonProperty("inFlight")]
        internal int InFlight { get; set; }

        [JsonProperty("queued")]
        internal int Queued { get; set; }

        [JsonProperty("backgroundActive")]
        internal bool BackgroundActive { get; set; }

        [JsonProperty("evictable")]
        internal bool Evictable { get; set; }

        [JsonProperty("lastActivityUtc")]
        internal DateTime? LastActivityUtc { get; set; }

        [JsonProperty("refusal")]
        internal SharedHostRefusal? Refusal { get; set; }

        [JsonProperty("updatedUtc")]
        internal DateTime UpdatedUtc { get; set; }

        internal string ToJson()
        {
            return JsonConvert.SerializeObject(this, Formatting.Indented, SharedHostRecordJson.Settings);
        }
    }

    internal static class SharedHostRecordJson
    {
        // Round-trip UTC format with seven fractional digits, matching the design's record example.
        private const string UtcDateFormat = "yyyy'-'MM'-'dd'T'HH':'mm':'ss'.'fffffff'Z'";

        internal static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Include,
            DateParseHandling = DateParseHandling.DateTime,
            DateTimeZoneHandling = DateTimeZoneHandling.Utc,
            DateFormatString = UtcDateFormat,
            Culture = CultureInfo.InvariantCulture,
            MissingMemberHandling = MissingMemberHandling.Ignore
        };

        internal static readonly JsonSerializer Serializer = JsonSerializer.Create(Settings);
    }

    internal enum RecordReadStatus
    {
        Absent,
        Unparseable,
        Parsed,

        // The file exists but could not be read (sharing violation, access denied). Classified fail-closed as
        // LivenessUnverified, never as stale, so a transient read failure cannot cause a live record's removal.
        Unreadable
    }

    // Outcome of reading one registry v2 record. Error names the failure for Unparseable and Unreadable.
    internal sealed class RecordReadResult
    {
        private RecordReadResult(RecordReadStatus status, SharedHostRecord? record, string? error)
        {
            Status = status;
            Record = record;
            Error = error;
        }

        internal static RecordReadResult Absent { get; } = new RecordReadResult(RecordReadStatus.Absent, null, null);

        internal RecordReadStatus Status { get; }

        internal SharedHostRecord? Record { get; }

        internal string? Error { get; }

        internal static RecordReadResult FromJson(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return Unparseable("record text is empty");
            }

            try
            {
                using (var stringReader = new StringReader(text))
                using (var reader = new JsonTextReader(stringReader))
                {
                    reader.DateParseHandling = DateParseHandling.DateTime;
                    reader.DateTimeZoneHandling = DateTimeZoneHandling.Utc;
                    reader.Culture = CultureInfo.InvariantCulture;

                    if (!ReadSignificant(reader))
                    {
                        return Unparseable("record text contains no JSON value");
                    }

                    if (reader.TokenType != JsonToken.StartObject)
                    {
                        return Unparseable("record root is " + reader.TokenType + ", expected an object");
                    }

                    var obj = JObject.Load(reader);
                    if (ReadSignificant(reader))
                    {
                        return Unparseable("record has additional content after the root object");
                    }

                    if (!obj.TryGetValue("schema", StringComparison.Ordinal, out var schema)
                        || schema.Type != JTokenType.Integer
                        || schema.Value<long>() != SharedHostRecord.CurrentSchema)
                    {
                        return Unparseable("record schema is missing or is not " +
                            SharedHostRecord.CurrentSchema.ToString(CultureInfo.InvariantCulture));
                    }

                    var record = obj.ToObject<SharedHostRecord>(SharedHostRecordJson.Serializer);
                    if (record == null)
                    {
                        return Unparseable("record deserialized to null");
                    }

                    if (record.HostPid <= 0)
                    {
                        return Unparseable("record hostPid must be positive");
                    }

                    if (record.HostStartTimeUtcTicks <= 0)
                    {
                        return Unparseable("record hostStartTimeUtcTicks must be positive");
                    }

                    return new RecordReadResult(RecordReadStatus.Parsed, record, null);
                }
            }
            catch (JsonException ex)
            {
                return Unparseable(ex.GetType().Name + ": " + ex.Message);
            }
            catch (FormatException ex)
            {
                return Unparseable(ex.GetType().Name + ": " + ex.Message);
            }
            catch (InvalidCastException ex)
            {
                return Unparseable(ex.GetType().Name + ": " + ex.Message);
            }
            catch (OverflowException ex)
            {
                return Unparseable(ex.GetType().Name + ": " + ex.Message);
            }
            catch (ArgumentException ex)
            {
                return Unparseable(ex.GetType().Name + ": " + ex.Message);
            }
        }

        internal static RecordReadResult ReadFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("A record path is required.", nameof(path));
            }

            string text;
            try
            {
                // Share read, write and delete so a reader never blocks the owner's temp-file-plus-replace write.
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
                {
                    text = reader.ReadToEnd();
                }
            }
            catch (FileNotFoundException)
            {
                return Absent;
            }
            catch (DirectoryNotFoundException)
            {
                return Absent;
            }
            catch (IOException ex)
            {
                return Unreadable(ex.GetType().Name + ": " + ex.Message);
            }
            catch (UnauthorizedAccessException ex)
            {
                return Unreadable(ex.GetType().Name + ": " + ex.Message);
            }

            return FromJson(text);
        }

        private static bool ReadSignificant(JsonTextReader reader)
        {
            while (reader.Read())
            {
                if (reader.TokenType != JsonToken.Comment)
                {
                    return true;
                }
            }

            return false;
        }

        private static RecordReadResult Unparseable(string error) =>
            new RecordReadResult(RecordReadStatus.Unparseable, null, error);

        private static RecordReadResult Unreadable(string error) =>
            new RecordReadResult(RecordReadStatus.Unreadable, null, error);
    }
}
