using System.Data.Common;
using AwesomeAssertions;
using ManagedSqliteConnection = Ahtola.Data.Sqlite.SqliteConnection;
using MsData = Microsoft.Data.Sqlite;

namespace Ahtola.Tests;

/// <summary>
/// SQLite blames an ALTER for a broken view or trigger only when the ALTER is actually to blame.
/// An object that was valid beforehand reports "error in view v after drop column: ...", while one
/// that was already broken reports "error in view v: ..." with no operation. Getting this wrong in
/// either direction misattributes the failure and sends the reader after the wrong statement.
/// </summary>
[NonParallelizable]
public sealed class ManagedAlterTableDependentSchemaBlameTests
{
    private const string BlamedSuffix = " after ";

    [TestCase(
        "CREATE TABLE s(a, b); CREATE VIEW v AS SELECT b FROM s;",
        "ALTER TABLE s DROP COLUMN b;",
        "error in view v after drop column: no such column: b",
        TestName = "DropColumn blames itself for a view it just broke")]
    [TestCase(
        "CREATE TABLE s(a, b); CREATE TABLE d(x); "
            + "CREATE TRIGGER tg AFTER INSERT ON d BEGIN SELECT b FROM s; END;",
        "ALTER TABLE s DROP COLUMN b;",
        "error in trigger tg after drop column: no such column: b",
        TestName = "DropColumn blames itself for a trigger body it just broke")]
    public void AlterBlamesItselfForAnObjectItBreaks(string setup, string alter, string expected)
    {
        using var managed = OpenManagedMemory();
        using var sqlite = OpenMicrosoftMemory();
        Execute(managed, setup);
        Execute(sqlite, setup);

        // Real SQLite is the specification, so assert against it as well as the literal text.
        FailureMessage(sqlite, alter).Should().Be(expected);
        FailureMessage(managed, alter).Should().Be(expected);
    }

    [TestCase(
        "CREATE TABLE s(a, b); CREATE TABLE g(z); CREATE VIEW v AS SELECT * FROM g; DROP TABLE g;",
        "ALTER TABLE s DROP COLUMN b;",
        "error in view v",
        TestName = "DropColumn does not blame itself for a view broken by an earlier DROP TABLE")]
    [TestCase(
        "CREATE TABLE s(a, b); CREATE TABLE g(z); CREATE VIEW v AS SELECT * FROM g; DROP TABLE g;",
        "ALTER TABLE s RENAME COLUMN b TO bb;",
        "error in view v",
        TestName = "RenameColumn does not blame itself for a view broken by an earlier DROP TABLE")]
    [TestCase(
        "CREATE TABLE s(a, b); CREATE TABLE g(z); "
            + "CREATE TRIGGER tg AFTER INSERT ON s BEGIN SELECT * FROM g; END; DROP TABLE g;",
        "ALTER TABLE s DROP COLUMN b;",
        "error in trigger tg",
        TestName = "DropColumn does not blame itself for a trigger broken by an earlier DROP TABLE")]
    public void AlterDoesNotBlameItselfForAnObjectThatWasAlreadyBroken(
        string setup,
        string alter,
        string expectedPrefix)
    {
        using var managed = OpenManagedMemory();
        using var sqlite = OpenMicrosoftMemory();
        Execute(managed, setup);
        Execute(sqlite, setup);

        var sqliteMessage = FailureMessage(sqlite, alter);
        var managedMessage = FailureMessage(managed, alter);

        // SQLite qualifies the missing table as "main.g" where the managed engine says "g", so the
        // assertion is on who gets blamed rather than on the inner resolver text.
        sqliteMessage.Should().StartWith(expectedPrefix + ":").And.NotContain(BlamedSuffix);
        managedMessage.Should().StartWith(expectedPrefix + ":").And.NotContain(BlamedSuffix);
    }

    private static string FailureMessage(DbConnection connection, string sql)
    {
        var act = () => Execute(connection, sql);
        return Unwrap(act.Should().Throw<Exception>().Which.Message);
    }

    /// <summary>
    /// Microsoft.Data.Sqlite reports the engine text as "SQLite Error 1: '&lt;message&gt;'.", so it
    /// has to be unwrapped before the two engines can be compared.
    /// </summary>
    private static string Unwrap(string message)
    {
        var start = message.IndexOf('\'');
        var end = message.LastIndexOf('\'');
        return start >= 0 && end > start
            ? message[(start + 1)..end]
            : message;
    }

    private static ManagedSqliteConnection OpenManagedMemory()
    {
        var connection = new ManagedSqliteConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();
        return connection;
    }

    private static MsData.SqliteConnection OpenMicrosoftMemory()
    {
        var connection = new MsData.SqliteConnection("Data Source=:memory:");
        connection.Open();
        return connection;
    }

    private static void Execute(DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
