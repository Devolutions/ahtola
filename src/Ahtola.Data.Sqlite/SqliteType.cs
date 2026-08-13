namespace Ahtola.Data.Sqlite;

// The integer values intentionally match the standard SQLite type codes
// (SQLITE_INTEGER=1, SQLITE_FLOAT=2, SQLITE_TEXT=3, SQLITE_BLOB=4), which is the
// same numbering Microsoft.Data.Sqlite uses (its members are assigned the
// SQLitePCL.raw SQLITE_* constants). Matching those values keeps this provider
// drop-in compatible: schema-table ProviderType values and any consumer code
// that casts SqliteType to int (e.g. PowerShell enum coercion) observe the same
// numbers it would under Microsoft.Data.Sqlite. Notably Integer=1 (not 0), so
// truthiness checks like `if (!type)` no longer misfire on INTEGER columns.
public enum SqliteType
{
    Integer = 1,
    Real = 2,
    Text = 3,
    Blob = 4,
}
