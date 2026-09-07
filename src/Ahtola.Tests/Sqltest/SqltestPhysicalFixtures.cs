using System.Collections.Concurrent;
using System.Globalization;
using Ahtola.Core.Storage;

namespace Ahtola.Tests.Sqltest;

/// <summary>
/// Registry of <c>@database &lt;path&gt; readonly</c> fixtures the managed sqltest harness can
/// materialize in-process, so a <see cref="SqltestDatabaseKind.Path"/> database is a reviewed,
/// bounded capability rather than a blanket harness limitation. Two kinds of entries:
/// <list type="bullet">
/// <item>Byte-identical static fixtures vendored unmodified from Turso's own
/// <c>sqlite/conformance/database/</c> (see <c>conformance/sqlite-sqltests/database/</c>),
/// opened directly from the corpus copy.</item>
/// <item>The <c>integrity_check/parity_*</c> corruption fixtures built on demand by
/// <see cref="SqltestIntegrityFixtureGenerator"/>, cached once per test process the same way
/// <see cref="SqltestDefaultDatabaseGenerator"/> caches the default fixture.</item>
/// </list>
/// A path that is not registered here remains a genuine, reviewable harness limitation (see
/// <c>SqltestCorpus.DescribeFileLimitation</c>) rather than being silently accepted or hidden.
/// </summary>
internal static class SqltestPhysicalFixtures
{
    private static readonly string CacheDirectory = Path.Combine(
        TestContext.CurrentContext.WorkDirectory,
        ".sqltest-path-fixtures",
        Environment.ProcessId.ToString(CultureInfo.InvariantCulture));

    private static readonly Lazy<IReadOnlyDictionary<string, Action<string>>> LazyGenerators =
        new(BuildGenerators, LazyThreadSafetyMode.ExecutionAndPublication);

    private static IReadOnlyDictionary<string, Action<string>> Generators => LazyGenerators.Value;

    private static readonly ConcurrentDictionary<string, Lazy<string>> Cache = new(StringComparer.Ordinal);

    static SqltestPhysicalFixtures()
        => AppDomain.CurrentDomain.ProcessExit += static (_, _) => TryDeleteCacheDirectory();

    /// <summary>Whether <paramref name="relativePath"/> (as written in <c>@database</c>) is registered.</summary>
    public static bool IsKnown(string relativePath) => Generators.ContainsKey(relativePath);

    /// <summary>
    /// Returns an absolute, ready-to-open path for <paramref name="relativePath"/>, generating
    /// or copying it into a private per-process cache directory on first use.
    /// </summary>
    public static string Materialize(string relativePath)
    {
        if (!Generators.TryGetValue(relativePath, out var generate))
        {
            throw new NotSupportedException(
                $"The managed sqltest harness does not have a registered fixture for '{relativePath}'.");
        }

        return Cache.GetOrAdd(relativePath, path => new Lazy<string>(() =>
        {
            var destination = Path.Combine(CacheDirectory, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
                File.Delete(destination + suffix);
            generate(destination);
            return destination;
        }, LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    private static IReadOnlyDictionary<string, Action<string>> BuildGenerators()
    {
        var map = new Dictionary<string, Action<string>>(StringComparer.Ordinal);
        foreach (var (relativePath, sourceFileName) in StaticFixtures)
        {
            map[relativePath] = destination =>
            {
                File.Copy(
                    Path.Combine(SqltestCorpus.CorpusRoot, "database", sourceFileName),
                    destination,
                    overwrite: true);

                // Turso's own static fixtures ship as a bare .db file with no WAL/SHM
                // sidecars. Ahtola's read-only open refuses to synthesize a missing WAL
                // shared-memory carrier (that would mutate storage), so prime it with one
                // writable pager open/close on the private copy. Do not parse the SQL
                // schema here: the legacy-view fixture intentionally contains invalid SQL.
                using var priming = SqlitePager.Open(
                    PhysicalFileSystem.Instance, destination, destination + "-wal");
            };
        }

        SqltestIntegrityFixtureGenerator.RegisterInto(map);
        return map;
    }

    /// <summary>
    /// <c>@database</c> relative path to the byte-identical file vendored under
    /// <c>conformance/sqlite-sqltests/database/</c> (verified via SHA-256 against
    /// <c>turso-src/sqlite/conformance/database/</c> at vendoring time).
    /// </summary>
    private static readonly (string RelativePath, string SourceFileName)[] StaticFixtures =
    [
        ("database/testing_small.db", "testing_small.db"),
        ("database/testing_user_version_10.db", "testing_user_version_10.db"),
        ("database/testing_legacy_unquoted_view_columns.db", "testing_legacy_unquoted_view_columns.db"),
    ];

    private static void TryDeleteCacheDirectory()
    {
        try
        {
            if (Directory.Exists(CacheDirectory))
                Directory.Delete(CacheDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
