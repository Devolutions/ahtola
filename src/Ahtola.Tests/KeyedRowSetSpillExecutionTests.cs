using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Compilation;
using Ahtola.Core.Execution;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

public class KeyedRowSetSpillExecutionTests
{
    private const string TemporaryDirectory = "keyed-row-set-tests";

    private static readonly VdbeRowEquality ByteExactRows = static (left, right) =>
        left.AsSpan().SequenceEqual(right);

    [Test]
    public void DistinctSpillsWithinTinyBudgetAndCleansTemporaryFile()
    {
        var fileSystem = new TrackingFileSystem();
        var metrics = new VdbeExecutionMetrics();
        var values = Enumerable.Range(0, 64)
            .Select(static value => SqlValue.Integer(value))
            .Append(SqlValue.Null)
            .Append(SqlValue.Null)
            .Append(SqlValue.Integer(0))
            .ToArray();
        var options = Options(fileSystem, metrics, SpillInfrastructureBytes + 512);
        using var statement = ResumableStatement.CreateWithExecutionOptions(
            DistinctProgram(ByteExactRows, values),
            options);

        var rows = Drain(statement);

        rows.Should().HaveCount(65);
        rows.Take(64).Select(static row => row[0].AsInteger())
            .Should().Equal(Enumerable.Range(0, 64).Select(static value => (long)value));
        rows[^1][0].Should().Be(SqlValue.Null);
        metrics.KeyedRowSetsSpilled.Should().Be(1);
        metrics.SpillBytesWritten.Should().BeGreaterThan(0);
        metrics.SpillBytesRead.Should().BeGreaterThan(0);
        metrics.PeakRetainedBytes.Should().BeLessThanOrEqualTo(options.MemoryLimitBytes);
        metrics.CurrentRetainedBytes.Should().Be(0);
        metrics.CurrentRetainedRows.Should().Be(0);
        metrics.ActiveSpillFiles.Should().Be(0);
        fileSystem.Deleted.Should().BeEquivalentTo(fileSystem.Created);
    }

    [Test]
    public void SpilledIntersectPreservesNullCollationReplacementAndKeyOrder()
    {
        var left = Enumerable.Range(0, 64)
            .Select(static value => SqlValue.Text($"k{value:D3}"))
            .Prepend(SqlValue.Text("A"))
            .Prepend(SqlValue.Null)
            .Append(SqlValue.Text("a"))
            .Append(SqlValue.Text("b"))
            .ToArray();
        var right = Enumerable.Range(0, 64)
            .Where(static value => value % 2 == 0)
            .Select(static value => SqlValue.Text($"K{value:D3}"))
            .Prepend(SqlValue.Text("B"))
            .Prepend(SqlValue.Text("a"))
            .Prepend(SqlValue.Null)
            .ToArray();
        var compound = CompoundProgramBuilder.BuildIntersect(
            [ScanTerm("left", left), ScanTerm("right", right)],
            NoCaseRows,
            CompareNoCaseRows);
        var fileSystem = new TrackingFileSystem();
        var metrics = new VdbeExecutionMetrics();
        using var statement = ResumableStatement.CreateWithExecutionOptions(
            compound.Program,
            Options(fileSystem, metrics, memoryLimitBytes: (SpillInfrastructureBytes * 4) + 8192),
            compound.CursorSources);

        var rows = Drain(statement);

        rows[0][0].Should().Be(SqlValue.Null);
        rows[1][0].Should().Be(SqlValue.Text("a"));
        rows[2][0].Should().Be(SqlValue.Text("b"));
        rows.Skip(3).Select(static row => row[0].AsText())
            .Should().Equal(Enumerable.Range(0, 32).Select(static value => $"k{value * 2:D3}"));
        metrics.KeyedRowSetsSpilled.Should().BeGreaterThan(0);
        metrics.ActiveSpillFiles.Should().Be(0);
        metrics.CurrentRetainedBytes.Should().Be(0);
        fileSystem.Deleted.Should().BeEquivalentTo(fileSystem.Created);
    }

