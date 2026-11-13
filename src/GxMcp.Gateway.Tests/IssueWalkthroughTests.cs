using System;
using System.Collections.Generic;
using System.Linq;
using GxMcp.Gateway;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    // genexus_doctor's issueReport.walkthrough: the calls that led to a failure, safe to paste
    // into a public issue and complete enough to replay on a test KB (#961, #890, #959, #965
    // could not be reproduced without the reporters' KBs).
    public class IssueWalkthroughTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 11, 13, 3, 0, 0, DateTimeKind.Utc);

        private static ISet<string> Words(params string[] words) =>
            new HashSet<string>(words, StringComparer.OrdinalIgnoreCase);

        private static Func<string, ISet<string>> Vocabulary => tool => tool switch
        {
            "genexus_wwp" => Words("action", "name", "kb", "collection", "rollbackOnFailure", "add_grid"),
            "genexus_read" => Words("name", "part", "kb", "Source"),
            "genexus_edit" => Words("name", "content", "properties"),
            _ => Words()
        };

        private static IssueStep Step(int second, string tool, JObject args, string status = "Completed",
            string? code = null, string? message = null, string? exceptionType = null, string? failureTrace = null) =>
            new IssueStep
            {
                AtUtc = T0.AddSeconds(second),
                Tool = tool,
                Status = status,
                DurationMs = 120,
                Arguments = args,
                Code = code,
                Message = message,
                ExceptionType = exceptionType,
                FailureTrace = failureTrace
            };

        [Fact]
        public void SchemaWordsStay_OtherValuesBecomeStableTokens()
        {
            var steps = new[]
            {
                Step(0, "genexus_read", new JObject { ["name"] = "WWCustomer", ["part"] = "Source", ["kb"] = "AcmeKb" }),
                Step(3, "genexus_wwp", new JObject
                {
                    ["action"] = "add_grid", ["name"] = "WWCustomer", ["kb"] = "AcmeKb",
                    ["collection"] = "OrderItems", ["rollbackOnFailure"] = true
                }, status: "Failed", code: "WwpGridFailed",
                    message: @"Type DropDownValueOptionCustomType not found while editing WWCustomer at C:\KBs\Acme\WWCustomer.xml",
                    exceptionType: "WwpAttributeRejected", failureTrace: "WwpActionService.SetNativeDropDown")
            };

            JArray walkthrough = IssueWalkthrough.Build(steps, Vocabulary);

            Assert.Equal(2, walkthrough.Count);
            var read = (JObject)walkthrough[0]!;
            var grid = (JObject)walkthrough[1]!;
            Assert.Equal("Source", read["args"]!["part"]!.ToString());
            Assert.Equal("<v1>", read["args"]!["name"]!.ToString());
            Assert.Equal("<v2>", read["args"]!["kb"]!.ToString());
            Assert.Equal("add_grid", grid["args"]!["action"]!.ToString());
            Assert.Equal("<v1>", grid["args"]!["name"]!.ToString());
            Assert.Equal("<v2>", grid["args"]!["kb"]!.ToString());
            Assert.Equal("<v3>", grid["args"]!["collection"]!.ToString());
            Assert.True(grid["args"]!["rollbackOnFailure"]!.Value<bool>());
            Assert.Equal(3.0, grid["atSeconds"]!.Value<double>());
            Assert.Equal("Failed", grid["outcome"]!.ToString());
            Assert.Equal("WwpGridFailed", grid["code"]!.ToString());
            Assert.Equal("WwpAttributeRejected", grid["exceptionType"]!.ToString());
            Assert.Equal("WwpActionService.SetNativeDropDown", grid["failureTrace"]!.ToString());
            string message = grid["message"]!.ToString();
            Assert.Contains("DropDownValueOptionCustomType", message);
            Assert.Contains("<v1>", message);
            Assert.Contains("<path>", message);

            string all = walkthrough.ToString();
            Assert.DoesNotContain("WWCustomer", all);
            Assert.DoesNotContain("AcmeKb", all);
            Assert.DoesNotContain("OrderItems", all);
            Assert.DoesNotContain(@"C:\KBs", all);
        }

        [Fact]
        public void SourceTextAndUndeclaredKeysNeverLeave()
        {
            var steps = new[]
            {
                Step(0, "genexus_edit", new JObject
                {
                    ["name"] = "PrcInvoice",
                    ["content"] = "&Total = &Price * 2\nMsg('secret rule')",
                    ["properties"] = new JObject { ["CustomerTaxId"] = "123", ["Count"] = 2 }
                })
            };

            var args = (JObject)IssueWalkthrough.Build(steps, Vocabulary)[0]!["args"]!;

            Assert.Equal("<text 38 chars>", args["content"]!.ToString());
            var properties = (JObject)args["properties"]!;
            Assert.DoesNotContain(properties.Properties(), p => p.Name == "CustomerTaxId");
            Assert.Contains(properties.Properties(), p => p.Value.Type == JTokenType.Integer && p.Value.Value<int>() == 2);
            Assert.DoesNotContain("secret rule", args.ToString());
        }

        [Fact]
        public void KeepsTheLastStepsOldestFirst()
        {
            var steps = Enumerable.Range(0, IssueWalkthrough.MaxSteps + 5)
                .Select(i => Step(i, "genexus_read", new JObject { ["part"] = "Source" }))
                .Reverse()
                .ToArray();

            JArray walkthrough = IssueWalkthrough.Build(steps, Vocabulary);

            Assert.Equal(IssueWalkthrough.MaxSteps, walkthrough.Count);
            Assert.Equal(1, walkthrough[0]!["step"]!.Value<int>());
            Assert.Equal(0.0, walkthrough[0]!["atSeconds"]!.Value<double>());
            Assert.Equal(IssueWalkthrough.MaxSteps - 1.0, walkthrough.Last!["atSeconds"]!.Value<double>());
        }

        [Fact]
        public void TrackerSnapshot_CarriesTheWorkerFailureAndSkipsExcludedTools()
        {
            var tracker = new OperationTracker(TimeSpan.FromMinutes(60));
            tracker.StartOperation("r1", "genexus_wwp", new JObject { ["action"] = "add_grid" }, "c1");
            tracker.CompleteFromWorker("r1", JObject.Parse(@"{ id: 'r1', result: {
                status: 'Error',
                error: { code: 'WwpGridFailed', message: 'Object reference not set' },
                exceptionType: 'NullReferenceException',
                failureTrace: 'Artech.Patterns.Element.AddChild' } }"));
            tracker.StartOperation("r2", "genexus_doctor", null, "c2");

            IReadOnlyList<IssueStep> steps = tracker.SnapshotIssueSteps(10, tool => tool != "genexus_doctor");

            IssueStep step = Assert.Single(steps);
            Assert.Equal("genexus_wwp", step.Tool);
            Assert.Equal("Failed", step.Status);
            Assert.Equal("WwpGridFailed", step.Code);
            Assert.Equal("Object reference not set", step.Message);
            Assert.Equal("NullReferenceException", step.ExceptionType);
            Assert.Equal("Artech.Patterns.Element.AddChild", step.FailureTrace);
            Assert.Equal("add_grid", step.Arguments!["action"]!.ToString());
        }

        [Fact]
        public void SchemaVocabulary_HasPropertyNamesAndEnumValuesButNotExamples()
        {
            GatewayArgsValidator.PrimeCache("test_walkthrough_vocabulary", JObject.Parse(@"{
                type: 'object',
                properties: {
                    action: { type: 'string', enum: ['add_grid', 'list'] },
                    columns: { type: 'array', items: { type: 'object', properties: { attribute: { type: 'string' } } } }
                },
                examples: [ { action: 'add_grid', columns: [ { attribute: 'CustomerName' } ] } ]
            }"));

            ISet<string> words = GatewayArgsValidator.SchemaVocabulary("test_walkthrough_vocabulary");

            Assert.Contains("action", words);
            Assert.Contains("add_grid", words);
            Assert.Contains("columns", words);
            Assert.Contains("attribute", words);
            Assert.Contains("kb", words);
            Assert.DoesNotContain("CustomerName", words);
        }

        [Fact]
        public void DoctorIssueReport_CarriesAWalkthroughWithoutTheDoctorItself()
        {
            Program.OperationTracker.RecordSyntheticCompletion("genexus_read", 5, false,
                new JObject { ["name"] = "ZzWalkthroughSecretObject" });

            var report = (JObject)Program.BuildGatewayDoctorEnvelope(null)["result"]!["issueReport"]!;

            var walkthrough = Assert.IsType<JArray>(report["walkthrough"]);
            Assert.DoesNotContain(walkthrough, s => s["tool"]?.ToString() == "genexus_doctor");
            Assert.DoesNotContain("ZzWalkthroughSecretObject", report.ToString());
        }
    }
}
