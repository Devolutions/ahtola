using System.Runtime.InteropServices;
using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

public sealed class SqlValueBlobSnapshotIsolationAdversarialTests
{
    [Test]
    public void BoundAndReboundBlobsRemainStableAfterInputAndExposedBufferMutation()
    {
        using var connection = new EmbeddedDatabase().Connect();
        using var statement = connection.Prepare("VALUES (?, ?), (?, ?)");

        var initialSources = new byte[][]
        {
            [0x01, 0x02],
            [0x03, 0x04],
            [0x05, 0x06],
            [0x07, 0x08],
        };
        var initialExpected = initialSources.Select(source => source.ToArray()).ToArray();
        var initialValues = initialSources.Select(source => SqlValue.Blob(source)).ToArray();

        BindAndCorrupt(statement, initialSources, initialValues);
        initialValues.Should().Equal(initialExpected.Select(bytes => SqlValue.Blob(bytes)));
        SqliteRecordCodec.Decode(SqliteRecordCodec.Encode(initialValues))
            .Should().Equal(initialExpected.Select(bytes => SqlValue.Blob(bytes)));
        AssertEmittedRowsAreStable(statement, initialExpected);

        statement.Reset();

        var reboundSources = new byte[][]
        {
            [0x11, 0x12],
            [0x13, 0x14],
            [0x15, 0x16],
            [0x17, 0x18],
        };
        var reboundExpected = reboundSources.Select(source => source.ToArray()).ToArray();
        var reboundValues = reboundSources.Select(source => SqlValue.Blob(source)).ToArray();

        BindAndCorrupt(statement, reboundSources, reboundValues);
        reboundValues.Should().Equal(reboundExpected.Select(bytes => SqlValue.Blob(bytes)));
        SqliteRecordCodec.Decode(SqliteRecordCodec.Encode(reboundValues))
            .Should().Equal(reboundExpected.Select(bytes => SqlValue.Blob(bytes)));
        AssertEmittedRowsAreStable(statement, reboundExpected);
    }

    private static void BindAndCorrupt(
        EmbeddedStatement statement,
        IReadOnlyList<byte[]> sourceBuffers,
        IReadOnlyList<SqlValue> values)
    {
        for (var index = 0; index < values.Count; index++)
            statement.Bind(index + 1, values[index]);

        for (var index = 0; index < sourceBuffers.Count; index++)
            sourceBuffers[index][0] = unchecked((byte)(0x80 + index));

        for (var index = 0; index < values.Count; index++)
            MutateExposedBackingBuffer(values[index], unchecked((byte)(0xC0 + index)));
    }

    private static void AssertEmittedRowsAreStable(EmbeddedStatement statement, IReadOnlyList<byte[]> expected)
    {
        for (var row = 0; row < 2; row++)
        {
            statement.Step().Should().Be(StatementStepResult.Row);
            for (var column = 0; column < 2; column++)
            {
                var expectedBlob = expected[(row * 2) + column];
                var emitted = statement.GetValue(column);
                emitted.AsBlob().ToArray().Should().Equal(expectedBlob);

                MutateExposedBackingBuffer(emitted, 0xFF);

                statement.GetValue(column).AsBlob().ToArray().Should().Equal(expectedBlob);
            }
        }

        statement.Step().Should().Be(StatementStepResult.Done);
    }

    private static void MutateExposedBackingBuffer(SqlValue value, byte replacement)
    {
        MemoryMarshal.TryGetArray(value.AsBlob(), out var segment).Should().BeTrue();
        segment.Array.Should().NotBeNull();
        segment.Array![segment.Offset] = replacement;
    }
}
