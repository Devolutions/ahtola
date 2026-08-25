using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Ahtola.Data.Sqlite;
using AwesomeAssertions;

namespace Ahtola.Tests;

/// <summary>
/// Trim and NativeAOT contracts for the shipped ADO.NET and EF Core packages.
/// </summary>
/// <remarks>
/// The publish-time gate lives in <c>scripts/Invoke-BrowserTrimAnalysis.ps1</c>, which fails on any
/// IL2xxx/IL3xxx warning attributable to Ahtola. These tests guard the source-level invariants that
/// keep that gate green, so a regression is caught by the ordinary suite instead of only by a
/// browser publish: the annotated <see cref="DbDataReader.GetFieldType(int)"/> contract, the
/// reflection-free optional native provider, the statically rooted tuple accumulator, the schema
/// table shape, and the project properties that turn the analyzers on in the first place.
/// </remarks>
public sealed class ManagedTrimAndAotContractTests
{
    private const DynamicallyAccessedMemberTypes FieldTypeContract =
        DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.PublicProperties;

    private static readonly string[] ShippedProjectDirectories =
    [
        "Ahtola.Core",
        "Ahtola.Data",
        "Ahtola.Data.Sqlite",
        "Ahtola.Data.Sqlite.Browser",
        "Ahtola.EntityFrameworkCore.Sqlite",
    ];

    [Test]
    public void ShippedProjectsDeclareAotAndTrimProperties()
    {
        var repositoryRoot = FindRepositoryRoot();
        foreach (var directory in ShippedProjectDirectories)
        {
            var projectPath = Path.Combine(repositoryRoot, "src", directory, $"{directory}.csproj");
            File.Exists(projectPath).Should().BeTrue($"'{projectPath}' must exist");

            var project = XDocument.Load(projectPath);
            foreach (var property in new[] { "IsAotCompatible", "IsTrimmable" })
            {
                var values = project.Descendants(property).Select(static element => element.Value).ToList();
                values.Should().ContainSingle($"{directory} must declare <{property}> exactly once");
                values[0].Should().Be("true", $"{directory}'s <{property}> must be true");
            }
        }
    }

    [Test]
    public void ShippedSourcesNeverSuppressTrimOrAotWarnings()
    {
        var repositoryRoot = FindRepositoryRoot();
        string[] forbiddenInSource =
        [
            "UnconditionalSuppressMessage",
            "SuppressTrimAnalysisWarnings",
        ];

        var offenders = new List<string>();
        foreach (var directory in ShippedProjectDirectories)
        {
            var root = Path.Combine(repositoryRoot, "src", directory);
            Directory.Exists(root).Should().BeTrue($"'{root}' must exist");

            foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                {
                    continue;
                }

                var text = File.ReadAllText(file);
                foreach (var pattern in forbiddenInSource)
                {
                    if (text.Contains(pattern, StringComparison.Ordinal))
                        offenders.Add($"{Path.GetFileName(file)} contains '{pattern}'");
                }
            }

            // NoWarn must never silence an IL2xxx/IL3xxx diagnostic.
            var projectPath = Path.Combine(root, $"{directory}.csproj");
            foreach (var noWarn in XDocument.Load(projectPath).Descendants("NoWarn").Select(static e => e.Value))
            {
                if (Regex.IsMatch(noWarn, @"\bIL[23]\d{3}\b"))
                    offenders.Add($"{directory}.csproj NoWarn silences a trim/AOT diagnostic: {noWarn}");
            }
        }

