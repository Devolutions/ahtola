using System.Globalization;
using Ahtola.Core;
using Ahtola.Core.Storage;
using AwesomeAssertions;

namespace Ahtola.Tests;

/// <summary>
/// End-to-end behavior of the compiled <c>CREATE INDEX</c>/<c>DROP INDEX</c> path: what the programs do
/// when they succeed, what they leave behind when they fail, what survives a reopen, and what
/// <c>EXPLAIN</c> reports without running them.
/// </summary>
public sealed class IndexBytecodeTests
{
    [Test]
    public void ExplainCreateIndexDescribesTheSchemaProgramWithoutMutatingAnything()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER, b TEXT);");
        var schemaVersion = ReadScalar(connection, "PRAGMA schema_version;").AsInteger();

        var opcodes = ExplainOpcodes(connection, "EXPLAIN CREATE INDEX idx ON t(a);");

        opcodes.Should().Contain(
            ["CreateBtree", "NewRowid", "MakeRecord", "Insert", "SetCookie", "ParseSchema", "IndexBuild", "Halt"]);
        ReadScalar(connection, "PRAGMA schema_version;").AsInteger().Should().Be(schemaVersion);
        ReadScalar(connection, "SELECT COUNT(*) FROM sqlite_schema WHERE name = 'idx';").AsInteger().Should().Be(0);
    }

    [Test]
    public void ExplainDropIndexDescribesTheScanAndDestroyProgramWithoutMutatingAnything()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER);");
        Execute(connection, "CREATE INDEX idx ON t(a);");
        var schemaVersion = ReadScalar(connection, "PRAGMA schema_version;").AsInteger();

        var opcodes = ExplainOpcodes(connection, "EXPLAIN DROP INDEX idx;");

        opcodes.Should().Contain(
            ["Rewind", "Column", "Compare", "Delete", "Next", "SetCookie", "Destroy", "DropIndex", "Halt"]);
        ReadScalar(connection, "PRAGMA schema_version;").AsInteger().Should().Be(schemaVersion);
        ReadScalar(connection, "SELECT COUNT(*) FROM sqlite_schema WHERE name = 'idx';").AsInteger().Should().Be(1);
    }

    [Test]
    public void ExplainCreateIndexUsingAMethodDescribesItsLifecycleWithoutAttachingIt()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE docs(id INTEGER PRIMARY KEY, embedding BLOB);");

        var opcodes = ExplainOpcodes(
            connection,
            "EXPLAIN CREATE INDEX docs_knn ON docs USING vector (embedding) WITH (dims = 4);");

        opcodes.Should().Contain(["CreateBtree", "MakeRecord", "Insert", "ParseSchema", "IndexMethodCreate", "Halt"]);
        opcodes.Should().NotContain("IndexBuild");
        ReadScalar(connection, "SELECT COUNT(*) FROM sqlite_schema WHERE name = 'docs_knn';")
            .AsInteger()
            .Should()
            .Be(0);
    }

    [Test]
    public void AMethodIndexIsCreatedAndDestroyedThroughItsOwnLifecycleOpcodes()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE docs(id INTEGER PRIMARY KEY, embedding BLOB);");
        Execute(connection, "INSERT INTO docs VALUES (1, vector32('[1,0,0,0]')), (2, vector32('[0,1,0,0]'));");

        Execute(connection, "CREATE INDEX docs_knn ON docs USING vector (embedding) WITH (dims = 4, min_rows = 0);");
        ReadScalar(connection, "SELECT sql FROM sqlite_schema WHERE name = 'docs_knn';")
            .AsText()
            .Should()
            .Be("CREATE INDEX docs_knn ON docs USING vector (embedding) WITH (dims = 4, min_rows = 0)");
        ReadRows(
                connection,
                "SELECT id FROM docs ORDER BY vector_distance_l2(embedding, vector32('[1,0,0,0]')) LIMIT 1;")
            .Should()
            .Equal("1");

        Execute(connection, "DROP INDEX docs_knn;");
        ReadScalar(connection, "SELECT COUNT(*) FROM sqlite_schema WHERE name = 'docs_knn';")
            .AsInteger()
            .Should()
            .Be(0);
        ReadRows(
                connection,
                "SELECT id FROM docs ORDER BY vector_distance_l2(embedding, vector32('[1,0,0,0]')) LIMIT 1;")
            .Should()
            .Equal("1");
    }

    [Test]
    public void AFailedMethodIndexCreateLeavesNoAttachmentBehind()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE docs(id INTEGER PRIMARY KEY, embedding BLOB);");
        var rows = ReadRows(connection, SchemaQuery);

        Action unknownMethod = () =>
            Execute(connection, "CREATE INDEX docs_knn ON docs USING nonexistent (embedding);");
        unknownMethod.Should().Throw<EmbeddedSqlException>();

        ReadRows(connection, SchemaQuery).Should().Equal(rows);

        // The name is still free, and creating it for real afterwards works from a clean slate.
        Execute(connection, "CREATE INDEX docs_knn ON docs USING vector (embedding) WITH (dims = 4);");
        ReadScalar(connection, "SELECT COUNT(*) FROM sqlite_schema WHERE name = 'docs_knn';")
            .AsInteger()
            .Should()
            .Be(1);
    }

    [Test]
    public void RollingBackAMethodIndexDropRestoresIt()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE docs(id INTEGER PRIMARY KEY, embedding BLOB);");
        Execute(connection, "INSERT INTO docs VALUES (1, vector32('[1,0,0,0]')), (2, vector32('[0,1,0,0]'));");
        Execute(connection, "CREATE INDEX docs_knn ON docs USING vector (embedding) WITH (dims = 4, min_rows = 0);");
        var rows = ReadRows(connection, SchemaQuery);

        Execute(connection, "BEGIN;");
        Execute(connection, "DROP INDEX docs_knn;");
        Execute(connection, "ROLLBACK;");

        ReadRows(connection, SchemaQuery).Should().Equal(rows);
        ReadRows(
                connection,
                "SELECT id FROM docs ORDER BY vector_distance_l2(embedding, vector32('[0,1,0,0]')) LIMIT 1;")
            .Should()
            .Equal("2");
    }

    [Test]
    public void CreateIndexWritesTheSchemaRowTheProgramBuilt()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER, b TEXT);");

        Execute(connection, "CREATE UNIQUE INDEX idx ON t(b COLLATE NOCASE DESC) WHERE a > 0;");

        ReadRows(connection, "SELECT type, name, tbl_name, sql FROM sqlite_schema WHERE type = 'index';")
            .Should()
            .Equal("index|idx|t|CREATE UNIQUE INDEX idx ON t(b COLLATE NOCASE DESC) WHERE a > 0");
    }

    [Test]
    public void CreateIndexBumpsTheSchemaCookieExactlyOnce()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER);");
        var before = ReadScalar(connection, "PRAGMA schema_version;").AsInteger();

        Execute(connection, "CREATE INDEX idx ON t(a);");

        ReadScalar(connection, "PRAGMA schema_version;").AsInteger().Should().Be(before + 1);
    }

    [Test]
    public void DropIndexBumpsTheSchemaCookieExactlyOnce()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER);");
        Execute(connection, "CREATE INDEX idx ON t(a);");
        var before = ReadScalar(connection, "PRAGMA schema_version;").AsInteger();

        Execute(connection, "DROP INDEX idx;");

        ReadScalar(connection, "PRAGMA schema_version;").AsInteger().Should().Be(before + 1);
    }

    [Test]
    public void IndexDdlLeavesLastInsertRowidAndChangesUntouched()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, v TEXT);");
        Execute(connection, "INSERT INTO t VALUES (42, 'x');");

        Execute(connection, "CREATE INDEX idx ON t(v);");
        ReadScalar(connection, "SELECT last_insert_rowid();").AsInteger().Should().Be(42);
        ReadScalar(connection, "SELECT changes();").AsInteger().Should().Be(1);

        Execute(connection, "DROP INDEX idx;");
        ReadScalar(connection, "SELECT last_insert_rowid();").AsInteger().Should().Be(42);
        ReadScalar(connection, "SELECT changes();").AsInteger().Should().Be(1);
    }

    [Test]
    public void CreateIndexIfNotExistsOnAnExistingIndexChangesNothing()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER);");
        Execute(connection, "CREATE INDEX idx ON t(a);");
        var schemaVersion = ReadScalar(connection, "PRAGMA schema_version;").AsInteger();
        var rows = ReadRows(connection, SchemaQuery);

        Execute(connection, "CREATE INDEX IF NOT EXISTS idx ON t(a);");

        ReadRows(connection, SchemaQuery).Should().Equal(rows);
        ReadScalar(connection, "PRAGMA schema_version;").AsInteger().Should().Be(schemaVersion);
    }

    [Test]
    public void DropIndexIfExistsOnAMissingIndexChangesNothing()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER);");
        var schemaVersion = ReadScalar(connection, "PRAGMA schema_version;").AsInteger();

        Execute(connection, "DROP INDEX IF EXISTS missing;");

        ReadScalar(connection, "PRAGMA schema_version;").AsInteger().Should().Be(schemaVersion);
    }

    [Test]
    public void DropIndexUsesTheResolvedStoredNameCaseInsensitively()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER);");
        Execute(connection, "CREATE INDEX MixedCase ON t(a);");

        Execute(connection, "DROP INDEX mixedcase;");

        ReadScalar(connection, "SELECT COUNT(*) FROM sqlite_schema WHERE name = 'MixedCase';")
            .AsInteger()
            .Should()
            .Be(0);
    }

    [Test]
    public void AUniqueIndexOverConflictingRowsFailsAndLeavesNothingBehind()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1), (1);");
        var schemaVersion = ReadScalar(connection, "PRAGMA schema_version;").AsInteger();
        var rows = ReadRows(connection, SchemaQuery);

        Action create = () => Execute(connection, "CREATE UNIQUE INDEX idx ON t(a);");

        create.Should().Throw<EmbeddedSqlException>().WithMessage("UNIQUE constraint failed: t.a");
        ReadRows(connection, SchemaQuery).Should().Equal(rows);
        ReadScalar(connection, "PRAGMA schema_version;").AsInteger().Should().Be(schemaVersion);
        // The failed program must not leave the index in the live catalog either.
        Execute(connection, "INSERT INTO t VALUES (1);");
        ReadScalar(connection, "SELECT COUNT(*) FROM t;").AsInteger().Should().Be(3);
    }

    [Test]
    public void AUniqueIndexTreatsNullsAsDistinctAndHonorsAPartialPredicate()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER, keep INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (NULL, 1), (NULL, 1), (7, 0), (7, 0);");

        Execute(connection, "CREATE UNIQUE INDEX idx ON t(a) WHERE keep = 1;");

        ReadScalar(connection, "SELECT COUNT(*) FROM sqlite_schema WHERE name = 'idx';").AsInteger().Should().Be(1);
    }

    [Test]
    public void AUniqueIndexUsesTheDeclaredCollationWhenItLooksForConflicts()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a TEXT);");
        Execute(connection, "INSERT INTO t VALUES ('x'), ('X');");

        Action caseInsensitive = () => Execute(connection, "CREATE UNIQUE INDEX ci ON t(a COLLATE NOCASE);");
        caseInsensitive.Should().Throw<EmbeddedSqlException>().WithMessage("UNIQUE constraint failed: t.a");

        Execute(connection, "CREATE UNIQUE INDEX cs ON t(a);");
        ReadScalar(connection, "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'index';").AsInteger().Should().Be(1);
    }

    [Test]
    public void CreateIndexIsRejectedWhenItsExpressionNamesAnApplicationDefinedFunction()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        connection.RegisterScalarFunction("upper", 1, static arguments => arguments[0]);
        Execute(connection, "CREATE TABLE t(a TEXT);");

        Action create = () => Execute(connection, "CREATE INDEX idx ON t(upper(a));");

        create.Should().Throw<EmbeddedSqlException>().WithMessage(
            "application-defined functions are prohibited in index expressions and partial index WHERE clauses");
        ReadScalar(connection, "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'index';").AsInteger().Should().Be(0);
    }

    [Test]
    public void CreateIndexIsRejectedWhenItsCollationIsNotRegistered()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a TEXT);");

        Action create = () => Execute(connection, "CREATE INDEX idx ON t(a COLLATE unavailable);");

        create.Should().Throw<EmbeddedSqlException>().WithMessage("no such collation sequence: unavailable");
        ReadScalar(connection, "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'index';").AsInteger().Should().Be(0);
    }

    [Test]
    public void ACustomCollationIndexIsUsableAfterItsCollationIsRegistered()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        connection.RegisterCollation("reverse", static (left, right) => string.CompareOrdinal(right, left));
        Execute(connection, "CREATE TABLE t(a TEXT);");
        Execute(connection, "INSERT INTO t VALUES ('a'), ('b'), ('c');");

        Execute(connection, "CREATE INDEX idx ON t(a COLLATE reverse);");

        ReadScalar(connection, "SELECT sql FROM sqlite_schema WHERE name = 'idx';")
            .AsText()
            .Should()
            .Be("CREATE INDEX idx ON t(a COLLATE reverse)");
    }

    [Test]
    public void AnUnavailableCustomCollationIndexSurvivesAReopenUntouched()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "index-bytecode-collation.db";

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            connection.RegisterCollation("reverse", static (left, right) => string.CompareOrdinal(right, left));
            Execute(connection, "CREATE TABLE t(a TEXT);");
            Execute(connection, "INSERT INTO t VALUES ('a'), ('b');");
            Execute(connection, "CREATE INDEX idx ON t(a COLLATE reverse);");
        }

        using var reopenedDatabase = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopened = reopenedDatabase.Connect();
        ReadScalar(reopened, "SELECT sql FROM sqlite_schema WHERE name = 'idx';")
            .AsText()
            .Should()
            .Be("CREATE INDEX idx ON t(a COLLATE reverse)");
    }

    [Test]
    public void CreateIndexIsRejectedForTargetsThatCannotBeIndexed()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER);");
        Execute(connection, "CREATE VIEW v AS SELECT a FROM t;");

        Action onView = () => Execute(connection, "CREATE INDEX idx ON v(a);");
        onView.Should().Throw<EmbeddedSqlException>().WithMessage("views may not be indexed");

        Action onSchema = () => Execute(connection, "CREATE INDEX idx ON sqlite_schema(name);");
        onSchema.Should().Throw<EmbeddedSqlException>().WithMessage("table sqlite_schema may not be indexed");

        Action reservedName = () => Execute(connection, "CREATE INDEX sqlite_idx ON t(a);");
        reservedName.Should().Throw<EmbeddedSqlException>()
            .WithMessage("object name reserved for internal use: sqlite_idx");

        Action clashesWithView = () => Execute(connection, "CREATE INDEX v ON t(a);");
        clashesWithView.Should().Throw<EmbeddedSqlException>().WithMessage("there is already a view named v");
    }

    [Test]
    public void DropIndexIsRejectedForAConstraintBackedIndex()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER UNIQUE);");
        var automatic = ReadScalar(connection, "SELECT name FROM sqlite_schema WHERE type = 'index';").AsText();

        Action drop = () => Execute(connection, $"DROP INDEX {automatic};");

        drop.Should().Throw<EmbeddedSqlException>().WithMessage(
            $"index associated with UNIQUE or PRIMARY KEY constraint cannot be dropped: {automatic}");
        ReadScalar(connection, "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'index';").AsInteger().Should().Be(1);
    }

    [Test]
    public void DropIndexRemovesOnlyItsOwnSchemaRow()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER, b INTEGER);");
        Execute(connection, "CREATE INDEX idx_a ON t(a);");
        Execute(connection, "CREATE INDEX idx_b ON t(b);");

        Execute(connection, "DROP INDEX idx_a;");

        ReadRows(connection, SchemaQuery).Should().Equal("index|idx_b|t", "table|t|t");
    }

    [Test]
    public void RollingBackATransactionDiscardsBothIndexDdlDirections()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER, b INTEGER);");
        Execute(connection, "CREATE INDEX idx_b ON t(b);");
        var schemaVersion = ReadScalar(connection, "PRAGMA schema_version;").AsInteger();
        var rows = ReadRows(connection, SchemaQuery);

        Execute(connection, "BEGIN;");
        Execute(connection, "CREATE INDEX idx_a ON t(a);");
        Execute(connection, "DROP INDEX idx_b;");
        Execute(connection, "ROLLBACK;");

        ReadRows(connection, SchemaQuery).Should().Equal(rows);
        ReadScalar(connection, "PRAGMA schema_version;").AsInteger().Should().Be(schemaVersion);
    }

    [Test]
    public void SavepointRollbackDiscardsIndexDdl()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER);");

        Execute(connection, "BEGIN;");
        Execute(connection, "SAVEPOINT before_index;");
        Execute(connection, "CREATE INDEX idx ON t(a);");
        ReadScalar(connection, "SELECT COUNT(*) FROM sqlite_schema WHERE name = 'idx';").AsInteger().Should().Be(1);
        Execute(connection, "ROLLBACK TO before_index;");
        Execute(connection, "RELEASE before_index;");
        Execute(connection, "COMMIT;");

        ReadScalar(connection, "SELECT COUNT(*) FROM sqlite_schema WHERE name = 'idx';").AsInteger().Should().Be(0);
    }

    [Test]
    public void CommittedIndexDdlSurvivesAFileReopen()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "index-bytecode.db";

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE t(a INTEGER, b TEXT);");
            Execute(connection, "INSERT INTO t VALUES (1, 'x'), (2, 'y');");
            Execute(connection, "CREATE INDEX idx_a ON t(a);");
            Execute(connection, "CREATE INDEX idx_b ON t(b);");
            Execute(connection, "DROP INDEX idx_a;");
        }

        using var reopenedDatabase = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopened = reopenedDatabase.Connect();
        ReadRows(reopened, SchemaQuery).Should().Equal("index|idx_b|t", "table|t|t");
        ReadRows(reopened, "SELECT a FROM t WHERE b = 'y';").Should().Equal("2");
    }

    [Test]
    public void ARolledBackFileBackedIndexDoesNotSurviveAReopen()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "index-bytecode-rollback.db";

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE t(a INTEGER);");
            Execute(connection, "CREATE INDEX stable ON t(a);");
            Execute(connection, "BEGIN;");
            Execute(connection, "CREATE INDEX transient ON t(a);");
            Execute(connection, "DROP INDEX stable;");
            Execute(connection, "ROLLBACK;");
        }

        using var reopenedDatabase = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopened = reopenedDatabase.Connect();
        ReadRows(reopened, SchemaQuery).Should().Equal("index|stable|t", "table|t|t");
    }

    [Test]
    public void CreateIndexIsRejectedWhenItWouldExceedTheMaximumPageCount()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "PRAGMA user_version = 1;");
        Execute(connection, "CREATE TABLE t(a INTEGER);");
        var pageCount = ReadScalar(connection, "PRAGMA page_count;").AsInteger();
        Execute(connection, $"PRAGMA max_page_count = {pageCount};");
        var rows = ReadRows(connection, SchemaQuery);

        Action create = () => Execute(connection, "CREATE INDEX idx ON t(a);");

        create.Should().Throw<EmbeddedSqlException>().WithMessage("database or disk is full");
        ReadRows(connection, SchemaQuery).Should().Equal(rows);
    }

    [Test]
    public void AnIndexCreatedByBytecodeIsUsableByThePlanner()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER, b TEXT);");
        for (var value = 0; value < 32; value++)
            Execute(connection, $"INSERT INTO t VALUES ({value}, 'v{value}');");

        Execute(connection, "CREATE INDEX idx ON t(a);");

        ReadRows(connection, "SELECT b FROM t WHERE a = 17;").Should().Equal("v17");
        var plan = ReadRows(connection, "EXPLAIN QUERY PLAN SELECT b FROM t WHERE a = 17;");
        plan.Should().Contain(row => row.Contains("idx", StringComparison.Ordinal));
    }

    [Test]
    public void IndexDdlOnATemporarySchemaStaysOutOfTheMainSchema()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TEMP TABLE scratch(a INTEGER);");
        Execute(connection, "INSERT INTO scratch VALUES (5);");

        Execute(connection, "CREATE INDEX scratch_a ON scratch(a);");

        ReadScalar(connection, "SELECT a FROM scratch WHERE a = 5;").AsInteger().Should().Be(5);
        ReadScalar(connection, "SELECT COUNT(*) FROM main.sqlite_schema WHERE name = 'scratch_a';")
            .AsInteger()
            .Should()
            .Be(0);

        Execute(connection, "DROP INDEX scratch_a;");
        ReadScalar(connection, "SELECT a FROM scratch WHERE a = 5;").AsInteger().Should().Be(5);
    }

    [Test]
    public void ReindexStillRebuildsAnIndexTheBytecodePathCreated()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (3), (1), (2);");
        Execute(connection, "CREATE INDEX idx ON t(a);");
        var rows = ReadRows(connection, SchemaQuery);

        Execute(connection, "REINDEX idx;");

        ReadRows(connection, SchemaQuery).Should().Equal(rows);
        ReadRows(connection, "SELECT a FROM t WHERE a > 1 ORDER BY a;").Should().Equal("2", "3");
    }

    private const string SchemaQuery =
        "SELECT type, name, tbl_name FROM sqlite_schema ORDER BY type, name;";

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        while (statement.Step() == StatementStepResult.Row)
        {
        }
    }

    private static SqlValue ReadScalar(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Row);
        return statement.GetValue(0);
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

        return rows.ToArray();
    }

    private static string[] ExplainOpcodes(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        var opcodes = new List<string>();
        while (statement.Step() == StatementStepResult.Row)
            opcodes.Add(statement.GetValue(1).AsText());

        return opcodes.ToArray();
    }
}
