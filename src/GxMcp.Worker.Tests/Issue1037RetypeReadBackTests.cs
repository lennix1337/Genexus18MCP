using GxMcp.Worker.Helpers;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// Issue #1037: on a WorkWithPlus WebPanel, modify VarChar(40) -> VarChar(256) answered
    /// VariableRenamed / persistedState=confirmed while the re-read Variables part (and its
    /// persistedHash) still showed VARCHAR(40). The success must be checked against the re-read.
    /// </summary>
    public class Issue1037RetypeReadBackTests
    {
        private static JObject Receipt(string persistedType, string source, bool reReadConfirmed = true) => new JObject
        {
            ["status"] = "ok",
            ["code"] = "VariableRenamed",
            ["target"] = "WPDominios",
            ["result"] = new JObject
            {
                ["requestedType"] = "VarChar",
                ["persistedType"] = persistedType,
                ["inPlace"] = true,
                ["retyped"] = true,
                ["persistedState"] = "confirmed"
            },
            ["source"] = source,
            ["persistedHash"] = "abc",
            ["postSaveVerification"] = new JObject { ["reReadConfirmed"] = reReadConfirmed }
        };

        [Fact]
        public void ALengthTheReReadDoesNotShowIsReportedAsNotPersisted()
        {
            var receipt = Receipt("VARCHAR(256)", "&wasDominio : VARCHAR(40)\n&Other : NUMERIC(4)\n");

            var checkedReceipt = RetypeReadBack.Enforce(receipt, "wasDominio");

            Assert.Equal("error", (string)checkedReceipt["status"]);
            Assert.Equal("WriteNotPersisted", (string)checkedReceipt["code"]);
            Assert.False((bool)checkedReceipt["result"]["retyped"]);
            Assert.Equal("notPersisted", (string)checkedReceipt["result"]["persistedState"]);
            Assert.Equal("VARCHAR(40)", (string)checkedReceipt["result"]["readBackType"]);
            Assert.Contains("VARCHAR(40)", (string)checkedReceipt["message"]);
            Assert.Contains("VARCHAR(256)", (string)checkedReceipt["message"]);
        }

        [Theory]
        [InlineData("VARCHAR(256)", "&wasDominio : VARCHAR(256)")]
        [InlineData("VarChar(256)", "&WASDOMINIO : VARCHAR(256)")]
        [InlineData("NUMERIC(10,2)", "&wasDominio : NUMERIC(10,2)")]
        [InlineData("DATE", "&wasDominio : DATE(8)")]
        public void AReReadThatMatchesKeepsTheSuccess(string persistedType, string source)
        {
            var receipt = Receipt(persistedType, source + "\n");

            var checkedReceipt = RetypeReadBack.Enforce(receipt, "wasDominio");

            Assert.Equal("ok", (string)checkedReceipt["status"]);
            Assert.Equal("VariableRenamed", (string)checkedReceipt["code"]);
            Assert.Equal("confirmed", (string)checkedReceipt["result"]["persistedState"]);
        }

        [Theory]
        [InlineData("NUMERIC(10,2)", "&wasDominio : NUMERIC(10)")]
        [InlineData("NUMERIC(10)", "&wasDominio : CHARACTER(10)")]
        public void ADifferentTypeOrScaleIsNotPersisted(string persistedType, string source)
        {
            var checkedReceipt = RetypeReadBack.Enforce(Receipt(persistedType, source), "wasDominio");

            Assert.Equal("WriteNotPersisted", (string)checkedReceipt["code"]);
        }

        [Fact]
        public void AVariableMissingFromTheReReadIsNotPersisted()
        {
            var checkedReceipt = RetypeReadBack.Enforce(Receipt("VARCHAR(256)", "&Other : NUMERIC(4)"), "wasDominio");

            Assert.Equal("WriteNotPersisted", (string)checkedReceipt["code"]);
        }

        [Fact]
        public void WithoutAReliableReReadTheSuccessIsNoLongerConfirmed()
        {
            var checkedReceipt = RetypeReadBack.Enforce(Receipt("VARCHAR(256)", null, reReadConfirmed: false), "wasDominio");

            Assert.Equal("ok", (string)checkedReceipt["status"]);
            Assert.Equal("unverified", (string)checkedReceipt["result"]["persistedState"]);
        }

        [Fact]
        public void ARefusalIsLeftAsItIs()
        {
            var refusal = new JObject { ["status"] = "error", ["code"] = "ModifyVariableFailed" };

            var checkedReceipt = RetypeReadBack.Enforce(refusal, "wasDominio");

            Assert.Equal("ModifyVariableFailed", (string)checkedReceipt["code"]);
        }
    }
}
