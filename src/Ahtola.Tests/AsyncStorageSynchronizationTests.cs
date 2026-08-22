using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

public class AsyncStorageSynchronizationTests
{
    [Test]
    public async Task FifoGateServesAsyncWaitersInArrivalOrder()
    {
        using var gate = new AsyncFifoGate();
        using var first = gate.Enter(Timeout.InfiniteTimeSpan);
        var secondTask = gate.EnterAsync(Timeout.InfiniteTimeSpan).AsTask();
        var thirdTask = gate.EnterAsync(Timeout.InfiniteTimeSpan).AsTask();

        first.Dispose();
        using var second = await secondTask.WaitAsync(TimeSpan.FromSeconds(5));
        thirdTask.IsCompleted.Should().BeFalse();

        second.Dispose();
        using var third = await thirdTask.WaitAsync(TimeSpan.FromSeconds(5));
        third.IsActive.Should().BeTrue();
    }

    [Test]
    public async Task FifoGateCancellationDoesNotLeakOrSkipTheNextWaiter()
    {
        using var gate = new AsyncFifoGate();
        using var first = gate.Enter(Timeout.InfiniteTimeSpan);
        using var cancellation = new CancellationTokenSource();
        var canceledTask = gate
            .EnterAsync(Timeout.InfiniteTimeSpan, cancellation.Token)
            .AsTask();
        var nextTask = gate.EnterAsync(Timeout.InfiniteTimeSpan).AsTask();

        cancellation.Cancel();
        Func<Task> awaitCanceled = async () => await canceledTask;
        await awaitCanceled.Should().ThrowAsync<OperationCanceledException>();

        first.Dispose();
        using var next = await nextTask.WaitAsync(TimeSpan.FromSeconds(5));
        next.IsActive.Should().BeTrue();
    }

    [Test]
    public async Task FifoGateTimeoutAndDisposalReleaseEveryWaiter()
    {
        using var gate = new AsyncFifoGate();
        using var first = gate.Enter(Timeout.InfiniteTimeSpan);
        var timedOutTask = gate.EnterAsync(TimeSpan.FromMilliseconds(20)).AsTask();
        Func<Task> awaitTimeout = async () => await timedOutTask;
        await awaitTimeout.Should().ThrowAsync<TimeoutException>();

        var disposedTask = gate.EnterAsync(Timeout.InfiniteTimeSpan).AsTask();
        gate.Dispose();
        Func<Task> awaitDisposed = async () => await disposedTask;
        await awaitDisposed.Should().ThrowAsync<ObjectDisposedException>();

        first.Dispose();
        Action enterAgain = () => gate.Enter();
        enterAgain.Should().Throw<ObjectDisposedException>();
    }

    [Test]
    public async Task TransactionLockAsyncWaitersPreserveFifoHandoff()
    {
        var transactionLock = new EmbeddedTransactionLock();
        var first = new object();
        var second = new object();
        var third = new object();
        transactionLock.Enter(first, excludeReaders: false, Timeout.InfiniteTimeSpan);
        var secondTask = transactionLock
            .EnterAsync(second, excludeReaders: false, Timeout.InfiniteTimeSpan)
            .AsTask();
        var thirdTask = transactionLock
            .EnterAsync(third, excludeReaders: false, Timeout.InfiniteTimeSpan)
            .AsTask();

        transactionLock.Exit(first);
        await secondTask.WaitAsync(TimeSpan.FromSeconds(5));
        transactionLock.IsHeldBy(second).Should().BeTrue();
        thirdTask.IsCompleted.Should().BeFalse();

        transactionLock.Exit(second);
        await thirdTask.WaitAsync(TimeSpan.FromSeconds(5));
        transactionLock.IsHeldBy(third).Should().BeTrue();
        transactionLock.Exit(third);
    }

