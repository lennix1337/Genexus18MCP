using System;
using System.Data;
using System.Linq;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;
using Xunit.Abstractions;
using static GxMcp.Worker.Services.TransactionRecordsService;

namespace GxMcp.Worker.Tests
{
    public class TransactionRecordsServiceTests
    {
        private readonly ITestOutputHelper _output;
        public TransactionRecordsServiceTests(ITestOutputHelper output) { _output = output; }
        private sealed class Fixture
        {
            internal readonly TransactionRecordsFakeDatabase Db = new TransactionRecordsFakeDatabase();
            internal readonly TransactionMetadata Metadata;
            internal readonly DatabaseMetadata Database;
            internal readonly TransactionRecordsService Service;
            internal Fixture(int count = 1)
            {
                var key = new AttributeMetadata { Name = "Id", Type = "INT", IsKey = true };
                Metadata = new TransactionMetadata { Identity = "synthetic-object", Name = "SyntheticRecord", Table = "SyntheticRecord" };
                Metadata.Attributes.AddRange(new[] { key, new AttributeMetadata { Name = "Value", Type = "VARCHAR", Length = 100 },
                    new AttributeMetadata { Name = "Stamp", Type = "INT" } });
                Metadata.Keys.Add(key);
                Database = new DatabaseMetadata { KbIdentity = "synthetic-kb", EnvironmentIdentity = "synthetic-env",
                    Name = "Default", Family = "sqlserver", Schema = "dbo", Factory = Db, ConnectionString = "synthetic" };
                Service = new TransactionRecordsService(_ => Metadata, _ => Database);
                Db.Rows.AddRange(Enumerable.Range(1, count).Select(i => TransactionRecordsFakeDatabase.Row(i)));
            }
            internal JObject Execute(string action, JObject args) => JObject.Parse(Service.Execute(action, "SyntheticRecord", args));
            internal string Preview(string action, JObject args)
            {
                args["dryRun"] = true;
                var response = Execute(action, args);
                Assert.True(response["status"].Value<string>() == "ok", response.ToString());
                Assert.False(response["result"]["persisted"].Value<bool>());
                return response["result"]["versionToken"].Value<string>();
            }
            internal JObject Write(string action, JObject args, string token)
            {
                args["dryRun"] = false; args["expectedVersion"] = token;
                return Execute(action, args);
            }
        }
        private static JObject Update() => JObject.Parse("{'where':{'Id':1},'values':{'Value':'new'}}");
        private static JObject Insert(bool identity = false)
            => JObject.Parse(identity ? "{'values':{'Value':'new'}}" : "{'values':{'Id':100001,'Value':'new'}}");
        private static JToken Error(JObject response, string code)
        {
            Assert.Equal("error", response["status"].Value<string>());
            Assert.Equal(code, response["error"]["code"].Value<string>());
            return response["error"];
        }
        [Fact]
        public void OnlyIssuedPreviewV2AuthorizesWriting()
        {
            Assert.False(IsWriteAllowed(false, null));
            Assert.False(IsWriteAllowed(false, "trn-v1:current"));
            Assert.True(IsWriteAllowed(false, "trn-v2:receipt"));
            Assert.True(IsWriteAllowed(true, null));
            var f = new Fixture();
            Error(f.Write("UpdateRecords", Update(), "trn-v2:invented"), "DryRunRequired");
            Assert.Equal(0, f.Db.Writes);
        }
        [Theory]
        [InlineData("sqlserver", "[sales].[Order]")]
        [InlineData("oracle", "\"sales\".\"Order\"")]
        public void IdentifiersAreQuotedByProvider(string family, string expected)
            => Assert.Equal(expected, QuoteIdentifier("sales.Order", family));

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void InsertOnLargeTableReadsOnlyItsOwnScope(bool identity)
        {
            var f = new Fixture(10000);
            var args = Insert(identity);
            string token = f.Preview("InsertRecord", args);
            Assert.Equal(identity ? 0 : 1, f.Db.Selects);
            Assert.Equal(0, f.Db.SelectedRows);
            Assert.Equal(0, f.Db.Writes);
            Assert.Equal(10000, f.Db.Rows.Count);
            f.Db.ResetCounters();
            var result = f.Write("InsertRecord", args, token);
            Assert.True(result["status"].Value<string>() == "ok", result.ToString());
            Assert.True(result["result"]["rereadConfirmed"].Value<bool>());
            Assert.Equal(10001, f.Db.Rows.Count);
            Assert.Equal(identity ? 2 : 3, f.Db.Selects);
            Assert.Equal(2, f.Db.SelectedRows);
            Assert.Equal(1, f.Db.Writes);
            Assert.Equal(1, f.Db.Transactions);
            Assert.All(f.Db.Commands.Where(c => c.StartsWith("SELECT ")), c => Assert.Contains("TOP 2 ", c));
            _output.WriteLine("Insert identity={0}: {1} SELECTs, {2} returned rows, {3} mutation, {4} serializable transaction; initial table: 10000.",
                identity, f.Db.Selects, f.Db.SelectedRows, f.Db.Writes, f.Db.Transactions);
        }
        [Fact]
        public void BroadUpdateStopsAtSecondRecord()
        {
            var f = new Fixture(10000);
            var error = Error(f.Execute("UpdateRecords", JObject.Parse("{'where':{'Value':'old'},'values':{'Value':'new'}}")), "ExpectedCountMismatch");
            Assert.Equal(2, error["matchedCount"].Value<int>());
            Assert.False(error["matchedCountExact"].Value<bool>());
            Assert.Equal(2, f.Db.SelectedRows);
            Assert.Equal(0, f.Db.Writes);
            Assert.All(f.Db.Rows, r => Assert.Equal("old", r["Value"].Value<string>()));
        }
        [Fact]
        public void UpdateUsesOneTransactionAndThreeReadRows()
        {
            var f = new Fixture(); var args = Update();
            string token = f.Preview("UpdateRecords", args);
            Assert.Equal(0, f.Db.Writes);
            f.Db.ResetCounters();
            var result = f.Write("UpdateRecords", args, token);
            Assert.True(result["status"].Value<string>() == "ok", result.ToString());
            Assert.Equal("new", f.Db.Rows[0]["Value"].Value<string>());
            Assert.Equal(3, f.Db.Selects);
            Assert.Equal(3, f.Db.SelectedRows);
            Assert.Equal(1, f.Db.Transactions);
            Assert.Equal(1, f.Db.Commits);
        }
        [Theory]
        [InlineData("values")][InlineData("filter")][InlineData("row")][InlineData("kb")]
        [InlineData("environment")][InlineData("datastore")][InlineData("schema")][InlineData("object")]
        [InlineData("server")][InlineData("database")][InlineData("connection")][InlineData("metadata")]
        [InlineData("rollback")][InlineData("managed")]
        public void PreviewIsBoundToDestinationPlanAndSnapshot(string change)
        {
            var f = new Fixture(); var args = Update(); string token = f.Preview("UpdateRecords", args);
            switch (change)
            {
                case "values": args["values"]["Value"] = "different"; break;
                case "filter": args["where"]["Stamp"] = 0; break;
                case "row": f.Db.Rows[0]["Stamp"] = 1; break;
                case "kb": f.Database.KbIdentity += "B"; break;
                case "environment": f.Database.EnvironmentIdentity += "B"; break;
                case "datastore": f.Database.Name += "B"; break;
                case "schema": f.Database.Schema += "B"; break;
                case "object": f.Metadata.Identity += "B"; break;
                case "server": f.Db.Server += "B"; break;
                case "database": f.Db.Catalog += "B"; break;
                case "connection": f.Database.ConnectionString += "B"; break;
                case "metadata": f.Metadata.Attributes[1].Length++; break;
                case "rollback": args["rollbackOnFailure"] = false; break;
                case "managed": args["databaseManagedFields"] = new JArray("Stamp"); break;
            }
            Error(f.Write("UpdateRecords", args, token), "VersionConflict");
            Assert.Equal(0, f.Db.Writes);
        }
        [Fact]
        public void InsertIgnoresUnrelatedChangesButChecksItsKey()
        {
            var f = new Fixture(100); var args = Insert();
            string token = f.Preview("InsertRecord", args);
            f.Db.Rows[0]["Value"] = "concurrent-unrelated";
            Assert.Equal("ok", f.Write("InsertRecord", args, token)["status"].Value<string>());
            Assert.Equal("concurrent-unrelated", f.Db.Rows[0]["Value"].Value<string>());
            Error(f.Execute("InsertRecord", Insert()), "RecordAlreadyExists");
        }
        [Fact]
        public void InsertRejectsKeyCreatedAfterPreview()
        {
            var f = new Fixture(); var args = Insert(); string token = f.Preview("InsertRecord", args);
            f.Db.Rows.Add(TransactionRecordsFakeDatabase.Row(100001));
            Error(f.Write("InsertRecord", args, token), "RecordAlreadyExists");
            Assert.Equal(0, f.Db.Writes);
        }
        [Fact]
        public void GeneratedInsertReceiptCannotBeReplayed()
        {
            var f = new Fixture(); var args = Insert(true); string token = f.Preview("InsertRecord", args);
            Assert.Equal("ok", f.Write("InsertRecord", args, token)["status"].Value<string>());
            Error(f.Write("InsertRecord", args, token), "DryRunRequired");
            Assert.Equal(1, f.Db.Writes);
            Assert.Equal(2, f.Db.Rows.Count);
        }

