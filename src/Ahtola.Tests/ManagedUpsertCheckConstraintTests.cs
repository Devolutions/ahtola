using AwesomeAssertions;
using Ahtola.Core;
using MsData = Microsoft.Data.Sqlite;

namespace Ahtola.Tests;

/// <summary>
/// Differential coverage for CHECK constraint enforcement on the UPSERT
/// <c>DO UPDATE</c> branch.
/// </summary>
/// <remarks>
/// The managed engine validated the INSERT candidate and the NOT NULL, UNIQUE,
/// PRIMARY KEY and FOREIGN KEY constraints of the DO UPDATE result, but never
/// evaluated the table's CHECK constraints against that result. A conflicting
/// insert whose DO UPDATE assignment produced a violating value therefore stored
/// a row that contradicted its own declared schema.
///
/// Every expectation below was verified against real SQLite before the fix, and
/// each case runs against both engines so the two cannot drift. The observed
/// constraint order is NOT NULL, then CHECK, then UNIQUE, identical for a plain
/// UPDATE and for the DO UPDATE branch.
/// </remarks>
public sealed class ManagedUpsertCheckConstraintTests
{
    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        while (statement.Step() == StatementStepResult.Row)
        {
        }
    }

    private static void Execute(MsData.SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static IReadOnlyList<string> Read(EmbeddedConnection connection, string sql)
    {
        var rows = new List<string>();
        using var statement = connection.Prepare(sql);
        while (statement.Step() == StatementStepResult.Row)
        {
            var row = new List<string>();
            for (var index = 0; index < statement.ColumnCount; index++)
            {
                var value = statement.GetValue(index);
                row.Add(value.Kind switch
                {
                    SqlValueKind.Null => "NULL",
                    SqlValueKind.Integer => value.AsInteger().ToString(),
                    _ => value.AsText(),
                });
            }

            rows.Add(string.Join(",", row));
        }

        return rows;
    }

    private static IReadOnlyList<string> Read(MsData.SqliteConnection connection, string sql)
    {
        var rows = new List<string>();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var row = new List<string>();
            for (var index = 0; index < reader.FieldCount; index++)
            {
                row.Add(reader.IsDBNull(index) ? "NULL" : reader.GetValue(index).ToString() ?? "?");
            }

            rows.Add(string.Join(",", row));
        }

        return rows;
    }

    /// <summary>
    /// Runs <paramref name="subject"/> against both engines and returns the managed
    /// error message, asserting first that both engines agree on whether the
    /// statement failed, on the message, and on the resulting table contents.
    /// </summary>
    private static string? AssertBothEnginesAgree(string[] setup, string subject, string readback)
    {
        using var database = new EmbeddedDatabase();
        using var managed = database.Connect();
        using var sqlite = new MsData.SqliteConnection("Data Source=:memory:");
        sqlite.Open();

        foreach (var statement in setup)
        {
            Execute(managed, statement);
            Execute(sqlite, statement);
        }

        var managedError = CaptureError(() => Execute(managed, subject));
        var sqliteError = CaptureError(() => Execute(sqlite, subject));

        managedError.Should().Be(sqliteError);
        Read(managed, readback).Should().Equal(Read(sqlite, readback));
        return managedError;
    }

    private static string? CaptureError(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception exception)
        {
            return exception.Message
                .Replace("SQLite Error 19: ", string.Empty)
                .Replace("SQLite Error 1: ", string.Empty)
                .Trim('\'', '.', ' ');
        }
    }

    [Test]
    public void ALiteralDoUpdateAssignmentThatViolatesAColumnCheckIsRejected()
    {
        var error = AssertBothEnginesAgree(
            ["CREATE TABLE t(id INTEGER PRIMARY KEY, v INTEGER CHECK(v > 0))", "INSERT INTO t VALUES(1,10)"],
            "INSERT INTO t VALUES(1,20) ON CONFLICT(id) DO UPDATE SET v = -5",
            "SELECT id,v FROM t");

        error.Should().Be("CHECK constraint failed: v > 0");
    }

    [Test]
    public void ADoUpdateAssignmentDerivedFromExcludedIsValidatedAfterEvaluation()
    {
        // The INSERT candidate (v = 5) satisfies the constraint on its own, so this
        // fails only if the DO UPDATE *result* is validated.
        var error = AssertBothEnginesAgree(
            ["CREATE TABLE t(id INTEGER PRIMARY KEY, v INTEGER CHECK(v > 0))", "INSERT INTO t VALUES(1,10)"],
            "INSERT INTO t VALUES(1,5) ON CONFLICT(id) DO UPDATE SET v = excluded.v - 10",
            "SELECT id,v FROM t");

        error.Should().Be("CHECK constraint failed: v > 0");
    }

    [Test]
    public void ADoUpdateAssignmentDerivedFromTheStoredRowIsValidated()
    {
        var error = AssertBothEnginesAgree(
            ["CREATE TABLE t(id INTEGER PRIMARY KEY, v INTEGER CHECK(v > 0))", "INSERT INTO t VALUES(1,10)"],
            "INSERT INTO t VALUES(1,5) ON CONFLICT(id) DO UPDATE SET v = v - 100",
            "SELECT id,v FROM t");

        error.Should().Be("CHECK constraint failed: v > 0");
    }

    [Test]
    public void ATableLevelCheckIsValidatedAgainstTheDoUpdateResult()
    {
        var error = AssertBothEnginesAgree(
            ["CREATE TABLE t(id INTEGER PRIMARY KEY, a INT, b INT, CHECK(a < b))", "INSERT INTO t VALUES(1,1,2)"],
            "INSERT INTO t VALUES(1,1,2) ON CONFLICT(id) DO UPDATE SET a = 9, b = 1",
            "SELECT id,a,b FROM t");

        error.Should().Be("CHECK constraint failed: a < b");
    }

    [Test]
    public void ANamedCheckConstraintReportsItsName()
    {
        var error = AssertBothEnginesAgree(
            ["CREATE TABLE t(id INTEGER PRIMARY KEY, v INTEGER CONSTRAINT ck CHECK(v > 0))", "INSERT INTO t VALUES(1,10)"],
            "INSERT INTO t VALUES(1,5) ON CONFLICT(id) DO UPDATE SET v = -5",
            "SELECT id,v FROM t");

        error.Should().Be("CHECK constraint failed: ck");
    }

    [Test]
    public void AGeneratedColumnCheckIsValidatedAfterRecomputation()
    {
        var error = AssertBothEnginesAgree(
            ["CREATE TABLE t(id INTEGER PRIMARY KEY, v INT, g INT AS (v*2) CHECK(g < 100))", "INSERT INTO t(id,v) VALUES(1,1)"],
            "INSERT INTO t(id,v) VALUES(1,2) ON CONFLICT(id) DO UPDATE SET v = 500",
            "SELECT id,v,g FROM t");

        error.Should().Be("CHECK constraint failed: g < 100");
    }

    [Test]
    public void NotNullIsReportedBeforeCheck()
    {
        var error = AssertBothEnginesAgree(
            ["CREATE TABLE t(id INTEGER PRIMARY KEY, v INT CHECK(v > 0), w INT NOT NULL)", "INSERT INTO t VALUES(1,10,100)"],
            "INSERT INTO t VALUES(1,7,7) ON CONFLICT(id) DO UPDATE SET v = -5, w = NULL",
            "SELECT id,v,w FROM t");

        error.Should().Be("NOT NULL constraint failed: t.w");
    }

    [Test]
    public void CheckIsReportedBeforeUnique()
    {
        var error = AssertBothEnginesAgree(
            ["CREATE TABLE t(id INTEGER PRIMARY KEY, v INT CHECK(v > 0), w INT UNIQUE)", "INSERT INTO t VALUES(1,10,100),(2,20,200)"],
            "INSERT INTO t VALUES(1,7,7) ON CONFLICT(id) DO UPDATE SET v = -5, w = 200",
            "SELECT id,v,w FROM t ORDER BY id");

        error.Should().Be("CHECK constraint failed: v > 0");
    }

    [Test]
    [TestCase("OR IGNORE")]
    [TestCase("OR REPLACE")]
    public void AnInsertConflictAlgorithmPrefixDoesNotSuppressTheCheckFailure(string prefix)
    {
        var error = AssertBothEnginesAgree(
            ["CREATE TABLE t(id INTEGER PRIMARY KEY, v INTEGER CHECK(v > 0))", "INSERT INTO t VALUES(1,10)"],
            $"INSERT {prefix} INTO t VALUES(1,7) ON CONFLICT(id) DO UPDATE SET v = -5",
            "SELECT id,v FROM t");

        error.Should().Be("CHECK constraint failed: v > 0");
    }

    [Test]
    public void AFalseDoUpdatePredicateSkipsTheCheckEntirely()
    {
        var error = AssertBothEnginesAgree(
            ["CREATE TABLE t(id INTEGER PRIMARY KEY, v INTEGER CHECK(v > 0))", "INSERT INTO t VALUES(1,10)"],
            "INSERT INTO t VALUES(1,7) ON CONFLICT(id) DO UPDATE SET v = -5 WHERE 0",
            "SELECT id,v FROM t");

        error.Should().BeNull();
    }

    [Test]
    public void AValidDoUpdateStillSucceeds()
    {
        var error = AssertBothEnginesAgree(
            ["CREATE TABLE t(id INTEGER PRIMARY KEY, v INTEGER CHECK(v BETWEEN 0 AND 100))", "INSERT INTO t VALUES(1,50)"],
            "INSERT INTO t VALUES(1,60) ON CONFLICT(id) DO UPDATE SET v = 70",
            "SELECT id,v FROM t");

        error.Should().BeNull();
    }

    [Test]
    public void AnUpdateToAColumnUnrelatedToTheCheckStillSucceeds()
    {
        var error = AssertBothEnginesAgree(
            ["CREATE TABLE t(id INTEGER PRIMARY KEY, a INT, b INT CHECK(b > 0))", "INSERT INTO t VALUES(1,1,5)"],
            "INSERT INTO t VALUES(1,9,9) ON CONFLICT(id) DO UPDATE SET a = 99",
            "SELECT id,a,b FROM t");

        error.Should().BeNull();
    }

    [Test]
    public void AFailedCheckRollsBackEveryPrecedingCandidateInAMultiRowUpsert()
    {
        var error = AssertBothEnginesAgree(
            ["CREATE TABLE t(id INTEGER PRIMARY KEY, v INTEGER CHECK(v > 0))", "INSERT INTO t VALUES(1,10),(2,20)"],
            "INSERT INTO t VALUES(1,5),(2,5) ON CONFLICT(id) DO UPDATE SET v = excluded.v - 10",
            "SELECT id,v FROM t ORDER BY id");

        error.Should().Be("CHECK constraint failed: v > 0");
    }

    [Test]
    public void AnUpdateTriggerDoesNotFireWhenTheCheckFails()
    {
        using var database = new EmbeddedDatabase();
        using var managed = database.Connect();
        using var sqlite = new MsData.SqliteConnection("Data Source=:memory:");
        sqlite.Open();

        string[] setup =
        [
            "CREATE TABLE t(id INTEGER PRIMARY KEY, v INTEGER CHECK(v > 0))",
            "CREATE TABLE log(m TEXT)",
            "INSERT INTO t VALUES(1,10)",
            "CREATE TRIGGER tr AFTER UPDATE ON t BEGIN INSERT INTO log VALUES('fired'); END",
        ];

        foreach (var statement in setup)
        {
            Execute(managed, statement);
            Execute(sqlite, statement);
        }

        const string Subject = "INSERT INTO t VALUES(1,7) ON CONFLICT(id) DO UPDATE SET v = -5";
        CaptureError(() => Execute(managed, Subject)).Should().Be("CHECK constraint failed: v > 0");
        CaptureError(() => Execute(sqlite, Subject)).Should().Be("CHECK constraint failed: v > 0");

        Read(managed, "SELECT count(*) FROM log").Should().Equal(["0"]);
        Read(sqlite, "SELECT count(*) FROM log").Should().Equal(["0"]);
    }

    [Test]
    public void AFailedCheckLeavesAFileBackedDatabaseIntact()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "managed-upsert-check-tests");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"upsert-check-{Guid.NewGuid():N}.db");
        try
        {
            using (var connection = OpenManagedFile(path))
            {
                ExecuteFacade(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, v INTEGER CHECK(v > 0))");
                ExecuteFacade(connection, "INSERT INTO t VALUES(1,10)");
                CaptureError(() => ExecuteFacade(
                        connection,
                        "INSERT INTO t VALUES(1,5) ON CONFLICT(id) DO UPDATE SET v = -5"))
                    .Should().Be("CHECK constraint failed: v > 0");
            }

            using (var connection = OpenManagedFile(path))
            {
                ReadFacade(connection, "SELECT id,v FROM t").Should().Equal(["1,10"]);
                ReadFacade(connection, "PRAGMA integrity_check").Should().Equal(["ok"]);
            }
        }
        finally
        {
            Ahtola.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
            {
                var file = path + suffix;
                if (File.Exists(file))
                    File.Delete(file);
            }
        }
    }

    private static Ahtola.Data.Sqlite.SqliteConnection OpenManagedFile(string path)
    {
        var connection = new Ahtola.Data.Sqlite.SqliteConnection(
            $"Data Source={path};Pooling=False;Local Provider=Managed");
        connection.Open();
        return connection;
    }

    /// <summary>
    /// A <c>DO UPDATE</c> that reassigns the INTEGER PRIMARY KEY must have its CHECK
    /// constraints evaluated against the rowid the row is moving <em>to</em>.
    /// </summary>
    /// <remarks>
    /// CHECK validation and rowid movement were introduced on separate branches and
    /// merged without textual conflict, but the CHECK call still passed the
    /// conflicting row's original rowid. A bare <c>rowid</c> reference in a CHECK
    /// expression therefore saw the pre-move value and the violating row was stored.
    /// Verified against real SQLite: the move to 500 is rejected. This is the only
    /// one of the three cases below that discriminates: reverting the argument to
    /// the pre-move rowid fails this test and no other.
    /// </remarks>
    [Test]
    public void ADoUpdateThatMovesTheRowidValidatesChecksAgainstTheNewRowid()
    {
        var error = AssertBothEnginesAgree(
            [
                "CREATE TABLE t(id INTEGER PRIMARY KEY, u INTEGER UNIQUE, CHECK(rowid < 100))",
                "INSERT INTO t VALUES(1,10)",
            ],
            "INSERT INTO t VALUES(5,10) ON CONFLICT(u) DO UPDATE SET id = 500",
            "SELECT id,u FROM t");

        error.Should().Be("CHECK constraint failed: rowid < 100");
    }

    /// <summary>
    /// The same reassignment expressed through the INTEGER PRIMARY KEY's declared
    /// column name.
    /// </summary>
    /// <remarks>
    /// This case passed both before and after the rowid fix, because a named
    /// INTEGER PRIMARY KEY is read out of the row image, which already carries the
    /// assigned value. It is kept as a guard rather than as a discriminator: it
    /// pins the named-column spelling to the same outcome as the bare
    /// <c>rowid</c> spelling above, which is the only one that observed the stale
    /// identity.
    /// </remarks>
    [Test]
    public void ADoUpdateThatMovesTheRowidValidatesChecksNamingThatColumn()
    {
        var error = AssertBothEnginesAgree(
            [
                "CREATE TABLE t(id INTEGER PRIMARY KEY, u INTEGER UNIQUE, CHECK(id < 100))",
                "INSERT INTO t VALUES(1,10)",
            ],
            "INSERT INTO t VALUES(5,10) ON CONFLICT(u) DO UPDATE SET id = 500",
            "SELECT id,u FROM t");

        error.Should().Be("CHECK constraint failed: id < 100");
    }

    /// <summary>
    /// The complement: a rowid move that satisfies the CHECK must still be applied,
    /// so the stricter validation does not reject legal movement.
    /// </summary>
    [Test]
    public void ADoUpdateThatMovesTheRowidWithinTheCheckRangeStillApplies()
    {
        var error = AssertBothEnginesAgree(
            [
                "CREATE TABLE t(id INTEGER PRIMARY KEY, u INTEGER UNIQUE, CHECK(rowid < 100))",
                "INSERT INTO t VALUES(1,10)",
            ],
            "INSERT INTO t VALUES(5,10) ON CONFLICT(u) DO UPDATE SET id = 50",
            "SELECT id,u,rowid FROM t");

        error.Should().BeNull();
    }

    private static void ExecuteFacade(Ahtola.Data.Sqlite.SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static IReadOnlyList<string> ReadFacade(Ahtola.Data.Sqlite.SqliteConnection connection, string sql)
    {
        var rows = new List<string>();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var row = new List<string>();
            for (var index = 0; index < reader.FieldCount; index++)
            {
                row.Add(reader.IsDBNull(index) ? "NULL" : reader.GetValue(index).ToString() ?? "?");
            }

            rows.Add(string.Join(",", row));
        }

        return rows;
    }

    // A DO UPDATE SET / WHERE expression may only reference the target table and
    // the excluded row; a column qualified by any other table is an unresolvable
    // column reference, not a missing table. SQLite/Turso emit "no such column".
    [Test]
    public void ADoUpdateSetReferencingAnOutOfScopeTableColumnReportsNoSuchColumn()
    {
        var error = AssertBothEnginesAgree(
            [
                "CREATE TABLE t(id INTEGER PRIMARY KEY, val INTEGER)",
                "CREATE TABLE other(id INTEGER PRIMARY KEY, val INTEGER)",
                "INSERT INTO t VALUES(1,10)",
            ],
            "INSERT INTO t VALUES(1,20) ON CONFLICT(id) DO UPDATE SET val = other.val",
            "SELECT id,val FROM t");

        error.Should().Contain("no such column");
    }

    [Test]
    public void ADoUpdateWhereReferencingAnOutOfScopeTableColumnReportsNoSuchColumn()
    {
        var error = AssertBothEnginesAgree(
            [
                "CREATE TABLE t(id INTEGER PRIMARY KEY, val INTEGER)",
                "CREATE TABLE other(id INTEGER PRIMARY KEY, val INTEGER)",
                "INSERT INTO t VALUES(1,10)",
            ],
            "INSERT INTO t VALUES(1,20) ON CONFLICT(id) DO UPDATE SET val = excluded.val WHERE other.val > 0",
            "SELECT id,val FROM t");

        error.Should().Contain("no such column");
    }
}
