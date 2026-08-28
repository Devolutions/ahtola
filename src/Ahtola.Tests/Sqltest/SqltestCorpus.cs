using System.Collections.Concurrent;

namespace Ahtola.Tests.Sqltest;

internal enum SqltestCaseStatus
{
    /// <summary>The case is executable against the managed engine.</summary>
    Runnable,

    /// <summary>The corpus itself asks for the case to be skipped, or targets another backend.</summary>
    SkippedByCorpus,

    /// <summary>The managed harness cannot construct the case's database.</summary>
    UnsupportedHarness,
}

internal sealed record SqltestDiscoveredCase(
    string RelativePath,
    string FullPath,
    string TestName,
    SqltestCaseStatus Status,
    string? Reason)
{
    public string Id => $"{RelativePath}::{TestName}";
}

/// <summary>
/// Discovers every case in the repository's <c>sqlite-sqltests</c> corpus so managed
/// conformance coverage is measured against the whole corpus instead of a hand-maintained
/// list. Cases the managed engine cannot yet satisfy are recorded in
/// <c>Conformance/managed-sqltest-expected-failures.txt</c> rather than being dropped.
/// </summary>
internal static class SqltestCorpus
{
    private const string ExpectedFailuresFileName = "managed-sqltest-expected-failures.txt";
    private const string HarnessExclusionsFileName = "managed-sqltest-harness-exclusions.txt";

    private static readonly HashSet<string> ManagedCapabilities =
        new(StringComparer.Ordinal) { "trigger", "strict" };

    private static readonly Lazy<IReadOnlyList<SqltestDiscoveredCase>> LazyCases = new(Discover);

    private static readonly Lazy<IReadOnlyDictionary<string, string>> LazyExpectedFailures =
        new(LoadExpectedFailures);

    private static readonly Lazy<IReadOnlyDictionary<string, string>> LazyHarnessExclusions =
        new(() => LoadReasonFile(HarnessExclusionsFileName));

    private static readonly ConcurrentDictionary<string, SqltestFile> ParsedFiles = new(StringComparer.Ordinal);

    public static IReadOnlyList<SqltestDiscoveredCase> Cases => LazyCases.Value;

    /// <summary>Case id to the reason the managed engine currently fails it.</summary>
    public static IReadOnlyDictionary<string, string> ExpectedFailures => LazyExpectedFailures.Value;

    /// <summary>Case id to a reviewed reason it cannot be bounded by the in-process harness.</summary>
    public static IReadOnlyDictionary<string, string> HarnessExclusions => LazyHarnessExclusions.Value;

    public static string ExpectedFailuresSourcePath =>
        Path.Combine(ResolveTestProjectDirectory(), "Conformance", ExpectedFailuresFileName);

    public static SqltestFile LoadFile(string relativePath, string fullPath)
        => ParsedFiles.GetOrAdd(relativePath, static (key, path) =>
            SqltestParser.Parse(key, File.ReadAllText(path)), fullPath);

    public static string CorpusRoot { get; } = ResolveCorpusRoot();

    private static IReadOnlyList<SqltestDiscoveredCase> Discover()
    {
        var discovered = new List<SqltestDiscoveredCase>();
        var files = Directory
            .EnumerateFiles(CorpusRoot, "*.sqltest", SearchOption.AllDirectories)
            .OrderBy(static path => path, StringComparer.Ordinal);

        foreach (var fullPath in files)
        {
            var relativePath = Path
                .GetRelativePath(CorpusRoot, fullPath)
                .Replace(Path.DirectorySeparatorChar, '/');

            SqltestFile file;
            try
            {
                file = LoadFile(relativePath, fullPath);
            }
            catch (SqltestParseException error)
            {
                discovered.Add(new SqltestDiscoveredCase(
                    relativePath,
                    fullPath,
                    "<file>",
                    SqltestCaseStatus.UnsupportedHarness,
                    $"managed sqltest parser rejected the file: {error.Message}"));
                continue;
            }

            foreach (var test in file.Tests)
            {
                var (status, reason) = Classify(file, test);
                discovered.Add(new SqltestDiscoveredCase(relativePath, fullPath, test.Name, status, reason));
            }
        }

        return discovered;
    }

