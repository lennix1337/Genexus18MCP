using System;
using System.Reflection;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class GatewayBudgetTests
    {
        [Theory]
        [InlineData("genexus_edit", "Events", 180000)]
        [InlineData("genexus_edit", "Source", 180000)]
        [InlineData("genexus_import_object", "Events", 300000)]
        [InlineData("genexus_import_object", "Rules", 300000)]
        [InlineData("genexus_query", null, 60000)]
        public void GetToolTimeoutMs_ShouldApplyPartAwareTimeoutPolicy(string toolName, string? part, int expected)
        {
            JObject? args = part == null ? null : new JObject { ["part"] = part };

            int timeoutMs = Program.GetToolTimeoutMs(toolName, args);

            Assert.Equal(expected, timeoutMs);
        }

        // Issue #656: the router sends an edit without `part` to Source, but the timeout
        // policy only looked at the literal argument, so `mode=patch` without `part` got the
        // 60s default ceiling while the same edit with part=Source got 180s. The Worker was
        // cut off mid-write and the caller was left not knowing whether the patch landed.
        [Theory]
        [InlineData("patch")]
        [InlineData("full")]
        [InlineData(null)]
        public void GetToolTimeoutMs_EditWithoutPartUsesTheSourceBudgetTheRouterDefaultsTo(string? mode)
        {
            var args = new JObject { ["name"] = "SyntheticProc" };
            if (mode != null) args["mode"] = mode;

            Assert.Equal(180000, Program.GetToolTimeoutMs("genexus_edit", args));
        }

        [Theory]
        [InlineData("genexus_lifecycle")]
        [InlineData("genexus_analyze")]
        [InlineData("genexus_test")]
        public void GetToolTimeoutMs_ShouldApplyHeavyOperationBudget(string toolName)
        {
            int timeoutMs = Program.GetToolTimeoutMs(toolName, null);

            Assert.Equal(600000, timeoutMs);
        }

        [Theory]
        [InlineData("wait", 60, 70000)]
        [InlineData("wait_seconds", 45, 55000)]
        public void LifecycleStatusWaitGetsRequestedBudgetInsteadOfGenericFiftySecondCap(string field, int wait, int expected)
        {
            int timeoutMs = Program.GetToolTimeoutMs("genexus_lifecycle", new JObject
            {
                ["action"] = "status",
                [field] = wait
            });
            Assert.Equal(expected, timeoutMs);
            Assert.True(timeoutMs > McpRouter.SafeLongPollSecondsWithoutProgress * 1000);
        }

        // Plan 070: deep=true on genexus_db (reorg_impact/reorg_preview/drift_check)
        // runs ISpecifierService.ImpactDatabase — a build-heavy specification pass that
        // exceeds the 60s default; the deep flag must lift the sync ceiling to 10 min.
        [Theory]
        [InlineData("genexus_db")]
        [InlineData("genexus_db_drift")]
        public void GetToolTimeoutMs_DeepDbAnalysis_GetsTenMinuteCeiling(string toolName)
        {
            int timeoutMs = Program.GetToolTimeoutMs(toolName, new JObject { ["deep"] = true, ["action"] = "reorg_impact" });
            Assert.Equal(600000, timeoutMs);
        }

        [Fact]
        public void GetToolTimeoutMs_ShallowDbAnalysis_KeepsDefaultCeiling()
        {
            // deep=false (or omitted) is the fast timestamp heuristic — 60s is fine.
            int withDeepFalse = Program.GetToolTimeoutMs("genexus_db", new JObject { ["deep"] = false, ["action"] = "reorg_impact" });
            int withoutDeep = Program.GetToolTimeoutMs("genexus_db", new JObject { ["action"] = "reorg_impact" });
            Assert.Equal(60000, withDeepFalse);
            Assert.Equal(60000, withoutDeep);
        }

        [Fact]
        public void GetToolTimeoutMs_DeepFlagOnOtherTools_KeepsDefaultCeiling()
        {
            // The deep flag must not leak the 10-min ceiling onto unrelated tools.
            int timeoutMs = Program.GetToolTimeoutMs("genexus_query", new JObject { ["deep"] = true });
            Assert.Equal(60000, timeoutMs);
        }

        [Fact]
        public void GetToolTimeoutMs_ApplyPattern_UsesReapplyWindowPlusCushion()
        {
            var prior = Environment.GetEnvironmentVariable("GENEXUS_MCP_REAPPLY_TIMEOUT_MS");
            try
            {
                Environment.SetEnvironmentVariable("GENEXUS_MCP_REAPPLY_TIMEOUT_MS", null);
                // default reapply window 300000 + 30000 gateway cushion
                Assert.Equal(330000, Program.GetToolTimeoutMs("genexus_apply_pattern", null));

                // env override is honoured and the cushion still added
                Environment.SetEnvironmentVariable("GENEXUS_MCP_REAPPLY_TIMEOUT_MS", "120000");
                Assert.Equal(150000, Program.GetToolTimeoutMs("genexus_apply_pattern", null));
            }
            finally
            {
                Environment.SetEnvironmentVariable("GENEXUS_MCP_REAPPLY_TIMEOUT_MS", prior);
            }
        }

        [Fact]
        public void TruncateResponseIfNeeded_ShouldPreserveSearchResultsInsteadOfReturningErrorEnvelope()
        {
            var results = new JArray();
            for (int i = 0; i < 400; i++)
            {
                results.Add(new JObject
                {
                    ["guid"] = Guid.NewGuid().ToString(),
                    ["name"] = $"Object{i:D4}",
                    ["type"] = "Folder",
                    ["description"] = new string('X', 300),
                    ["parent"] = "Root Module"
                });
            }

            var payload = new JObject
            {
                ["count"] = results.Count,
                ["results"] = results
            };

            var method = typeof(Program).GetMethod(
                "TruncateResponseIfNeeded",
                BindingFlags.NonPublic | BindingFlags.Static);

            Assert.NotNull(method);

            var truncated = (JToken?)method!.Invoke(null, new object?[] { payload, "genexus_query" });

            Assert.NotNull(truncated);
            var obj = Assert.IsType<JObject>(truncated);
            Assert.Null(obj["error"]);
            Assert.NotNull(obj["results"]);
            Assert.True(obj["results"] is JArray);
            Assert.True(((JArray)obj["results"]!).Count > 0);
            Assert.True(obj["isTruncated"]?.Value<bool>() ?? false);
            Assert.True(obj.ToString(Newtonsoft.Json.Formatting.None).Length <= 80000);
        }

        // Issue #888: an oversized result list was cut to a fixed 5 rows whatever the caller's limit,
        // costing dozens of calls to page a listing the budget could mostly hold.
        [Fact]
        public void TruncateResponseIfNeeded_KeepsAsManyResultRowsAsTheBudgetHolds()
        {
            var results = new JArray();
            for (int i = 0; i < 1200; i++)
            {
                results.Add(new JObject { ["name"] = $"Panel{i:D4}", ["type"] = "WebPanel", ["description"] = new string('X', 40) });
            }
            var payload = new JObject { ["count"] = 1200, ["total"] = 1200, ["returned"] = 1200, ["results"] = results };

            var method = typeof(Program).GetMethod("TruncateResponseIfNeeded", BindingFlags.NonPublic | BindingFlags.Static);
            var obj = Assert.IsType<JObject>(method!.Invoke(null, new object?[] { payload, "genexus_query" }));

            int kept = ((JArray)obj["results"]!).Count;
            Assert.True(kept > 200, $"kept only {kept} rows");
            Assert.True(kept < 1200);
            Assert.True(obj.ToString(Newtonsoft.Json.Formatting.None).Length <= 80000);
            Assert.Equal(kept, obj["returned"]!.Value<int>());
            Assert.Equal(kept, obj["returnedCount"]!.Value<int>());
            Assert.Equal(1200, obj["originalCount"]!.Value<int>());
            Assert.True(obj["hasMore"]!.Value<bool>());
            Assert.True(obj["isTruncated"]!.Value<bool>());
        }

        [Fact]
        public void SingularRead_LongLineMustNotBeCutAtAnUnreachableLineOffset()
        {
            string source = new string('x', 210000);
            var payload = new JObject { ["part"] = "Source", ["source"] = source, ["explicitFullRead"] = true, ["offset"] = 0 };
            var method = typeof(Program).GetMethod("TruncateResponseIfNeeded", BindingFlags.NonPublic | BindingFlags.Static);
            var result = (JObject)method!.Invoke(null, new object?[] { payload, "genexus_read" })!;
            Assert.Equal(source, result["source"]!.Value<string>());
            Assert.Null(result["gatewaySafeNextOffset"]);
        }

        [Fact]
        public void TruncateResponseIfNeeded_ShouldDropDerivedReadMetadataBeforeTruncatingSource()
        {
            var payload = new JObject
            {
                ["name"] = "RelControleExtensaoHoras",
                ["source"] = new string('S', 26000),
                ["variables"] = new JArray(new JObject { ["name"] = "Linha", ["type"] = "Numeric(4)" }),
                ["calls"] = new JArray(new JObject { ["name"] = "SubRotina", ["parmRule"] = "parm(in:&Linha);" }),
                ["dataSchema"] = new JArray(new JObject { ["table"] = "Horas" }),
                ["patternMetadata"] = new JObject { ["pattern"] = "WorkWithPlus" }
            };

            var method = typeof(Program).GetMethod(
                "TruncateResponseIfNeeded",
                BindingFlags.NonPublic | BindingFlags.Static);

            Assert.NotNull(method);

            var truncated = (JToken?)method!.Invoke(null, new object?[] { payload, "genexus_read" });

            Assert.NotNull(truncated);
            var obj = Assert.IsType<JObject>(truncated);
            Assert.Null(obj["variables"]);
            Assert.Null(obj["calls"]);
            Assert.Null(obj["dataSchema"]);
            Assert.Null(obj["patternMetadata"]);
            Assert.NotNull(obj["source"]);
            Assert.True(obj["isTruncated"]?.Value<bool>() ?? false);
            Assert.Contains("Gateway trimmed derived metadata", obj["message"]?.Value<string>() ?? string.Empty);
        }

        [Fact]
        public void TruncateResponseIfNeeded_ShouldPreserveLargeVisualLayoutXmlWithinVisualBudget()
        {
            var payload = new JObject
            {
                ["part"] = "Layout",
                ["contentType"] = "application/xml",
                ["source"] = new string('X', 50000)
            };

            var method = typeof(Program).GetMethod(
                "TruncateResponseIfNeeded",
                BindingFlags.NonPublic | BindingFlags.Static);

            Assert.NotNull(method);

            var truncated = (JToken?)method!.Invoke(null, new object?[] { payload, "genexus_read" });

            Assert.NotNull(truncated);
            var obj = Assert.IsType<JObject>(truncated);
            Assert.Equal("Layout", obj["part"]?.ToString());
            Assert.Equal(50000, obj["source"]?.Value<string>()?.Length);
            Assert.Null(obj["isTruncated"]);
        }

        [Fact]
        public void TruncateResponseIfNeeded_ShouldPreservePatternInstanceXmlWithinMetadataBudget()
        {
            var payload = new JObject
            {
                ["part"] = "PatternInstance",
                ["contentType"] = "application/xml",
                ["source"] = new string('P', 50000)
            };

            var method = typeof(Program).GetMethod(
                "TruncateResponseIfNeeded",
                BindingFlags.NonPublic | BindingFlags.Static);

            Assert.NotNull(method);

            var truncated = (JToken?)method!.Invoke(null, new object?[] { payload, "genexus_read" });

            Assert.NotNull(truncated);
            var obj = Assert.IsType<JObject>(truncated);
            Assert.Equal("PatternInstance", obj["part"]?.ToString());
            Assert.Equal(50000, obj["source"]?.Value<string>()?.Length);
            Assert.Null(obj["isTruncated"]);
        }

        [Fact]
        public void NormalizeToolPayloadForAxi_ShouldAddMetaAndListAggregates()
        {
            var payload = new JObject
            {
                ["count"] = 3,
                ["results"] = new JArray(
                    new JObject { ["name"] = "A" },
                    new JObject { ["name"] = "B" })
            };

            var args = new JObject
            {
                ["limit"] = 2,
                ["offset"] = 0
            };

            var method = typeof(Program).GetMethod(
                "NormalizeToolPayloadForAxi",
                BindingFlags.NonPublic | BindingFlags.Static);

            Assert.NotNull(method);

            var normalized = (JToken?)method!.Invoke(null, new object?[] { payload, "genexus_query", args, false });
            Assert.NotNull(normalized);

            var obj = Assert.IsType<JObject>(normalized);
            // schemaVersion/tool no longer duplicated per response — see initialize handshake.
            Assert.Null(obj["meta"]?["schemaVersion"]);
            Assert.Null(obj["meta"]?["tool"]);
            Assert.Equal(2, obj["returned"]?.Value<int>());
            Assert.Equal(3, obj["total"]?.Value<int>());
            Assert.False(obj["empty"]?.Value<bool>() ?? true);
            Assert.True(obj["hasMore"]?.Value<bool>() ?? false);
            Assert.Equal(2, obj["nextOffset"]?.Value<int>());
        }

        [Fact]
        public void NormalizeToolPayloadForAxi_ShouldMarkNoChangeOnSuccessfulNoChangeWrite()
        {
            // v2.8.0 — canonical envelope: status:"ok" with code:"NoChange" (or similar),
            // payload nested under result. Gateway's noChange detector recognises both
            // the explicit code AND the legacy details:"No change" fallback.
            var payload = new JObject
            {
                ["status"] = "ok",
                ["code"] = "NoChange",
                ["result"] = new JObject
                {
                    ["details"] = "No change"
                }
            };

            var method = typeof(Program).GetMethod(
                "NormalizeToolPayloadForAxi",
                BindingFlags.NonPublic | BindingFlags.Static);

            Assert.NotNull(method);

            var normalized = (JToken?)method!.Invoke(null, new object?[] { payload, "genexus_edit", null, false });
            Assert.NotNull(normalized);

            var obj = Assert.IsType<JObject>(normalized);
            Assert.True(obj["noChange"]?.Value<bool>() ?? false);
            // schemaVersion is now emitted once at initialize, not per-response.
            Assert.Null(obj["meta"]?["schemaVersion"]);
        }

        [Fact]
        public void NormalizeToolPayloadForAxi_ShouldAppendTruncationHintWhenTruncated()
        {
            var payload = new JObject
            {
                ["isTruncated"] = true,
                ["source"] = "abc"
            };

            var method = typeof(Program).GetMethod(
                "NormalizeToolPayloadForAxi",
                BindingFlags.NonPublic | BindingFlags.Static);

            Assert.NotNull(method);

            var normalized = (JToken?)method!.Invoke(null, new object?[] { payload, "genexus_read", null, false });
            Assert.NotNull(normalized);

            var obj = Assert.IsType<JObject>(normalized);
            Assert.True(obj["meta"]?["truncated"]?.Value<bool>() ?? false);
            var help = Assert.IsType<JArray>(obj["help"]);
            Assert.Contains(help, item => item?.ToString()?.Contains("Use limit/offset", StringComparison.OrdinalIgnoreCase) == true);
        }

        [Fact]
        public void NormalizeToolPayloadForAxi_ShouldProjectRequestedFieldsForQuery()
        {
            var payload = new JObject
            {
                ["results"] = new JArray(
                    new JObject { ["name"] = "A", ["type"] = "Procedure", ["path"] = "M/Procs", ["description"] = "Long text" })
            };

            var args = new JObject
            {
                ["fields"] = "name,type"
            };

            var method = typeof(Program).GetMethod(
                "NormalizeToolPayloadForAxi",
                BindingFlags.NonPublic | BindingFlags.Static);

            Assert.NotNull(method);

            var normalized = (JToken?)method!.Invoke(null, new object?[] { payload, "genexus_query", args, false });
            var obj = Assert.IsType<JObject>(normalized);
            var rows = Assert.IsType<JArray>(obj["results"]);
            var first = Assert.IsType<JObject>(rows[0]);

            Assert.NotNull(first["name"]);
            Assert.NotNull(first["type"]);
            Assert.Null(first["path"]);
            Assert.Equal("name", obj["meta"]?["fields"]?[0]?.ToString());
            Assert.Equal("type", obj["meta"]?["fields"]?[1]?.ToString());
        }

        [Fact]
        public void NormalizeToolPayloadForAxi_ShouldApplyCompactDefaultsForListObjects()
        {
            var payload = new JObject
            {
                ["results"] = new JArray(
                    new JObject
                    {
                        ["name"] = "InvoiceProc",
                        ["type"] = "Procedure",
                        ["path"] = "Main/Procs/InvoiceProc",
                        ["parentPath"] = "Main/Procs",
                        ["description"] = "verbose"
                    })
            };

            var args = new JObject
            {
                ["axiCompact"] = true
            };

            var method = typeof(Program).GetMethod(
                "NormalizeToolPayloadForAxi",
                BindingFlags.NonPublic | BindingFlags.Static);

            Assert.NotNull(method);

            var normalized = (JToken?)method!.Invoke(null, new object?[] { payload, "genexus_list_objects", args, false });
            var obj = Assert.IsType<JObject>(normalized);
            var first = Assert.IsType<JObject>(Assert.IsType<JArray>(obj["results"])[0]);

            Assert.NotNull(first["name"]);
            Assert.NotNull(first["type"]);
            Assert.NotNull(first["path"]);
            Assert.NotNull(first["parentPath"]);
            Assert.Null(first["description"]);
        }

        [Fact]
        public void NormalizeToolPayloadForAxi_ShouldAddEmptyStateHelpForQuery()
        {
            var payload = new JObject
            {
                ["results"] = new JArray()
            };

            var method = typeof(Program).GetMethod(
                "NormalizeToolPayloadForAxi",
                BindingFlags.NonPublic | BindingFlags.Static);

            Assert.NotNull(method);

            var normalized = (JToken?)method!.Invoke(null, new object?[] { payload, "genexus_query", null, false });
            var obj = Assert.IsType<JObject>(normalized);

            Assert.True(obj["empty"]?.Value<bool>() ?? false);
            var help = Assert.IsType<JArray>(obj["help"]);
            Assert.Contains(help, item => item?.ToString()?.Contains("No matches found", StringComparison.OrdinalIgnoreCase) == true);
        }

        [Fact]
        public void NormalizeToolPayloadForAxi_ShouldWrapArrayPayloadIntoResultsObject()
        {
            var payload = new JArray(
                new JObject { ["name"] = "A", ["type"] = "Folder" },
                new JObject { ["name"] = "B", ["type"] = "Procedure" }
            );

            var args = new JObject
            {
                ["limit"] = 10,
                ["offset"] = 0
            };

            var method = typeof(Program).GetMethod(
                "NormalizeToolPayloadForAxi",
                BindingFlags.NonPublic | BindingFlags.Static);

            Assert.NotNull(method);

            var normalized = (JToken?)method!.Invoke(null, new object?[] { payload, "genexus_list_objects", args, false });
            var obj = Assert.IsType<JObject>(normalized);

            // schemaVersion/tool no longer duplicated per response — see initialize handshake.
            Assert.Null(obj["meta"]?["schemaVersion"]);
            Assert.Null(obj["meta"]?["tool"]);
            Assert.Equal(2, obj["returned"]?.Value<int>());
            Assert.False(obj["empty"]?.Value<bool>() ?? true);
            Assert.True(obj["results"] is JArray);
        }

        [Fact]
        public void NormalizeToolPayloadForAxi_ShouldApplyCompactByDefault_WhenAxiCompactOmitted()
        {
            var payload = new JObject
            {
                ["results"] = new JArray(
                    new JObject
                    {
                        ["name"] = "InvoiceProc",
                        ["type"] = "Procedure",
                        ["path"] = "Main/Procs/InvoiceProc",
                        ["parentPath"] = "Main/Procs",
                        ["description"] = "verbose"
                    })
            };

            var args = new JObject();

            var method = typeof(Program).GetMethod(
                "NormalizeToolPayloadForAxi",
                BindingFlags.NonPublic | BindingFlags.Static);

            Assert.NotNull(method);

            var normalized = (JToken?)method!.Invoke(null, new object?[] { payload, "genexus_list_objects", args, false });
            var obj = Assert.IsType<JObject>(normalized);
            var first = Assert.IsType<JObject>(Assert.IsType<JArray>(obj["results"])[0]);

            Assert.NotNull(first["name"]);
            Assert.NotNull(first["type"]);
            Assert.NotNull(first["path"]);
            Assert.NotNull(first["parentPath"]);
            Assert.Null(first["description"]); // omitted under compact default
        }

        [Fact]
        public void NormalizeToolPayloadForAxi_ShouldReturnFullFields_WhenAxiCompactFalse()
        {
            var payload = new JObject
            {
                ["results"] = new JArray(
                    new JObject
                    {
                        ["name"] = "InvoiceProc",
                        ["type"] = "Procedure",
                        ["path"] = "Main/Procs/InvoiceProc",
                        ["parentPath"] = "Main/Procs",
                        ["description"] = "verbose"
                    })
            };

            var args = new JObject { ["axiCompact"] = false };

            var method = typeof(Program).GetMethod(
                "NormalizeToolPayloadForAxi",
                BindingFlags.NonPublic | BindingFlags.Static);

            var normalized = (JToken?)method!.Invoke(null, new object?[] { payload, "genexus_list_objects", args, false });
            var obj = Assert.IsType<JObject>(normalized);
            var first = Assert.IsType<JObject>(Assert.IsType<JArray>(obj["results"])[0]);

            Assert.NotNull(first["description"]); // full payload preserved
        }

        [Fact]
        public void NormalizeToolPayloadForAxi_ShouldRespectExplicitFields_OverrideCompactDefault()
        {
            var payload = new JObject
            {
                ["results"] = new JArray(
                    new JObject
                    {
                        ["name"] = "InvoiceProc",
                        ["type"] = "Procedure",
                        ["description"] = "verbose"
                    })
            };

            var args = new JObject { ["fields"] = "name,description" };

            var method = typeof(Program).GetMethod(
                "NormalizeToolPayloadForAxi",
                BindingFlags.NonPublic | BindingFlags.Static);

            var normalized = (JToken?)method!.Invoke(null, new object?[] { payload, "genexus_list_objects", args, false });
            var obj = Assert.IsType<JObject>(normalized);
            var first = Assert.IsType<JObject>(Assert.IsType<JArray>(obj["results"])[0]);

            Assert.NotNull(first["name"]);
            Assert.NotNull(first["description"]);
            Assert.Null(first["type"]); // not requested
        }

        [Fact]
        public void NormalizeToolPayloadForAxi_ShouldAddAggregatesForNestedResultCollection()
        {
            // genexus_api shape: array lives under result.endpoints, not top-level.
            var payload = new JObject
            {
                ["status"] = "ok",
                ["result"] = new JObject
                {
                    ["endpoints"] = new JArray(
                        new JObject { ["type"] = "Api" },
                        new JObject { ["type"] = "Api" })
                }
            };

            var method = typeof(Program).GetMethod(
                "NormalizeToolPayloadForAxi",
                BindingFlags.NonPublic | BindingFlags.Static);

            Assert.NotNull(method);

            var normalized = (JToken?)method!.Invoke(null, new object?[] { payload, "genexus_api", null, false });
            var obj = Assert.IsType<JObject>(normalized);

            Assert.Equal(2, obj["returned"]?.Value<int>());
            Assert.False(obj["empty"]?.Value<bool>() ?? true);
            Assert.Equal(2, obj["meta"]?["totalByType"]?["Api"]?.Value<int>());
        }

        [Fact]
        public void NormalizeToolPayloadForAxi_ShouldFlagEmptyForNestedEmptyCollection()
        {
            var payload = new JObject
            {
                ["status"] = "ok",
                ["result"] = new JObject
                {
                    ["endpoints"] = new JArray()
                }
            };

            var method = typeof(Program).GetMethod(
                "NormalizeToolPayloadForAxi",
                BindingFlags.NonPublic | BindingFlags.Static);

            Assert.NotNull(method);

            var normalized = (JToken?)method!.Invoke(null, new object?[] { payload, "genexus_api", null, false });
            var obj = Assert.IsType<JObject>(normalized);

            Assert.Equal(0, obj["returned"]?.Value<int>());
            Assert.True(obj["empty"]?.Value<bool>() ?? false);
            var help = Assert.IsType<JArray>(obj["help"]);
            Assert.Contains(help, item => item?.ToString()?.Contains("No results returned", StringComparison.OrdinalIgnoreCase) == true);
        }

        [Fact]
        public void NormalizeToolPayloadForAxi_ShouldStillEnrichTopLevelResultsRegression()
        {
            // Regression guard: top-level `results` (query/list_objects shape) must
            // keep working exactly as before the nested/broadened lookup was added.
            var payload = new JObject
            {
                ["count"] = 1,
                ["results"] = new JArray(new JObject { ["name"] = "A", ["type"] = "Procedure" })
            };

            var method = typeof(Program).GetMethod(
                "NormalizeToolPayloadForAxi",
                BindingFlags.NonPublic | BindingFlags.Static);

            Assert.NotNull(method);

            var normalized = (JToken?)method!.Invoke(null, new object?[] { payload, "genexus_query", null, false });
            var obj = Assert.IsType<JObject>(normalized);

            Assert.Equal(1, obj["returned"]?.Value<int>());
            Assert.False(obj["empty"]?.Value<bool>() ?? true);
            Assert.Equal(1, obj["meta"]?["totalByType"]?["Procedure"]?.Value<int>());
        }

        [Fact]
        public void NormalizeToolPayloadForAxi_ShouldNotAddAggregatesWhenNoCollectionPresent()
        {
            var payload = new JObject
            {
                ["status"] = "ok",
                ["result"] = new JObject
                {
                    ["note"] = "x"
                }
            };

            var method = typeof(Program).GetMethod(
                "NormalizeToolPayloadForAxi",
                BindingFlags.NonPublic | BindingFlags.Static);

            Assert.NotNull(method);

            var normalized = (JToken?)method!.Invoke(null, new object?[] { payload, "genexus_gxserver", null, false });
            var obj = Assert.IsType<JObject>(normalized);

            Assert.Null(obj["returned"]);
            Assert.Null(obj["empty"]);
            Assert.Null(obj["meta"]);
        }
    }
}
