using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Ahtola.Core;
using MsData = Microsoft.Data.Sqlite;

namespace Ahtola.Tests;

/// <summary>
/// Differential regressions (managed engine vs bundled SQLite) for the schema, DML and PRAGMA
/// parity fixes ported from Turso v0.8.1: REPLACE defaults feeding generated columns, invalid
/// generated-column clauses, UPSERT target aliases, BEFORE UPDATE triggers fired by UPSERT,
/// the 2,000-column limit, parameter tokenization, PRAGMA count_changes, temp.synchronous and
/// RENAME onto a view. Sequence backing-table protection is Turso-only and is checked directly.
/// </summary>
public class SchemaDmlPragmaParityTests
{
    [TestCase("""
        CREATE TABLE t(a NOT NULL DEFAULT 5, b AS (a+1) UNIQUE);
        REPLACE INTO t(a) VALUES(NULL) RETURNING a, b;
        """)]
    [TestCase("""
        CREATE TABLE t(a NOT NULL DEFAULT 5, b AS (a+1) UNIQUE);
        REPLACE INTO t(a) VALUES(NULL);
        SELECT rowid, a, b FROM t;
        SELECT b FROM t INDEXED BY sqlite_autoindex_t_1 WHERE b = 6;
        PRAGMA integrity_check;
        """)]
    [TestCase("""
        CREATE TABLE t(a NOT NULL DEFAULT 5, b AS (a+1) CHECK(b IS 6));
        REPLACE INTO t(a) VALUES(NULL);
        SELECT a, b FROM t;
        """)]
    [TestCase("""
        CREATE TABLE t(b AS(a+c) NOT NULL UNIQUE CHECK(b=12), a NOT NULL DEFAULT 5, c NOT NULL DEFAULT 7);
        INSERT OR REPLACE INTO t(a,c) VALUES(NULL,NULL) RETURNING a,c,b;
        PRAGMA integrity_check;
        """)]
    [TestCase("""
        CREATE TABLE t(a TEXT NOT NULL ON CONFLICT REPLACE DEFAULT '42', b INT AS(a)) STRICT;
        INSERT INTO t VALUES(NULL);
        SELECT a, b, typeof(b) FROM t;
        """)]
    [TestCase("""
        CREATE TABLE t(a NOT NULL DEFAULT 5, b AS (a*2));
        CREATE TABLE log(v);
        CREATE TRIGGER tr BEFORE INSERT ON t BEGIN INSERT INTO log VALUES (quote(new.a) || '/' || quote(new.b)); END;
        REPLACE INTO t(a) VALUES(NULL);
        SELECT a, b FROM t;
        SELECT v FROM log;
        """)]
    [TestCase("""
        CREATE TABLE t(id INTEGER PRIMARY KEY, a NOT NULL DEFAULT 5, b AS (a+1));
        INSERT INTO t VALUES(1, 1);
        UPDATE OR REPLACE t SET a = NULL WHERE id = 1;
        SELECT a, b FROM t;
        """)]
    public void ReplaceDefaultsAreAppliedBeforeGeneratedColumnsAreComputed(string sql)
        => AssertSameOutcome(sql);

    [TestCase("CREATE TABLE t(a); ALTER TABLE t ADD COLUMN b DEFAULT 5 AS (a+1);")]
    [TestCase("CREATE TABLE t(a); ALTER TABLE t ADD COLUMN b AS (a+1) DEFAULT 5;")]
    [TestCase("CREATE TABLE t(a, b DEFAULT 5 AS (a+1));")]
    [TestCase("CREATE TABLE t(a, b AS (a+1) DEFAULT 5);")]
    [TestCase("CREATE TABLE t(a, b DEFAULT 5 GENERATED ALWAYS AS (a+1));")]
    [TestCase("CREATE TABLE t(a, b AS (a) WAT);")]
    [TestCase("CREATE TABLE t(a, b AS (a) REINDEX);")]
    [TestCase("CREATE TABLE t(a, \"b\" AS (a) WAT);")]
    [TestCase("CREATE TABLE t(a, b, c AS (a+1) AS (a+2));")]