    [Test]
    public void MemoryOnlyDistinctFailsAtBudgetWithoutCreatingSpillFiles()
    {
        var fileSystem = new TrackingFileSystem();
        var metrics = new VdbeExecutionMetrics();
        var options = new VdbeExecutionOptions(
            fileSystem,
            sorterMemoryLimitBytes: 256,
            temporaryDirectory: TemporaryDirectory,
            allowTemporaryFileSpill: false,
            metrics: metrics);
        using var statement = ResumableStatement.CreateWithExecutionOptions(
            DistinctProgram(
                ByteExactRows,
                Enumerable.Range(0, 16).Select(static value => SqlValue.Integer(value)).ToArray()),
            options);

        Assert.Throws<VdbeMemoryLimitExceededException>(() => Drain(statement));

        statement.State.Should().Be(ResumableStatementState.Faulted);
        metrics.SpillFilesCreated.Should().Be(0);
        metrics.CurrentRetainedBytes.Should().Be(0);
        metrics.CurrentRetainedRows.Should().Be(0);
        fileSystem.Created.Should().BeEmpty();
    }

    [Test]
    public void MemoryOnlyReplacementUsesNetRetainedGrowth()
    {
        var fileSystem = new TrackingFileSystem();
        var metrics = new VdbeExecutionMetrics();
        var first = new[] { SqlValue.Text("A") };
        var replacement = new[] { SqlValue.Text("a") };
        var memoryLimit = checked(
            VdbeManagedFootprint.EstimateSorterRow(first)
            + VdbeManagedFootprint.EstimateReferenceListStorage(4));
        var options = new VdbeExecutionOptions(
            fileSystem,
            sorterMemoryLimitBytes: memoryLimit,
            temporaryDirectory: TemporaryDirectory,
            allowTemporaryFileSpill: false,
            metrics: metrics);
        var memory = new VdbeExecutionMemory(memoryLimit, metrics);

        using (var store = new VdbeKeyedRowStore(options, memory))
        {
            store.TryInsert(first, NoCaseRows, replaceExisting: true, default).Should().BeTrue();
            store.TryInsert(replacement, NoCaseRows, replaceExisting: true, default).Should().BeTrue();
            store.Rewind(default).Should().BeTrue();
            store.Current()[0].Should().Be(SqlValue.Text("a"));
        }

        metrics.SpillFilesCreated.Should().Be(0);
        metrics.PeakRetainedBytes.Should().Be(memoryLimit);
        metrics.CurrentRetainedBytes.Should().Be(0);
        metrics.CurrentRetainedRows.Should().Be(0);
    }

    [Test]
    public void CancellationAfterDistinctSpillFaultsAndCleansResources()
    {
        using var cancellation = new CancellationTokenSource();
        var fileSystem = new TrackingFileSystem();
        var metrics = new VdbeExecutionMetrics();
        var comparisonsAfterSpill = 0;
        VdbeRowEquality equality = (left, right) =>
        {
            if (metrics.KeyedRowSetsSpilled > 0 && ++comparisonsAfterSpill == 3)
                cancellation.Cancel();
            return ByteExactRows(left, right);
        };
        using var statement = ResumableStatement.CreateWithExecutionOptions(
            DistinctProgram(
                equality,
                Enumerable.Range(0, 64).Select(static value => SqlValue.Integer(value)).ToArray()),
            Options(fileSystem, metrics, SpillInfrastructureBytes + 512));

        var exception = Assert.Throws<OperationCanceledException>(
            () => Drain(statement, cancellation.Token));

        exception!.CancellationToken.Should().Be(cancellation.Token);
        statement.State.Should().Be(ResumableStatementState.Faulted);
        metrics.KeyedRowSetsSpilled.Should().Be(1);
        metrics.ActiveSpillFiles.Should().Be(0);
        metrics.CurrentRetainedBytes.Should().Be(0);
        metrics.CurrentRetainedRows.Should().Be(0);
        fileSystem.Deleted.Should().BeEquivalentTo(fileSystem.Created);
    }