    [Test]
    public async Task TransactionLockCanceledHeadDoesNotStrandOwnership()
    {
        var transactionLock = new EmbeddedTransactionLock();
        var first = new object();
        var canceled = new object();
        var next = new object();
        transactionLock.Enter(first, excludeReaders: false, Timeout.InfiniteTimeSpan);
        using var cancellation = new CancellationTokenSource();
        var canceledTask = transactionLock
            .EnterAsync(canceled, excludeReaders: false, Timeout.InfiniteTimeSpan, cancellation.Token)
            .AsTask();
        var nextTask = transactionLock
            .EnterAsync(next, excludeReaders: false, Timeout.InfiniteTimeSpan)
            .AsTask();

        cancellation.Cancel();
        Func<Task> awaitCanceled = async () => await canceledTask;
        await awaitCanceled.Should().ThrowAsync<OperationCanceledException>();

        transactionLock.Exit(first);
        await nextTask.WaitAsync(TimeSpan.FromSeconds(5));
        transactionLock.IsHeldBy(next).Should().BeTrue();
        transactionLock.IsHeldBy(canceled).Should().BeFalse();
        transactionLock.Exit(next);
    }

    [Test]
    public async Task TransactionLockTimeoutLeavesNoOwnershipBehind()
    {
        var transactionLock = new EmbeddedTransactionLock();
        var first = new object();
        var timedOut = new object();
        var next = new object();
        transactionLock.Enter(first, excludeReaders: false, Timeout.InfiniteTimeSpan);

        var timedOutTask = transactionLock
            .EnterAsync(timedOut, excludeReaders: false, TimeSpan.FromMilliseconds(20))
            .AsTask();
        Func<Task> awaitTimeout = async () => await timedOutTask;
        await awaitTimeout.Should().ThrowAsync<EmbeddedBusyException>()
            .WithMessage("database is locked");

        transactionLock.Exit(first);
        await transactionLock.EnterAsync(
            next,
            excludeReaders: false,
            TimeSpan.FromSeconds(1));
        transactionLock.IsHeldBy(next).Should().BeTrue();
        transactionLock.Exit(next);
    }

