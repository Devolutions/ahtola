using AwesomeAssertions;
using Ahtola.Data.Sqlite.Browser.Storage;

#pragma warning disable CA1416

namespace Ahtola.Tests;

public sealed class AhtolaBrowserOpfsPathReservationTests
{
    [TestCase(".ahtola-replace-anything")]
    [TestCase(".ahtola-replace-journal.0")]
    [TestCase(".ahtola-replace-journal.1")]
    [TestCase("app-data/.ahtola-replace-nested")]
    [TestCase("a/b/.ahtola-replace-deep.tmp")]
    public void CanonicalizeRejectsAnySegmentStartingWithTheReservedPrefix(string path)
    {
        var act = () => OpfsAsyncFileSystem.Canonicalize(path);
        act.Should().Throw<ArgumentException>();
    }

    [TestCase("not.ahtola-replace-mine.db")]
    [TestCase("app-data/my.ahtola-replace-notes.db")]
    [TestCase("plainfile.db")]
    [TestCase("nested/dir/file.db")]
    public void CanonicalizeAllowsNamesThatMerelyContainTheReservedPrefix(string path)
    {
        OpfsAsyncFileSystem.Canonicalize(path).Should().Be(path);
    }

    [TestCase("a/../b")]
    [TestCase("a/./b")]
    [TestCase("a//b")]
    public void CanonicalizeStillRejectsEmptyCurrentAndParentSegments(string path)
    {
        var act = () => OpfsAsyncFileSystem.Canonicalize(path);
        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void CanonicalizeRejectsRootedPaths()
    {
        var act = () => OpfsAsyncFileSystem.Canonicalize("/absolute/path");
        act.Should().Throw<ArgumentException>();
    }
}

#pragma warning restore CA1416
