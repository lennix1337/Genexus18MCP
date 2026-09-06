using System;
using System.Collections.Generic;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public sealed class ChangeSetServiceTests
    {
        [Fact]
        public void PreviewAndValidate_ReturnStableRevisionAndPlan()
        {
            var writer = new FakeWriter();
            var service = new ChangeSetService(new MutationEngine(writer));
            var args = ChangeSet("preview");

            var preview = JObject.Parse(service.Run(args));
            Assert.Equal("ok", preview["status"]?.ToString());
            Assert.Equal("ChangeSetPreview", preview["code"]?.ToString());
            Assert.False(string.IsNullOrWhiteSpace(preview["result"]?["changeSetId"]?.ToString()));
            Assert.False(string.IsNullOrWhiteSpace(preview["result"]?["baseRevision"]?.ToString()));
            Assert.Equal("readable", preview["result"]?["mutations"]?[0]?["verification"]?.ToString());

            var validateArgs = ChangeSet("validate");
            var validate = JObject.Parse(service.Run(validateArgs));
            Assert.Equal("ok", validate["status"]?.ToString());
            Assert.True(validate["result"]?["valid"]?.ToObject<bool>());
            Assert.Equal(preview["result"]?["baseRevision"]?.ToString(), validate["result"]?["baseRevision"]?.ToString());
            Assert.Equal(preview["result"]?["changeSetId"]?.ToString(), validate["result"]?["changeSetId"]?.ToString());
        }

        [Fact]
        public void Apply_RequiresValidatedIdentity_AndReturnsVerifiedReceipt()
        {
            var writer = new FakeWriter();
            var service = new ChangeSetService(new MutationEngine(writer));
            var preview = JObject.Parse(service.Run(ChangeSet("preview")));
            var changeSet = ChangeSet("apply")["changeSet"] as JObject;
            changeSet["changeSetId"] = preview["result"]?["changeSetId"];
            changeSet["baseRevision"] = preview["result"]?["baseRevision"];

            var applied = JObject.Parse(service.Run(new JObject { ["changeSet"] = changeSet }));

            Assert.Equal("ok", applied["status"]?.ToString());
            Assert.Equal("ChangeSetApplied", applied["code"]?.ToString());
            Assert.Equal(preview["result"]?["changeSetId"]?.ToString(), applied["changeSetId"]?.ToString());
            Assert.Equal("compensated", applied["atomicity"]?.ToString());
            Assert.Equal("confirmed", applied["result"]?["outcome"]?.ToString());
            Assert.All(applied["result"]?["targets"] as JArray ?? new JArray(), item => Assert.True(item["verified"]?.ToObject<bool>()));
        }

        [Fact]
        public void Apply_RefusesRevisionDriftBeforeWriting()
        {
            var writer = new FakeWriter();
            var service = new ChangeSetService(new MutationEngine(writer));
            var preview = JObject.Parse(service.Run(ChangeSet("preview")));
            writer.Values["Customer"] = "changed outside preview";
            writer.WriteCount = 0;

            var changeSet = ChangeSet("apply")["changeSet"] as JObject;
            changeSet["changeSetId"] = preview["result"]?["changeSetId"];
            changeSet["baseRevision"] = preview["result"]?["baseRevision"];
            var response = JObject.Parse(service.Run(new JObject { ["changeSet"] = changeSet }));

            Assert.Equal("error", response["status"]?.ToString());
            Assert.Equal("ChangeSetConflict", response["error"]?["code"]?.ToString());
            Assert.Equal(0, writer.WriteCount);
        }

        [Fact]
        public void Apply_FailedMutationReportsRollbackOutcome()
        {
            var writer = new FakeWriter { FailPart = "Rules", FailRollback = true };
            var service = new ChangeSetService(new MutationEngine(writer));
            var preview = JObject.Parse(service.Run(ChangeSet("preview")));
            var changeSet = ChangeSet("apply")["changeSet"] as JObject;
            changeSet["changeSetId"] = preview["result"]?["changeSetId"];
            changeSet["baseRevision"] = preview["result"]?["baseRevision"];

            var response = JObject.Parse(service.Run(new JObject { ["changeSet"] = changeSet }));

            Assert.Equal("error", response["status"]?.ToString());
            Assert.Equal("partial", response["atomicity"]?.ToString());
            Assert.Equal("partial", response["rollback"]?["outcome"]?.ToString());
        }

        [Fact]
        public void UnsupportedPart_IsRejectedBeforePreview()
        {
            var service = new ChangeSetService(new MutationEngine(new FakeWriter()));
            var args = new JObject
            {
                ["changeSet"] = new JObject
                {
                    ["action"] = "preview",
                    ["changes"] = new JArray(new JObject
                    {
                        ["name"] = "Customer",
                        ["part"] = "Structure",
                        ["content"] = "<structure/>"
                    })
                }
            };

            var response = JObject.Parse(service.Run(args));
            Assert.Equal("error", response["status"]?.ToString());
            Assert.Equal("ChangeSetInvalid", response["error"]?["code"]?.ToString());
        }

        private static JObject ChangeSet(string action)
        {
            return new JObject
            {
                ["changeSet"] = new JObject
                {
                    ["action"] = action,
                    ["changes"] = new JArray(
                        new JObject { ["name"] = "Customer", ["part"] = "Source", ["content"] = "new source" },
                        new JObject { ["name"] = "Customer", ["part"] = "Rules", ["content"] = "parm(in:&Id);" })
                }
            };
        }

        private sealed class FakeWriter : ISdkObjectWriter
        {
            public readonly Dictionary<string, string> Values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Customer"] = "old source"
            };
            private readonly Dictionary<string, string> Parts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Customer|Rules"] = "old rules"
            };
            public int WriteCount { get; set; }
            public string FailPart { get; set; }
            public bool FailRollback { get; set; }

            public string WriteObject(string target, JObject args)
            {
                WriteCount++;
                string part = args["part"]?.ToString() ?? "Source";
                string content = args["content"]?.ToString() ?? string.Empty;
                bool isRollback = args["isRollback"]?.ToObject<bool?>() == true;
                if (string.Equals(part, FailPart, StringComparison.OrdinalIgnoreCase)
                    || isRollback && FailRollback)
                    return "{\"status\":\"Error\"}";
                if (string.Equals(part, "Source", StringComparison.OrdinalIgnoreCase)) Values[target] = content;
                else Parts[target + "|" + part] = content;
                return "{\"status\":\"Success\"}";
            }

            public string ApplySemanticOps(JObject args) => "{\"status\":\"Success\"}";
            public string ApplyJsonPatch(JObject args) => "{\"status\":\"Success\"}";
            public string BulkWrite(JObject args) => "{\"status\":\"Success\"}";
            public string ReadObjectSource(string target, string part)
            {
                if (target.Equals("Customer", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.Equals(part, "Source", StringComparison.OrdinalIgnoreCase)) return Values["Customer"];
                    return Parts.TryGetValue(target + "|" + part, out var value) ? value : null;
                }
                return null;
            }
        }
    }
}
