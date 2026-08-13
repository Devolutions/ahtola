using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Execution;

namespace Ahtola.Tests;

// Execution coverage for the late-binding mechanism through ResumableStatement: a program that reads
// parameter slots re-runs with fresh values after Reset/Rebind without being rebuilt, every SqlValue kind
// survives the bind→LoadParameter→ResultRow round trip, a bound NULL is a real value while a missing
// binding is a hard error, and the binding lifecycle (Reset preserves, Rebind replaces only from Ready,
// Dispose clears, width must match) behaves exactly as designed.
public class LateBoundParameterExecutionTests
{
    [Test]
    public void RebindReExecutesWithNewValuesWithoutRebuildingTheProgram()
    {
        var program = ParamRowProgram(1);
        using var statement = new ResumableStatement(program, parameterBinding: Binding(SqlValue.Integer(1)));

        Integers(DrainRows(statement)).Should().Equal(1);

        statement.Reset();
        statement.Rebind(Binding(SqlValue.Integer(2)));

        Integers(DrainRows(statement)).Should().Equal(2);
        statement.Program.Should().BeSameAs(program);
    }

    [Test]
    public void PreservesEveryValueKindThroughBoundParameters()
    {
        var blob = new byte[] { 0x01, 0x02, 0xFF };
        var program = ParamRowProgram(5);
        var binding = VdbeParameterBinding.FromValues(
            SqlValue.Integer(long.MinValue),
            SqlValue.Real(3.5),
            SqlValue.Text("π"),
            SqlValue.Blob(blob),
            SqlValue.Null);

        using var statement = new ResumableStatement(program, parameterBinding: binding);
        var row = DrainRows(statement).Single();

        row[0].Should().Be(SqlValue.Integer(long.MinValue));
        row[1].Should().Be(SqlValue.Real(3.5));
        row[2].Should().Be(SqlValue.Text("π"));
        row[3].Kind.Should().Be(SqlValueKind.Blob);
        row[3].AsBlob().ToArray().Should().Equal(blob);
        row[4].Kind.Should().Be(SqlValueKind.Null);
    }

    [Test]
    public void BoundNullIsARealValueDistinctFromAMissingBinding()
    {
        var program = ParamRowProgram(1);

        using var bound = new ResumableStatement(program, parameterBinding: Binding(SqlValue.Null));
        DrainRows(bound).Single()[0].Kind.Should().Be(SqlValueKind.Null);

        using var unbound = new ResumableStatement(program);
        Assert.Throws<VdbeParameterBindingException>(() => unbound.StepResumable());
    }

    [Test]
    public void ResetPreservesTheBindingAndReplaysTheSameValues()
    {
        var program = ParamRowProgram(1);
        using var statement = new ResumableStatement(program, parameterBinding: Binding(SqlValue.Integer(42)));

        Integers(DrainRows(statement)).Should().Equal(42);

        statement.Reset();

        statement.ParameterBinding.Should().NotBeNull();
        Integers(DrainRows(statement)).Should().Equal(42);
    }

    [Test]
    public void RebindReplacesTheExposedBinding()
    {
        var program = ParamRowProgram(1);
        var first = Binding(SqlValue.Integer(1));
        var second = Binding(SqlValue.Integer(2));
        using var statement = new ResumableStatement(program, parameterBinding: first);

        statement.ParameterBinding.Should().BeSameAs(first);

        statement.Rebind(second);
        statement.ParameterBinding.Should().BeSameAs(second);
    }

    [Test]
    public void RebindEnablesAProgramConstructedWithoutABinding()
    {
        var program = ParamRowProgram(1);
        using var statement = new ResumableStatement(program);

        statement.ParameterBinding.Should().BeNull();
        statement.Rebind(Binding(SqlValue.Integer(5)));

        Integers(DrainRows(statement)).Should().Equal(5);
    }

    [Test]
    public void RebindIsRejectedOnceExecutionHasStarted()
    {
        var program = ParamRowProgram(1);
        using var statement = new ResumableStatement(program, parameterBinding: Binding(SqlValue.Integer(1)));

        statement.StepResumable().Should().Be(ResumableStatementStepResult.Row);
        statement.State.Should().Be(ResumableStatementState.Row);

        Assert.Throws<InvalidOperationException>(() => statement.Rebind(Binding(SqlValue.Integer(2))));
    }