    [Test]
    public async Task TransactionLockExclusiveHandoffNeverAdmitsACompetingReader()
    {
        var transactionLock = new EmbeddedTransactionLock();
        var first = new object();
        var second = new object();
        var reader = new object();
        transactionLock.Enter(first, excludeReaders: true, Timeout.InfiniteTimeSpan);
        var secondTask = transactionLock
            .EnterAsync(second, excludeReaders: true, Timeout.InfiniteTimeSpan)
            .AsTask();
        var readerTask = transactionLock
            .ThrowIfReadBlockedAsync(reader, Timeout.InfiniteTimeSpan)
            .AsTask();

        transactionLock.Exit(first);
        await secondTask.WaitAsync(TimeSpan.FromSeconds(5));
        readerTask.IsCompleted.Should().BeFalse();

        transactionLock.Exit(second);
        await readerTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task PagerCheckpointKeepsPriorityAcrossAsyncWakeups()
    {
        var locks = new SqlitePagerLockManager();
        using var firstReader = locks.EnterReader();
        var checkpointTask = locks
            .EnterCheckpointAsync(TimeSpan.FromSeconds(5))
            .AsTask();
        SpinWait.SpinUntil(
                () => locks.WaitingCheckpointCount == 1,
                TimeSpan.FromSeconds(2))
            .Should()
            .BeTrue();
        var laterReaderTask = locks
            .EnterReaderAsync(TimeSpan.FromSeconds(5))
            .AsTask();

        firstReader.Dispose();
        using var checkpoint = await checkpointTask.WaitAsync(TimeSpan.FromSeconds(5));
        locks.State.Should().Be(SqlitePagerLockState.Checkpoint);
        laterReaderTask.IsCompleted.Should().BeFalse();

        checkpoint.Dispose();
        using var laterReader = await laterReaderTask.WaitAsync(TimeSpan.FromSeconds(5));
        locks.State.Should().Be(SqlitePagerLockState.Readers);
    }

    [Test]
    public async Task PagerAsyncCancellationAndTimeoutDoNotLeakWriterOwnership()
    {
        var locks = new SqlitePagerLockManager();
        using var first = locks.EnterWriter();
        using var cancellation = new CancellationTokenSource();
        var canceledTask = locks
            .EnterWriterAsync(Timeout.InfiniteTimeSpan, cancellation.Token)
            .AsTask();
        cancellation.Cancel();
        Func<Task> awaitCanceled = async () => await canceledTask;
        await awaitCanceled.Should().ThrowAsync<OperationCanceledException>();

        var timedOutTask = locks
            .EnterWriterAsync(TimeSpan.FromMilliseconds(20))
            .AsTask();
        Func<Task> awaitTimeout = async () => await timedOutTask;
        await awaitTimeout.Should().ThrowAsync<SqlitePagerBusyException>();

        first.Dispose();
        using var next = await locks.EnterWriterAsync(TimeSpan.FromSeconds(1));
        locks.State.Should().Be(SqlitePagerLockState.Writer);
    }

    [Test]
    public async Task PagerExternalAsyncCancellationAndDisposalReleaseExactlyOnce()
    {
        var coordinator = new TestAsyncPagerCoordinator { BlockAcquisition = true };
        var locks = new SqlitePagerLockManager(coordinator);
        using var cancellation = new CancellationTokenSource();
        var canceledTask = locks
            .EnterWriterAsync(Timeout.InfiniteTimeSpan, cancellation.Token)
            .AsTask();
        SpinWait.SpinUntil(
                () => coordinator.AsyncAcquireCount == 1,
                TimeSpan.FromSeconds(2))
            .Should()
            .BeTrue();

        cancellation.Cancel();
        Func<Task> awaitCanceled = async () => await canceledTask;
        await awaitCanceled.Should().ThrowAsync<OperationCanceledException>();
        locks.State.Should().Be(SqlitePagerLockState.Unlocked);

        coordinator.BlockAcquisition = false;
        var lease = await locks.EnterWriterAsync(TimeSpan.FromSeconds(1));
        lease.Dispose();
        lease.Dispose();

        coordinator.LeaseDisposeCount.Should().Be(1);
        locks.State.Should().Be(SqlitePagerLockState.Unlocked);
    }

    [Test]
    public async Task AsyncBusyBackoffObservesCancellationWithoutBlocking()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Func<Task> wait = async () => await SqliteBusyBackoff.WaitAsync(
            attempt: 0,
            Timeout.InfiniteTimeSpan,
            stopwatch: null,
            cancellation.Token);

        await wait.Should().ThrowAsync<OperationCanceledException>();
    }

    private sealed class TestAsyncPagerCoordinator :
        ISqlitePagerLockCoordinator,
        IAsyncSqlitePagerLockCoordinator
    {
        private volatile bool _blockAcquisition;
        private int _asyncAcquireCount;
        private int _leaseDisposeCount;

        internal bool BlockAcquisition
        {
            get => _blockAcquisition;
            set => _blockAcquisition = value;
        }

        internal int AsyncAcquireCount => Volatile.Read(ref _asyncAcquireCount);

        internal int LeaseDisposeCount => Volatile.Read(ref _leaseDisposeCount);

        public IDisposable Acquire(SqlitePagerLockOperation operation, TimeSpan timeout)
            => throw new AssertionException("Synchronous coordinator acquisition was not expected.");

        public IDisposable AcquireRecovery(TimeSpan timeout)
            => throw new AssertionException("Synchronous coordinator acquisition was not expected.");

        public async ValueTask<IDisposable> AcquireAsync(
            SqlitePagerLockOperation operation,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _asyncAcquireCount);
            if (_blockAcquisition)
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new CountingLease(this);
        }

        public ValueTask<IDisposable> AcquireRecoveryAsync(
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IDisposable>(new CountingLease(this));

        private sealed class CountingLease(TestAsyncPagerCoordinator owner) : IDisposable
        {
            public void Dispose() => Interlocked.Increment(ref owner._leaseDisposeCount);
        }
    }
}
