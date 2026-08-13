using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Execution;

namespace Ahtola.Tests;

// Direct state-machine coverage for VdbeTransactionContext's savepoint name matching. SQLite compares
// savepoint identifiers case-insensitively, and this binding folds every SQL identifier with ordinal
// case-insensitivity, so RELEASE/ROLLBACK TO must resolve a frame opened as "Foo" when named "foo" or
// "fOo". These tests drive the context object directly (rather than through the interpreter's opcodes) so
// they pin the resolution rule — topmost matching frame wins, released names become unmatchable, and a
// missing name is an error — independently of any SQL wiring.
public class VdbeTransactionContextSavepointNameMatchingTests
{
    [Test]
    public void ReleaseResolvesTheSavepointNameRegardlessOfLetterCase()
    {
        var context = new VdbeTransactionContext();
        context.Savepoint("Foo", Registers(1));

        context.Release("foo");

        context.InTransaction.Should().BeFalse();
        context.Depth.Should().Be(0);
    }

    [Test]
    public void RollbackToResolvesTheSavepointNameRegardlessOfLetterCaseAndKeepsTheFrame()
    {
        var context = new VdbeTransactionContext();
        var registers = Registers(1);
        context.Savepoint("Foo", registers);

        registers[0] = SqlValue.Integer(2);
        context.RollbackTo("fOo", registers);

        // The frame's snapshot is restored, and the named frame itself survives so it can be rolled back to
        // again — the case used to name it does not change either outcome.
        registers[0].AsInteger().Should().Be(1);
        context.Depth.Should().Be(1);
        context.SavepointNames.Should().Equal("Foo");
    }

    [Test]
    public void DuplicateNestedNameInADifferentCaseResolvesToTheInnermostFrame()
    {
        var context = new VdbeTransactionContext();
        var registers = Registers(10);
        context.Savepoint("Foo", registers); // outer snapshot: 10

        registers[0] = SqlValue.Integer(20);
        context.Savepoint("foo", registers); // inner snapshot: 20, same name in a different case

        registers[0] = SqlValue.Integer(30);
        context.RollbackTo("fOo", registers);

        // Topmost match wins, so rolling back resolves to the inner frame (snapshot 20), not the outer one
        // (snapshot 10). Both frames stay open: reusing a name nests unambiguously rather than colliding.
        registers[0].AsInteger().Should().Be(20);
        context.Depth.Should().Be(2);
        context.SavepointNames.Should().Equal("Foo", "foo");
    }

    [Test]
    public void ReleasingTheInnerDuplicateExposesTheOuterFrameToTheSameName()
    {
        var context = new VdbeTransactionContext();
        var registers = Registers(10);
        context.Savepoint("Foo", registers); // outer snapshot: 10

        registers[0] = SqlValue.Integer(20);
        context.Savepoint("foo", registers); // inner snapshot: 20

        registers[0] = SqlValue.Integer(30);
        context.Release("FOO"); // folds the inner (topmost) frame without restoring registers

        context.Depth.Should().Be(1);
        context.SavepointNames.Should().Equal("Foo");
        registers[0].AsInteger().Should().Be(30);

        // With the inner duplicate gone, the same case-insensitive name now resolves to the outer frame.
        context.RollbackTo("foo", registers);
        registers[0].AsInteger().Should().Be(10);
        context.Depth.Should().Be(1);
    }

    [Test]
    public void ReleaseOfAnUnknownSavepointNameThrows()
    {
        var context = new VdbeTransactionContext();
        context.Savepoint("Foo", Registers(1));

        Assert.Throws<VdbeTransactionException>(() => context.Release("bar"));
    }

    [Test]
    public void RollbackToOfAnUnknownSavepointNameThrows()
    {
        var context = new VdbeTransactionContext();
        var registers = Registers(1);
        context.Savepoint("Foo", registers);

        Assert.Throws<VdbeTransactionException>(() => context.RollbackTo("bar", registers));
    }

    [Test]
    public void AReleasedSavepointNameIsNoLongerMatchableInAnyCase()
    {
        var context = new VdbeTransactionContext();
        context.Savepoint("Foo", Registers(1));
        context.Release("foo");

        Assert.Throws<VdbeTransactionException>(() => context.Release("fOo"));
    }

    private static SqlValue[] Registers(params long[] values)
    {
        var registers = new SqlValue[values.Length];
        for (var index = 0; index < values.Length; index++)
            registers[index] = SqlValue.Integer(values[index]);
        return registers;
    }
}
