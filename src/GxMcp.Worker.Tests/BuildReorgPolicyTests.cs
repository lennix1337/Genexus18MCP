using System;
using System.Reflection;
using Artech.Genexus.Common.Commands;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    [Collection("InProcessSdkReflection")]
    public class BuildReorgPolicyTests
    {
        private static readonly BindingFlags Private = BindingFlags.Static | BindingFlags.NonPublic;

        internal static void AssertBuildFactoryRefusesReorg()
        {
            var args = (BuildArgs)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(BuildArgs));
            args.Options = BuildOptions.Rebuild;
            var factory = BuildCommandFactory.Factory;
            // No KB: command construction fails on the absent model, after policy
            // admission. This also proves the wrapper forwards to the SDK factory.
            Assert.Throws<TargetInvocationException>(() => factory.GetType().GetMethod("Create").Invoke(factory, new object[] { args }));
            Assert.Equal(BuildOptions.FailIfReorg, args.Options & BuildOptions.FailIfReorg);
            Assert.True(args.HasOption(BuildOptions.DoNotExecuteReorg));
            Assert.True(args.HasOption(BuildOptions.Rebuild));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Missing_or_ignored_reorg_policy_refuses_before_task_execution(bool ignoredSetter)
        {
            using (var fixture = new BuildEventBrokerRestoreTests.BrokerWritingTaskFixture("_typeIdeWebBuildAndDeploy", false, false))
            {
                typeof(InProcessBuildRunner).GetField("_typeIdeWebBuildAndDeploy", Private)
                    .SetValue(null, ignoredSetter ? typeof(IgnoredPolicyTask) : typeof(UnsafeTask));
                UnsafeTask.Calls = 0;
                bool built = (bool)typeof(InProcessBuildRunner).GetMethod("ExecuteIdeWebBuildAndDeploy", Private)
                    .Invoke(null, new object[] { new object(), new InProcessBuildEngine((line, error) => { }), false });
                Assert.False(built);
                Assert.Equal(0, UnsafeTask.Calls);
            }
        }

        [Fact]
        public void FailIfReorg_alone_already_requests_DoNotExecuteReorg()
        {
            // The guard requests FailIfReorg only. The SDK reads that flag as a refusal that
            // also skips execution (0xC00000 contains 0x400000), so no separate
            // DoNotExecuteReorg policy is needed. A DoNotExecuteReorg-only policy would instead
            // let the reorganization report success and the model record it as done.
            Assert.Equal(BuildOptions.DoNotExecuteReorg, BuildOptions.FailIfReorg & BuildOptions.DoNotExecuteReorg);
        }

        public class UnsafeTask
        {
            public static int Calls;
            public bool Execute() { Calls++; return true; }
        }

        public sealed class IgnoredPolicyTask : UnsafeTask
        {
            public bool FailIfReorg { get { return false; } set { } }
            public bool DoNotExecuteReorg { get; set; }
        }
    }
}
