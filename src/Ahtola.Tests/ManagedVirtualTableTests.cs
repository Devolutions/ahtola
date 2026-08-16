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

    [Test]
    public void VirtualScanNegotiatesLocalConstraintsOrderAndResidualFiltering()
    {
        _ = Module;
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, $"CREATE VIRTUAL TABLE entries USING {ModuleName};");

        ReadRows(connection, "SELECT value FROM entries WHERE value >= 2 AND value < 3 ORDER BY value DESC;")
            .Select(static row => row.Single())
            .Should().Equal(SqlValue.Integer(2));

        Module.LastCreated!.LastConstraints.Should().Equal(
            new ManagedVirtualTableConstraint(0, ManagedVirtualTableConstraintOperator.GreaterThanOrEqual),
            new ManagedVirtualTableConstraint(0, ManagedVirtualTableConstraintOperator.LessThan));
        Module.LastCreated.LastOrderBy.Should().Equal(new ManagedVirtualTableOrderBy(0, Descending: true));
        Module.LastCreated.FilterArguments.Should().Equal(SqlValue.Integer(2), SqlValue.Integer(3));
    }

    [Test]
    public void VirtualTableDmlUsesVUpdateArgumentLayoutAndTransactionLifecycle()
    {
        _ = Module;
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, $"CREATE VIRTUAL TABLE entries USING {ModuleName};");

        Execute(connection, "INSERT INTO entries(value) VALUES (7);");
        Execute(connection, "UPDATE entries SET value = 8 WHERE value = 2;");
        Execute(connection, "DELETE FROM entries WHERE value = 1;");

        var table = Module.LastCreated!;
        table.BeginCalls.Should().Be(3);
        table.SyncCalls.Should().Be(3);
        table.CommitCalls.Should().Be(3);
        table.RollbackCalls.Should().Be(0);
        table.Updates.Should().HaveCount(3);
        table.Updates[0].Should().Equal(SqlValue.Null, SqlValue.Null, SqlValue.Integer(7), SqlValue.Null);
        table.Updates[1].Should().Equal(
            SqlValue.Integer(2), SqlValue.Integer(2), SqlValue.Integer(8), SqlValue.Text("hidden"));
        table.Updates[2].Should().Equal(
            SqlValue.Integer(1), SqlValue.Null, SqlValue.Integer(1), SqlValue.Text("hidden"));
    }

    [Test]
    public void VirtualTableDmlRollsBackWhenTheModuleRejectsAMutation()
    {
        _ = Module;
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, $"CREATE VIRTUAL TABLE entries USING {ModuleName};");
        Module.LastCreated!.ThrowOnUpdate = true;

        Action insert = () => Execute(connection, "INSERT INTO entries(value) VALUES (7);");

        insert.Should().Throw<EmbeddedSqlException>().WithMessage("virtual update failed");
        Module.LastCreated.BeginCalls.Should().Be(1);
        Module.LastCreated.SyncCalls.Should().Be(0);
        Module.LastCreated.CommitCalls.Should().Be(0);
        Module.LastCreated.RollbackCalls.Should().Be(1);
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
        public IReadOnlyList<ManagedVirtualTableConstraint> LastConstraints { get; private set; } = [];
        public IReadOnlyList<ManagedVirtualTableOrderBy> LastOrderBy { get; private set; } = [];
        public IReadOnlyList<SqlValue> FilterArguments { get; private set; } = [];
        public List<IReadOnlyList<SqlValue>> Updates { get; } = [];
        public int BeginCalls { get; private set; }
        public int SyncCalls { get; private set; }
        public int CommitCalls { get; private set; }
        public int RollbackCalls { get; private set; }
        public bool ThrowOnUpdate { get; set; }

        public override ManagedVirtualTableSchema Schema => TestSchema;

        public override ManagedVirtualTablePlan BestIndex(
            IReadOnlyList<ManagedVirtualTableConstraint> constraints,
            IReadOnlyList<ManagedVirtualTableOrderBy> orderBy)
        {
            BestIndexCalls++;
            LastConstraints = constraints.ToArray();
            LastOrderBy = orderBy.ToArray();
            return constraints.Count == 0
                ? Plan
                : new ManagedVirtualTablePlan(
                    constraints.Select((_, index) => new ManagedVirtualTableConstraintUsage(
                        index + 1,
                        Omit: index == 0)));
        }

        public override ManagedVirtualTableCursor Open() => LastCursor = new TestCursor(this);

        public override long? Update(IReadOnlyList<SqlValue> arguments)
        {
            Updates.Add(arguments.ToArray());
            if (ThrowOnUpdate)
                throw new EmbeddedSqlException("virtual update failed");
            return arguments[1].Kind == SqlValueKind.Integer ? arguments[1].AsInteger() : null;
        }

        public override void Begin() => BeginCalls++;
        public override void Sync() => SyncCalls++;
        public override void Commit() => CommitCalls++;
        public override void Rollback() => RollbackCalls++;
        public override void Destroy() => Destroyed = true;

        private sealed class TestCursor(TestTable table) : ManagedVirtualTableCursor
        {
            private int _position;
            private int _end = 2;

            public bool Disposed { get; private set; }

            public override bool Filter(ManagedVirtualTablePlan plan, IReadOnlyList<SqlValue> arguments)
            {
                table.FilterCalls++;
                table.FilterArguments = arguments.ToArray();
                _position = arguments.Count > 0 && arguments[0].Kind == SqlValueKind.Integer
                    ? checked((int)arguments[0].AsInteger() - 1)
                    : 0;
                _end = table.LastConstraints.Count > 0
                    && table.LastConstraints[0].Operator == ManagedVirtualTableConstraintOperator.Equal
                    ? _position + 1
                    : 2;
                return true;
            }

            public override void Next() => _position++;

            public override bool Eof => _position >= _end;

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