        [Fact]
        public void PreviewCannotAuthorizeAnotherOperation()
        {
            var f = new Fixture();
            string token = f.Preview("InsertRecord", Insert(true));
            Error(f.Write("UpdateRecords", Update(), token), "VersionConflict");
            Assert.Equal(0, f.Db.Writes);
        }

        [Fact]
        public void SdkNumericKeyCanRoundTripThroughIntegralSqlColumn()
        {
            var f = new Fixture(); f.Metadata.Keys[0].Type = "NUMERIC";
            var args = Insert(); string token = f.Preview("InsertRecord", args);
            var result = f.Write("InsertRecord", args, token);
            Assert.True(result["status"].Value<string>() == "ok", result.ToString());
            Assert.Equal(100001, result["result"]["keys"]["Id"].Value<int>());
        }

        [Fact]
        public void WriteConflictConsumesReceiptAndRequiresNewPreview()
        {
            var f = new Fixture(); var args = Update();
            string token = f.Preview("UpdateRecords", args);
            args["values"]["Value"] = "different";
            Error(f.Write("UpdateRecords", args, token), "VersionConflict");
            args["values"]["Value"] = "new";
            Error(f.Write("UpdateRecords", args, token), "DryRunRequired");
            Assert.Equal(0, f.Db.Writes);
        }

