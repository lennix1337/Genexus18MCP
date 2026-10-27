using System;
using Artech.Architecture.Common.Events;
using GxMcp.Worker.Helpers;
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
            var publisher = new SdkEventSuppressionScope.GlobalPublisher();
            var receiver = new SdkEventSuppressionScope.Receiver();
            using (var task = new BuildAllTaskFixture(originallySuspended))
            {
                BuildAllTaskFixture.EventTask.ExecuteCallback = () =>
                {
                    publisher.Raise();
                    if (throws) throw new InvalidOperationException("synthetic build failure");
                    return true;
                };
                using (broker.ScopedParticipation(publisher))
                using (broker.ScopedParticipation(receiver))
                using (writeIsolation ? new SdkEventSuppressionScope() : null)
                {
                    bool result = task.Execute();
                    Assert.Equal(!throws && !writeIsolation, result);
                    Assert.Equal(writeIsolation ? 0 : 1, receiver.Count);
                    Assert.Equal(writeIsolation || originallySuspended, broker.EventsSuspended);
                }
                Assert.Equal(originallySuspended, broker.EventsSuspended);
            }
        }
    }
}
