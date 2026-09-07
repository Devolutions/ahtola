using System.Text.Json;
using Ahtola.Tests.Sqltest;
using AwesomeAssertions;

namespace Ahtola.Tests;

[Category("CoverageExcluded")]
public class ManagedGapInventoryContractTests
{
    [Test]
    public void CurrentExpectedFailureCountMatchesTheLiveCorpusBaseline()
    {
        using var inventory = ReadInventory();

        inventory.RootElement.GetProperty("meta")
            .GetProperty("current_conformance_expected_failures").GetInt32()
            .Should().Be(SqltestCorpus.ExpectedFailures.Count,
                "historical closure counts must not obscure current conformance gaps");
    }

    [TestCase("by_layer", "layer")]
    [TestCase("by_kind", "kind")]
    [TestCase("by_severity", "severity")]
    [TestCase("by_effort", "effort")]
    [TestCase("by_status", "status")]
    public void HistoricalCountsMatchTheInventoryRecords(string countName, string propertyName)
    {
        using var inventory = ReadInventory();
        var root = inventory.RootElement;
        var records = root.GetProperty("gaps").EnumerateArray().ToArray();
        var metadata = root.GetProperty("meta");

        metadata.GetProperty("entry_count").GetInt32().Should().Be(records.Length);

        var recordedCounts = metadata.GetProperty("counts").GetProperty(countName)
            .EnumerateObject()
            .ToDictionary(static entry => entry.Name, static entry => entry.Value.GetInt32());
        var actualCounts = records
            .GroupBy(record => record.GetProperty(propertyName).GetString()
                ?? throw new InvalidDataException($"Inventory record has a null {propertyName}."))
            .ToDictionary(static group => group.Key, static group => group.Count());

        recordedCounts.Should().BeEquivalentTo(actualCounts);
    }

    [Test]
    public void InventoryLinksToAnExistingCurrentExecutionPlan()
    {
        using var inventory = ReadInventory();
        var plan = inventory.RootElement.GetProperty("meta")
            .GetProperty("current_execution_plan").GetString();

        plan.Should().NotBeNullOrWhiteSpace();
        File.Exists(Path.Combine(FindRepositoryRoot(), plan!))
            .Should().BeTrue("historical dispositions need a current closure plan");
    }

    private static JsonDocument ReadInventory()
        => JsonDocument.Parse(File.ReadAllText(
            Path.Combine(FindRepositoryRoot(), "docs", "turso-gap-inventory.json")));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Ahtola.slnx")))
            directory = directory.Parent;

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("The inventory contract requires a repository checkout.");
    }
}
