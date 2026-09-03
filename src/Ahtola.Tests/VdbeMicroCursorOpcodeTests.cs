using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Execution;

namespace Ahtola.Tests;

/// <summary>
/// Covers the micro-cursor opcodes ported from Turso's same-named <c>Insn</c>s:
/// <c>ResetSorter</c>, <c>OpenDup</c>, <c>OpenAutoindex</c>, <c>ColumnHasField</c>,
/// <c>DeferredSeek</c>, <c>SeekEnd</c>, <c>BloomFilter</c>, <c>BloomFilterAdd</c>,
/// and the hash-join family (<c>HashBuild</c>, <c>HashDistinct</c>, <c>HashBuildFinalize</c>,
/// <c>HashProbe</c>, <c>HashNext</c>, <c>HashClose</c>, <c>HashClear</c>,
/// <c>HashMarkMatched</c>, <c>HashResetMatched</c>, <c>HashScanUnmatched</c>,
/// <c>HashNextUnmatched</c>).
/// The aggregate half (<c>AggValue</c>) lives in <c>AggregateOpcodeExecutionTests</c>.
/// </summary>
public sealed class VdbeMicroCursorOpcodeTests
{
    [Test]
    public void ResetSorterClearsRowsAndRestartsRowIds()
    {
        // Insert two rows, then ResetSorter clears them in place: the rewind must see an
        // empty table, and the next insert must receive rowid 1 again.
        VdbeInstruction[] instructions =
        [
            new OpenEphemeralInstruction(new Cursor(0), ColumnCount: 1),
            new LoadConstantInstruction(new Register(0), SqlValue.Text("a")),
            new EphemeralInsertInstruction(new Cursor(0), new RegisterRange(new Register(0), 1)),
            new LoadConstantInstruction(new Register(0), SqlValue.Text("b")),
            new EphemeralInsertInstruction(new Cursor(0), new RegisterRange(new Register(0), 1)),
            new ResetSorterInstruction(new Cursor(0)),
            new RewindCursorInstruction(new Cursor(0), new ProgramCounter(9)),
            new LoadConstantInstruction(new Register(1), SqlValue.Text("leak")),
            new ResultRowInstruction(new RegisterRange(new Register(1), 1)),
            new LoadConstantInstruction(new Register(1), SqlValue.Text("empty")),
            new ResultRowInstruction(new RegisterRange(new Register(1), 1)),
            new LoadConstantInstruction(new Register(0), SqlValue.Text("c")),
            new EphemeralInsertInstruction(new Cursor(0), new RegisterRange(new Register(0), 1)),
            new LoadConstantInstruction(new Register(2), SqlValue.Integer(1)),
            new SeekRowidInstruction(new Cursor(0), new Register(2), new ProgramCounter(17), "seek rowid 1"),
            new ColumnInstruction(new Cursor(0), ColumnIndex: 0, new Register(1)),
            new ResultRowInstruction(new RegisterRange(new Register(1), 1)),
            new HaltInstruction(),
        ];

        var program = new VdbeProgram(registerCount: 3, cursorCount: 1, instructions);
        using var statement = new ResumableStatement(program);
        var values = new List<string>();
        while (statement.StepResumable() == ResumableStatementStepResult.Row)
            values.Add(statement.CurrentRow![0].AsText());

        values.Should().Equal("empty", "c");
    }

