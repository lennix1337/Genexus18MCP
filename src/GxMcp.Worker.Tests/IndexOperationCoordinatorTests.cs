using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class IndexOperationCoordinatorTests
    {
        [Fact]
        public async Task ConcurrentRequestsShareOneActiveOperation()
        {
            var coordinator = new IndexOperationCoordinator();
            var leases = await Task.WhenAll(Enumerable.Range(0, 32)
                .Select(_ => Task.Run(() => coordinator.Acquire(false, workerAlive: true, recoverStalled: null))));

            Assert.Single(leases.Select(lease => lease.OperationId).Distinct());
            Assert.Single(leases, lease => !lease.Reused);
            Assert.All(leases, lease => Assert.Equal(leases[0].Generation, lease.Generation));
        }

        [Fact]
        public void ForceDoesNotCancelAHealthyActiveWorker()
        {
            var coordinator = new IndexOperationCoordinator();
            var first = coordinator.Acquire(false, workerAlive: true, recoverStalled: null);
            coordinator.MarkWorkerStarted(first.Generation);
            int recoveries = 0;

            var repeated = coordinator.Acquire(true, workerAlive: true, recoverStalled: () => recoveries++);

            Assert.True(repeated.Reused);
            Assert.Equal(first.OperationId, repeated.OperationId);
            Assert.Equal(0, recoveries);
        }

        [Fact]
        public void ForceRecoversStalledWorkerWithANewGeneration()
        {
            var coordinator = new IndexOperationCoordinator();
            var first = coordinator.Acquire(false, workerAlive: true, recoverStalled: null);
            coordinator.MarkWorkerStarted(first.Generation);
            Assert.True(coordinator.MarkStalled(first.Generation));
            int recoveries = 0;

            var recovered = coordinator.Acquire(true, workerAlive: true, recoverStalled: () => recoveries++);

            Assert.False(recovered.Reused);
            Assert.NotEqual(first.OperationId, recovered.OperationId);
            Assert.NotEqual(first.Generation, recovered.Generation);
            Assert.Equal(1, recoveries);
            Assert.False(coordinator.IsCurrent(first.Generation));
            Assert.True(coordinator.IsCurrent(recovered.Generation));
        }

        [Fact]
        public void OldCompletionCannotFinishARecoveredOperation()
        {
            var coordinator = new IndexOperationCoordinator();
            var first = coordinator.Acquire(false, workerAlive: true, recoverStalled: null);
            coordinator.MarkWorkerStarted(first.Generation);
            coordinator.MarkStalled(first.Generation);
            var recovered = coordinator.Acquire(true, workerAlive: true, recoverStalled: null);

            Assert.False(coordinator.Complete(first.Generation));
            Assert.True(coordinator.IsCurrent(recovered.Generation));
            Assert.True(coordinator.Complete(recovered.Generation));
            Assert.False(coordinator.GetSnapshot(workerAlive: false).Active);
        }

        [Fact]
        public void ForceDoesNotCancelBeforeWorkerStarts()
        {
            var coordinator = new IndexOperationCoordinator();
            var first = coordinator.Acquire(false, workerAlive: false, recoverStalled: null);
            int recoveries = 0;

            var repeated = coordinator.Acquire(true, workerAlive: false, recoverStalled: () => recoveries++);
            var snapshot = coordinator.GetSnapshot(workerAlive: false);

            Assert.True(repeated.Reused);
            Assert.Equal(first.OperationId, repeated.OperationId);
            Assert.Equal(0, recoveries);
            Assert.Equal("Starting", snapshot.State);
            Assert.False(snapshot.Recoverable);
        }

        [Fact]
        public void DeadStartedWorkerIsRecoverableAndUsesWorkerExitedState()
        {
            var coordinator = new IndexOperationCoordinator();
            var first = coordinator.Acquire(false, workerAlive: false, recoverStalled: null);
            coordinator.MarkWorkerStarted(first.Generation);

            var snapshot = coordinator.GetSnapshot(workerAlive: false);

            Assert.Equal("WorkerExited", snapshot.State);
            Assert.True(snapshot.Recoverable);
        }

        [Fact]
        public void RetryBackoffRemainsSingleFlightUntilTheRetryStarts()
        {
            var coordinator = new IndexOperationCoordinator();
            var first = coordinator.Acquire(false, workerAlive: false, recoverStalled: null);
            Assert.True(coordinator.MarkRetryPending(first.Generation, pending: true));

            var repeated = coordinator.Acquire(true, workerAlive: false, recoverStalled: null);
            var waiting = coordinator.GetSnapshot(workerAlive: false);

            Assert.True(repeated.Reused);
            Assert.Equal(first.OperationId, repeated.OperationId);
            Assert.Equal("Starting", waiting.State);
            Assert.False(waiting.Recoverable);

            coordinator.MarkWorkerStarted(first.Generation);
            var running = coordinator.GetSnapshot(workerAlive: true);
            Assert.Equal("Building", running.State);
        }
    }
}
