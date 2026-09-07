using System.Text;
using Ahtola.Core;
using AwesomeAssertions;

namespace Ahtola.Tests;

/// <summary>
/// Regression coverage for a gap found reviewing 3309475's legacy-broken-view tolerance: the
/// <see cref="ViewDefinition.BrokenReason"/> guard was only checked by
/// <c>EmbeddedDatabase.TryGetView</c> (the ordinary SELECT-side lookup). The three INSTEAD OF
/// trigger DML paths -- <c>PerformInsteadOfInsert</c>/<c>PerformInsteadOfUpdate</c>/
/// <c>PerformInsteadOfDelete</c> in EmbeddedDatabase.Triggers.cs -- looked up
/// <c>context.Views</c> directly instead, so they could resolve a broken view's placeholder
/// query ("SELECT 1 WHERE 0") and report DML success instead of failing closed, even though a
/// direct SELECT against the same view already failed with "could not be loaded". This
/// silently ran (and reported success for) an INSERT/UPDATE/DELETE that never actually reached
/// the trigger body's real INSERT/UPDATE/DELETE into the base table, because the broken view
/// produced zero rows for its own placeholder query.
/// </summary>
/// <remarks>
/// The three DML paths now go through the same <c>TryGetView</c> helper as the SELECT path
/// (fixed alongside <c>ResolveViewColumns</c>, the shape-resolution primitive most of the view
/// machinery funnels through, so future call sites cannot reintroduce the gap by adding a new
/// direct <c>context.Views.TryGetValue</c> lookup that forgets the check).
///
/// A broken view row only exists once persisted and reopened (<c>ManagedSchemaAdoptionMode.Load</c>
/// tolerates unparseable stored SQL; a freshly executed CREATE VIEW never does, since it is
/// parsed before being written at all), so this test creates a private file database with a
/// normal, valid view and its INSTEAD OF triggers, closes it, corrupts the view's persisted SQL
/// text on disk with an equal-length, byte-for-byte substitution (preserving every b-tree
/// structure and payload length so nothing else about the file changes), and reopens it.
/// </remarks>
public sealed class InsteadOfTriggerOnBrokenStoredViewTests
{
    [Test]
    public void InsteadOfInsertOnABrokenStoredViewFailsClosedInsteadOfSilentlySucceeding()
    {
        var path = CreateBrokenViewFixture(
            "CREATE TRIGGER tr_ins INSTEAD OF INSERT ON v BEGIN INSERT INTO t(a) VALUES (new.a); END;");
        try
        {
            using var database = EmbeddedDatabase.OpenFile(path);
            using var connection = database.Connect();

            Action insert = () => Execute(connection, "INSERT INTO v(a) VALUES (1);");

            insert.Should().Throw<EmbeddedSqlException>().WithMessage("*could not be loaded*");
            // A silent "success" would have run the trigger body and inserted into t.
            ReadRows(connection, "SELECT a FROM t;").Should().BeEmpty();
        }
        finally
        {
            DeleteDatabaseFiles(path);
        }
    }

    [Test]
    public void InsteadOfUpdateOnABrokenStoredViewFailsClosedInsteadOfSilentlySucceeding()
    {
        var path = CreateBrokenViewFixture(
            "CREATE TRIGGER tr_upd INSTEAD OF UPDATE ON v BEGIN UPDATE t SET a = new.a WHERE a = old.a; END;",
            seedRow: true);
        try
        {
            using var database = EmbeddedDatabase.OpenFile(path);
            using var connection = database.Connect();

            Action update = () => Execute(connection, "UPDATE v SET a = 2 WHERE a = 1;");

            update.Should().Throw<EmbeddedSqlException>().WithMessage("*could not be loaded*");
            // A silent "success" would have changed the seeded row to 2.
            ReadRows(connection, "SELECT a FROM t;").Should().Equal("1");
        }
        finally
        {
            DeleteDatabaseFiles(path);
        }
    }

