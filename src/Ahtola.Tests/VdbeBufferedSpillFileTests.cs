using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Execution;
using Ahtola.Core.Storage;
using Ahtola.Data.Sqlite;

namespace Ahtola.Tests;

/// <summary>
/// The sorter's spill writer and run readers coalesce the record codec's per-value file
/// operations. Their bytes must be identical to unbuffered writes for every codec write
/// shape (reserved length headers patched afterwards, buffers filling mid-record, records
/// larger than the buffer, reads of not-yet-flushed bytes, rollback truncation), and a large
/// spilled GROUP BY must stay roughly linear instead of issuing several writes per value.
/// </summary>
public sealed class VdbeBufferedSpillFileTests
{
    [TestCase(16)]
    [TestCase(64)]
    [TestCase(4096)]
    public void BufferedCodecWritesMatchUnbufferedBytes(int bufferBytes)
    {
        var random = new Random(4242);
        var rows = new List<SqlValue[]>();
        for (var index = 0; index < 400; index++)
        {
            var width = random.Next(1, 6);
            var row = new SqlValue[width];
            for (var column = 0; column < width; column++)
            {
                row[column] = random.Next(5) switch
                {
                    0 => SqlValue.Null,
                    1 => SqlValue.Integer(random.NextInt64()),
                    2 => SqlValue.Real(random.NextDouble()),
                    3 => SqlValue.Text(new string('x', random.Next(0, bufferBytes * 2))),
                    _ => SqlValue.BlobOwned(new byte[random.Next(0, 9)]),
                };
            }

            rows.Add(row);
        }

        var expected = WriteRecords(file => file, rows);
        var actual = WriteRecords(file => new VdbeBufferedSpillFile(file, bufferBytes), rows);

        actual.Should().Equal(expected);
    }

    [Test]
    public void ReadsOfBufferedBytesSeeThePendingWrites()
    {
        using var inner = OpenFile();
        var file = new VdbeBufferedSpillFile(inner, 64);

        file.Write(0, [1, 2, 3, 4]);
        file.Length.Should().Be(4);
        inner.Length.Should().Be(0, "the bytes are still buffered");

        Span<byte> read = stackalloc byte[4];
        file.Read(0, read).Should().Be(4);
        read.ToArray().Should().Equal(1, 2, 3, 4);
        inner.Length.Should().Be(4, "a read of buffered bytes writes them out first");
    }

    [Test]
    public void SetLengthAfterBufferedWritesTruncatesTheFlushedImage()
    {
        using var inner = OpenFile();
        var file = new VdbeBufferedSpillFile(inner, 64);

        file.Write(0, [1, 2, 3, 4, 5, 6]);
        file.SetLength(2);
        file.Write(2, [9]);
        file.FlushToDisk();

        var bytes = new byte[inner.Length];
        inner.Read(0, bytes);
        bytes.Should().Equal(1, 2, 9);
    }

    [Test]
    public void ReadAheadViewServesSequentialReadsFromOneBlock()
    {
        using var inner = OpenFile();
        var payload = Enumerable.Range(0, 300).Select(static value => (byte)value).ToArray();
        inner.Write(0, payload);
        var counting = new CountingFile(inner);
        using var view = new VdbeReadAheadSpillFile(counting, 128);

        var collected = new List<byte>();
        Span<byte> one = stackalloc byte[1];
        for (var position = 0L; position < payload.Length; position++)
        {
            view.Read(position, one).Should().Be(1);
            collected.Add(one[0]);
        }

        collected.Should().Equal(payload);
        counting.Reads.Should().BeLessThanOrEqualTo(3, "300 one-byte reads fit in three 128-byte blocks");
    }

    [Test]
    public void SpilledGroupByWritesWholeBuffersNotSingleValues()
    {
        // The default 2 MB execution budget spills this sort; before buffering, each spilled
        // value cost two or three file writes and the statement took tens of seconds at 10k rows.
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        Execute(connection, "CREATE TABLE users (id INTEGER PRIMARY KEY, first_name TEXT, last_name TEXT, email TEXT, phone_number TEXT, address TEXT, city TEXT, state TEXT, zipcode TEXT, age INTEGER)");
        Execute(connection, "WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i+1 FROM n WHERE i < 10000) INSERT INTO users(first_name,last_name,email,phone_number,address,city,state,zipcode,age) SELECT 'fn'||(i%700), 'ln'||(i%900), 'e'||i, 'p'||i, 'a'||i, 'c'||(i%300), 'S'||(i%50), 'z'||i, i%100 FROM n");

        var watch = System.Diagnostics.Stopwatch.StartNew();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT first_name, sum(age), count(*) FROM users GROUP BY first_name ORDER BY first_name LIMIT 3";
        var rows = new List<string>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
                rows.Add($"{reader.GetString(0)}|{reader.GetInt64(1)}|{reader.GetInt64(2)}");
        }
        watch.Stop();