    [TestCase("CREATE TABLE t(a, b AS (a) VIRTUAL AS (a+1));")]
    [TestCase("CREATE TABLE t(a, b AS (a) CONSTRAINT another AS (a+1));")]
    [TestCase("CREATE TABLE t(a); ALTER TABLE t ADD COLUMN b AS (a) WAT;")]
    [TestCase("""
        CREATE TABLE t(a, b AS (a) stored NOT NULL, c GENERATED ALWAYS AS (a) ViRtUaL,
            d AS (a) CONSTRAINT positive CHECK (d > 0), e AS (a) COLLATE nocase, f DEFAULT 5);
        INSERT INTO t(a) VALUES (3);
        SELECT a, b, c, d, e, f FROM t;
        """)]
    public void InvalidGeneratedColumnClausesAreRejectedLikeSqlite(string sql)
        => AssertSameOutcome(sql);

    // SQLite's grammar reads GENERATED after a generation clause as the column type and then
    // fails with `near "ALWAYS": syntax error`; like Turso's parser the managed one reports the
    // second generation clause itself.
    [TestCase("CREATE TABLE t(a, b AS (a+1) GENERATED ALWAYS AS (a+2));")]
    [TestCase("CREATE TABLE t(a); ALTER TABLE t ADD COLUMN b GENERATED ALWAYS AS (a+1) GENERATED ALWAYS AS (a+2);")]
    public void ASecondGenerationClauseIsRejectedLikeTurso(string sql)
    {
        RunSqlite(sql).Error.Should().NotBeNull();
        RunManaged(sql).Error.Should().Be("error in generated column \"b\"");
    }

    [TestCase("""
        CREATE TABLE t(k INT PRIMARY KEY, n INT);
        INSERT INTO t VALUES(1,10);
        INSERT INTO t AS excluded VALUES(1,99) ON CONFLICT(k) DO UPDATE SET n = excluded.n;
        SELECT n FROM t;
        """)]
    [TestCase("""
        CREATE TABLE t(k INT PRIMARY KEY, n INT);
        INSERT INTO t VALUES(1,10);
        INSERT INTO t AS x VALUES(1,7) ON CONFLICT(k) DO UPDATE SET n = x.n + excluded.n WHERE x.n > 0;
        SELECT n FROM t;
        """)]
    [TestCase("""
        CREATE TABLE t(k INT PRIMARY KEY, n INT);
        INSERT INTO t VALUES(1,10);
        INSERT INTO t AS x VALUES(1,7) ON CONFLICT(k) DO UPDATE SET n = t.n + 1;
        """)]
    [TestCase("""
        CREATE TABLE t(k INT PRIMARY KEY, n INT);
        CREATE TABLE log(v);
        CREATE TRIGGER tr BEFORE UPDATE ON t BEGIN INSERT INTO log VALUES (old.n); END;
        INSERT INTO t VALUES(1,10);
        INSERT INTO t AS x VALUES(1,7) ON CONFLICT(k) DO UPDATE SET n = x.n * 2;
        SELECT n FROM t;
        SELECT v FROM log;
        """)]
    public void UpsertHonorsTheInsertTargetAlias(string sql)
        => AssertSameOutcome(sql);

    [TestCase("""
        CREATE TABLE t(a INTEGER PRIMARY KEY, b INTEGER, c INTEGER);
        CREATE INDEX i ON t(c);
        INSERT INTO t VALUES(1,1,1);
        CREATE TRIGGER tg BEFORE UPDATE ON t BEGIN UPDATE t SET c=c+10 WHERE a=NEW.a; END;
        INSERT INTO t VALUES(1,9,9) ON CONFLICT(a) DO UPDATE SET b=b+5;
        SELECT a, b, c FROM t;
        SELECT c FROM t WHERE c > 0 ORDER BY c;
        PRAGMA integrity_check;
        """)]
    [TestCase("""
        CREATE TABLE t(a INTEGER PRIMARY KEY, c INTEGER, g AS (c*2));
        CREATE INDEX i ON t(c);
        CREATE INDEX ig ON t(g);
        INSERT INTO t VALUES(1,1);
        CREATE TRIGGER tg BEFORE UPDATE ON t BEGIN UPDATE t SET c=c+1 WHERE a=NEW.a; END;
        INSERT INTO t VALUES(1,9) ON CONFLICT(a) DO UPDATE SET c=c+5;
        SELECT a, c, g FROM t;
        SELECT g FROM t WHERE g > 0;
        DELETE FROM t;
        SELECT count(*) FROM t WHERE c > 0;
        PRAGMA integrity_check;
        """)]
    public void UpsertKeepsRowChangesMadeByABeforeUpdateTrigger(string sql)
        => AssertSameOutcome(sql);

