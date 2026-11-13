using System.Linq;
using GxMcp.Gateway;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    // v2.6.9 — covers the 10 canonical scenarios the builder routes
    // (8 happy + 2 error). Read-only tools must return null. Suggestion
    // cap is 3. Each generated args block must carry the keys the named
    // tool's schema requires so the LLM can copy-paste.
    public class NextLegalActionsBuilderTests
    {
        [Fact]
        public void ReadOnlyTool_ReturnsNull()
        {
            var result = NextLegalActionsBuilder.BuildFor("genexus_whoami", new JObject(), new JObject(), isError: false);
            Assert.Null(result);
        }

        [Fact]
        public void UnknownTool_ReturnsNull()
        {
            var result = NextLegalActionsBuilder.BuildFor("genexus_does_not_exist", new JObject(), new JObject(), isError: false);
            Assert.Null(result);
        }

        [Fact]
        public void EmptyToolName_ReturnsNull()
        {
            var result = NextLegalActionsBuilder.BuildFor("", new JObject(), new JObject(), isError: false);
            Assert.Null(result);
        }

        [Fact]
        public void ApplyPatternSuccess_SuggestsBuildEditAndRestore()
        {
            var args = new JObject { ["target"] = "Foo", ["pattern"] = "WorkWithPlus" };
            var payload = new JObject { ["status"] = "Success", ["hostName"] = "WorkWithPlusFoo" };
            var result = NextLegalActionsBuilder.BuildFor("genexus_apply_pattern", args, payload, isError: false);

            Assert.NotNull(result);
            Assert.InRange(result!.Count, 1, 3);
            var tools = result.Select(s => s["tool"]?.ToString()).ToList();
            Assert.Contains("genexus_lifecycle", tools);
            Assert.Contains("genexus_edit", tools);
            // Cap enforced.
            Assert.True(result.Count <= 3);
        }

        [Fact]
        public void ApplyPatternError_WithValidParentTypes_SuggestsInspectAndCreate()
        {
            var args = new JObject { ["target"] = "MyProc", ["pattern"] = "WorkWithPlus" };
            var payload = new JObject
            {
                ["status"] = "Error",
                ["target"] = "MyProc",
                ["validParentTypes"] = new JArray("Transaction", "WebPanel"),
                ["message"] = "WorkWithPlus cannot be applied to a Procedure."
            };
            var result = NextLegalActionsBuilder.BuildFor("genexus_apply_pattern", args, payload, isError: true);

            Assert.NotNull(result);
            var tools = result!.Select(s => s["tool"]?.ToString()).ToList();
            Assert.Contains("genexus_inspect", tools);
            Assert.Contains("genexus_create", tools);
        }

        [Fact]
        public void ApplyPatternError_WithoutValidParentTypes_ReturnsNull()
        {
            var args = new JObject { ["target"] = "Foo" };
            var payload = new JObject { ["status"] = "Error", ["message"] = "generic failure" };
            var result = NextLegalActionsBuilder.BuildFor("genexus_apply_pattern", args, payload, isError: true);
            Assert.Null(result);
        }

        [Fact]
        public void CreateObject_Transaction_SuggestsEditStructureAndApplyPattern()
        {
            var args = new JObject { ["type"] = "Transaction", ["name"] = "Aluno" };
            var payload = new JObject { ["status"] = "Success", ["name"] = "Aluno", ["type"] = "Transaction" };
            var result = NextLegalActionsBuilder.BuildFor("genexus_create_object", args, payload, isError: false);

            Assert.NotNull(result);
            var first = (JObject)result![0];
            Assert.Equal("genexus_edit", first["tool"]?.ToString());
            Assert.Equal("Structure", first["args"]?["part"]?.ToString());

            var tools = result.Select(s => s["tool"]?.ToString()).ToList();
            Assert.Contains("genexus_apply_pattern", tools);
        }

        [Fact]
        public void CreateObject_Procedure_DoesNotSuggestApplyPattern()
        {
            var args = new JObject { ["type"] = "Procedure", ["name"] = "ProcA" };
            var payload = new JObject { ["status"] = "Success", ["name"] = "ProcA", ["type"] = "Procedure" };
            var result = NextLegalActionsBuilder.BuildFor("genexus_create_object", args, payload, isError: false);

            Assert.NotNull(result);
            var tools = result!.Select(s => s["tool"]?.ToString()).ToList();
            Assert.DoesNotContain("genexus_apply_pattern", tools);
        }

        [Fact]
        public void EditSuccess_SuggestsBuild()
        {
            var args = new JObject { ["name"] = "Foo", ["part"] = "Events" };
            var payload = new JObject { ["status"] = "Success", ["name"] = "Foo" };
            var result = NextLegalActionsBuilder.BuildFor("genexus_edit", args, payload, isError: false);
            Assert.NotNull(result);
            var tools = result!.Select(s => s["tool"]?.ToString()).ToList();
            Assert.Contains("genexus_lifecycle", tools);
        }

        [Fact]
        public void EditError_ReturnsNull()
        {
            var args = new JObject { ["name"] = "Foo" };
            var payload = new JObject { ["status"] = "Error", ["message"] = "patch failed" };
            var result = NextLegalActionsBuilder.BuildFor("genexus_edit", args, payload, isError: true);
            Assert.Null(result);
        }

        [Fact]
        public void Suggestions_AlwaysHaveRequiredFields()
        {
            // Every suggestion entry must carry tool/args/why/priority — the
            // schema the agent will copy from. Sanity-check across two
            // representative scenarios so a future refactor that drops a
            // field is caught early.
            var args = new JObject { ["target"] = "Foo", ["pattern"] = "WorkWithPlus" };
            var payload = new JObject { ["hostName"] = "WorkWithPlusFoo" };
            var result = NextLegalActionsBuilder.BuildFor("genexus_apply_pattern", args, payload, isError: false);
            Assert.NotNull(result);
            foreach (var s in result!.OfType<JObject>())
            {
                Assert.NotNull(s["tool"]);
                Assert.NotNull(s["args"]);
                Assert.NotNull(s["why"]);
                Assert.NotNull(s["priority"]);
                Assert.Contains(s["priority"]?.ToString(), new[] { "high", "medium", "low" });
            }
        }

        [Fact]
        public void Cap_AtThreeSuggestions()
        {
            // The builder caps at 3. Verify by exercising apply_pattern
            // which generates exactly 3 on the happy path.
            var args = new JObject { ["target"] = "Foo", ["pattern"] = "WorkWithPlus" };
            var payload = new JObject { ["hostName"] = "WorkWithPlusFoo" };
            var result = NextLegalActionsBuilder.BuildFor("genexus_apply_pattern", args, payload, isError: false);
            Assert.NotNull(result);
            Assert.True(result!.Count <= 3, $"expected ≤3 suggestions, got {result.Count}");
        }

        // --- Plan 041: canonical genexus_create dispatch by action ---

        [Fact]
        public void CanonicalCreate_ActionObject_SuggestsEditAndApplyPattern()
        {
            var args = new JObject { ["action"] = "object", ["type"] = "Transaction", ["name"] = "Foo" };
            var payload = new JObject { ["status"] = "Success", ["name"] = "Foo", ["type"] = "Transaction" };
            var result = NextLegalActionsBuilder.BuildFor("genexus_create", args, payload, isError: false);

            Assert.NotNull(result);
            Assert.NotEmpty(result!);
            var first = (JObject)result[0];
            Assert.Equal("genexus_edit", first["tool"]?.ToString());
        }

        [Theory]
        [InlineData("MasterPage", "WebForm")]
        [InlineData("WebPanel", "WebForm")]
        [InlineData("Procedure", "Source")]
        public void CanonicalCreate_ActionObject_SuggestsEditingThePartThatHoldsTheContentOfTheType(string type, string part)
        {
            var args = new JObject { ["action"] = "object", ["type"] = type, ["name"] = "Foo" };
            var payload = new JObject { ["status"] = "Success", ["name"] = "Foo", ["type"] = type };
            var result = NextLegalActionsBuilder.BuildFor("genexus_create", args, payload, isError: false);

            Assert.NotNull(result);
            var first = (JObject)result![0];
            Assert.Equal("genexus_edit", first["tool"]?.ToString());
            Assert.Equal(part, first["args"]?["part"]?.ToString());
        }

        [Fact]
        public void CanonicalCreate_ActionPopup_SuggestsEditAndBuild()
        {
            var args = new JObject { ["action"] = "popup", ["parent"] = "WorkWithPlusFoo", ["name"] = "PopupFoo" };
            var payload = new JObject { ["status"] = "Success", ["created"] = "PopupFoo" };
            var result = NextLegalActionsBuilder.BuildFor("genexus_create", args, payload, isError: false);

            Assert.NotNull(result);
            Assert.NotEmpty(result!);
            var tools = result!.Select(s => s["tool"]?.ToString()).ToList();
            Assert.Contains("genexus_edit", tools);
        }

        [Fact]
        public void CanonicalCreate_ActionSaveAs_SuggestsEditAndBuild()
        {
            var args = new JObject { ["action"] = "save_as", ["newName"] = "FooCopy" };
            var payload = new JObject { ["status"] = "Success", ["created"] = "FooCopy" };
            var result = NextLegalActionsBuilder.BuildFor("genexus_create", args, payload, isError: false);

            Assert.NotNull(result);
            Assert.NotEmpty(result!);
            var tools = result!.Select(s => s["tool"]?.ToString()).ToList();
            Assert.Contains("genexus_edit", tools);
            Assert.Contains("genexus_lifecycle", tools);
        }

        [Fact]
        public void CanonicalCreate_Error_ReturnsNull()
        {
            var args = new JObject { ["action"] = "object", ["type"] = "Transaction", ["name"] = "Foo" };
            var payload = new JObject { ["status"] = "Error", ["message"] = "spec failure" };
            var result = NextLegalActionsBuilder.BuildFor("genexus_create", args, payload, isError: true);
            Assert.Null(result);
        }

        [Fact]
        public void CanonicalCreate_UnknownAction_ReturnsNull()
        {
            var args = new JObject { ["action"] = "translate" };
            var payload = new JObject { ["status"] = "Success" };
            var result = NextLegalActionsBuilder.BuildFor("genexus_create", args, payload, isError: false);
            Assert.Null(result);
        }

        // --- Plan 041: no emitted suggestion may name a known-legacy alias ---

        private static readonly string[] KnownLegacyToolNames =
        {
            "genexus_undo",
            "genexus_create_object",
            "genexus_create_popup",
            "genexus_save_as",
            "genexus_logs",
            "genexus_playbook",
        };

        [Theory]
        [InlineData("genexus_apply_pattern")]
        [InlineData("genexus_create")]
        [InlineData("genexus_edit")]
        [InlineData("genexus_lifecycle")]
        [InlineData("genexus_versioning")]
        [InlineData("genexus_read")]
        [InlineData("genexus_query")]
        public void NoSuggestion_NamesAKnownLegacyAlias(string toolName)
        {
            var args = new JObject
            {
                ["target"] = "Foo",
                ["name"] = "Foo",
                ["action"] = toolName == "genexus_create" ? "object" : "build",
                ["type"] = "Transaction",
                ["pattern"] = "WorkWithPlus",
                ["parent"] = "WorkWithPlusFoo",
                ["newName"] = "FooCopy",
                ["validParentTypes"] = new JArray("Transaction"),
            };
            var payload = new JObject
            {
                ["status"] = "Success",
                ["hostName"] = "WorkWithPlusFoo",
                ["name"] = "Foo",
                ["created"] = "Foo",
                ["target"] = "Foo",
                ["results"] = new JArray(new JObject { ["name"] = "Foo", ["type"] = "Transaction" }),
            };

            foreach (var isError in new[] { false, true })
            {
                var result = NextLegalActionsBuilder.BuildFor(toolName, args, payload, isError);
                if (result == null) continue;
                foreach (var s in result.OfType<JObject>())
                {
                    var tool = s["tool"]?.ToString();
                    Assert.DoesNotContain(tool, KnownLegacyToolNames);
                }
            }
        }

        [Fact]
        public void ReadSuccess_SuggestsEditAnalyzeAndNavigation()
        {
            var args = new JObject { ["name"] = "PCalculaDesconto", ["part"] = "Source" };
            var payload = new JObject { ["name"] = "PCalculaDesconto", ["type"] = "Procedure" };
            var result = NextLegalActionsBuilder.BuildFor("genexus_read", args, payload, isError: false);

            Assert.NotNull(result);
            Assert.Equal(3, result!.Count);
            var tools = result.Select(s => s["tool"]?.ToString()).ToList();
            Assert.Contains("genexus_edit", tools);
            Assert.Contains("genexus_analyze", tools);
            Assert.Contains("genexus_navigation", tools);

            var first = (JObject)result[0];
            Assert.Equal("genexus_edit", first["tool"]?.ToString());
            Assert.Equal("PCalculaDesconto", first["args"]?["name"]?.ToString());
            Assert.Equal("Source", first["args"]?["part"]?.ToString());
        }

        [Fact]
        public void QuerySuccess_SuggestsReadAndInspectForTopMatch()
        {
            var args = new JObject { ["query"] = "PCalculaDesconto" };
            var payload = new JObject
            {
                ["results"] = new JArray(new JObject { ["name"] = "PCalculaDesconto", ["type"] = "Procedure" })
            };
            var result = NextLegalActionsBuilder.BuildFor("genexus_query", args, payload, isError: false);

            Assert.NotNull(result);
            Assert.Equal(2, result!.Count);
            var first = (JObject)result[0];
            Assert.Equal("genexus_read", first["tool"]?.ToString());
            Assert.Equal("PCalculaDesconto", first["args"]?["name"]?.ToString());
        }
    }
}
