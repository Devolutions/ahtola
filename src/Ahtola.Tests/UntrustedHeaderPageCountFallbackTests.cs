using Ahtola.Core;
using Ahtola.Core.Storage;
using AwesomeAssertions;

namespace Ahtola.Tests;

/// <summary>
/// Regression coverage for a gap in the untrusted-header relaxation from 3309475: when a
/// SQLite database's <c>version-valid-for</c> number does not match its <c>change-counter</c>
/// (or its in-header size is zero), the file format spec says the header's declared page count
/// is merely untrusted, not evidence of corruption -- but the fixed code still stored that raw,
/// untrusted <c>DatabaseSizeInPages</c> value into <c>FileCatalogVersion</c>
/// (<c>EmbeddedDatabase.ReadFileCatalogVersionCore</c> and
/// <c>EmbeddedFileStore.CommittedCatalogVersion</c>/<c>FileCatalogVersion.FromHeader</c>), so
/// <c>PRAGMA page_count</c> (and anything else reading <c>GetPageCount()</c>) could still report
/// a stale or fabricated size instead of the pager's own, always-trustworthy
/// <c>CommittedPageCount</c> computed from the actual file.
/// </summary>
public sealed class UntrustedHeaderPageCountFallbackTests
{
    [Test]
    public void ReportsThePagersRealPageCountWhenTheHeaderSizeIsStaleAndUntrusted()
    {
        var path = Path.Combine(Path.GetTempPath(), $"untrusted-header-{Guid.NewGuid():N}.db");
        try
        {
            uint realPageCount;
            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                Execute(connection, "CREATE TABLE t(a BLOB);");
                // A handful of large rows push the file past a single page so a stale header
                // size could plausibly disagree with the pager's own page count.
                for (var i = 0; i < 20; i++)
                    Execute(connection, "INSERT INTO t(a) VALUES (randomblob(2000));");

                realPageCount = uint.Parse(ReadRows(connection, "PRAGMA page_count;")[0]);
            }

            realPageCount.Should().BeGreaterThan(1, "the test needs a multi-page file to be meaningful");
            // Delete any checkpoint-marker WAL remnant so the reopen below takes the plain
            // "no WAL file at all" path (InitializeCleanWalView) this fix targets, rather than
            // the WAL-recovery checkpoint-marker path, which has its own, deliberately stricter
            // tamper-detection contract (SqlitePagerCheckpointRecoveryVisibilityTests) unrelated
            // to this untrusted-Turso-header scenario.
            DeleteWalRemnants(path);
            CorruptHeaderToUntrustedStaleSize(path, staleSize: 1);

            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                ReadRows(connection, "PRAGMA page_count;").Should().Equal(realPageCount.ToString());
                // The engine must still be able to read every row back -- a truncated page
                // count would silently hide data past the fabricated boundary.
                ReadRows(connection, "SELECT count(*) FROM t;").Should().Equal("20");
            }
        }
        finally
        {
            foreach (var suffix in new[] { string.Empty, "-wal", "-shm", "-journal" })
            {
                var candidate = path + suffix;
                if (File.Exists(candidate))
                    File.Delete(candidate);
            }
        }
    }

    [Test]
    public void ReportsThePagersRealPageCountWhenTheHeaderSizeIsZeroEvenIfCountersMatch()
    {
        var path = Path.Combine(Path.GetTempPath(), $"untrusted-header-zero-{Guid.NewGuid():N}.db");
        try
        {
            uint realPageCount;
            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                Execute(connection, "CREATE TABLE t(a BLOB);");
                for (var i = 0; i < 20; i++)
                    Execute(connection, "INSERT INTO t(a) VALUES (randomblob(2000));");

                realPageCount = uint.Parse(ReadRows(connection, "PRAGMA page_count;")[0]);
            }

            realPageCount.Should().BeGreaterThan(1, "the test needs a multi-page file to be meaningful");
            DeleteWalRemnants(path);
            // A zero size is untrusted per the file format spec even when version-valid-for
            // still matches change-counter (the counters are left alone here).
            CorruptHeaderToZeroSizeWithMatchingCounters(path);

            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                ReadRows(connection, "PRAGMA page_count;").Should().Equal(realPageCount.ToString());
                ReadRows(connection, "SELECT count(*) FROM t;").Should().Equal("20");
            }
        }
        finally
        {
            foreach (var suffix in new[] { string.Empty, "-wal", "-shm", "-journal" })
            {
                var candidate = path + suffix;
                if (File.Exists(candidate))
                    File.Delete(candidate);
            }
        }
    }

    // ---------------------------------------------------------------- fixture corruption

    private static void DeleteWalRemnants(string path)
    {
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            var candidate = path + suffix;
            if (File.Exists(candidate))
                File.Delete(candidate);
        }
    }

    private static void CorruptHeaderToUntrustedStaleSize(string path, uint staleSize)
    {
        var bytes = File.ReadAllBytes(path);
        var header = SqliteDatabaseHeader.Parse(bytes);
        var corrupted = header with
        {
            DatabaseSizeInPages = staleSize,
            // Mismatched on purpose: Turso's own fixed version-valid-for constant (only
            // updated by VACUUM) produces exactly this disagreement on an un-vacuumed
            // Turso-authored database.
            VersionValidFor = unchecked(header.ChangeCounter - 1),
        };
        corrupted.WriteTo(bytes);
        File.WriteAllBytes(path, bytes);
    }

    private static void CorruptHeaderToZeroSizeWithMatchingCounters(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var header = SqliteDatabaseHeader.Parse(bytes);
        var corrupted = header with { DatabaseSizeInPages = 0 };
        corrupted.WriteTo(bytes);
        File.WriteAllBytes(path, bytes);
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