        [Fact]
        public void FailedRollbackReportsUnknownPersistence()
        {
            var f = new Fixture(); var args = Update();
            string token = f.Preview("UpdateRecords", args);
            f.Db.Trigger = row => { row["Value"] = "not-requested"; return row; };
            f.Db.ThrowRollback = true;
            var response = f.Write("UpdateRecords", args, token);
            var error = Error(response, "TransactionRecordsDatabaseFailed");
            Assert.Equal(JTokenType.Null, error["persisted"].Type);
            Assert.Equal("Indeterminate", error["persistenceState"].Value<string>());
            Assert.False(error["stateRestored"].Value<bool>());
            Assert.False(error["retrySafe"].Value<bool>());
        }

        [Fact]
        public void RollbackDisabledPreservesCommitAndReportsDivergence()
        {
            var f = new Fixture(); var args = Update(); args["rollbackOnFailure"] = false;
            string token = f.Preview("UpdateRecords", args);
            f.Db.AfterCommit = db => db.Rows[0]["Value"] = "another-writer";
            var error = Error(f.Write("UpdateRecords", args, token), "PostCommitDivergence");
            Assert.False(error["rollbackAttempted"].Value<bool>());
            Assert.Equal("NotRequested", error["rollbackDiagnostic"].Value<string>());
            Assert.Equal(JTokenType.Null, error["persisted"].Type);
            Assert.Equal(1, f.Db.Writes);
        }