    [Test]
    public void EqualityFailureAfterDistinctSpillRemainsPrimaryAndCleansResources()
    {
        var primary = new InvalidOperationException("distinct equality failed");
        var fileSystem = new TrackingFileSystem();
        var metrics = new VdbeExecutionMetrics();
        var comparisonsAfterSpill = 0;
        VdbeRowEquality equality = (left, right) =>
        {
            if (metrics.KeyedRowSetsSpilled > 0 && ++comparisonsAfterSpill == 3)
                throw primary;
            return ByteExactRows(left, right);
        };
        using var statement = ResumableStatement.CreateWithExecutionOptions(
            DistinctProgram(
                equality,
                Enumerable.Range(0, 64).Select(static value => SqlValue.Integer(value)).ToArray()),
            Options(fileSystem, metrics, SpillInfrastructureBytes + 512));

        Assert.Throws<InvalidOperationException>(() => Drain(statement)).Should().BeSameAs(primary);

        statement.State.Should().Be(ResumableStatementState.Faulted);
        metrics.KeyedRowSetsSpilled.Should().Be(1);
        metrics.ActiveSpillFiles.Should().Be(0);
        metrics.CurrentRetainedBytes.Should().Be(0);
        metrics.CurrentRetainedRows.Should().Be(0);
        fileSystem.Deleted.Should().BeEquivalentTo(fileSystem.Created);
    }

    [Test]
    public void SpilledLookupAndIterationReadEachRequestedSlotDirectly()
    {
        const int rowCount = 80;
        var fileSystem = new TrackingFileSystem();
        var metrics = new VdbeExecutionMetrics();
        var options = Options(fileSystem, metrics, SpillInfrastructureBytes + 512);
        var memory = new VdbeExecutionMemory(options.MemoryLimitBytes, metrics);

        using (var store = new VdbeKeyedRowStore(options, memory))
        {
            for (var value = 0; value < rowCount; value++)
            {
                store.TryInsert(
                    [SqlValue.Integer(value)],
                    ByteExactRows,
                    replaceExisting: false,
                    default).Should().BeTrue();
            }

            store.IsSpilled.Should().BeTrue();
            store.Rewind(default).Should().BeTrue();
            var actual = new List<long>(rowCount);
            do
            {
                actual.Add(store.Current()[0].AsInteger());
            }
            while (store.MoveNext(default));

            actual.Should().Equal(Enumerable.Range(0, rowCount).Select(static value => (long)value));
        }

        // Each requested slot needs seven fixed-position reads: two file headers, the offset,
        // record length, slot, value tag, and integer payload. The upper bound assumes spilling
        // before the first insert; buffering some prefix only reduces the number of requests.
        var maximumSlotRequests = checked((long)rowCount * (rowCount + 1) / 2);
        fileSystem.ReadCalls.Should().BeLessThanOrEqualTo(checked(maximumSlotRequests * 7));
        metrics.ActiveSpillFiles.Should().Be(0);
        metrics.CurrentRetainedBytes.Should().Be(0);
        fileSystem.Deleted.Should().BeEquivalentTo(fileSystem.Created);
    }

    private static long SpillInfrastructureBytes =>
        VdbeManagedFootprint.EstimateKeyedRowSetSpillInfrastructure(TemporaryDirectory);

    private static VdbeExecutionOptions Options(
        IFileSystem fileSystem,
        VdbeExecutionMetrics metrics,
        long memoryLimitBytes) =>
        new(
            fileSystem,
            sorterMemoryLimitBytes: memoryLimitBytes,
            temporaryDirectory: TemporaryDirectory,
            metrics: metrics);

    private static VdbeProgram DistinctProgram(
        VdbeRowEquality equality,
        IReadOnlyList<SqlValue> values)
    {
        var instructions = new List<VdbeInstruction>(checked((values.Count * 2) + 1));
        foreach (var value in values)
        {
            instructions.Add(new LoadConstantInstruction(new Register(0), value));
            instructions.Add(
                new DistinctResultRowInstruction(
                    new RegisterRange(new Register(0), 1),
                    equality,
                    DistinctSetIndex: 0));
        }
        instructions.Add(new HaltInstruction());
        return new VdbeProgram(1, cursorCount: 0, instructions, distinctSetCount: 1);
    }

