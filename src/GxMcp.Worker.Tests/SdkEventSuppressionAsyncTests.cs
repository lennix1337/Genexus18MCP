using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Threading;
using Artech.Architecture.Common.Events;
using GxMcp.Worker.Helpers;
using Microsoft.Practices.CompositeUI.EventBroker;
using Xunit;

namespace GxMcp.Worker.Tests
{
    [Collection("SDK event suspension")]
    public sealed class SdkEventSuppressionAsyncTests
    {
        private const string PublisherTopic = "event://GxMcp/NoKbAsyncIsolation/Publisher";
        private const string BackgroundTopic = "event://GxMcp/NoKbAsyncIsolation/Background";

        [Fact]
        public void UiWorkQueuedBeforeSuspension_IsNotCanceledByTheBrokerSwitch()
        {
            var global = EventsService.Events;
            Assert.False(global.EventsSuspended);
            var context = new QueuedContext();
            var receiver = new Receiver(context);
            using var receiverSignal = receiver.Changed;
            var publisher = new GlobalPublisher();
            using (global.ScopedParticipation(publisher))
            using (global.ScopedParticipation(receiver))
            {
                publisher.Raise(false);
                Assert.Equal(1, receiver.Count);
                Assert.Equal(1, context.PostCount);
                Assert.Equal(0, receiver.UiCount);
                using (new SdkEventSuppressionScope())
                {
                    // The switch prevents new broker publications. It cannot
                    // retract work that an earlier handler has already queued.
                    context.Drain();
                    Assert.Equal(1, receiver.UiCount);
                    publisher.Raise(false);
                    AssertQuiet(receiver, context, 1);
                }
            }
            Assert.False(global.EventsSuspended);
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void SuspendedPublication_DoesNotDispatchOrQueueUiWork(bool background, bool kbBroker)
        {
            // This SDK exposes Publisher and Background, not a UserInterface
            // ThreadOption. Model deferred UI work with a controlled context.
            var global = EventsService.Events;
            Assert.False(global.EventsSuspended);
            var broker = kbBroker ? CreateSecondaryBroker() : global;
            var context = new QueuedContext();
            var receiver = new Receiver(context);
            using var receiverSignal = receiver.Changed;
            object publisher = kbBroker ? (object)new KbPublisher() : new GlobalPublisher();
            Action raise = kbBroker
                ? (Action)(() => ((KbPublisher)publisher).Raise(background))
                : () => ((GlobalPublisher)publisher).Raise(background);

            using (broker.ScopedParticipation(publisher))
            using (broker.ScopedParticipation(receiver))
            {
                raise();
                Assert.True(receiver.Changed.Wait(2000),
                    "The enabled SDK broker did not deliver the initial publication.");
                Assert.Equal(1, receiver.Count);
                Assert.Equal(1, context.PostCount);
                Assert.Equal(0, receiver.UiCount);
                context.Drain();
                Assert.Equal(1, receiver.UiCount);

                using (new SdkEventSuppressionScope(kbBroker ? broker : null))
                {
                    raise();
                    AssertQuiet(receiver, context, 1);
                }
                // Includes a bounded asynchronous observation after resumption:
                // a suppressed event must not be replayed or enqueue UI work.
                AssertQuiet(receiver, context, 1);

                raise();
                Assert.True(receiver.Changed.Wait(2000),
                    "The SDK broker did not resume delivery after the scope.");
                Assert.Equal(2, receiver.Count);
                Assert.Equal(2, context.PostCount);
                context.Drain();
                Assert.Equal(2, receiver.UiCount);
                AssertQuiet(receiver, context, 2);
            }
            Assert.False(global.EventsSuspended);
            Assert.False(broker.EventsSuspended);
        }

        private static EventsService CreateSecondaryBroker()
        {
            // Diagnostic broker only: never instantiate or open a KnowledgeBase.
            var constructor = typeof(EventsService).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic,
                null, new[] { typeof(string), typeof(Type), typeof(Type) }, null);
            Assert.NotNull(constructor);
            return (EventsService)constructor.Invoke(new object[] {
                "GxMcp.AsyncIsolationProbe", typeof(KBEventPublicationAttribute), typeof(KBEventArgs) });
        }

        private static void AssertQuiet(Receiver receiver, QueuedContext context, int expected)
        {
            receiver.Changed.Reset();
            Assert.Equal(expected, receiver.Count);
            Assert.Equal(expected, context.PostCount);
            Assert.False(receiver.Changed.Wait(250), "An unexpected asynchronous SDK callback was delivered.");
            Assert.Equal(expected, receiver.Count);
            Assert.Equal(expected, context.PostCount);
            context.Drain();
            Assert.Equal(expected, receiver.UiCount);
        }

        public sealed class GlobalPublisher
        {
            [EventPublication(PublisherTopic)] public event EventHandler PublisherSignal;
            [EventPublication(BackgroundTopic)] public event EventHandler BackgroundSignal;
            public void Raise(bool background)
            {
                if (background) BackgroundSignal?.Invoke(this, EventArgs.Empty);
                else PublisherSignal?.Invoke(this, EventArgs.Empty);
            }
        }

        public sealed class KbPublisher
        {
            [KBEventPublication(PublisherTopic)] public event EventHandler<KBEventArgs> PublisherSignal;
            [KBEventPublication(BackgroundTopic)] public event EventHandler<KBEventArgs> BackgroundSignal;
            public void Raise(bool background)
            {
                if (background) BackgroundSignal?.Invoke(this, new KBEventArgs(null));
                else PublisherSignal?.Invoke(this, new KBEventArgs(null));
            }
        }

        public sealed class Receiver
        {
            private readonly SynchronizationContext _context;
            private int _count;
            private int _uiCount;
            public Receiver(SynchronizationContext context) { _context = context; }
            public readonly ManualResetEventSlim Changed = new ManualResetEventSlim();
            public int Count => Volatile.Read(ref _count);
            public int UiCount => Volatile.Read(ref _uiCount);
            [EventSubscription(PublisherTopic, ThreadOption.Publisher)]
            public void PublisherReceive(object sender, EventArgs args) => Receive();
            [EventSubscription(BackgroundTopic, ThreadOption.Background)]
            public void BackgroundReceive(object sender, EventArgs args) => Receive();
            private void Receive()
            {
                Interlocked.Increment(ref _count);
                _context.Post(_ => Interlocked.Increment(ref _uiCount), null);
                Changed.Set();
            }
        }

        private sealed class QueuedContext : SynchronizationContext
        {
            private readonly ConcurrentQueue<Action> _queue = new ConcurrentQueue<Action>();
            private int _posts;
            public int PostCount => Volatile.Read(ref _posts);
            public override void Post(SendOrPostCallback callback, object state)
            {
                _queue.Enqueue(() => callback(state));
                Interlocked.Increment(ref _posts);
            }
            public void Drain()
            {
                while (_queue.TryDequeue(out var callback)) callback();
            }
        }
    }
}