    [TestCase("""
        CREATE TABLE x(a);
        CREATE VIEW y AS SELECT 1;
        ALTER TABLE x RENAME TO y;
        """)]
    [TestCase("""
        CREATE TABLE x(a);
        CREATE VIEW y AS SELECT 1;
        ALTER TABLE x RENAME TO Y;
        """)]
    public void RenamingOntoAViewReportsSqlitesMessage(string sql)
        => AssertSameOutcome(sql);

    [TestCase("""
        PRAGMA temp.synchronous;
        PRAGMA temp.synchronous=FULL;
        PRAGMA temp.synchronous;
        PRAGMA temp.synchronous=NORMAL;
        PRAGMA temp.synchronous;
        PRAGMA main.synchronous;
        PRAGMA synchronous=OFF;
        PRAGMA main.synchronous;
        """)]
    [TestCase("BEGIN; PRAGMA temp.synchronous=OFF;")]
    public void TempSynchronousStaysOff(string sql)
        => AssertSameOutcome(sql);

    [Test]
    public void CreateTableRejectsMoreThanTwoThousandColumns()
    {
        AssertSameOutcome($"CREATE TABLE t({Columns(2000)}); SELECT count(*) FROM pragma_table_info('t');");
        AssertSameOutcome($"CREATE TABLE t({Columns(2001)});");
        AssertSameOutcome($"CREATE TABLE \"My T\"({Columns(2001)});");
        AssertSameOutcome($"CREATE TABLE main.t({Columns(2001)});");
        AssertSameOutcome($"CREATE TEMP TABLE t({Columns(2001)});");
    }

    [Test]
    public void AlterTableAddColumnRejectsTheTwoThousandFirstColumn()
    {
        AssertSameOutcome($"CREATE TABLE t({Columns(1999)}); ALTER TABLE t ADD COLUMN last; SELECT count(*) FROM pragma_table_info('t');");
        AssertSameOutcome($"CREATE TABLE t({Columns(2000)}); ALTER TABLE t ADD COLUMN extra;");
        AssertSameOutcome($"CREATE TABLE t({Columns(2000)}); ALTER TABLE t ADD COLUMN c1;");
    }

    [TestCase("SELECT $;")]
    [TestCase("SELECT :;")]
    [TestCase("SELECT @;")]
    [TestCase("SELECT 1 WHERE $ = 1;")]
    [TestCase("SELECT $::;")]
    [TestCase("SELECT @::;")]
    [TestCase("SELECT $a(b;")]
    [TestCase("SELECT $a(b c);")]
    public void BadParameterNamesAreUnrecognizedTokens(string sql)
    {
        var managed = RunManaged(sql);
        var sqlite = RunSqlite(sql);

        sqlite.Error.Should().StartWith("unrecognized token: ");
        managed.Error.Should().Be(sqlite.Error);
    }

    [Test]
    public void TclStyleParameterNamesTokenizeLikeSqlite()
    {
        string[] names = ["$::ii", "$ns::var", "$arr(elem)", "@a::b::c", ":a::b", ":::g", "$a::", "$a(x,y)", "$a::::b", "$é"];
        var sql = $"SELECT {string.Join(", ", names)};";

        using var managedDatabase = new EmbeddedDatabase();
        using var managedConnection = managedDatabase.Connect();
        using var statement = managedConnection.Prepare(sql);
        statement.ParameterCount.Should().Be(names.Length);
        for (var index = 0; index < names.Length; index++)
        {
            statement.GetParameterName(index + 1).Should().Be(names[index]);
            statement.Bind(statement.GetParameterIndex(names[index]), SqlValue.Integer(index + 1));
        }

        statement.Step().Should().Be(StatementStepResult.Row);
        var managedRow = Enumerable.Range(0, statement.ColumnCount)
            .Select(column => statement.GetValue(column).AsInteger())
            .ToArray();

        using var sqlite = new MsData.SqliteConnection("Data Source=:memory:;Pooling=False");
        sqlite.Open();
        using var command = sqlite.CreateCommand();
        command.CommandText = sql;
        for (var index = 0; index < names.Length; index++)
            command.Parameters.AddWithValue(names[index], index + 1);
        using var reader = command.ExecuteReader();
        reader.Read().Should().BeTrue();
        var sqliteRow = Enumerable.Range(0, reader.FieldCount).Select(reader.GetInt64).ToArray();

        managedRow.Should().Equal(sqliteRow);
        managedRow.Should().Equal(Enumerable.Range(1, names.Length).Select(static value => (long)value));
    }

