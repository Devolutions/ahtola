using System.Buffers.Binary;
using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Execution;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

[NonParallelizable]
public sealed class ManagedVirtualTableTests
{
    private const string ModuleName = "managed_virtual_table_test";
    private static readonly TestModule Module = RegisterModule();

    [SetUp]
    public void ResetModule() => Module.Reset();

    [Test]
    public void BestIndexPlanRejectsInvalidConstraintUsage()
    {
        var usable = new ManagedVirtualTableConstraint(
            0,
            ManagedVirtualTableConstraintOperator.Equal);
        var unusable = usable with { Usable = false };

        Action negative = () => new ManagedVirtualTablePlan(
            [new(-1)]).ValidateFor([usable]);
        Action duplicate = () => new ManagedVirtualTablePlan(
            [new(1), new(1)]).ValidateFor([usable, usable]);
        Action gap = () => new ManagedVirtualTablePlan(
            [new(1), new(3), new(0)]).ValidateFor([usable, usable, usable]);
        Action outOfRange = () => new ManagedVirtualTablePlan(
            [new(2)]).ValidateFor([usable]);
        Action consumeUnusable = () => new ManagedVirtualTablePlan(
            [new(1)]).ValidateFor([unusable]);
        Action omitUnusable = () => new ManagedVirtualTablePlan(
            [new(0, Omit: true)]).ValidateFor([unusable]);
        Action omitNullWithoutArgument = () => new ManagedVirtualTablePlan(
            [new(0, Omit: true)]).ValidateFor(
            [new ManagedVirtualTableConstraint(0, ManagedVirtualTableConstraintOperator.IsNull)]);

        negative.Should().Throw<InvalidOperationException>();
        duplicate.Should().Throw<InvalidOperationException>();
        gap.Should().Throw<InvalidOperationException>();
        outOfRange.Should().Throw<InvalidOperationException>();
        consumeUnusable.Should().Throw<InvalidOperationException>();
        omitUnusable.Should().Throw<InvalidOperationException>();
        omitNullWithoutArgument.Should().NotThrow();
    }

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
        Module.LastCreated.DestroyCalls.Should().Be(1);
        Module.LastCreated.DisconnectCalls.Should().Be(0);
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
    public void ResumableVirtualCursorCancellationFaultsAndDisposesImmediately()
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
            ]);
        using var statement = new ResumableStatement(
            program,
            virtualTableBindings: [new VdbeVirtualTableBinding(table)]);
        statement.StepResumable().Should().Be(ResumableStatementStepResult.Row);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Action step = () => statement.StepResumable(cancellation.Token);

        step.Should().Throw<OperationCanceledException>();
        statement.State.Should().Be(ResumableStatementState.Faulted);
        table.CursorDisposed.Should().BeTrue();
    }

    [Test]
    public void ProductionVirtualScanStreamsThroughVdbeAndDisposesOnResetAndCancellation()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, $"CREATE VIRTUAL TABLE entries USING {ModuleName};");

        ReadRows(connection, "EXPLAIN SELECT value FROM entries;")
            .Select(static row => row[1].AsText())
            .Should().ContainInOrder("VOpen", "VFilter", "VColumn", "VNext");

        using var statement = connection.Prepare("SELECT value FROM entries;");
        statement.Step().Should().Be(StatementStepResult.Row);
        var firstCursor = Module.LastCreated!.LastCursorInstance;
        firstCursor.Should().NotBeNull();
        firstCursor!.Disposed.Should().BeFalse();
        Module.LastCreated.ColumnCalls.Should().Be(2);
        Module.LastCreated.NextCalls.Should().Be(0);

        statement.Reset();
        firstCursor.Disposed.Should().BeTrue();

        statement.Step().Should().Be(StatementStepResult.Row);
        var secondCursor = Module.LastCreated.LastCursorInstance;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Action cancelledStep = () => statement.Step(cancellation.Token);
        cancelledStep.Should().Throw<OperationCanceledException>();
        secondCursor!.Disposed.Should().BeTrue();
    }

    [Test]
    public void VirtualScanDisposesItsCursorWhenAReadCallbackFails()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, $"CREATE VIRTUAL TABLE entries USING {ModuleName};");
        Module.ThrowOnColumn = true;

        using var statement = connection.Prepare("SELECT value FROM entries;");
        Action step = () => statement.Step();

        step.Should().Throw<InvalidOperationException>().WithMessage("virtual column failed");
        Module.LastCreated!.LastCursorInstance!.Disposed.Should().BeTrue();
    }

    [Test]
    public void LifecycleStatementsExecuteAndExplainDedicatedOpcodes()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        ReadRows(connection, $"EXPLAIN CREATE VIRTUAL TABLE entries USING {ModuleName};")
            .Select(static row => row[1].AsText())
            .Should().Contain("VCreate");
        Execute(connection, $"CREATE VIRTUAL TABLE entries USING {ModuleName};");
        ReadRows(connection, "EXPLAIN INSERT OR REPLACE INTO entries(value) VALUES (3);")
            .Select(static row => row[1].AsText())
            .Should().ContainInOrder("VOpen", "VUpdate");
        ReadRows(connection, "EXPLAIN ALTER TABLE entries RENAME TO renamed_entries;")
            .Select(static row => row[1].AsText())
            .Should().Contain("VRename");
        Execute(connection, "ALTER TABLE entries RENAME TO renamed_entries;");
        ReadRows(connection, "EXPLAIN DROP TABLE renamed_entries;")
            .Select(static row => row[1].AsText())
            .Should().Contain("VDestroy");
        Execute(connection, "DROP TABLE renamed_entries;");
    }

    [Test]
    public void VirtualTablePlannerReceivesNullAndLimitConstraints()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, $"CREATE VIRTUAL TABLE entries USING {ModuleName};");

        _ = ReadRows(
            connection,
            "EXPLAIN QUERY PLAN SELECT value FROM entries WHERE value IS NOT NULL AND value != 9 LIMIT 2 OFFSET 1;");

        Module.LastCreated!.LastConstraints.Select(static item => item.Operator).Should().ContainInOrder(
            ManagedVirtualTableConstraintOperator.IsNotNull,
            ManagedVirtualTableConstraintOperator.NotEqual,
            ManagedVirtualTableConstraintOperator.Limit,
            ManagedVirtualTableConstraintOperator.Offset);
    }

    [Test]
    public void VirtualTableAuthorizerUsesDedicatedCreateAndDropActions()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        var seen = new List<SqliteAuthorizerContext>();
        connection.Hooks.Authorizer = context =>
        {
            seen.Add(context);
            return context.Action is SqliteAuthorizerAction.CreateVTable or SqliteAuthorizerAction.DropVTable
                ? SqliteAuthorizerResult.Ignore
                : SqliteAuthorizerResult.Ok;
        };

        Execute(connection, $"CREATE VIRTUAL TABLE entries USING {ModuleName};");
        Execute(connection, "DROP TABLE entries;");
        Execute(connection, $"CREATE VIRTUAL TABLE temp.temp_entries USING {ModuleName};");
        ReadRows(connection, "SELECT value FROM temp.temp_entries;").Should().HaveCount(2);
        Execute(connection, "DROP TABLE temp.temp_entries;");

        seen.Should().Contain(context =>
            context.Action == SqliteAuthorizerAction.CreateVTable
            && context.Argument0 == "entries"
            && context.Argument1 == ModuleName
            && context.Database == "main");
        seen.Should().Contain(context =>
            context.Action == SqliteAuthorizerAction.DropVTable
            && context.Argument0 == "entries"
            && context.Database == "main");
        seen.Should().Contain(context =>
            context.Action == SqliteAuthorizerAction.CreateVTable
            && context.Argument0 == "temp_entries"
            && context.Database == "temp");

        connection.Hooks.Authorizer = context =>
            context.Action == SqliteAuthorizerAction.CreateVTable
                ? SqliteAuthorizerResult.Deny
                : SqliteAuthorizerResult.Ok;
        Action denied = () => connection.Prepare($"CREATE VIRTUAL TABLE denied USING {ModuleName};");
        denied.Should().Throw<EmbeddedAuthorizationDeniedException>();
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
    public void ConsumedVirtualOrderingSuppressesTheEngineSorter()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, $"CREATE VIRTUAL TABLE entries USING {ModuleName};");
        Module.ConsumeOrderBy = true;

        ReadRows(connection, "SELECT value FROM entries ORDER BY value DESC;")
            .Select(static row => row.Single())
            .Should().Equal(SqlValue.Integer(2), SqlValue.Integer(1));
        ReadRows(connection, "EXPLAIN SELECT value FROM entries ORDER BY value DESC;")
            .Select(static row => row[1].AsText())
            .Should().ContainInOrder("VOpen", "VFilter", "VColumn", "VNext")
            .And.NotContain("SorterSort");
        ReadRows(connection, "EXPLAIN QUERY PLAN SELECT value FROM entries ORDER BY value DESC;")
            .Single()[3].AsText().Should().Contain("rows~2 cost~2 order=consumed");
    }

    [Test]
    public void CorrelatedVirtualConstraintBecomesUsableForEachOuterRow()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, $"CREATE VIRTUAL TABLE entries USING {ModuleName};");
        Execute(connection, "CREATE TABLE anchors(id INTEGER);");
        Execute(connection, "INSERT INTO anchors VALUES (2), (1);");

        ReadRows(
                connection,
                "SELECT anchors.id, entries.value FROM anchors JOIN entries ON entries.value = anchors.id ORDER BY anchors.id;")
            .Select(static row => $"{row[0].AsInteger()}:{row[1].AsInteger()}")
            .Should().Equal("1:1", "2:2");

        Module.LastCreated!.ConstraintHistory.Should().Contain(
            constraints => constraints.Any(static constraint => !constraint.Usable));
        Module.LastCreated.ConstraintHistory.Should().Contain(
            constraints => constraints.Any(static constraint => constraint.Usable));

        Module.LastCreated.ConstraintHistory.Clear();
        ReadRows(
                connection,
                "SELECT anchors.id, entries.value FROM entries JOIN anchors ON entries.value = anchors.id ORDER BY anchors.id;")
            .Select(static row => $"{row[0].AsInteger()}:{row[1].AsInteger()}")
            .Should().Equal("1:1", "2:2");
        Module.LastCreated.ConstraintHistory.Should().Contain(
            constraints => constraints.Any(static constraint => constraint.Usable));
    }

    [Test]
    public void JoinPushdownPreservesVirtualMatchConstraintsOrderAndOmittedResiduals()
    {
        _ = Module;
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, $"CREATE VIRTUAL TABLE entries USING {ModuleName};");
        Execute(connection, "CREATE TABLE anchors(id INTEGER);");
        Execute(connection, "INSERT INTO anchors VALUES (1);");

        ReadRows(
                connection,
                "SELECT entries.value FROM entries JOIN anchors ON 1 = 1 WHERE entries.query MATCH 'needle' ORDER BY entries.value DESC;")
            .Select(static row => row.Single())
            .Should().Equal(SqlValue.Integer(2), SqlValue.Integer(1));

        Module.LastCreated!.LastConstraints.Should().Equal(
            new ManagedVirtualTableConstraint(1, ManagedVirtualTableConstraintOperator.Match));
        Module.LastCreated.LastOrderBy.Should().Equal(new ManagedVirtualTableOrderBy(0, Descending: true));
        Module.LastCreated.FilterArguments.Should().Equal(SqlValue.Text("needle"));
    }

    [Test]
    public void VirtualTableRowIdAliasesResolveInSelectAndWhere()
    {
        _ = Module;
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, $"CREATE VIRTUAL TABLE entries USING {ModuleName};");

        ReadRows(
                connection,
                "SELECT entries.rowid, entries._rowid_, entries.oid FROM entries WHERE entries._rowid_ = 2;")
            .Should().ContainSingle()
            .Which.Should().Equal(SqlValue.Integer(2), SqlValue.Integer(2), SqlValue.Integer(2));
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

        Module.BeginCalls.Should().Be(3);
        Module.SyncCalls.Should().Be(3);
        Module.CommitCalls.Should().Be(3);
        Module.RollbackCalls.Should().Be(0);
        Module.Updates.Should().HaveCount(3);
        Module.Updates[0].Should().Equal(SqlValue.Null, SqlValue.Null, SqlValue.Integer(7), SqlValue.Null);
        Module.Updates[1].Should().Equal(
            SqlValue.Integer(2), SqlValue.Integer(2), SqlValue.Integer(8), SqlValue.Text("hidden"));
        Module.Updates[2].Should().Equal(
            SqlValue.Integer(1), SqlValue.Null, SqlValue.Integer(1), SqlValue.Text("hidden"));
    }

    [Test]
    public void VirtualTableDmlRollsBackWhenTheModuleRejectsAMutation()
    {
        _ = Module;
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, $"CREATE VIRTUAL TABLE entries USING {ModuleName};");
        Module.ThrowOnUpdate = true;

        Action insert = () => Execute(connection, "INSERT INTO entries(value) VALUES (7);");

        insert.Should().Throw<EmbeddedSqlException>().WithMessage("virtual update failed");
        Module.BeginCalls.Should().Be(1);
        Module.SyncCalls.Should().Be(0);
        Module.CommitCalls.Should().Be(0);
        Module.RollbackCalls.Should().Be(1);
        ReadRows(connection, "SELECT value FROM entries;")
            .Select(static row => row.Single())
            .Should().Equal(SqlValue.Integer(1), SqlValue.Integer(2));
    }

    [Test]
    public void VirtualTableDmlParticipatesInExplicitTransactionsAndSavepoints()
    {
        _ = Module;
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, $"CREATE VIRTUAL TABLE entries USING {ModuleName};");

        Execute(connection, "BEGIN;");
        Execute(connection, "INSERT INTO entries(value) VALUES (7);");
        var transactionTable = Module.LastCreated!;
        transactionTable.Updates.Should().ContainSingle();
        Execute(connection, "ROLLBACK;");

        Execute(connection, "SAVEPOINT virtual_table_write;");
        Execute(connection, "INSERT INTO entries(value) VALUES (8);");
        Module.LastCreated!.Updates.Should().ContainSingle();
        Execute(connection, "ROLLBACK TO virtual_table_write;");
        Execute(connection, "RELEASE virtual_table_write;");

        ReadRows(connection, "SELECT value FROM entries;").Should().HaveCount(2);
    }

    [Test]
    public void ExplicitTransactionHooksRunOnceAtTheRealBoundary()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, $"CREATE VIRTUAL TABLE entries USING {ModuleName};");

        Execute(connection, "BEGIN;");
        Execute(connection, "INSERT INTO entries(value) VALUES (7);");
        Execute(connection, "SAVEPOINT nested;");
        Execute(connection, "INSERT INTO entries(value) VALUES (8);");
        Execute(connection, "ROLLBACK TO nested;");
        Execute(connection, "RELEASE nested;");
        Execute(connection, "INSERT INTO entries(value) VALUES (9);");
        Execute(connection, "COMMIT;");

        Module.BeginCalls.Should().Be(1);
        Module.SyncCalls.Should().Be(1);
        Module.CommitCalls.Should().Be(1);
        Module.RollbackCalls.Should().Be(0);
    }

    [Test]
    public void ExplicitTransactionRollbackRunsOnceWithoutSyncOrCommit()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, $"CREATE VIRTUAL TABLE entries USING {ModuleName};");

        Execute(connection, "BEGIN;");
        Execute(connection, "INSERT INTO entries(value) VALUES (7);");
        Execute(connection, "INSERT INTO entries(value) VALUES (8);");
        Execute(connection, "ROLLBACK;");

        Module.BeginCalls.Should().Be(1);
        Module.SyncCalls.Should().Be(0);
        Module.CommitCalls.Should().Be(0);
        Module.RollbackCalls.Should().Be(1);
        ReadRows(connection, "SELECT value FROM entries;").Should().HaveCount(2);
    }

    [Test]
    public void VirtualTableSchemaChangesRollBackWithTheirTransaction()
    {
        _ = Module;
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, $"CREATE VIRTUAL TABLE entries USING {ModuleName};");

        Execute(connection, "BEGIN;");
        Execute(connection, $"CREATE VIRTUAL TABLE other_entries USING {ModuleName};");
        Execute(connection, "ROLLBACK;");
        Action selectCreated = () => ReadRows(connection, "SELECT * FROM other_entries;");
        selectCreated.Should().Throw<EmbeddedSqlException>().WithMessage("*no such table*");

        Execute(connection, "BEGIN;");
        Execute(connection, "ALTER TABLE entries RENAME TO renamed_entries;");
        Execute(connection, "ROLLBACK;");
        ReadRows(connection, "SELECT value FROM entries;").Should().HaveCount(2);

        Execute(connection, "BEGIN;");
        Execute(connection, "DROP TABLE entries;");
        Execute(connection, "ROLLBACK;");
        ReadRows(connection, "SELECT value FROM entries;").Should().HaveCount(2);
    }

    [Test]
    public void EscapedLikeConstraintRemainsAnEngineResidual()
    {
        _ = Module;
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, $"CREATE VIRTUAL TABLE entries USING {ModuleName};");

        ReadRows(connection, "SELECT value FROM entries WHERE value LIKE '1' ESCAPE '#';")
            .Select(static row => row.Single())
            .Should().Equal(SqlValue.Integer(1));

        Module.LastCreated!.LastConstraints.Should().BeEmpty();
    }

    [Test]
    public void VirtualTableInsertPropagatesTheModuleAssignedRowId()
    {
        _ = Module;
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, $"CREATE VIRTUAL TABLE entries USING {ModuleName};");
        Module.InsertedRowId = 42;

        Execute(connection, "INSERT INTO entries(value) VALUES (7);");

        ReadRows(connection, "SELECT last_insert_rowid();").Single().Single().Should().Be(SqlValue.Integer(42));
    }

    [Test]
    public void VirtualTableVUpdateReceivesTheStatementConflictMode()
    {
        _ = Module;
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, $"CREATE VIRTUAL TABLE entries USING {ModuleName};");

        Execute(connection, "INSERT OR REPLACE INTO entries(value) VALUES (7);");

        Module.LastCreated!.LastConflictMode.Should().Be(ManagedVirtualTableConflictMode.Replace);
    }

    [Test]
    public void VirtualTableDmlUsesStandardRowProductionAndSelectionShapes()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, $"CREATE VIRTUAL TABLE entries USING {ModuleName};");

        ReadRows(
                connection,
                "INSERT OR IGNORE INTO entries(value) "
                + "SELECT value FROM generate_series(3,4) RETURNING value;")
            .Select(static row => row.Single())
            .Should().Equal(SqlValue.Integer(3), SqlValue.Integer(4));
        Module.LastCreated!.LastConflictMode.Should().Be(ManagedVirtualTableConflictMode.Ignore);

        Execute(connection, "INSERT INTO entries DEFAULT VALUES;");
        Execute(connection, "CREATE TABLE replacements(old_value INTEGER, new_value INTEGER);");
        Execute(connection, "INSERT INTO replacements VALUES (2, 20);");
        Execute(
            connection,
            "UPDATE entries SET value = replacements.new_value FROM replacements "
            + "WHERE entries.value = replacements.old_value ORDER BY entries.value DESC LIMIT 1;");
        Execute(connection, "DELETE FROM entries ORDER BY value DESC LIMIT 1;");

        ReadRows(connection, "SELECT value FROM entries ORDER BY value;")
            .Select(static row => row.Single())
            .Should().Equal(SqlValue.Integer(0), SqlValue.Integer(1), SqlValue.Integer(3), SqlValue.Integer(4));
    }

    [Test]
    public void DisposingDatabaseDisconnectsRemainingVirtualTableInstancesExactlyOnce()
    {
        _ = Module;
        var database = new EmbeddedDatabase();
        var connection = database.Connect();
        Execute(connection, $"CREATE VIRTUAL TABLE entries USING {ModuleName};");
        var table = Module.LastCreated!;

        database.Dispose();
        database.Dispose();

        table.DisconnectCalls.Should().Be(1);
        table.DestroyCalls.Should().Be(0);
        connection.Dispose();
    }

    [Test]
    public void DropRemainsAtomicWhenDestroyThrows()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, $"CREATE VIRTUAL TABLE entries USING {ModuleName};");
        Module.ThrowOnDestroy = true;

        Action drop = () => Execute(connection, "DROP TABLE entries;");

        drop.Should().Throw<InvalidOperationException>().WithMessage("virtual destroy failed");
        ReadRows(connection, "SELECT value FROM entries;").Should().HaveCount(2);

        Module.ThrowOnDestroy = false;
        Execute(connection, "DROP TABLE entries;");
        Action selectDropped = () => ReadRows(connection, "SELECT value FROM entries;");
        selectDropped.Should().Throw<EmbeddedSqlException>().WithMessage("*no such table*");
    }

    [Test]
    public void CreateAndRenameCallbackFailuresLeaveTheCatalogAtomic()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Module.ThrowOnPersistence = true;

        Action create = () => Execute(connection, $"CREATE VIRTUAL TABLE failed_entries USING {ModuleName};");
        create.Should().Throw<InvalidOperationException>().WithMessage("virtual persistence failed");
        Module.LastCreated!.DisconnectCalls.Should().Be(1);

        Module.ThrowOnPersistence = false;
        Action selectFailedCreate = () => ReadRows(connection, "SELECT * FROM failed_entries;");
        selectFailedCreate.Should().Throw<EmbeddedSqlException>().WithMessage("*no such table*");

        Execute(connection, $"CREATE VIRTUAL TABLE entries USING {ModuleName};");
        Module.ThrowOnRename = true;
        Action rename = () => Execute(connection, "ALTER TABLE entries RENAME TO renamed_entries;");
        rename.Should().Throw<InvalidOperationException>().WithMessage("virtual rename failed");

        Module.ThrowOnRename = false;
        ReadRows(connection, "SELECT value FROM entries;").Should().HaveCount(2);
        Action selectRenamed = () => ReadRows(connection, "SELECT * FROM renamed_entries;");
        selectRenamed.Should().Throw<EmbeddedSqlException>().WithMessage("*no such table*");
    }

    [Test]
    public void ReplacedAndAbandonedCatalogInstancesDisconnectExactlyOnce()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, $"CREATE VIRTUAL TABLE entries USING {ModuleName};");
        Execute(connection, "BEGIN;");
        Execute(connection, "INSERT INTO entries(value) VALUES (7);");
        Execute(connection, "SAVEPOINT nested;");
        Execute(connection, "INSERT INTO entries(value) VALUES (8);");
        Execute(connection, "ROLLBACK TO nested;");
        Execute(connection, "COMMIT;");

        database.Dispose();

        Module.Instances.Should().OnlyContain(
            table => table.DisconnectCalls + table.DestroyCalls == 1);
    }

    [Test]
    public void FileCatalogReloadDisconnectsTheReplacedInstance()
    {
        var fileSystem = new InMemoryFileSystem();
        using var writerDatabase = EmbeddedDatabase.OpenFile("managed-vtab.db", fileSystem);
        using var readerDatabase = EmbeddedDatabase.OpenFile("managed-vtab.db", fileSystem);
        using var writer = writerDatabase.Connect();
        using var reader = readerDatabase.Connect();
        Execute(writer, $"CREATE VIRTUAL TABLE entries USING {ModuleName};");
        ReadRows(reader, "SELECT value FROM entries;").Should().HaveCount(2);
        var replacedReaderInstance = Module.LastCreated!;

        Execute(writer, "INSERT INTO entries(value) VALUES (7);");
        ReadRows(reader, "SELECT value FROM entries;").Should().HaveCount(3);

        replacedReaderInstance.DisconnectCalls.Should().Be(1);
        replacedReaderInstance.DestroyCalls.Should().Be(0);
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
        public List<TestTable> Instances { get; } = [];
        public int BeginCalls { get; private set; }
        public int SyncCalls { get; private set; }
        public int CommitCalls { get; private set; }
        public int RollbackCalls { get; private set; }
        public List<IReadOnlyList<SqlValue>> Updates { get; } = [];
        public bool ThrowOnUpdate { get; set; }
        public bool ThrowOnDestroy { get; set; }
        public bool ThrowOnRename { get; set; }
        public bool ThrowOnPersistence { get; set; }
        public bool ThrowOnColumn { get; set; }
        public long? InsertedRowId { get; set; }
        public bool ConsumeOrderBy { get; set; }

        public override ManagedVirtualTable Create(ManagedVirtualTableCreateContext context)
            => Track(new TestTable(this, [1, 2]));

        public override ManagedVirtualTable Create(
            ManagedVirtualTableCreateContext context,
            ManagedVirtualTablePersistencePayload payload)
        {
            if (payload.Version != 1 || payload.Bytes.Length % sizeof(long) != 0)
                throw new EmbeddedSqlException("invalid test virtual-table persistence payload");
            var values = new long[payload.Bytes.Length / sizeof(long)];
            for (var index = 0; index < values.Length; index++)
            {
                values[index] = BinaryPrimitives.ReadInt64LittleEndian(
                    payload.Bytes.Span.Slice(index * sizeof(long), sizeof(long)));
            }
            return Track(new TestTable(this, values));
        }

        public void Reset()
        {
            LastCreated = null;
            Instances.Clear();
            BeginCalls = 0;
            SyncCalls = 0;
            CommitCalls = 0;
            RollbackCalls = 0;
            Updates.Clear();
            ThrowOnUpdate = false;
            ThrowOnDestroy = false;
            ThrowOnRename = false;
            ThrowOnPersistence = false;
            ThrowOnColumn = false;
            InsertedRowId = null;
            ConsumeOrderBy = false;
        }

        public void RecordBegin() => BeginCalls++;
        public void RecordSync() => SyncCalls++;
        public void RecordCommit() => CommitCalls++;
        public void RecordRollback() => RollbackCalls++;
        public void RecordUpdate(IReadOnlyList<SqlValue> arguments) => Updates.Add(arguments.ToArray());

        private TestTable Track(TestTable table)
        {
            Instances.Add(table);
            return LastCreated = table;
        }
    }

    private sealed class TestTable : ManagedVirtualTable
    {
        private readonly TestModule? _module;
        private readonly List<long> _values;
        private static readonly ManagedVirtualTableSchema TestSchema = new(
            [
                new ManagedVirtualTableColumn("value", ManagedVirtualTableAffinity.Integer),
                new ManagedVirtualTableColumn("query", ManagedVirtualTableAffinity.Text, IsHidden: true),
            ]);

        public int BestIndexCalls { get; private set; }
        public int FilterCalls { get; private set; }
        public int ColumnCalls { get; private set; }
        public int NextCalls { get; private set; }
        public int DisconnectCalls { get; private set; }
        public int DestroyCalls { get; private set; }
        public TestCursor? LastCursorInstance { get; private set; }
        public bool CursorDisposed => LastCursorInstance?.Disposed == true;
        public ManagedVirtualTablePlan Plan { get; } = new([]);
        public IReadOnlyList<ManagedVirtualTableConstraint> LastConstraints { get; private set; } = [];
        public List<IReadOnlyList<ManagedVirtualTableConstraint>> ConstraintHistory { get; } = [];
        public IReadOnlyList<ManagedVirtualTableOrderBy> LastOrderBy { get; private set; } = [];
        public IReadOnlyList<SqlValue> FilterArguments { get; private set; } = [];
        public List<IReadOnlyList<SqlValue>> Updates { get; } = [];
        public int BeginCalls { get; private set; }
        public int SyncCalls { get; private set; }
        public int CommitCalls { get; private set; }
        public int RollbackCalls { get; private set; }
        public long? InsertedRowId { get; set; }
        public ManagedVirtualTableConflictMode LastConflictMode { get; private set; }

        public TestTable(TestModule? module = null, IEnumerable<long>? values = null)
        {
            _module = module;
            _values = values?.ToList() ?? [1, 2];
        }

        public override ManagedVirtualTableSchema Schema => TestSchema;

        public override ManagedVirtualTablePersistencePayload GetPersistencePayload()
        {
            if (_module?.ThrowOnPersistence == true)
                throw new InvalidOperationException("virtual persistence failed");
            var bytes = new byte[_values.Count * sizeof(long)];
            for (var index = 0; index < _values.Count; index++)
            {
                BinaryPrimitives.WriteInt64LittleEndian(
                    bytes.AsSpan(index * sizeof(long), sizeof(long)),
                    _values[index]);
            }
            return new ManagedVirtualTablePersistencePayload(1, bytes);
        }

        public override ManagedVirtualTablePlan BestIndex(
            IReadOnlyList<ManagedVirtualTableConstraint> constraints,
            IReadOnlyList<ManagedVirtualTableOrderBy> orderBy)
        {
            BestIndexCalls++;
            LastConstraints = constraints.ToArray();
            ConstraintHistory.Add(LastConstraints);
            LastOrderBy = orderBy.ToArray();
            var nextArgument = 0;
            var usages = constraints.Select(constraint =>
            {
                if (!constraint.Usable
                    || constraint.Operator is ManagedVirtualTableConstraintOperator.Limit
                        or ManagedVirtualTableConstraintOperator.Offset)
                {
                    return ManagedVirtualTableConstraintUsage.Unused;
                }

                return new ManagedVirtualTableConstraintUsage(
                    ++nextArgument,
                    Omit: constraint.Operator is ManagedVirtualTableConstraintOperator.Equal
                        or ManagedVirtualTableConstraintOperator.Match);
            }).ToArray();
            var consumeOrder = _module?.ConsumeOrderBy == true && orderBy.Count != 0;
            return new ManagedVirtualTablePlan(
                usages,
                indexNumber: consumeOrder ? 2 : 0,
                indexString: consumeOrder ? "ordered" : null,
                orderByConsumed: consumeOrder,
                estimatedCost: constraints.Any(static constraint => constraint.Usable) ? 1 : 2,
                estimatedRows: constraints.Any(static constraint => constraint.Usable) ? 1 : 2);
        }

        public override ManagedVirtualTableCursor Open() => LastCursorInstance = new TestCursor(this);

        public override long? Update(IReadOnlyList<SqlValue> arguments)
        {
            Updates.Add(arguments.ToArray());
            _module?.RecordUpdate(arguments);
            var oldRowId = arguments[0].Kind == SqlValueKind.Integer
                ? checked((int)arguments[0].AsInteger())
                : 0;
            if (oldRowId == 0)
                _values.Add(arguments[2].Kind == SqlValueKind.Null ? 0 : arguments[2].AsInteger());
            else if (arguments[1].Kind == SqlValueKind.Null)
                _values.RemoveAt(oldRowId - 1);
            else
                _values[oldRowId - 1] = arguments[2].AsInteger();

            if (_module?.ThrowOnUpdate == true)
                throw new EmbeddedSqlException("virtual update failed");
            if (arguments[0].Kind == SqlValueKind.Null
                && (_module?.InsertedRowId ?? InsertedRowId) is { } insertedRowId)
                return insertedRowId;
            return arguments[1].Kind == SqlValueKind.Integer ? arguments[1].AsInteger() : null;
        }

        public override ManagedVirtualTableUpdateResult Update(
            IReadOnlyList<SqlValue> arguments,
            ManagedVirtualTableConflictMode conflictMode)
        {
            LastConflictMode = conflictMode;
            return new ManagedVirtualTableUpdateResult(Update(arguments));
        }

        public override void Begin()
        {
            BeginCalls++;
            _module?.RecordBegin();
        }

        public override void Sync()
        {
            SyncCalls++;
            _module?.RecordSync();
        }

        public override void Commit()
        {
            CommitCalls++;
            _module?.RecordCommit();
        }

        public override void Rollback()
        {
            RollbackCalls++;
            _module?.RecordRollback();
        }

        public override void Disconnect() => DisconnectCalls++;

        public override void Rename(string newName)
        {
            if (_module?.ThrowOnRename == true)
                throw new InvalidOperationException("virtual rename failed");
        }

        public override void Destroy()
        {
            DestroyCalls++;
            if (_module?.ThrowOnDestroy == true)
                throw new InvalidOperationException("virtual destroy failed");
        }

        public sealed class TestCursor(TestTable table) : ManagedVirtualTableCursor
        {
            private int _position;
            private int _end;
            private int _direction = 1;

            public bool Disposed { get; private set; }

            public override bool Filter(ManagedVirtualTablePlan plan, IReadOnlyList<SqlValue> arguments)
            {
                table.FilterCalls++;
                table.FilterArguments = arguments.ToArray();
                _direction = plan.OrderByConsumed
                    && table.LastOrderBy is [{ Descending: true }]
                        ? -1
                        : 1;
                _position = _direction < 0 ? table._values.Count - 1 : 0;
                _end = _direction < 0 ? -1 : table._values.Count;
                var usedConstraintIndex = plan.ConstraintUsages
                    .Select((usage, index) => (usage, index))
                    .FirstOrDefault(static item => item.usage.ArgumentIndex == 1)
                    .index;
                if (arguments.Count > 0
                    && arguments[0].Kind == SqlValueKind.Integer
                    && usedConstraintIndex < table.LastConstraints.Count)
                {
                    var requested = checked((int)arguments[0].AsInteger() - 1);
                    var operation = table.LastConstraints[usedConstraintIndex].Operator;
                    if (operation is ManagedVirtualTableConstraintOperator.Equal)
                    {
                        _position = requested;
                        _end = _direction < 0 ? requested - 1 : requested + 1;
                    }
                    else if (operation is ManagedVirtualTableConstraintOperator.GreaterThanOrEqual)
                    {
                        _position = requested;
                    }
                }
                return true;
            }

            public override void Next()
            {
                table.NextCalls++;
                _position += _direction;
            }

            public override bool Eof => _direction > 0 ? _position >= _end : _position <= _end;

            public override SqlValue Column(int columnIndex)
            {
                table.ColumnCalls++;
                return table._module?.ThrowOnColumn == true
                    ? throw new InvalidOperationException("virtual column failed")
                    : columnIndex switch
                    {
                        0 => SqlValue.Integer(table._values[_position]),
                        1 => SqlValue.Text("hidden"),
                        _ => throw new ArgumentOutOfRangeException(nameof(columnIndex)),
                    };
            }

            public override long RowId => _position + 1;

            public override void Dispose() => Disposed = true;
        }
    }
}
