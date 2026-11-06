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

        [Fact]
        public void AssessVerdict_WithoutDeepAnalysis_ReturnsNull()
        {
            Assert.Null(ReorgSqlPreview.AssessVerdict(null, false));
        }
    }
}