    [TestCase("INSERT INTO t1 VALUES (5), (6);")]
    [TestCase("INSERT INTO t1 SELECT x + 100 FROM t1;")]
    [TestCase("INSERT OR IGNORE INTO t1 VALUES (2), (7);")]
    [TestCase("INSERT INTO t1 VALUES (3), (8) ON CONFLICT(x) DO UPDATE SET x = x + 10;")]
    [TestCase("INSERT INTO t1 VALUES (5), (1) ON CONFLICT DO NOTHING;")]
    [TestCase("REPLACE INTO t1 VALUES (1), (2);")]
    [TestCase("WITH c(v) AS (SELECT 7) INSERT INTO t1 SELECT v FROM c;")]
    [TestCase("UPDATE t1 SET x = x + 1 WHERE x >= 2;")]
    [TestCase("UPDATE t1 SET x = x WHERE 0;")]
    [TestCase("DELETE FROM t1 WHERE 0;")]
    [TestCase("DELETE FROM t1;")]
    [TestCase("INSERT INTO t1 VALUES (9) RETURNING x;")]
    [TestCase("UPDATE t1 SET x = x + 20 WHERE x = 1 RETURNING x;")]
    [TestCase("DELETE FROM t1 WHERE x = 1 RETURNING x;")]
    [TestCase("INSERT INTO t1 VALUES (5); SELECT changes(), total_changes();")]
    [TestCase("UPDATE t1 SET x = x + 1; SELECT changes();")]
    [TestCase("PRAGMA count_changes = off; INSERT INTO t1 VALUES (5); PRAGMA count_changes;")]
    public void CountChangesReturnsTheChangedRowCount(string sql)
    {
        const string setup = """
            CREATE TABLE t1(x UNIQUE);
            CREATE TABLE log(v);
            CREATE TRIGGER tr AFTER INSERT ON t1 BEGIN INSERT INTO log VALUES (new.x); UPDATE log SET v = v; END;
            INSERT INTO t1 VALUES (1), (2), (3);
            PRAGMA count_changes = 1;
            """;
        AssertSameOutcome(setup + sql, compareColumns: true);
    }

    [Test]
    public void CountChangesDescribesItsResultColumnAtPrepareTime()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(x); PRAGMA count_changes = 1;");
        foreach (var (sql, column) in new[]
                 {
                     ("INSERT INTO t VALUES (1)", "rows inserted"),
                     ("UPDATE t SET x = 2", "rows updated"),
                     ("DELETE FROM t", "rows deleted"),
                 })
        {
            using var statement = connection.Prepare(sql);
            statement.GetColumnCount().Should().Be(1, sql);
            statement.GetColumnName(0).Should().Be(column, sql);
        }