    [Test]
    public void ResetSorterRejectsNonEphemeralCursor()
    {
        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(
            registerCount: 0,
            cursorCount: 1,
            [
                new OpenReadCursorInstruction(new Cursor(0), "t", ColumnCount: 1),
                new ResetSorterInstruction(new Cursor(0)),
                new HaltInstruction(),
            ]))!.Message.Should().Contain("not an ephemeral cursor");
    }

    [Test]
    public void OpenDupSharesRowsAcrossCursors()
    {
        // A row inserted through the original cursor must be visible through the duplicate:
        // both cursors share one ephemeral storage instance. Scanning through the duplicate
        // also proves it inherited the original's column count.
        VdbeInstruction[] instructions =
        [
            new OpenEphemeralInstruction(new Cursor(0), ColumnCount: 1),
            new OpenDupInstruction(new Cursor(1), new Cursor(0)),
            new LoadConstantInstruction(new Register(0), SqlValue.Text("shared")),
            new EphemeralInsertInstruction(new Cursor(0), new RegisterRange(new Register(0), 1)),
            new RewindCursorInstruction(new Cursor(1), new ProgramCounter(8)),
            new ColumnInstruction(new Cursor(1), ColumnIndex: 0, new Register(1)),
            new ResultRowInstruction(new RegisterRange(new Register(1), 1)),
            new NextInstruction(new Cursor(1), new ProgramCounter(5)),
            new HaltInstruction(),
        ];

        var program = new VdbeProgram(registerCount: 2, cursorCount: 2, instructions);
        using var statement = new ResumableStatement(program);
        var values = new List<string>();
        while (statement.StepResumable() == ResumableStatementStepResult.Row)
            values.Add(statement.CurrentRow![0].AsText());

        values.Should().ContainSingle().Which.Should().Be("shared");
    }

    [Test]
    public void OpenAutoindexInsertsAndScans()
    {
        // Autoindex cursors open through the ephemeral path and scan like any ephemeral table.
        VdbeInstruction[] instructions =
        [
            new OpenAutoindexInstruction(new Cursor(0), ColumnCount: 1),
            new LoadConstantInstruction(new Register(0), SqlValue.Text("x")),
            new EphemeralInsertInstruction(new Cursor(0), new RegisterRange(new Register(0), 1)),
            new RewindCursorInstruction(new Cursor(0), new ProgramCounter(7)),
            new ColumnInstruction(new Cursor(0), ColumnIndex: 0, new Register(1)),
            new ResultRowInstruction(new RegisterRange(new Register(1), 1)),
            new NextInstruction(new Cursor(0), new ProgramCounter(4)),
            new HaltInstruction(),
        ];

        var program = new VdbeProgram(registerCount: 2, cursorCount: 1, instructions);
        using var statement = new ResumableStatement(program);
        var values = new List<string>();
        while (statement.StepResumable() == ResumableStatementStepResult.Row)
            values.Add(statement.CurrentRow![0].AsText());

        values.Should().ContainSingle().Which.Should().Be("x");
    }

    [Test]
    public void ColumnHasFieldJumpsOnlyWhenCurrentRecordCarriesTheColumn()
    {
        // The first record carries column 2 (jump branch); the second is a short record
        // (fall-through branch).
        VdbeInstruction[] instructions =
        [
            new OpenReadCursorInstruction(new Cursor(0), "t", ColumnCount: 3),
            new RewindCursorInstruction(new Cursor(0), new ProgramCounter(8)),
            new ColumnHasFieldInstruction(new Cursor(0), Column: 2, new ProgramCounter(5)),
            new LoadConstantInstruction(new Register(0), SqlValue.Text("short")),
            new GotoInstruction(new ProgramCounter(6)),
            new LoadConstantInstruction(new Register(0), SqlValue.Text("wide")),
            new ResultRowInstruction(new RegisterRange(new Register(0), 1)),
            new NextInstruction(new Cursor(0), new ProgramCounter(2)),
            new HaltInstruction(),
        ];

        var program = new VdbeProgram(registerCount: 1, cursorCount: 1, instructions);
        using var statement = new ResumableStatement(
            program,
            [new VdbeCursorSource([
                [SqlValue.Integer(1), SqlValue.Integer(2), SqlValue.Integer(3)],
                [SqlValue.Integer(1)],
            ])]);
        var values = new List<string>();
        while (statement.StepResumable() == ResumableStatementStepResult.Row)
            values.Add(statement.CurrentRow![0].AsText());

        values.Should().Equal("wide", "short");
    }

    [Test]
    public void ColumnHasFieldFallsThroughWithoutCurrentRecord()
    {
        // A cursor that was opened but never advanced has no current record and must fall
        // through, matching Turso's missing-record behavior.
        VdbeInstruction[] instructions =
        [
            new OpenReadCursorInstruction(new Cursor(0), "t", ColumnCount: 1),
            new ColumnHasFieldInstruction(new Cursor(0), Column: 0, new ProgramCounter(4)),
            new LoadConstantInstruction(new Register(0), SqlValue.Text("miss")),
            new GotoInstruction(new ProgramCounter(5)),
            new LoadConstantInstruction(new Register(0), SqlValue.Text("hit")),
            new ResultRowInstruction(new RegisterRange(new Register(0), 1)),
            new HaltInstruction(),
        ];

        var program = new VdbeProgram(registerCount: 1, cursorCount: 1, instructions);
        using var statement = new ResumableStatement(
            program,
            [new VdbeCursorSource([[SqlValue.Integer(1)]])]);
        var values = new List<string>();
        while (statement.StepResumable() == ResumableStatementStepResult.Row)
            values.Add(statement.CurrentRow![0].AsText());

        values.Should().ContainSingle().Which.Should().Be("miss");
    }

    [Test]
    public void ValidationRejectsMalformedMicroCursorBytecode()
    {
        // OpenDup onto a non-ephemeral original.
        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(
            registerCount: 0,
            cursorCount: 2,
            [
                new OpenReadCursorInstruction(new Cursor(0), "t", ColumnCount: 1),
                new OpenDupInstruction(new Cursor(1), new Cursor(0)),
                new HaltInstruction(),
            ]))!.Message.Should().Contain("not an ephemeral cursor");

        // OpenDup onto itself.
        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(
            registerCount: 0,
            cursorCount: 1,
            [
                new OpenEphemeralInstruction(new Cursor(0), ColumnCount: 1),
                new OpenDupInstruction(new Cursor(0), new Cursor(0)),
                new HaltInstruction(),
            ]))!.Message.Should().Contain("onto itself");

        // OpenDup before the original is open.
        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(
            registerCount: 0,
            cursorCount: 2,
            [
                new OpenDupInstruction(new Cursor(1), new Cursor(0)),
                new HaltInstruction(),
            ]))!.Message.Should().Contain("before opening it");

        // OpenDup onto a cursor that is already open.
        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(
            registerCount: 0,
            cursorCount: 2,
            [
                new OpenEphemeralInstruction(new Cursor(0), ColumnCount: 1),
                new OpenEphemeralInstruction(new Cursor(1), ColumnCount: 1),
                new OpenDupInstruction(new Cursor(1), new Cursor(0)),
                new HaltInstruction(),
            ]))!.Message.Should().Contain("opens cursor 1 twice");

        // ColumnHasField with a negative column.
        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(
            registerCount: 0,
            cursorCount: 1,
            [
                new OpenEphemeralInstruction(new Cursor(0), ColumnCount: 1),
                new ColumnHasFieldInstruction(new Cursor(0), Column: -1, new ProgramCounter(2)),
                new HaltInstruction(),
            ]))!.Message.Should().Contain("negative column");

        // OpenAutoindex with a non-positive column count.
        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(
            registerCount: 0,
            cursorCount: 1,
            [
                new OpenAutoindexInstruction(new Cursor(0), ColumnCount: 0),
                new HaltInstruction(),
            ]))!.Message.Should().Contain("non-positive column count");

        // OpenAutoindex on an already-open cursor.
        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(
            registerCount: 0,
            cursorCount: 1,
            [
                new OpenEphemeralInstruction(new Cursor(0), ColumnCount: 1),
                new OpenAutoindexInstruction(new Cursor(0), ColumnCount: 1),
                new HaltInstruction(),
            ]))!.Message.Should().Contain("opens cursor 0 twice");
    }

    [Test]
    public void ExplainDescribesMicroCursorOpcodes()
    {
        var (p1, p2, _, _, autoindexComment) = VdbeExplain.Describe(new OpenAutoindexInstruction(new Cursor(2), ColumnCount: 5));
        p1.Should().Be(2);
        p2.Should().Be(5);
        autoindexComment.Should().Be("open autoindex cursor 2 cols=5");

        var (dupNew, dupOriginal, _, _, dupComment) = VdbeExplain.Describe(new OpenDupInstruction(new Cursor(1), new Cursor(0)));
        dupNew.Should().Be(1);
        dupOriginal.Should().Be(0);
        dupComment.Should().Be("duplicate ephemeral cursor 0 to cursor 1");

        var (resetCursor, _, _, _, resetComment) = VdbeExplain.Describe(new ResetSorterInstruction(new Cursor(3)));
        resetCursor.Should().Be(3);
        resetComment.Should().Be("reset ephemeral cursor 3");

        var (hasCursor, hasColumn, hasTarget, _, hasComment) = VdbeExplain.Describe(
            new ColumnHasFieldInstruction(new Cursor(0), Column: 2, new ProgramCounter(7)));
        hasCursor.Should().Be(0);
        hasColumn.Should().Be(2);
        hasTarget.Should().Be(7);
        hasComment.Should().Be("jump to 7 when cursor 0 has column 2");

        var aggValue = new AggValueInstruction(new Accumulator(1), AggregateTestSupport.Sum(), new Register(2));
        var (accIndex, destIndex, _, aggName, aggComment) = VdbeExplain.Describe(aggValue);
        accIndex.Should().Be(1);
        destIndex.Should().Be(2);
        aggName.Should().Be("sum");
        aggComment.Should().Contain("no reset");
        aggValue.Opcode.Should().Be(VdbeOpcode.AggValue);
    }

    [Test]
    public void DeferredSeekSeeksTableCursorByIndexRowId()
    {
        var indexSource = new VdbeCursorSource(
            [
                [SqlValue.Integer(10)],
                [SqlValue.Integer(20)],
            ],
            [10, 20]);
        // Table rows are stored reversed relative to the index rowids so a plain forward
        // scan would return them in the wrong order; only a real seek yields 210, 220.
        var tableSource = new VdbeCursorSource(
            [
                [SqlValue.Integer(220)],
                [SqlValue.Integer(210)],
            ],
            [20, 10]);

        VdbeInstruction[] instructions =
        [
            new OpenReadCursorInstruction(new Cursor(0), "i", ColumnCount: 1),
            new OpenReadCursorInstruction(new Cursor(1), "t", ColumnCount: 1),
            new RewindCursorInstruction(new Cursor(0), new ProgramCounter(7)),
            new DeferredSeekInstruction(new Cursor(0), new Cursor(1)),
            new ColumnInstruction(new Cursor(1), 0, new Register(0)),
            new ResultRowInstruction(new RegisterRange(new Register(0), 1)),
            new NextInstruction(new Cursor(0), new ProgramCounter(3)),
            new HaltInstruction(),
        ];
        var program = new VdbeProgram(registerCount: 1, cursorCount: 2, instructions);
        using var statement = new ResumableStatement(program, [indexSource, tableSource]);

        var values = new List<long>();
        while (statement.StepResumable() == ResumableStatementStepResult.Row)
            values.Add(statement.CurrentRow![0].AsInteger());

        values.Should().Equal(210L, 220L);
    }

    [Test]
    public void DeferredSeekWritesNullWhenIndexCursorIsUnpositioned()
    {
        var indexSource = new VdbeCursorSource(
            [
                [SqlValue.Integer(10)],
            ],
            [10]);
        var tableSource = new VdbeCursorSource(
            [
                [SqlValue.Integer(220)],
            ],
            [10]);

        VdbeInstruction[] instructions =
        [
            new OpenReadCursorInstruction(new Cursor(0), "i", ColumnCount: 1),
            new OpenReadCursorInstruction(new Cursor(1), "t", ColumnCount: 1),
            new DeferredSeekInstruction(new Cursor(0), new Cursor(1)),
            new ColumnInstruction(new Cursor(1), 0, new Register(0)),
            new ResultRowInstruction(new RegisterRange(new Register(0), 1)),
            new HaltInstruction(),
        ];
        var program = new VdbeProgram(registerCount: 1, cursorCount: 2, instructions);
        using var statement = new ResumableStatement(program, [indexSource, tableSource]);

        statement.StepResumable().Should().Be(ResumableStatementStepResult.Row);
        statement.CurrentRow![0].Should().Be(SqlValue.Null);
    }

    [Test]
    public void DeferredSeekResolvesRowIdReadThroughIndexCursor()
    {
        var indexSource = new VdbeCursorSource(
            [
                [SqlValue.Integer(42)],
            ],
            [42]);
        var tableSource = new VdbeCursorSource(
            [
                [SqlValue.Integer(1)],
            ],
            [42]);

        VdbeInstruction[] instructions =
        [
            new OpenReadCursorInstruction(new Cursor(0), "i", ColumnCount: 1),
            new OpenReadCursorInstruction(new Cursor(1), "t", ColumnCount: 1),
            new RewindCursorInstruction(new Cursor(0), new ProgramCounter(6)),
            new DeferredSeekInstruction(new Cursor(0), new Cursor(1)),
            new RowIdInstruction(new Cursor(1), new Register(0)),
            new ResultRowInstruction(new RegisterRange(new Register(0), 1)),
            new HaltInstruction(),
        ];
        var program = new VdbeProgram(registerCount: 1, cursorCount: 2, instructions);
        using var statement = new ResumableStatement(program, [indexSource, tableSource]);

        statement.StepResumable().Should().Be(ResumableStatementStepResult.Row);
        statement.CurrentRow![0].Should().Be(SqlValue.Integer(42));
    }

    [Test]
    public void DeferredSeekRowIdReadThrowsWhenIndexCursorIsUnpositioned()
    {
        var indexSource = new VdbeCursorSource(
            [
                [SqlValue.Integer(42)],
            ],
            [42]);
        var tableSource = new VdbeCursorSource(
            [
                [SqlValue.Integer(1)],
            ],
            [42]);

        VdbeInstruction[] instructions =
        [
            new OpenReadCursorInstruction(new Cursor(0), "i", ColumnCount: 1),
            new OpenReadCursorInstruction(new Cursor(1), "t", ColumnCount: 1),
            new DeferredSeekInstruction(new Cursor(0), new Cursor(1)),
            new RowIdInstruction(new Cursor(1), new Register(0)),
            new HaltInstruction(),
        ];
        var program = new VdbeProgram(registerCount: 1, cursorCount: 2, instructions);
        using var statement = new ResumableStatement(program, [indexSource, tableSource]);

        Assert.Throws<InvalidOperationException>(() => statement.StepResumable())!
            .Message.Should().Contain("positioned on a record");
    }

    [Test]
    public void ReopeningTableCursorInvalidatesPendingDeferredSeek()
    {
        var indexSource = new VdbeCursorSource(
            [
                [SqlValue.Integer(10)],
            ],
            [10]);
        var tableSource = new VdbeCursorSource(
            [
                [SqlValue.Integer(220)],
            ],
            [10]);

        VdbeInstruction[] instructions =
        [
            new OpenReadCursorInstruction(new Cursor(0), "i", ColumnCount: 1),
            new OpenReadCursorInstruction(new Cursor(1), "t", ColumnCount: 1),
            new RewindCursorInstruction(new Cursor(0), new ProgramCounter(7)),
            new DeferredSeekInstruction(new Cursor(0), new Cursor(1)),
            new CloseCursorInstruction(new Cursor(1)),
            new OpenReadCursorInstruction(new Cursor(1), "t", ColumnCount: 1),
            new ColumnInstruction(new Cursor(1), 0, new Register(0)),
            new HaltInstruction(),
        ];
        var program = new VdbeProgram(registerCount: 1, cursorCount: 2, instructions);
        using var statement = new ResumableStatement(program, [indexSource, tableSource]);

        Assert.Throws<InvalidOperationException>(() => statement.StepResumable())!
            .Message.Should().Contain("not positioned on a row");
    }

    [Test]
    public void SeekEndThenPrevWalksRowsInReverse()
    {
        var source = new VdbeCursorSource(
            [
                [SqlValue.Integer(1)],
                [SqlValue.Integer(2)],
                [SqlValue.Integer(3)],
            ],
            [1, 2, 3]);

        VdbeInstruction[] instructions =
        [
            new OpenReadCursorInstruction(new Cursor(0), "t", ColumnCount: 1),
            new SeekEndInstruction(new Cursor(0)),
            new PrevInstruction(new Cursor(0), new ProgramCounter(4)),
            new GotoInstruction(new ProgramCounter(7)),
            new ColumnInstruction(new Cursor(0), 0, new Register(0)),
            new ResultRowInstruction(new RegisterRange(new Register(0), 1)),
            new GotoInstruction(new ProgramCounter(2)),
            new HaltInstruction(),
        ];
        var program = new VdbeProgram(registerCount: 1, cursorCount: 1, instructions);
        using var statement = new ResumableStatement(program, [source]);

        var values = new List<long>();
        while (statement.StepResumable() == ResumableStatementStepResult.Row)
            values.Add(statement.CurrentRow![0].AsInteger());

        values.Should().Equal(3L, 2L, 1L);
    }

    [Test]
    public void SeekEndThenNextExitsImmediately()
    {
        var source = new VdbeCursorSource(
            [
                [SqlValue.Integer(1)],
            ],
            [1]);

        VdbeInstruction[] instructions =
        [
            new OpenReadCursorInstruction(new Cursor(0), "t", ColumnCount: 1),
            new SeekEndInstruction(new Cursor(0)),
            new NextInstruction(new Cursor(0), new ProgramCounter(4)),
            new GotoInstruction(new ProgramCounter(7)),
            new ColumnInstruction(new Cursor(0), 0, new Register(0)),
            new ResultRowInstruction(new RegisterRange(new Register(0), 1)),
            new GotoInstruction(new ProgramCounter(2)),
            new HaltInstruction(),
        ];
        var program = new VdbeProgram(registerCount: 1, cursorCount: 1, instructions);
        using var statement = new ResumableStatement(program, [source]);

        var values = new List<long>();
        while (statement.StepResumable() == ResumableStatementStepResult.Row)
            values.Add(statement.CurrentRow![0].AsInteger());

        values.Should().BeEmpty();
    }

    [Test]
    public void ValidationRejectsDeferredSeekOnUnopenedCursors()
    {
        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(
            registerCount: 0,
            cursorCount: 2,
            [
                new DeferredSeekInstruction(new Cursor(0), new Cursor(1)),
                new HaltInstruction(),
            ]))!
            .Message.Should().Contain("uses cursor 0 before opening it");
    }

    [Test]
    public void ValidationRejectsDeferredSeekAgainstSameCursor()
    {
        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(
            registerCount: 0,
            cursorCount: 1,
            [
                new OpenReadCursorInstruction(new Cursor(0), "t", ColumnCount: 1),
                new DeferredSeekInstruction(new Cursor(0), new Cursor(0)),
                new HaltInstruction(),
            ]))!
            .Message.Should().Contain("against itself");
    }

    [Test]
    public void ValidationRejectsSeekEndOnUnopenedCursor()
    {
        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(
            registerCount: 0,
            cursorCount: 1,
            [
                new SeekEndInstruction(new Cursor(0)),
                new HaltInstruction(),
            ]))!
            .Message.Should().Contain("uses cursor 0 before opening it");
    }

    [Test]
    public void ExplainDescribesDeferredSeekAndSeekEnd()
    {
        var deferredSeek = new DeferredSeekInstruction(new Cursor(0), new Cursor(1));
        var (seekIndexCursor, seekTableCursor, seekP3, seekP4, seekComment) = VdbeExplain.Describe(deferredSeek);
        seekIndexCursor.Should().Be(0);
        seekTableCursor.Should().Be(1);
        seekP3.Should().Be(0);
        seekP4.Should().BeNull();
        seekComment.Should().Be("deferred seek table cursor 1 via index cursor 0");
        deferredSeek.Opcode.Should().Be(VdbeOpcode.DeferredSeek);

        var seekEnd = new SeekEndInstruction(new Cursor(2));
        var (endCursor, endP2, endP3, endP4, endComment) = VdbeExplain.Describe(seekEnd);
        endCursor.Should().Be(2);
        endP2.Should().Be(0);
        endP3.Should().Be(0);
        endP4.Should().BeNull();
        endComment.Should().Be("position cursor 2 past its last row for a reverse scan");
        seekEnd.Opcode.Should().Be(VdbeOpcode.SeekEnd);
    }

    private static List<SqlValue> RunBloomProbe(VdbeInstruction[] instructions, int registerCount = 2)
    {
        var program = new VdbeProgram(registerCount: registerCount, cursorCount: 1, instructions);
        using var statement = new ResumableStatement(program);

        var values = new List<SqlValue>();
        while (statement.StepResumable() == ResumableStatementStepResult.Row)
        {
            values.Add(statement.CurrentRow![0]);
        }

        return values;
    }

    /// <summary>
    /// Layout: open an ephemeral cursor (its index keys the per-cursor filter), optionally add a
    /// key with BloomFilterAdd, probe with BloomFilter, and emit one row only when the probe
    /// fell through (i.e. the key might be present or no filter exists).
    /// </summary>
    private static VdbeInstruction[] BloomProbeLayout(SqlValue insertedKey, SqlValue probeKey, bool insert)
    {
        var instructions = new List<VdbeInstruction>
        {
            new OpenEphemeralInstruction(new Cursor(0), ColumnCount: 1),
        };
        if (insert)
        {
            instructions.Add(new LoadConstantInstruction(new Register(0), insertedKey));
            instructions.Add(new BloomFilterAddInstruction(new Cursor(0), new RegisterRange(new Register(0), 1)));
        }

        // BloomFilter jump target = instructions.Count + 3 at add time = the terminal Halt.
        instructions.Add(new LoadConstantInstruction(new Register(1), probeKey));
        instructions.Add(new BloomFilterInstruction(new Cursor(0), new RegisterRange(new Register(1), 1), new ProgramCounter(instructions.Count + 3)));
        instructions.Add(new LoadConstantInstruction(new Register(0), SqlValue.Text("hit")));
        instructions.Add(new ResultRowInstruction(new RegisterRange(new Register(0), 1)));
        instructions.Add(new HaltInstruction());
        return instructions.ToArray();
    }

    [Test]
    public void BloomFilterFallsThroughWhenKeyMightBePresent()
    {
        var rows = RunBloomProbe(BloomProbeLayout(SqlValue.Integer(10), SqlValue.Integer(10), insert: true));
        rows.Should().ContainSingle();
        rows[0].AsText().Should().Be("hit");
    }

    [Test]
    public void BloomFilterJumpsWhenKeyWasNeverInserted()
    {
        var rows = RunBloomProbe(BloomProbeLayout(SqlValue.Integer(10), SqlValue.Integer(20), insert: true));
        rows.Should().BeEmpty();
    }

    [Test]
    public void BloomFilterNeverReportsNullKeys()
    {
        var rows = RunBloomProbe(BloomProbeLayout(SqlValue.Null, SqlValue.Null, insert: true));
        rows.Should().BeEmpty();
    }

    [Test]
    public void BloomFilterFallsThroughWhenCursorHasNoFilter()
    {
        var rows = RunBloomProbe(BloomProbeLayout(SqlValue.Null, SqlValue.Integer(10), insert: false));
        rows.Should().ContainSingle();
        rows[0].AsText().Should().Be("hit");
    }

    [Test]
    public void BloomFilterTreatsExactIntegersAndRealsAsTheSameDomain()
    {
        var hit = RunBloomProbe(BloomProbeLayout(SqlValue.Integer(10), SqlValue.Real(10.0), insert: true));
        hit.Should().ContainSingle();
        hit[0].AsText().Should().Be("hit");

        // 2^53 + 1 cannot be represented exactly as a double, so it stays in the integer domain
        // and must not collide with its lossy double image 2^53.
        var miss = RunBloomProbe(BloomProbeLayout(SqlValue.Integer(9007199254740993), SqlValue.Real(9007199254740992.0), insert: true));
        miss.Should().BeEmpty();
    }

    [Test]
    public void BloomFilterTreatsNegativeZeroAsZero()
    {
        var rows = RunBloomProbe(BloomProbeLayout(SqlValue.Real(-0.0), SqlValue.Real(0.0), insert: true));
        rows.Should().ContainSingle();
        rows[0].AsText().Should().Be("hit");
    }

    [Test]
    public void BloomFilterProbesCompositeKeys()
    {
        VdbeInstruction[] instructions =
        [
            new OpenEphemeralInstruction(new Cursor(0), ColumnCount: 2),
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(1)),
            new LoadConstantInstruction(new Register(1), SqlValue.Text("a")),
            new BloomFilterAddInstruction(new Cursor(0), new RegisterRange(new Register(0), 2)),
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(1)),
            new LoadConstantInstruction(new Register(1), SqlValue.Text("a")),
            new BloomFilterInstruction(new Cursor(0), new RegisterRange(new Register(0), 2), new ProgramCounter(9)),
            new LoadConstantInstruction(new Register(0), SqlValue.Text("hit")),
            new ResultRowInstruction(new RegisterRange(new Register(0), 1)),
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(1)),
            new LoadConstantInstruction(new Register(1), SqlValue.Text("b")),
            new BloomFilterInstruction(new Cursor(0), new RegisterRange(new Register(0), 2), new ProgramCounter(14)),
            new LoadConstantInstruction(new Register(0), SqlValue.Text("miss")),
            new ResultRowInstruction(new RegisterRange(new Register(0), 1)),
            new HaltInstruction(),
        ];

        var rows = RunBloomProbe(instructions);
        rows.Should().ContainSingle();
        rows[0].AsText().Should().Be("hit");
    }

    [Test]
    public void BloomFilterRejectsCompositeKeysContainingNull()
    {
        VdbeInstruction[] instructions =
        [
            new OpenEphemeralInstruction(new Cursor(0), ColumnCount: 2),
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(1)),
            new LoadConstantInstruction(new Register(1), SqlValue.Text("a")),
            new BloomFilterAddInstruction(new Cursor(0), new RegisterRange(new Register(0), 2)),
            new LoadConstantInstruction(new Register(0), SqlValue.Null),
            new LoadConstantInstruction(new Register(1), SqlValue.Text("a")),
            new BloomFilterInstruction(new Cursor(0), new RegisterRange(new Register(0), 2), new ProgramCounter(9)),
            new LoadConstantInstruction(new Register(0), SqlValue.Text("hit")),
            new ResultRowInstruction(new RegisterRange(new Register(0), 1)),
            new HaltInstruction(),
        ];

        RunBloomProbe(instructions).Should().BeEmpty();
    }

    [Test]
    public void RewindClearsTheCursorBloomFilter()
    {
        // Build a one-row ephemeral cursor and add its key to the filter, then scan the cursor
        // with Rewind/Next. Rewind removes the filter before the emptiness check, so after the
        // scan a probe of a never-inserted key at index 9 finds no filter and falls through to
        // emit "cleared". Had the filter survived, the probe would miss and jump to Halt.
        VdbeInstruction[] instructions =
        [
            new OpenEphemeralInstruction(new Cursor(0), ColumnCount: 1),
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(10)),
            new EphemeralInsertInstruction(new Cursor(0), new RegisterRange(new Register(0), 1)),
            new BloomFilterAddInstruction(new Cursor(0), new RegisterRange(new Register(0), 1)),
            new RewindCursorInstruction(new Cursor(0), new ProgramCounter(10)),
            new ColumnInstruction(new Cursor(0), ColumnIndex: 0, new Register(1)),
            new ResultRowInstruction(new RegisterRange(new Register(1), 1)),
            new NextInstruction(new Cursor(0), new ProgramCounter(5)),
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(999)),
            new BloomFilterInstruction(new Cursor(0), new RegisterRange(new Register(0), 1), new ProgramCounter(12)),
            new LoadConstantInstruction(new Register(0), SqlValue.Text("cleared")),
            new ResultRowInstruction(new RegisterRange(new Register(0), 1)),
            new HaltInstruction(),
        ];

        var rows = RunBloomProbe(instructions);
        rows.Should().HaveCount(2);
        rows[0].Should().Be(SqlValue.Integer(10));
        rows[1].AsText().Should().Be("cleared");
    }

    [Test]
    public void ExplainDescribesBloomFilterOpcodes()
    {
        var bloomAdd = new BloomFilterAddInstruction(new Cursor(2), new RegisterRange(new Register(4), 2));
        var (addCursor, addP2, addP3, addP4, addComment) = VdbeExplain.Describe(bloomAdd);
        addCursor.Should().Be(2);
        addP2.Should().Be(0);
        addP3.Should().Be(4);
        addP4.Should().Be("r[4..5]");
        addComment.Should().Be("bloom_filter_add(r[4..5])");
        bloomAdd.Opcode.Should().Be(VdbeOpcode.BloomFilterAdd);

        var bloomFilter = new BloomFilterInstruction(new Cursor(2), new RegisterRange(new Register(6), 1), new ProgramCounter(9));
        var (probeCursor, probeP2, probeP3, probeP4, probeComment) = VdbeExplain.Describe(bloomFilter);
        probeCursor.Should().Be(2);
        probeP2.Should().Be(9);
        probeP3.Should().Be(6);
        probeP4.Should().Be("r[6]");
        probeComment.Should().Be("if !bloom_filter(r[6]) goto 9");
        bloomFilter.Opcode.Should().Be(VdbeOpcode.BloomFilter);
    }

    [Test]
    public void ValidationRejectsEmptyBloomFilterKeys()
    {
        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(
            registerCount: 2,
            cursorCount: 1,
            [
                new OpenEphemeralInstruction(new Cursor(0), ColumnCount: 1),
                new BloomFilterAddInstruction(new Cursor(0), new RegisterRange(new Register(0), 0)),
                new HaltInstruction(),
            ]))!
            .Message.Should().Contain("requires a positive key width");

        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(
            registerCount: 2,
            cursorCount: 1,
            [
                new OpenEphemeralInstruction(new Cursor(0), ColumnCount: 1),
                new BloomFilterInstruction(new Cursor(0), new RegisterRange(new Register(0), 0), new ProgramCounter(2)),
                new HaltInstruction(),
            ]))!
            .Message.Should().Contain("requires a positive key width");
    }

    [Test]
    public void HashBuildProbeNextYieldsAllMatchesInInsertionOrder()
    {
        // Build side (cursor 0): rows (1,a), (2,b), (1,c) -> rowids 1, 2, 3.
        // Probe side (cursor 1): a single row with key 1. HashProbe finds the first match,
        // then HashNext walks the remaining matches in bucket insertion order.
        VdbeInstruction[] instructions =
        [
            new OpenEphemeralInstruction(new Cursor(0), ColumnCount: 2),
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(1)),
            new LoadConstantInstruction(new Register(1), SqlValue.Text("a")),
            new EphemeralInsertInstruction(new Cursor(0), new RegisterRange(new Register(0), 2)),
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(2)),
            new LoadConstantInstruction(new Register(1), SqlValue.Text("b")),
            new EphemeralInsertInstruction(new Cursor(0), new RegisterRange(new Register(0), 2)),
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(1)),
            new LoadConstantInstruction(new Register(1), SqlValue.Text("c")),
            new EphemeralInsertInstruction(new Cursor(0), new RegisterRange(new Register(0), 2)),
            new RewindCursorInstruction(new Cursor(0), new ProgramCounter(26)),
            new ColumnInstruction(new Cursor(0), ColumnIndex: 0, new Register(2)),
            new ColumnInstruction(new Cursor(0), ColumnIndex: 1, new Register(3)),
            new HashBuildInstruction(new Cursor(0), new RegisterRange(new Register(2), 1), HashTableId: 0, MemoryBudget: 0, Collations: [null], Payload: new RegisterRange(new Register(3), 1), TrackMatched: false),
            new NextInstruction(new Cursor(0), new ProgramCounter(11)),
            new HashBuildFinalizeInstruction(HashTableId: 0),
            new OpenEphemeralInstruction(new Cursor(1), ColumnCount: 1),
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(1)),
            new EphemeralInsertInstruction(new Cursor(1), new RegisterRange(new Register(0), 1)),
            new RewindCursorInstruction(new Cursor(1), new ProgramCounter(26)),
            new ColumnInstruction(new Cursor(1), ColumnIndex: 0, new Register(2)),
            new HashProbeInstruction(HashTableId: 0, new RegisterRange(new Register(2), 1), new Register(4), PayloadDestination: new RegisterRange(new Register(5), 1), NotFoundTarget: new ProgramCounter(26)),
            new ResultRowInstruction(new RegisterRange(new Register(4), 2)),
            new HashNextInstruction(HashTableId: 0, new Register(4), PayloadDestination: new RegisterRange(new Register(5), 1), ExhaustedTarget: new ProgramCounter(26)),
            new ResultRowInstruction(new RegisterRange(new Register(4), 2)),
            new GotoInstruction(new ProgramCounter(23)),
            new HaltInstruction(),
        ];

        var rows = RunHashProgram(instructions, registerCount: 6, cursorCount: 2, hashTableCount: 1);
        rows.Should().HaveCount(4);
        rows[0].Should().Be(SqlValue.Integer(1));
        rows[1].Should().Be(SqlValue.Text("a"));
        rows[2].Should().Be(SqlValue.Integer(3));
        rows[3].Should().Be(SqlValue.Text("c"));
    }

    [Test]
    public void HashProbeJumpsWhenTableIsMissingOrKeyAbsent()
    {
        // Table 1 is declared but never created, so HashProbe must jump to Halt without a row.
        VdbeInstruction[] instructions =
        [
            new LoadConstantInstruction(new Register(1), SqlValue.Integer(1)),
            new HashProbeInstruction(HashTableId: 1, new RegisterRange(new Register(1), 1), new Register(2), PayloadDestination: null, NotFoundTarget: new ProgramCounter(3)),
            new ResultRowInstruction(new RegisterRange(new Register(1), 1)),
            new HaltInstruction(),
        ];

        RunHashProgram(instructions, registerCount: 3, cursorCount: 1, hashTableCount: 2).Should().BeEmpty();
    }

    [Test]
    public void HashProbeJumpsWhenKeyIsAbsent()
    {
        // Build one row with key 1, finalize, then probe for key 999, which must jump to Halt.
        VdbeInstruction[] instructions =
        [
            new OpenEphemeralInstruction(new Cursor(0), ColumnCount: 1),
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(1)),
            new EphemeralInsertInstruction(new Cursor(0), new RegisterRange(new Register(0), 1)),
            new RewindCursorInstruction(new Cursor(0), new ProgramCounter(9)),
            new ColumnInstruction(new Cursor(0), ColumnIndex: 0, new Register(1)),
            new HashBuildInstruction(new Cursor(0), new RegisterRange(new Register(1), 1), HashTableId: 0, MemoryBudget: 0, Collations: [null], Payload: null, TrackMatched: false),
            new NextInstruction(new Cursor(0), new ProgramCounter(4)),
            new HashBuildFinalizeInstruction(HashTableId: 0),
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(999)),
            new HashProbeInstruction(HashTableId: 0, new RegisterRange(new Register(0), 1), new Register(2), PayloadDestination: null, NotFoundTarget: new ProgramCounter(12)),
            new LoadConstantInstruction(new Register(0), SqlValue.Text("found")),
            new ResultRowInstruction(new RegisterRange(new Register(0), 1)),
            new HaltInstruction(),
        ];

        RunHashProgram(instructions, registerCount: 3, cursorCount: 1, hashTableCount: 1).Should().BeEmpty();
    }

    [Test]
    public void HashProbeRequiresFinalizedTable()
    {
        // HashDistinct lazily creates table 0 in the Building state; probing it must throw.
        VdbeInstruction[] instructions =
        [
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(1)),
            new HashDistinctInstruction(new RegisterRange(new Register(0), 1), Collations: [null], HashTableId: 0, DuplicateTarget: new ProgramCounter(3)),
            new HashProbeInstruction(HashTableId: 0, new RegisterRange(new Register(0), 1), new Register(1), PayloadDestination: null, NotFoundTarget: new ProgramCounter(3)),
            new HaltInstruction(),
        ];

        var program = new VdbeProgram(registerCount: 2, cursorCount: 1, instructions, hashTableCount: 1);
        using var statement = new ResumableStatement(program);
        Assert.Throws<InvalidOperationException>(() =>
        {
            while (statement.StepResumable() == ResumableStatementStepResult.Row)
            {
            }
        })!.Message.Should().Contain("Hash table must be finalized before probing.");
    }

    [Test]
    public void HashNextWithoutProbeThrows()
    {
        // Lazily create table 0 (HashDistinct), finalize it, then HashNext without a probe.
        VdbeInstruction[] instructions =
        [
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(1)),
            new HashDistinctInstruction(new RegisterRange(new Register(0), 1), Collations: [null], HashTableId: 0, DuplicateTarget: new ProgramCounter(4)),
            new HashBuildFinalizeInstruction(HashTableId: 0),
            new HashNextInstruction(HashTableId: 0, new Register(0), PayloadDestination: null, ExhaustedTarget: new ProgramCounter(4)),
            new HaltInstruction(),
        ];

        var program = new VdbeProgram(registerCount: 1, cursorCount: 1, instructions, hashTableCount: 1);
        using var statement = new ResumableStatement(program);
        Assert.Throws<InvalidOperationException>(() =>
        {
            while (statement.StepResumable() == ResumableStatementStepResult.Row)
            {
            }
        })!.Message.Should().Contain("HashNext requires a preceding HashProbe on the same hash table.");
    }

    [Test]
    public void HashNextOnMissingTableThrows()
    {
        VdbeInstruction[] instructions =
        [
            new HashNextInstruction(HashTableId: 0, new Register(0), PayloadDestination: null, ExhaustedTarget: new ProgramCounter(1)),
            new HaltInstruction(),
        ];

        var program = new VdbeProgram(registerCount: 1, cursorCount: 1, instructions, hashTableCount: 1);
        using var statement = new ResumableStatement(program);
        Assert.Throws<InvalidOperationException>(() => statement.StepResumable())!
            .Message.Should().Contain("Hash table not found with ID: 0");
    }

    [Test]
    public void HashNextUnmatchedOnMissingTableThrows()
    {
        VdbeInstruction[] instructions =
        [
            new HashNextUnmatchedInstruction(HashTableId: 0, new Register(0), PayloadDestination: null, ExhaustedTarget: new ProgramCounter(1)),
            new HaltInstruction(),
        ];

        var program = new VdbeProgram(registerCount: 1, cursorCount: 1, instructions, hashTableCount: 1);
        using var statement = new ResumableStatement(program);
        Assert.Throws<InvalidOperationException>(() => statement.StepResumable())!
            .Message.Should().Contain("Hash table not found with ID: 0");
    }

    [Test]
    public void HashBuildRejectsInsertsAfterFinalize()
    {
        // HashDistinct lazily creates table 0 in the Building state, finalize flips it to
        // Probing, and the following HashBuild must reject the insert. The cursor stays
        // positioned on its single row so HashBuild reaches the state check.
        VdbeInstruction[] instructions =
        [
            new OpenEphemeralInstruction(new Cursor(0), ColumnCount: 1),
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(1)),
            new EphemeralInsertInstruction(new Cursor(0), new RegisterRange(new Register(0), 1)),
            new RewindCursorInstruction(new Cursor(0), new ProgramCounter(7)),
            new HashDistinctInstruction(new RegisterRange(new Register(0), 1), Collations: [null], HashTableId: 0, DuplicateTarget: new ProgramCounter(7)),
            new HashBuildFinalizeInstruction(HashTableId: 0),
            new HashBuildInstruction(new Cursor(0), new RegisterRange(new Register(0), 1), HashTableId: 0, MemoryBudget: 0, Collations: [null], Payload: null, TrackMatched: false),
            new HaltInstruction(),
        ];

        var program = new VdbeProgram(registerCount: 1, cursorCount: 1, instructions, hashTableCount: 1);
        using var statement = new ResumableStatement(program);
        Assert.Throws<InvalidOperationException>(() =>
        {
            while (statement.StepResumable() == ResumableStatementStepResult.Row)
            {
            }
        })!.Message.Should().Contain("Hash table can only accept inserts while building.");
    }

    [Test]
    public void HashDistinctDeduplicatesKeysIncludingNull()
    {
        // Probe rows: 1, 1, NULL, NULL. HashDistinct keeps the first 1 and the first NULL
        // (NULL == NULL for distinctness) and jumps on each duplicate. Every fall-through emits 10.
        VdbeInstruction[] instructions =
        [
            new OpenEphemeralInstruction(new Cursor(0), ColumnCount: 1),
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(1)),
            new EphemeralInsertInstruction(new Cursor(0), new RegisterRange(new Register(0), 1)),
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(1)),
            new EphemeralInsertInstruction(new Cursor(0), new RegisterRange(new Register(0), 1)),
            new LoadConstantInstruction(new Register(0), SqlValue.Null),
            new EphemeralInsertInstruction(new Cursor(0), new RegisterRange(new Register(0), 1)),
            new LoadConstantInstruction(new Register(0), SqlValue.Null),
            new EphemeralInsertInstruction(new Cursor(0), new RegisterRange(new Register(0), 1)),
            new RewindCursorInstruction(new Cursor(0), new ProgramCounter(15)),
            new ColumnInstruction(new Cursor(0), ColumnIndex: 0, new Register(1)),
            new HashDistinctInstruction(new RegisterRange(new Register(1), 1), Collations: [null], HashTableId: 0, DuplicateTarget: new ProgramCounter(14)),
            new LoadConstantInstruction(new Register(2), SqlValue.Integer(10)),
            new ResultRowInstruction(new RegisterRange(new Register(2), 1)),
            new NextInstruction(new Cursor(0), new ProgramCounter(10)),
            new HaltInstruction(),
        ];

        var rows = RunHashProgram(instructions, registerCount: 3, cursorCount: 1, hashTableCount: 1);
        rows.Should().HaveCount(2);
        rows[0].Should().Be(SqlValue.Integer(10));
        rows[1].Should().Be(SqlValue.Integer(10));
    }

    [Test]
    public void HashClearResetsTableToBuilding()
    {
        // HashDistinct lazily creates table 0 in Building, Finalize flips it to Probing,
        // and Clear must reset it to Building so the second HashDistinct can insert again
        // (otherwise InsertDistinct throws "can only accept inserts while building").
        VdbeInstruction[] instructions =
        [
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(1)),
            new HashDistinctInstruction(new RegisterRange(new Register(0), 1), Collations: [null], HashTableId: 0, DuplicateTarget: new ProgramCounter(7)),
            new HashBuildFinalizeInstruction(HashTableId: 0),
            new HashClearInstruction(HashTableId: 0),
            new HashDistinctInstruction(new RegisterRange(new Register(0), 1), Collations: [null], HashTableId: 0, DuplicateTarget: new ProgramCounter(7)),
            new LoadConstantInstruction(new Register(1), SqlValue.Text("cleared")),
            new ResultRowInstruction(new RegisterRange(new Register(1), 1)),
            new HaltInstruction(),
        ];

        var rows = RunHashProgram(instructions, registerCount: 2, cursorCount: 1, hashTableCount: 1);
        rows.Should().HaveCount(1);
        rows[0].Should().Be(SqlValue.Text("cleared"));
    }

    [Test]
    public void HashCloseRemovesTheTable()
    {
        VdbeInstruction[] instructions =
        [
            new HashBuildFinalizeInstruction(HashTableId: 0),
            new HashCloseInstruction(HashTableId: 0),
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(1)),
            new HashProbeInstruction(HashTableId: 0, new RegisterRange(new Register(0), 1), new Register(1), PayloadDestination: null, NotFoundTarget: new ProgramCounter(6)),
            new LoadConstantInstruction(new Register(0), SqlValue.Text("found")),
            new ResultRowInstruction(new RegisterRange(new Register(0), 1)),
            new HaltInstruction(),
        ];

        RunHashProgram(instructions, registerCount: 2, cursorCount: 1, hashTableCount: 1).Should().BeEmpty();
    }

    [Test]
    public void HashMarkMatchedAndUnmatchedScanYieldOnlyUnmatchedRows()
    {
        // Build rows (1,a) rowid 1, (2,b) rowid 2, (1,c) rowid 3, track matched bits.
        // Probe key 1 marks both matches, so the unmatched scan returns only rowid 2.
        VdbeInstruction[] instructions =
        [
            new OpenEphemeralInstruction(new Cursor(0), ColumnCount: 2),
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(1)),
            new LoadConstantInstruction(new Register(1), SqlValue.Text("a")),
            new EphemeralInsertInstruction(new Cursor(0), new RegisterRange(new Register(0), 2)),
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(2)),
            new LoadConstantInstruction(new Register(1), SqlValue.Text("b")),
            new EphemeralInsertInstruction(new Cursor(0), new RegisterRange(new Register(0), 2)),
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(1)),
            new LoadConstantInstruction(new Register(1), SqlValue.Text("c")),
            new EphemeralInsertInstruction(new Cursor(0), new RegisterRange(new Register(0), 2)),
            new RewindCursorInstruction(new Cursor(0), new ProgramCounter(15)),
            new ColumnInstruction(new Cursor(0), ColumnIndex: 0, new Register(2)),
            new ColumnInstruction(new Cursor(0), ColumnIndex: 1, new Register(3)),
            new HashBuildInstruction(new Cursor(0), new RegisterRange(new Register(2), 1), HashTableId: 0, MemoryBudget: 0, Collations: [null], Payload: new RegisterRange(new Register(3), 1), TrackMatched: true),
            new NextInstruction(new Cursor(0), new ProgramCounter(11)),
            new HashBuildFinalizeInstruction(HashTableId: 0),
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(1)),
            new HashProbeInstruction(HashTableId: 0, new RegisterRange(new Register(0), 1), new Register(4), PayloadDestination: null, NotFoundTarget: new ProgramCounter(22)),
            new HashMarkMatchedInstruction(HashTableId: 0),
            new HashNextInstruction(HashTableId: 0, new Register(4), PayloadDestination: null, ExhaustedTarget: new ProgramCounter(22)),
            new HashMarkMatchedInstruction(HashTableId: 0),
            new GotoInstruction(new ProgramCounter(19)),
            new HashScanUnmatchedInstruction(HashTableId: 0, new Register(5), PayloadDestination: new RegisterRange(new Register(6), 1), ExhaustedTarget: new ProgramCounter(27)),
            new ResultRowInstruction(new RegisterRange(new Register(5), 2)),
            new HashNextUnmatchedInstruction(HashTableId: 0, new Register(5), PayloadDestination: new RegisterRange(new Register(6), 1), ExhaustedTarget: new ProgramCounter(27)),
            new ResultRowInstruction(new RegisterRange(new Register(5), 2)),
            new GotoInstruction(new ProgramCounter(24)),
            new HaltInstruction(),
        ];

        var rows = RunHashProgram(instructions, registerCount: 7, cursorCount: 1, hashTableCount: 1);
        rows.Should().HaveCount(2);
        rows[0].Should().Be(SqlValue.Integer(2));
        rows[1].Should().Be(SqlValue.Text("b"));
    }

    [Test]
    public void HashMarkMatchedWithoutCurrentMatchThrows()
    {
        // TrackMatched:true requires HashBuild (HashDistinct lazily creates tables without
        // matched-bit tracking, where MarkCurrentMatched silently no-ops). Build one row,
        // then mark with no preceding probe/next: the entry index is invalid.
        VdbeInstruction[] instructions =
        [
            new OpenEphemeralInstruction(new Cursor(0), ColumnCount: 1),
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(1)),
            new EphemeralInsertInstruction(new Cursor(0), new RegisterRange(new Register(0), 1)),
            new RewindCursorInstruction(new Cursor(0), new ProgramCounter(7)),
            new HashBuildInstruction(new Cursor(0), new RegisterRange(new Register(0), 1), HashTableId: 0, MemoryBudget: 0, Collations: [null], Payload: null, TrackMatched: true),
            new NextInstruction(new Cursor(0), new ProgramCounter(4)),
            new HashMarkMatchedInstruction(HashTableId: 0),
            new HaltInstruction(),
        ];

        var program = new VdbeProgram(registerCount: 1, cursorCount: 1, instructions, hashTableCount: 1);
        using var statement = new ResumableStatement(program);
        Assert.Throws<InvalidOperationException>(() =>
        {
            while (statement.StepResumable() == ResumableStatementStepResult.Row)
            {
            }
        })!.Message.Should().Contain("HashMarkMatched requires a current match from HashProbe or HashNext.");
    }

    [Test]
    public void ExplainDescribesHashJoinOpcodes()
    {
        var hashBuild = new HashBuildInstruction(new Cursor(2), new RegisterRange(new Register(4), 2), HashTableId: 0, MemoryBudget: 4096, Collations: [null, "BINARY"], Payload: new RegisterRange(new Register(6), 1), TrackMatched: true);
        var (buildCursor, buildP2, buildP3, buildP4, buildComment) = VdbeExplain.Describe(hashBuild);
        buildCursor.Should().Be(2);
        buildP2.Should().Be(4);
        buildP3.Should().Be(2);
        buildP4.Should().Be("r[4..5]");
        buildComment.Should().Be("hash_build c[2] keys r[4..5] -> hash_table[0] budget=4096 payload r[6] track_matched");
        hashBuild.Opcode.Should().Be(VdbeOpcode.HashBuild);

        var hashDistinct = new HashDistinctInstruction(new RegisterRange(new Register(4), 1), Collations: [null], HashTableId: 1, DuplicateTarget: new ProgramCounter(9));
        var (distinctP1, distinctP2, distinctP3, distinctP4, distinctComment) = VdbeExplain.Describe(hashDistinct);
        distinctP1.Should().Be(1);
        distinctP2.Should().Be(4);
        distinctP3.Should().Be(1);
        distinctP4.Should().Be("r[4]");
        distinctComment.Should().Be("hash_distinct r[4] jmp=9");
        hashDistinct.Opcode.Should().Be(VdbeOpcode.HashDistinct);

        var hashFinalize = new HashBuildFinalizeInstruction(HashTableId: 2);
        var (finalizeP1, finalizeP2, finalizeP3, finalizeP4, finalizeComment) = VdbeExplain.Describe(hashFinalize);
        finalizeP1.Should().Be(2);
        finalizeP2.Should().Be(0);
        finalizeP3.Should().Be(0);
        finalizeP4.Should().BeNull();
        finalizeComment.Should().Be("hash_build_finalize hash_table[2]");
        hashFinalize.Opcode.Should().Be(VdbeOpcode.HashBuildFinalize);

        var hashProbe = new HashProbeInstruction(HashTableId: 0, new RegisterRange(new Register(4), 2), new Register(8), PayloadDestination: new RegisterRange(new Register(9), 1), NotFoundTarget: new ProgramCounter(12));
        var (probeP1, probeP2, probeP3, probeP4, probeComment) = VdbeExplain.Describe(hashProbe);
        probeP1.Should().Be(0);
        probeP2.Should().Be(4);
        probeP3.Should().Be(2);
        probeP4.Should().Be("r[4..5]");
        probeComment.Should().Be("r[8]=hash_probe(r[4..5]) goto 12 if not found payload r[9]");
        hashProbe.Opcode.Should().Be(VdbeOpcode.HashProbe);

        var hashNext = new HashNextInstruction(HashTableId: 0, new Register(8), PayloadDestination: new RegisterRange(new Register(9), 1), ExhaustedTarget: new ProgramCounter(14));
        var (nextP1, nextP2, nextP3, nextP4, nextComment) = VdbeExplain.Describe(hashNext);
        nextP1.Should().Be(0);
        nextP2.Should().Be(8);
        nextP3.Should().Be(14);
        nextP4.Should().BeNull();
        nextComment.Should().Be("r[8]=hash_next goto 14 if exhausted payload r[9]");
        hashNext.Opcode.Should().Be(VdbeOpcode.HashNext);

        var hashClose = new HashCloseInstruction(HashTableId: 3);
        var (closeP1, closeP2, closeP3, closeP4, closeComment) = VdbeExplain.Describe(hashClose);
        closeP1.Should().Be(3);
        closeP2.Should().Be(0);
        closeP3.Should().Be(0);
        closeP4.Should().BeNull();
        closeComment.Should().Be("hash_close hash_table[3]");
        hashClose.Opcode.Should().Be(VdbeOpcode.HashClose);

        var hashClear = new HashClearInstruction(HashTableId: 3);
        var (clearP1, clearP2, clearP3, clearP4, clearComment) = VdbeExplain.Describe(hashClear);
        clearP1.Should().Be(3);
        clearP2.Should().Be(0);
        clearP3.Should().Be(0);
        clearP4.Should().BeNull();
        clearComment.Should().Be("hash_clear hash_table[3]");
        hashClear.Opcode.Should().Be(VdbeOpcode.HashClear);

        var hashMark = new HashMarkMatchedInstruction(HashTableId: 3);
        var (markP1, markP2, markP3, markP4, markComment) = VdbeExplain.Describe(hashMark);
        markP1.Should().Be(3);
        markP2.Should().Be(0);
        markP3.Should().Be(0);
        markP4.Should().BeNull();
        markComment.Should().Be("hash_mark_matched hash_table[3]");
        hashMark.Opcode.Should().Be(VdbeOpcode.HashMarkMatched);

        var hashReset = new HashResetMatchedInstruction(HashTableId: 3);
        var (resetP1, resetP2, resetP3, resetP4, resetComment) = VdbeExplain.Describe(hashReset);
        resetP1.Should().Be(3);
        resetP2.Should().Be(0);
        resetP3.Should().Be(0);
        resetP4.Should().BeNull();
        resetComment.Should().Be("hash_reset_matched hash_table[3]");
        hashReset.Opcode.Should().Be(VdbeOpcode.HashResetMatched);

        var hashScanUnmatched = new HashScanUnmatchedInstruction(HashTableId: 0, new Register(8), PayloadDestination: new RegisterRange(new Register(9), 2), ExhaustedTarget: new ProgramCounter(16));
        var (scanP1, scanP2, scanP3, scanP4, scanComment) = VdbeExplain.Describe(hashScanUnmatched);
        scanP1.Should().Be(0);
        scanP2.Should().Be(8);
        scanP3.Should().Be(16);
        scanP4.Should().BeNull();
        scanComment.Should().Be("r[8]=hash_scan_unmatched goto 16 if exhausted payload r[9..10]");
        hashScanUnmatched.Opcode.Should().Be(VdbeOpcode.HashScanUnmatched);

        var hashNextUnmatched = new HashNextUnmatchedInstruction(HashTableId: 0, new Register(8), PayloadDestination: new RegisterRange(new Register(9), 2), ExhaustedTarget: new ProgramCounter(18));
        var (nextUnmatchedP1, nextUnmatchedP2, nextUnmatchedP3, nextUnmatchedP4, nextUnmatchedComment) = VdbeExplain.Describe(hashNextUnmatched);
        nextUnmatchedP1.Should().Be(0);
        nextUnmatchedP2.Should().Be(8);
        nextUnmatchedP3.Should().Be(18);
        nextUnmatchedP4.Should().BeNull();
        nextUnmatchedComment.Should().Be("r[8]=hash_next_unmatched goto 18 if exhausted payload r[9..10]");
        hashNextUnmatched.Opcode.Should().Be(VdbeOpcode.HashNextUnmatched);
    }

    [Test]
    public void ValidationRejectsMalformedHashJoinBytecode()
    {
        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(
            registerCount: 2,
            cursorCount: 1,
            [
                new ColumnInstruction(new Cursor(0), ColumnIndex: 0, new Register(0)),
                new HaltInstruction(),
            ],
            hashTableCount: 1))!
            .Message.Should().Contain("uses cursor 0 before opening it");

        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(
            registerCount: 2,
            cursorCount: 1,
            [
                new OpenEphemeralInstruction(new Cursor(0), ColumnCount: 1),
                new HashBuildInstruction(new Cursor(0), new RegisterRange(new Register(0), 0), HashTableId: 0, MemoryBudget: 0, Collations: [], Payload: null, TrackMatched: false),
                new HaltInstruction(),
            ],
            hashTableCount: 1))!
            .Message.Should().Contain("HashBuild requires a positive key width");

        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(
            registerCount: 2,
            cursorCount: 1,
            [
                new OpenEphemeralInstruction(new Cursor(0), ColumnCount: 1),
                new HashBuildInstruction(new Cursor(0), new RegisterRange(new Register(0), 1), HashTableId: 9, MemoryBudget: 0, Collations: [null], Payload: null, TrackMatched: false),
                new HaltInstruction(),
            ],
            hashTableCount: 1))!
            .Message.Should().Contain("references hash table 9, but the program has 1 hash tables");

        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(
            registerCount: 2,
            cursorCount: 1,
            [
                new OpenEphemeralInstruction(new Cursor(0), ColumnCount: 1),
                new HashBuildInstruction(new Cursor(0), new RegisterRange(new Register(0), 1), HashTableId: 0, MemoryBudget: -1, Collations: [null], Payload: null, TrackMatched: false),
                new HaltInstruction(),
            ],
            hashTableCount: 1))!
            .Message.Should().Contain("HashBuild has a negative memory budget of -1");

        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(
            registerCount: 2,
            cursorCount: 1,
            [
                new OpenEphemeralInstruction(new Cursor(0), ColumnCount: 1),
                new HashBuildInstruction(new Cursor(0), new RegisterRange(new Register(0), 1), HashTableId: 0, MemoryBudget: 0, Collations: [null, null], Payload: null, TrackMatched: false),
                new HaltInstruction(),
            ],
            hashTableCount: 1))!
            .Message.Should().Contain("declares 2 collations for 1 key columns");

        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(
            registerCount: 2,
            cursorCount: 1,
            [
                new OpenEphemeralInstruction(new Cursor(0), ColumnCount: 1),
                new HashBuildInstruction(new Cursor(0), new RegisterRange(new Register(0), 1), HashTableId: 0, MemoryBudget: 0, Collations: ["CUSTOM"], Payload: null, TrackMatched: false),
                new HaltInstruction(),
            ],
            hashTableCount: 1))!
            .Message.Should().Contain("uses unsupported collation 'CUSTOM' for key column 0");

        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(
            registerCount: 3,
            cursorCount: 1,
            [
                new OpenEphemeralInstruction(new Cursor(0), ColumnCount: 1),
                new HashBuildInstruction(new Cursor(0), new RegisterRange(new Register(0), 1), HashTableId: 0, MemoryBudget: 0, Collations: [null], Payload: new RegisterRange(new Register(1), 0), TrackMatched: false),
                new HaltInstruction(),
            ],
            hashTableCount: 1))!
            .Message.Should().Contain("HashBuild payload range must carry at least one register");

        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(
            registerCount: 2,
            cursorCount: 1,
            [
                new HashDistinctInstruction(new RegisterRange(new Register(0), 0), Collations: [], HashTableId: 0, DuplicateTarget: new ProgramCounter(1)),
                new HaltInstruction(),
            ],
            hashTableCount: 1))!
            .Message.Should().Contain("HashDistinct requires a positive key width");

        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(
            registerCount: 3,
            cursorCount: 1,
            [
                new HashProbeInstruction(HashTableId: 0, new RegisterRange(new Register(0), 0), new Register(1), PayloadDestination: null, NotFoundTarget: new ProgramCounter(1)),
                new HaltInstruction(),
            ],
            hashTableCount: 1))!
            .Message.Should().Contain("HashProbe requires a positive key width");

        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(
            registerCount: 3,
            cursorCount: 1,
            [
                new HashProbeInstruction(HashTableId: 0, new RegisterRange(new Register(0), 1), new Register(1), PayloadDestination: new RegisterRange(new Register(2), 0), NotFoundTarget: new ProgramCounter(1)),
                new HaltInstruction(),
            ],
            hashTableCount: 1))!
            .Message.Should().Contain("HashProbe payload range must carry at least one register");

        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(
            registerCount: 2,
            cursorCount: 1,
            [
                new HashNextInstruction(HashTableId: 0, new Register(0), PayloadDestination: new RegisterRange(new Register(1), 0), ExhaustedTarget: new ProgramCounter(1)),
                new HaltInstruction(),
            ],
            hashTableCount: 1))!
            .Message.Should().Contain("HashNext payload range must carry at least one register");

        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(
            registerCount: 2,
            cursorCount: 1,
            [
                new HashScanUnmatchedInstruction(HashTableId: 0, new Register(0), PayloadDestination: new RegisterRange(new Register(1), 0), ExhaustedTarget: new ProgramCounter(1)),
                new HaltInstruction(),
            ],
            hashTableCount: 1))!
            .Message.Should().Contain("HashScanUnmatched payload range must carry at least one register");

        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(
            registerCount: 2,
            cursorCount: 1,
            [
                new HashNextUnmatchedInstruction(HashTableId: 0, new Register(0), PayloadDestination: new RegisterRange(new Register(1), 0), ExhaustedTarget: new ProgramCounter(1)),
                new HaltInstruction(),
            ],
            hashTableCount: 1))!
            .Message.Should().Contain("HashNextUnmatched payload range must carry at least one register");

        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(
            registerCount: 1,
            cursorCount: 1,
            [
                new HashBuildFinalizeInstruction(HashTableId: 3),
                new HaltInstruction(),
            ],
            hashTableCount: 1))!
            .Message.Should().Contain("references hash table 3, but the program has 1 hash tables");

        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(
            registerCount: 1,
            cursorCount: 1,
            [
                new HashCloseInstruction(HashTableId: -1),
                new HaltInstruction(),
            ],
            hashTableCount: 1))!
            .Message.Should().Contain("references hash table -1, but the program has 1 hash tables");
    }

    private static List<SqlValue> RunHashProgram(VdbeInstruction[] instructions, int registerCount, int cursorCount, int hashTableCount)
    {
        var program = new VdbeProgram(registerCount: registerCount, cursorCount: cursorCount, instructions, hashTableCount: hashTableCount);
        using var statement = new ResumableStatement(program);

        var values = new List<SqlValue>();
        while (statement.StepResumable() == ResumableStatementStepResult.Row)
        {
            values.AddRange(statement.CurrentRow!);
        }

        return values;
    }
}
