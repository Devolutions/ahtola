using System.Diagnostics.CodeAnalysis;

namespace Ahtola.Data.Sqlite;

/// <summary>
/// System.Data.SQLite's declared-type to CLR-type table (<c>SQLiteConvert</c> type names),
/// used by <see cref="SqliteTypeMapping.SystemDataSQLite"/>. The declared type is matched
/// whole, case-insensitively, after removing a length or precision suffix such as
/// <c>(50)</c>; unknown names map to nothing and keep storage-class typing.
/// </summary>
internal static class SystemDataSqliteTypeMap
{
    [return: DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.PublicProperties)]
    internal static Type? Get(string? declaredType)
    {
        if (string.IsNullOrWhiteSpace(declaredType))
            return null;

        var sizeStart = declaredType.IndexOf('(');
        var name = (sizeStart >= 0 ? declaredType[..sizeStart] : declaredType).Trim().ToUpperInvariant();
        return name switch
        {
            "BIGINT" or "COUNTER" or "IDENTITY" or "INT64" or "INTEGER" or "LONG"
                => typeof(long),
            "INT" or "INT32" or "MEDIUMINT"
                => typeof(int),
            "INT16" or "SMALLINT"
                => typeof(short),
            "TINYINT"
                => typeof(byte),
            "BIT" or "BOOL" or "BOOLEAN" or "LOGICAL" or "YESNO"
                => typeof(bool),
            "DATE" or "DATETIME" or "DATETIME2" or "SMALLDATE" or "TIME" or "TIMESTAMP"
                => typeof(DateTime),
            "GUID" or "UNIQUEIDENTIFIER"
                => typeof(Guid),
            "DOUBLE" or "FLOAT" or "REAL"
                => typeof(double),
            "SINGLE"
                => typeof(float),
            "CURRENCY" or "DECIMAL" or "MONEY" or "NUMBER" or "NUMERIC"
                => typeof(decimal),
            "CHAR" or "CLOB" or "LONGCHAR" or "LONGTEXT" or "LONGVARCHAR" or "MEMO" or "NCHAR" or "NOTE" or "NTEXT" or "NVARCHAR" or "NVARCHAR2" or "STRING" or "TEXT" or "VARCHAR" or "VARCHAR2"
                => typeof(string),
            "BINARY" or "BLOB" or "GENERAL" or "IMAGE" or "OLEOBJECT" or "RAW" or "VARBINARY"
                => typeof(byte[]),
            _ => null,
        };
    }
}