    [Test]
    public void RebindAfterCancellationRequiresResetEvenWhenTheStatementIsReady()
    {
        using var cancellation = new CancellationTokenSource();
        var cancel = new VdbeScalarFunction
        {
            Name = "cancel",
            Arity = 0,
            Invoke = _ =>
            {
                cancellation.Cancel();
                return SqlValue.Null;
            },
        };
        var program = new VdbeProgram(
            registerCount: 3,
            cursorCount: 0,
            instructions:
            [
                new LoadParameterInstruction(new Register(0), new ParameterSlot(0)),
                new FunctionInstruction(new Register(2), cancel, new RegisterRange(new Register(0), 0)),
                new LoadParameterInstruction(new Register(1), new ParameterSlot(1)),
                new ResultRowInstruction(new RegisterRange(new Register(0), 2)),
                new HaltInstruction(),
            ],
            parameterSlotCount: 2);
        using var statement = new ResumableStatement(
            program,
            parameterBinding: Binding(SqlValue.Integer(1), SqlValue.Integer(2)));

        Assert.Throws<OperationCanceledException>(() => statement.StepResumable(cancellation.Token));
        statement.State.Should().Be(ResumableStatementState.Ready);
        Assert.Throws<InvalidOperationException>(() =>
            statement.Rebind(Binding(SqlValue.Integer(10), SqlValue.Integer(20))));

        statement.Reset();
        statement.Rebind(Binding(SqlValue.Integer(10), SqlValue.Integer(20)));
        DrainRows(statement).Single().Should().Equal(SqlValue.Integer(10), SqlValue.Integer(20));
    }

    [Test]
    public void RebindRejectsNull()
    {
        var program = ParamRowProgram(1);
        using var statement = new ResumableStatement(program, parameterBinding: Binding(SqlValue.Integer(1)));

        Assert.Throws<ArgumentNullException>(() => statement.Rebind(null!));
    }

    [Test]
    public void RebindRejectsAWidthThatDoesNotMatchTheProgram()
    {
        var program = ParamRowProgram(2);
        using var statement = new ResumableStatement(
            program,
            parameterBinding: VdbeParameterBinding.FromValues(SqlValue.Integer(1), SqlValue.Integer(2)));

        Assert.Throws<VdbeParameterBindingException>(() => statement.Rebind(Binding(SqlValue.Integer(9))));
    }

    [Test]
    public void ConstructorRejectsABindingWhoseWidthDoesNotMatchTheProgram()
    {
        var program = ParamRowProgram(2);

        Assert.Throws<VdbeParameterBindingException>(
            () => new ResumableStatement(program, parameterBinding: Binding(SqlValue.Integer(1))));
    }

    [Test]
    public void DisposeClearsTheBindingAndBlocksRebinding()
    {
        var program = ParamRowProgram(1);
        var statement = new ResumableStatement(program, parameterBinding: Binding(SqlValue.Integer(1)));

        statement.ParameterBinding.Should().NotBeNull();

        statement.Dispose();

        statement.ParameterBinding.Should().BeNull();
        Assert.Throws<ObjectDisposedException>(() => statement.Rebind(Binding(SqlValue.Integer(2))));
    }

    // A single-row program that loads parameter slots 0..n-1 into registers and emits them as one row.
    private static VdbeProgram ParamRowProgram(int slotCount)
    {
        var instructions = new List<VdbeInstruction>();
        for (var index = 0; index < slotCount; index++)
            instructions.Add(new LoadParameterInstruction(new Register(index), new ParameterSlot(index)));

        instructions.Add(new ResultRowInstruction(new RegisterRange(new Register(0), slotCount)));
        instructions.Add(new HaltInstruction());
        return new VdbeProgram(slotCount, cursorCount: 0, instructions, parameterSlotCount: slotCount);
    }

    private static VdbeParameterBinding Binding(params SqlValue[] values) => VdbeParameterBinding.FromValues(values);

    private static List<long> Integers(IEnumerable<SqlValue[]> rows)
        => rows.Select(row => row[0].AsInteger()).ToList();

    private static List<SqlValue[]> DrainRows(ResumableStatement statement)
    {
        var rows = new List<SqlValue[]>();
        while (true)
        {
            var result = statement.StepResumable();
            if (result == ResumableStatementStepResult.Row)
                rows.Add([.. statement.CurrentRow!]);
            else if (result == ResumableStatementStepResult.Done)
                break;
            else
                throw new InvalidOperationException($"Unexpected step result {result}.");
        }

        return rows;
    }
}
