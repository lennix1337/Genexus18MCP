using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// Unit tests for GatewayArgsValidator: required-field, type, and enum checks
    /// plus an end-to-end test confirming InvalidArgs is returned without hitting the worker.
    /// </summary>
    public class GatewayArgsValidatorTests
    {
        // ── Helper: build a minimal inputSchema ──────────────────────────────

        private static JObject MakeSchema(
            IEnumerable<(string name, string type, string[]? enumValues)>? props = null,
            string[]? required = null,
            bool? additionalPropertiesFalse = null)
        {
            var schema = new JObject { ["type"] = "object" };
            var propertiesObj = new JObject();
            foreach (var (name, type, enumValues) in props ?? System.Array.Empty<(string, string, string[]?)>())
            {
                var p = new JObject { ["type"] = type };
                if (enumValues != null)
                    p["enum"] = new JArray(enumValues);
                propertiesObj[name] = p;
            }
            schema["properties"] = propertiesObj;

            if (required != null)
                schema["required"] = new JArray(required);
            if (additionalPropertiesFalse == true)
                schema["additionalProperties"] = false;

            return schema;
        }

        // ── 1. Pass: well-formed call ─────────────────────────────────────────

        [Fact]
        public void Validate_WellFormedCall_ReturnsOk()
        {
            // genexus_read schema: name is a string (not required in schema but type-declared)
            GatewayArgsValidator.ClearCache();
            GatewayArgsValidator.PrimeCache("genexus_read", MakeSchema(
                props: new[] { ("name", "string", (string[]?)null) }
            ));

            var result = GatewayArgsValidator.Validate("genexus_read", new JObject { ["name"] = "MyProcedure" });

            Assert.True(result.Ok);
            Assert.Empty(result.Violations);
            var inferredCreate = GatewayArgsValidator.Validate("genexus_create", new JObject
            {
                ["name"] = "P",
                ["objectType"] = "Procedure"
            });
            Assert.True(inferredCreate.Ok);
        }

        // ── 2. Fail: missing required field ──────────────────────────────────

        [Fact]
        public void Validate_MissingRequiredField_ReturnsViolation()
        {
            GatewayArgsValidator.ClearCache();
            GatewayArgsValidator.PrimeCache("genexus_edit", MakeSchema(
                props: new[] { ("name", "string", (string[]?)null), ("part", "string", (string[]?)null) },
                required: new[] { "name" }
            ));

            // Call without the required "name" field
            var result = GatewayArgsValidator.Validate("genexus_edit", new JObject { ["part"] = "Source" });

            Assert.False(result.Ok);
            Assert.Contains(result.Violations, v => v.Path == "name" && v.Actual == "missing");
        }

        [Fact]
        public void Validate_NullArgs_WithRequiredField_ReturnsViolation()
        {
            GatewayArgsValidator.ClearCache();
            GatewayArgsValidator.PrimeCache("genexus_inspect", MakeSchema(
                props: new[] { ("name", "string", (string[]?)null) },
                required: new[] { "name" }
            ));

            var result = GatewayArgsValidator.Validate("genexus_inspect", null);

            Assert.False(result.Ok);
            Assert.Contains(result.Violations, v => v.Path == "name" && v.Actual == "missing");
        }

        // ── 3. Fail: wrong type ───────────────────────────────────────────────

        [Fact]
        public void Validate_WrongTypeValue_ReturnsViolation()
        {
            GatewayArgsValidator.ClearCache();
            GatewayArgsValidator.PrimeCache("genexus_read", MakeSchema(
                props: new[] { ("limit", "integer", (string[]?)null) }
            ));

            // Pass a string where an integer is expected
            var result = GatewayArgsValidator.Validate("genexus_read", new JObject { ["limit"] = "not-a-number" });

            Assert.False(result.Ok);
            Assert.Contains(result.Violations, v => v.Path == "limit" && v.Expected == "integer");
        }

        [Fact]
        public void Validate_WrongTypeBoolean_ReturnsViolation()
        {
            GatewayArgsValidator.ClearCache();
            GatewayArgsValidator.PrimeCache("genexus_edit", MakeSchema(
                props: new[] { ("dryRun", "boolean", (string[]?)null) }
            ));

            // Pass a string instead of boolean
            var result = GatewayArgsValidator.Validate("genexus_edit", new JObject { ["dryRun"] = "yes" });

            Assert.False(result.Ok);
            Assert.Contains(result.Violations, v => v.Path == "dryRun" && v.Expected == "boolean");
        }

        // ── 4. Fail: out-of-enum value ────────────────────────────────────────

        [Fact]
        public void Validate_OutOfEnumValue_ReturnsViolation()
        {
            GatewayArgsValidator.ClearCache();
            // genexus_edit mode: enum [full, patch, ops]
            GatewayArgsValidator.PrimeCache("genexus_edit", MakeSchema(
                props: new (string, string, string[]?)[] { ("mode", "string", new[] { "full", "patch", "ops" }) }
            ));

            var result = GatewayArgsValidator.Validate("genexus_edit", new JObject { ["mode"] = "invalid_mode" });

            Assert.False(result.Ok);
            Assert.Contains(result.Violations, v => v.Path == "mode" && v.Actual == "invalid_mode");
        }

        [Fact]
        public void Validate_ValidEnumValue_ReturnsOk()
        {
            GatewayArgsValidator.ClearCache();
            GatewayArgsValidator.PrimeCache("genexus_edit", MakeSchema(
                props: new (string, string, string[]?)[] { ("mode", "string", new[] { "full", "patch", "ops" }) }
            ));

            var result = GatewayArgsValidator.Validate("genexus_edit", new JObject { ["mode"] = "patch" });

            Assert.True(result.Ok);
            Assert.Empty(result.Violations);
        }

        // ── 5. Pass: tool with no declared schema ─────────────────────────────

        [Fact]
        public void Validate_NoSchema_AlwaysReturnsOk()
        {
            GatewayArgsValidator.ClearCache();
            GatewayArgsValidator.PrimeCache("genexus_no_schema_tool", null);

            var result = GatewayArgsValidator.Validate("genexus_no_schema_tool", new JObject { ["anything"] = "goes" });

            Assert.True(result.Ok);
        }

        // ── 6. Pass: no required fields + empty args ──────────────────────────

        [Fact]
        public void Validate_NoRequiredFields_NullArgs_ReturnsOk()
        {
            GatewayArgsValidator.ClearCache();
            GatewayArgsValidator.PrimeCache("genexus_whoami", MakeSchema()); // no required

            var result = GatewayArgsValidator.Validate("genexus_whoami", null);

            Assert.True(result.Ok);
        }

        // ── 7. Fail: additionalProperties: false with unknown field ───────────

        [Fact]
        public void Validate_AdditionalPropertiesFalse_UnknownField_ReturnsViolation()
        {
            GatewayArgsValidator.ClearCache();
            GatewayArgsValidator.PrimeCache("genexus_doctor", MakeSchema(additionalPropertiesFalse: true));

            var result = GatewayArgsValidator.Validate("genexus_doctor", new JObject { ["unknown"] = "x" });

            Assert.False(result.Ok);
            Assert.Contains(result.Violations, v => v.Path == "unknown");
        }

        // ── 7b. Enum violation carries a DidYouMean suggestion ───────────────

        [Fact]
        public void Validate_OutOfEnumValue_TypoNearMember_CarriesSuggestion()
        {
            GatewayArgsValidator.ClearCache();
            GatewayArgsValidator.PrimeCache("genexus_edit", MakeSchema(
                props: new (string, string, string[]?)[] { ("mode", "string", new[] { "full", "patch", "ops" }) }
            ));

            var result = GatewayArgsValidator.Validate("genexus_edit", new JObject { ["mode"] = "patche" });

            Assert.False(result.Ok);
            var v = Assert.Single(result.Violations);
            Assert.Equal("mode", v.Path);
            Assert.Equal("patch", v.Suggestion);
        }

        [Fact]
        public void Validate_OutOfEnumValue_FarValue_NoSuggestion()
        {
            GatewayArgsValidator.ClearCache();
            GatewayArgsValidator.PrimeCache("genexus_edit", MakeSchema(
                props: new (string, string, string[]?)[] { ("mode", "string", new[] { "full", "patch", "ops" }) }
            ));

            var result = GatewayArgsValidator.Validate("genexus_edit", new JObject { ["mode"] = "completely_wrong" });

            Assert.False(result.Ok);
            Assert.Null(Assert.Single(result.Violations).Suggestion);
        }

        // ── 7c. Unknown-arg typo detection (no additionalProperties:false) ────

        [Fact]
        public void Validate_UnknownArg_TypoOfDeclaredProp_FailsLoudWithSuggestion()
        {
            GatewayArgsValidator.ClearCache();
            GatewayArgsValidator.PrimeCache("genexus_read", MakeSchema(
                props: new[] { ("name", "string", (string[]?)null), ("part", "string", (string[]?)null) }
            ));

            // "nam" is one edit from the declared "name" → high-confidence typo.
            var result = GatewayArgsValidator.Validate("genexus_read", new JObject { ["nam"] = "Foo" });

            Assert.False(result.Ok);
            var v = Assert.Single(result.Violations);
            Assert.Equal("nam", v.Path);
            Assert.Equal("unknown argument", v.Actual);
            Assert.Equal("name", v.Suggestion);
        }

        [Fact]
        public void Validate_UnknownArg_NotCloseToAnyProp_IsNotFlagged()
        {
            GatewayArgsValidator.ClearCache();
            GatewayArgsValidator.PrimeCache("genexus_read", MakeSchema(
                props: new[] { ("name", "string", (string[]?)null) }
            ));

            // "include" isn't within edit distance of "name" — undeclared passthrough
            // args must NOT be rejected (only high-confidence typos fail loud).
            var result = GatewayArgsValidator.Validate("genexus_read", new JObject { ["include"] = "x" });

            Assert.True(result.Ok);
            Assert.Empty(result.Violations);
        }

        [Fact]
        public void Validate_CrossCuttingArg_IsNeverFlagged()
        {
            GatewayArgsValidator.ClearCache();
            // Declare a prop close enough that a naive check might misfire, then
            // confirm the cross-cutting allowlist wins.
            GatewayArgsValidator.PrimeCache("genexus_query", MakeSchema(
                props: new[] { ("name", "string", (string[]?)null) }
            ));

            var result = GatewayArgsValidator.Validate("genexus_query", new JObject
            {
                ["name"] = "Foo",
                ["axiCompact"] = false,
                ["projection"] = "minimal",
                ["fields"] = new JArray("name")
            });

            Assert.True(result.Ok);
            Assert.Empty(result.Violations);
        }

        // ── 8. End-to-end: bad args via ProcessMcpRequest → InvalidArgs envelope ──

        [Fact]
        public async Task ProcessMcpRequest_MissingRequiredArg_ReturnsInvalidArgsEnvelope_WithoutWorker()
        {
            // Prime cache with a schema that requires "name" for genexus_inspect.
            // This bypasses file-loading and ensures isolation from other tests.
            GatewayArgsValidator.ClearCache();
            GatewayArgsValidator.PrimeCache("genexus_inspect", MakeSchema(
                props: new[] { ("name", "string", (string[]?)null) },
                required: new[] { "name" }
            ));

            var request = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = "e2e-test-1",
                ["method"] = "tools/call",
                ["params"] = new JObject
                {
                    ["name"] = "genexus_inspect",
                    // "name" arg intentionally omitted — required by schema
                    ["arguments"] = new JObject { ["include"] = new JArray("metadata") }
                }
            };

            var response = await Program.ProcessMcpRequest(request);

            Assert.NotNull(response);
            // Should be a result (tool envelope), not a JSON-RPC error
            var result = response!["result"] as JObject;
            Assert.NotNull(result);
            var content = result!["content"] as JArray;
            Assert.NotNull(content);
            string text = content![0]?["text"]?.ToString() ?? "";
            var payload = JObject.Parse(text);

            Assert.Equal("error", payload["status"]?.ToString());
            var error = payload["error"] as JObject;
            Assert.NotNull(error);
            Assert.Equal("InvalidArgs", error!["code"]?.ToString());
            Assert.Contains("genexus_inspect", error["message"]?.ToString() ?? "");

            var violations = error["violations"] as JArray;
            Assert.NotNull(violations);
            Assert.Contains(violations!.OfType<JObject>(), v => v["path"]?.ToString() == "name" && v["actual"]?.ToString() == "missing");

            var nextSteps = error["nextSteps"] as JArray;
            Assert.NotNull(nextSteps);
            Assert.Contains(nextSteps!.OfType<JObject>(), s => s["tool"]?.ToString() == "genexus_orient");
        }

        [Fact]
        public void Validate_Issue302_SearchSourceUnknownArg_FailsWithSuggestion()
        {
            GatewayArgsValidator.ClearCache();
            var result = GatewayArgsValidator.Validate("genexus_search_source", new JObject
            {
                ["pattern"] = "&trace",
                ["objects"] = "pTarifaLoggi,pAPIPrazo",
                ["limit"] = 120,
                ["kbAlias"] = "MC30"
            });

            Assert.False(result.Ok);
            Assert.Contains(result.Violations, v => v.Path == "objects" && v.Suggestion == "objectName");
            Assert.Contains(result.Violations, v => v.Path == "limit" && v.Suggestion == "maxResults");
            Assert.DoesNotContain(result.Violations, v => v.Path == "kbAlias");
        }

        // ── #655: the rejection says which field is wrong and what is accepted ──

        private static async Task<JObject> InvalidArgsErrorFor(string tool, JObject args)
        {
            var request = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = "invalid-args-report",
                ["method"] = "tools/call",
                ["params"] = new JObject { ["name"] = tool, ["arguments"] = args }
            };
            var response = await Program.ProcessMcpRequest(request);
            var payload = JObject.Parse(response!["result"]!["content"]![0]!["text"]!.ToString());
            Assert.Equal("InvalidArgs", payload["error"]!["code"]!.ToString());
            return (JObject)payload["error"]!;
        }

        [Fact]
        public async Task InvalidArgs_MessageNamesTheOffendingFieldsAndTheAcceptedValues()
        {
            GatewayArgsValidator.ClearCache();
            GatewayArgsValidator.PrimeCache("genexus_edit", MakeSchema(
                props: new[]
                {
                    ("name", "string", (string[]?)null),
                    ("validate", "string", new[] { "strict", "best-effort", "only" }),
                },
                required: new[] { "name" },
                additionalPropertiesFalse: true));

            var error = await InvalidArgsErrorFor("genexus_edit", new JObject
            {
                ["name"] = "SyntheticProc",
                ["validate"] = "none",
                ["bogus"] = 1,
            });

            string message = error["message"]!.ToString();
            Assert.Contains("genexus_edit", message);
            Assert.Contains("'validate' expected one of [strict, best-effort, only], got none", message);
            Assert.Contains("'bogus' is not an argument of this tool", message);

            // The hint walks the other violations too, and the unknown argument lists the names to use.
            string hint = error["hint"]!.ToString();
            Assert.Contains("Field 'validate': expected one of [strict, best-effort, only], got none.", hint);
            Assert.Contains("Also: 'bogus' is not an argument of this tool", hint);
            var unknown = ((JArray)error["violations"]!).OfType<JObject>().Single(v => v["path"]!.ToString() == "bogus");
            Assert.Equal(new[] { "name", "validate" }, unknown["accepted"]!.Select(t => t.ToString()).ToArray());
        }

        [Fact]
        public async Task InvalidArgs_UnknownArgumentHintListsTheAcceptedNames()
        {
            GatewayArgsValidator.ClearCache();
            GatewayArgsValidator.PrimeCache("genexus_structure", MakeSchema(
                props: new[]
                {
                    ("action", "string", new[] { "get_visual", "add", "remove" }),
                    ("name", "string", (string[]?)null),
                },
                additionalPropertiesFalse: true));

            var error = await InvalidArgsErrorFor("genexus_structure", new JObject
            {
                ["action"] = "get_visual",
                ["name"] = "SyntheticTrn",
                ["mode"] = "read",
            });

            Assert.Contains("Unknown argument 'mode'.", error["hint"]!.ToString());
            Assert.Contains("Accepted arguments: action, name.", error["hint"]!.ToString());
        }

        [Fact]
        public void InvalidArgsReport_CapsTheAcceptedNamesAndTheClausesOfALongViolationList()
        {
            var accepted = Enumerable.Range(1, 20).Select(i => "arg" + i).ToList();
            var violations = new List<GatewayArgsValidator.Violation>
            {
                new() { Path = "zzz", Expected = "none (additionalProperties: false)", Actual = "unknown argument", Accepted = accepted },
                new() { Path = "a", Expected = "string", Actual = "number" },
                new() { Path = "b", Expected = "string", Actual = "number" },
                new() { Path = "c", Expected = "string", Actual = "number" },
                new() { Path = "d", Expected = "string", Actual = "number" },
            };

            string hint = InvalidArgsReport.Hint(violations);
            Assert.Contains("arg15, and 5 more (see violations[].accepted)", hint);
            Assert.DoesNotContain("arg16,", hint);
            Assert.Contains("and 2 more (see violations)", hint);

            string message = InvalidArgsReport.Message("genexus_x", violations);
            Assert.Contains("'zzz' is not an argument", message);
            Assert.Contains("'b' expected string, got number", message);
            Assert.DoesNotContain("'c'", message);
            Assert.EndsWith("; and 2 more.", message);
        }

        [Theory]
        [InlineData("read", new[] { "get", "set", "move" }, "get")]
        [InlineData("read", new[] { "get_visual", "add", "remove" }, "get_visual")]
        [InlineData("fetch", new[] { "inspect", "set" }, "inspect")]
        [InlineData("read", new[] { "set", "move" }, null)]
        public void DidYouMean_ReadVerbsPointAtTheGetActions(string input, string[] candidates, string? expected)
        {
            Assert.Equal(expected, DidYouMean.Suggest(input, candidates));
        }

        // ── #652: objectName as an array ─────────────────────────────────────

        [Fact]
        public void SearchSource_ObjectNameArray_IsRefusedUntilItIsJoined_ThenAccepted()
        {
            GatewayArgsValidator.ClearCache();
            var args = new JObject { ["pattern"] = "SoloSi", ["objectName"] = new JArray("ProcA", " ProcB ") };

            Assert.False(GatewayArgsValidator.Validate("genexus_search_source", args).Ok);

            Assert.True(ListArgumentCoercion.Apply("genexus_search_source", args));
            Assert.Equal("ProcA,ProcB", args["objectName"]!.ToString());
            Assert.True(GatewayArgsValidator.Validate("genexus_search_source", args).Ok);
        }

        [Theory]
        [InlineData("[]")]
        [InlineData("[1,2]")]
        [InlineData("[\"A\",\"\"]")]
        [InlineData("[\"A\",[\"B\"]]")]
        public void SearchSource_ObjectNameThatIsNotAListOfNames_IsLeftForTheValidator(string json)
        {
            var args = new JObject { ["pattern"] = "x", ["objectName"] = JArray.Parse(json) };

            Assert.False(ListArgumentCoercion.Apply("genexus_search_source", args));
            Assert.Equal(JTokenType.Array, args["objectName"]!.Type);
        }

        [Fact]
        public void ListCoercion_OnlyTouchesTheDocumentedArgumentOfItsTool()
        {
            var other = new JObject { ["objectName"] = new JArray("A", "B") };
            Assert.False(ListArgumentCoercion.Apply("genexus_read", other));
            Assert.Equal(JTokenType.Array, other["objectName"]!.Type);

            var text = new JObject { ["objectName"] = "A,B" };
            Assert.False(ListArgumentCoercion.Apply("genexus_search_source", text));
            Assert.False(ListArgumentCoercion.Apply("genexus_search_source", null));
        }
    }
}
