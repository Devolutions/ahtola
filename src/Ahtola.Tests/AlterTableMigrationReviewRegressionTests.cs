using System.Globalization;
using Ahtola.Core;
using Ahtola.Core.Execution;
using Ahtola.Core.Storage;
using AwesomeAssertions;

namespace Ahtola.Tests;

/// <summary>
/// Regressions for the three <c>ALTER TABLE</c> migration defects found reviewing the compiled DDL port:
/// index rows paired by position rather than by identity, rename collisions that only the runtime noticed,
/// and typed schema opcodes that re-derived a replacement table the plan had already built.
/// </summary>
public sealed class AlterTableMigrationReviewRegressionTests
{
    private const string SchemaQuery =
        "SELECT type || '|' || name || '|' || tbl_name FROM sqlite_schema ORDER BY type, name;";

    // ------------------------------------------------- index rows pair by identity, not by position

    [Test]
    public void DroppingAUniqueConstraintKeepsAnExplicitIndexDeclaredAfterIt()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER UNIQUE, b TEXT);");
        Execute(connection, "CREATE INDEX t_b ON t(b);");
        Execute(connection, "INSERT INTO t VALUES (1, 'x');");
        Execute(connection, "INSERT INTO t VALUES (2, 'y');");
        ReadRows(connection, SchemaQuery)
            .Should()
            .Equal("index|sqlite_autoindex_t_1|t", "index|t_b|t", "table|t|t");

        Execute(connection, "ALTER TABLE t ALTER COLUMN a TO a INTEGER;");

        // Only the constraint index disappears. The explicit index keeps its own row, its own storage and
        // its own SQL; pairing the two index lists by position retired it instead.
        ReadRows(connection, SchemaQuery).Should().Equal("index|t_b|t", "table|t|t");
        ReadRows(connection, "SELECT sql FROM sqlite_schema WHERE name = 't_b';")
            .Should()
            .Equal("CREATE INDEX t_b ON t(b)");
        Execute(connection, "INSERT INTO t VALUES (1, 'z');");
        ReadRows(connection, "SELECT a FROM t WHERE b = 'x';").Should().Equal("1");
        ReadRows(connection, "SELECT b FROM t ORDER BY b;").Should().Equal("x", "y", "z");
    }

    [Test]
    public void DroppingAUniqueConstraintKeepsALaterMethodIndex()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE docs(id INTEGER PRIMARY KEY, title TEXT UNIQUE, body TEXT);");
        Execute(connection, "CREATE INDEX docs_fts ON docs USING fts (body);");
        Execute(connection, "INSERT INTO docs VALUES (1, 'a', 'alpha beta');");
        Execute(connection, "INSERT INTO docs VALUES (2, 'b', 'gamma delta');");

        Execute(connection, "ALTER TABLE docs ALTER COLUMN title TO title TEXT;");

        ReadRows(connection, SchemaQuery).Should().Equal("index|docs_fts|docs", "table|docs|docs");
        ReadRows(connection, "SELECT sql FROM sqlite_schema WHERE name = 'docs_fts';")
            .Should()
            .Equal("CREATE INDEX docs_fts ON docs USING fts (body)");
        // The method index still answers a match query, so its attachment survived with its row.
        ReadRows(connection, "SELECT id FROM docs WHERE fts_match(body, 'gamma');").Should().Equal("2");
        Execute(connection, "INSERT INTO docs VALUES (3, 'a', 'gamma epsilon');");
        ReadRows(connection, "SELECT id FROM docs WHERE fts_match(body, 'gamma') ORDER BY id;")
            .Should()
            .Equal("2", "3");
    }

    [Test]
    public void DroppingOneOfTwoUniqueConstraintsRenumbersOnlyTheSurvivingConstraintIndex()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER UNIQUE, b INTEGER UNIQUE, c TEXT);");
        Execute(connection, "CREATE INDEX t_c ON t(c);");
        Execute(connection, "INSERT INTO t VALUES (1, 10, 'x');");
        ReadRows(connection, SchemaQuery)
            .Should()
            .Equal(
                "index|sqlite_autoindex_t_1|t",
                "index|sqlite_autoindex_t_2|t",
                "index|t_c|t",
                "table|t|t");

        Execute(connection, "ALTER TABLE t ALTER COLUMN a TO a INTEGER;");

        // b's constraint index survives under the ordinal it now occupies, a's is retired, and the
        // explicit index is untouched.
        ReadRows(connection, SchemaQuery)
            .Should()
            .Equal("index|sqlite_autoindex_t_1|t", "index|t_c|t", "table|t|t");
        Execute(connection, "INSERT INTO t VALUES (1, 20, 'y');");
        ShouldThrow(connection, "INSERT INTO t VALUES (3, 10, 'z');")
            .Message.Should().Contain("UNIQUE constraint failed");
        ReadRows(connection, "SELECT c FROM t ORDER BY c;").Should().Equal("x", "y");
    }

    [Test]
    public void AddingAColumnLeavesEveryConstraintAndExplicitIndexRowAlone()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER UNIQUE, b TEXT);");
        Execute(connection, "CREATE INDEX t_b ON t(b);");
        Execute(connection, "INSERT INTO t VALUES (1, 'x');");

        Execute(connection, "ALTER TABLE t ADD COLUMN c BLOB;");

        ReadRows(connection, SchemaQuery)
            .Should()
            .Equal("index|sqlite_autoindex_t_1|t", "index|t_b|t", "table|t|t");
        ReadRows(connection, "SELECT sql FROM sqlite_schema WHERE name = 't_b';")
            .Should()
            .Equal("CREATE INDEX t_b ON t(b)");
        ShouldThrow(connection, "INSERT INTO t VALUES (1, 'y', NULL);")
            .Message.Should().Contain("UNIQUE constraint failed");
    }

    [Test]
    public void RenamingATableCarriesConstraintAndExplicitIndexRowsToTheirNewIdentities()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER UNIQUE, b TEXT);");
        Execute(connection, "CREATE INDEX t_b ON t(b);");
        Execute(connection, "INSERT INTO t VALUES (1, 'x');");

        Execute(connection, "ALTER TABLE t RENAME TO u;");

        ReadRows(connection, SchemaQuery)
            .Should()
            .Equal("index|sqlite_autoindex_u_1|u", "index|t_b|u", "table|u|u");
        ReadRows(connection, "SELECT sql FROM sqlite_schema WHERE name = 't_b';")
            .Should()
            .Equal("CREATE INDEX t_b ON \"u\"(b)");
        ShouldThrow(connection, "INSERT INTO u VALUES (1, 'y');")
            .Message.Should().Contain("UNIQUE constraint failed");
        ReadRows(connection, "SELECT b FROM u WHERE b = 'x';").Should().Equal("x");
    }

    [Test]
    public void DroppingAColumnBeforeAConstraintColumnKeepsBothIndexRows()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a TEXT, b INTEGER UNIQUE, c TEXT);");
        Execute(connection, "CREATE INDEX t_c ON t(c);");
        Execute(connection, "INSERT INTO t VALUES ('x', 1, 'p');");

        Execute(connection, "ALTER TABLE t DROP COLUMN a;");

        ReadRows(connection, SchemaQuery)
            .Should()
            .Equal("index|sqlite_autoindex_t_1|t", "index|t_c|t", "table|t|t");
        ShouldThrow(connection, "INSERT INTO t VALUES (1, 'q');")
            .Message.Should().Contain("UNIQUE constraint failed");
        ReadRows(connection, "SELECT c FROM t WHERE c = 'p';").Should().Equal("p");
    }

    [Test]
    public void ASurvivingExplicitIndexIsNotEvenNamedByTheProgramThatDropsAConstraint()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER UNIQUE, b TEXT);");
        Execute(connection, "CREATE INDEX t_b ON t(b);");

        var operands = ExplainOperands(connection, "EXPLAIN ALTER TABLE t ALTER COLUMN a TO a INTEGER;");

        // The program retires the constraint index's row and rewrites the table's. Pairing the index lists
        // by position instead made it rewrite the constraint row into the explicit index's identity and
        // then delete the explicit index's own row, so the survivor's name appeared in the program.
        operands.Should().Contain("sqlite_autoindex_t_1");
        operands.Should().NotContain(operand => operand.Contains("t_b", StringComparison.Ordinal));
    }

    [Test]
    public void ARenumberedConstraintIndexIsRewrittenFromItsOwnRowNotFromTheNextIndexs()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER UNIQUE, b INTEGER UNIQUE, c TEXT);");
        Execute(connection, "CREATE INDEX t_c ON t(c);");

        var operands = ExplainOperands(connection, "EXPLAIN ALTER TABLE t ALTER COLUMN a TO a INTEGER;");

        // b's constraint index moves from ordinal 2 to ordinal 1, so its own row is the one rewritten.
        operands.Should().Contain("sqlite_autoindex_t_1");
        operands.Should().Contain("sqlite_autoindex_t_2");
        operands.Should().NotContain(operand => operand.Contains("t_c", StringComparison.Ordinal));
    }

    [Test]
    public void AnAlterationThatTurnsARowidAliasIntoAKeyedPrimaryKeyCreatesTheIndexItNeeds()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER, b TEXT, PRIMARY KEY(a));");
        Execute(connection, "INSERT INTO t VALUES (1, 'x');");
        ReadRows(connection, SchemaQuery).Should().Equal("table|t|t");

        // The PRIMARY KEY stops being a rowid alias, so the constraint it used to get for free now needs
        // an index of its own; the alteration used to fail closed with an internal error instead.
        Execute(connection, "ALTER TABLE t ALTER COLUMN a TO a TEXT;");

        ReadRows(connection, SchemaQuery).Should().Equal("index|sqlite_autoindex_t_1|t", "table|t|t");
        ShouldThrow(connection, "INSERT INTO t VALUES ('1', 'y');")
            .Message.Should().Contain("UNIQUE constraint failed");
        Execute(connection, "INSERT INTO t VALUES ('2', 'y');");
        ReadRows(connection, "SELECT a FROM t ORDER BY a;").Should().Equal("1", "2");
    }

    [Test]
    public void AnIndexTheAlterationCreatesSurvivesAReopenWithARootOfItsOwn()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "alter-added-index.db";
        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE t(a INTEGER, b TEXT, PRIMARY KEY(a));");
            Execute(connection, "CREATE INDEX t_b ON t(b);");
            Execute(connection, "INSERT INTO t VALUES (1, 'x'), (2, 'y');");
            Execute(connection, "ALTER TABLE t ALTER COLUMN a TO a TEXT;");
        }

        using (var reopened = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = reopened.Connect())
        {
            ReadRows(connection, SchemaQuery)
                .Should()
                .Equal("index|sqlite_autoindex_t_1|t", "index|t_b|t", "table|t|t");
            ReadRows(connection, "SELECT a || '|' || b FROM t ORDER BY a;").Should().Equal("1|x", "2|y");
            ShouldThrow(connection, "INSERT INTO t VALUES ('1', 'z');")
                .Message.Should().Contain("UNIQUE constraint failed");
            ReadRows(connection, "SELECT a FROM t WHERE b = 'y';").Should().Equal("2");
        }
    }

    [Test]
    public void ASurvivingIndexKeepsItsOwnStorageAcrossAConstraintRemovalAndAReopen()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "alter-surviving-index.db";
        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE t(a INTEGER UNIQUE, b TEXT);");
            Execute(connection, "CREATE INDEX t_b ON t(b);");
            Execute(connection, "INSERT INTO t VALUES (1, 'x'), (2, 'y');");
            Execute(connection, "ALTER TABLE t ALTER COLUMN a TO a INTEGER;");
        }

        using (var reopened = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = reopened.Connect())
        {
            ReadRows(connection, SchemaQuery).Should().Equal("index|t_b|t", "table|t|t");
            ReadRows(connection, "SELECT sql FROM sqlite_schema WHERE name = 't_b';")
                .Should()
                .Equal("CREATE INDEX t_b ON t(b)");
            ReadRows(connection, "SELECT a FROM t WHERE b = 'y';").Should().Equal("2");
            Execute(connection, "INSERT INTO t VALUES (1, 'z');");
            ReadRows(connection, "SELECT b FROM t ORDER BY b;").Should().Equal("x", "y", "z");
        }
    }

    // ---------------------------------------------------- rename collisions are decided while planning

    [Test]
    public void RenamingATableOntoAViewIsRefusedWhilePlanning()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER);");
        Execute(connection, "CREATE VIEW v AS SELECT 1 AS x;");

        ShouldThrow(connection, "ALTER TABLE t RENAME TO v;")
            .Message.Should().Be("there is already a view named v");
        ReadRows(connection, SchemaQuery).Should().Equal("table|t|t", "view|v|v");
    }

    [Test]
    public void RenamingATableOntoATriggerIsRefusedWhilePlanning()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER);");
        Execute(connection, "CREATE TABLE log(a INTEGER);");
        Execute(connection, "CREATE TRIGGER tr AFTER INSERT ON log BEGIN SELECT 1; END;");

        ShouldThrow(connection, "ALTER TABLE t RENAME TO tr;")
            .Message.Should().Be("there is already a trigger named tr");
        ReadRows(connection, SchemaQuery)
            .Should()
            .Equal("table|log|log", "table|t|t", "trigger|tr|log");
    }

    [Test]
    public void RenamingATableOntoAVirtualTableIsRefusedWhilePlanning()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER);");
        Execute(connection, "CREATE VIRTUAL TABLE s USING fts5(body);");

        ShouldThrow(connection, "ALTER TABLE t RENAME TO s;")
            .Message.Should().Be("table s already exists");
        ReadRows(connection, "SELECT name FROM sqlite_schema WHERE name = 't';").Should().Equal("t");
        ReadScalar(connection, "SELECT COUNT(*) FROM s;").AsInteger().Should().Be(0);
    }

    [Test]
    public void ARefusedRenameNeverSurfacesTheRuntimeSchemaException()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER);");
        Execute(connection, "CREATE VIEW v AS SELECT 1 AS x;");
        Execute(connection, "CREATE TABLE log(a INTEGER);");
        Execute(connection, "CREATE TRIGGER tr AFTER INSERT ON log BEGIN SELECT 1; END;");

        foreach (var sql in new[] { "ALTER TABLE t RENAME TO v;", "ALTER TABLE t RENAME TO tr;" })
        {
            Action rename = () => Execute(connection, sql);
            rename.Should().Throw<EmbeddedSqlException>();
            rename.Should().NotThrow<VdbeSchemaExecutionException>();
        }
    }

    [TestCase("v", "there is already a view named v")]
    [TestCase("tr", "there is already a trigger named tr")]
    [TestCase("occupied", "table occupied already exists")]
    [TestCase("ix", "there is already an index named ix")]
    public void ExplainRenameRejectsAnOccupiedTargetName(string target, string message)
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER);");
        Execute(connection, "CREATE TABLE occupied(a INTEGER);");
        Execute(connection, "CREATE TABLE log(a INTEGER);");
        Execute(connection, "CREATE VIEW v AS SELECT 1 AS x;");
        Execute(connection, "CREATE TRIGGER tr AFTER INSERT ON log BEGIN SELECT 1; END;");
        Execute(connection, "CREATE INDEX ix ON log(a);");
        using var explain = connection.Prepare($"EXPLAIN ALTER TABLE t RENAME TO {target};");

        Action step = () => explain.Step();

        step.Should().Throw<EmbeddedSqlException>().WithMessage(message);
    }

    // ------------------------------------------ the typed opcode adopts the plan's replacement table

    [Test]
    public void ALargeAlterationProjectsEveryRowExactlyOnce()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER, b TEXT, c TEXT);");
        Execute(connection, "BEGIN;");
        for (var row = 0; row < 2000; row++)
            Execute(connection, $"INSERT INTO t VALUES ({row}, 'b{row}', 'c{row}');");
        Execute(connection, "COMMIT;");

        Execute(connection, "ALTER TABLE t DROP COLUMN b;");

        ReadScalar(connection, "SELECT COUNT(*) FROM t;").AsInteger().Should().Be(2000);
        ReadRows(connection, "SELECT a || '/' || c FROM t WHERE a IN (0, 1999) ORDER BY a;")
            .Should()
            .Equal("0/c0", "1999/c1999");
        ReadRows(connection, "SELECT sql FROM sqlite_schema WHERE name = 't';")
            .Should()
            .Equal("CREATE TABLE t(a INTEGER, c TEXT)");
    }

    [Test]
    public void DropColumnAdoptsThePreparedReplacementInsteadOfRecomputingIt()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER, b TEXT);");
        Execute(connection, "INSERT INTO t VALUES (1, 'x');");

        var stage = CreateStage(database);
        var prepared = database.SnapshotCatalog().Tables["t"].CreateWithoutColumn("b", CancellationToken.None);
        // A marker no recomputation could produce: if the opcode re-derived the replacement, the staged
        // table would carry the regenerated text instead.
        prepared.Sql = "CREATE TABLE t(a INTEGER) /* prepared */";
        var operations = new ManagedSchemaOperations(
            stage,
            databaseIndex: 0,
            indexServices: null,
            new ManagedSchemaPendingObjects(AlteredTables: new Dictionary<string, EmbeddedTable>(
                StringComparer.OrdinalIgnoreCase)
            {
                ["t"] = prepared,
            }));
        operations.DropColumn(0, "t", 1);

        var staged = stage.Catalog.Tables["t"];
        staged.Sql.Should().Be("CREATE TABLE t(a INTEGER) /* prepared */");
        staged.Columns.Should().Equal("a");
        staged.Rows.Should().HaveCount(1);
        // The stage owns a clone, so a program that fails afterwards cannot have mutated the plan.
        staged.Should().NotBeSameAs(prepared);
    }

    [Test]
    public void AnAlterationHonoursCancellationBeforeItTouchesTheSchema()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER, b TEXT);");
        Execute(connection, "INSERT INTO t VALUES (1, 'x');");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        using var statement = connection.Prepare("ALTER TABLE t DROP COLUMN b;");
        Action step = () => statement.Step(cancelled.Token);

        step.Should().Throw<OperationCanceledException>();
        ReadRows(connection, "SELECT sql FROM sqlite_schema WHERE name = 't';")
            .Should()
            .Equal("CREATE TABLE t(a INTEGER, b TEXT)");
    }

    // ---------------------------------------------------------------- helpers

    private static ManagedSchemaStage CreateStage(EmbeddedDatabase database)
        => ManagedSchemaStage.Create(
            "main",
            database.SnapshotCatalog,
            database.GetPragmaHeaderMetadata(),
            ManagedSchemaFixedCookies.Default);

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        while (statement.Step() == StatementStepResult.Row)
        {
        }
    }

    private static EmbeddedSqlException ShouldThrow(EmbeddedConnection connection, string sql)
    {
        Action action = () => Execute(connection, sql);
        return action.Should().Throw<EmbeddedSqlException>().Which;
    }

    private static SqlValue ReadScalar(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Row);
        return statement.GetValue(0);
    }

    /// <summary>
    /// The <c>p4</c> operand of every instruction a described program carries, with the quoting
    /// <c>EXPLAIN</c> renders text constants in removed, so a case can assert which objects a program names.
    /// </summary>
    private static string[] ExplainOperands(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        var operands = new List<string>();
        while (statement.Step() == StatementStepResult.Row)
        {
            var value = statement.GetValue(5);
            if (value.Kind is SqlValueKind.Null)
                continue;

            var text = value.AsText();
            operands.Add(text.Length >= 2 && text[0] == '\'' && text[^1] == '\''
                ? text[1..^1]
                : text);
        }

        return [.. operands];
    }

    private static string[] ReadRows(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        var rows = new List<string>();
        while (statement.Step() == StatementStepResult.Row)
        {
            var values = new List<string>();
            for (var column = 0; column < statement.ColumnCount; column++)
            {
                var value = statement.GetValue(column);
                values.Add(value.Kind switch
                {
                    SqlValueKind.Null => string.Empty,
                    SqlValueKind.Integer => value.AsInteger().ToString(CultureInfo.InvariantCulture),
                    SqlValueKind.Real => value.AsReal().ToString(CultureInfo.InvariantCulture),
                    _ => value.AsText(),
                });
            }

            rows.Add(string.Join("|", values));
        }

        return [.. rows];
    }
}
