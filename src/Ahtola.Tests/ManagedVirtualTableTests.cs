using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Execution;

namespace Ahtola.Tests;

[NonParallelizable]
public sealed class ManagedVirtualTableTests
{
    private const string ModuleName = "managed_virtual_table_test";
    private static readonly TestModule Module = RegisterModule();

    [Test]
    public void CreateVirtualTableScansVisibleColumnsAndDestroysTheInstance()
    {
        _ = Module;
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        Execute(connection, $"CREATE VIRTUAL TABLE entries USING {ModuleName}(alpha, beta);");

        ReadRows(connection, "SELECT * FROM entries;").Select(static row => row.Single()).Should().Equal(
            SqlValue.Integer(1),
            SqlValue.Integer(2));
        Module.LastCreated.Should().NotBeNull();
        Module.LastCreated!.FilterCalls.Should().Be(1);
        Module.LastCreated.BestIndexCalls.Should().Be(1);

        Execute(connection, "DROP TABLE entries;");
        Module.LastCreated.Destroyed.Should().BeTrue();
    }

    [Test]
    public void VirtualCursorVdbeInstructionsExecuteAndDisposeTheCursor()
    {
        var table = new TestTable();
        var program = new VdbeProgram(
            1,
            1,
            [
                new VOpenInstruction(new Cursor(0)),
                new VFilterInstruction(new Cursor(0), table.Plan, new RegisterRange(new Register(0), 0), new ProgramCounter(5)),
                new VColumnInstruction(new Cursor(0), 0, new Register(0)),
                new ResultRowInstruction(new RegisterRange(new Register(0), 1)),
                new VNextInstruction(new Cursor(0), new ProgramCounter(2)),
                new HaltInstruction(),
            ],
            sorterCount: 0,
            accumulatorCount: 0,
            distinctSetCount: 0,
            parameterSlotCount: 0,
            workTableCount: 0);

        using var statement = new ResumableStatement(
            program,
            virtualTableBindings: [new VdbeVirtualTableBinding(table)]);
        statement.StepResumable().Should().Be(ResumableStatementStepResult.Row);
        statement.CurrentRow.Should().Equal(SqlValue.Integer(1));
        statement.StepResumable().Should().Be(ResumableStatementStepResult.Row);
        statement.CurrentRow.Should().Equal(SqlValue.Integer(2));
        statement.StepResumable().Should().Be(ResumableStatementStepResult.Done);
        table.CursorDisposed.Should().BeTrue();
    }

    private static TestModule RegisterModule()
    {
        var module = new TestModule();
        ManagedVirtualTableModuleRegistry.Register(module);
        return module;
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        while (statement.Step() != StatementStepResult.Done)
        {
        }
    }

    private static IReadOnlyList<SqlValue[]> ReadRows(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        var rows = new List<SqlValue[]>();
        while (statement.Step() == StatementStepResult.Row)
        {
            var row = new SqlValue[statement.GetColumnCount()];
            for (var index = 0; index < row.Length; index++)
                row[index] = statement.GetValue(index);
            rows.Add(row);
        }

        return rows;
    }

    private sealed class TestModule : ManagedVirtualTableModule
    {
        public override string Name => ModuleName;

        public TestTable? LastCreated { get; private set; }

        public override ManagedVirtualTable Create(ManagedVirtualTableCreateContext context)
            => LastCreated = new TestTable();
    }

    private sealed class TestTable : ManagedVirtualTable
    {
        private static readonly ManagedVirtualTableSchema TestSchema = new(
            [
                new ManagedVirtualTableColumn("value", ManagedVirtualTableAffinity.Integer),
                new ManagedVirtualTableColumn("query", ManagedVirtualTableAffinity.Text, IsHidden: true),
            ]);

        public int BestIndexCalls { get; private set; }
        public int FilterCalls { get; private set; }
        public bool Destroyed { get; private set; }
        private TestCursor? LastCursor { get; set; }
        public bool CursorDisposed => LastCursor?.Disposed == true;
        public ManagedVirtualTablePlan Plan { get; } = new([]);

        public override ManagedVirtualTableSchema Schema => TestSchema;

        public override ManagedVirtualTablePlan BestIndex(
            IReadOnlyList<ManagedVirtualTableConstraint> constraints,
            IReadOnlyList<ManagedVirtualTableOrderBy> orderBy)
        {
            BestIndexCalls++;
            return Plan;
        }

        public override ManagedVirtualTableCursor Open() => LastCursor = new TestCursor(this);

        public override void Destroy() => Destroyed = true;

        private sealed class TestCursor(TestTable table) : ManagedVirtualTableCursor
        {
            private int _position;

            public bool Disposed { get; private set; }

            public override bool Filter(ManagedVirtualTablePlan plan, IReadOnlyList<SqlValue> arguments)
            {
                table.FilterCalls++;
                _position = 0;
                return true;
            }

            public override void Next() => _position++;

            public override bool Eof => _position >= 2;

            public override SqlValue Column(int columnIndex)
                => columnIndex switch
                {
                    0 => SqlValue.Integer(_position + 1),
                    1 => SqlValue.Text("hidden"),
                    _ => throw new ArgumentOutOfRangeException(nameof(columnIndex)),
                };

            public override long RowId => _position + 1;

            public override void Dispose() => Disposed = true;
        }
    }
}
