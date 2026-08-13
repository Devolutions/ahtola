namespace Ahtola.Data.Sqlite.Properties;

public static class Resources
{
    public static string KeywordNotSupported(object? keyword) => $"Keyword not supported: {keyword}";

    public static string ConnectionStringRequiresClosedConnection => "The connection string can only be set when the connection is closed.";

    public static string SetRequiresNoOpenReader(object? property) => $"{property} cannot be set while a data reader is open.";

    public static string InvalidCommandType(object? commandType) => $"Command type {commandType} is not supported.";

    public static string CallRequiresOpenConnection(object? method) => $"{method} requires an open connection.";

    public static string DataReaderClosed(object? method) => $"{method} requires an open data reader.";

    public static string InvalidIsolationLevel(object? isolationLevel) => $"Isolation level {isolationLevel} is not supported.";

    public static string InvalidParameterDirection(object? direction) => $"Parameter direction {direction} is not supported.";

    public static string RequiresSet(object? propertyName) => $"{propertyName} must be set.";

    public static string SqlBlobRequiresOpenConnection => "SqliteBlob requires an open connection.";

    public static string ManagedExtensionsNotSupported => "SQLite extension loading is not supported when Local Provider=Managed because extensions require the native SQLite loader.";


    public static string ManagedSharedCacheReadUncommittedNotSupported => ManagedSharedCacheContract.ReadUncommittedNotSupportedMessage;

    // Retained for callers/tests that still reference the historical message surface.
    // Functions/aggregates/collations are now catalog-scoped on shared memory; hooks remain blocked.
    public static string ManagedSharedCacheCallbacksNotSupported => ManagedSharedCacheHooksNotSupported;

    public static string ManagedMemoryEncryptionNotSupported => "Encryption is supported only for file-backed databases when Local Provider=Managed.";

    public static string ManagedForeignReadOnlyNotSupported => "Foreign Read Only requires Local Provider=Managed, Mode=ReadOnly, Pooling=False, a file-backed Data Source, and no shared cache or encryption options.";

    public static string InvalidOffsetAndCount => "Offset and count were out of bounds for the array.";

    public static string SqliteNativeError(object? errorCode, object? message) => $"SQLite Error {errorCode}: '{message}'.";

    public static string DefaultNativeError => "unknown error";

    public static string DataReaderOpen => "There is already an open DataReader associated with this Command.";

    public static string NoData => "No data exists for the row/column.";

    public static string TransactionCompleted => "The transaction has completed.";

    public static string TransactionConnectionMismatch => "The transaction connection does not match the command connection.";

    public static string TransactionRequired => "Execute requires the command to have a transaction when the connection has a pending local transaction.";

    public static string ParameterNotFound(object? parameterName) => $"Parameter {parameterName} was not found.";

    public static string MissingParameters(object? parameters) => $"Missing parameter values for {parameters}.";

    public static string AmbiguousParameterName(object? parameterName) => $"Parameter name {parameterName} is ambiguous.";

    public static string CalledOnNullValue(object? ordinal) => $"The data is NULL at ordinal {ordinal}.";

    public static string UnknownCollection(object? collectionName) => $"Unknown collection: {collectionName}.";

    public static string TooManyRestrictions(object? collectionName) => $"Too many restrictions specified for collection {collectionName}.";

    public static string UnknownDataType(object? typeName) => $"Unknown data type: {typeName}.";

    public static string SeekBeforeBegin => "An attempt was made to seek before the beginning of the stream.";

    public static string InvalidEnumValue(object? enumType, object? value) => $"Invalid value {value} for enum type {enumType}.";

    public static string ResizeNotSupported => "Resizing is not supported.";

    public static string WriteNotSupported => "Writing is not supported.";

    public static string CannotStoreNaN => "NaN values cannot be stored.";

    public static string EncryptionNotSupported(object? libraryName) => $"Encryption is not supported by {libraryName}.";

    public static string UDFCalledWithNull(object? function, object? ordinal) => $"Function {function} was called with a NULL value at ordinal {ordinal}.";

    public static string ParallelTransactionsNotSupported => "Parallel transactions are not supported.";

    public static string ManagedBackupMixedProvidersNotSupported => "BackupDatabase does not support copying between managed and native providers.";

    public static string ManagedBackupAttachedDatabasesNotSupported => "BackupDatabase supports only the main database when Local Provider=Managed.";

    public static string ManagedBackupSameConnectionNotSupported => "BackupDatabase requires distinct source and destination connections when Local Provider=Managed.";

    public static string ManagedBackupDestinationMustBeEmpty => "BackupDatabase requires an empty destination when Local Provider=Managed.";

    public static string ManagedBackupPhysicalFileIdentityNotSupported => "BackupDatabase cannot safely copy between file systems with unknown identity semantics when Local Provider=Managed.";

    public static string ManagedBackupSchemaObjectNotSupported(object? type) => $"BackupDatabase does not support schema object type '{type}' when Local Provider=Managed.";

    public static string ManagedBackupRowidNotAccessible(object? table) => $"BackupDatabase cannot preserve rowids for table '{table}' when Local Provider=Managed because all rowid aliases are declared columns.";

    public static string ManagedBackupColumnCountMismatch(object? table) => $"BackupDatabase could not read all columns for table '{table}' when Local Provider=Managed.";

    public static string ManagedVfsNotSupported => "Vfs is not supported when Local Provider=Managed because the managed engine does not use native SQLite VFS implementations.";

    public static string HooksRequireManagedProvider => "Update, commit, rollback, authorizer, trace, and progress callbacks are implemented only by the managed engine; open the connection with Local Provider=Managed.";

    public static string ManagedSharedCacheHooksNotSupported => "Managed shared-memory databases do not support update, commit, rollback, authorizer, trace, or progress callbacks because the managed catalog is shared across connections.";

    public static string ProgressIntervalOutOfRange => "The progress handler step interval must be greater than zero.";

    public static string AmbiguousColumnName(object? name, object? column1, object? column2) => $"Column name {name} is ambiguous between {column1} and {column2}.";

    public static string ConvertFailed(object? sourceType, object? targetType) => $"Cannot convert {sourceType} to {targetType}.";
}
