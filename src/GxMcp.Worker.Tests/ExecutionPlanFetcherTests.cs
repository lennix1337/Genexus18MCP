using GxMcp.Worker.Helpers;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // Wave-3 item 34: ExecutionPlanFetcher unit tests. No SDK involvement —
    // we exercise the per-DBMS EXPLAIN string and the planUnavailable envelope
    // shape against the documented contract.
    public class ExecutionPlanFetcherTests
    {
        [Fact]
        public void Oracle_BuildsExplainPlanFor_Prefix()
        {
            string s = ExecutionPlanFetcher.BuildExplainSyntax("oracle", "SELECT * FROM Aluno");
            Assert.Equal("EXPLAIN PLAN FOR SELECT * FROM Aluno", s);
        }

        [Fact]
        public void Postgres_UsesBareExplain()
        {
            string s = ExecutionPlanFetcher.BuildExplainSyntax("postgres", "SELECT 1");
            Assert.Equal("EXPLAIN SELECT 1", s);
        }

        [Theory]
        [InlineData("com.ibm.as400.access.AS400JDBCDriver", 0)]
        [InlineData("IBM", 8)]
        [InlineData(null, 9)]
        public void Db2Datastores_KeepNavigationPlanStructuredAsUnavailable(string provider, int dbms)
        {
            string family = ExecutionPlanFetcher.ResolveDbmsFamily(provider, dbms);
            var queries = new JArray { new JObject { ["sql"] = "SELECT * FROM A", ["baseTable"] = "A" } };

            ExecutionPlanFetcher.AttachExecutionPlans(queries, family);

            var plan = queries[0]["executionPlan"];
            Assert.Equal("db2", (string)plan["dbmsFamily"]);
            Assert.True((bool)plan["planUnavailable"]);
            Assert.Equal("no-live-db-connection", (string)plan["reason"]);
        }

        [Fact]
        public void ModernPostgresDbmsCode_UsesPostgresFamily()
        {
            Assert.Equal("postgres", ExecutionPlanFetcher.ResolveDbmsFamily(15));
        }

        [Fact]
        public void ProviderAndDbmsCode_UseTheSameFamilyResolution()
        {
            Assert.Equal("postgres", ExecutionPlanFetcher.ResolveDbmsFamily("Npgsql", 0));
            Assert.Equal("postgres", ExecutionPlanFetcher.ResolveDbmsFamily(null, 15));
        }

        [Fact]
        public void AttachExecutionPlans_AlwaysMarksUnavailable()
        {
            var queries = new JArray {
                new JObject { ["sql"] = "SELECT * FROM A", ["baseTable"] = "A" },
                new JObject { ["sql"] = "SELECT * FROM B", ["baseTable"] = "B" }
            };
            ExecutionPlanFetcher.AttachExecutionPlans(queries, 7); // Oracle
            foreach (var q in queries)
            {
                var plan = q["executionPlan"];
                Assert.NotNull(plan);
                Assert.True((bool)plan["planUnavailable"]);
                Assert.Equal("no-live-db-connection", (string)plan["reason"]);
                Assert.Equal("oracle", (string)plan["dbmsFamily"]);
                Assert.StartsWith("EXPLAIN PLAN FOR", (string)plan["explainSyntax"]);
            }
        }

        [Fact]
        public void UnknownDbms_FallsBackToExplain()
        {
            string s = ExecutionPlanFetcher.BuildExplainSyntax("unknown", "SELECT 1");
            Assert.Equal("EXPLAIN SELECT 1", s);
        }
    }
}