        [Fact]
        public void GeneratedInsertUsesTriggerCompatibleOutputAndReturnsAuditState()
        {
            var f = new Fixture(); var args = Insert(true); args["databaseManagedFields"] = new JArray("Stamp");
            string token = f.Preview("InsertRecord", args);
            f.Db.Trigger = row => { row["Stamp"] = 5; return row; };
            var result = f.Write("InsertRecord", args, token);
            Assert.Equal("ok", result["status"].Value<string>());
            Assert.Equal(5, result["result"]["records"][0]["Stamp"].Value<int>());
            Assert.Contains(f.Db.Commands, c => c.Contains("OUTPUT INSERTED.[Id] INTO @generatedKeys"));
        }

        [Fact]
        public void SqlServerChecksOuterStatementRowCount()
        {
            var f = new Fixture(); var args = Update(); string token = f.Preview("UpdateRecords", args);
            Assert.Equal("ok", f.Write("UpdateRecords", args, token)["status"].Value<string>());
            Assert.Contains(f.Db.Commands, c => c.StartsWith("UPDATE ") && c.EndsWith("; SET @affectedRows = @@ROWCOUNT"));
            Assert.Equal(DbType.Int32, f.Db.OutputParameterTypes["@affectedRows"]);
            Assert.Equal(0, f.Db.ScalarCalls);
        }

        [Theory]
        [InlineData(false, -1)]
        [InlineData(false, 999)]
        [InlineData(true, -1)]
        [InlineData(true, 999)]
        public void TriggerSelectCannotReplaceGeneratedKeyOrAffectedCount(bool insert, int nonQueryCount)
        {
            var f = new Fixture(); var args = insert ? Insert(true) : Update();
            string action = insert ? "InsertRecord" : "UpdateRecords";
            args["databaseManagedFields"] = new JArray("Stamp");
            string token = f.Preview(action, args);
            f.Db.Trigger = row => { row["Stamp"] = 5; return row; };
            f.Db.TriggerScalarResult = "SYNTHETIC_TRIGGER_RESULT_NOT_A_KEY_OR_COUNT";
            f.Db.NonQueryReturnOverride = nonQueryCount;
            var response = f.Write(action, args, token);
            Assert.True(response["status"].Value<string>() == "ok", response.ToString());
            Assert.Equal(insert ? 1000001 : 1, response["result"]["keys"]["Id"].Value<long>());
            Assert.Equal(5, response["result"]["records"][0]["Stamp"].Value<int>());
            Assert.True(response["result"]["rereadConfirmed"].Value<bool>());
            Assert.Equal(0, f.Db.ScalarCalls);
            Assert.Equal(1, f.Db.NonQueryCalls);
            Assert.Equal(insert ? DbType.Int64 : DbType.Int32,
                f.Db.OutputParameterTypes[insert ? "@generatedKey" : "@affectedRows"]);
            Assert.DoesNotContain("SYNTHETIC_TRIGGER_RESULT", response.ToString());
        }

