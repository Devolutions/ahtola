using AwesomeAssertions;
using Ahtola.Data.Sqlite;

namespace Ahtola.Tests;

public sealed class ManagedBackupTriggerSnapshotCoverageTests
{
    [Test]
    public void ManagedBackupCopiesRowTriggersInDeclarationOrderAfterRestoringRows()
    {
        using var source = OpenManagedConnection();
        using var destination = OpenManagedConnection();
        source.ExecuteNonQuery("""
            CREATE TABLE event_data(value TEXT);
            CREATE TABLE audit(value TEXT);
            INSERT INTO event_data VALUES ('before backup');
            CREATE TRIGGER event_data_first AFTER INSERT ON event_data
            BEGIN
                INSERT INTO audit VALUES ('first:' || NEW.value);
            END;
            CREATE TRIGGER event_data_second AFTER INSERT ON event_data WHEN NEW.value IS NOT NULL
            BEGIN
                INSERT INTO audit VALUES ('second:' || NEW.value);
            END;
            """);

        source.BackupDatabase(destination);

        destination.ExecuteScalar<long>("SELECT COUNT(*) FROM event_data;").Should().Be(1);
        destination.ExecuteScalar<long>("SELECT COUNT(*) FROM audit;").Should().Be(0);
        destination.ExecuteNonQuery("INSERT INTO event_data VALUES ('after backup');");
        destination.ExecuteScalar<long>("SELECT COUNT(*) FROM audit;").Should().Be(2);
        using (var command = destination.CreateCommand())
        {
            command.CommandText = "SELECT value FROM audit ORDER BY rowid";
            using var reader = command.ExecuteReader();
            reader.Read().Should().BeTrue();
            reader.GetString(0).Should().Be("second:after backup");
            reader.Read().Should().BeTrue();
            reader.GetString(0).Should().Be("first:after backup");
            reader.Read().Should().BeFalse();
        }
        destination.ExecuteScalar<string>(
                "SELECT sql FROM sqlite_master WHERE type = 'trigger' AND name = 'event_data_second';")
            .Should()
            .Contain("WHEN NEW.value IS NOT NULL");
    }

    [Test]
    public void ManagedBackupCopiesRecursiveTriggerProgramsWithoutCopyingConnectionPragmaState()
    {
        using var source = OpenManagedConnection();
        using var destination = OpenManagedConnection();
        source.ExecuteNonQuery("""
            PRAGMA recursive_triggers = ON;
            CREATE TABLE event_data(id INTEGER PRIMARY KEY);
            CREATE TABLE audit(id INTEGER);
            CREATE TRIGGER event_data_recursive AFTER INSERT ON event_data WHEN NEW.id < 3
            BEGIN
                INSERT INTO audit VALUES (NEW.id);
                INSERT INTO event_data VALUES (NEW.id + 1);
            END;
            INSERT INTO event_data VALUES (1);
            """);

        source.BackupDatabase(destination);

        destination.ExecuteScalar<long>("PRAGMA recursive_triggers;").Should().Be(0);
        destination.ExecuteScalar<long>("SELECT COUNT(*) FROM event_data;").Should().Be(3);
        destination.ExecuteScalar<long>("SELECT COUNT(*) FROM audit;").Should().Be(2);
        destination.ExecuteNonQuery("""
            DELETE FROM event_data;
            DELETE FROM audit;
            PRAGMA recursive_triggers = ON;
            INSERT INTO event_data VALUES (1);
            """);
        destination.ExecuteScalar<long>("SELECT COUNT(*) FROM event_data;").Should().Be(3);
        destination.ExecuteScalar<long>("SELECT COUNT(*) FROM audit;").Should().Be(2);
    }

    [Test]
    public void ManagedBackupRollsBackCopiedTriggerSchemaWhenALaterTableCannotPreserveRowids()
    {
        using var source = OpenManagedConnection();
        using var destination = OpenManagedConnection();
        source.ExecuteNonQuery("""
            CREATE TABLE event_data(value TEXT);
            CREATE TABLE audit(value TEXT);
            INSERT INTO event_data VALUES ('source row');
            CREATE TRIGGER event_data_audit AFTER INSERT ON event_data
            BEGIN
                INSERT INTO audit VALUES ('inserted');
            END;
            CREATE TABLE inaccessible_rowid(rowid TEXT, _rowid_ TEXT, oid TEXT);
            INSERT INTO inaccessible_rowid VALUES ('a', 'b', 'c');
            """);

        source.Invoking(connection => connection.BackupDatabase(destination))
            .Should().Throw<NotSupportedException>()
            .WithMessage(Data.Sqlite.Properties.Resources.ManagedBackupRowidNotAccessible("inaccessible_rowid"));

        destination.ExecuteScalar<long>("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table';").Should().Be(0);
        destination.ExecuteScalar<long>("SELECT COUNT(*) FROM sqlite_master WHERE type = 'trigger';").Should().Be(0);
        source.ExecuteScalar<string>("SELECT value FROM event_data;").Should().Be("source row");
    }

    private static SqliteConnection OpenManagedConnection()
    {
        var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();
        return connection;
    }
}
