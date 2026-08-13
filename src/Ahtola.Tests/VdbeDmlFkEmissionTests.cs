using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Compilation;
using Ahtola.Core.Execution;

namespace Ahtola.Tests;

/// <summary>
/// P4-C: compiled DML Insert flags + FkCheck epilogue, and connection-scoped deferred FK
/// counters shared across ResumableStatement instances (inventory: vdbe-fk-enforcement-opcodes).
/// </summary>
public sealed class VdbeDmlFkEmissionTests
{
    [Test]
    public void CompiledUpdateEmitsRequireSeekOnMutation()
    {
        var writeTarget = new VdbeWriteTarget
        {
            TableName = "t",
            RowCount = 1,
            GetRow = _ => [SqlValue.Integer(1)],
            GetRowId = _ => 1,
            MutateRow = _ => new VdbeRowMutation([SqlValue.Integer(2)], 1),
            Commit = () => null,
        };

        var compiled = DmlStatementCompiler.Compile(
            DmlKind.Update,
            "t",
            columnCount: 1,
            predicate: null,
            returning: Array.Empty<DmlReturningExpression>(),
            writeTarget);

        compiled.Program.Instructions.OfType<UpdateInstruction>().Should().ContainSingle()
            .Which.Flags.Should().HaveFlag(VdbeInsertFlags.RequireSeek);
    }

    [Test]
    public void CompiledInsertWithForeignKeyChecksEmitsFkCheckEpilogue()
    {
        var writeTarget = new VdbeWriteTarget
        {
            TableName = "child",
            RowCount = 1,
            MutateRow = _ => new VdbeRowMutation([SqlValue.Integer(1)], 1),
            Commit = () => 1L,
        };

        var compiled = DmlStatementCompiler.Compile(
            DmlKind.Insert,
            "child",
            columnCount: 1,
            predicate: null,
            returning: Array.Empty<DmlReturningExpression>(),
            writeTarget,
            new DmlCompileOptions(EmitForeignKeyChecks: true));

        var opcodes = compiled.Program.Instructions.Select(i => i.Opcode).ToArray();
        opcodes.Should().Contain(VdbeOpcode.FkCheck);

        var commitIndex = Array.IndexOf(opcodes, VdbeOpcode.Commit);
        var fkChecks = compiled.Program.Instructions
            .Select((ins, index) => (ins, index))
            .Where(pair => pair.ins is FkCheckInstruction)
            .Select(pair => pair.index)
            .ToArray();
        fkChecks.Should().HaveCount(2);
        fkChecks.Should().OnlyContain(index => index > commitIndex);

        var checks = compiled.Program.Instructions.OfType<FkCheckInstruction>().ToArray();
        checks.Select(c => c.Deferred).Should().Equal(false, true);
    }

    [Test]
    public void SharedTransactionAccumulatesDeferredFkAcrossStatements()
    {
        var shared = new VdbeTransactionContext();
        shared.Begin([]);

        var bump = new VdbeProgram(
            registerCount: 0,
            cursorCount: 0,
            [
                new FkCounterInstruction(Increment: 1, Deferred: true),
                new HaltInstruction(),
            ]);

        using (var first = new ResumableStatement(bump, sharedTransaction: shared))
        {
            first.StepResumable().Should().Be(ResumableStatementStepResult.Done);
        }

        shared.InTransaction.Should().BeTrue();
        shared.DeferredForeignKeyViolations.Should().Be(1);

        var bumpAgain = new VdbeProgram(
            registerCount: 0,
            cursorCount: 0,
            [
                new FkCounterInstruction(Increment: 2, Deferred: true),
                new HaltInstruction(),
            ]);

        using (var second = new ResumableStatement(bumpAgain, sharedTransaction: shared))
        {
            second.StepResumable().Should().Be(ResumableStatementStepResult.Done);
        }

        shared.DeferredForeignKeyViolations.Should().Be(3);

        var error = Assert.Throws<EmbeddedSqlException>(() => shared.Commit());
        error!.SqliteErrorCode.Should().Be(SqliteResultCode.ConstraintForeignKey);
    }

