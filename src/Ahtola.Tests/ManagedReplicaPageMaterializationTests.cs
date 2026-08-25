using System.Net;
using System.Net.Http.Headers;
using System.Text;
using AwesomeAssertions;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

public sealed class ManagedReplicaPageMaterializationTests
{
    private const int PageSize = 4096;
    private const string Revision = "revision-42";

    [Test]
    public void MissingPageIsFetchedOnceAndThenReadFromTheDurableCache()
    {
        var fileSystem = CreatePartialDatabase(pageCount: 4);
        var expected = CreatePage(0x42);
        var source = new RecordingPageSource(
            databasePages: 4,
            pageFactory: _ => expected);

        using var materializing = OpenMaterializing(fileSystem, source, prefetchSegments: false);
        using var store = SqlitePageStore.Open(materializing, "replica.db");

        store.ReadPage(2).Should().Equal(expected);
        store.ReadPage(2).Should().Equal(expected);

        source.CallCount.Should().Be(1);
        source.Requests.Should().ContainSingle().Which.Should().Equal(1UL);
    }

    [Test]
    public async Task ConcurrentFaultsForTheSamePageShareOneRemoteFetch()
    {
        var fileSystem = CreatePartialDatabase(pageCount: 4);
        var expected = CreatePage(0x53);
        var source = new BlockingPageSource(databasePages: 4, expected);
        using var materializing = OpenMaterializing(fileSystem, source, prefetchSegments: false);

        var reads = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() =>
            {
                using var file = materializing.OpenFile("replica.db", FileOpenMode.OpenExisting, readOnly: true);
                var page = new byte[PageSize];
                file.Read(PageSize, page).Should().Be(PageSize);
                return page;
            }))
            .ToArray();

        await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        source.CallCount.Should().Be(1);
        source.Release();

        var pages = await Task.WhenAll(reads).WaitAsync(TimeSpan.FromSeconds(5));
        pages.Should().AllSatisfy(page => page.Should().Equal(expected));
        source.CallCount.Should().Be(1);
    }

    [Test]
    public async Task ConnectionsSharingAReplicaRegistryEntryCoalesceTheirFault()
    {
        var databasePath = $"registry-{Guid.NewGuid():N}.db";
        var fileSystem = new InMemoryFileSystem();
        InitializePartialDatabase(fileSystem, databasePath, pageCount: 4);
        var expected = CreatePage(0x54);
        var source = new BlockingPageSource(databasePages: 4, expected);
        using var first = ManagedReplicaPageMaterializationRegistry.Acquire(
            fileSystem,
            databasePath,
            Revision,
            source,
            prefetchSegments: false);
        using var second = ManagedReplicaPageMaterializationRegistry.Acquire(
            fileSystem,
            databasePath,
            Revision,
            new RecordingPageSource(4, _ => throw new InvalidOperationException("The first source owns the entry.")),
            prefetchSegments: false);

        var reads = new[] { first, second }
            .Select(lease => Task.Run(() =>
            {
                using var file = lease.FileSystem.OpenFile(databasePath, FileOpenMode.OpenExisting, readOnly: true);
                var page = new byte[PageSize];
                file.Read(PageSize, page).Should().Be(PageSize);
                return page;
            }))
            .ToArray();

        await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        source.CallCount.Should().Be(1);
        source.Release();

        var pages = await Task.WhenAll(reads).WaitAsync(TimeSpan.FromSeconds(5));
        pages.Should().AllSatisfy(page => page.Should().Equal(expected));
        source.CallCount.Should().Be(1);
    }

    [Test]
    public async Task RegistryDoesNotPublishAReplacementUntilLastCloseFinishes()
    {
        var databasePath = $"registry-close-{Guid.NewGuid():N}.db";
        var inner = new InMemoryFileSystem();
        InitializePartialDatabase(inner, databasePath, pageCount: 4);
        var fileSystem = new BlockingDisposeFileSystem(inner, databasePath);
        var source = new RecordingPageSource(4, _ => CreatePage(0x55));
        var first = ManagedReplicaPageMaterializationRegistry.Acquire(
            fileSystem,
            databasePath,
            Revision,
            source,
            prefetchSegments: false);
        fileSystem.Arm();

        var close = Task.Run(first.Dispose);
        await fileSystem.DisposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var acquireStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var replacement = Task.Run(() =>
        {
            acquireStarted.SetResult(true);
            return ManagedReplicaPageMaterializationRegistry.Acquire(
                fileSystem,
                databasePath,
                Revision,
                source,
                prefetchSegments: false);
        });
        await acquireStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(100);

        replacement.IsCompleted.Should().BeFalse();
        fileSystem.OpenedWhileDisposeWasBlocked.Should().BeFalse();
        fileSystem.Release();

        await close.WaitAsync(TimeSpan.FromSeconds(5));
        using var reopened = await replacement.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestCase(InvalidPageResponse.WrongPage)]
    [TestCase(InvalidPageResponse.WrongSize)]
    public void InvalidRemotePageIsRejectedBeforeSparseBytesCanBeRead(InvalidPageResponse response)
    {
        var fileSystem = CreatePartialDatabase(pageCount: 4);
        var source = new DelegatePageSource((revision, _, _) =>
        {
            var page = response switch
            {
                InvalidPageResponse.WrongPage =>
                    new ManagedReplicaFetchedPage(2, CreatePage(0x61)),
                InvalidPageResponse.WrongSize =>
                    new ManagedReplicaFetchedPage(1, new byte[PageSize - 1]),
                _ => throw new ArgumentOutOfRangeException(nameof(response)),
            };
            return Task.FromResult(new ManagedReplicaPageBatch(revision, 4, [page]));
        });

        using var materializing = OpenMaterializing(fileSystem, source, prefetchSegments: false);
        using var file = materializing.OpenFile("replica.db", FileOpenMode.OpenExisting, readOnly: true);
        var destination = Enumerable.Repeat((byte)0xcc, PageSize).ToArray();

        var read = () => file.Read(PageSize, destination);

        read.Should().Throw<InvalidDataException>();
        destination.Should().OnlyContain(value => value == 0xcc);
    }

    [Test]
    public void MaterializedPageStateSurvivesReopenWithoutAnotherFetch()
    {
        var fileSystem = CreatePartialDatabase(pageCount: 4);
        var expected = CreatePage(0x72);
        var firstSource = new RecordingPageSource(4, _ => expected);
        using (var materializing = OpenMaterializing(fileSystem, firstSource, prefetchSegments: false))
        using (var store = SqlitePageStore.Open(materializing, "replica.db"))
        {
            store.ReadPage(3).Should().Equal(expected);
        }

        var reopenedSource = new DelegatePageSource((_, _, _) =>
            Task.FromException<ManagedReplicaPageBatch>(
                new InvalidOperationException("A durable cache hit must not contact the remote source.")));
        using var reopened = OpenMaterializing(fileSystem, reopenedSource, prefetchSegments: false);
        using var reopenedStore = SqlitePageStore.Open(reopened, "replica.db");

        reopenedStore.ReadPage(3).Should().Equal(expected);
        firstSource.CallCount.Should().Be(1);
        reopenedSource.CallCount.Should().Be(0);
    }

    [Test]
    public void LocalWritesReplaceWholeMissingPagesAndPersistTheirState()
    {
        var fileSystem = CreatePartialDatabase(pageCount: 4);
        var expected = CreatePage(0x74);
        var source = new DelegatePageSource((_, _, _) =>
            Task.FromException<ManagedReplicaPageBatch>(
                new InvalidOperationException("A local page replacement must not contact the remote source.")));
        using (var materializing = OpenMaterializing(fileSystem, source, prefetchSegments: false))
        using (var file = materializing.OpenFile("replica.db", FileOpenMode.OpenExisting))
        {
            var partialWrite = () => file.Write(PageSize + 1, expected.AsSpan(1));

            partialWrite.Should().Throw<InvalidDataException>();
            file.Write(PageSize, expected);
            file.FlushToDisk();
        }

        using var reopened = OpenMaterializing(fileSystem, source, prefetchSegments: false);
        using var store = SqlitePageStore.Open(reopened, "replica.db");

        store.ReadPage(2).Should().Equal(expected);
        reopened.HasLocalMutations.Should().BeTrue();
        source.CallCount.Should().Be(0);
    }

    [TestCase((int)ManagedReplicaDurableBoundary.PageMutationIntentPersisted, false)]
    [TestCase((int)ManagedReplicaDurableBoundary.PageMutationDatabasePersisted, true)]
    public void InterruptedLocalPageWritesRecoverWithoutPublishingSparseBytes(
        int boundaryValue,
        bool localWriteWasDurable)
    {
        var boundary = (ManagedReplicaDurableBoundary)boundaryValue;
        var fileSystem = CreatePartialDatabase(pageCount: 4);
        var localPage = CreatePage(0x75);
        var remotePage = CreatePage(0x76);
        var unusedSource = new DelegatePageSource((_, _, _) =>
            Task.FromException<ManagedReplicaPageBatch>(
                new InvalidOperationException("The interrupted write must not fetch.")));
        using (var materializing = OpenMaterializing(fileSystem, unusedSource, prefetchSegments: false))
        using (var file = materializing.OpenFile("replica.db", FileOpenMode.OpenExisting))
        using (ManagedReplicaFaultInjection.Push(point =>
               {
                   if (point == boundary)
                       throw new InvalidOperationException("Injected page mutation interruption.");
               }))
        {
            var write = () => file.Write(PageSize, localPage);
            write.Should().Throw<InvalidOperationException>();
        }

        var reopenedSource = new RecordingPageSource(4, _ => remotePage);
        using var reopened = OpenMaterializing(fileSystem, reopenedSource, prefetchSegments: false);
        using var store = SqlitePageStore.Open(reopened, "replica.db");

        store.ReadPage(2).Should().Equal(localWriteWasDurable ? localPage : remotePage);
        reopened.HasLocalMutations.Should().BeTrue();
        reopenedSource.CallCount.Should().Be(localWriteWasDurable ? 0 : 1);
    }

    [Test]
    public void PhysicalPartialReplicaHasExclusiveCrossProcessOwnership()
    {
        var path = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            $"managed-replica-page-owner-{Guid.NewGuid():N}.db");
        try
        {
            InitializePartialDatabase(PhysicalFileSystem.Instance, path, pageCount: 4);
            var source = new RecordingPageSource(4, _ => CreatePage(0x77));
            using var owner = new ManagedReplicaPageMaterializingFileSystem(
                PhysicalFileSystem.Instance,
                path,
                Revision,
                source,
                prefetchSegments: false);

            var secondOpen = () => new ManagedReplicaPageMaterializingFileSystem(
                PhysicalFileSystem.Instance,
                path,
                Revision,
                source,
                prefetchSegments: false);

            secondOpen.Should().Throw<IOException>().WithMessage("*another process*");
        }
        finally
        {
            foreach (var suffix in new[]
                     {
                         string.Empty,
                         "-wal",
                         ManagedReplicaPageMaterializingFileSystem.StateSuffix,
                         ManagedReplicaPageMaterializingFileSystem.OwnershipLockSuffix,
                     })
            {
                var artifact = path + suffix;
                if (File.Exists(artifact))
                    File.Delete(artifact);
            }
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void SegmentPrefetchCanBeEnabledWithoutChangingTheFaultedPage(bool prefetchSegments)
    {
        var fileSystem = CreatePartialDatabase(
            pageCount: 8,
            segmentSize: 4 * PageSize);
        var source = new RecordingPageSource(
            databasePages: 8,
            pageFactory: pageId => CreatePage(checked((byte)(0x20 + pageId))));
        using var materializing = OpenMaterializing(fileSystem, source, prefetchSegments);
        using var store = SqlitePageStore.Open(materializing, "replica.db");

        store.ReadPage(6).Should().Equal(CreatePage(0x25));

        source.CallCount.Should().Be(1);
        source.Requests.Should().ContainSingle();
        if (prefetchSegments)
        {
            source.Requests[0].Should().Equal(4UL, 5UL, 6UL, 7UL);
            materializing.IsSegmentMaterialized(5).Should().BeTrue();
            store.ReadPage(8).Should().Equal(CreatePage(0x27));
            source.CallCount.Should().Be(1);
        }
        else
        {
            source.Requests[0].Should().Equal(5UL);
            materializing.IsSegmentMaterialized(5).Should().BeFalse();
        }
    }

    [Test]
    public void OfflineFailureIsNotCachedAndNeverFallsThroughToSparseZeros()
    {
        var fileSystem = CreatePartialDatabase(pageCount: 4);
        var expected = CreatePage(0x34);
        var source = new RetryPageSource(databasePages: 4, expected);
        using var materializing = OpenMaterializing(fileSystem, source, prefetchSegments: false);
        using var file = materializing.OpenFile("replica.db", FileOpenMode.OpenExisting, readOnly: true);
        var destination = Enumerable.Repeat((byte)0xdd, PageSize).ToArray();

        var firstRead = () => file.Read(PageSize, destination);

        firstRead.Should().Throw<HttpRequestException>().WithMessage("offline");
        destination.Should().OnlyContain(value => value == 0xdd);

        file.Read(PageSize, destination).Should().Be(PageSize);
        destination.Should().Equal(expected);
        source.CallCount.Should().Be(2);
    }

    [Test]
    public void FailedStateTailRollbackPoisonsTheOpenMaterializer()
    {
        var inner = CreatePartialDatabase(pageCount: 4);
        var fileSystem = new StateTailFailureFileSystem(
            inner,
            "replica.db" + ManagedReplicaPageMaterializingFileSystem.StateSuffix);
        var source = new RecordingPageSource(4, _ => CreatePage(0x35));
        using var materializing = OpenMaterializing(fileSystem, source, prefetchSegments: false);
        using var file = materializing.OpenFile("replica.db", FileOpenMode.OpenExisting, readOnly: true);
        fileSystem.Arm();
        var destination = new byte[PageSize];

        var firstRead = () => file.Read(PageSize, destination);
        firstRead.Should().Throw<IOException>().WithMessage("*partial record could not be removed*");

        var secondRead = () => file.Read(2 * PageSize, destination);
        secondRead.Should().Throw<InvalidDataException>().WithMessage("*close and reopen*");
        source.CallCount.Should().Be(1);
    }

    [Test]
    public async Task PagerDoesNotHoldItsInternalGateDuringRemoteFetch()
    {
        var fileSystem = CreatePartialDatabase(pageCount: 4);
        var expected = CreatePage(0x45);
        SqlitePager? pager = null;
        var source = new DelegatePageSource(async (revision, pageIds, cancellationToken) =>
        {
            var headerRead = Task.Run(
                () => pager!.ReadCommittedPage(1),
                cancellationToken);
            await headerRead.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            return new ManagedReplicaPageBatch(
                revision,
                4,
                pageIds.Select(pageId => new ManagedReplicaFetchedPage(pageId, expected)).ToArray());
        });
        using var materializing = OpenMaterializing(fileSystem, source, prefetchSegments: false);
        using var openedPager = SqlitePager.Open(
            materializing,
            "replica.db",
            "replica.db-wal",
            readOnly: true);
        pager = openedPager;
        var read = Task.Run(() => openedPager.ReadCommittedPage(2));

        (await read.WaitAsync(TimeSpan.FromSeconds(5))).Should().Equal(expected);

        source.CallCount.Should().Be(1);
    }

    [Test]
    public async Task TargetedPullPinsRevisionAndSendsPortableRoaringPageSelector()
    {
        var pageOne = CreatePage(0x41);
        var pageThree = CreatePage(0x43);
        var handler = new CapturingHandler(
            CreateTargetedPullResponse(
                Revision,
                databasePages: 6,
                new ManagedReplicaFetchedPage(1, pageOne),
                new ManagedReplicaFetchedPage(3, pageThree)));
        var options = new AhtolaReplicaOptions(
            "unused.db",
            new Uri("https://example.test/cluster"),
            authToken: "token-42")
        {
            HttpPolicy = new AhtolaSyncHttpPolicy(handler),
        };
        var source = new ManagedReplicaPullPageSource(options);

        var batch = await source.FetchPagesAsync(Revision, [1, 3], CancellationToken.None);

        batch.Revision.Should().Be(Revision);
        batch.DatabasePages.Should().Be(6);
        batch.Pages.Select(page => page.PageId).Should().Equal(1UL, 3UL);
        var fields = ReadLengthDelimitedFields(handler.RequestBody!);
        Encoding.UTF8.GetString(fields[2]).Should().Be(Revision);
        fields[5].Should().Equal(
            0x3a, 0x30, 0x00, 0x00, // no-run portable Roaring cookie
            0x01, 0x00, 0x00, 0x00, // one container
            0x00, 0x00, 0x01, 0x00, // key 0, cardinality 2
            0x10, 0x00, 0x00, 0x00, // container offset
            0x01, 0x00, 0x03, 0x00);
    }

    /// <summary>
    /// A query-selected bootstrap leaves an arbitrary scattered set materialized instead of a prefix.
    /// The materializer, its sidecar and the fault path are page-id keyed and strategy-agnostic
    /// (mirroring Turso's <c>database_sync_lazy_storage.rs</c>, which has no notion of Prefix vs
    /// Query), so a scattered initial set must behave exactly like a prefix one: materialized pages
    /// are never refetched and each missing page faults by id.
    /// </summary>
    [Test]
    public void ScatteredInitialSetNeverRefetchesMaterializedPagesAndFaultsMissingOnesById()
    {
        var fileSystem = CreatePartialDatabase(pageCount: 8, materializedPageIds: [5, 0, 3]);
        var expected = CreatePage(0x81);
        var source = new RecordingPageSource(databasePages: 8, pageFactory: _ => expected);

        using var materializing = OpenMaterializing(fileSystem, source, prefetchSegments: false);
        using var file = materializing.OpenFile("replica.db", FileOpenMode.OpenExisting, readOnly: true);
        var page = new byte[PageSize];

        // Already materialized: pages 3 and 5 sit inside otherwise-missing neighbourhoods.
        file.Read(3 * PageSize, page).Should().Be(PageSize);
        file.Read(5 * PageSize, page).Should().Be(PageSize);
        source.CallCount.Should().Be(0);

        // Missing: each fault addresses exactly the missing page id, in a non-contiguous pattern.
        file.Read(4 * PageSize, page).Should().Be(PageSize);
        page.Should().Equal(expected);
        file.Read(7 * PageSize, page).Should().Be(PageSize);
        page.Should().Equal(expected);

        source.CallCount.Should().Be(2);
        source.Requests.Should().SatisfyRespectively(
            first => first.Should().Equal(4UL),
            second => second.Should().Equal(7UL));

        // Re-reading a now-materialized page is served from the durable cache.
        file.Read(4 * PageSize, page).Should().Be(PageSize);
        source.CallCount.Should().Be(2);
    }

    [Test]
    public async Task ConcurrentFaultsOnAScatteredInitialSetShareOneRemoteFetch()
    {
        var fileSystem = CreatePartialDatabase(pageCount: 8, materializedPageIds: [6, 0, 2]);
        var expected = CreatePage(0x82);
        var source = new BlockingPageSource(databasePages: 8, expected);
        using var materializing = OpenMaterializing(fileSystem, source, prefetchSegments: false);

        var reads = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() =>
            {
                using var file = materializing.OpenFile("replica.db", FileOpenMode.OpenExisting, readOnly: true);
                var page = new byte[PageSize];
                file.Read(5 * PageSize, page).Should().Be(PageSize);
                return page;
            }))
            .ToArray();

        await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        source.CallCount.Should().Be(1);
        source.Release();

        var pages = await Task.WhenAll(reads).WaitAsync(TimeSpan.FromSeconds(5));
        pages.Should().AllSatisfy(page => page.Should().Equal(expected));
        source.CallCount.Should().Be(1);
    }

    [Test]
    public void ScatteredMaterializedStateSurvivesReopenWithoutAnotherFetch()
    {
        var fileSystem = CreatePartialDatabase(pageCount: 8, materializedPageIds: [7, 0, 4]);
        var expected = CreatePage(0x83);
        var firstSource = new RecordingPageSource(8, _ => expected);
        using (var materializing = OpenMaterializing(fileSystem, firstSource, prefetchSegments: false))
        using (var file = materializing.OpenFile("replica.db", FileOpenMode.OpenExisting, readOnly: true))
        {
            var page = new byte[PageSize];
            file.Read(2 * PageSize, page).Should().Be(PageSize);
            page.Should().Equal(expected);
        }

        var reopenedSource = new DelegatePageSource((_, _, _) =>
            Task.FromException<ManagedReplicaPageBatch>(
                new InvalidOperationException("A durable cache hit must not contact the remote source.")));
        using var reopened = OpenMaterializing(fileSystem, reopenedSource, prefetchSegments: false);
        using var reopenedFile = reopened.OpenFile("replica.db", FileOpenMode.OpenExisting, readOnly: true);
        var reopenedPage = new byte[PageSize];

        // Both the original scattered set and the page faulted above are still materialized.
        reopenedFile.Read(4 * PageSize, reopenedPage).Should().Be(PageSize);
        reopenedFile.Read(7 * PageSize, reopenedPage).Should().Be(PageSize);
        reopenedFile.Read(2 * PageSize, reopenedPage).Should().Be(PageSize);
        reopenedPage.Should().Equal(expected);
        firstSource.CallCount.Should().Be(1);
        reopenedSource.CallCount.Should().Be(0);
    }

    [Test]
    public void LocalWritesReplaceWholeMissingPagesInAScatteredImage()
    {
        var fileSystem = CreatePartialDatabase(pageCount: 8, materializedPageIds: [0, 6]);
        var expected = CreatePage(0x84);
        var source = new DelegatePageSource((_, _, _) =>
            Task.FromException<ManagedReplicaPageBatch>(
                new InvalidOperationException("A local page replacement must not contact the remote source.")));
        using (var materializing = OpenMaterializing(fileSystem, source, prefetchSegments: false))
        using (var file = materializing.OpenFile("replica.db", FileOpenMode.OpenExisting))
        {
            var partialWrite = () => file.Write(3 * PageSize + 1, expected.AsSpan(1));

            partialWrite.Should().Throw<InvalidDataException>();
            file.Write(3 * PageSize, expected);
            file.FlushToDisk();
        }

        using var reopened = OpenMaterializing(fileSystem, source, prefetchSegments: false);
        using var reopenedFile = reopened.OpenFile("replica.db", FileOpenMode.OpenExisting, readOnly: true);
        var page = new byte[PageSize];

        reopenedFile.Read(3 * PageSize, page).Should().Be(PageSize);
        page.Should().Equal(expected);
        reopened.HasLocalMutations.Should().BeTrue();
        source.CallCount.Should().Be(0);
    }

    /// <summary>
    /// After a query bootstrap the query string is never persisted and never resent: later faults are
    /// plain revision-pinned page-id selectors (tag 5), with no <c>server_query_selector</c> (tag 7).
    /// </summary>
    [Test]
    public async Task TargetedPullOfAScatteredPageSetSendsPageIdsAndNeverTheQueryText()
    {
        var handler = new CapturingHandler(
            CreateTargetedPullResponse(
                Revision,
                databasePages: 12,
                new ManagedReplicaFetchedPage(1, CreatePage(0x41)),
                new ManagedReplicaFetchedPage(4, CreatePage(0x44)),
                new ManagedReplicaFetchedPage(9, CreatePage(0x49))));
        var options = new AhtolaReplicaOptions(
            "unused.db",
            new Uri("https://example.test/cluster"),
            authToken: "token-42")
        {
            PartialBootstrap = AhtolaPartialBootstrapOptions.QueryPages("SELECT 1;"),
            HttpPolicy = new AhtolaSyncHttpPolicy(handler),
        };
        var source = new ManagedReplicaPullPageSource(options);

        var batch = await source.FetchPagesAsync(Revision, [1, 4, 9], CancellationToken.None);

        batch.Revision.Should().Be(Revision);
        batch.Pages.Select(page => page.PageId).Should().Equal(1UL, 4UL, 9UL);
        var fields = ReadLengthDelimitedFields(handler.RequestBody!);
        Encoding.UTF8.GetString(fields[2]).Should().Be(Revision);
        fields.Should().ContainKey(5);
        fields.Should().NotContainKey(7, "the bootstrap query is never persisted or resent for a lazy fault");
    }

    private static InMemoryFileSystem CreatePartialDatabase(
        int pageCount,
        int segmentSize = ManagedReplicaPageMaterializingFileSystem.DefaultSegmentSize,
        IReadOnlyList<ulong>? materializedPageIds = null)
    {
        var fileSystem = new InMemoryFileSystem();
        InitializePartialDatabase(fileSystem, "replica.db", pageCount, segmentSize, materializedPageIds);
        return fileSystem;
    }

    private static void InitializePartialDatabase(
        IFileSystem fileSystem,
        string databasePath,
        int pageCount,
        int segmentSize = ManagedReplicaPageMaterializingFileSystem.DefaultSegmentSize,
        IReadOnlyList<ulong>? materializedPageIds = null)
    {
        var header = SqliteDatabaseHeader.CreateDefault() with
        {
            ChangeCounter = 1,
            DatabaseSizeInPages = checked((uint)pageCount),
            VersionValidFor = 1,
        };
        var firstPage = new byte[PageSize];
        header.WriteTo(firstPage);
        SqliteBtreePageHeader
            .CreateEmpty(
                SqliteBtreePageType.TableLeaf,
                PageSize,
                isFirstPage: true,
                usableSpace: header.UsableSpace)
            .WriteTo(firstPage);

        using (var file = fileSystem.OpenFile(databasePath, FileOpenMode.CreateNew))
        {
            file.SetLength(checked((long)pageCount * PageSize));
            file.Write(0, firstPage);
            file.FlushToDisk();
        }
        using (SqliteWalFile.Create(
                   fileSystem,
                   databasePath + "-wal",
                   SqliteWalHeader.Create(PageSize, salt1: 11, salt2: 13)))
        {
        }

        ManagedReplicaPageMaterializingFileSystem.InitializeState(
            fileSystem,
            databasePath,
            Revision,
            checked((ulong)pageCount),
            PageSize,
            segmentSize,
            materializedPageIds ?? [0]);
    }

    private static ManagedReplicaPageMaterializingFileSystem OpenMaterializing(
        IFileSystem fileSystem,
        IManagedReplicaPageSource source,
        bool prefetchSegments)
        => new(
            fileSystem,
            "replica.db",
            Revision,
            source,
            prefetchSegments);

    private static byte[] CreatePage(byte marker)
        => Enumerable.Repeat(marker, PageSize).ToArray();

    private static byte[] CreateTargetedPullResponse(
        string revision,
        ulong databasePages,
        params ManagedReplicaFetchedPage[] pages)
    {
        var header = new List<byte>();
        WriteLengthDelimitedField(header, 1, Encoding.UTF8.GetBytes(revision));
        WriteVarintField(header, 2, databasePages);
        WriteLengthDelimitedField(header, 3, []);

        var response = new List<byte>();
        WriteDelimitedMessage(response, header);
        foreach (var fetchedPage in pages)
        {
            var page = new List<byte>();
            WriteVarintField(page, 1, fetchedPage.PageId);
            WriteLengthDelimitedField(page, 2, fetchedPage.Data);
            WriteDelimitedMessage(response, page);
        }
        return response.ToArray();
    }

    private static Dictionary<int, byte[]> ReadLengthDelimitedFields(byte[] payload)
    {
        var fields = new Dictionary<int, byte[]>();
        var offset = 0;
        while (offset < payload.Length)
        {
            var key = ReadVarint(payload, ref offset);
            (key & 7).Should().Be(2);
            var length = checked((int)ReadVarint(payload, ref offset));
            fields.Add(
                checked((int)(key >> 3)),
                payload.AsSpan(offset, length).ToArray());
            offset += length;
        }
        return fields;
    }

    private static void WriteDelimitedMessage(List<byte> destination, List<byte> message)
    {
        WriteVarint(destination, checked((ulong)message.Count));
        destination.AddRange(message);
    }

    private static void WriteLengthDelimitedField(
        List<byte> destination,
        int fieldNumber,
        ReadOnlySpan<byte> value)
    {
        WriteVarint(destination, checked((ulong)fieldNumber << 3 | 2));
        WriteVarint(destination, checked((ulong)value.Length));
        destination.AddRange(value.ToArray());
    }

    private static void WriteVarintField(List<byte> destination, int fieldNumber, ulong value)
    {
        WriteVarint(destination, checked((ulong)fieldNumber << 3));
        WriteVarint(destination, value);
    }

    private static void WriteVarint(List<byte> destination, ulong value)
    {
        while (value >= 0x80)
        {
            destination.Add((byte)((value & 0x7f) | 0x80));
            value >>= 7;
        }
        destination.Add((byte)value);
    }

    private static ulong ReadVarint(byte[] source, ref int offset)
    {
        ulong value = 0;
        for (var shift = 0; shift < 64; shift += 7)
        {
            var next = source[offset++];
            value |= (ulong)(next & 0x7f) << shift;
            if ((next & 0x80) == 0)
                return value;
        }
        throw new InvalidDataException("Invalid protobuf varint.");
    }

    public enum InvalidPageResponse
    {
        WrongPage,
        WrongSize,
    }

    private sealed class DelegatePageSource(
        Func<string, IReadOnlyList<ulong>, CancellationToken, Task<ManagedReplicaPageBatch>> fetch)
        : IManagedReplicaPageSource
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public Task<ManagedReplicaPageBatch> FetchPagesAsync(
            string revision,
            IReadOnlyList<ulong> pageIds,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            return fetch(revision, pageIds, cancellationToken);
        }
    }

    private sealed class RecordingPageSource(
        ulong databasePages,
        Func<ulong, byte[]> pageFactory) : IManagedReplicaPageSource
    {
        private readonly object _gate = new();
        private readonly List<ulong[]> _requests = [];
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public IReadOnlyList<ulong[]> Requests
        {
            get
            {
                lock (_gate)
                    return _requests.ToArray();
            }
        }

        public Task<ManagedReplicaPageBatch> FetchPagesAsync(
            string revision,
            IReadOnlyList<ulong> pageIds,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _callCount);
            lock (_gate)
                _requests.Add(pageIds.ToArray());
            return Task.FromResult(new ManagedReplicaPageBatch(
                revision,
                databasePages,
                pageIds.Select(pageId => new ManagedReplicaFetchedPage(pageId, pageFactory(pageId))).ToArray()));
        }
    }

    private sealed class BlockingPageSource(
        ulong databasePages,
        byte[] page) : IManagedReplicaPageSource
    {
        private readonly TaskCompletionSource<bool> _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _callCount;

        public TaskCompletionSource<bool> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int CallCount => Volatile.Read(ref _callCount);

        public void Release() => _release.TrySetResult(true);

        public async Task<ManagedReplicaPageBatch> FetchPagesAsync(
            string revision,
            IReadOnlyList<ulong> pageIds,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            Started.TrySetResult(true);
            await _release.Task.WaitAsync(cancellationToken);
            return new ManagedReplicaPageBatch(
                revision,
                databasePages,
                pageIds.Select(pageId => new ManagedReplicaFetchedPage(pageId, page)).ToArray());
        }
    }

    private sealed class RetryPageSource(
        ulong databasePages,
        byte[] page) : IManagedReplicaPageSource
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public Task<ManagedReplicaPageBatch> FetchPagesAsync(
            string revision,
            IReadOnlyList<ulong> pageIds,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref _callCount) == 1)
                return Task.FromException<ManagedReplicaPageBatch>(new HttpRequestException("offline"));
            return Task.FromResult(new ManagedReplicaPageBatch(
                revision,
                databasePages,
                pageIds.Select(pageId => new ManagedReplicaFetchedPage(pageId, page)).ToArray()));
        }
    }

    private sealed class BlockingDisposeFileSystem(
        IFileSystem inner,
        string databasePath) : IFileSystem
    {
        private readonly ManualResetEventSlim _release = new(initialState: false);
        private int _armed;
        private int _disposeBlocked;
        private int _openedWhileDisposeWasBlocked;

        public TaskCompletionSource<bool> DisposeStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool OpenedWhileDisposeWasBlocked =>
            Volatile.Read(ref _openedWhileDisposeWasBlocked) != 0;

        public void Arm() => Volatile.Write(ref _armed, 1);

        public void Release() => _release.Set();

        public bool FileExists(string path) => inner.FileExists(path);

        public IFile OpenFile(string path, FileOpenMode mode, bool readOnly = false)
        {
            if (Volatile.Read(ref _disposeBlocked) != 0)
                Volatile.Write(ref _openedWhileDisposeWasBlocked, 1);
            var file = inner.OpenFile(path, mode, readOnly);
            return string.Equals(path, databasePath, StringComparison.Ordinal)
                ? new BlockingDisposeFile(this, file)
                : file;
        }

        public void DeleteFile(string path) => inner.DeleteFile(path);

        public FileWriteStamp? GetWriteStamp(string path) => inner.GetWriteStamp(path);

        private sealed class BlockingDisposeFile(
            BlockingDisposeFileSystem owner,
            IFile innerFile) : IFile
        {
            public long Length => innerFile.Length;

            public bool IsReadOnly => innerFile.IsReadOnly;

            public int Read(long position, Span<byte> destination) =>
                innerFile.Read(position, destination);

            public void Write(long position, ReadOnlySpan<byte> source) =>
                innerFile.Write(position, source);

            public void SetLength(long length) => innerFile.SetLength(length);

            public void FlushToDisk() => innerFile.FlushToDisk();

            public void Dispose()
            {
                if (Volatile.Read(ref owner._armed) != 0
                    && Interlocked.CompareExchange(ref owner._disposeBlocked, 1, 0) == 0)
                {
                    owner.DisposeStarted.TrySetResult(true);
                    owner._release.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                    Volatile.Write(ref owner._disposeBlocked, 0);
                }

                innerFile.Dispose();
            }
        }
    }

    private sealed class StateTailFailureFileSystem(
        IFileSystem inner,
        string statePath) : IFileSystem
    {
        private int _armed;
        private int _writeFailed;
        private int _rollbackFailed;

        public void Arm() => Volatile.Write(ref _armed, 1);

        public bool FileExists(string path) => inner.FileExists(path);

        public IFile OpenFile(string path, FileOpenMode mode, bool readOnly = false)
        {
            var file = inner.OpenFile(path, mode, readOnly);
            return string.Equals(path, statePath, StringComparison.Ordinal)
                ? new StateTailFailureFile(this, file)
                : file;
        }

        public void DeleteFile(string path) => inner.DeleteFile(path);

        public FileWriteStamp? GetWriteStamp(string path) => inner.GetWriteStamp(path);

        private sealed class StateTailFailureFile(
            StateTailFailureFileSystem owner,
            IFile innerFile) : IFile
        {
            public long Length => innerFile.Length;

            public bool IsReadOnly => innerFile.IsReadOnly;

            public int Read(long position, Span<byte> destination) =>
                innerFile.Read(position, destination);

            public void Write(long position, ReadOnlySpan<byte> source)
            {
                if (Volatile.Read(ref owner._armed) != 0
                    && Interlocked.CompareExchange(ref owner._writeFailed, 1, 0) == 0)
                {
                    innerFile.Write(position, source[..Math.Min(8, source.Length)]);
                    throw new IOException("Injected partial page-state write.");
                }

                innerFile.Write(position, source);
            }

            public void SetLength(long length)
            {
                if (Volatile.Read(ref owner._armed) != 0
                    && Volatile.Read(ref owner._writeFailed) != 0
                    && Interlocked.CompareExchange(ref owner._rollbackFailed, 1, 0) == 0)
                {
                    throw new IOException("Injected page-state rollback failure.");
                }

                innerFile.SetLength(length);
            }

            public void FlushToDisk() => innerFile.FlushToDisk();

            public void Dispose() => innerFile.Dispose();
        }
    }

    private sealed class CapturingHandler(byte[] responsePayload) : HttpMessageHandler
    {
        public byte[]? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestBody = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(responsePayload),
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/protobuf");
            return response;
        }
    }
}
