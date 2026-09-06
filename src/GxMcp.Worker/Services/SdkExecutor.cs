using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// Admission and dispatch boundary for work that must execute on the existing
    /// GeneXus STA. It never creates a second SDK thread: the host supplies the
    /// bridge post callback and owner-thread predicate.
    /// </summary>
    public sealed class SdkExecutor : IDisposable
    {
        private readonly Func<bool> _isOwnerThread;
        private readonly Func<Action, bool> _post;
        private readonly SemaphoreSlim _slots;
        private readonly ConcurrentDictionary<long, Action> _pendingCancellations =
            new ConcurrentDictionary<long, Action>();
        private long _nextInvocationId;
        private int _disposed;

        public SdkExecutor(Func<bool> isOwnerThread, Func<Action, bool> post, int capacity)
        {
            if (isOwnerThread == null) throw new ArgumentNullException(nameof(isOwnerThread));
            if (post == null) throw new ArgumentNullException(nameof(post));
            if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
            _isOwnerThread = isOwnerThread;
            _post = post;
            Capacity = capacity;
            _slots = new SemaphoreSlim(capacity, capacity);
        }

        public int Capacity { get; }

        public Task<T> InvokeAsync<T>(Func<T> operation, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));
            if (Volatile.Read(ref _disposed) != 0)
                return Task.FromException<T>(new ObjectDisposedException(nameof(SdkExecutor)));
            if (cancellationToken.IsCancellationRequested)
                return Task.FromCanceled<T>(cancellationToken);

            // Reentrancy is required when an SDK callback asks for a small nested
            // read. Posting it back to the queue would self-deadlock the STA.
            if (_isOwnerThread())
            {
                try { return Task.FromResult(operation()); }
                catch (Exception ex) { return Task.FromException<T>(ex); }
            }

            if (!_slots.Wait(0))
                return Task.FromException<T>(new SdkBusyException(Capacity));

            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            long invocationId = Interlocked.Increment(ref _nextInvocationId);
            int startedOrCancelled = 0;
            int slotReleased = 0;
            CancellationTokenRegistration cancellationRegistration = default(CancellationTokenRegistration);

            void ReleaseSlotOnce()
            {
                if (Interlocked.Exchange(ref slotReleased, 1) == 0)
                    ReleaseSlot();
            }

            void CancelPending()
            {
                // Exactly one of cancellation/disposal or the owner callback may
                // claim the invocation.  A cancelled wait must release admission
                // immediately and the later callback must not enter the SDK.
                if (Interlocked.CompareExchange(ref startedOrCancelled, 1, 0) != 0)
                    return;

                _pendingCancellations.TryRemove(invocationId, out _);
                if (Volatile.Read(ref _disposed) != 0)
                    completion.TrySetException(new ObjectDisposedException(nameof(SdkExecutor)));
                else
                    completion.TrySetCanceled(cancellationToken);
                ReleaseSlotOnce();
            }

            _pendingCancellations[invocationId] = CancelPending;
            if (cancellationToken.CanBeCanceled)
                cancellationRegistration = cancellationToken.Register(CancelPending);

            Action callback = () =>
            {
                _pendingCancellations.TryRemove(invocationId, out _);
                bool shouldRun = Interlocked.CompareExchange(ref startedOrCancelled, 1, 0) == 0;
                try
                {
                    if (shouldRun)
                    {
                        if (Volatile.Read(ref _disposed) != 0)
                            completion.TrySetException(new ObjectDisposedException(nameof(SdkExecutor)));
                        else if (cancellationToken.IsCancellationRequested)
                            completion.TrySetCanceled(cancellationToken);
                        else
                            completion.TrySetResult(operation());
                    }
                }
                catch (Exception ex) { completion.TrySetException(ex); }
                finally
                {
                    cancellationRegistration.Dispose();
                    ReleaseSlotOnce();
                }
            };

            bool posted = false;
            try { posted = _post(callback); }
            catch (Exception ex)
            {
                _pendingCancellations.TryRemove(invocationId, out _);
                if (Interlocked.CompareExchange(ref startedOrCancelled, 1, 0) == 0)
                    completion.TrySetException(ex);
                cancellationRegistration.Dispose();
                ReleaseSlotOnce();
                return completion.Task;
            }
            if (!posted)
            {
                _pendingCancellations.TryRemove(invocationId, out _);
                if (Interlocked.CompareExchange(ref startedOrCancelled, 1, 0) == 0)
                    completion.TrySetException(new SdkBusyException(Capacity));
                cancellationRegistration.Dispose();
                ReleaseSlotOnce();
            }
            return completion.Task;
        }

        public bool TryEnqueue(Action operation)
        {
            if (operation == null) return false;
            try
            {
                var task = InvokeAsync(() => { operation(); return true; });
                return !task.IsFaulted && !task.IsCanceled;
            }
            catch (SdkBusyException) { return false; }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

            foreach (var cancellation in _pendingCancellations.ToArray())
            {
                if (_pendingCancellations.TryRemove(cancellation.Key, out var cancel))
                    cancel();
            }

            _slots.Dispose();
        }

        private void ReleaseSlot()
        {
            try { _slots.Release(); }
            catch (ObjectDisposedException) { }
        }
    }

    public sealed class SdkBusyException : Exception
    {
        public SdkBusyException(int capacity)
            : base("SDK executor is busy; retry after the active operation yields.")
        {
            Capacity = capacity;
            RetryAfterMs = 250;
        }

        public int Capacity { get; }
        public int RetryAfterMs { get; }
    }
}