        [Theory]
        [InlineData("INT", DbType.Int64, "bigint")]
        [InlineData("NUMERIC", DbType.Decimal, "decimal(28,0)")]
        [InlineData("GUID", DbType.Guid, "uniqueidentifier")]
        [InlineData("VARCHAR", DbType.String, "nvarchar(28)")]
        public void GeneratedKeyOutputIsTypedFromTrustedSdkMetadata(string sdkType, DbType expectedType, string sqlType)
        {
            var f = new Fixture(0); f.Metadata.Keys[0].Type = sdkType; f.Metadata.Keys[0].Length = 28;
            if (sdkType == "GUID") f.Db.GeneratedKeyOverride = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
            if (sdkType == "VARCHAR") f.Db.GeneratedKeyOverride = "synthetic-key";
            var args = Insert(true); string token = f.Preview("InsertRecord", args);
            f.Db.TriggerScalarResult = "misleading-trigger-select";
            var response = f.Write("InsertRecord", args, token);
            Assert.True(response["status"].Value<string>() == "ok", response.ToString());
            Assert.Equal(expectedType, f.Db.OutputParameterTypes["@generatedKey"]);
            Assert.Contains(f.Db.Commands, c => c.StartsWith("DECLARE @generatedKeys TABLE (Value " + sqlType + "); "));
            Assert.DoesNotContain(f.Db.Commands, c => c.Contains("sql_variant"));
            Assert.Equal(f.Db.Rows[0]["Id"].ToString(), response["result"]["keys"]["Id"].ToString());
            Assert.Equal(0, f.Db.ScalarCalls);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void MissingOutputParameterRefusesCommitEvenWhenTriggerReturnsPlausibleValue(bool insert)
        {
            var f = new Fixture(); var args = insert ? Insert(true) : Update();
            string action = insert ? "InsertRecord" : "UpdateRecords"; string token = f.Preview(action, args);
            f.Db.OmitOutputParameters = true;
            f.Db.TriggerScalarResult = insert ? 1000001L : 1L;
            var error = Error(f.Write(action, args, token), insert ? "GeneratedKeyUnavailable" : "AffectedRowsUnavailable");
            Assert.False(error["persisted"].Value<bool>());
            Assert.True(error["stateRestored"].Value<bool>());
            Assert.Equal(0, f.Db.Commits);
            Assert.Single(f.Db.Rows);
            Assert.Equal("old", f.Db.Rows[0]["Value"].Value<string>());
            Assert.Equal(0, f.Db.ScalarCalls);
        }

        [Fact]
        public void FakeRejectsSqlVariantToTypedOutputLikeSqlServerError257()
        {
            var db = new TransactionRecordsFakeDatabase();
            using (var connection = db.CreateConnection())
            {
                connection.Open();
                using (var tx = connection.BeginTransaction(IsolationLevel.Serializable))
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = tx;
                    command.CommandText = "DECLARE @generatedKeys TABLE (Value sql_variant); INSERT INTO [dbo].[SyntheticRecord] ([Value])"
                        + " OUTPUT INSERTED.[Id] INTO @generatedKeys (Value) VALUES (@v0); SELECT @generatedKey = Value FROM @generatedKeys";
                    var output = command.CreateParameter();
                    output.ParameterName = "@generatedKey";
                    output.Direction = ParameterDirection.Output;
                    output.DbType = DbType.Int64;
                    command.Parameters.Add(output);
                    var input = command.CreateParameter();
                    input.ParameterName = "@v0";
                    input.Value = "new";
                    command.Parameters.Add(input);
                    var exception = Assert.Throws<TransactionRecordsFakeDatabase.FakeDbException>(() => command.ExecuteNonQuery());
                    Assert.Equal(257, exception.Number);
                    Assert.Equal(0, db.Writes);
                    Assert.Empty(db.Rows);
                }
            }
        }

        [Fact]
        public void MissingDestinationIdentityCannotIssuePreview()
        {
            var f = new Fixture(); f.Database.EnvironmentIdentity = null;
            Error(f.Execute("InsertRecord", Insert(true)), "DestinationIdentityUnavailable");
            Assert.Equal(0, f.Db.Writes);
        }