    [Test]
    public void InsteadOfDeleteOnABrokenStoredViewFailsClosedInsteadOfSilentlySucceeding()
    {
        var path = CreateBrokenViewFixture(
            "CREATE TRIGGER tr_del INSTEAD OF DELETE ON v BEGIN DELETE FROM t WHERE a = old.a; END;",
            seedRow: true);
        try
        {
            using var database = EmbeddedDatabase.OpenFile(path);
            using var connection = database.Connect();

            Action delete = () => Execute(connection, "DELETE FROM v WHERE a = 1;");

            delete.Should().Throw<EmbeddedSqlException>().WithMessage("*could not be loaded*");
            // A silent "success" would have removed the seeded row.
            ReadRows(connection, "SELECT a FROM t;").Should().Equal("1");
        }
        finally
        {
            DeleteDatabaseFiles(path);
        }
    }

    // ---------------------------------------------------------------- fixture construction

    private const string OriginalViewSelect = "SELECT a FROM t";
    private const string CorruptedViewSelect = "SELEQT a FROM t";

    /// <summary>
    /// Builds a private file database with table <c>t(a)</c>, view <c>v(a) AS SELECT a FROM t</c>,
    /// and the given INSTEAD OF trigger, then corrupts the view's persisted SQL on disk (an
    /// equal-length substitution of its SELECT keyword, guaranteed to fail reparsing) so it loads
    /// as broken on the next open.
    /// </summary>
    private static string CreateBrokenViewFixture(string triggerSql, bool seedRow = false)
    {
        var path = Path.Combine(Path.GetTempPath(), $"instead-of-broken-view-{Guid.NewGuid():N}.db");
        using (var database = EmbeddedDatabase.OpenFile(path))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE t(a INTEGER);");
            Execute(connection, $"CREATE VIEW v(a) AS {OriginalViewSelect};");
            Execute(connection, triggerSql);
            if (seedRow)
                Execute(connection, "INSERT INTO t(a) VALUES (1);");
        }

        CorruptStoredViewSql(path);
        return path;
    }

    /// <summary>
    /// Rewrites the on-disk bytes of the database file, replacing every occurrence of the
    /// view's original SELECT text with an equal-length, unparseable substitute. The
    /// replacement is byte-for-byte the same length, so every surrounding b-tree cell, varint
    /// payload length, and page offset stays valid -- only the view's own stored SQL becomes
    /// unparseable, exactly like the vendored legacy-unquoted-view-columns fixture's own
    /// pre-corrupted row.
    /// </summary>
    private static void CorruptStoredViewSql(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var original = Encoding.UTF8.GetBytes(OriginalViewSelect);
        var corrupted = Encoding.UTF8.GetBytes(CorruptedViewSelect);
        original.Length.Should().Be(corrupted.Length);

        var replacements = 0;
        for (var index = 0; index <= bytes.Length - original.Length; index++)
        {
            if (!bytes.AsSpan(index, original.Length).SequenceEqual(original))
                continue;

            corrupted.CopyTo(bytes.AsSpan(index));
            replacements++;
        }

        // Exactly one occurrence: the view's own CREATE VIEW text. The trigger body never
        // repeats the view's SELECT, so this also verifies the corruption did not accidentally
        // clobber trigger or index SQL sharing the same substring.
        replacements.Should().Be(1);
        File.WriteAllBytes(path, bytes);
    }

    private static void DeleteDatabaseFiles(string path)
    {
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm", "-journal" })
        {
            var candidate = path + suffix;
            if (File.Exists(candidate))
                File.Delete(candidate);
        }
    }

    // ---------------------------------------------------------------- helpers

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step();
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
                    SqlValueKind.Integer => value.AsInteger().ToString(System.Globalization.CultureInfo.InvariantCulture),
                    SqlValueKind.Real => value.AsReal().ToString(System.Globalization.CultureInfo.InvariantCulture),
                    _ => value.AsText(),
                });
            }

            rows.Add(string.Join("|", values));
        }

        return [.. rows];
    }
}
