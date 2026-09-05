using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Execution;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

/// <summary>
/// Covers OpenEphemeral / EphemeralInsert (inventory: vdbe-open-ephemeral).
/// </summary>
public sealed class VdbeEphemeralTableOpcodeTests
{
    [Test]
    public void EphemeralTableInsertsAndScansInOrder()
    {
        // Open ephemeral, insert three rows, rewind and yield each first column.
        VdbeInstruction[] instructions =
        [
            new OpenEphemeralInstruction(new Cursor(0), ColumnCount: 2),
            new LoadConstantInstruction(new Register(0), SqlValue.Text("a")),
            new LoadConstantInstruction(new Register(1), SqlValue.Integer(1)),
            new EphemeralInsertInstruction(new Cursor(0), new RegisterRange(new Register(0), 2)),
            new LoadConstantInstruction(new Register(0), SqlValue.Text("b")),
            new LoadConstantInstruction(new Register(1), SqlValue.Integer(2)),
            new EphemeralInsertInstruction(new Cursor(0), new RegisterRange(new Register(0), 2)),
            new LoadConstantInstruction(new Register(0), SqlValue.Text("c")),
            new LoadConstantInstruction(new Register(1), SqlValue.Integer(3)),
            new EphemeralInsertInstruction(new Cursor(0), new RegisterRange(new Register(0), 2)),
            new RewindCursorInstruction(new Cursor(0), new ProgramCounter(14)),
            new ColumnInstruction(new Cursor(0), ColumnIndex: 0, new Register(2)),
            new ResultRowInstruction(new RegisterRange(new Register(2), 1)),
            new NextInstruction(new Cursor(0), new ProgramCounter(11)),
            new CloseCursorInstruction(new Cursor(0)),
            new HaltInstruction(),
        ];

        var program = new VdbeProgram(registerCount: 3, cursorCount: 1, instructions);
        using var statement = new ResumableStatement(program);
        var values = new List<string>();
        while (statement.StepResumable() == ResumableStatementStepResult.Row)
            values.Add(statement.CurrentRow![0].AsText());

        values.Should().Equal("a", "b", "c");
    }

    [Test]
    public void EphemeralSeekRowidFindsAssignedRowIds()
    {
        VdbeInstruction[] instructions =
        [
            new OpenEphemeralInstruction(new Cursor(0), ColumnCount: 1),
            new LoadConstantInstruction(new Register(0), SqlValue.Text("first")),
            new EphemeralInsertInstruction(new Cursor(0), new RegisterRange(new Register(0), 1)),
            new LoadConstantInstruction(new Register(0), SqlValue.Text("second")),
            new EphemeralInsertInstruction(new Cursor(0), new RegisterRange(new Register(0), 1)),
            // Seek rowid 2
            new LoadConstantInstruction(new Register(1), SqlValue.Integer(2)),
            new SeekRowidInstruction(
                new Cursor(0),
                new Register(1),
                new ProgramCounter(10),
                "seek 2"),
            new ColumnInstruction(new Cursor(0), ColumnIndex: 0, new Register(2)),
            new ResultRowInstruction(new RegisterRange(new Register(2), 1)),
            new GotoInstruction(new ProgramCounter(12)),
            // not-found: emit sentinel then halt
            new LoadConstantInstruction(new Register(2), SqlValue.Text("missing")),
            new ResultRowInstruction(new RegisterRange(new Register(2), 1)),
            new HaltInstruction(),
        ];

        var program = new VdbeProgram(registerCount: 3, cursorCount: 1, instructions);
        using var statement = new ResumableStatement(program);
        statement.StepResumable().Should().Be(ResumableStatementStepResult.Row);
        statement.CurrentRow![0].AsText().Should().Be("second");
        statement.StepResumable().Should().Be(ResumableStatementStepResult.Done);
    }

    [Test]
    public void EmptyEphemeralRewindJumpsToEmptyTarget()
    {
        VdbeInstruction[] instructions =
        [
            new OpenEphemeralInstruction(new Cursor(0), ColumnCount: 1),
            new RewindCursorInstruction(new Cursor(0), new ProgramCounter(4)),
            new LoadConstantInstruction(new Register(0), SqlValue.Text("row")),
            new ResultRowInstruction(new RegisterRange(new Register(0), 1)),
            new LoadConstantInstruction(new Register(0), SqlValue.Text("empty")),
            new ResultRowInstruction(new RegisterRange(new Register(0), 1)),
            new HaltInstruction(),
        ];

        var program = new VdbeProgram(registerCount: 1, cursorCount: 1, instructions);
        using var statement = new ResumableStatement(program);
        statement.StepResumable().Should().Be(ResumableStatementStepResult.Row);
        statement.CurrentRow![0].AsText().Should().Be("empty");
    }

