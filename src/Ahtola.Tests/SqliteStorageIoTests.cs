using AwesomeAssertions;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

public class SqliteStorageIoTests
{
    private string _workDirectory = string.Empty;

    [SetUp]
    public void SetUp()
    {
        // Keep scratch files inside the test output tree, never a shared temp dir.
        _workDirectory = Path.Combine(TestContext.CurrentContext.WorkDirectory, "pagestore-io", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workDirectory);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_workDirectory))
            Directory.Delete(_workDirectory, recursive: true);
    }

    [Test]
    public void PhysicalFileRoundTripsPositionalReadsAndWrites()
    {
        var path = Path.Combine(_workDirectory, "positional.bin");
        using (var file = PhysicalFileSystem.Instance.OpenFile(path, FileOpenMode.CreateNew))
        {
            file.Write(0, [1, 2, 3, 4]);
            file.Write(8, [9, 9]);
            file.FlushToDisk();
            file.Length.Should().Be(10);

            var buffer = new byte[10];
            file.Read(0, buffer).Should().Be(10);
            buffer.Should().Equal(1, 2, 3, 4, 0, 0, 0, 0, 9, 9);
        }

        using var reopened = PhysicalFileSystem.Instance.OpenFile(path, FileOpenMode.OpenExisting, readOnly: true);
        var tail = new byte[4];
        reopened.Read(8, tail).Should().Be(2);
        tail.Should().Equal(9, 9, 0, 0);
        reopened.IsReadOnly.Should().BeTrue();
        Assert.Throws<InvalidOperationException>(() => reopened.Write(0, [0]));
    }

    [Test]
    public void PhysicalFileSetLengthTruncatesAndExtends()
    {
        var path = Path.Combine(_workDirectory, "resize.bin");
        using var file = PhysicalFileSystem.Instance.OpenFile(path, FileOpenMode.OpenOrCreate);
        file.Write(0, [1, 2, 3, 4, 5, 6, 7, 8]);

        file.SetLength(4);
        file.Length.Should().Be(4);

        file.SetLength(8);
        var buffer = new byte[8];
        file.Read(0, buffer).Should().Be(8);
        buffer.Should().Equal(1, 2, 3, 4, 0, 0, 0, 0);
    }

    [Test]
    public void PhysicalFileSystemOpenExistingThrowsWhenMissing()
    {
        var path = Path.Combine(_workDirectory, "missing.bin");
        Assert.Throws<FileNotFoundException>(
            () => PhysicalFileSystem.Instance.OpenFile(path, FileOpenMode.OpenExisting));
    }

    [Test]
    public void InMemoryFileZeroFillsHolesAndTruncatesTail()
    {
        var fileSystem = new InMemoryFileSystem();
        using var file = fileSystem.OpenFile("db", FileOpenMode.OpenOrCreate);

        file.Write(10, [7, 7, 7]);
        file.Length.Should().Be(13);

        var buffer = new byte[13];
        file.Read(0, buffer).Should().Be(13);
        buffer.Should().Equal(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 7, 7, 7);

        file.Read(13, new byte[4]).Should().Be(0);

        file.SetLength(11);
        file.SetLength(13);
        var afterTruncate = new byte[13];
        file.Read(0, afterTruncate).Should().Be(13);
        afterTruncate[10].Should().Be(7);
        afterTruncate[11].Should().Be(0);
        afterTruncate[12].Should().Be(0);
    }

    [Test]
    public void InMemoryFileDoesNotExtendForEmptyWritesOrOverflowedOffsets()
    {
        var fileSystem = new InMemoryFileSystem();
        using var file = fileSystem.OpenFile("db", FileOpenMode.OpenOrCreate);

        file.Write(10, ReadOnlySpan<byte>.Empty);
        file.Length.Should().Be(0);
        Assert.Throws<OverflowException>(() => file.Write(long.MaxValue, [1]));
        file.Length.Should().Be(0);
    }

    [Test]
    public void InMemoryFileSystemEnforcesOpenModes()
    {
        var fileSystem = new InMemoryFileSystem();
        Assert.Throws<FileNotFoundException>(() => fileSystem.OpenFile("nope", FileOpenMode.OpenExisting));

        using (fileSystem.OpenFile("db", FileOpenMode.CreateNew))
        {
        }

        Assert.Throws<IOException>(() => fileSystem.OpenFile("db", FileOpenMode.CreateNew));
        fileSystem.FileExists("db").Should().BeTrue();
    }

    [Test]
    public void DeterministicFaultInjectorFailsScheduledWriteWithoutMutating()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        using var file = fileSystem.OpenFile("db", FileOpenMode.OpenOrCreate);

        file.Write(0, [1, 2, 3, 4]);
        faults.FailNext(FileSystemOperation.Write);

        Assert.Throws<IOException>(() => file.Write(0, [9, 9, 9, 9]));

        var buffer = new byte[4];
        file.Read(0, buffer).Should().Be(4);
        buffer.Should().Equal(1, 2, 3, 4);

        // The next write succeeds because only the scheduled occurrence fails.
        file.Write(0, [5, 6, 7, 8]);
        file.Read(0, buffer).Should().Be(4);
        buffer.Should().Equal(5, 6, 7, 8);
    }

    [Test]
    public void PageStoreCreatesReopenableSqliteDatabase()
    {
        var fileSystem = new InMemoryFileSystem();
        using (var created = SqlitePageStore.Create(fileSystem, "main.db"))
        {
            created.PageSize.Should().Be(SqlitePageSize.Default);
            created.PageCount.Should().Be(1);
            created.Header.DatabaseSizeInPages.Should().Be(1);
        }

        using var opened = SqlitePageStore.Open(fileSystem, "main.db");
        opened.PageCount.Should().Be(1);
        opened.Header.PageSize.Should().Be(SqlitePageSize.Default);

        // Page 1 must contain a valid b-tree header immediately after the 100-byte db header.
        var page = opened.ReadPage(1);
        var btree = SqliteBtreePageHeader.Parse(page, isFirstPage: true);
        btree.PageType.Should().Be(SqliteBtreePageType.TableLeaf);
        btree.CellCount.Should().Be(0);
    }

    [Test]
    public void PageStoreRoundTripsAndAppendsPagesPreservingAlignment()
    {
        var fileSystem = new InMemoryFileSystem();
        using var store = SqlitePageStore.Create(fileSystem, "main.db");
        var pageSize = store.PageSize;

        var page2 = CreatePage(pageSize, fill: 0xAB);
        store.WritePage(2, page2);
        store.PageCount.Should().Be(2);

        var page3 = CreatePage(pageSize, fill: 0xCD);
        store.WritePage(3, page3);
        store.PageCount.Should().Be(3);

        store.ReadPage(2).Should().Equal(page2);
        store.ReadPage(3).Should().Equal(page3);

        // Overwrite in place does not change the page count.
        var rewritten = CreatePage(pageSize, fill: 0x11);
        store.WritePage(2, rewritten);
        store.PageCount.Should().Be(3);
        store.ReadPage(2).Should().Equal(rewritten);

        using var file = fileSystem.OpenFile("main.db", FileOpenMode.OpenExisting, readOnly: true);
        (file.Length % pageSize).Should().Be(0);
        file.Length.Should().Be((long)pageSize * 3);
    }

    [Test]
    public void PageStoreRejectsGapsAndOutOfRangeAccess()
    {
        var fileSystem = new InMemoryFileSystem();
        using var store = SqlitePageStore.Create(fileSystem, "main.db");
        var page = CreatePage(store.PageSize, fill: 0x22);

        // Skipping over page 2 would leave an uninitialized gap.
        Assert.Throws<ArgumentOutOfRangeException>(() => store.WritePage(3, page));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.ReadPage(2));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.ReadPage(0));
        Assert.Throws<ArgumentException>(() => store.WritePage(2, new byte[store.PageSize - 1]));
        Assert.Throws<ArgumentException>(() => store.ReadPage(1, new byte[store.PageSize + 1]));
    }

    [Test]
    public void PageStoreWritingPage1UpdatesHeaderButNotPageSize()
    {
        var fileSystem = new InMemoryFileSystem();
        using var store = SqlitePageStore.Create(fileSystem, "main.db");

        var updated = store.Header with { UserVersion = 99, SchemaCookie = 5 };
        var page1 = new byte[store.PageSize];
        store.ReadPage(1, page1);
        updated.WriteTo(page1);

        store.WritePage(1, page1);
        store.Header.UserVersion.Should().Be(99);
        store.Header.SchemaCookie.Should().Be(5);

        var wrongPageSize = store.Header with { PageSize = store.PageSize == 4096 ? 8192 : 4096 };
        var mismatched = new byte[store.PageSize];
        wrongPageSize.WriteTo(mismatched);
        Assert.Throws<InvalidOperationException>(() => store.WritePage(1, mismatched));
    }

    [Test]
    public void PageStoreRejectsStaleAuthoritativePageOneHeaders()
    {
        var fileSystem = new InMemoryFileSystem();
        using var store = SqlitePageStore.Create(fileSystem, "main.db");
        var stalePageOne = store.ReadPage(1);

        store.WritePage(2, CreatePage(store.PageSize, fill: 0xD0));
        Assert.Throws<InvalidOperationException>(() => store.WritePage(1, stalePageOne));

        store.PageCount.Should().Be(2);
        using var reopened = SqlitePageStore.Open(fileSystem, "main.db");
        reopened.PageCount.Should().Be(2);
    }

    [Test]
    public void PageStoreRestoresLengthWhenAppendHeaderUpdateFails()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        using var store = SqlitePageStore.Create(fileSystem, "main.db");
        faults.FailOnOccurrence(FileSystemOperation.Write, 3);

        Assert.Throws<IOException>(() => store.WritePage(2, CreatePage(store.PageSize, fill: 0xD1)));
        store.PageCount.Should().Be(1);
        using (var reopened = SqlitePageStore.Open(fileSystem, "main.db"))
            reopened.PageCount.Should().Be(1);

        store.WritePage(2, CreatePage(store.PageSize, fill: 0xD2));
        store.PageCount.Should().Be(2);
    }

    [Test]
    public void PageStoreUsesUsableSpaceForReservedPageBtreeHeaders()
    {
        var fileSystem = new InMemoryFileSystem();
        var header = SqliteDatabaseHeader.CreateDefault() with { ReservedSpace = 16 };
        using var store = SqlitePageStore.Create(fileSystem, "reserved.db", header);

        var root = SqliteBtreePageHeader.Parse(store.ReadPage(1), isFirstPage: true);
        root.CellContentAreaOffset.Should().Be(store.PageSize - 16);
        Assert.Throws<InvalidOperationException>(() => SqlitePageStore.Create(
            fileSystem,
            "invalid.db",
            SqliteDatabaseHeader.CreateDefault() with { PageSize = 512, ReservedSpace = 33 }));
    }

    [Test]
    public void PageStoreOpenRejectsCorruptLayout()
    {
        var fileSystem = new InMemoryFileSystem();
        using (SqlitePageStore.Create(fileSystem, "aligned.db"))
        {
        }

        // Non page-aligned length is corruption.
        using (var file = fileSystem.OpenFile("aligned.db", FileOpenMode.OpenExisting))
        {
            file.SetLength(file.Length + 7);
        }

        Assert.Throws<InvalidDataException>(() => SqlitePageStore.Open(fileSystem, "aligned.db"));

        // A file too small to hold a header cannot be opened.
        using (var tiny = fileSystem.OpenFile("tiny.db", FileOpenMode.OpenOrCreate))
        {
            tiny.Write(0, new byte[50]);
        }

        Assert.Throws<InvalidDataException>(() => SqlitePageStore.Open(fileSystem, "tiny.db"));
    }

    [Test]
    public void PageStoreOpenRejectsHeaderPageCountMismatch()
    {
        var fileSystem = new InMemoryFileSystem();
        using (SqlitePageStore.Create(fileSystem, "main.db"))
        {
        }

        // Header says one page but the file is two pages, with an authoritative counter.
        using (var file = fileSystem.OpenFile("main.db", FileOpenMode.OpenExisting))
        {
            Span<byte> header = stackalloc byte[SqliteDatabaseHeader.Size];
            file.Read(0, header).Should().Be(SqliteDatabaseHeader.Size);
            var pageSize = SqliteDatabaseHeader.Parse(header).PageSize;
            file.SetLength((long)pageSize * 2);
        }

        Assert.Throws<InvalidDataException>(() => SqlitePageStore.Open(fileSystem, "main.db"));
    }

    [Test]
    public void PageStoreReadOnlyRejectsWrites()
    {
        var fileSystem = new InMemoryFileSystem();
        using (SqlitePageStore.Create(fileSystem, "main.db"))
        {
        }

        using var store = SqlitePageStore.Open(fileSystem, "main.db", readOnly: true);
        store.IsReadOnly.Should().BeTrue();
        Assert.Throws<InvalidOperationException>(() => store.WritePage(1, new byte[store.PageSize]));
    }

    [Test]
    public void PageStoreSurfacesWriteFaultAndKeepsAlignment()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        using var store = SqlitePageStore.Create(fileSystem, "main.db");
        var pageSize = store.PageSize;

        faults.FailNext(FileSystemOperation.Write);
        Assert.Throws<IOException>(() => store.WritePage(2, CreatePage(pageSize, fill: 0x33)));

        // The failed append must not have grown the file past a page boundary.
        store.PageCount.Should().Be(1);
        using var file = fileSystem.OpenFile("main.db", FileOpenMode.OpenExisting, readOnly: true);
        (file.Length % pageSize).Should().Be(0);
        file.Length.Should().Be(pageSize);
    }

    [Test]
    public void PageStoreRoundTripsOnDisk()
    {
        var path = Path.Combine(_workDirectory, "ondisk.db");
        byte[] page2;
        using (var store = SqlitePageStore.Create(PhysicalFileSystem.Instance, path))
        {
            page2 = CreatePage(store.PageSize, fill: 0x5A);
            store.WritePage(2, page2);
            store.Flush();
        }

        using var reopened = SqlitePageStore.Open(PhysicalFileSystem.Instance, path);
        reopened.PageCount.Should().Be(2);
        reopened.ReadPage(2).Should().Equal(page2);
        (new FileInfo(path).Length % reopened.PageSize).Should().Be(0);
    }

    private static byte[] CreatePage(int pageSize, byte fill)
    {
        var page = new byte[pageSize];
        Array.Fill(page, fill);
        return page;
    }
}