        rows.Should().Equal("fn0|0|14", "fn1|15|15", "fn10|150|15");
        watch.Elapsed.Should().BeLessThan(
            TimeSpan.FromSeconds(10),
            "the spilled sort must not degrade to per-value file I/O (it took ~28 s before)");
    }

    [Test]
    public void SpilledHashJoinKeepsProbeBatchingAndReadsPartitionsInBlocks()
    {
        // Under the default 2 MB budget this join spills its build side. Resident partitions
        // used to starve probe-batch admission (one probe per batch, a partition reload per
        // probe, half of them failing part-way), all read one value at a time: ~32 s at 3k rows.
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        Execute(connection, "CREATE TABLE a(id INTEGER PRIMARY KEY, k INTEGER, pad TEXT)");
        Execute(connection, "CREATE TABLE b(id INTEGER PRIMARY KEY, k INTEGER, pad TEXT)");
        Execute(connection, "WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i+1 FROM n WHERE i < 3000) INSERT INTO a(k, pad) SELECT i, printf('%050d', i) FROM n");
        Execute(connection, "WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i+1 FROM n WHERE i < 3000) INSERT INTO b(k, pad) SELECT i % 1500, printf('%050d', i) FROM n");

        var watch = System.Diagnostics.Stopwatch.StartNew();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*), sum(a.k) FROM a JOIN b ON a.k = b.k";
        using var reader = command.ExecuteReader();
        reader.Read().Should().BeTrue();
        var matches = reader.GetInt64(0);
        var keySum = reader.GetInt64(1);
        watch.Stop();

        // b.k cycles 0..1499 twice; a.k 1..1499 match two rows each (k = 0 matches nothing).
        matches.Should().Be(2998);
        keySum.Should().Be(2L * (1499L * 1500L / 2));
        watch.Elapsed.Should().BeLessThan(
            TimeSpan.FromSeconds(10),
            "a spilled hash join must not degrade to a partition reload per probe");
    }

    private static byte[] WriteRecords(Func<IFile, IFile> wrap, IReadOnlyList<SqlValue[]> rows)
    {
        using var inner = OpenFile();
        var file = wrap(inner);
        var metrics = new VdbeExecutionMetrics();
        var position = VdbeSpillRecordCodec.InitializeFile(file, VdbeSpillFileKind.SorterRun, metrics);
        foreach (var row in rows)
        {
            var recordStart = VdbeSpillRecordCodec.BeginRecord(ref position);
            VdbeSpillRecordCodec.WriteValues(file, ref position, row, metrics);
            VdbeSpillRecordCodec.CompleteRecord(file, recordStart, position, metrics);
        }

        file.FlushToDisk();
        var bytes = new byte[inner.Length];
        inner.Read(0, bytes);

        position = VdbeSpillRecordCodec.FileHeaderSize;
        foreach (var row in rows)
        {
            var recordEnd = VdbeSpillRecordCodec.ReadRecordEnd(file, ref position, metrics);
            var values = VdbeSpillRecordCodec.ReadValues(
                file, ref position, row.Length, recordEnd, metrics, CancellationToken.None);
            VdbeSpillRecordCodec.RequireRecordEnd(position, recordEnd);
            values.Length.Should().Be(row.Length);
        }

        return bytes;
    }

    private static IFile OpenFile()
        => new InMemoryFileSystem().OpenFile("spill.bin", FileOpenMode.CreateNew);

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private sealed class CountingFile(IFile inner) : IFile
    {
        public int Reads { get; private set; }

        public long Length => inner.Length;

        public bool IsReadOnly => inner.IsReadOnly;

        public int Read(long position, Span<byte> destination)
        {
            Reads++;
            return inner.Read(position, destination);
        }

        public void Write(long position, ReadOnlySpan<byte> source) => inner.Write(position, source);

        public void SetLength(long length) => inner.SetLength(length);

        public void FlushToDisk() => inner.FlushToDisk();

        public void Dispose()
        {
        }
    }
}