    private static CompoundTerm ScanTerm(string name, IReadOnlyList<SqlValue> values)
    {
        VdbeInstruction[] instructions =
        [
            new OpenReadCursorInstruction(new Cursor(0), name, ColumnCount: 1),
            new RewindCursorInstruction(new Cursor(0), new ProgramCounter(6)),
            new ColumnInstruction(new Cursor(0), 0, new Register(0)),
            new ResultRowInstruction(new RegisterRange(new Register(0), 1)),
            new NextInstruction(new Cursor(0), new ProgramCounter(2)),
            new CloseCursorInstruction(new Cursor(0)),
            new HaltInstruction(),
        ];
        var rows = values.Select(static value => new[] { value }).ToArray();
        return new CompoundTerm(
            new VdbeProgram(registerCount: 1, cursorCount: 1, instructions),
            [new VdbeCursorSource(rows)]);
    }

    private static bool NoCaseRows(SqlValue[] left, SqlValue[] right)
    {
        if (left.Length != right.Length)
            return false;
        for (var index = 0; index < left.Length; index++)
        {
            var leftValue = left[index];
            var rightValue = right[index];
            if (leftValue.Kind == SqlValueKind.Null || rightValue.Kind == SqlValueKind.Null)
            {
                if (leftValue.Kind != rightValue.Kind)
                    return false;
                continue;
            }
            if (leftValue.Kind != SqlValueKind.Text
                || rightValue.Kind != SqlValueKind.Text
                || !string.Equals(leftValue.AsText(), rightValue.AsText(), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        return true;
    }

    private static int CompareNoCaseRows(SqlValue[] left, SqlValue[] right)
    {
        if (left[0].Kind == SqlValueKind.Null)
            return right[0].Kind == SqlValueKind.Null ? 0 : -1;
        if (right[0].Kind == SqlValueKind.Null)
            return 1;
        return StringComparer.OrdinalIgnoreCase.Compare(left[0].AsText(), right[0].AsText());
    }

    private static List<SqlValue[]> Drain(
        ResumableStatement statement,
        CancellationToken cancellationToken = default)
    {
        var rows = new List<SqlValue[]>();
        while (true)
        {
            var result = statement.StepResumable(cancellationToken);
            if (result == ResumableStatementStepResult.Done)
                return rows;
            result.Should().Be(ResumableStatementStepResult.Row);
            rows.Add([.. statement.CurrentRow!]);
        }
    }

    private sealed class TrackingFileSystem : IFileSystem
    {
        private readonly IFileSystem _inner = new InMemoryFileSystem();

        public List<string> Created { get; } = [];

        public List<string> Deleted { get; } = [];

        public long ReadCalls { get; private set; }

        public bool FileExists(string path) => _inner.FileExists(path);

        public IFile OpenFile(string path, FileOpenMode mode, bool readOnly = false)
        {
            var file = _inner.OpenFile(path, mode, readOnly);
            if (mode == FileOpenMode.CreateNew)
                Created.Add(path);
            return new TrackingFile(this, file);
        }

        public void DeleteFile(string path)
        {
            Deleted.Add(path);
            _inner.DeleteFile(path);
        }

        private sealed class TrackingFile(TrackingFileSystem owner, IFile inner) : IFile
        {
            public long Length => inner.Length;

            public bool IsReadOnly => inner.IsReadOnly;

            public int Read(long position, Span<byte> destination)
            {
                owner.ReadCalls++;
                return inner.Read(position, destination);
            }

            public void Write(long position, ReadOnlySpan<byte> source) =>
                inner.Write(position, source);

            public void SetLength(long length) => inner.SetLength(length);

            public void FlushToDisk() => inner.FlushToDisk();

            public void Dispose() => inner.Dispose();
        }
    }
}
