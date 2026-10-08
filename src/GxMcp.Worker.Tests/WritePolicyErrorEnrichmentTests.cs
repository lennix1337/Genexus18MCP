using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // Friction-report item #2: full-mode write returned bare {"error":"Erro"} when the SDK
    // threw without diagnostics, while patch-mode surfaced the real "src0059: Esperando..." text.
    // The fix centralizes the "is this bare?" detection and the "use diagnostics if we have
    // anything better" enrichment in WritePolicy so both write paths share one rule.
    public class WritePolicyErrorEnrichmentTests
    {
        [Theory]
        [InlineData("Saving não é permitido neste momento. User has a bloqueio CopyModel.", true)]
        [InlineData("SDK save rejected: CopyModel", true)]
        [InlineData("src0059: syntax error", false)]
        public void CopyModelWriteLock_IsIdentified(string message, bool expected)
        {
            Assert.Equal(expected, WritePolicy.IsCopyModelWriteLock(message));
        }

        [Theory]
        [InlineData("Erro")]
        [InlineData("erro")]
        [InlineData(" Error ")]
        [InlineData("Erro, line: 1")]
        [InlineData("error,line:42")]
        [InlineData("")]
        [InlineData(null)]
        public void IsBareGenericError_RecognizesUninformativeMessages(string message)
        {
            Assert.True(WritePolicy.IsBareGenericError(message));
        }

        [Theory]
        [InlineData("src0059: Esperando 'EndFor' para fechar 'For Each'")]
        [InlineData("Part save failed: src0216: 'Visible' propriedade invalida")]
        [InlineData("KB not opened")]
        public void IsBareGenericError_RejectsInformativeMessages(string message)
        {
            Assert.False(WritePolicy.IsBareGenericError(message));
        }

        [Fact]
        public void PreferDetailedMessage_BareException_PicksSdkMessages()
        {
            string better = WritePolicy.PreferDetailedMessage(
                exceptionMessage: "Erro",
                sdkMessages: "src0059: Esperando 'EndFor' para fechar 'For Each'",
                issues: null);
            Assert.Equal("src0059: Esperando 'EndFor' para fechar 'For Each'", better);
        }

        [Fact]
        public void PreferDetailedMessage_BareException_FallsThroughBareSdkMessagesToIssues()
        {
            // When SDK messages are also bare "Erro", search the issues[] array for a description
            // with the real diagnostic. This is the common shape from SdkDiagnosticsHelper.GetDiagnostics.
            var issues = new JArray(
                new JObject { ["description"] = "Erro", ["severity"] = "Error" },
                new JObject { ["description"] = "src0216: 'Visible' propriedade invalida", ["severity"] = "Error" });
            string better = WritePolicy.PreferDetailedMessage("Erro", "Erro", issues);
            Assert.Equal("src0216: 'Visible' propriedade invalida", better);
        }

        [Fact]
        public void PreferDetailedMessage_InformativeException_LeavesItUntouched()
        {
            // We never overwrite a non-bare exception, even if diagnostics also have content.
            var issues = new JArray(new JObject { ["description"] = "something else" });
            string result = WritePolicy.PreferDetailedMessage(
                "Part save failed: src0301: invalid attribute",
                "noise",
                issues);
            Assert.Equal("Part save failed: src0301: invalid attribute", result);
        }

        [Fact]
        public void PreferDetailedMessage_NothingBetterAvailable_KeepsBareMessage()
        {
            string result = WritePolicy.PreferDetailedMessage("Erro", null, null);
            Assert.Equal("Erro", result);
        }

        // issue #39: `Unique(att);` is not a GeneXus rule; the SDK rejects the Rules save with a
        // bare "Erro". FindInvalidRuleKeywords lets CreateTransactionErrorResponse surface a hint.
        [Theory]
        [InlineData("Unique(CountryName);", "Unique")]
        [InlineData("Default(City, 'N/A');\nUnique(Name);", "Unique")]
        [InlineData("unique(Name);", "unique")]                 // case-insensitive
        [InlineData("Unique( Name );", "Unique")]               // whitespace tolerated
        public void FindInvalidRuleKeywords_FlagsInvalidPseudoRules(string source, string expected)
        {
            var hits = WritePolicy.FindInvalidRuleKeywords(source);
            Assert.Contains(expected, hits, System.StringComparer.OrdinalIgnoreCase);
        }

        [Theory]
        [InlineData("Error('x') if Name.IsEmpty();")]           // valid rule
        [InlineData("NoAccept(Name);\nDefault(City, 'N/A');")]  // valid rules
        [InlineData("Name = &Name if not &Name.IsEmpty();")]    // assignment rule
        [InlineData("MyProc.Call(Id) On BeforeInsert;")]        // member call, not a leading keyword
        [InlineData("// Unique(Name) noted for later")]         // keyword only in a comment
        [InlineData("/* Unique(Name); */ Default(City, 'X');")] // keyword only in a block comment
        [InlineData("")]
        [InlineData(null)]
        public void FindInvalidRuleKeywords_LeavesValidRulesAlone(string source)
        {
            Assert.Empty(WritePolicy.FindInvalidRuleKeywords(source));
        }

        [Fact]
        public void BuildInvalidRuleHint_MentionsUniqueIndexRemedy()
        {
            string hint = WritePolicy.BuildInvalidRuleHint(new[] { "Unique" });
            Assert.NotNull(hint);
            Assert.Contains("unique index", hint, System.StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void BuildInvalidRuleHint_EmptyInput_ReturnsNull()
        {
            Assert.Null(WritePolicy.BuildInvalidRuleHint(new string[0]));
        }

        // The exception WriteService throws is wrapped ("Part save failed: Erro"), so the plain
        // IsBareGenericError misses it — IsUninformativeSaveError strips the wrapper first.
        [Theory]
        [InlineData("Part save failed: Erro", true)]
        [InlineData("Part save reported errors: Error", true)]
        [InlineData("Erro", true)]
        [InlineData("Part save failed: src0059: Esperando 'EndFor'", false)]
        [InlineData("Part save failed: 'Unique' is not a known object", false)]
        public void IsUninformativeSaveError_StripsWrapperPrefix(string message, bool expected)
        {
            Assert.Equal(expected, WritePolicy.IsUninformativeSaveError(message));
        }
    }
}
