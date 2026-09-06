using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using Xunit;
using GxMcp.Worker.Services;
using GxMcp.Worker.Helpers;

namespace GxMcp.Worker.Tests
{
    public class MutationEngineTests
    {
        [Fact]
        public void MutationEngine_PreflightGuard_RejectsLiteralLineBreaks()
        {
            var engine = new MutationEngine();
            var req = new MutationRequest
            {
                Target = "TestProc",
                Part = "Source",
                Content = "// comment\\r\\nMsg('hello');",
                Mode = MutationMode.Xml
            };

            var res = engine.Execute(req);

            Assert.False(res.Success);
            Assert.Equal("LiteralLineBreaksDetected", res.ErrorCode);
            Assert.Contains("literal line break", res.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void MutationEngine_DryRun_ReturnsPlanWithoutPersisting()
        {
            var engine = new MutationEngine();
            var req = new MutationRequest
            {
                Target = "CustomerProc",
                Part = "Source",
                Content = "Msg('Hello World');",
                Mode = MutationMode.Xml,
                DryRun = true
            };

            var res = engine.Execute(req);

            Assert.True(res.Success);
            Assert.NotNull(res.Plan);
            Assert.Equal(1, res.Plan["totalObjects"]?.ToObject<int>());
            var mutations = res.Plan["mutations"] as JArray;
            Assert.NotNull(mutations);
            Assert.Single(mutations);
            Assert.Equal("CustomerProc", mutations[0]["target"]?.ToString());
            Assert.Equal("Source", mutations[0]["part"]?.ToString());
        }

        [Fact]
        public void MutationEngine_OptimisticConcurrency_RejectsVersionMismatch()
        {
            var engine = new MutationEngine();
            var req = new MutationRequest
            {
                Target = "CustomerProc",
                Part = "Source",
                Content = "Msg('Updated');",
                Mode = MutationMode.Xml,
                ExpectedVersion = "v1.0",
                CurrentVersionResolver = (target, part) => "v2.0"
            };

            var res = engine.Execute(req);

            Assert.False(res.Success);
            Assert.Equal("ConcurrencyConflict", res.ErrorCode);
            Assert.Contains("expected version", res.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void MutationEngine_MultiObjectUnitOfWork_ExecutesLifoRollbackOnFailure()
        {
            var applied = new List<string>();
            var rolledBack = new List<string>();

            var mockWriter = new DelegateSdkObjectWriter(
                write: (target, args) =>
                {
                    if (target == "FailObj")
                    {
                        return new JObject { ["status"] = "Error", ["message"] = "Simulated disk failure" }.ToString();
                    }
                    applied.Add(target);
                    return new JObject { ["status"] = "Success" }.ToString();
                },
                rollback: (target, args) =>
                {
                    rolledBack.Add(target);
                    return new JObject { ["status"] = "Success" }.ToString();
                }
            );

            var engine = new MutationEngine(mockWriter);
            var req = new MutationRequest
            {
                Targets = new JArray
                {
                    new JObject { ["target"] = "Obj1", ["part"] = "Source", ["content"] = "Code1" },
                    new JObject { ["target"] = "Obj2", ["part"] = "Source", ["content"] = "Code2" },
                    new JObject { ["target"] = "FailObj", ["part"] = "Source", ["content"] = "CodeFail" }
                },
                RollbackOnFailure = true
            };

            var res = engine.Execute(req);

            Assert.False(res.Success);
            Assert.True(res.RolledBack);
            Assert.Contains("FailObj", res.ErrorMessage);
            // Verify LIFO order of rollback: Obj2 rolled back before Obj1
            Assert.Equal(2, rolledBack.Count);
            Assert.Equal("Obj2", rolledBack[0]);
            Assert.Equal("Obj1", rolledBack[1]);
        }

        [Fact]
        public void MutationEngine_RollbackRequiresReadbackConfirmation()
        {
            int reads = 0;
            var mockWriter = new DelegateSdkObjectWriter(
                write: (target, args) => target == "FailObj"
                    ? new JObject { ["status"] = "Error", ["message"] = "Simulated disk failure" }.ToString()
                    : new JObject { ["status"] = "Success" }.ToString(),
                rollback: (target, args) => new JObject { ["status"] = "Success" }.ToString(),
                read: (target, part) => reads++ == 0 ? "original-content" : "post-write-content");

            var engine = new MutationEngine(mockWriter);
            var res = engine.Execute(new MutationRequest
            {
                Targets = new JArray
                {
                    new JObject { ["target"] = "Obj1", ["part"] = "Source", ["content"] = "Code1" },
                    new JObject { ["target"] = "FailObj", ["part"] = "Source", ["content"] = "CodeFail" }
                },
                RollbackOnFailure = true
            });

            Assert.False(res.RolledBack);
            Assert.Equal("indeterminate", res.RollbackOutcome);
            var envelope = JObject.Parse(res.ResponseJson);
            Assert.Equal("indeterminate", envelope["rollback"]?["outcome"]?.ToString());
        }

        [Fact]
        public void MutationEngine_MultiObjectVersionConflictWritesNothing()
        {
            int writes = 0;
            var mockWriter = new DelegateSdkObjectWriter(
                write: (target, args) =>
                {
                    writes++;
                    return new JObject { ["status"] = "Success" }.ToString();
                },
                read: (target, part) => "CurrentSource");

            var engine = new MutationEngine(mockWriter);
            var res = engine.Execute(new MutationRequest
            {
                Targets = new JArray
                {
                    new JObject
                    {
                        ["target"] = "Obj1", ["part"] = "Source", ["content"] = "Code1",
                        ["expectedVersion"] = "stale"
                    }
                }
            });

            Assert.False(res.Success);
            Assert.Equal("ConcurrencyConflict", res.ErrorCode);
            Assert.Equal(0, writes);
        }

        [Fact]
        public void MutationEngine_SingleExpectedVersionFailsClosedWhenCurrentStateCannotBeRead()
        {
            int writes = 0;
            var mockWriter = new DelegateSdkObjectWriter(
                write: (target, args) =>
                {
                    writes++;
                    return new JObject { ["status"] = "Success" }.ToString();
                },
                read: (target, part) => null);

            var engine = new MutationEngine(mockWriter);
            var res = engine.Execute(new MutationRequest
            {
                Target = "Obj1",
                Part = "Source",
                Content = "Updated",
                ExpectedVersion = "v1"
            });

            Assert.False(res.Success);
            Assert.Equal("ConcurrencyStateUnavailable", res.ErrorCode);
            Assert.Equal(0, writes);
        }

        [Fact]
        public void MutationEngine_MultiObjectExpectedVersionFailsClosedWhenCurrentStateCannotBeRead()
        {
            int writes = 0;
            var mockWriter = new DelegateSdkObjectWriter(
                write: (target, args) =>
                {
                    writes++;
                    return new JObject { ["status"] = "Success" }.ToString();
                },
                read: (target, part) => null);

            var engine = new MutationEngine(mockWriter);
            var res = engine.Execute(new MutationRequest
            {
                Targets = new JArray
                {
                    new JObject
                    {
                        ["target"] = "Obj1",
                        ["part"] = "Source",
                        ["content"] = "Updated",
                        ["baseVersion"] = "v1"
                    }
                }
            });

            Assert.False(res.Success);
            Assert.Equal("ConcurrencyStateUnavailable", res.ErrorCode);
            Assert.Equal(0, writes);
        }

        [Fact]
        public void MutationEngine_PreviewReportsVersionCheck()
        {
            const string current = "CurrentSource";
            var mockWriter = new DelegateSdkObjectWriter(
                write: (target, args) => new JObject { ["status"] = "Success" }.ToString(),
                read: (target, part) => current);
            var engine = new MutationEngine(mockWriter);
            var version = ComputeVersionToken(current);

            var plan = engine.Plan(new MutationRequest
            {
                Targets = new JArray
                {
                    new JObject
                    {
                        ["target"] = "Obj1",
                        ["part"] = "Source",
                        ["content"] = "Updated",
                        ["expectedVersion"] = version
                    }
                }
            });

            Assert.Equal(version, plan.Mutations[0]["currentVersion"]?.ToString());
            Assert.Equal("match", plan.Mutations[0]["versionCheck"]?.ToString());
        }

        [Fact]
        public void MutationEngine_PreviewMarksStaleVersionInvalid()
        {
            var mockWriter = new DelegateSdkObjectWriter(
                write: (target, args) => new JObject { ["status"] = "Success" }.ToString(),
                read: (target, part) => "CurrentSource");
            var engine = new MutationEngine(mockWriter);

            var plan = engine.Plan(new MutationRequest
            {
                Targets = new JArray
                {
                    new JObject
                    {
                        ["target"] = "Obj1",
                        ["part"] = "Source",
                        ["content"] = "Updated",
                        ["expectedVersion"] = "stale"
                    }
                }
            });

            Assert.False(plan.IsValid);
            Assert.Contains("version conflict", plan.ValidationErrors[0], StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void MutationEngine_MultiObjectSuccessReturnsPerTargetVerification()
        {
            var current = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Obj1"] = "Original1",
                ["Obj2"] = "Original2"
            };
            var mockWriter = new DelegateSdkObjectWriter(
                write: (target, args) =>
                {
                    current[target] = args["content"]?.ToString() ?? string.Empty;
                    return new JObject { ["status"] = "Success" }.ToString();
                },
                read: (target, part) => current.TryGetValue(target, out var value) ? value : null);

            var engine = new MutationEngine(mockWriter);
            var res = engine.Execute(new MutationRequest
            {
                Targets = new JArray
                {
                    new JObject { ["target"] = "Obj1", ["part"] = "Source", ["content"] = "Updated1" },
                    new JObject { ["target"] = "Obj2", ["part"] = "Source", ["content"] = "Updated2" }
                }
            });

            Assert.True(res.Success);
            var result = JObject.Parse(res.ResponseJson)["result"];
            Assert.Equal("confirmed", result?["outcome"]?.ToString());
            Assert.Equal(2, result?["targets"]?.ToObject<JArray>()?.Count);
            Assert.All(result?["targets"] as JArray ?? new JArray(), target => Assert.True(target["verified"]?.ToObject<bool>()));
        }

        private class DelegateSdkObjectWriter : ISdkObjectWriter
        {
            private readonly Func<string, JObject, string> _write;
            private readonly Func<string, JObject, string> _rollback;
            private readonly Func<string, string, string> _read;

            public DelegateSdkObjectWriter(
                Func<string, JObject, string> write,
                Func<string, JObject, string> rollback = null,
                Func<string, string, string> read = null)
            {
                _write = write;
                _rollback = rollback ?? write;
                _read = read ?? ((target, part) => "OriginalSource");
            }

            public string WriteObject(string target, JObject args)
            {
                bool isRollback = args["isRollback"]?.ToObject<bool?>() ?? false;
                return isRollback ? _rollback(target, args) : _write(target, args);
            }

            public string ApplySemanticOps(JObject args) => new JObject { ["status"] = "Success" }.ToString();
            public string ApplyJsonPatch(JObject args) => new JObject { ["status"] = "Success" }.ToString();
            public string BulkWrite(JObject args) => new JObject { ["status"] = "Success" }.ToString();
            public string ReadObjectSource(string target, string part) => _read(target, part);
        }

        private static string ComputeVersionToken(string content)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(content));
                return BitConverter.ToString(hash).Replace("-", string.Empty).Substring(0, 16);
            }
        }
    }
}