        [Fact]
        public void ManagedFieldArrayIsValidatedStrictly()
        {
            var f = new Fixture(); var args = Update(); args["databaseManagedFields"] = "Stamp";
            Error(f.Execute("UpdateRecords", args), "InvalidDatabaseManagedFields");
            Assert.Empty(f.Db.Commands);
        }

        [Fact]
        public void ManagedFieldsNeverPermitAnUnrequestedChangeElsewhere()
        {
            var f = new Fixture(); var args = Update(); args["databaseManagedFields"] = new JArray("Stamp");
            string token = f.Preview("UpdateRecords", args);
            f.Db.Trigger = row => { row["Value"] = "wrong"; row["Stamp"] = 1; return row; };
            var error = Error(f.Write("UpdateRecords", args, token), "WriteVerificationFailed");
            Assert.True(error["stateRestored"].Value<bool>());
            Assert.Equal("old", f.Db.Rows[0]["Value"].Value<string>());
        }
        [Theory]
        [InlineData(false)][InlineData(true)]
        public void CommitExceptionNeverClaimsNonPersistence(bool afterCommit)
        {
            var f = new Fixture(); var args = Insert(true); string token = f.Preview("InsertRecord", args);
            f.Db.ThrowBeforeCommit = !afterCommit; f.Db.ThrowAfterCommit = afterCommit;
            var response = f.Write("InsertRecord", args, token);
            var error = Error(response, "CommitOutcomeUnknown");
            Assert.Equal(JTokenType.Null, error["persisted"].Type);
            Assert.Equal("Indeterminate", error["commitState"].Value<string>());
            Assert.False(error["retrySafe"].Value<bool>());
            Assert.Equal(afterCommit ? 2 : 1, f.Db.Rows.Count);
            Assert.DoesNotContain("SYNTHETIC_SECRET", response.ToString());
        }
        [Fact]
        public void FailedRereadAfterCommitReportsCommittedUnverified()
        {
            var f = new Fixture(); var args = Insert(true); string token = f.Preview("InsertRecord", args);
            f.Db.BeforeSelect = (db, _) => { if (db.Commits > 0) throw new TransactionRecordsFakeDatabase.FakeDbException(); };
            var response = f.Write("InsertRecord", args, token);
            var error = Error(response, "PostCommitReadFailed");
            Assert.True(error["persisted"].Value<bool>());
            Assert.True(error["commitConfirmed"].Value<bool>());
            Assert.False(error["rereadConfirmed"].Value<bool>());
            Assert.False(error["retrySafe"].Value<bool>());
            Assert.NotNull(error["keys"]["Id"]);
            Assert.Equal(2, f.Db.Rows.Count);
            Assert.DoesNotContain("SYNTHETIC_SECRET", response.ToString());
        }
        [Fact]
        public void PostCommitConcurrentWriterIsPreserved()
        {
            var f = new Fixture(); var args = Update(); string token = f.Preview("UpdateRecords", args);
            f.Db.AfterCommit = db => db.Rows[0]["Value"] = "another-writer";
            var error = Error(f.Write("UpdateRecords", args, token), "PostCommitDivergence");
            Assert.Equal(JTokenType.Null, error["persisted"].Type);
            Assert.True(error["commitConfirmed"].Value<bool>());
            Assert.False(error["stateRestored"].Value<bool>());
            Assert.Equal("ConcurrentChangePreserved", error["rollbackDiagnostic"].Value<string>());
            Assert.Equal("another-writer", f.Db.Rows[0]["Value"].Value<string>());
            Assert.Equal(1, f.Db.Writes);
        }
        [Theory]
        [InlineData(false)][InlineData(true)]
        public void TriggerChangesRequireExplicitDeclaration(bool declared)
        {
            var f = new Fixture(); var args = Update();
            if (declared) args["databaseManagedFields"] = new JArray("Stamp");
            string token = f.Preview("UpdateRecords", args);
            f.Db.Trigger = row => { row["Stamp"] = 1; return row; };
            var result = f.Write("UpdateRecords", args, token);
            if (declared)
            {
                Assert.Equal("ok", result["status"].Value<string>());
                Assert.Equal(1, result["result"]["records"][0]["Stamp"].Value<int>());
            }
            else
            {
                var error = Error(result, "WriteVerificationFailed");
                Assert.False(error["persisted"].Value<bool>());
                Assert.True(error["stateRestored"].Value<bool>());
                Assert.Equal("old", f.Db.Rows[0]["Value"].Value<string>());
                Assert.Equal(0, f.Db.Rows[0]["Stamp"].Value<int>());
                Assert.Equal(0, f.Db.Commits);
            }
        }
        [Theory]
        [InlineData("Id")][InlineData("Value")][InlineData("Missing")]
        public void InvalidManagedFieldsAreRejectedBeforeDatabaseAccess(string field)
        {
            var f = new Fixture(); var args = Update(); args["databaseManagedFields"] = new JArray(field);
            Error(f.Execute("UpdateRecords", args), "InvalidDatabaseManagedFields");
            Assert.Empty(f.Db.Commands);
        }
        [Fact]
        public void ManagedFieldsParticipateInPostCommitVerification()
        {
            var f = new Fixture(); var args = Update(); args["databaseManagedFields"] = new JArray("Stamp");
            string token = f.Preview("UpdateRecords", args);
            f.Db.Trigger = row => { row["Stamp"] = 1; return row; };
            f.Db.AfterCommit = db => db.Rows[0]["Stamp"] = 2;
            var error = Error(f.Write("UpdateRecords", args, token), "PostCommitDivergence");
            Assert.Equal("ConcurrentChangePreserved", error["rollbackDiagnostic"].Value<string>());
            Assert.Equal(2, f.Db.Rows[0]["Stamp"].Value<int>());
        }
        // A differing read followed by equal values cannot prove the same row
        // incarnation. Refuse compensation instead of overwriting an ABA writer.
        private static void DivergeFirstPostCommitRead(TransactionRecordsFakeDatabase db)
        {
            bool injected = false;
            db.BeforeSelect = (database, _) =>
            {
                if (database.Commits == 1 && !injected)
                {
                    database.OverrideRead = database.Rows.Select(r => (JObject)r.DeepClone()).ToList();
                    database.OverrideRead[0]["Value"] = "transient-observation";
                    injected = true;
                }
            };
        }
        [Theory]
        [InlineData(false)][InlineData(true)]
        public void DivergentObservationNeverAuthorizesAutomaticCompensation(bool insert)
        {
            var f = new Fixture(insert ? 0 : 1); var args = insert ? Insert() : Update();
            string action = insert ? "InsertRecord" : "UpdateRecords"; string token = f.Preview(action, args);
            DivergeFirstPostCommitRead(f.Db);
            var error = Error(f.Write(action, args, token), "PostCommitDivergence");
            Assert.False(error["stateRestored"].Value<bool>());
            Assert.False(error["rollbackAttempted"].Value<bool>());
            Assert.Equal(JTokenType.Null, error["persisted"].Type);
            Assert.Equal("ConcurrentChangePreserved", error["rollbackDiagnostic"].Value<string>());
            Assert.Single(f.Db.Rows);
            Assert.Equal(1, f.Db.Writes);
        }
        [Theory]
        [InlineData(false)][InlineData(true)]
        public void UnsafeRestoreIsNotAttemptedEvenWhenProviderWouldAllowIt(bool providerFailure)
        {
            var f = new Fixture(); var args = Update(); args["databaseManagedFields"] = new JArray("Stamp");
            string token = f.Preview("UpdateRecords", args);
            if (providerFailure) f.Db.FailRestore = true;
            else f.Db.Trigger = row => { row["Stamp"] = row["Stamp"].Value<int>() + 1; return row; };
            DivergeFirstPostCommitRead(f.Db);
            var error = Error(f.Write("UpdateRecords", args, token), "PostCommitDivergence");
            Assert.Equal(JTokenType.Null, error["persisted"].Type);
            Assert.False(error["stateRestored"].Value<bool>());
            Assert.Equal("ConcurrentChangePreserved", error["rollbackDiagnostic"].Value<string>());
            Assert.False(error["rollbackAttempted"].Value<bool>());
            Assert.Equal(1, f.Db.Writes);
            Assert.Equal("new", f.Db.Rows[0]["Value"].Value<string>());
            Assert.Equal(1, f.Db.Commits);
        }
        [Theory]
        [InlineData(100, false)][InlineData(101, true)]
        public void QueryChecksExtraRowAndPreservesIdentity(int count, bool truncated)
        {
            var f = new Fixture(count);
            var response = f.Execute("QueryRecords", JObject.Parse("{'fields':['Value'],'limit':100}"));
            var result = response["result"];
            Assert.Equal(truncated, result["truncated"].Value<bool>());
            Assert.Equal(100, ((JArray)result["records"]).Count);
            Assert.NotNull(result["records"][0]["Id"]);
            Assert.Null(result["records"][0]["Stamp"]);
            Assert.Equal("read-only", result["versionTokenKind"].Value<string>());
            Assert.True(result["writePreviewRequired"].Value<bool>());
            Assert.Equal("typed_sql", result["dataAccess"].Value<string>());
            Assert.False(result["businessRulesExecuted"].Value<bool>());
            Assert.Equal(count, f.Db.SelectedRows);
            Error(f.Write("UpdateRecords", Update(), result["versionToken"].Value<string>()), "DryRunRequired");
            Assert.Equal(0, f.Db.Writes);
        }

