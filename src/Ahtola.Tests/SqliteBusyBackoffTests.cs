using AwesomeAssertions;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

public class SqliteBusyBackoffTests
{
    [Test]
    public void DelayForAttemptFollowsSqliteDefaultBusySchedule()
    {
        SqliteBusyBackoff.DelayForAttempt(0, TimeSpan.Zero, Timeout.InfiniteTimeSpan)
            .Should().Be(TimeSpan.FromMilliseconds(1));
        SqliteBusyBackoff.DelayForAttempt(1, TimeSpan.Zero, Timeout.InfiniteTimeSpan)
            .Should().Be(TimeSpan.FromMilliseconds(2));
        SqliteBusyBackoff.DelayForAttempt(2, TimeSpan.Zero, Timeout.InfiniteTimeSpan)
            .Should().Be(TimeSpan.FromMilliseconds(5));
        SqliteBusyBackoff.DelayForAttempt(11, TimeSpan.Zero, Timeout.InfiniteTimeSpan)
            .Should().Be(TimeSpan.FromMilliseconds(100));
        SqliteBusyBackoff.DelayForAttempt(100, TimeSpan.Zero, Timeout.InfiniteTimeSpan)
            .Should().Be(TimeSpan.FromMilliseconds(100));
    }

    [Test]
    public void DelayForAttemptRespectsRemainingTimeoutBudget()
    {
        SqliteBusyBackoff.DelayForAttempt(0, TimeSpan.Zero, TimeSpan.FromMilliseconds(1))
            .Should().Be(TimeSpan.FromMilliseconds(1));
        SqliteBusyBackoff.DelayForAttempt(0, TimeSpan.Zero, TimeSpan.Zero)
            .Should().Be(TimeSpan.Zero);
        SqliteBusyBackoff.DelayForAttempt(5, TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(52))
            .Should().Be(TimeSpan.FromMilliseconds(2));
    }

    [Test]
    public void PagerBusyExceptionPreservesOperationAndStage4Reason()
    {
        var busy = new SqlitePagerBusyException(
            SqlitePagerLockOperation.Writer,
            SqlitePagerBusyReason.Recovery,
            TimeSpan.FromSeconds(1));
        busy.Operation.Should().Be(SqlitePagerLockOperation.Writer);
        busy.Reason.Should().Be(SqlitePagerBusyReason.Recovery);
        busy.Timeout.Should().Be(TimeSpan.FromSeconds(1));
        busy.Message.Should().Contain("recovery");

        var plain = new SqlitePagerBusyException(SqlitePagerLockOperation.Checkpoint, TimeSpan.Zero);
        plain.Reason.Should().Be(SqlitePagerBusyReason.Busy);
    }
}
