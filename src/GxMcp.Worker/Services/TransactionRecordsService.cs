using System;
using System.Collections.Generic;
using System.Collections;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Artech.Genexus.Common.Objects;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// Typed CRUD for application rows represented by a GeneXus Transaction.
    ///
    /// The SDK remains the source of truth for the table, attributes, keys and
    /// scalar types. Values are always parameters; callers cannot inject a table,
    /// column or predicate. Writes verify before committing a serializable ADO
    /// transaction. Post-commit divergence requires explicit recovery: value
    /// equality alone cannot distinguish a concurrently deleted/recreated row.
    /// </summary>
    public sealed class TransactionRecordsService
    {
        private const int DefaultLimit = 100;
        private const int MaxLimit = 1000;
        private const int DefaultCommandTimeoutSeconds = 15;

        private readonly KbService _kbService;
        private readonly ObjectService _objectService;
        private readonly Func<string, TransactionMetadata> _metadataResolver;
        private readonly Func<JObject, DatabaseMetadata> _databaseResolver;

        // Trusted internal seam for ADO integration tests; never populated from client JSON.
        internal TransactionRecordsService(Func<string, TransactionMetadata> metadataResolver, Func<JObject, DatabaseMetadata> databaseResolver)
        {
            _metadataResolver = metadataResolver;
            _databaseResolver = databaseResolver;
        }

        private TransactionMetadata ResolveMetadata(string name)
        {
            if (_metadataResolver != null) return _metadataResolver(name);
            var transaction = _objectService?.FindObject(name, "Transaction") as Transaction;
            return transaction == null ? null : ReadMetadata(transaction);
        }

        public TransactionRecordsService(KbService kbService, ObjectService objectService)
        {
            _kbService = kbService;
            _objectService = objectService;
        }

        public string Execute(string action, string target, JObject args)
        {
            try
            {
                args = args ?? new JObject();
                string transactionName = FirstText(args, "transaction", "name") ?? target;
                if (string.IsNullOrWhiteSpace(transactionName))
                    return Error("TransactionRequired", "Transaction name is required.", "Provide transaction or name.");

                var metadata = ResolveMetadata(transactionName);
                if (metadata == null)
                    return Error("TransactionNotFound", "The requested Transaction could not be resolved by the SDK.", "Read the Transaction with type=Transaction and retry.", transactionName);

                if (metadata.Attributes.Count == 0)
                    return Error("TransactionSchemaUnavailable", "The SDK returned no root-level attributes for the Transaction.", "The operation currently supports the root level of a Transaction.", metadata.Name);

                if (string.Equals(action, "QueryRecords", StringComparison.OrdinalIgnoreCase))
                    return Query(metadata, args);
                if (string.Equals(action, "InsertRecord", StringComparison.OrdinalIgnoreCase))
                    return Write(metadata, args, isInsert: true);
                if (string.Equals(action, "UpdateRecords", StringComparison.OrdinalIgnoreCase))
                    return Write(metadata, args, isInsert: false);

                return Error("InvalidTransactionRecordsAction", "Unsupported Transaction records action.", "Use records_query, records_insert or records_update.", metadata.Name);
            }
            catch (RecordOperationException ex)
            {
                return Error(ex.Code, ex.Message, ex.Hint, target, ex.Extra);
            }
            catch (DbException ex)
            {
                Logger.Error("[TRANSACTION-RECORDS] datastore failure: " + ex.GetType().Name);
                return Error("TransactionRecordsDatabaseFailed", "The datastore rejected the Transaction records operation.", "Check the active datastore and retry; no GeneXus lifecycle action was run.", target,
                    new JObject
                    {
                        ["persisted"] = false,
                        ["rereadConfirmed"] = false,
                        ["diagnostic"] = "The database provider returned an error; connection details were omitted."
                    });
            }
            catch (Exception ex)
            {
                Logger.Error("[TRANSACTION-RECORDS] unexpected failure: " + ex.GetType().Name);
                return Error("TransactionRecordsFailed", "The Transaction records operation failed.", "Inspect the datastore diagnostics and retry with a fresh version token.", target,
                    new JObject { ["diagnostic"] = "Unexpected backend failure; sensitive connection details were omitted." });
            }
        }

        private string Query(TransactionMetadata metadata, JObject args)
        {
            var filterObject = ReadObject(args, "where", "filters");
            var filters = filterObject == null ? null : NormalizeValues(metadata, filterObject);
            var fields = ResolveFields(metadata, args["fields"] as JArray);
            int limit = ClampLimit(args["limit"]?.Value<int?>() ?? DefaultLimit);
            var db = OpenDatabase(args);
            using (var connection = db.Factory.CreateConnection())
            {
                db.Bind(metadata);
                connection.ConnectionString = db.ConnectionString;
                connection.Open();
                using (var command = BuildSelect(connection, metadata, fields, filters, limit + 1, null, db))
                {
                    command.CommandTimeout = ReadTimeout(args);
                    var rows = ReadRows(command, fields);
                    var result = BuildReadResult(metadata, db, fields, filters, rows, limit);
                    return McpResponse.Ok(target: metadata.Name, code: "TransactionRecordsRead", result: result);
                }
            }
        }

        // Approval receipts contain no source or row cache. Only a digest of the
        // reviewed plan is retained, for 15 minutes, and consumed before mutation.
        private static readonly object PreviewLock = new object();
        private static readonly Dictionary<string, PreviewApproval> Previews = new Dictionary<string, PreviewApproval>();
        private sealed class PreviewApproval
        {
            public string Digest;
            public DateTime Expires;
        }

        private string Write(TransactionMetadata metadata, JObject args, bool isInsert)
        {
            bool dryRun = args["dryRun"] == null || args["dryRun"].Value<bool>();
            bool rollbackOnFailure = args["rollbackOnFailure"]?.Value<bool?>() ?? true;
            string expectedVersion = FirstText(args, "expectedVersion", "versionToken");
            var filters = ReadObject(args, "where", "filters");
            var values = ReadObject(args, "values", "data", "record");
            if (values == null || values.Count == 0)
                throw new RecordOperationException("RecordValuesRequired", "Record values are required.", "Provide values as an object.");
            if (!isInsert && (filters == null || filters.Count == 0))
                throw new RecordOperationException("UpdateFilterRequired", "An update requires an explicit equality filter.", "Provide a unique where filter.");
            if (isInsert && filters != null && filters.Count != 0)
                throw new RecordOperationException("InsertFilterUnsupported", "Insert scope is determined by its primary key.", "Remove where from the insert.");
            if (!IsWriteAllowed(dryRun, expectedVersion))
                throw new RecordOperationException("DryRunRequired", "A current write preview is required.", "Run the same operation with dryRun=true; query and v1 tokens cannot authorize writes.");
            var normalizedValues = NormalizeValues(metadata, values);
            var normalizedFilters = filters == null ? null : NormalizeValues(metadata, filters);
            if (metadata.Keys.Count == 0)
                throw new RecordOperationException("TransactionPrimaryKeyUnavailable", "The Transaction does not expose a primary key.", "Record writes require a primary key.");
            if (!isInsert && metadata.Keys.Any(k => normalizedValues.ContainsKey(k.Name)))
                throw new RecordOperationException("KeyMutationNotSupported", "Primary-key changes are not supported.", "Update only non-key attributes.");
            if (metadata.Keys.Any(k => normalizedValues.ContainsKey(k.Name) && normalizedValues[k.Name].Type == JTokenType.Null))
                throw new RecordOperationException("PrimaryKeyRequired", "Primary keys cannot be null.", "Supply non-null key values.");
            int expectedCount = args["expectedCount"]?.Value<int?>() ?? (isInsert ? 0 : 1);
            if (expectedCount != (isInsert ? 0 : 1))
                throw new RecordOperationException("SingleRowUpdateRequired", "The adapter inserts one absent key or updates exactly one row.", "Use expectedCount=0 for insert or expectedCount=1 for update.");
            var managedFields = ResolveManagedFields(metadata, args, normalizedValues);
            var db = OpenDatabase(args);
            db.Bind(metadata);
            var missingKeys = metadata.Keys.Where(k => !normalizedValues.ContainsKey(k.Name)).ToList();
            if (isInsert && (missingKeys.Count > 1 || (missingKeys.Count == 1 && db.Family != "sqlserver")))
                throw new RecordOperationException("GeneratedKeyUnavailable", "The provider cannot safely return this generated primary key.", "Supply the complete primary key, or use SQL Server with one generated key.");
            bool generatedIdentity = isInsert && missingKeys.Count == 1;
            var scope = isInsert ? (generatedIdentity ? null : BuildKeyFilter(metadata, normalizedValues)) : normalizedFilters;
            bool commitAttempted = false;
            bool commitConfirmed = false;
            bool rollbackAttempted = false;
            bool rollbackConfirmed = false;
            List<JObject> snapshot = null;
            List<JObject> expectedAfter = null;
            List<JObject> persisted = null;
            int timeout = ReadTimeout(args);
            try
            {
                using (var connection = db.Factory.CreateConnection())
                {
                    connection.ConnectionString = db.ConnectionString;
                    connection.Open();
                    using (var tx = connection.BeginTransaction(IsolationLevel.Serializable))
                    {
                        try
                        {
                            snapshot = generatedIdentity ? new List<JObject>() : SelectRows(connection, tx, db, metadata, scope, timeout);
                            if (isInsert && snapshot.Count != 0)
                                throw new RecordOperationException("RecordAlreadyExists", "The insert primary key already exists.", "Review the existing record before creating a new preview.",
                                    new JObject { ["persisted"] = false });
                            if (!isInsert && snapshot.Count != 1)
                                throw new RecordOperationException("ExpectedCountMismatch", "The update must match exactly one record.", "Use a unique where filter.",
                                    new JObject { ["matchedCount"] = snapshot.Count, ["matchedCountExact"] = snapshot.Count < 2, ["persisted"] = false });
                            string digest = ComputePlanDigest(metadata, db, connection, isInsert, normalizedFilters, normalizedValues,
                                managedFields, snapshot, expectedCount, rollbackOnFailure);
                            if (dryRun)
                            {
                                // Ending a read-only transaction writes no application data.
                                tx.Rollback();
                                string receipt = IssuePreview(digest);
                                var preview = BuildDryRunResult(metadata, db, isInsert, normalizedFilters, normalizedValues,
                                    snapshot, receipt, expectedCount, rollbackOnFailure);
                                preview["databaseManagedFields"] = new JArray(managedFields.OrderBy(x => x, StringComparer.Ordinal));
                                preview["versionTokenKind"] = "write-preview-v2";
                                preview["previewExpiresInSeconds"] = 900;
                                preview["previewSingleUse"] = true;
                                return McpResponse.Ok(target: metadata.Name, code: "TransactionRecordDryRun", result: preview);
                            }
                            ConsumePreview(expectedVersion, digest);
                            JToken generatedKey;
                            ExecuteWrite(connection, tx, db, isInsert, normalizedFilters, normalizedValues, snapshot, out generatedKey, timeout);
                            if (generatedIdentity)
                            {
                                if (generatedKey == null || generatedKey.Type == JTokenType.Null)
                                    throw new RecordOperationException("GeneratedKeyUnavailable", "The datastore did not return the generated key.", "Supply the primary key explicitly.");
                                normalizedValues[missingKeys[0].Name] = NormalizeToken(generatedKey, missingKeys[0]);
                            }
                            var finalScope = isInsert ? BuildKeyFilter(metadata, normalizedValues) : BuildKeyFilterForRows(metadata, snapshot);
                            expectedAfter = SelectRows(connection, tx, db, metadata, finalScope, timeout);
                            if (!VerifyRows(metadata, isInsert, normalizedValues, snapshot, expectedAfter, managedFields))
                                throw new RecordOperationException("WriteVerificationFailed", "The write did not round-trip inside the transaction.", "Inspect the values and explicitly declare database-managed fields where appropriate.");
                            commitAttempted = true;
                            tx.Commit();
                            commitConfirmed = true;
                        }
                        catch
                        {
                            if (!commitAttempted)
                            {
                                rollbackAttempted = true;
                                try { tx.Rollback(); rollbackConfirmed = true; } catch { }
                            }
                            throw;
                        }
                    }
                }

                var finalFilters = isInsert ? BuildKeyFilter(metadata, normalizedValues) : BuildKeyFilterForRows(metadata, snapshot);
                using (var reread = db.Factory.CreateConnection())
                {
                    reread.ConnectionString = db.ConnectionString;
                    reread.Open();
                    persisted = SelectRows(reread, null, db, metadata, finalFilters, timeout);
                }
                // Compare the complete observed state, including database-managed fields.
                if (!RowsEquivalent(expectedAfter, persisted))
                {
                    // Never compensate after observing divergence. Even an equal
                    // subsequent row may be another writer's replacement (ABA).
                    // Only the still-open original transaction can roll back safely.
                    return Error("PostCommitDivergence", "The committed record changed before confirmation.",
                        "Do not retry or restore automatically. Inspect the current keys and obtain a new preview for explicit recovery.", metadata.Name,
                        new JObject
                        {
                            ["persisted"] = JValue.CreateNull(),
                            ["commitState"] = "Confirmed", ["commitConfirmed"] = true,
                            ["persistenceState"] = "Diverged",
                            ["rereadConfirmed"] = false, ["retrySafe"] = false,
                            ["rollbackAttempted"] = false, ["stateRestored"] = false,
                            ["rollbackDiagnostic"] = rollbackOnFailure ? "ConcurrentChangePreserved" : "NotRequested",
                            ["automaticCompensationSupported"] = false,
                            ["keys"] = BuildKeys(metadata, expectedAfter)
                        });
                }
                return McpResponse.Ok(target: metadata.Name, code: isInsert ? "TransactionRecordInserted" : "TransactionRecordsUpdated",
                    result: new JObject
                    {
                        ["transaction"] = metadata.Name, ["table"] = metadata.Table,
                        ["persisted"] = true, ["commitState"] = "Confirmed", ["commitConfirmed"] = true,
                        ["persistenceState"] = "Confirmed", ["rereadConfirmed"] = true, ["retrySafe"] = false,
                        ["rollbackAttempted"] = false, ["stateRestored"] = false,
                        ["versionTokenBefore"] = expectedVersion,
                        ["versionToken"] = ComputeVersionToken(metadata, finalFilters, persisted),
                        ["versionTokenKind"] = "read-only", ["writePreviewRequired"] = true,
                        ["matchedCount"] = persisted.Count, ["matchedCountExact"] = true,
                        ["records"] = new JArray(persisted), ["keys"] = BuildKeys(metadata, persisted)
                    });
            }
            catch (Exception ex)
            {
                // Once Commit is attempted, an exception cannot prove non-persistence.
                // Never leak provider messages or suggest blindly repeating an insert.
                var known = ex as RecordOperationException;
                var extra = known?.Extra == null ? new JObject() : (JObject)known.Extra.DeepClone();
                extra["persisted"] = commitConfirmed ? (JToken)true : commitAttempted || (rollbackAttempted && !rollbackConfirmed) ? JValue.CreateNull() : (JToken)false;
                extra["commitState"] = commitConfirmed ? "Confirmed" : commitAttempted ? "Indeterminate" : "NotAttempted";
                extra["commitConfirmed"] = commitConfirmed;
                extra["persistenceState"] = commitConfirmed ? "CommittedUnverified" : commitAttempted || (rollbackAttempted && !rollbackConfirmed) ? "Indeterminate" : "NotPersisted";
                extra["rereadConfirmed"] = false;
                extra["retrySafe"] = false;
                extra["rollbackAttempted"] = rollbackAttempted;
                extra["stateRestored"] = rollbackConfirmed;
                if (expectedAfter != null) extra["keys"] = BuildKeys(metadata, expectedAfter);
                return Error(commitConfirmed ? "PostCommitReadFailed" : commitAttempted ? "CommitOutcomeUnknown" : known?.Code ?? "TransactionRecordsDatabaseFailed",
                    commitConfirmed ? "Commit succeeded but persisted state could not be confirmed." : commitAttempted ? "The commit outcome is unknown." : known?.Message ?? "The record operation failed.",
                    "Do not retry automatically. Inspect the current record state and obtain a new dry-run preview.", metadata.Name, extra);
            }
        }

        private static List<JObject> SelectRows(DbConnection connection, DbTransaction tx, DatabaseMetadata db,
            TransactionMetadata metadata, Dictionary<string, JToken> filters, int timeout)
        {
            using (var command = BuildSelect(connection, metadata, metadata.Attributes, filters, 2, tx, db))
            {
                command.CommandTimeout = timeout;
                return ReadRows(command, metadata.Attributes);
            }
        }

        private static HashSet<string> ResolveManagedFields(TransactionMetadata metadata, JObject args, Dictionary<string, JToken> values)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var requested = args["databaseManagedFields"];
            if (requested == null) return result;
            if (!(requested is JArray array))
                throw new RecordOperationException("InvalidDatabaseManagedFields", "databaseManagedFields must be an array.", "Supply existing non-key attributes not present in values.");
            foreach (var name in array)
            {
                var attr = name.Type == JTokenType.String
                    ? metadata.Attributes.FirstOrDefault(a => string.Equals(a.Name, name.Value<string>(), StringComparison.OrdinalIgnoreCase)) : null;
                if (attr == null || attr.IsKey || values.ContainsKey(attr.Name))
                    throw new RecordOperationException("InvalidDatabaseManagedFields", "A database-managed field is unknown, a key, or explicitly assigned.", "Supply existing non-key attributes not present in values.");
                result.Add(attr.Name);
            }
            return result;
        }

        private static string ComputePlanDigest(TransactionMetadata metadata, DatabaseMetadata db, DbConnection connection, bool insert,
            Dictionary<string, JToken> filters, Dictionary<string, JToken> values, HashSet<string> managedFields,
            List<JObject> snapshot, int expectedCount, bool rollback)
        {
            if (string.IsNullOrWhiteSpace(db.KbIdentity) || string.IsNullOrWhiteSpace(db.EnvironmentIdentity) || string.IsNullOrWhiteSpace(metadata.Identity))
                throw new RecordOperationException("DestinationIdentityUnavailable", "The SDK did not expose a stable destination identity.", "Resolve the KB, environment and Transaction identity before writing.");
            return Hash(Canonical(new JObject
            {
                ["kb"] = db.KbIdentity, ["environment"] = db.EnvironmentIdentity, ["datastore"] = db.Name,
                ["provider"] = db.Factory.GetType().AssemblyQualifiedName, ["family"] = db.Family,
                ["server"] = connection.DataSource, ["database"] = connection.Database,
                // The digest is private: credentials and connection details never enter responses.
                ["connectionContext"] = Hash(db.ConnectionString), ["schemaTable"] = db.QualifiedTable,
                ["object"] = metadata.Identity, ["schemaState"] = ComputeVersionToken(metadata, filters, snapshot),
                ["operation"] = insert ? "insert" : "update", ["values"] = ToObject(values),
                ["databaseManagedFields"] = new JArray(managedFields.OrderBy(x => x, StringComparer.Ordinal)),
                ["expectedCount"] = expectedCount, ["rollbackOnFailure"] = rollback
            }));
        }

        private static string Hash(string value)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? ""))).Replace("-", "").ToLowerInvariant();
        }

        private static string IssuePreview(string digest)
        {
            lock (PreviewLock)
            {
                foreach (var expired in Previews.Where(p => p.Value.Expires <= DateTime.UtcNow).Select(p => p.Key).ToList())
                    Previews.Remove(expired);
                if (Previews.Count >= 1024) Previews.Remove(Previews.OrderBy(p => p.Value.Expires).First().Key);
                string token = "trn-v2:" + Guid.NewGuid().ToString("N");
                Previews.Add(token, new PreviewApproval { Digest = digest, Expires = DateTime.UtcNow.AddMinutes(15) });
                return token;
            }
        }

        private static void ConsumePreview(string token, string digest)
        {
            lock (PreviewLock)
            {
                if (!Previews.TryGetValue(token, out var preview) || preview.Expires <= DateTime.UtcNow)
                    throw new RecordOperationException("DryRunRequired", "The preview is missing, expired, consumed or belongs to another Worker.", "Create a new dry-run preview.");
                // Any submitted attempt consumes the receipt, even a conflicting plan.
                Previews.Remove(token);
                if (!string.Equals(preview.Digest, digest, StringComparison.Ordinal))
                    throw new RecordOperationException("VersionConflict", "The destination, plan or scoped records changed after preview.", "Review a new dry-run; the previous approval cannot authorize this write.");
            }
        }

        private static void ExecuteWrite(DbConnection connection, DbTransaction tx, DatabaseMetadata db, bool isInsert, Dictionary<string, JToken> filters,
            Dictionary<string, JToken> values, List<JObject> before, out JToken generatedKey, int timeout)
        {
            generatedKey = null;
            var command = connection.CreateCommand();
            command.Transaction = tx;
            command.CommandTimeout = timeout;
            try
            {
                if (isInsert)
                {
                    var missingKeys = db.Keys.Where(k => !values.ContainsKey(k.Name)).ToList();
                    if (missingKeys.Count > 1)
                        throw new RecordOperationException("GeneratedKeyUnavailable", "An insert with multiple missing primary-key values cannot be rolled back safely.", "Provide the complete primary key in values.");
                    if (missingKeys.Count == 1 && !string.Equals(db.Family, "sqlserver", StringComparison.OrdinalIgnoreCase))
                        throw new RecordOperationException("GeneratedKeyUnavailable", "This datastore cannot return a generated primary key through the native provider adapter.", "Provide the primary key in values or use a provider with generated-key support.");

                    var columns = values.Keys.Select(name => db.AttributeMap[name]).ToList();
                    string prefix = ParameterPrefix(db.Family);
                    var keyOutput = missingKeys.Count == 1 ? AddGeneratedKeyOutput(command, missingKeys[0]) : null;
                    // OUTPUT INTO supports enabled triggers; direct OUTPUT does not.
                    // Match the table variable to the typed output parameter: SQL
                    // Server does not implicitly convert sql_variant to scalar outputs.
                    string output = missingKeys.Count == 1 ? " OUTPUT INSERTED." + QuoteIdentifier(missingKeys[0].Name, db.Family) + " INTO @generatedKeys (Value)" : string.Empty;
                    command.CommandText = (keyOutput != null ? "DECLARE @generatedKeys TABLE (Value " + GeneratedKeySqlType(keyOutput) + "); " : "")
                        + "INSERT INTO " + db.QualifiedTable + " (" + string.Join(", ", columns.Select(a => QuoteIdentifier(a.Name, db.Family))) + ")" + output + " VALUES ("
                        + string.Join(", ", columns.Select((a, i) => AddParameter(command, prefix, "v" + i, a, values[a.Name]))) + ")"
                        + (missingKeys.Count == 1 ? "; SELECT @generatedKey = Value FROM @generatedKeys" : "");
                    // Triggers may emit arbitrary result sets. Read only the typed
                    // output parameter after the whole batch has completed.
                    command.ExecuteNonQuery();
                    generatedKey = keyOutput == null ? null : ToJsonToken(keyOutput.Value);
                    return;
                }

                var updateColumns = values.Keys.Select(name => db.AttributeMap[name]).ToList();
                string parameterPrefix = ParameterPrefix(db.Family);
                command.CommandText = "UPDATE " + db.QualifiedTable + " SET "
                    + string.Join(", ", updateColumns.Select((a, i) => QuoteIdentifier(a.Name, db.Family) + "=" + AddParameter(command, parameterPrefix, "v" + i, a, values[a.Name])))
                    + BuildWhere(command, db, filters, parameterPrefix, "w");
                int affected = ExecuteAffected(command, db);
                if (affected != before.Count)
                    throw new RecordOperationException("ConcurrentWriteDetected", "The update affected a different number of rows than the locked snapshot.", "Retry with the versionToken returned by a fresh dry-run.");
            }
            finally { command.Dispose(); }
        }

        private static int ExecuteAffected(DbCommand command, DatabaseMetadata db)
        {
            if (db.Family != "sqlserver") return command.ExecuteNonQuery();
            // ExecuteNonQuery includes trigger row counts and can return -1 under
            // NOCOUNT. @@ROWCOUNT identifies the outer statement's affected rows.
            var affected = AddOutputParameter(command, "@affectedRows", DbType.Int32);
            command.CommandText += "; SET @affectedRows = @@ROWCOUNT";
            command.ExecuteNonQuery();
            if (affected.Value == null || affected.Value == DBNull.Value)
                throw new RecordOperationException("AffectedRowsUnavailable", "The datastore did not return the statement row count.", "Inspect the provider before obtaining a new preview.");
            return Convert.ToInt32(affected.Value, CultureInfo.InvariantCulture);
        }

        private static DbParameter AddOutputParameter(DbCommand command, string name, DbType type)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Direction = ParameterDirection.Output;
            parameter.DbType = type;
            parameter.Value = DBNull.Value;
            command.Parameters.Add(parameter);
            return parameter;
        }

        private static DbParameter AddGeneratedKeyOutput(DbCommand command, AttributeMetadata key)
        {
            string type = (key.Type ?? "").ToUpperInvariant();
            DbType dbType;
            if (type.Contains("NUMERIC") || type.Contains("PACKED") || type.Contains("ZONED") || type.Contains("DECIMAL")) dbType = DbType.Decimal;
            else if (type.Contains("INT")) dbType = DbType.Int64;
            else if (type.Contains("DATETIME")) dbType = DbType.DateTime2;
            else if (type.Contains("DATE")) dbType = DbType.Date;
            else if (type.Contains("BOOLEAN") || type == "BIT") dbType = DbType.Boolean;
            else if (type.Contains("GUID")) dbType = DbType.Guid;
            else if (type.Contains("CHAR")) dbType = DbType.String;
            else throw new RecordOperationException("GeneratedKeyTypeUnsupported", "The SDK key type has no supported output parameter mapping.", "Provide the primary key explicitly.");
            var parameter = AddOutputParameter(command, "@generatedKey", dbType);
            if (dbType == DbType.String) parameter.Size = key.Length > 0 ? key.Length : 4000;
            if (dbType == DbType.Decimal)
            {
                parameter.Precision = (byte)(key.Length > 0 ? Math.Min(key.Length, 38) : 38);
                parameter.Scale = (byte)Math.Max(0, Math.Min(key.Decimals, parameter.Precision));
            }
            return parameter;
        }

        private static string GeneratedKeySqlType(DbParameter parameter)
        {
            // Closed mapping from SDK-derived DbType; no caller-supplied SQL types.
            switch (parameter.DbType)
            {
                case DbType.Int64: return "bigint";
                case DbType.Decimal: return "decimal(" + parameter.Precision.ToString(CultureInfo.InvariantCulture)
                        + "," + parameter.Scale.ToString(CultureInfo.InvariantCulture) + ")";
                case DbType.DateTime2: return "datetime2(7)";
                case DbType.Date: return "date";
                case DbType.Boolean: return "bit";
                case DbType.Guid: return "uniqueidentifier";
                case DbType.String: return "nvarchar(" + (parameter.Size > 4000 ? "max" : parameter.Size.ToString(CultureInfo.InvariantCulture)) + ")";
                default: throw new InvalidOperationException("Unsupported generated-key output type.");
            }
        }


        private static DbCommand BuildSelect(DbConnection connection, TransactionMetadata metadata, IList<AttributeMetadata> fields,
            Dictionary<string, JToken> filters, int limit, DbTransaction tx, DatabaseMetadata db)
        {
            var command = connection.CreateCommand();
            command.Transaction = tx;
            string selectLimit = string.Empty;
            string suffix = string.Empty;
            if (limit > 0 && db.Family == "sqlserver") selectLimit = "TOP " + limit.ToString(CultureInfo.InvariantCulture) + " ";
            else if (limit > 0 && db.Family == "oracle") suffix = " FETCH FIRST " + limit.ToString(CultureInfo.InvariantCulture) + " ROWS ONLY";
            else if (limit > 0 && (db.Family == "postgres" || db.Family == "mysql")) suffix = " LIMIT " + limit.ToString(CultureInfo.InvariantCulture);
            command.CommandText = "SELECT " + selectLimit + string.Join(", ", fields.Select(a => QuoteIdentifier(a.Name, db.Family)))
                + " FROM " + db.QualifiedTable + BuildWhere(command, db, filters, ParameterPrefix(db.Family), "f") + suffix;
            return command;
        }

        private static string BuildWhere(DbCommand command, DatabaseMetadata db, Dictionary<string, JToken> filters, string prefix, string parameterStem)
        {
            if (filters == null || filters.Count == 0) return string.Empty;
            var clauses = new List<string>();
            int index = 0;
            foreach (var pair in filters.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            {
                var attribute = db.AttributeMap[pair.Key];
                if (pair.Value == null || pair.Value.Type == JTokenType.Null)
                {
                    clauses.Add(QuoteIdentifier(attribute.Name, db.Family) + " IS NULL");
                }
                else
                {
                    string parameter = AddParameter(command, prefix, parameterStem + index.ToString(CultureInfo.InvariantCulture), attribute, pair.Value);
                    clauses.Add(QuoteIdentifier(attribute.Name, db.Family) + "=" + parameter);
                    index++;
                }
            }
            return " WHERE " + string.Join(" AND ", clauses);
        }

        private static List<JObject> ReadRows(DbCommand command, IList<AttributeMetadata> fields)
        {
            var rows = new List<JObject>();
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    var row = new JObject();
                    for (int i = 0; i < fields.Count; i++) row[fields[i].Name] = ToJsonToken(reader.IsDBNull(i) ? null : reader.GetValue(i));
                    rows.Add(row);
                }
            }
            return rows;
        }

        private static JObject BuildReadResult(TransactionMetadata metadata, DatabaseMetadata db, IList<AttributeMetadata> fields,
            Dictionary<string, JToken> filters, List<JObject> rows, int limit)
        {
            bool truncated = rows.Count > limit;
            if (truncated) rows.RemoveRange(limit, rows.Count - limit);
            return new JObject
            {
                ["transaction"] = metadata.Name,
                ["table"] = metadata.Table,
                ["dataStore"] = db.Name,
                ["versionTokenKind"] = "read-only",
                ["writePreviewRequired"] = true,
                ["fields"] = new JArray(fields.Select(a => a.Name)),
                ["records"] = new JArray(rows),
                ["matchedCount"] = rows.Count,
                ["limit"] = limit,
                ["truncated"] = truncated,
                ["matchedCountExact"] = !truncated,
                ["versionToken"] = ComputeVersionToken(metadata, filters, rows),
                ["keys"] = BuildKeys(metadata, rows)
            };
        }

        private static JObject BuildDryRunResult(TransactionMetadata metadata, DatabaseMetadata db, bool isInsert,
            Dictionary<string, JToken> filters, Dictionary<string, JToken> values, List<JObject> before, string version, int expectedCount, bool rollbackOnFailure)
        {
            var diff = new JObject
            {
                ["operation"] = isInsert ? "insert" : "update",
                ["matchedCount"] = isInsert ? 0 : before.Count,
                ["matchedCountExact"] = true,
                ["expectedCount"] = expectedCount,
                ["changedFields"] = new JArray(values.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)),
                ["values"] = ToObject(values)
            };
            if (!isInsert) diff["keys"] = BuildKeys(metadata, before);
            return new JObject
            {
                ["transaction"] = metadata.Name,
                ["table"] = metadata.Table,
                ["dataStore"] = db.Name,
                ["action"] = isInsert ? "records_insert" : "records_update",
                ["persisted"] = false,
                ["rereadConfirmed"] = false,
                ["rollbackOnFailure"] = rollbackOnFailure,
                ["diff"] = diff,
                ["versionToken"] = version
            };
        }

        private static bool VerifyRows(TransactionMetadata metadata, bool isInsert, Dictionary<string, JToken> values,
            List<JObject> before, List<JObject> after, HashSet<string> managedFields)
        {
            if (after == null || after.Count != 1) return false;
            if (isInsert)
            {
                return after.Any(row => values.All(pair => ValueEquals(row[pair.Key], pair.Value)));
            }
            if (before == null || before.Count != after.Count) return false;
            foreach (var oldRow in before)
            {
                var current = after.FirstOrDefault(row => KeysEqual(metadata, oldRow, row));
                if (current == null) return false;
                foreach (var attribute in metadata.Attributes)
                {
                    if (managedFields.Contains(attribute.Name)) continue;
                    JToken expected = values.ContainsKey(attribute.Name) ? values[attribute.Name] : oldRow[attribute.Name];
                    if (!ValueEquals(current[attribute.Name], expected)) return false;
                }
            }
            return true;
        }

        private static bool RowsEquivalent(List<JObject> expected, List<JObject> actual)
        {
            if (expected == null || actual == null || expected.Count != actual.Count) return false;
            var left = expected.Select(r => Canonical(r)).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            var right = actual.Select(r => Canonical(r)).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            return left.SequenceEqual(right, StringComparer.Ordinal);
        }

        private static bool KeysEqual(TransactionMetadata metadata, JObject left, JObject right)
            => metadata.Keys.All(k => ValueEquals(left[k.Name], right[k.Name]));

        private static JObject BuildKeys(TransactionMetadata metadata, IEnumerable<JObject> rows)
        {
            var result = new JObject();
            foreach (var key in metadata.Keys)
            {
                var values = new JArray();
                foreach (var row in rows ?? Enumerable.Empty<JObject>()) values.Add(row[key.Name]);
                result[key.Name] = values.Count == 1 ? values[0] : values;
            }
            return result;
        }

        private static Dictionary<string, JToken> BuildKeyFilter(TransactionMetadata metadata, Dictionary<string, JToken> values)
        {
            if (metadata.Keys.Any(key => !values.ContainsKey(key.Name)))
                throw new RecordOperationException("PrimaryKeyRequired", "The operation needs a complete primary key to verify and roll back the row.", "Supply every key attribute in values.");
            return metadata.Keys.ToDictionary(key => key.Name, key => values[key.Name], StringComparer.OrdinalIgnoreCase);
        }

        private static Dictionary<string, JToken> BuildKeyFilterForRows(TransactionMetadata metadata, IEnumerable<JObject> rows)
        {
            var list = (rows ?? Enumerable.Empty<JObject>()).ToList();
            if (list.Count != 1) throw new RecordOperationException("CompositeRollbackScopeUnsupported", "Rollback requires one identifiable row per operation.", "Keep expectedCount=1 for update or provide an explicit unique key filter.");
            return BuildKeyFilterForRow(metadata, list[0]);
        }

        private static Dictionary<string, JToken> BuildKeyFilterForRow(TransactionMetadata metadata, JObject row)
            => metadata.Keys.ToDictionary(key => key.Name, key => row[key.Name], StringComparer.OrdinalIgnoreCase);

        private static Dictionary<string, JToken> BuildKeyFilterForRow(DatabaseMetadata db, JObject row)
            => db.Keys.ToDictionary(key => key.Name, key => row[key.Name], StringComparer.OrdinalIgnoreCase);

        private static TransactionMetadata ReadMetadata(Transaction transaction)
        {
            dynamic root = transaction.Structure?.Root;
            if (root == null) throw new RecordOperationException("TransactionSchemaUnavailable", "The Transaction has no root structure.", "Read the Transaction structure and retry.");
            string table = TryString(() => root.AssociatedTable?.Name) ?? transaction.Name;
            var attributes = new List<AttributeMetadata>();
            foreach (dynamic item in (IEnumerable)root.Attributes)
            {
                string name = TryString(() => item.Attribute?.Name) ?? TryString(() => item.Name);
                if (string.IsNullOrWhiteSpace(name)) continue;
                string type = TryString(() => item.Attribute?.Type?.ToString()) ?? "";
                bool isKey = TryBool(() => item.IsKey);
                int length = TryInt(() => item.Attribute?.Length);
                int decimals = TryInt(() => item.Attribute?.Decimals);
                attributes.Add(new AttributeMetadata { Name = name, Type = type, Length = length, Decimals = decimals, IsKey = isKey });
            }
            var keys = attributes.Where(a => a.IsKey).ToList();
            return new TransactionMetadata { Identity = transaction.Guid.ToString("D"), Name = transaction.Name, Table = table, Attributes = attributes, Keys = keys };
        }

        private DatabaseMetadata OpenDatabase(JObject args)
        {
            if (_databaseResolver != null) return _databaseResolver(args);
            dynamic kb = _kbService?.GetKB();
            if (kb == null) throw new RecordOperationException("KbNotOpen", "No KB is currently open.", "Open the KB before accessing Transaction records.");
            string requested = FirstText(args, "dataStore", "datastore");
            dynamic first = null;
            dynamic selected = null;
            foreach (dynamic ds in DatabaseInfoService.EnumerateViaDataStoresPart(kb))
            {
                if (ds == null) continue;
                if (first == null) first = ds;
                bool isDefault = TryBool(() => ds.IsDefault);
                string name = FirstDynamicString(ds, "Name", "Category.Name", "Type");
                if ((!string.IsNullOrWhiteSpace(requested) && string.Equals(requested, name, StringComparison.OrdinalIgnoreCase))
                    || (string.IsNullOrWhiteSpace(requested) && isDefault)) { selected = ds; break; }
            }
            if (selected == null && string.IsNullOrWhiteSpace(requested)) selected = first;
            if (selected == null)
                throw new RecordOperationException("DataStoreNotFound", "The requested GeneXus datastore was not found in the active environment.", "Use the exact dataStore name returned by the datastore inspection.", requested);

            string provider = FirstDynamicProperty(selected, "ADONET_DRIVER", "Provider", "AdoNetProvider");
            string family = DetectFamily(provider, TryInt(() => selected.Dbms));
            if (family == "unknown")
                throw new RecordOperationException("DataStoreProviderUnsupported", "The active datastore provider could not be mapped to a supported SQL dialect.", "Use SQL Server or Oracle with a registered ADO.NET provider.");
            string connectionString = FirstConnectionString(selected, "CONNECTION_STRING", "ConnectionString", "CS_CONNECTIONSTRING", "DS_DBMS_ADDINFO", "DBMS_ADDINFO");
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                string server = FirstDynamicProperty(selected, "CS_SERVER", "ServerName", "Server");
                string database = FirstDynamicProperty(selected, "CS_DBNAME", "CS_DATABASE", "DBNAME", "DATABASE", "DATABASE_NAME", "DB_NAME");
                string schema = FirstDynamicProperty(selected, "CS_SCHEMA", "DatabaseSchema", "Schema");
                string user = FirstDynamicProperty(selected, "USER_ID", "UserId", "User");
                string password = FirstDynamicProperty(selected, "USER_PASSWORD", "PASSWORD", "Password");
                bool integrated = ParseYesNo(FirstDynamicProperty(selected, "TRUSTED_CONNECTION", "INTEGRATED_SECURITY", "IntegratedSecurity"));
                if (string.IsNullOrWhiteSpace(server) || (family != "oracle" && string.IsNullOrWhiteSpace(database)))
                    throw new RecordOperationException("DataStoreConnectionUnavailable", "The selected datastore does not expose enough connection metadata for a safe native record operation.", "Use a datastore with server/database metadata; credentials and connection strings are never returned by this tool.");
                if (family == "sqlserver")
                {
                    connectionString = "Server=" + server + ";Initial Catalog=" + database + ";" + (integrated ? "Integrated Security=SSPI;" : "User ID=" + user + ";Password=" + password + ";") + "Application Name=GeneXusMCP;Connect Timeout=15";
                }
                else if (family == "oracle")
                {
                    connectionString = "Data Source=" + server + ";User Id=" + user + ";Password=" + password + ";Connection Timeout=15";
                }
                else
                {
                    throw new RecordOperationException("DataStoreProviderUnsupported", "The active datastore provider is not supported by the native Transaction records adapter.", "Use SQL Server or Oracle with an ADO.NET provider registered in the worker.");
                }
            }
            var factory = ResolveFactory(provider, family);
            string schemaName = FirstDynamicProperty(selected, "CS_SCHEMA", "DatabaseSchema", "Schema");
            return new DatabaseMetadata
            {
                Name = FirstDynamicString(selected, "Name", "Category.Name", "Type") ?? "default",
                Family = family,
                Factory = factory,
                ConnectionString = connectionString,
                Schema = schemaName,
                KbIdentity = _kbService.GetKbPath(),
                EnvironmentIdentity = _kbService.GetActiveEnvironment()
            };
        }

        private static DbProviderFactory ResolveFactory(string provider, string family)
        {
            if (family == "sqlserver") return System.Data.SqlClient.SqlClientFactory.Instance;
            if (family == "oracle")
            {
                var oracle = Type.GetType("Oracle.ManagedDataAccess.Client.OracleClientFactory, Oracle.ManagedDataAccess", false);
                var instance = oracle?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null, null) as DbProviderFactory;
                if (instance != null) return instance;
            }
            if (!string.IsNullOrWhiteSpace(provider))
            {
                try { return DbProviderFactories.GetFactory(provider); } catch { }
                var type = Type.GetType(provider, false);
                var instance = type?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null, null) as DbProviderFactory;
                if (instance != null) return instance;
            }
            throw new RecordOperationException("DataStoreProviderUnavailable", "The ADO.NET provider for the selected datastore is not available in the worker process.", "Install/register the provider used by the GeneXus environment before retrying.");
        }

        private static string DetectFamily(string provider, int dbms)
        {
            string p = provider ?? string.Empty;
            if (p.IndexOf("oracle", StringComparison.OrdinalIgnoreCase) >= 0) return "oracle";
            if (p.IndexOf("sqlclient", StringComparison.OrdinalIgnoreCase) >= 0 || p.IndexOf("sql server", StringComparison.OrdinalIgnoreCase) >= 0) return "sqlserver";
            if (p.IndexOf("mysql", StringComparison.OrdinalIgnoreCase) >= 0) return "mysql";
            if (p.IndexOf("npgsql", StringComparison.OrdinalIgnoreCase) >= 0 || p.IndexOf("postgres", StringComparison.OrdinalIgnoreCase) >= 0) return "postgres";
            switch (dbms)
            {
                case 1: case 12: return "sqlserver";
                case 4: case 7: return "oracle";
                case 5: return "mysql";
                case 6: return "postgres";
                default: return "unknown";
            }
        }

        private static bool LooksLikeConnectionString(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.IndexOf('=') < 0) return false;
            string text = value.ToLowerInvariant();
            return text.Contains("server=") || text.Contains("data source=") || text.Contains("host=")
                || text.Contains("user id=") || text.Contains("uid=") || text.Contains("integrated security=");
        }

        private static Dictionary<string, JToken> NormalizeValues(TransactionMetadata metadata, JObject input)
        {
            var result = new Dictionary<string, JToken>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in input.Properties())
            {
                var attr = metadata.Attributes.FirstOrDefault(a => string.Equals(a.Name, property.Name, StringComparison.OrdinalIgnoreCase));
                if (attr == null) throw new RecordOperationException("TransactionAttributeNotFound", "The requested attribute is not part of the Transaction root structure.", "Use the attribute names returned by the Transaction metadata.", property.Name);
                result[attr.Name] = NormalizeToken(property.Value, attr);
            }
            return result;
        }

        private static JToken NormalizeToken(JToken token, AttributeMetadata attr)
        {
            if (token == null || token.Type == JTokenType.Null) return JValue.CreateNull();
            object value;
            try
            {
                string text = token.Type == JTokenType.String ? token.Value<string>() : token.ToString(Formatting.None);
                string type = (attr.Type ?? string.Empty).ToUpperInvariant();
                if (type.Contains("NUMERIC") || type.Contains("PACKED") || type.Contains("ZONED") || type.Contains("DECIMAL")) value = decimal.Parse(text, NumberStyles.Number, CultureInfo.InvariantCulture);
                else if (type.Contains("INT")) value = long.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
                else if (type.Contains("DATE") && !type.Contains("DATETIME")) value = DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal).Date;
                else if (type.Contains("DATETIME")) value = DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                else if (type.Contains("BOOLEAN") || type == "BIT") value = ParseBoolean(text);
                else if (type.Contains("GUID")) value = Guid.Parse(text);
                else value = token.Type == JTokenType.String ? (object)text : token.ToObject<object>();
                if (value is string s && attr.Length > 0 && s.Length > attr.Length) throw new FormatException("value exceeds the GeneXus attribute length");
                return ToJsonToken(value);
            }
            catch (Exception ex) { throw new RecordOperationException("InvalidTransactionValue", "A record value does not match the SDK type or length of the attribute.", "Correct the value using the Transaction metadata.", attr.Name, ex); }
        }

        private static string AddParameter(DbCommand command, string prefix, string name, AttributeMetadata attr, JToken token)
        {
            string parameterName = prefix + name;
            var parameter = command.CreateParameter();
            parameter.ParameterName = parameterName;
            parameter.Value = ToDbValue(token, attr);
            command.Parameters.Add(parameter);
            return parameterName;
        }

        private static object ToDbValue(JToken token, AttributeMetadata attr)
        {
            if (token == null || token.Type == JTokenType.Null) return DBNull.Value;
            string type = (attr.Type ?? string.Empty).ToUpperInvariant();
            if (type.Contains("NUMERIC") || type.Contains("PACKED") || type.Contains("ZONED") || type.Contains("DECIMAL")) return token.Value<decimal>();
            if (type.Contains("INT")) return token.Value<long>();
            if (type.Contains("DATE") && !type.Contains("DATETIME")) return token.Value<DateTime>().Date;
            if (type.Contains("DATETIME")) return token.Value<DateTime>();
            if (type.Contains("BOOLEAN") || type == "BIT") return token.Value<bool>();
            if (type.Contains("GUID")) return Guid.Parse(token.ToString());
            return token.Type == JTokenType.String ? token.Value<string>() : token.ToObject<object>();
        }

        private static string ComputeVersionToken(TransactionMetadata metadata, Dictionary<string, JToken> filters, IEnumerable<JObject> rows)
        {
            var payload = new JObject
            {
                ["transaction"] = metadata.Name,
                ["table"] = metadata.Table,
                ["attributes"] = new JArray(metadata.Attributes.Select(a => a.Name + ":" + a.Type + ":" + a.Length + ":" + a.Decimals + ":" + a.IsKey)),
                ["where"] = ToObject(filters),
                ["rows"] = new JArray((rows ?? Enumerable.Empty<JObject>()).Select(CloneRow).OrderBy(Canonical, StringComparer.Ordinal))
            };
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(Canonical(payload)));
                return "trn-v1:" + BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        private static JObject ToObject(Dictionary<string, JToken> values)
        {
            var result = new JObject();
            if (values == null) return result;
            foreach (var pair in values.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)) result[pair.Key] = pair.Value?.DeepClone() ?? JValue.CreateNull();
            return result;
        }

        internal static string QuoteIdentifier(string name, string family)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Identifier is required.", nameof(name));
            var parts = name.Split('.');
            string quote = family == "sqlserver" ? "[" : family == "mysql" ? "`" : "\"";
            string close = family == "sqlserver" ? "]" : quote;
            return string.Join(".", parts.Select(part => quote + part.Trim().Trim('[', ']', '`', '"') + close));
        }

        internal static bool IsWriteAllowed(bool dryRun, string expectedVersion)
            => dryRun || (expectedVersion != null && expectedVersion.StartsWith("trn-v2:", StringComparison.Ordinal));

        private static string ParameterPrefix(string family) => family == "oracle" ? ":" : "@";
        private static int ClampLimit(int limit) => limit <= 0 ? DefaultLimit : Math.Min(limit, MaxLimit);
        private static int ReadTimeout(JObject args) => Math.Max(1, Math.Min(args["timeoutSeconds"]?.Value<int?>() ?? DefaultCommandTimeoutSeconds, 60));

        private static List<AttributeMetadata> ResolveFields(TransactionMetadata metadata, JArray requested)
        {
            if (requested == null || requested.Count == 0) return metadata.Attributes;
            var result = new List<AttributeMetadata>();
            foreach (var token in requested)
            {
                string name = token?.ToString();
                var attr = metadata.Attributes.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
                if (attr == null) throw new RecordOperationException("TransactionAttributeNotFound", "A requested field is not part of the Transaction root structure.", "Use the field names returned by the Transaction metadata.", name);
                if (!result.Any(a => string.Equals(a.Name, attr.Name, StringComparison.OrdinalIgnoreCase))) result.Add(attr);
            }
            // Keep identity visible in projections. Read tokens do not authorize writes.
            foreach (var key in metadata.Keys)
                if (!result.Any(a => string.Equals(a.Name, key.Name, StringComparison.OrdinalIgnoreCase))) result.Add(key);
            return result;
        }

        private static JObject ReadObject(JObject args, params string[] names)
        {
            foreach (string name in names)
                if (args[name] is JObject obj) return obj;
            return null;
        }

        private static string FirstText(JObject args, params string[] names)
        {
            foreach (string name in names) if (!string.IsNullOrWhiteSpace(args[name]?.ToString())) return args[name].ToString();
            return null;
        }

        private static string FirstDynamicProperty(dynamic target, params string[] names)
        {
            foreach (string name in names)
            {
                try
                {
                    object value = target.Properties.GetPropertyValue(name);
                    if (value != null && !string.IsNullOrWhiteSpace(value.ToString())) return value.ToString();
                }
                catch { }
            }
            return null;
        }

        private static string FirstConnectionString(dynamic target, params string[] names)
        {
            foreach (string name in names)
            {
                string value = FirstDynamicProperty(target, name);
                if (LooksLikeConnectionString(value)) return value;
            }
            return null;
        }

        private static string FirstDynamicString(dynamic target, params string[] paths)
        {
            foreach (string path in paths)
            {
                try
                {
                    object current = target;
                    foreach (string segment in path.Split('.')) current = current?.GetType().GetProperty(segment, BindingFlags.Public | BindingFlags.Instance)?.GetValue(current, null);
                    if (current != null && !string.IsNullOrWhiteSpace(current.ToString())) return current.ToString();
                }
                catch { }
            }
            return null;
        }

        private static bool ParseYesNo(string value)
            => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase) || value == "1";

        private static bool ParseBoolean(string value)
        {
            if (string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "t", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(value, "0", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "f", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "false", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "no", StringComparison.OrdinalIgnoreCase)) return false;
            throw new FormatException("invalid boolean");
        }

        private static JToken ToJsonToken(object value)
        {
            if (value == null || value == DBNull.Value) return JValue.CreateNull();
            if (value is byte[] bytes) return Convert.ToBase64String(bytes);
            if (value is Guid guid) return guid.ToString("D");
            if (value is DateTime dateTime) return dateTime.ToString("o", CultureInfo.InvariantCulture);
            if (value is DateTimeOffset dateTimeOffset) return dateTimeOffset.ToString("o", CultureInfo.InvariantCulture);
            return JToken.FromObject(value);
        }

        private static bool ValueEquals(JToken left, JToken right)
        {
            if (left == null || left.Type == JTokenType.Null) return right == null || right.Type == JTokenType.Null;
            if (right == null || right.Type == JTokenType.Null) return false;
            // SDK Numeric attributes may map to integral SQL columns. JSON's
            // integer/float distinction must not reject equal database numbers.
            if ((left.Type == JTokenType.Integer || (left as JValue)?.Value is decimal)
                && (right.Type == JTokenType.Integer || (right as JValue)?.Value is decimal))
            {
                try { return left.Value<decimal>() == right.Value<decimal>(); }
                catch (Exception ex) when (ex is OverflowException || ex is FormatException) { return false; }
            }
            if (left.Type == JTokenType.String || right.Type == JTokenType.String) return string.Equals(left.ToString(), right.ToString(), StringComparison.Ordinal);
            return JToken.DeepEquals(left, right);
        }

        private static string Canonical(JToken token)
        {
            if (token == null) return "null";
            if (token is JObject obj) return "{" + string.Join(",", obj.Properties().OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Select(p => JsonConvert.ToString(p.Name) + ":" + Canonical(p.Value))) + "}";
            if (token is JArray array) return "[" + string.Join(",", array.Select(Canonical)) + "]";
            return token.ToString(Formatting.None);
        }

        private static JObject CloneRow(JObject row) => row == null ? null : (JObject)row.DeepClone();

        private static string TryString(Func<object> getter) { try { return getter()?.ToString(); } catch { return null; } }
        private static bool TryBool(Func<object> getter) { try { return Convert.ToBoolean(getter(), CultureInfo.InvariantCulture); } catch { return false; } }
        private static int TryInt(Func<object> getter) { try { return Convert.ToInt32(getter(), CultureInfo.InvariantCulture); } catch { return 0; } }

        private static string Error(string code, string message, string hint, string target = null, JObject extra = null)
            => McpResponse.Err(code, message, hint, target: target, errorExtra: extra);

        internal sealed class RecordOperationException : Exception
        {
            public string Code { get; }
            public string Hint { get; }
            public JObject Extra { get; }
            public RecordOperationException(string code, string message, string hint, JObject extra = null, Exception inner = null) : base(message, inner) { Code = code; Hint = hint; Extra = extra; }
            public RecordOperationException(string code, string message, string hint, string target, Exception inner = null)
                : this(code, message, hint, new JObject { ["target"] = target }, inner) { }
        }

        internal sealed class TransactionMetadata
        {
            public string Identity;
            public string Name;
            public string Table;
            public List<AttributeMetadata> Attributes = new List<AttributeMetadata>();
            public List<AttributeMetadata> Keys = new List<AttributeMetadata>();
        }

        internal sealed class AttributeMetadata
        {
            public string Name;
            public string Type;
            public int Length;
            public int Decimals;
            public bool IsKey;
        }

        internal sealed class DatabaseMetadata
        {
            public string KbIdentity;
            public string EnvironmentIdentity;
            public string Name;
            public string Family;
            public string Schema;
            public DbProviderFactory Factory;
            public string ConnectionString;
            public string QualifiedTable;
            public List<AttributeMetadata> Attributes;
            public List<AttributeMetadata> Keys;
            public Dictionary<string, AttributeMetadata> AttributeMap;

            public string Table { get; set; }

            public void Bind(TransactionMetadata metadata)
            {
                Attributes = metadata.Attributes;
                Keys = metadata.Keys;
                AttributeMap = Attributes.ToDictionary(a => a.Name, StringComparer.OrdinalIgnoreCase);
                string table = string.IsNullOrWhiteSpace(Schema) ? metadata.Table : Schema + "." + metadata.Table;
                QualifiedTable = QuoteIdentifier(table, Family);
                Table = metadata.Table;
            }
        }
    }
}
