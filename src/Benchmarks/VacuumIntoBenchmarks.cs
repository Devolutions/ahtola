using System.Globalization;
using System.Text;
using Ahtola;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using MicrosoftSqlite = Microsoft.Data.Sqlite;

namespace Benchmarks;

/// <summary>
/// Focused <c>VACUUM INTO</c> comparison between the managed Ahtola engine and
/// the native SQLite provider over an identical, deterministic source image.
/// </summary>
/// <remarks>
/// <para>
/// The source image is generated once in <see cref="GlobalSetup"/> (outside the
/// measured region) and only ever copied afterwards, so both engines vacuum
/// byte-identical inputs. Each iteration gets a fresh private copy plus a fresh
/// destination path because <c>VACUUM INTO</c> refuses a non-empty destination.
/// </para>
/// <para>
/// Two categories are reported. <c>statement</c> times only the
/// <c>VACUUM INTO</c> statement on an already-open connection, which is how a
/// long-lived application issues it. <c>end-to-end</c> also includes opening the
/// connection, which is the conservative number: the managed engine materializes
/// and validates the whole catalog on open, whereas native SQLite opens lazily
/// and reads the b-tree during the vacuum itself.
/// </para>
/// </remarks>
[MemoryDiagnoser]
[CategoriesColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
public class VacuumIntoBenchmarks
{
    private const string StatementCategory = "statement";
    private const string EndToEndCategory = "end-to-end";

    /// <summary>Deterministic seed so every run generates the same source image.</summary>
    private const int FixtureSeed = 20260819;

    private string _root = string.Empty;
    private string _pristineSourcePath = string.Empty;
    private string _managedSourcePath = string.Empty;
    private string _nativeSourcePath = string.Empty;
    private string _managedStatementSourcePath = string.Empty;
    private string _nativeStatementSourcePath = string.Empty;
    private string _managedDestinationPath = string.Empty;
    private string _nativeDestinationPath = string.Empty;
    private string _managedStatementDestinationPath = string.Empty;
    private string _nativeStatementDestinationPath = string.Empty;
    private AhtolaConnection? _managedStatementConnection;
    private MicrosoftSqlite.SqliteConnection? _nativeStatementConnection;
    private long _iteration;

    /// <summary>Row count tuned so the source image lands near 2.6 MB at 4 KiB pages.</summary>
    [Params(9700)]
    public int RowCount { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        _root = Path.Combine(Path.GetTempPath(), "ahtola-vacuum-bench-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _pristineSourcePath = Path.Combine(_root, "source.db");

        BuildDeterministicFixture(_pristineSourcePath, RowCount);

        var length = new FileInfo(_pristineSourcePath).Length;
        Console.WriteLine(
            $"[fixture] rows={RowCount} bytes={length} "
            + $"({(length / 1024d / 1024d).ToString("F2", CultureInfo.InvariantCulture)} MiB) "
            + DescribeImage(_pristineSourcePath));
    }

    [IterationSetup]
    public void IterationSetup()
    {
        _iteration++;
        var stamp = _iteration.ToString(CultureInfo.InvariantCulture);
        _managedSourcePath = Path.Combine(_root, $"managed-source-{stamp}.db");
        _nativeSourcePath = Path.Combine(_root, $"native-source-{stamp}.db");
        _managedStatementSourcePath = Path.Combine(_root, $"managed-stmt-source-{stamp}.db");
        _nativeStatementSourcePath = Path.Combine(_root, $"native-stmt-source-{stamp}.db");
        _managedDestinationPath = Path.Combine(_root, $"managed-out-{stamp}.db");
        _nativeDestinationPath = Path.Combine(_root, $"native-out-{stamp}.db");
        _managedStatementDestinationPath = Path.Combine(_root, $"managed-stmt-out-{stamp}.db");
        _nativeStatementDestinationPath = Path.Combine(_root, $"native-stmt-out-{stamp}.db");

        foreach (var target in new[]
                 {
                     _managedSourcePath,
                     _nativeSourcePath,
                     _managedStatementSourcePath,
                     _nativeStatementSourcePath,
                 })
        {
            File.Copy(_pristineSourcePath, target, overwrite: true);
        }

        foreach (var target in new[]
                 {
                     _managedDestinationPath,
                     _nativeDestinationPath,
                     _managedStatementDestinationPath,
                     _nativeStatementDestinationPath,
                 })
        {
            DeleteDatabaseFiles(target);
        }

        // Connections for the statement-only cases are opened outside the
        // measured region so those numbers isolate the VACUUM INTO work.
        _managedStatementConnection = new AhtolaConnection($"Data Source={_managedStatementSourcePath}");
        _managedStatementConnection.Open();
        _nativeStatementConnection = new MicrosoftSqlite.SqliteConnection(
            $"Data Source={_nativeStatementSourcePath}");
        _nativeStatementConnection.Open();
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        _managedStatementConnection?.Dispose();
        _managedStatementConnection = null;
        _nativeStatementConnection?.Dispose();
        _nativeStatementConnection = null;
        MicrosoftSqlite.SqliteConnection.ClearAllPools();

        foreach (var path in new[]
                 {
                     _managedSourcePath,
                     _nativeSourcePath,
                     _managedStatementSourcePath,
                     _nativeStatementSourcePath,
                     _managedDestinationPath,
                     _nativeDestinationPath,
                     _managedStatementDestinationPath,
                     _nativeStatementDestinationPath,
                 })
        {
            DeleteDatabaseFiles(path);
        }
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: benchmark scratch space lives under the OS temp directory.
        }
    }

    [BenchmarkCategory(StatementCategory)]
    [Benchmark(Baseline = true, Description = "native SQLite VACUUM INTO (statement)")]
    public long NativeVacuumIntoStatement()
        => VacuumNative(_nativeStatementConnection!, _nativeStatementDestinationPath);

    [BenchmarkCategory(StatementCategory)]
    [Benchmark(Description = "managed Ahtola VACUUM INTO (statement)")]
    public long ManagedVacuumIntoStatement()
        => VacuumManaged(_managedStatementConnection!, _managedStatementDestinationPath);

    [BenchmarkCategory(EndToEndCategory)]
    [Benchmark(Baseline = true, Description = "native SQLite open + VACUUM INTO")]
    public long NativeOpenAndVacuumInto()
    {
        using var connection = new MicrosoftSqlite.SqliteConnection($"Data Source={_nativeSourcePath}");
        connection.Open();
        return VacuumNative(connection, _nativeDestinationPath);
    }

    [BenchmarkCategory(EndToEndCategory)]
    [Benchmark(Description = "managed Ahtola open + VACUUM INTO")]
    public long ManagedOpenAndVacuumInto()
    {
        using var connection = new AhtolaConnection($"Data Source={_managedSourcePath}");
        connection.Open();
        return VacuumManaged(connection, _managedDestinationPath);
    }

    private static long VacuumNative(MicrosoftSqlite.SqliteConnection connection, string destinationPath)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "VACUUM INTO $target;";
        command.Parameters.AddWithValue("$target", destinationPath);
        command.ExecuteNonQuery();
        return new FileInfo(destinationPath).Length;
    }

    private static long VacuumManaged(AhtolaConnection connection, string destinationPath)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "VACUUM INTO $target;";
        var target = command.CreateParameter();
        target.ParameterName = "$target";
        target.Value = destinationPath;
        command.Parameters.Add(target);
        command.ExecuteNonQuery();
        return new FileInfo(destinationPath).Length;
    }

    /// <summary>
    /// Builds the shared source image with the managed engine so both engines
    /// start from one byte-identical on-disk file, then fragments it with
    /// deletes so VACUUM actually has free space to reclaim.
    /// </summary>
    /// <remarks>
    /// The managed engine currently rejects some index-interior page layouts
    /// that native SQLite emits after deletes ("untracked free gap"), so the
    /// fixture is authored managed-side; native SQLite reads it back fine,
    /// which keeps the comparison on one identical input.
    /// </remarks>
    private static void BuildDeterministicFixture(string path, int rowCount)
    {
        DeleteDatabaseFiles(path);
        using (var connection = new AhtolaConnection($"Data Source={path}"))
        {
            connection.Open();
            Execute(connection, "PRAGMA page_size = 4096;");
            Execute(connection, "PRAGMA journal_mode = delete;");
            Execute(
                connection,
                """
                CREATE TABLE docs(
                    id INTEGER PRIMARY KEY,
                    bucket INTEGER NOT NULL,
                    label TEXT NOT NULL,
                    payload TEXT NOT NULL,
                    score REAL NOT NULL);
                """);
            Execute(connection, "CREATE INDEX docs_bucket ON docs(bucket, label);");
            Execute(connection, "CREATE INDEX docs_score ON docs(score);");

            var random = new Random(FixtureSeed);
            using (var transaction = connection.BeginTransaction())
            {
                using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText =
                    "INSERT INTO docs(id, bucket, label, payload, score) VALUES ($id, $bucket, $label, $payload, $score);";
                var id = AddParameter(insert, "$id");
                var bucket = AddParameter(insert, "$bucket");
                var label = AddParameter(insert, "$label");
                var payload = AddParameter(insert, "$payload");
                var score = AddParameter(insert, "$score");
                for (var row = 1; row <= rowCount; row++)
                {
                    id.Value = (long)row;
                    bucket.Value = (long)(row % 64);
                    label.Value = "label-" + row.ToString("D7", CultureInfo.InvariantCulture);
                    payload.Value = BuildPayload(random, row);
                    score.Value = Math.Round(random.NextDouble() * 1000d, 6);
                    insert.ExecuteNonQuery();
                }

                transaction.Commit();
            }

            // Fragment the image so the vacuumed output is measurably more
            // compact than the source: delete a deterministic 40% of rows spread
            // across the whole key space, leaving free pages and partial leaves.
            Execute(connection, "DELETE FROM docs WHERE id % 5 IN (1, 3);");
        }

        DeleteSidecars(path);
    }

    private static System.Data.Common.DbParameter AddParameter(System.Data.Common.DbCommand command, string name)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        command.Parameters.Add(parameter);
        return parameter;
    }

    private static string BuildPayload(Random random, int row)
    {
        // Deterministic, low-entropy-but-varied text: representative of real row
        // payloads without being compressible into a degenerate page layout.
        var length = 140 + random.Next(0, 80);
        var builder = new StringBuilder(length + 24);
        builder.Append("row:").Append(row.ToString("D7", CultureInfo.InvariantCulture)).Append('|');
        const string Alphabet = "abcdefghijklmnopqrstuvwxyz0123456789-_";
        for (var index = 0; index < length; index++)
            builder.Append(Alphabet[random.Next(Alphabet.Length)]);

        return builder.ToString();
    }

    private static void Execute(System.Data.Common.DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void DeleteSidecars(string path)
    {
        foreach (var suffix in new[] { "-wal", "-journal", "-shm" })
        {
            try
            {
                var candidate = path + suffix;
                if (File.Exists(candidate))
                    File.Delete(candidate);
            }
            catch (IOException)
            {
                // The fixture image itself is authoritative; sidecars are rebuilt on open.
            }
        }
    }

    private static string DescribeImage(string path)
    {
        using var connection = new MicrosoftSqlite.SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        connection.Open();
        var pageSize = ScalarLong(connection, "PRAGMA page_size;");
        var pageCount = ScalarLong(connection, "PRAGMA page_count;");
        var freelist = ScalarLong(connection, "PRAGMA freelist_count;");
        return $"page_size={pageSize} page_count={pageCount} freelist_count={freelist}";
    }

    private static long ScalarLong(MicrosoftSqlite.SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void DeleteDatabaseFiles(string path)
    {
        if (string.IsNullOrEmpty(path))
            return;

        foreach (var suffix in new[] { "", "-wal", "-journal", "-shm" })
        {
            try
            {
                var candidate = path + suffix;
                if (File.Exists(candidate))
                    File.Delete(candidate);
            }
            catch (IOException)
            {
                // Stale scratch artifacts do not affect correctness of the next iteration.
            }
        }
    }
}