    [Test]
    public void SharedTransactionResetDoesNotClearDeferredCounter()
    {
        var shared = new VdbeTransactionContext();
        shared.Begin([]);

        var program = new VdbeProgram(
            registerCount: 0,
            cursorCount: 0,
            [
                new FkCounterInstruction(Increment: 1, Deferred: true),
                new HaltInstruction(),
            ]);

        using var statement = new ResumableStatement(program, sharedTransaction: shared);
        statement.StepResumable().Should().Be(ResumableStatementStepResult.Done);
        statement.Reset();
        statement.StepResumable().Should().Be(ResumableStatementStepResult.Done);

        shared.DeferredForeignKeyViolations.Should().Be(2);
    }

    [Test]
        public void ExplainInsertChildShowsFkCheckWhenForeignKeysOn()
        {
            using var connection = new EmbeddedDatabase().Connect();
            Execute(connection, "PRAGMA foreign_keys=ON;");
            Execute(connection, "CREATE TABLE parent(id INTEGER PRIMARY KEY);");
            Execute(connection, "CREATE TABLE child(id INTEGER PRIMARY KEY, pid INTEGER REFERENCES parent(id));");
            Execute(connection, "INSERT INTO parent(id) VALUES (1);");

            var opcodes = Opcodes(connection, "EXPLAIN INSERT INTO child(id, pid) VALUES (10, 1);");
            opcodes.Should().Contain("FkCheck");
            opcodes.Should().Contain("Insert");
        }

        [Test]
        public void CompiledInsertWithForeignKeysOnStillRejectsOrphanChild()
        {
            using var connection = new EmbeddedDatabase().Connect();
            Execute(connection, "PRAGMA foreign_keys=ON;");
            Execute(connection, "CREATE TABLE parent(id INTEGER PRIMARY KEY);");
            Execute(connection, "CREATE TABLE child(id INTEGER PRIMARY KEY, pid INTEGER REFERENCES parent(id));");

            var error = Assert.Throws<EmbeddedSqlException>(() =>
                Execute(connection, "INSERT INTO child(id, pid) VALUES (1, 99);"));
            error!.Message.Should().Contain("FOREIGN KEY");
        }

        [Test]
        public void CompiledUpdateProgramRunsWithRequireSeek()
        {
            var mutated = false;
            var writeTarget = new VdbeWriteTarget
            {
                TableName = "t",
                RowCount = 1,
                GetRow = _ => [SqlValue.Integer(1)],
                GetRowId = _ => 1,
                MutateRow = _ =>
                {
                    mutated = true;
                    return new VdbeRowMutation([SqlValue.Integer(2)], 1);
                },
                Commit = () => null,
            };

            var compiled = DmlStatementCompiler.Compile(
                DmlKind.Update,
                "t",
                columnCount: 1,
                predicate: null,
                returning: Array.Empty<DmlReturningExpression>(),
                writeTarget);

            using var runtime = new ResumableStatement(
                compiled.Program,
                writeTargets: compiled.RuntimeWriteTargets);
            runtime.StepResumable().Should().Be(ResumableStatementStepResult.Done);
            mutated.Should().BeTrue();
            runtime.RowsAffected.Should().Be(1);
        }

        private static void Execute(EmbeddedConnection connection, string sql)
        {
            using var statement = connection.Prepare(sql);
            while (statement.Step() == StatementStepResult.Row)
            {
            }
        }

        private static List<SqlValue[]> ReadRows(EmbeddedConnection connection, string sql)
        {
            using var statement = connection.Prepare(sql);
            var rows = new List<SqlValue[]>();
            while (statement.Step() == StatementStepResult.Row)
            {
                var row = new SqlValue[statement.GetColumnCount()];
                for (var i = 0; i < row.Length; i++)
                    row[i] = statement.GetValue(i);
                rows.Add(row);
            }

            return rows;
        }

        private static List<string> Opcodes(EmbeddedConnection connection, string explainSql)
            => ReadRows(connection, explainSql).Select(row => row[1].AsText()!).ToList();
    }