    [Test]
    public void NotExistsOnEphemeralDetectsMissingRowid()
    {
        VdbeInstruction[] instructions =
        [
            new OpenEphemeralInstruction(new Cursor(0), ColumnCount: 1),
            new LoadConstantInstruction(new Register(0), SqlValue.Text("only")),
            new EphemeralInsertInstruction(new Cursor(0), new RegisterRange(new Register(0), 1)),
            new LoadConstantInstruction(new Register(1), SqlValue.Integer(99)),
            new NotExistsInstruction(
                new Cursor(0),
                new Register(1),
                new ProgramCounter(7),
                "missing"),
            new LoadConstantInstruction(new Register(2), SqlValue.Text("present")),
            new GotoInstruction(new ProgramCounter(8)),
            new LoadConstantInstruction(new Register(2), SqlValue.Text("absent")),
            new ResultRowInstruction(new RegisterRange(new Register(2), 1)),
            new HaltInstruction(),
        ];

        var program = new VdbeProgram(registerCount: 3, cursorCount: 1, instructions);
        using var statement = new ResumableStatement(program);
        statement.StepResumable().Should().Be(ResumableStatementStepResult.Row);
        statement.CurrentRow![0].AsText().Should().Be("absent");
    }

    [Test]
    public void ExplainRendersOpenEphemeral()
    {
        var open = new OpenEphemeralInstruction(new Cursor(3), ColumnCount: 4);
        var (p1, p2, _, _, comment) = VdbeExplain.Describe(open);
        p1.Should().Be(3);
        p2.Should().Be(4);
        comment.Should().Contain("ephemeral");
        open.Opcode.Should().Be(VdbeOpcode.OpenEphemeral);
    }

    [Test]
    public void EphemeralTableSpillsAndPreservesScanOrderAndRowIds()
    {
        // Insert enough text rows to cross a budget that can hold only a couple of them, then scan:
        // the spill must preserve insertion order, the sequential rowids, and every value.
        const string temporaryDirectory = "ephemeral-spill-scan-tests";
        const int rowCount = 12;

        // Assemble the drain loop by hand: open -> (load+insert)*n -> rewind -> column -> result
        // -> next -> close -> halt.
        var body = new List<VdbeInstruction>
        {
            new OpenEphemeralInstruction(new Cursor(0), ColumnCount: 1),
        };
        for (var index = 0; index < rowCount; index++)
        {
            body.Add(new LoadConstantInstruction(new Register(0), SqlValue.Text(new string('v', 64) + index.ToString("00"))));
            body.Add(new EphemeralInsertInstruction(new Cursor(0), new RegisterRange(new Register(0), 1)));
        }

        var loopTop = body.Count;
        var doneTarget = new ProgramCounter(loopTop + 4);
        body.Add(new RewindCursorInstruction(new Cursor(0), doneTarget));
        body.Add(new ColumnInstruction(new Cursor(0), ColumnIndex: 0, new Register(2)));
        body.Add(new ResultRowInstruction(new RegisterRange(new Register(2), 1)));
        body.Add(new NextInstruction(new Cursor(0), new ProgramCounter(loopTop + 1)));
        body.Add(new CloseCursorInstruction(new Cursor(0)));
        body.Add(new HaltInstruction());

        var program = new VdbeProgram(registerCount: 3, cursorCount: 1, [.. body]);

        var sample = new[] { SqlValue.Text(new string('v', 64)) };
        var rowBytes = VdbeManagedFootprint.EstimateSorterRow(sample);
        var infrastructure = VdbeManagedFootprint.EstimateEphemeralTableSpillInfrastructure(
            temporaryDirectory);
        // Room for two buffered rows plus the spill infrastructure: the third insert trips the spill.
        var budget = checked((rowBytes * 2) + infrastructure + 64);
        var fileSystem = new TrackingFileSystem();
        var metrics = new VdbeExecutionMetrics();
        var options = new VdbeExecutionOptions(
            fileSystem,
            sorterMemoryLimitBytes: budget,
            temporaryDirectory: temporaryDirectory,
            metrics: metrics);

        using var statement = ResumableStatement.CreateWithExecutionOptions(program, options);
        var rows = new List<string>();
        while (statement.StepResumable() == ResumableStatementStepResult.Row)
            rows.Add(statement.CurrentRow![0].AsText());

        rows.Should().HaveCount(rowCount);
        rows.Select(static value => value[^2..]).Should().Equal(
            Enumerable.Range(0, rowCount).Select(static index => index.ToString("00")));

        metrics.EphemeralTablesSpilled.Should().Be(1);
        metrics.SpillBytesWritten.Should().BeGreaterThan(0);
        metrics.SpillBytesRead.Should().BeGreaterThan(0);
        metrics.PeakRetainedBytes.Should().BeLessThanOrEqualTo(budget);
        metrics.CurrentRetainedBytes.Should().Be(0);
        metrics.ActiveSpillFiles.Should().Be(0);
        fileSystem.Deleted.Should().BeEquivalentTo(fileSystem.Created);
    }

    private sealed class TrackingFileSystem : IFileSystem
    {
        private readonly IFileSystem _inner = new Ahtola.Core.Storage.InMemoryFileSystem();

        public List<string> Created { get; } = [];

        public List<string> Deleted { get; } = [];

        public bool FileExists(string path) => _inner.FileExists(path);

        public IFile OpenFile(string path, FileOpenMode mode, bool readOnly = false)
        {
            var file = _inner.OpenFile(path, mode, readOnly);
            if (mode == FileOpenMode.CreateNew)
                Created.Add(path);
            return file;
        }

        public void DeleteFile(string path)
        {
            Deleted.Add(path);
            _inner.DeleteFile(path);
        }
    }
}
