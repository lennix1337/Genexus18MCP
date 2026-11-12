using System;
using System.IO;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class ReorgSqlPreviewTests
    {
        [Fact]
        public void Parse_ClassifiesTablesIndexesAndDestructiveChanges()
        {
            JObject plan = ReorgSqlPreview.Parse(@"
CREATE TABLE [Customer] ([CustomerId] int NOT NULL);
ALTER TABLE [Customer] ADD [Name] varchar(80) NULL;
CREATE INDEX IX_Customer_Name ON [Customer] ([Name]);
ALTER TABLE [Customer] DROP COLUMN [Legacy];", true);

            Assert.True((bool)plan["ddlEffective"]);
            Assert.Equal(4, ((JArray)plan["ddl"]).Count);
            Assert.Contains("Customer", plan["affectedTables"].Values<string>());
            Assert.Single((JArray)plan["indexes"]);
            Assert.Single((JArray)plan["destructiveConversions"]);
        }

        [Fact]
        public void AssessVerdict_NeededWithoutDdl_IsDistinctFromConfirmedAndStaysNeeded()
        {
            JObject withoutDdl = ReorgSqlPreview.AssessVerdict("ReorganizationNeeded", false);
            JObject withDdl = ReorgSqlPreview.AssessVerdict("ReorganizationNeeded", true);

            Assert.Equal("ReorganizationNeededDdlUnavailable", (string)withoutDdl["state"]);
            Assert.False((bool)withoutDdl["confirmed"]);
            Assert.NotNull(withoutDdl["note"]);
            Assert.Equal("ReorganizationConfirmedByDdl", (string)withDdl["state"]);
            Assert.True((bool)withDdl["confirmed"]);
            Assert.Equal("ReorganizationNeeded", (string)withoutDdl["sdkVerdict"]);
        }

        [Theory]
        [InlineData("NoReorgNeeded")]
        [InlineData("NoReorgButShowImpact")]
        public void AssessVerdict_NotNeeded_ReportsNoReorganization(string verdict)
        {
            JObject evidence = ReorgSqlPreview.AssessVerdict(verdict, false);
            Assert.Equal("NoReorganizationReported", (string)evidence["state"]);
        }

        // #928: Impact Analysis says ReorganizationNeeded with no DDL, the SDK's
        // lastReorg is from 2010, but the IDE ran a reorg after the last table change.
        [Fact]
        public void AssessVerdict_NoDdlAndMigrationAfterLastTableChange_IsInSyncByMigrationHistory()
        {
            var lastTable = new DateTime(2026, 11, 9, 20, 26, 10, DateTimeKind.Utc);
            var migration = new DateTime(2026, 11, 9, 20, 42, 41, DateTimeKind.Utc);

            JObject evidence = ReorgSqlPreview.AssessVerdict("ReorganizationNeeded", false, lastTable, migration);

            Assert.Equal("InSyncByMigrationHistory", (string)evidence["state"]);
            Assert.False((bool)evidence["confirmed"]);
            Assert.Equal("ReorganizationNeeded", (string)evidence["sdkVerdict"]);
            Assert.NotNull(evidence["migrationHistory"]?["newestMigrationUtc"]);
        }

        [Fact]
        public void AssessVerdict_TableChangedAfterNewestMigration_StaysDdlUnavailable()
        {
            var migration = new DateTime(2026, 11, 9, 20, 0, 0, DateTimeKind.Utc);
            var lastTable = migration.AddMinutes(5);

            JObject evidence = ReorgSqlPreview.AssessVerdict("ReorganizationNeeded", false, lastTable, migration);

            Assert.Equal("ReorganizationNeededDdlUnavailable", (string)evidence["state"]);
        }

        [Fact]
        public void AssessVerdict_DdlPresent_WinsOverMigrationHistory()
        {
            var lastTable = new DateTime(2026, 11, 9, 20, 0, 0, DateTimeKind.Utc);
            JObject evidence = ReorgSqlPreview.AssessVerdict("ReorganizationNeeded", true, lastTable, lastTable.AddHours(1));

            Assert.Equal("ReorganizationConfirmedByDdl", (string)evidence["state"]);
        }

        [Fact]
        public void EffectiveLastReorg_UsesNewerOfSdkAndMigrationHistory()
        {
            var stale = new DateTime(2010, 4, 23, 14, 54, 56, DateTimeKind.Utc);
            var migration = new DateTime(2026, 11, 9, 20, 42, 41, DateTimeKind.Utc);

            Assert.Equal(migration, ReorgSqlPreview.EffectiveLastReorg(stale, migration));
            Assert.Equal(stale, ReorgSqlPreview.EffectiveLastReorg(stale, null));
            Assert.Equal(migration, ReorgSqlPreview.EffectiveLastReorg(migration, stale));
        }

        [Fact]
        public void AssessVerdict_LocalTableTimestampAfterUtcMigration_StaysDdlUnavailable()
        {
            var tableUtc = new DateTime(2026, 11, 9, 20, 0, 0, DateTimeKind.Utc);
            var tableLocal = tableUtc.ToLocalTime();
            var earlierMigrationUtc = tableUtc.AddHours(-2);

            JObject evidence = ReorgSqlPreview.AssessVerdict("ReorganizationNeeded", false, tableLocal, earlierMigrationUtc);

            Assert.Equal("ReorganizationNeededDdlUnavailable", (string)evidence["state"]);
        }

        [Fact]
        public void EffectiveLastReorg_LocalSdkTimestampNewerThanUtcMigration_KeepsSdkTimestampInUtc()
        {
            var sdkUtc = new DateTime(2026, 11, 9, 20, 0, 0, DateTimeKind.Utc);
            var sdkLocal = sdkUtc.ToLocalTime();
            var earlierMigrationUtc = sdkUtc.AddHours(-2);

            Assert.Equal(sdkUtc, ReorgSqlPreview.EffectiveLastReorg(sdkLocal, earlierMigrationUtc));
        }

        [Fact]
        public void FindLatestMigration_PicksNewestScriptUnderMigrationsFolder()
        {
            string kb = Path.Combine(Path.GetTempPath(), "gxmcp-928-" + Guid.NewGuid().ToString("N"));
            string dir = Path.Combine(kb, "CSharpModel", "migrations", "default");
            Directory.CreateDirectory(dir);
            try
            {
                string older = Path.Combine(dir, "V2026.11.01.100000__migration.sql");
                string newer = Path.Combine(dir, "V2026.11.09.174241__migration.sql");
                string outside = Path.Combine(kb, "V2030.01.01.000000__migration.sql");
                File.WriteAllText(older, "--");
                File.WriteAllText(newer, "--");
                File.WriteAllText(outside, "--");
                File.SetLastWriteTimeUtc(older, new DateTime(2026, 11, 1, 13, 0, 0, DateTimeKind.Utc));
                File.SetLastWriteTimeUtc(newer, new DateTime(2026, 11, 9, 20, 42, 41, DateTimeKind.Utc));
                File.SetLastWriteTimeUtc(outside, new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc));

                FileInfo latest = ReorgSqlPreview.FindLatestMigration(kb);

                Assert.Equal(newer, latest?.FullName);
                Assert.Null(ReorgSqlPreview.FindLatestMigration(Path.Combine(kb, "missing")));
            }
            finally { Directory.Delete(kb, true); }
        }

        [Fact]
        public void AssessVerdict_WithoutDeepAnalysis_ReturnsNull()
        {
            Assert.Null(ReorgSqlPreview.AssessVerdict(null, false));
        }
    }
}