    internal static string? DescribeFileLimitation(SqltestFile file)
    {
        if (file.Databases.Count == 0)
            return "the file declares no @database";

        foreach (var database in file.Databases)
        {
            if (database.Kind == SqltestDatabaseKind.Path)
            {
                return
                    $"path fixture '{database.Path}' has no equivalent managed generator; " +
                    "Turso's integrity fixtures are produced by explicit page-corruption routines";
            }
        }

        return null;
    }

    internal static (SqltestCaseStatus Status, string? Reason) Classify(
        SqltestFile file,
        SqltestCase test)
    {
        var id = $"{file.RelativePath}::{test.Name}";
        if (HarnessExclusions.TryGetValue(id, out var exclusionReason))
            return (SqltestCaseStatus.UnsupportedHarness, exclusionReason);

        var fileLimitation = DescribeFileLimitation(file);
        if (fileLimitation is not null)
            return (SqltestCaseStatus.UnsupportedHarness, fileLimitation);

        // The managed engine is neither the sqlite CLI nor an MVCC build, so only
        // unconditional skips apply. Conditional skips are evaluated the same way the
        // Rust `--backend rust` run evaluates them.
        foreach (var skip in file.GlobalSkips.Concat(test.Skips))
        {
            if (skip.Condition is null)
                return (SqltestCaseStatus.SkippedByCorpus, $"@skip: {skip.Reason}");
        }

        foreach (var capability in file.GlobalRequires.Concat(test.Requires))
        {
            if (!ManagedCapabilities.Contains(capability))
                return (SqltestCaseStatus.SkippedByCorpus, $"@requires {capability}");
        }

        if (test.Backend is { } backend)
            return (SqltestCaseStatus.SkippedByCorpus, $"@backend {backend}");

        return (SqltestCaseStatus.Runnable, null);
    }

    private static IReadOnlyDictionary<string, string> LoadExpectedFailures()
        => LoadReasonFile(ExpectedFailuresFileName);

    private static IReadOnlyDictionary<string, string> LoadReasonFile(string fileName)
    {
        var path = ResolveReasonFilePath(fileName);
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(path))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                continue;

            var separator = trimmed.IndexOf('|');
            if (separator < 0)
            {
                entries[trimmed] = "unclassified managed gap";
                continue;
            }

            entries[trimmed[..separator].Trim()] = trimmed[(separator + 1)..].Trim();
        }

        return entries;
    }

    private static string ResolveReasonFilePath(string fileName)
    {
        var copied = Path.Combine(
            TestContext.CurrentContext.TestDirectory,
            "Conformance",
            fileName);
        var source = Path.Combine(ResolveTestProjectDirectory(), "Conformance", fileName);
        return File.Exists(copied) ? copied : source;
    }

    private static string ResolveCorpusRoot()
    {
        var copied = Path.Combine(TestContext.CurrentContext.TestDirectory, "Conformance");
        if (Directory.Exists(copied) &&
            Directory.EnumerateFiles(copied, "*.sqltest", SearchOption.AllDirectories).Any())
        {
            return copied;
        }

        foreach (var root in Ancestors(TestContext.CurrentContext.TestDirectory)
                     .Concat(Ancestors(Directory.GetCurrentDirectory())))
        {
            var candidate = Path.Combine(root.FullName, "sqlite", "conformance", "sqlite-sqltests");
            if (Directory.Exists(candidate))
                return candidate;
        }

        throw new DirectoryNotFoundException(
            "Could not locate the sqlite-sqltests conformance corpus in the test output or repository checkout.");
    }

    private static string ResolveTestProjectDirectory()
    {
        foreach (var root in Ancestors(TestContext.CurrentContext.TestDirectory)
                     .Concat(Ancestors(Directory.GetCurrentDirectory())))
        {
            foreach (var layout in new[]
                     {
                         Path.Combine(root.FullName, "src", "Ahtola.Tests"),
                         Path.Combine(root.FullName, "bindings", "dotnet", "src", "Ahtola.Tests"),
                     })
            {
                if (Directory.Exists(layout))
                    return layout;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the Ahtola.Tests project directory.");
    }

    private static IEnumerable<DirectoryInfo> Ancestors(string path)
    {
        for (DirectoryInfo? directory = new(path); directory is not null; directory = directory.Parent)
            yield return directory;
    }
}
