using System.Data;

using AwesomeAssertions;

namespace Ahtola.Tests;

// NormalizeIsolationLevel runs in the constructor, ahead of any local/remote branching.
public sealed class TransactionIsolationLevelTests
{
    [Test]
    [TestCase(IsolationLevel.Unspecified)]
    [TestCase(IsolationLevel.ReadCommitted)]
    [TestCase(IsolationLevel.RepeatableRead)]
    [TestCase(IsolationLevel.Serializable)]
    [TestCase(IsolationLevel.ReadUncommitted)]
    public void BeginTransactionAcceptsEveryIsolationLevelSqliteCanHonour(IsolationLevel isolationLevel)
    {
        using var connection = new AhtolaConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();

        // RepeatableRead used to throw; Serializable is strictly stronger, so upgrading honours the request.
        Action begin = () =>
        {
            using var transaction = connection.BeginTransaction(isolationLevel);
            transaction.Rollback();
        };

        begin.Should().NotThrow();
    }

    [Test]
    public void BeginTransactionStillRejectsAnIsolationLevelSqliteCannotProvide()
    {
        using var connection = new AhtolaConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();

        // Snapshot is not serializable-or-weaker, so it must keep failing rather than being downgraded.
        Action begin = () => connection.BeginTransaction(IsolationLevel.Snapshot);

        begin.Should().Throw<NotSupportedException>()
            .WithMessage("*Snapshot*");
    }
}
