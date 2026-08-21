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
        source.CallCount.Should().Be(0);
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

    private static InMemoryFileSystem CreatePartialDatabase(
        int pageCount,
        int segmentSize = ManagedReplicaPageMaterializingFileSystem.DefaultSegmentSize)
    {
        var fileSystem = new InMemoryFileSystem();
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

        using (var file = fileSystem.OpenFile("replica.db", FileOpenMode.CreateNew))
        {
            file.SetLength(checked((long)pageCount * PageSize));
            file.Write(0, firstPage);
            file.FlushToDisk();
        }
        using (SqliteWalFile.Create(
                   fileSystem,
                   "replica.db-wal",
                   SqliteWalHeader.Create(PageSize, salt1: 11, salt2: 13)))
        {
        }

        ManagedReplicaPageMaterializingFileSystem.InitializeState(
            fileSystem,
            "replica.db",
            Revision,
            checked((ulong)pageCount),
            PageSize,
            segmentSize,
            [0]);
        return fileSystem;
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
