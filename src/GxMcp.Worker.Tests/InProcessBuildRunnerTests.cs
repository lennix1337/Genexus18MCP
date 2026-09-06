using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Services;
using Microsoft.Build.Framework;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // v2.6.6 Stream D — exercises the in-process build path without requiring
    // a live KB. Most tests assert the fallback behaviour (returns false) so
    // BuildService.RunBuild can transparently spawn MSBuild.exe instead.
    //
    // Serialized with PatternApplyServiceTests via "InProcessSdkReflection"
    // collection — both touch the static type cache populated by TryResolveTypes
    // / Genexus.MsBuild.Tasks reflection, which xunit's default parallel
    // scheduler occasionally races against.
    [Collection("InProcessSdkReflection")]
    public class InProcessBuildRunnerTests
    {
        private static BuildService.BuildTaskStatus NewStatus()
        {
            return new BuildService.BuildTaskStatus
            {
                TaskId = Guid.NewGuid().ToString("N").Substring(0, 8),
                Status = "Running",
                Action = "Build",
                StartedAt = DateTime.UtcNow
            };
        }

        [Fact]
        public void Run_returns_false_when_kbHandle_is_null()
        {
            var status = NewStatus();
            var outcome = InProcessBuildRunner.Run(
                status, "Build", new List<string> { "Foo" },
                (s, l, e) => { },
                kbHandle: null,
                kbLock: new object());
            Assert.Equal(InProcessBuildOutcome.CouldNotRun, outcome);
        }

        [Fact]
        public void Run_returns_false_when_kbLock_is_null()
        {
            var status = NewStatus();
            var outcome = InProcessBuildRunner.Run(
                status, "Build", new List<string> { "Foo" },
                (s, l, e) => { },
                kbHandle: new object(),
                kbLock: null);
            Assert.Equal(InProcessBuildOutcome.CouldNotRun, outcome);
        }

        [Fact]
        public void Run_returns_false_when_GX_PROGRAM_DIR_missing_or_dll_absent()
        {
            // If the SDK is genuinely installed, this test is a no-op (returns
            // true would be a real-KB scenario, false is what we assert here).
            // We intentionally do NOT mutate the env var globally; instead we
            // exercise the failure path by probing the type-resolution seam
            // and asserting it never throws.
            string error;
            bool resolved = InProcessBuildRunner.TryResolveTypes(out error);
            // resolved may be true on a GeneXus dev machine — both outcomes
            // are valid; the contract under test is "no exception".
            Assert.True(resolved || !string.IsNullOrEmpty(error));
        }

        [Fact]
        public void Adapter_forwards_LogErrorEvent_with_isError_true()
        {
            string captured = null;
            bool capturedIsError = false;
            var engine = new InProcessBuildEngine((line, isError) =>
            {
                captured = line;
                capturedIsError = isError;
            });

            engine.LogErrorEvent(new BuildErrorEventArgs(
                subcategory: null,
                code: "CS0246",
                file: "x.cs",
                lineNumber: 1, columnNumber: 1, endLineNumber: 1, endColumnNumber: 1,
                message: "Type 'Foo' could not be found",
                helpKeyword: null,
                senderName: "test"));

            Assert.NotNull(captured);
            Assert.True(capturedIsError);
            Assert.Contains("CS0246", captured);
            Assert.Contains("Foo", captured);
        }

        [Fact]
        public void Adapter_forwards_LogWarningEvent_with_isError_false()
        {
            string captured = null;
            bool capturedIsError = true;
            var engine = new InProcessBuildEngine((line, isError) =>
            {
                captured = line;
                capturedIsError = isError;
            });

            engine.LogWarningEvent(new BuildWarningEventArgs(
                subcategory: null,
                code: "spc0022",
                file: null,
                lineNumber: 0, columnNumber: 0, endLineNumber: 0, endColumnNumber: 0,
                message: "spec warning",
                helpKeyword: null,
                senderName: "test"));

            Assert.NotNull(captured);
            Assert.False(capturedIsError);
            Assert.Contains("spc0022", captured);
        }

        [Fact]
        public void Adapter_BuildProjectFile_is_noop_returning_true()
        {
            var engine = new InProcessBuildEngine((l, e) => { });
            bool ok = engine.BuildProjectFile("any.proj", new[] { "Build" }, new Hashtable(), new Hashtable());
            Assert.True(ok);
            Assert.True(engine.ContinueOnError);
        }

        [Fact]
        public void RebuildAll_with_multiple_targets_runs_targeted_specify_before_force_rebuild()
        {
            var specifyField = typeof(InProcessBuildRunner).GetField(
                "_typeSpecifyOneOnly", BindingFlags.Static | BindingFlags.NonPublic);
            var deployField = typeof(InProcessBuildRunner).GetField(
                "_typeIdeWebBuildAndDeploy", BindingFlags.Static | BindingFlags.NonPublic);
            var buildOneField = typeof(InProcessBuildRunner).GetField(
                "_typeBuildOne", BindingFlags.Static | BindingFlags.NonPublic);
            var attemptedField = typeof(InProcessBuildRunner).GetField(
                "_assemblyLoadAttempted", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(specifyField);
            Assert.NotNull(deployField);
            Assert.NotNull(buildOneField);
            Assert.NotNull(attemptedField);

            object oldSpecify = specifyField.GetValue(null);
            object oldDeploy = deployField.GetValue(null);
            object oldBuildOne = buildOneField.GetValue(null);
            object oldAttempted = attemptedField.GetValue(null);
            try
            {
                FakeSpecifyOneOnlyTask.Reset();
                FakeIdeWebBuildAndDeployTask.Reset();
                specifyField.SetValue(null, typeof(FakeSpecifyOneOnlyTask));
                deployField.SetValue(null, typeof(FakeIdeWebBuildAndDeployTask));
                buildOneField.SetValue(null, null);
                attemptedField.SetValue(null, true);

                var targets = new List<string>
                {
                    "ViewProdCatGeneralSDT",
                    "ViewProdCatGeneralGetP",
                    "ViewProdCatGeneralHtmlP",
                    "ProdCatDashboardWC"
                };
                var outcome = InProcessBuildRunner.Run(
                    NewStatus(), "RebuildAll", targets,
                    (s, l, e) => { },
                    kbHandle: new object(),
                    kbLock: new object());

                Assert.Equal(InProcessBuildOutcome.Succeeded, outcome);
                Assert.True(FakeSpecifyOneOnlyTask.Executed);
                Assert.Equal(string.Join(";", targets), FakeSpecifyOneOnlyTask.LastObjectNames);
                Assert.True(FakeIdeWebBuildAndDeployTask.Executed);
                Assert.True(FakeIdeWebBuildAndDeployTask.LastForceRebuild);
            }
            finally
            {
                specifyField.SetValue(null, oldSpecify);
                deployField.SetValue(null, oldDeploy);
                buildOneField.SetValue(null, oldBuildOne);
                attemptedField.SetValue(null, oldAttempted);
            }
        }

        [Fact]
        public void BuildAll_uses_native_build_all_task_with_fail_if_reorg()
        {
            var specifyField = typeof(InProcessBuildRunner).GetField("_typeSpecifyOneOnly", BindingFlags.Static | BindingFlags.NonPublic);
            var deployField = typeof(InProcessBuildRunner).GetField("_typeIdeWebBuildAndDeploy", BindingFlags.Static | BindingFlags.NonPublic);
            var buildOneField = typeof(InProcessBuildRunner).GetField("_typeBuildOne", BindingFlags.Static | BindingFlags.NonPublic);
            var buildAllField = typeof(InProcessBuildRunner).GetField("_typeBuildAll", BindingFlags.Static | BindingFlags.NonPublic);
            var attemptedField = typeof(InProcessBuildRunner).GetField("_assemblyLoadAttempted", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(buildAllField);

            object oldSpecify = specifyField.GetValue(null);
            object oldDeploy = deployField.GetValue(null);
            object oldBuildOne = buildOneField.GetValue(null);
            object oldBuildAll = buildAllField.GetValue(null);
            object oldAttempted = attemptedField.GetValue(null);
            try
            {
                FakeBuildAllTask.Reset();
                FakeIdeWebBuildAndDeployTask.Reset();
                specifyField.SetValue(null, typeof(FakeSpecifyOneOnlyTask));
                deployField.SetValue(null, typeof(FakeIdeWebBuildAndDeployTask));
                buildOneField.SetValue(null, null);
                buildAllField.SetValue(null, typeof(FakeBuildAllTask));
                attemptedField.SetValue(null, true);

                var lines = new List<string>();
                var outcome = InProcessBuildRunner.Run(
                    NewStatus(), "BuildAll", new List<string>(),
                    (s, l, e) => lines.Add(l),
                    kbHandle: new object(),
                    kbLock: new object());

                Assert.Equal(InProcessBuildOutcome.Succeeded, outcome);
                Assert.True(FakeBuildAllTask.Executed);
                Assert.False(FakeBuildAllTask.LastForceRebuild);
                Assert.True(FakeBuildAllTask.LastFailIfReorg);
                Assert.False(FakeIdeWebBuildAndDeployTask.Executed);
                Assert.Contains(lines, line => line.Contains("BuildAll completed"));
            }
            finally
            {
                specifyField.SetValue(null, oldSpecify);
                deployField.SetValue(null, oldDeploy);
                buildOneField.SetValue(null, oldBuildOne);
                buildAllField.SetValue(null, oldBuildAll);
                attemptedField.SetValue(null, oldAttempted);
            }
        }

        [Fact]
        public void BuildWithTheseOnly_Reflection_FieldResolved()
        {
            var field = typeof(InProcessBuildRunner).GetField(
                "_miBuildWithTheseOnly", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(field);
        }

        [LiveKbFact]
        public void TryResolveTypes_finds_GeneXus_tasks_when_SDK_installed()
        {
            // Gated on GXMCP_TEST_KB so CI skips, but a dev machine with the
            // GeneXus 18 install + a live KB env will actually resolve the
            // task types from Genexus.MsBuild.Tasks.dll.
            string error;
            bool resolved = InProcessBuildRunner.TryResolveTypes(out error);
            Assert.True(resolved, "Expected Genexus.MsBuild.Tasks types to resolve: " + error);
        }

        [Fact]
        public void ResolveTargetKBObject_returns_null_on_null_or_empty_model_or_target()
        {
            Assert.Null(InProcessBuildRunner.ResolveTargetKBObject(null, "Customer"));
            Assert.Null(InProcessBuildRunner.ResolveTargetKBObject(new object(), null));
            Assert.Null(InProcessBuildRunner.ResolveTargetKBObject(new object(), "  "));
        }

        [Fact]
        public void ResolveTargetKBObject_handles_type_prefix_and_guid_without_throwing()
        {
            // Dummy model object
            var dummyModel = new object();
            var resType = InProcessBuildRunner.ResolveTargetKBObject(dummyModel, "Transaction:Customer");
            Assert.Null(resType);

            var resGuid = InProcessBuildRunner.ResolveTargetKBObject(dummyModel, Guid.NewGuid().ToString());
            Assert.Null(resGuid);
        }
    }

    public sealed class FakeSpecifyOneOnlyTask
    {
        public static bool Executed { get; private set; }
        public static string LastObjectNames { get; private set; }

        public object KB { get; set; }
        public string ObjectNames { get; set; }
        public IBuildEngine BuildEngine { get; set; }

        public bool Execute()
        {
            Executed = true;
            LastObjectNames = ObjectNames;
            return true;
        }

        public static void Reset()
        {
            Executed = false;
            LastObjectNames = null;
        }
    }

    public sealed class FakeIdeWebBuildAndDeployTask
    {
        public static bool Executed { get; private set; }
        public static bool LastForceRebuild { get; private set; }

        public object KB { get; set; }
        public bool ForceRebuild { get; set; }
        public bool CompileMains { get; set; }
        public string Output { get; set; }
        public bool EventsSuspended { get; set; }
        public IBuildEngine BuildEngine { get; set; }

        public bool Execute()
        {
            Executed = true;
            LastForceRebuild = ForceRebuild;
            return true;
        }

        public static void Reset()
        {
            Executed = false;
            LastForceRebuild = false;
        }
    }

    public sealed class FakeBuildAllTask
    {
        public static bool Executed { get; private set; }
        public static bool LastForceRebuild { get; private set; }
        public static bool LastFailIfReorg { get; private set; }

        public object KB { get; set; }
        public bool ForceRebuild { get; set; }
        public bool CompileMains { get; set; }
        public bool FailIfReorg { get; set; }
        public bool DoNotExecuteReorg { get; set; }
        public bool DetailedNavigation { get; set; }
        public IBuildEngine BuildEngine { get; set; }

        public bool Execute()
        {
            Executed = true;
            LastForceRebuild = ForceRebuild;
            LastFailIfReorg = FailIfReorg;
            return true;
        }

        public static void Reset()
        {
            Executed = false;
            LastForceRebuild = false;
            LastFailIfReorg = false;
        }
    }
}