        offenders.Should().BeEmpty();
    }

    [Test]
    public void EveryGetFieldTypeOverrideMatchesTheBaseAnnotation()
    {
        var expected = typeof(DbDataReader)
            .GetMethod(nameof(DbDataReader.GetFieldType), [typeof(int)])!
            .ReturnParameter
            .GetCustomAttribute<DynamicallyAccessedMembersAttribute>();
        expected.Should().NotBeNull("the framework contract this test mirrors must exist");
        expected!.MemberTypes.Should().Be(FieldTypeContract);

        var readers = new[] { typeof(AhtolaDataReader).Assembly, typeof(SqliteDataReader).Assembly }
            .Distinct()
            .SelectMany(static assembly => assembly.GetTypes())
            .Where(static type => typeof(DbDataReader).IsAssignableFrom(type) && !type.IsAbstract)
            .ToList();

        readers.Should().NotBeEmpty("the ADO.NET stack must expose reader types");

        foreach (var reader in readers)
        {
            var method = reader.GetMethod(
                nameof(DbDataReader.GetFieldType),
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly,
                binder: null,
                [typeof(int)],
                modifiers: null);
            if (method is null)
                continue;

            var annotation = method.ReturnParameter.GetCustomAttribute<DynamicallyAccessedMembersAttribute>();
            annotation.Should().NotBeNull($"{reader.FullName}.GetFieldType must carry the base return annotation");
            annotation!.MemberTypes.Should().Be(
                FieldTypeContract,
                $"{reader.FullName}.GetFieldType must match DbDataReader.GetFieldType exactly");
        }
    }

    [Test]
    public void CreateAggregateOverloadsRootTheAccumulatorConstructors()
    {
        var overloads = typeof(SqliteConnection)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(static method => method.Name == nameof(SqliteConnection.CreateAggregate) && method.IsGenericMethodDefinition)
            .ToList();

        overloads.Should().NotBeEmpty();

        foreach (var overload in overloads)
        {
            var accumulator = overload.GetGenericArguments().SingleOrDefault(static argument => argument.Name == "TAccumulate");
            accumulator.Should().NotBeNull($"{overload} must declare a TAccumulate parameter");

            var annotation = accumulator!.GetCustomAttribute<DynamicallyAccessedMembersAttribute>();
            annotation.Should().NotBeNull(
                $"{overload}'s TAccumulate must be annotated so Activator.CreateInstance on a tuple accumulator is statically rooted");
            annotation!.MemberTypes.Should().Be(DynamicallyAccessedMemberTypes.PublicConstructors);
        }
    }

    [Test]
    public void TupleAccumulatorAggregateRoundTripsThroughTheEncodedAccumulator()
    {
        using var connection = new SqliteConnection("Data Source=:memory:;Mode=Memory");
        connection.Open();

        using (var seed = connection.CreateCommand())
        {
            seed.CommandText = "CREATE TABLE probe(value INTEGER NOT NULL); INSERT INTO probe(value) VALUES (10), (20), (30);";
            seed.ExecuteNonQuery();
        }

        // Same accumulator shape EF Core's ef_avg uses: (decimal sum, ulong count).
        connection.CreateAggregate<decimal, (decimal Sum, ulong Count), decimal?>(
            "probe_avg",
            (Sum: 0m, Count: 0UL),
            static (accumulator, value) => (accumulator.Sum + value, accumulator.Count + 1),
            static accumulator => accumulator.Count == 0 ? null : accumulator.Sum / accumulator.Count);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT probe_avg(value) FROM probe;";
        Convert.ToDecimal(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture)
            .Should().Be(20m);
    }

    [Test]
    public void NativeProviderRegistrationIsExplicitAndReflectionFree()
    {
        var repositoryRoot = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(repositoryRoot, "src", "Ahtola.Data", "AhtolaNativeProvider.cs"));

        string[] forbidden =
        [
            "Assembly.Load",
            "AssemblyLoadContext",
            "GetMethod(",
            "Type.GetType(",
            "Activator.CreateInstance",
            "MakeGenericType",
            "MakeGenericMethod",
        ];
        foreach (var pattern in forbidden)
            source.Should().NotContain(pattern, $"AhtolaNativeProvider must not use '{pattern}'");

        // The companion registers itself through the same explicit factory model as
        // SqliteNativeProvider; the only supported entry point is Register(factory).
        var register = typeof(AhtolaNativeProvider).GetMethod(
            nameof(AhtolaNativeProvider.Register),
            BindingFlags.Public | BindingFlags.Static);
        register.Should().NotBeNull();
        register!.GetParameters().Should().ContainSingle()
            .Which.ParameterType.Should().Be<AhtolaNativeProviderFactory>();

        typeof(SqliteNativeProvider).GetMethod(nameof(SqliteNativeProvider.Register), BindingFlags.Public | BindingFlags.Static)
            .Should().NotBeNull("the facade companion keeps the same explicit registration model");
    }

    [Test]
    public void NativeProviderFailsClosedWithTheCompanionMessageWhenNoFactoryIsRegistered()
    {
        // AhtolaNativeProvider.Register is process-wide, and other fixtures register a fake factory,
        // so assert the fail-closed contract on the message the product throws rather than on
        // whichever registration order the run happens to pick.
        AhtolaNativeProvider.MissingFactoryMessage.Should()
            .Contain("Turso.Data.Sqlite.Native")
            .And.Contain("PackageReference");
        AhtolaNativeProvider.NativeProviderAssemblyName.Should().Be("Turso.Data.Native");

        if (AhtolaNativeProvider.Current is null)
        {
            using var connection = new SqliteConnection("Data Source=:memory:;Mode=Memory;Local Provider=Native");
            var open = connection.Open;
            open.Should().Throw<NotSupportedException>()
                .WithMessage(AhtolaNativeProvider.MissingFactoryMessage);
        }
    }

    [Test]
    public void ReaderSchemaTablesDeclareTheDataTypeColumnAsSystemType()
    {
        using var connection = new SqliteConnection("Data Source=:memory:;Mode=Memory");
        connection.Open();
        using (var seed = connection.CreateCommand())
        {
            seed.CommandText = "CREATE TABLE probe(id INTEGER PRIMARY KEY, label TEXT); INSERT INTO probe(label) VALUES ('a');";
            seed.ExecuteNonQuery();
        }

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, label FROM probe;";
        using var reader = command.ExecuteReader();

        AssertSchemaShape(reader.GetSchemaTable()!);

        using var inner = new AhtolaConnection("Data Source=:memory:;Mode=Memory");
        inner.Open();
        foreach (var statement in new[]
                 {
                     "CREATE TABLE probe(id INTEGER PRIMARY KEY, label TEXT);",
                     "INSERT INTO probe(label) VALUES ('a');",
                 })
        {
            using var seed = inner.CreateCommand();
            seed.CommandText = statement;
            seed.ExecuteNonQuery();
        }

        using var innerCommand = inner.CreateCommand();
        innerCommand.CommandText = "SELECT id, label FROM probe;";
        using var innerReader = innerCommand.ExecuteReader();
        AssertSchemaShape(innerReader.GetSchemaTable()!);

        static void AssertSchemaShape(DataTable schema)
        {
            schema.Rows.Count.Should().Be(2);

            // Values stay CLR Type instances, which is what DbDataAdapter and DbCommandBuilder read.
            foreach (DataRow row in schema.Rows)
                row[SchemaTableColumn.DataType].Should().BeAssignableTo<Type>();

            // The column is declared System.Type, matching every other ADO.NET provider. It is
            // sourced from the schema table DataTableReader builds inside System.Data.Common, so
            // the annotated DataColumn.DataType flow is satisfied there rather than demanded here
            // — no reflection over System.Type, no suppression, no IL2111.
            schema.Columns[SchemaTableColumn.DataType]!.DataType.Should().Be<Type>();
            schema.Columns[SchemaTableColumn.DataType]!.AllowDBNull.Should().BeTrue();
            schema.Columns[SchemaTableColumn.DataType]!.ReadOnly.Should().BeFalse();
        }
    }

    [Test]
    public void AdapterFillStillTypesColumnsFromTheSchemaTable()
    {
        using var connection = new SqliteConnection("Data Source=:memory:;Mode=Memory");
        connection.Open();
        using (var seed = connection.CreateCommand())
        {
            seed.CommandText = "CREATE TABLE probe(id INTEGER PRIMARY KEY, label TEXT); INSERT INTO probe(label) VALUES ('a');";
            seed.ExecuteNonQuery();
        }

        using var adapter = new AhtolaDataAdapter("SELECT id, label FROM probe;", connection);
        var table = new DataTable();
        adapter.Fill(table);

        table.Rows.Count.Should().Be(1);
        table.Columns.Cast<DataColumn>().Select(static column => column.DataType)
            .Should().Equal(typeof(long), typeof(string));
    }

    [Test]
    public void AdoOnlyTrimConsumersExcludeEntityFrameworkCore()
    {
        var repositoryRoot = FindRepositoryRoot();
        string[] adoOnlyConsumers =
        [
            Path.Combine("samples", "AdoTrimConsumer", "AdoTrimConsumer.csproj"),
            Path.Combine("samples", "BrowserAdoTrimConsumer", "BrowserAdoTrimConsumer.csproj"),
        ];

        foreach (var relative in adoOnlyConsumers)
        {
            var projectPath = Path.Combine(repositoryRoot, relative);
            File.Exists(projectPath).Should().BeTrue($"'{projectPath}' must exist");

            var project = XDocument.Load(projectPath);
            var packages = project.Descendants("PackageReference")
                .Select(static element => element.Attribute("Include")?.Value ?? string.Empty)
                .ToList();

            packages.Should().Contain(
                static package => package.StartsWith("Devolutions.Ahtola.", StringComparison.Ordinal),
                $"{relative} must consume the packed Ahtola packages");
            packages.Should().NotContain(
                static package => package.Contains("EntityFrameworkCore", StringComparison.OrdinalIgnoreCase),
                $"{relative} proves the ADO-only closure and must not reference EF Core");

            // The gate reads granular, unsuppressed warnings from these publishes.
            project.Descendants("SuppressTrimAnalysisWarnings").Select(static e => e.Value)
                .Should().OnlyContain(static value => value == "false");
        }
    }

    [Test]
    public void TrimAnalysisGateRequiresZeroTotalWarningsOnTheAdoOnlyProfiles()
    {
        var repositoryRoot = FindRepositoryRoot();
        var gate = Path.Combine(repositoryRoot, "scripts", "Invoke-TrimAnalysisGate.ps1");
        File.Exists(gate).Should().BeTrue($"'{gate}' must exist");

        var text = File.ReadAllText(gate);
        foreach (var profile in new[] { "'Ado'", "'AdoDesktopTrimmed'", "'AdoDesktopAot'" })
        {
            var index = text.IndexOf($"Name        = {profile}", StringComparison.Ordinal);
            index.Should().BeGreaterThan(-1, $"the gate must define the {profile} profile");

            var block = text.Substring(index, Math.Min(700, text.Length - index));
            block.Should().Contain(
                "RequireZeroTotalWarnings = $true",
                $"the {profile} profile must fail on any IL2xxx/IL3xxx warning in the closure");
        }

        text.Should().Contain("-p:SuppressTrimAnalysisWarnings=false");
        text.Should().Contain("-p:TrimmerSingleWarn=false");
        text.Should().Contain("IL2104|IL3053", "grouped per-assembly warnings must be gated too");
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