        [Fact]
        public void QueryUsesProjectionAndReadsOnlyOneSentinelBeyondTheLimit()
        {
            var f = new Fixture(10000);
            var response = f.Execute("QueryRecords", JObject.Parse("{'fields':['Value'],'limit':10}"));
            var result = response["result"];

            Assert.Equal("ok", response["status"].Value<string>());
            Assert.Equal(10, ((JArray)result["records"]).Count);
            Assert.True(result["truncated"].Value<bool>());
            Assert.Equal(11, f.Db.SelectedRows);
            Assert.Contains("TOP 11 ", f.Db.Commands.Single(command => command.StartsWith("SELECT ")));
            Assert.Contains("[Value]", f.Db.Commands.Single(command => command.StartsWith("SELECT ")));
            Assert.DoesNotContain("[Stamp]", f.Db.Commands.Single(command => command.StartsWith("SELECT ")));
        }

        [Theory]
        [InlineData(false)][InlineData(true)]
        public void DeletedAndRecreatedIdenticalRowIsNeverCompensated(bool insert)
        {
            var f = new Fixture(insert ? 0 : 1);
            var args = insert ? Insert() : Update();
            string action = insert ? "InsertRecord" : "UpdateRecords";
            string token = f.Preview(action, args);
            JObject replacement = null;
            f.Db.BeforeSelect = (db, _) =>
            {
                if (db.Commits != 1 || replacement != null) return;
                replacement = (JObject)db.Rows[0].DeepClone();
                db.OverrideRead = new System.Collections.Generic.List<JObject>();
                db.Rows.Clear(); // Another transaction deleted and recreated the key.
                db.Rows.Add(replacement);
            };
            var error = Error(f.Write(action, args, token), "PostCommitDivergence");
            Assert.Same(replacement, f.Db.Rows[0]);
            Assert.Equal(1, f.Db.Writes);
            Assert.Equal(1, f.Db.Commits);
            Assert.False(error["rollbackAttempted"].Value<bool>());
            Assert.False(error["stateRestored"].Value<bool>());
            Assert.Equal(JTokenType.Null, error["persisted"].Type);
        }
    }
}
