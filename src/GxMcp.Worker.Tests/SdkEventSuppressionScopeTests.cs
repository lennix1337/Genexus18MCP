using System;
using System.Reflection;
using Artech.Architecture.Common.Events;
using GxMcp.Worker.Helpers;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    [CollectionDefinition("SDK event suspension", DisableParallelization = true)]
    public sealed class SdkEventSuppressionCollection { }

    [Collection("SDK event suspension")]
    public sealed class SdkEventSuppressionScopeTests
    {
        [Fact]
        public void GlobalBroker_SuppressesAndResumesSyntheticCallback_WithoutKb()
        {
            var result = SdkEventSuppressionScope.VerifyBroker(null);
            Assert.True(result["globalBrokerVerified"].Value<bool>());
            Assert.False(result["kbBrokerVerified"].Value<bool>());
            Assert.False(EventsService.Events.EventsSuspended);
        }

        [Fact]
        public void SecondaryKbPublicationBroker_IsSuppressedByGlobalSwitch_WithoutKb()
        {
            // Construction is diagnostic only; production uses the public kb.Events.
            var constructor = typeof(EventsService).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic,
                null, new[] { typeof(string), typeof(Type), typeof(Type) }, null);
            var broker = (EventsService)constructor.Invoke(new object[] {
                "GxMcp.SecondaryProbe", typeof(KBEventPublicationAttribute), typeof(KBEventArgs) });
            var result = SdkEventSuppressionScope.VerifyBrokers(broker, null);
            Assert.True(result["kbBrokerVerified"].Value<bool>());
            Assert.False(broker.EventsSuspended);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ExceptionAndNestedScopes_RestoreOriginalGlobalState(bool original)
        {
            var global = EventsService.Events;
            bool prior = global.EventsSuspended;
            try
            {
                global.EventsSuspended = original;
                Assert.Throws<InvalidOperationException>((Action)(() => {
                    using (new SdkEventSuppressionScope())
                    using (new SdkEventSuppressionScope())
                    { Assert.True(global.EventsSuspended); throw new InvalidOperationException("synthetic"); }
                }));
                Assert.Equal(original, global.EventsSuspended);
            }
            finally { global.EventsSuspended = prior; }
        }

        [Fact]
        public void Probe_RejectsAlreadySuspendedBrokerWithoutChangingIt()
        {
            var global = EventsService.Events;
            bool prior = global.EventsSuspended;
            try
            {
                global.EventsSuspended = true;
                Assert.Throws<InvalidOperationException>(() => SdkEventSuppressionScope.VerifyBroker(null));
                Assert.True(global.EventsSuspended);
            }
            finally { global.EventsSuspended = prior; }
        }
    }
}