        using var returning = connection.Prepare("INSERT INTO t VALUES (1) RETURNING x");
        returning.GetColumnCount().Should().Be(1);
        returning.GetColumnName(0).Should().Be("x");
    }

    [TestCase("INSERT INTO __TURSO_INTERNAL_SEQ_S DEFAULT VALUES;", "table __TURSO_INTERNAL_SEQ_S may not be modified")]
    [TestCase("INSERT INTO __TuRsO_iNtErNaL_sEq_S DEFAULT VALUES;", "table __TuRsO_iNtErNaL_sEq_S may not be modified")]
    [TestCase("INSERT INTO __turso_internal_seq_s DEFAULT VALUES;", "table __turso_internal_seq_s may not be modified")]
    [TestCase("UPDATE __TURSO_INTERNAL_SEQ_S SET value = 2;", "table __TURSO_INTERNAL_SEQ_S may not be modified")]
    [TestCase("DELETE FROM main.__Turso_Internal_Seq_S;", "table __Turso_Internal_Seq_S may not be modified")]
    public void SequenceBackingTablesRejectUserDmlRegardlessOfCase(string sql, string message)
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE SEQUENCE s START WITH 5;");

        Action write = () => Execute(connection, sql);

        write.Should().Throw<EmbeddedSqlException>().Which.Message.Should().Be(message);
        ReadRows(connection, "SELECT nextval('s'); SELECT nextval('s');").Should().Equal("5", "6");
    }

    private static string Columns(int count)
        => string.Join(", ", Enumerable.Range(1, count).Select(static index => $"c{index}"));

    private static void AssertSameOutcome(string sql, bool compareColumns = false)
    {
        var managed = RunManaged(sql);
        var sqlite = RunSqlite(sql);

        managed.Error.Should().Be(sqlite.Error, sql);
        managed.Rows.Should().Equal(sqlite.Rows, sql);
        if (compareColumns)
            managed.Columns.Should().Equal(sqlite.Columns, sql);
    }

    private sealed record Outcome(List<string> Columns, List<string> Rows, string? Error);

    private static Outcome RunManaged(string sql)
    {
        var columns = new List<string>();
        var rows = new List<string>();
        try
        {
            using var database = new EmbeddedDatabase();
            using var connection = database.Connect();
            foreach (var statement in connection.PrepareScript(sql))
            {
                using (statement)
                {
                    while (statement.Step() == StatementStepResult.Row)
                    {
                        var values = new string[statement.ColumnCount];
                        for (var column = 0; column < values.Length; column++)
                        {
                            columns.Add(statement.GetColumnName(column));
                            values[column] = FormatManaged(statement.GetValue(column));
                        }

                        rows.Add(string.Join('|', values));
                    }
                }
            }

            return new Outcome(columns, rows, null);
        }
        catch (EmbeddedSqlException exception)
        {
            return new Outcome(columns, rows, exception.Message);
        }
    }

    private static Outcome RunSqlite(string sql)
    {
        var columns = new List<string>();
        var rows = new List<string>();
        try
        {
            using var connection = new MsData.SqliteConnection("Data Source=:memory:;Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            using var reader = command.ExecuteReader();
            do
            {
                while (reader.Read())
                {
                    var values = new string[reader.FieldCount];
                    for (var column = 0; column < values.Length; column++)
                    {
                        columns.Add(reader.GetName(column));
                        values[column] = FormatSqlite(reader, column);
                    }

                    rows.Add(string.Join('|', values));
                }
            }
            while (reader.NextResult());

            return new Outcome(columns, rows, null);
        }
        catch (MsData.SqliteException exception)
        {
            // Microsoft.Data.Sqlite decorates sqlite3_errmsg as "SQLite Error N: 'message'.".
            var match = Regex.Match(exception.Message, "^SQLite Error \\d+: '(?<message>.*)'\\.$", RegexOptions.Singleline);
            return new Outcome(columns, rows, match.Success ? match.Groups["message"].Value : exception.Message);
        }
    }

    private static string FormatManaged(SqlValue value) => value.Kind switch
    {
        SqlValueKind.Null => "NULL",
        SqlValueKind.Integer => value.AsInteger().ToString(CultureInfo.InvariantCulture),
        SqlValueKind.Real => value.AsReal().ToString("R", CultureInfo.InvariantCulture),
        SqlValueKind.Text => value.AsText(),
        SqlValueKind.Blob => "x'" + Convert.ToHexString(value.AsBlob().Span) + "'",
        _ => throw new InvalidOperationException($"Unknown SQL value kind {value.Kind}."),
    };

    private static string FormatSqlite(MsData.SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
            return "NULL";

        return reader.GetValue(ordinal) switch
        {
            long integer => integer.ToString(CultureInfo.InvariantCulture),
            double real => real.ToString("R", CultureInfo.InvariantCulture),
            byte[] bytes => "x'" + Convert.ToHexString(bytes) + "'",
            string text => text,
            var other => Convert.ToString(other, CultureInfo.InvariantCulture) ?? string.Empty,
        };
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        foreach (var statement in connection.PrepareScript(sql))
        {
            using (statement)
            {
                while (statement.Step() == StatementStepResult.Row)
                {
                }
            }
        }
    }

    private static List<string> ReadRows(EmbeddedConnection connection, string sql)
    {
        var rows = new List<string>();
        foreach (var statement in connection.PrepareScript(sql))
        {
            using (statement)
            {
                while (statement.Step() == StatementStepResult.Row)
                {
                    var builder = new StringBuilder();
                    for (var column = 0; column < statement.ColumnCount; column++)
                    {
                        if (column > 0)
                            builder.Append('|');
                        builder.Append(FormatManaged(statement.GetValue(column)));
                    }

                    rows.Add(builder.ToString());
                }
            }
        }

        return rows;
    }
}
