using System;
using System.IO;
using System.Reflection;
using System.Threading;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class BuildVerificationDiagnosticsTests
    {
        // Public fakes allow the Worker's dynamic SDK-shape probes to access them.
        public class FakeKb
        {
            public string Location { get; set; }
            public FakeDesign DesignModel { get; set; }
        }
        public class FakeDesign { public FakeEnvironment Environment { get; set; } }
        public class FakeEnvironment { public FakeTarget TargetModel { get; set; } }
        public class FakeTarget
        {
            public string Name { get; set; } = "Development";
            public string TargetPath { get; set; }
            public string BagPath { get; set; }
            public object GetPropertyValue(string name) => name == "TargetPath" ? BagPath : null;
        }

        public enum FakeBuildOptions { BuildCalled = 1 }
        public class FakeBuildService
        {
            public bool Result;
            public bool Throw;
            public int Calls;
            public object SeenKeys;
            public FakeBuildOptions SeenOptions;
            public bool Build(object workingSet, FakeBuildOptions options, object keys, CancellationToken token)
            {
                Calls++;
                SeenKeys = keys;
                SeenOptions = options;
                if (Throw) throw new InvalidOperationException("SDK failed after invocation");
                return Result;
            }
        }
        public class VoidBuildService
        {
            public int Calls;
            public void Build(object workingSet, FakeBuildOptions options, object keys, CancellationToken token) { Calls++; }
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void VerifiedSdkResultPreservesKeysAndZeroOptionsWithoutRepeating(bool result)
        {
            var sdk = new FakeBuildService { Result = result };
            var keys = new[] { Guid.NewGuid() };
            var actual = InProcessBuildRunner.InvokeVerifiedBuild(typeof(FakeBuildService).GetMethod("Build"),
                sdk, new object(), keys, typeof(FakeBuildOptions), CancellationToken.None);
            Assert.Equal(result, actual);
            Assert.Equal(1, sdk.Calls);
            Assert.Same(keys, sdk.SeenKeys);
            Assert.Equal(0, (int)sdk.SeenOptions);
        }

        [Fact]
        public void VoidSignatureIsNotInvokedAsAResultBearingBuild()
        {
            var sdk = new VoidBuildService();
            Assert.Null(InProcessBuildRunner.InvokeVerifiedBuild(typeof(VoidBuildService).GetMethod("Build"),
                sdk, new object(), new object(), typeof(FakeBuildOptions), CancellationToken.None));
            Assert.Equal(0, sdk.Calls);
        }

        [Fact]
        public void ExceptionAfterSdkInvocationIsNotConvertedToUnavailable()
        {
            var sdk = new FakeBuildService { Throw = true };
            Assert.Throws<TargetInvocationException>(() => InProcessBuildRunner.InvokeVerifiedBuild(
                typeof(FakeBuildService).GetMethod("Build"), sdk, new object(), new object(),
                typeof(FakeBuildOptions), CancellationToken.None));
            Assert.Equal(1, sdk.Calls);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void WebRootUsesOpenedKbAndDesignEnvironmentTarget(bool propertyBag)
        {
            string root = Path.Combine(Path.GetTempPath(), "gxmcp-web-root-" + Guid.NewGuid().ToString("N"));
            string web = Path.Combine(root, "CSharpModel", "web");
            Directory.CreateDirectory(web);
            try
            {
                var target = new FakeTarget { TargetPath = propertyBag ? null : "CSharpModel",
                    BagPath = propertyBag ? "CSharpModel" : null };
                var service = CreateService(root, target);
                Assert.Equal(web, service.GetActiveEnvironmentWebPath());
            }
            finally { Directory.Delete(root, true); }
        }

        [Fact]
        public void MissingOrIncompatibleTargetDoesNotSelectArbitraryExistingWebFolder()
        {
            string root = Path.Combine(Path.GetTempPath(), "gxmcp-web-root-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "CSharpModel", "web"));
            Directory.CreateDirectory(Path.Combine(root, "Production", "web"));
            try
            {
                Assert.Null(CreateService(root, new FakeTarget()).GetActiveEnvironmentWebPath());
                Assert.Null(CreateService(root, new FakeTarget { TargetPath = "Production" }).GetActiveEnvironmentWebPath());
            }
            finally { Directory.Delete(root, true); }
        }

        private static KbService CreateService(string root, FakeTarget target)
        {
            var service = new KbService(null);
            typeof(KbService).GetField("_kb", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(service,
                new FakeKb { Location = root, DesignModel = new FakeDesign {
                    Environment = new FakeEnvironment { TargetModel = target } } });
            return service;
        }

        [Fact]
        public void EvidenceFailureKeepsRawProcessCodeAndSuppliesTypedError()
        {
            var status = new BuildService.BuildTaskStatus { Status = "Succeeded", ExitCode = 0,
                MsBuildExitCode = 0, PartialSuccess = true };
            BuildService.RecordVerificationFailure(status, "ActiveEnvironmentOutputRootUnavailable", "Output root unresolved.");
            Assert.Equal("Failed", status.Status);
            Assert.Equal(1, status.ExitCode);
            Assert.Equal(0, status.MsBuildExitCode);
            Assert.False(status.PartialSuccess);
            Assert.Equal(1, status.ErrorCount);
            Assert.Single(status.Errors);
            Assert.Equal("ActiveEnvironmentOutputRootUnavailable", Assert.Single(status.ErrorsDetailed).code);
            Assert.Equal("Verification", status.ErrorsDetailed[0].phase);
        }

        [Fact]
        public void UnverifiedNativeCallTerminalizesWithoutInventingCompilerSuccess()
        {
            var status = new BuildService.BuildTaskStatus();
            Assert.Equal(InProcessBuildOutcome.FailedWithDiagnostics, BuildService.UnverifiedNativeBuild(status));
            Assert.Null(status.MsBuildExitCode);
            Assert.Equal("NativeBuildOutcomeUnknown", Assert.Single(status.ErrorsDetailed).code);
            Assert.Equal("native", status.ErrorsDetailed[0].category);
            Assert.Contains("Do not retry", Assert.Single(status.Errors));
        }

        [Fact]
        public void NativeFailureReportsSdkFailureWithoutInventedCompilerDiagnosis()
        {
            var status = new BuildService.BuildTaskStatus();
            Assert.Equal(InProcessBuildOutcome.FailedWithDiagnostics, BuildService.FailedNativeBuild(status));
            Assert.Equal("Failed", status.Status);
            Assert.Equal(1, status.ErrorCount);
            Assert.Single(status.Errors);
            Assert.Equal("NativeBuildFailed", Assert.Single(status.ErrorsDetailed).code);
            Assert.Equal("native", status.ErrorsDetailed[0].category);
            Assert.Null(status.MsBuildExitCode);
        }

        [Fact]
        public void InProcessBuildAllDoesNotInventMsBuildProcessExitCode()
        {
            var status = new BuildService.BuildTaskStatus { Action = "BuildAll", BuildPath = "inproc", ExitCode = 0 };
            BuildService.FinalizeBuildAllStatus(status, "[GXMCP-BUILD-ALL] BuildAll completed");
            Assert.Null(status.MsBuildExitCode);
            Assert.Null(JObject.FromObject(status)["msBuildExitCode"]);
        }

        [Fact]
        public void TargetedInProcessBuildKeepsAbsentProcessCodeAcrossBuildAllFinalizer()
        {
            var status = new BuildService.BuildTaskStatus { Action = "Build", BuildPath = "inproc",
                Status = "Succeeded", ExitCode = 0, MsBuildExitCode = null };
            BuildService.FinalizeBuildAllStatus(status, "native Build(options=0) returned True.");
            Assert.Null(status.MsBuildExitCode);
            Assert.Null(status.BuildMode); // The BuildAll finalizer must remain a no-op here.
            Assert.Equal("Succeeded", status.Status);
        }
    }
}
