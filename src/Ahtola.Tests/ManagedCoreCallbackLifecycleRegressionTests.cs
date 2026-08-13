using System.Runtime.CompilerServices;
using AwesomeAssertions;
using Ahtola.Data.Sqlite;

namespace Ahtola.Tests;

public sealed class ManagedCoreCallbackLifecycleRegressionTests
{
    [Test]
    public void ManagedCallbacksRetainDelegatesAndValueConversionsAcrossReopen()
    {
        using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        var callbackState = RegisterStatefulCallbacks(connection);

        connection.Open();
        CollectGarbage();
        callbackState.IsAlive.Should().BeTrue();

        connection.ExecuteScalar<byte[]>("SELECT managed_callback_blob();").Should().Equal(1, 2, 3);
        connection.ExecuteScalar<long>("SELECT managed_callback_blob_length(X'010203');").Should().Be(3);
        connection.ExecuteScalar<string>("SELECT managed_callback_nullable(NULL);").Should().Be("null");
        connection.ExecuteScalar<long>("SELECT managed_callback_total(value) FROM (SELECT 2 AS value UNION ALL SELECT 3);")
            .Should().Be(5);
        connection.ExecuteScalar<string>("SELECT value FROM (SELECT 'a' AS value UNION ALL SELECT 'b') ORDER BY value COLLATE managed_callback_reverse LIMIT 1;")
            .Should().Be("b");

        var callbackFailure = Assert.Throws<SqliteException>(
            () => connection.ExecuteScalar<long>("SELECT managed_callback_failure();"))!;
        callbackFailure.SqliteErrorCode.Should().Be(211);
        callbackFailure.Message.Should().Be(
            Ahtola.Data.Sqlite.Properties.Resources.SqliteNativeError(211, "managed callback failure"));

        connection.Close();
        CollectGarbage();
        callbackState.IsAlive.Should().BeTrue();

        connection.Open();
        connection.ExecuteScalar<byte[]>("SELECT managed_callback_blob();").Should().Equal(1, 2, 3);
        connection.ExecuteScalar<long>("SELECT managed_callback_offset(7);").Should().Be(12);
        connection.ExecuteScalar<long>("SELECT managed_callback_total(value) FROM (SELECT 2 AS value UNION ALL SELECT 3);")
            .Should().Be(5);
        connection.ExecuteScalar<string>("SELECT value FROM (SELECT 'a' AS value UNION ALL SELECT 'b') ORDER BY value COLLATE managed_callback_reverse LIMIT 1;")
            .Should().Be("b");
    }

    [Test]
    public void ManagedCallbackReplacementAndRemovalPreserveOverloads()
    {
        using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        connection.CreateFunction<long, long>("managed_callback_arity", static value => value + 1);
        connection.CreateFunction<long, long, long>("managed_callback_arity", static (left, right) => left + right);
        connection.CreateFunction("managed_callback_arity", static values => (long)values.Length);
        connection.CreateAggregate<long, long>("managed_callback_total", 0L, static (total, value) => total + value);
        connection.CreateAggregate<long>("managed_callback_total", 0L, static (total, values) => total + values.Length);
        connection.CreateCollation("managed_callback_order", static (left, right) => -string.CompareOrdinal(left, right));
        connection.Open();

        connection.CreateFunction<long, long>("managed_callback_arity", static value => value + 10);
        connection.CreateAggregate<long, long>("managed_callback_total", 0L, static (total, value) => total + (value * 10));
        connection.CreateCollation("managed_callback_order", string.CompareOrdinal);

        connection.ExecuteScalar<long>("SELECT managed_callback_arity(2);").Should().Be(12);
        connection.ExecuteScalar<long>("SELECT managed_callback_arity(2, 3);").Should().Be(5);
        connection.ExecuteScalar<long>("SELECT managed_callback_arity(2, 3, 4);").Should().Be(3);
        connection.ExecuteScalar<long>("SELECT managed_callback_total(value) FROM (SELECT 2 AS value UNION ALL SELECT 3);")
            .Should().Be(50);
        connection.ExecuteScalar<long>("SELECT managed_callback_total(value, value) FROM (SELECT 2 AS value UNION ALL SELECT 3);")
            .Should().Be(4);
        connection.ExecuteScalar<string>("SELECT value FROM (SELECT 'a' AS value UNION ALL SELECT 'b') ORDER BY value COLLATE managed_callback_order LIMIT 1;")
            .Should().Be("a");

        connection.CreateFunction<long, long>("managed_callback_arity", default);
        connection.CreateAggregate<long, long>("managed_callback_total", 0L, default);
        connection.CreateCollation("managed_callback_order", null);

        Assert.Throws<SqliteException>(() => connection.ExecuteScalar<long>("SELECT managed_callback_arity(2);"))!
            .SqliteErrorCode.Should().Be(1);
        Assert.Throws<SqliteException>(
            () => connection.ExecuteScalar<long>(
                "SELECT managed_callback_total(value) FROM (SELECT 2 AS value);"))!
            .SqliteErrorCode.Should().Be(1);
        Assert.Throws<SqliteException>(
            () => connection.ExecuteScalar<string>(
                "SELECT value FROM (SELECT 'a' AS value) ORDER BY value COLLATE managed_callback_order;"))!
            .SqliteErrorCode.Should().Be(1);
    }

