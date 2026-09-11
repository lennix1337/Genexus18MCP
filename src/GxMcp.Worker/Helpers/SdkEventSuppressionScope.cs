using System;
using System.Threading;
using Artech.Architecture.Common.Events;
using Artech.Architecture.Common.Objects;
using Microsoft.Practices.CompositeUI.EventBroker;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Helpers
{
    // EventsSuspended is the SDK's public broker switch (also exposed by
    // ArtechTask). U16 tests show that the GLOBAL switch controls KB brokers;
    // setting only kb.Events.EventsSuspended does not suppress callbacks.
    internal sealed class SdkEventSuppressionScope : IDisposable
    {
        internal const string Topic = "event://GxMcp/EventsSaveIsolationProbe";
        internal const string KbTopic = "event://GxMcp/EventsSaveIsolationProbeKb";
        private static int _activeScopes;
        private static volatile bool _poisoned;
        internal static bool IsPoisoned => _poisoned;
        internal static bool IsActive => _poisoned || Volatile.Read(ref _activeScopes) != 0;
        internal static void Poison() => _poisoned = true;
        private readonly EventsService _global;
        private readonly EventsService _local;
        private readonly bool _globalBefore;
        private readonly bool _localBefore;
        private bool _disposed;

        internal SdkEventSuppressionScope(EventsService local = null)
        {
            _global = EventsService.Events ?? throw new InvalidOperationException("SDK global event broker unavailable.");
            _local = local;
            _globalBefore = _global.EventsSuspended;
            _localBefore = _local?.EventsSuspended ?? false;
            Interlocked.Increment(ref _activeScopes);
            try
            {
                _global.EventsSuspended = true;
                if (_local != null) _local.EventsSuspended = true;
                if (!_global.EventsSuspended) throw new InvalidOperationException("SDK event suspension was not accepted.");
            }
            catch { Dispose(); throw; }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                try { if (_local != null) _local.EventsSuspended = _localBefore; }
                finally { _global.EventsSuspended = _globalBefore; }
                if (_global.EventsSuspended != _globalBefore || (_local != null && _local.EventsSuspended != _localBefore))
                    throw new InvalidOperationException("SDK event broker state could not be restored.");
            }
            catch { _poisoned = true; throw; }
            finally { Interlocked.Decrement(ref _activeScopes); }
        }

        // Publishes an isolated synthetic topic, never a GeneXus lifecycle event.
        // The real KB instance is required for the KB broker's argument filter.
        internal static JObject VerifyBroker(KnowledgeBase kb)
            => VerifyBrokers(kb?.Events, kb);

        internal static JObject VerifyBrokers(EventsService local, KnowledgeBase kb)
        {
            var global = EventsService.Events;
            if (global == null || global.EventsSuspended || (local != null && local.EventsSuspended))
                throw new InvalidOperationException("The event brokers must be active before the isolation preflight.");
            var globalPublisher = new GlobalPublisher();
            var kbPublisher = new KbPublisher(kb);
            var globalReceiver = new Receiver();
            var kbReceiver = new KbReceiver();
            using (global.ScopedParticipation(globalPublisher))
            using (global.ScopedParticipation(globalReceiver))
            using (local?.ScopedParticipation(kbPublisher))
            using (local?.ScopedParticipation(kbReceiver))
            {
                globalPublisher.Raise();
                if (local != null) kbPublisher.Raise();
                AssertCounts(globalReceiver, kbReceiver, local != null, 1);
                using (new SdkEventSuppressionScope(local))
                {
                    globalPublisher.Raise();
                    if (local != null) kbPublisher.Raise();
                    AssertCounts(globalReceiver, kbReceiver, local != null, 1);
                }
                // Resumption must not replay the suppressed publication.
                AssertCounts(globalReceiver, kbReceiver, local != null, 1);
                globalPublisher.Raise();
                if (local != null) kbPublisher.Raise();
                AssertCounts(globalReceiver, kbReceiver, local != null, 2);
            }
            return new JObject
            {
                ["globalBrokerVerified"] = true, ["kbBrokerVerified"] = local != null,
                ["callbacksBeforeDuringAfter"] = new JArray(1, 1, 2),
                ["brokerStateRestored"] = true, ["sdkSaveAttempted"] = false
            };
        }

        private static void AssertCounts(Receiver global, KbReceiver local, bool hasLocal, int expected)
        {
            if (global.Count != expected || (hasLocal && local.Count != expected))
                throw new InvalidOperationException("The SDK event broker failed the normal/suspended/resumed callback test.");
        }

        public sealed class GlobalPublisher
        {
            [EventPublication(Topic)] public event EventHandler Signal;
            public void Raise() => Signal?.Invoke(this, EventArgs.Empty);
        }

        public sealed class KbPublisher
        {
            private readonly KnowledgeBase _kb;
            public KbPublisher(KnowledgeBase kb) { _kb = kb; }
            [KBEventPublication(KbTopic)] public event EventHandler<KBEventArgs> Signal;
            public void Raise() => Signal?.Invoke(this, new KBEventArgs(_kb));
        }

        public sealed class Receiver
        {
            public int Count;
            [EventSubscription(Topic, ThreadOption.Publisher)]
            public void Receive(object sender, EventArgs args) { Count++; }
        }

        public sealed class KbReceiver
        {
            public int Count;
            [EventSubscription(KbTopic, ThreadOption.Publisher)]
            public void Receive(object sender, EventArgs args) { Count++; }
        }
    }
}
