using System.Reflection;
using Ahtola.Core;
using Ahtola.Core.Indexing;
using Ahtola.Core.Search;
using AwesomeAssertions;

namespace Ahtola.Tests;

/// <summary>
/// NativeAOT and trimming guards for the index-method foundation. The whole mechanism must be
/// statically reachable: registration is a direct call from a static constructor, and nothing in
/// the shipped path may use reflection, runtime generic construction or dynamic type lookup.
/// </summary>
public sealed class ManagedIndexMethodAotSafetyTests
{
    private static readonly string[] IndexMethodSourceNamespaces =
    [
        "Ahtola.Core.Indexing",
        "Ahtola.Core.Search",
        "Ahtola.Core.Vectors",
    ];

    [Test]
    public void RegistrationIsStaticAndRequiresNoReflection()
    {
        // Touching the registry runs its static constructor; the methods must already be there.
        ManagedIndexMethodRegistry.Names.Should().Contain("fts").And.Contain("vector");
        ManagedIndexMethodRegistry.Resolve("fts").Should().BeSameAs(ManagedFtsIndexMethod.Instance);
        ManagedIndexMethodRegistry.Resolve("vector").Should().BeSameAs(Core.Vectors.ManagedVectorIndexMethod.Instance);
    }

    [Test]
    public void IndexMethodTypesDoNotDeclareReflectionOrInteropMembers()
    {
        var assembly = typeof(ManagedIndexMethod).Assembly;
        var offenders = new List<string>();
        foreach (var type in assembly.GetTypes())
        {
            if (type.Namespace is null || !IndexMethodSourceNamespaces.Contains(type.Namespace, StringComparer.Ordinal))
                continue;

            foreach (var method in type.GetMethods(
                         BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (method.GetCustomAttributes().Any(static attribute =>
                        attribute.GetType().Name is "DllImportAttribute" or "LibraryImportAttribute"))
                {
                    offenders.Add($"{type.FullName}.{method.Name} declares a P/Invoke");
                }
            }
        }

        offenders.Should().BeEmpty();
    }

    [Test]
    public void IndexMethodSourcesContainNoDynamicTypeLookupOrRuntimeGenerics()
    {
        var repositoryRoot = FindRepositoryRoot();
        var searchRoots = new[]
        {
            Path.Combine(repositoryRoot, "src", "Ahtola.Core", "Indexing"),
            Path.Combine(repositoryRoot, "src", "Ahtola.Core", "Search"),
            Path.Combine(repositoryRoot, "src", "Ahtola.Core", "Vectors"),
        };
        string[] forbidden =
        [
            "Type.GetType(",
            "Assembly.Load",
            "Activator.CreateInstance",
            "MakeGenericMethod",
            "MakeGenericType",
            "DllImport",
            "LibraryImport",
            "CompileToDynamicMethod",
            "UnconditionalSuppressMessage",

            // Vector index centroids are persisted, so a generator whose algorithm or seeding is an
            // ambient implementation detail would make the stored bytes differ between runs,
            // platforms and framework versions.
            "System.Random",
            "new Random(",
            "Random.Shared",
            "DateTime.Now",
            "DateTime.UtcNow",
            "Environment.TickCount",
            "GetHashCode()",
        ];

        var offenders = new List<string>();
        foreach (var root in searchRoots)
        {
            Directory.Exists(root).Should().BeTrue($"'{root}' must exist");
            foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                var text = File.ReadAllText(file);
                foreach (var pattern in forbidden)
                {
                    if (text.Contains(pattern, StringComparison.Ordinal))
                        offenders.Add($"{Path.GetFileName(file)} contains '{pattern}'");
                }
            }
        }

        offenders.Should().BeEmpty();
    }

    [Test]
    public void BuiltinFunctionRegistrationMatchesTheEvaluatorSurface()
    {
        foreach (var name in new[] { "FTS_MATCH", "FTS_SCORE", "FTS_HIGHLIGHT", "FTS_SNIPPET" })
        {
            SqliteBuiltinFunctions.Contains(name).Should().BeTrue(name);
            SqliteBuiltinFunctions.IsAggregate(name).Should().BeFalse(name);
            SqliteBuiltinFunctions.IsWindowOnly(name).Should().BeFalse(name);
        }

        // fts_match/fts_highlight/fts_snippet are pure functions of their arguments. fts_score is
        // not: it reads the covering index's corpus statistics and tokenizer configuration, so
        // creating, dropping or reconfiguring an index changes its result for unchanged arguments.
        // Schema expressions must therefore reject it the way SQLite rejects any function without
        // SQLITE_DETERMINISTIC.
        foreach (var name in new[] { "FTS_MATCH", "FTS_HIGHLIGHT", "FTS_SNIPPET" })
            SqliteBuiltinFunctions.IsDeterministic(name).Should().BeTrue(name);

        SqliteBuiltinFunctions.IsDeterministic("FTS_SCORE").Should().BeFalse();

        SqliteBuiltinFunctions.GetArities("FTS_MATCH").Should().Equal(-1);
        SqliteBuiltinFunctions.GetArities("FTS_SCORE").Should().Equal(-1);
        SqliteBuiltinFunctions.GetArities("FTS_HIGHLIGHT").Should().Equal(4);
        SqliteBuiltinFunctions.GetArities("FTS_SNIPPET").Should().Equal(6);
    }

    [Test]
    public void VectorDistanceFunctionsRemainDeterministicBuiltins()
    {
        // The vector method plans against the existing scalar functions rather than introducing new
        // ones, so their registration must be unchanged: a vector distance is a pure function of its
        // arguments and stays usable in schema expressions.
        foreach (var name in new[]
                 {
                     "VECTOR_DISTANCE_L2", "VECTOR_DISTANCE_COS", "VECTOR_DISTANCE_DOT", "VECTOR_DISTANCE_JACCARD",
                 })
        {
            SqliteBuiltinFunctions.Contains(name).Should().BeTrue(name);
            SqliteBuiltinFunctions.IsAggregate(name).Should().BeFalse(name);
            SqliteBuiltinFunctions.IsWindowOnly(name).Should().BeFalse(name);
            SqliteBuiltinFunctions.IsDeterministic(name).Should().BeTrue(name);
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Ahtola.slnx")))
            directory = directory.Parent;

        directory.Should().NotBeNull("the test run must sit inside the repository");
        return directory!.FullName;
    }
}