    [Test]
    public void ManagedFacadeDefersLaterProjectionCallbackFailuresUntilTheirRead()
    {
        using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        connection.CreateFunction<long, long>(
            "managed_fail_on_two",
            value => value == 2
               ? throw new InvalidOperationException("later row")
               : value);
        connection.Open();
        connection.ExecuteNonQuery("CREATE TABLE values_table(value INTEGER);");
        connection.ExecuteNonQuery("INSERT INTO values_table VALUES (1), (2);");
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT managed_fail_on_two(value) FROM values_table;";
        using var reader = command.ExecuteReader();

        reader.Read().Should().BeTrue();
        reader.GetInt64(0).Should().Be(1);
        Assert.Throws<SqliteException>(() => reader.Read())!
            .SqliteErrorCode.Should().Be(1);
    }

    [Test]
    public void ManagedFacadeDefersLaterWhereCallbackFailuresUntilTheirRead()
    {
        using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        connection.CreateFunction<long, long>(
            "managed_fail_where_on_two",
            value => value == 2
                ? throw new InvalidOperationException("later row")
                : 1);
        connection.Open();
        connection.ExecuteNonQuery("CREATE TABLE values_table(value INTEGER);");
        connection.ExecuteNonQuery("INSERT INTO values_table VALUES (1), (2);");
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM values_table WHERE managed_fail_where_on_two(value);";
        using var reader = command.ExecuteReader();

        reader.Read().Should().BeTrue();
        reader.GetInt64(0).Should().Be(1);
        Assert.Throws<SqliteException>(() => reader.Read())!
            .SqliteErrorCode.Should().Be(1);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference RegisterStatefulCallbacks(SqliteConnection connection)
    {
        var state = new CallbackState([1, 2, 3], 5);
        connection.CreateFunction<byte[]>("managed_callback_blob", () => state.Blob);
        connection.CreateFunction<byte[], long>("managed_callback_blob_length", static value => value.Length);
        connection.CreateFunction<string?, string>("managed_callback_nullable", static value => value ?? "null");
        connection.CreateFunction<long, long>("managed_callback_offset", value => value + state.Offset);
        connection.CreateFunction<long>("managed_callback_failure", () => throw new SqliteException("managed callback failure", 211));
        connection.CreateAggregate<long, long>("managed_callback_total", 0L, static (total, value) => total + value);
        connection.CreateCollation(
            "managed_callback_reverse",
            (left, right) => state.Offset > 0 ? -string.CompareOrdinal(left, right) : string.CompareOrdinal(left, right));
        return new WeakReference(state);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CollectGarbage()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private sealed class CallbackState(byte[] blob, long offset)
    {
        public byte[] Blob { get; } = blob;

        public long Offset { get; } = offset;
    }
}
