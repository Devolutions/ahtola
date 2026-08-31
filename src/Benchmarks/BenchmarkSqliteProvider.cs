using System.Runtime.CompilerServices;

namespace Benchmarks;

internal static class BenchmarkSqliteProvider
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_e_sqlite3());
        SQLitePCL.raw.FreezeProvider();
    }
}
