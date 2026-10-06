namespace Ahtola.Data.Sqlite;

/// <summary>How a data reader maps declared column types to CLR types.</summary>
public enum SqliteTypeMapping
{
    /// <summary>
    /// Microsoft.Data.Sqlite behaviour: values come back by storage class
    /// (<see cref="long"/>, <see cref="double"/>, <see cref="string"/>, <see cref="byte"/>[]),
    /// plus <see cref="System.Guid"/> for columns declared <c>GUID</c>/<c>UNIQUEIDENTIFIER</c>.
    /// </summary>
    Default = 0,

    /// <summary>
    /// System.Data.SQLite behaviour: the declared type picks the CLR type, so <c>INT</c> reads as
    /// <see cref="int"/>, <c>SMALLINT</c> as <see cref="short"/>, <c>TINYINT</c> as
    /// <see cref="byte"/>, <c>BIT</c>/<c>BOOL</c>/<c>BOOLEAN</c> as <see cref="bool"/>,
    /// <c>DATETIME</c>/<c>DATE</c>/<c>TIME</c>/<c>TIMESTAMP</c> as <see cref="System.DateTime"/>,
    /// <c>MONEY</c>/<c>DECIMAL</c>/<c>NUMERIC</c> as <see cref="decimal"/>, and
    /// <c>IMAGE</c>/<c>BINARY</c>/<c>VARBINARY</c>/<c>BLOB</c> as <see cref="byte"/>[] — in
    /// <c>GetValue</c>, <c>GetFieldType</c>, <c>GetSchemaTable</c> and <c>ExecuteScalar</c>.
    /// </summary>
    SystemDataSQLite = 1,
}
