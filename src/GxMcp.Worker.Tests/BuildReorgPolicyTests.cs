using System;
using System.Collections.Generic;
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

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Safety_flag_failure_reaches_the_build_status_and_is_not_a_reorg_refusal(bool ignoredSetter)
        {
            var lines = new List<(string Line, bool IsError)>();
            using (var fixture = new BuildEventBrokerRestoreTests.BrokerWritingTaskFixture("_typeIdeWebBuildAndDeploy", false, false))
            {
                typeof(InProcessBuildRunner).GetField("_typeIdeWebBuildAndDeploy", Private)
                    .SetValue(null, ignoredSetter ? typeof(IgnoredPolicyTask) : typeof(UnsafeTask));
                UnsafeTask.Calls = 0;
                var engine = new InProcessBuildEngine((line, error) => lines.Add((line, error)));
                bool built = (bool)typeof(InProcessBuildRunner).GetMethod("ExecuteIdeWebBuildAndDeploy", Private)
                    .Invoke(null, new object[] { new object(), engine, false });
                Assert.False(built);
                Assert.Equal(0, UnsafeTask.Calls);
            }

            var cause = Assert.Single(lines, l => l.IsError);
            Assert.Contains("FailIfReorg", cause.Line);
            Assert.False(BuildService.DetectBuildAllReorgRequired(cause.Line));

            var status = new BuildService.BuildTaskStatus { Action = "Build", Status = "Failed" };
            typeof(BuildService).GetMethod("HandleLine", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(new BuildService(), new object[] { status, cause.Line, true });
            Assert.NotEqual(true, status.ReorgRequired);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Safety_flag_messages_are_not_reorg_refusals(bool ignoredSetter)
        {
            object task = ignoredSetter ? (object)new IgnoredPolicyTask() : new UnsafeTask();
            var apply = typeof(InProcessBuildRunner).GetMethod("SetRequiredReorgPolicy", Private);
            var failure = Assert.Throws<TargetInvocationException>(() => apply.Invoke(null, new[] { task }));
            Assert.IsType<InvalidOperationException>(failure.InnerException);
            Assert.Contains("FailIfReorg", failure.InnerException.Message);
            Assert.False(BuildService.DetectBuildAllReorgRequired(failure.InnerException.Message));
        }

        [Fact]
        public void FailIfReorg_alone_already_requests_DoNotExecuteReorg()
        {
            // The guard sets both flags (NoReorgBuildCommandFactory.Create and SetRequiredReorgPolicy).
            // This pins the SDK fact that the guard relies on: FailIfReorg already contains the
            // DoNotExecuteReorg bit (0xC00000 contains 0x400000).
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
