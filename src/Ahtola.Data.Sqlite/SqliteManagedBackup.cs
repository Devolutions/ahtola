using Ahtola.Core;

namespace Ahtola.Data.Sqlite;

internal static class SqliteManagedBackup
{
    internal static void Copy(SqliteConnection source, SqliteConnection destination, string destinationName, string sourceName)
    {
        if (!source.IsManagedConnection || !destination.IsManagedConnection)
            throw new InvalidOperationException("Managed backup requires managed source and destination connections.");
        ArgumentNullException.ThrowIfNull(destinationName);
        ArgumentNullException.ThrowIfNull(sourceName);
        if (ReferenceEquals(source, destination)
            && string.Equals(sourceName, destinationName, StringComparison.OrdinalIgnoreCase))
        {
            throw new SqliteException(Properties.Resources.SqliteNativeError(1, "source and destination must be distinct"), 1);
        }
        if (destination.Transaction is not null || destination.HasOpenReader)
        {
            throw new SqliteException(Properties.Resources.SqliteNativeError(5, "database is locked"), 5);
        }
        try
        {
            source.ManagedConnection.CopySnapshotTo(destination.ManagedConnection, destinationName, sourceName);
        }
        catch (ManagedSnapshotException exception)
        {
            throw ToSqliteException(exception);
        }
        catch (EmbeddedSqlException exception)
        {
            throw SqliteCommand.ToSqliteException(exception);
        }
    }

    private static Exception ToSqliteException(ManagedSnapshotException exception)
    {
        return exception.Failure switch
        {
            ManagedSnapshotFailure.DestinationBusy
                => new SqliteException(Properties.Resources.SqliteNativeError(5, "database is locked"), 5),
            ManagedSnapshotFailure.SourceBusy
                => new SqliteException(Properties.Resources.SqliteNativeError(5, "source database is locked"), 5),
            ManagedSnapshotFailure.UnsupportedSchemaObject
                => new NotSupportedException(Properties.Resources.ManagedBackupSchemaObjectNotSupported(exception.ObjectName)),
            ManagedSnapshotFailure.RowidNotAccessible
                => new NotSupportedException(Properties.Resources.ManagedBackupRowidNotAccessible(exception.ObjectName)),
            ManagedSnapshotFailure.ColumnCountMismatch
                => new InvalidOperationException(Properties.Resources.ManagedBackupColumnCountMismatch(exception.ObjectName)),
            ManagedSnapshotFailure.PhysicalFileIdentityUnavailable
                => new NotSupportedException(Properties.Resources.ManagedBackupPhysicalFileIdentityNotSupported),
            _ => throw new InvalidOperationException($"Unknown managed snapshot failure {exception.Failure}."),
        };
    }
}
