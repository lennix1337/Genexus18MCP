using System;
using System.Reflection;
using Artech.Architecture.Common.Events;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Services;
using Microsoft.Build.Framework;
using Xunit;

namespace GxMcp.Worker.Tests
{
    [Collection("InProcessSdkReflection")]
    public class BuildAllEventDispatchTests
    {
        [Theory]
        [InlineData(false, false, false)]
        [InlineData(true, false, false)]
        [InlineData(false, true, false)]
        [InlineData(true, true, false)]
        [InlineData(false, false, true)]
        [InlineData(true, false, true)]
        public void BuildAll_delivers_callbacks_and_restores_broker_even_on_failure(bool originallySuspended, bool throws, bool writeIsolation)
        {
            var broker = EventsService.Events;
            bool original = broker.EventsSuspended;
            var typeField = typeof(InProcessBuildRunner).GetField("_typeBuildAll", BindingFlags.Static | BindingFlags.NonPublic);
            object originalType = typeField.GetValue(null);
            var publisher = new SdkEventSuppressionScope.GlobalPublisher();
            var receiver = new SdkEventSuppressionScope.Receiver();
            try
            {
                typeField.SetValue(null, typeof(EventBuildAllTask));
                broker.EventsSuspended = originallySuspended;
                EventBuildAllTask.ExecuteCallback = () =>
                {
                    publisher.Raise();
                    if (throws) throw new InvalidOperationException("synthetic build failure");
                };
                using (broker.ScopedParticipation(publisher))
                using (broker.ScopedParticipation(receiver))
                using (writeIsolation ? new SdkEventSuppressionScope() : null)
                {
                    bool result = InvokeBuildAll();
                    Assert.Equal(!throws && !writeIsolation, result);
                    Assert.Equal(writeIsolation ? 0 : 1, receiver.Count);
                    Assert.Equal(writeIsolation || originallySuspended, broker.EventsSuspended);
                }
                Assert.Equal(originallySuspended, broker.EventsSuspended);
            }
            finally
            {
                broker.EventsSuspended = original;
                typeField.SetValue(null, originalType);
                EventBuildAllTask.ExecuteCallback = null;
            }
        }

        private static bool InvokeBuildAll()
        {
            var execute = typeof(InProcessBuildRunner).GetMethod("ExecuteBuildAll", BindingFlags.Static | BindingFlags.NonPublic);
            return (bool)execute.Invoke(null, new object[]
            {
                new object(), new InProcessBuildEngine((line, error) => { }),
                new Action<string, bool>((line, error) => { })
            });
        }

        public sealed class EventBuildAllTask
        {
            public static Action ExecuteCallback;
            public object KB { get; set; }
            public IBuildEngine BuildEngine { get; set; }
            public bool EventsSuspended
            {
                get => EventsService.Events.EventsSuspended;
                set => EventsService.Events.EventsSuspended = value;
            }
            public bool Execute()
            {
                ExecuteCallback();
                return true;
            }
        }
    }
}
