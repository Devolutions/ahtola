using Ahtola.Core;
using Ahtola.Core.Parsing;
using AwesomeAssertions;

namespace Ahtola.Tests;

public class LazyRowLoadConcurrencyTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task ConcurrentReadersWaitForCompleteOrFailedLoad(bool fail)
    {
        var table = new EmbeddedTable(
            "t", [new EmbeddedColumn("id", "INTEGER", true, false, false, null)]);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var secondStarted = new ManualResetEventSlim();
        var attempts = 0;
        table.AttachPendingRowLoader(target =>
        {
            Interlocked.Increment(ref attempts);
            target.Rows.Add([SqlValue.Integer(1)]);
            target.RowIds.Add(1);
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("The row-load test did not release its loader.");
            if (fail)
                throw new InvalidOperationException("injected row load failure");
            target.Rows.Add([SqlValue.Integer(2)]);
            target.RowIds.Add(2);
        });

        var first = Task.Run(() => table.Rows.Count);
        Task<int>? second = null;
        var completedEarly = false;
        try
        {
            entered.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
            second = Task.Run(() =>
            {
                secondStarted.Set();
                return table.Rows.Count;
            });
            secondStarted.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
            completedEarly = second.Wait(TimeSpan.FromMilliseconds(250));
        }
        finally
        {
            release.Set();
        }

        second.Should().NotBeNull();
        if (fail)
        {
            Func<Task> firstRead = async () => { _ = await first; };
            Func<Task> secondRead = async () => { _ = await second!; };
            await firstRead.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("injected row load failure");
            await secondRead.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*previous attempt*");
        }
        else
        {
            (await first).Should().Be(2);
            (await second!).Should().Be(2);
            table.RowIds.Should().Equal(1, 2);
        }
        completedEarly.Should().BeFalse("another reader must not see partially populated rows");
        attempts.Should().Be(1);
    }
}
