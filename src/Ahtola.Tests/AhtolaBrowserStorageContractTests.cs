using AwesomeAssertions;
using Ahtola.Data.Sqlite.Browser;
using Ahtola.Data.Sqlite.Browser.Storage;

namespace Ahtola.Tests;

public sealed class AhtolaBrowserStorageContractTests
{
    [TestCase("NotFoundError: missing", typeof(FileNotFoundException))]
    [TestCase("QuotaExceededError: full", typeof(IOException))]
    [TestCase("NoModificationAllowedError: denied", typeof(UnauthorizedAccessException))]
    [TestCase("InvalidModificationError: exists", typeof(IOException))]
    [TestCase("InvalidStateError: closed", typeof(IOException))]
    public void BrowserStorageErrorsUseStructuredNames(
        string message,
        Type expectedExceptionType)
    {
        var source = new InvalidOperationException(message);
        var mapped = BrowserStorageExceptionMapper.Map(
            source,
            "data.db",
            "open");

        mapped.Should().BeOfType(expectedExceptionType);
        mapped.InnerException.Should().BeSameAs(source);
    }

    [Test]
    public void BrowserStorageDiagnosticSucceedsOnlyWhenEveryCheckPasses()
    {
        new AhtolaBrowserStorageDiagnosticResult(
                CompetingContextRejected: true,
                PositionalIoMatches: true,
                AtomicReplaceMatches: true,
                ManagedPersistenceMatches: true,
                Details: "passed")
            .Succeeded.Should().BeTrue();

        new AhtolaBrowserStorageDiagnosticResult(
                CompetingContextRejected: true,
                PositionalIoMatches: false,
                AtomicReplaceMatches: true,
                ManagedPersistenceMatches: true,
                Details: "failed")
            .Succeeded.Should().BeFalse();
    }
}
