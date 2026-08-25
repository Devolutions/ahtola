using AwesomeAssertions;
using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Ahtola.Tests;

/// <summary>
/// Query-selected partial bootstrap (<see cref="AhtolaPartialBootstrapKind.Query"/>).
/// </summary>
/// <remarks>
/// <para>
/// Every test here drives a hand-written fake server. Turso's vendored local dev server
/// (<c>turso-src/cli/sync_server.rs</c>) deliberately does not implement query selection -- its own
/// spec instructs implementers "You MUST ignore <c>server_query_selector</c> field"
/// (<c>turso-src/cli/sync_server.mdx:105</c>) and it only decodes <c>server_pages_selector</c>. No
/// reference server in this tree can execute a query bootstrap end to end, so a fake is the only way
/// to exercise the protocol.
/// </para>
/// <para>
/// Consequently, any assertion of the form "this query selects these pages" is asserting
/// <em>fake-server</em> behavior, not a Turso Cloud compatibility guarantee. The managed client is a
/// pure pass-through: it never parses, interprets, or re-derives the query, and it cannot predict or
/// validate the page set the server chooses. What these tests do pin down is the client half of the
/// contract: exact wire encoding, selector exclusivity, single round trip, acceptance of arbitrary
/// unordered/non-contiguous page sets, fail-closed validation, and durable sparse-image behavior.
/// </para>
/// </remarks>
public sealed partial class ManagedEmbeddedReplicaConnectionTests
{
    private const string BootstrapQuery = "SELECT value FROM bootstrap_marker;";

    /// <summary>
    /// Turso's <c>PullUpdatesReqProtoBody</c> (<c>turso-src/sync/engine/src/server_proto.rs:39-41</c>)
    /// carries the bootstrap query in tag 7 as a plain UTF-8 string. It must be the only selector on
    /// the request: Turso's client passes an empty first page selector whenever a query is present
    /// (<c>database_sync_operations.rs::bootstrap_db_file_v1</c>), and combining the two would leave
    /// the selected page set ambiguous.
    /// </summary>
    [Test]
    public void QueryBootstrapSendsFieldSevenAloneInExactlyOneRoundTrip()
    {
        var path = NewReplicaPath("managed-replica-query-wire");
        var databaseImage = CreateMultiPageDatabaseImage(path + ".source");
        var pageCount = databaseImage.Length / 4096;
        pageCount.Should().BeGreaterThan(3);
        var handler = new QueryBootstrapPullHandler(
            "revision-query",
            databaseImage,
            bootstrapPages: [0u, checked((uint)(pageCount - 1))]);
        var options = CreateOptions(
            path,
            handler,
            partialBootstrap: AhtolaPartialBootstrapOptions.QueryPages(BootstrapQuery));

        try
        {
            using var connection = AhtolaConnection.CreateReplica(options);
            connection.Open();
            ReadBootstrapMarker(connection).Should().Be(42);

            handler.BootstrapCallCount.Should().Be(
                1,
                "a query bootstrap is never chunked; the server streams the whole selected set once");
            handler.BootstrapQueries.Should().Equal(BootstrapQuery);
            handler.BootstrapRequestsCarryingAPageSelector.Should().Be(0);

            // Byte-exact tag-7 framing: key = (7 << 3) | 2, then a varint length, then UTF-8.
            var request = handler.Requests[0];
            var queryBytes = Encoding.UTF8.GetBytes(BootstrapQuery);
            var expected = new List<byte> { (7 << 3) | 2 };
            WriteVarint(expected, checked((ulong)queryBytes.Length));
            expected.AddRange(queryBytes);
            IndexOfSubsequence(request, expected.ToArray()).Should().BeGreaterThanOrEqualTo(0);

            // Raw page encoding is still negotiated explicitly, and no MVCC stream kind is requested.
            TryReadLengthDelimitedField(request, 5, out _).Should().BeFalse();
            ReadVarintField(request, 1).Should().Be(0ul);
            ReadVarintField(request, 8).Should().BeNull();
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    /// <summary>
    /// The server picks the page set. It may arrive unsorted and non-contiguous, and <c>db_size</c>
    /// still describes the whole database rather than the streamed subset. The replica must install a
    /// sparse image, publish an integrity-protected page-state sidecar, and fault the rest lazily.
    /// </summary>
    [Test]
    public void QueryBootstrapAcceptsUnsortedScatteredPagesAndFaultsTheRestLazily()
    {
        var path = NewReplicaPath("managed-replica-query-scattered");
        var databaseImage = CreateMultiPageDatabaseImage(path + ".source");
        var pageCount = databaseImage.Length / 4096;
        pageCount.Should().BeGreaterThan(3);
        var lastPage = checked((uint)(pageCount - 1));
        var handler = new QueryBootstrapPullHandler(
            "revision-query",
            databaseImage,
            bootstrapPages: [lastPage, 2u, 0u]);
        var options = CreateOptions(
            path,
            handler,
            partialBootstrap: AhtolaPartialBootstrapOptions.QueryPages(BootstrapQuery));

        try
        {
            using (var connection = AhtolaConnection.CreateReplica(options))
            {
                connection.Open();
                ReadBootstrapMarker(connection).Should().Be(42);
            }

            handler.BootstrapCallCount.Should().Be(1);
            File.Exists(path + ManagedReplicaPageMaterializingFileSystem.StateSuffix).Should().BeTrue();

            // Missing pages are the arbitrary complement of the scattered bootstrap set, and each
            // targeted pull addresses page ids against the pinned bootstrap revision. The query text
            // is never persisted and never resent.
            handler.TargetedRequests.Should().NotBeEmpty();
            handler.TargetedRequests.Should().AllSatisfy(static targeted =>
            {
                targeted.Revision.Should().Be("revision-query");
                targeted.Query.Should().BeNull();
            });
            handler.TargetedRequests
                .SelectMany(static targeted => targeted.Pages)
                .Should()
                .NotContain([lastPage, 2u, 0u]);
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    /// <summary>
    /// A query that selects no rows is legal as long as the structural pages the reader needs are
    /// present. Page 1 (the SQLite header page, id 0 on the wire) is mandatory; everything else is
    /// faulted on demand.
    /// </summary>
    [Test]
    public void QuerySelectingNoRowsStillOpensWhenTheHeaderPageIsPresent()
    {
        var path = NewReplicaPath("managed-replica-query-empty");
        var databaseImage = CreateMultiPageDatabaseImage(path + ".source");
        var handler = new QueryBootstrapPullHandler(
            "revision-query",
            databaseImage,
            bootstrapPages: [0u]);
        var options = CreateOptions(
            path,
            handler,
            partialBootstrap: AhtolaPartialBootstrapOptions.QueryPages("SELECT 1 WHERE 0;"));

        try
        {
            using var connection = AhtolaConnection.CreateReplica(options);
            connection.Open();
            ReadBootstrapMarker(connection).Should().Be(42);
            handler.BootstrapCallCount.Should().Be(1);
            handler.BootstrapQueries.Should().Equal("SELECT 1 WHERE 0;");
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    /// <summary>
    /// When the server's query happens to select every page, the bootstrap image is complete: no
    /// page-state sidecar is published and no lazy fault ever runs. Completeness is distinct-page
    /// coverage of <c>db_size</c>, not a prefix cutoff.
    /// </summary>
    [Test]
    public void QueryCoveringEveryPageProducesAFullImageWithNoSidecarOrFaults()
    {
        var path = NewReplicaPath("managed-replica-query-full");
        var databaseImage = CreateMultiPageDatabaseImage(path + ".source");
        var pageCount = databaseImage.Length / 4096;

        // Deliberately scrambled: a complete set is still an unordered set on the wire.
        var pages = Enumerable.Range(0, pageCount).Select(static page => checked((uint)page)).ToArray();
        Array.Reverse(pages);
        var handler = new QueryBootstrapPullHandler("revision-query", databaseImage, pages);
        var options = CreateOptions(
            path,
            handler,
            partialBootstrap: AhtolaPartialBootstrapOptions.QueryPages(BootstrapQuery));

        try
        {
            using var connection = AhtolaConnection.CreateReplica(options);
            connection.Open();
            ReadBootstrapMarker(connection).Should().Be(42);

            handler.BootstrapCallCount.Should().Be(1);
            handler.TargetedRequests.Should().BeEmpty();
            File.Exists(path + ManagedReplicaPageMaterializingFileSystem.StateSuffix).Should().BeFalse();
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    /// <summary>
    /// Managed replicas stay strict fail-closed for protocol violations: they never fall back to a
    /// full bootstrap and never publish a partially-installed replica.
    /// </summary>
    [TestCase(QueryBootstrapProtocolViolation.DuplicatePage)]
    [TestCase(QueryBootstrapProtocolViolation.OutOfRangePage)]
    [TestCase(QueryBootstrapProtocolViolation.MissingHeaderPage)]
    [TestCase(QueryBootstrapProtocolViolation.NoPagesAtAll)]
    [TestCase(QueryBootstrapProtocolViolation.ShortPage)]
    [TestCase(QueryBootstrapProtocolViolation.LogicalStreamKind)]
    [TestCase(QueryBootstrapProtocolViolation.Zstd)]
    public void QueryBootstrapRejectsProtocolViolationsWithoutPublishingAnything(
        QueryBootstrapProtocolViolation violation)
    {
        var path = NewReplicaPath($"managed-replica-query-invalid-{violation}");
        var databaseImage = CreateMultiPageDatabaseImage(path + ".source");
        var pageCount = checked((uint)(databaseImage.Length / 4096));
        var response = violation switch
        {
            QueryBootstrapProtocolViolation.DuplicatePage =>
                CreatePageSubsetPullResponse("revision-query", databaseImage, [0u, 2u, 2u]),
            QueryBootstrapProtocolViolation.OutOfRangePage =>
                CreatePageSubsetPullResponse(
                    "revision-query",
                    databaseImage,
                    [0u, pageCount],
                    allowOutOfRangePageIds: true),
            QueryBootstrapProtocolViolation.MissingHeaderPage =>
                CreatePageSubsetPullResponse("revision-query", databaseImage, [1u, 2u]),
            QueryBootstrapProtocolViolation.NoPagesAtAll =>
                CreatePageSubsetPullResponse("revision-query", databaseImage, []),
            QueryBootstrapProtocolViolation.ShortPage =>
                CreateShortPageSubsetResponse("revision-query", databaseImage),
            QueryBootstrapProtocolViolation.LogicalStreamKind =>
                CreateLogicalPullResponse("revision-query", body: [], declaredPages: pageCount),
            QueryBootstrapProtocolViolation.Zstd =>
                CreatePullResponse("revision-query", databaseImage, zstd: true),
            _ => throw new ArgumentOutOfRangeException(nameof(violation)),
        };

        var handler = new PullUpdatesHandler(response);
        var options = CreateOptions(
            path,
            handler,
            partialBootstrap: AhtolaPartialBootstrapOptions.QueryPages(BootstrapQuery));

        try
        {
            using var connection = AhtolaConnection.CreateReplica(options);
            Assert.Throws<InvalidDataException>(() => connection.Open());

            File.Exists(path).Should().BeFalse();
            File.Exists(path + ManagedReplicaBootstrapper.MetadataSuffix).Should().BeFalse();
            File.Exists(path + ManagedReplicaPageMaterializingFileSystem.StateSuffix).Should().BeFalse();
            Directory
                .GetFiles(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.bootstrap-*.tmp")
                .Should()
                .BeEmpty();
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    /// <summary>
    /// A sparse query image survives reopen: the durable sidecar replays the exact scattered
    /// materialized set, so no page is refetched. Deleting the sidecar must fail closed into a clean
    /// re-bootstrap rather than exposing uninitialized pages -- and the re-bootstrap sends the query
    /// again, because that is a fresh bootstrap, not a lazy fault.
    /// </summary>
    [Test]
    public void SparseQueryImageSurvivesReopenAndRebootstrapsWhenItsSidecarIsLost()
    {
        var path = NewReplicaPath("managed-replica-query-restart");
        var databaseImage = CreateMultiPageDatabaseImage(path + ".source");
        var handler = new QueryBootstrapPullHandler(
            "revision-query",
            databaseImage,
            bootstrapPages: [2u, 0u]);
        var options = CreateOptions(
            path,
            handler,
            partialBootstrap: AhtolaPartialBootstrapOptions.QueryPages(BootstrapQuery));

        try
        {
            using (var connection = AhtolaConnection.CreateReplica(options))
            {
                connection.Open();
                ReadBootstrapMarker(connection).Should().Be(42);
            }

            File.Exists(path + ManagedReplicaPageMaterializingFileSystem.StateSuffix).Should().BeTrue();
            var callsAfterFirstOpen = handler.TotalPullCallCount;

            using (var reopened = AhtolaConnection.CreateReplica(options))
            {
                reopened.Open();
                ReadBootstrapMarker(reopened).Should().Be(42);
            }

            handler.BootstrapCallCount.Should().Be(1, "reopen must reuse the durable page state");
            handler.TotalPullCallCount.Should().Be(callsAfterFirstOpen);

            File.Delete(path + ManagedReplicaPageMaterializingFileSystem.StateSuffix);
            using (var recovered = AhtolaConnection.CreateReplica(options))
            {
                recovered.Open();
                ReadBootstrapMarker(recovered).Should().Be(42);
            }

            handler.BootstrapCallCount.Should().Be(2);
            handler.BootstrapQueries.Should().Equal(BootstrapQuery, BootstrapQuery);
            File.Exists(path + ManagedReplicaPageMaterializingFileSystem.StateSuffix).Should().BeTrue();
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    /// <summary>
    /// A corrupt page-state sidecar must never be trusted into serving sparse bytes.
    /// </summary>
    [Test]
    public void CorruptQueryPageStateSidecarFailsClosedOnReopen()
    {
        var path = NewReplicaPath("managed-replica-query-corrupt-sidecar");
        var databaseImage = CreateMultiPageDatabaseImage(path + ".source");
        var handler = new QueryBootstrapPullHandler(
            "revision-query",
            databaseImage,
            bootstrapPages: [2u, 0u]);
        var options = CreateOptions(
            path,
            handler,
            partialBootstrap: AhtolaPartialBootstrapOptions.QueryPages(BootstrapQuery));

        try
        {
            using (var connection = AhtolaConnection.CreateReplica(options))
            {
                connection.Open();
                ReadBootstrapMarker(connection).Should().Be(42);
            }

            var statePath = path + ManagedReplicaPageMaterializingFileSystem.StateSuffix;
            var state = File.ReadAllBytes(statePath);
            state.Length.Should().BeGreaterThan(16);
            state[^1] ^= 0xFF; // flip a byte inside the integrity-hashed tail
            File.WriteAllBytes(statePath, state);

            using var reopened = AhtolaConnection.CreateReplica(options);
            Assert.Throws<InvalidDataException>(() => reopened.Open());
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    /// <summary>
    /// Concurrent readers that fault the same missing page share one remote fetch. This is the
    /// existing coalescing mechanism; a query-seeded sparse image must reuse it, not fork it.
    /// </summary>
    [Test]
    public async Task ConcurrentReadersOfAQuerySeededImageCoalesceTheirMissingPageFetches()
    {
        var path = NewReplicaPath("managed-replica-query-coalesce");
        var databaseImage = CreateMultiPageDatabaseImage(path + ".source");
        var handler = new QueryBootstrapPullHandler(
            "revision-query",
            databaseImage,
            bootstrapPages: [0u],
            gateTargetedPulls: true);
        var options = CreateOptions(
            path,
            handler,
            partialBootstrap: AhtolaPartialBootstrapOptions.QueryPages(BootstrapQuery));

        try
        {
            using var first = AhtolaConnection.CreateReplica(options);
            first.Open();
            using var second = AhtolaConnection.CreateReplica(options);
            second.Open();

            var readers = new[]
            {
                Task.Run(() => ReadBootstrapMarker(first)),
                Task.Run(() => ReadBootstrapMarker(second)),
            };
            (await Task.WhenAll(readers)).Should().AllBeEquivalentTo(42L);

            handler.MaximumConcurrentTargetedPulls.Should().Be(
                1,
                "the materializer serializes and coalesces faults for one replica path");
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    /// <summary>
    /// Local writes into a still-missing page replace the whole page and persist that decision, so
    /// the write is never overwritten by a later remote fetch. Tracked changes are pushed before the
    /// pinned image is completed by ordinary revision-advancing sync.
    /// </summary>
    [Test]
    public async Task LocalWritesToAQuerySeededSparseImageArePushedBeforeImageCompletion()
    {
        var path = NewReplicaPath("managed-replica-query-local-writes");
        var databaseImage = CreateMultiPageDatabaseImage(path + ".source");
        var handler = new QueryBootstrapPullHandler(
            "revision-query",
            databaseImage,
            bootstrapPages: [0u]);
        var options = CreateOptions(
            path,
            handler,
            partialBootstrap: AhtolaPartialBootstrapOptions.QueryPages(BootstrapQuery));

        try
        {
            using var connection = AhtolaConnection.CreateReplica(options);
            connection.Open();
            connection.ExecuteNonQuery("UPDATE bootstrap_marker SET value = 84;").Should().Be(1);

            var result = await connection.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None);

            result.Outcome.Should().Be(AhtolaSyncOutcome.UpToDate);
            result.Statistics.CdcOperations.Should().Be(1);
            handler.PushCallCount.Should().Be(1);
            ReadBootstrapMarker(connection).Should().Be(84);
            File.Exists(path + ManagedReplicaPageMaterializingFileSystem.StateSuffix).Should().BeFalse();
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    /// <summary>
    /// A push conflict on a sparse replica must behave identically whether the image was seeded by a
    /// prefix or by a server-side query: conflict handling is strategy-agnostic and query bootstrap
    /// must not fork it. The pending local change stays journalled and the pinned sparse image stays
    /// on disk -- a conflict never silently completes or discards it, and never advances the revision.
    /// </summary>
    /// <remarks>
    /// This asserts parity rather than a specific exception type on purpose. Today a conflict during
    /// a sparse-replica sync is masked by the host-reopen failure raised from the publication unit's
    /// <c>finally</c> block (<c>ManagedReplicaSyncRegistry.Entry.PublishAsync</c>), so the caller sees
    /// <see cref="InvalidOperationException"/> rather than <see cref="AhtolaReplicaConflictException"/>.
    /// That is pre-existing behavior shared by both partial-bootstrap kinds and is out of scope here;
    /// pinning parity keeps query bootstrap from diverging from it in either direction.
    /// </remarks>
    [Test]
    public void PushConflictBehavesIdenticallyForPrefixAndQuerySeededSparseImages()
    {
        var prefix = RunSparseReplicaPushConflict(query: false);
        var query = RunSparseReplicaPushConflict(query: true);

        query.ExceptionType.Should().Be(prefix.ExceptionType);
        query.PushCallCount.Should().Be(prefix.PushCallCount).And.Be(1);
        query.SidecarPresent.Should().Be(prefix.SidecarPresent).And.BeTrue();
        query.JournalPresent.Should().Be(prefix.JournalPresent).And.BeTrue();
        query.MetadataRevision.Should().Be(prefix.MetadataRevision);

        // The query is only ever sent by the bootstrap itself, never by conflict handling.
        query.BootstrapCallCount.Should().Be(1);
        prefix.BootstrapCallCount.Should().Be(0);
    }

    private static SparseConflictOutcome RunSparseReplicaPushConflict(bool query)
    {
        var path = NewReplicaPath($"managed-replica-sparse-conflict-{query}");
        var databaseImage = CreateMultiPageJournalDatabaseImage(path + ".source");
        var handler = new QueryBootstrapPullHandler(
            "revision-query",
            databaseImage,
            bootstrapPages: [0u],
            pushResponse: static () => ReplicaPushHandler.BatchErrorResponse(
                5,
                2,
                "conflicting local change",
                "SQLITE_CONSTRAINT"));
        var options = CreateOptions(
            path,
            handler,
            partialBootstrap: query
                ? AhtolaPartialBootstrapOptions.QueryPages(BootstrapQuery)
                : AhtolaPartialBootstrapOptions.Prefix(4096));

        try
        {
            Type exceptionType;
            using (var connection = AhtolaConnection.CreateReplica(options))
            {
                connection.Open();
                connection.ExecuteNonQuery("INSERT INTO journal_events VALUES (10);");
                exceptionType = Assert.CatchAsync(
                    () => connection.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None))!.GetType();
            }

            var metadataPath = path + ManagedReplicaBootstrapper.MetadataSuffix;
            return new SparseConflictOutcome(
                exceptionType,
                handler.PushCallCount,
                handler.BootstrapCallCount,
                File.Exists(path + ManagedReplicaPageMaterializingFileSystem.StateSuffix),
                File.Exists(path + ManagedReplicaChangeJournal.Suffix),
                File.Exists(metadataPath) ? File.ReadAllText(metadataPath).Contains("revision-query") : false);
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    private readonly record struct SparseConflictOutcome(
        Type ExceptionType,
        int PushCallCount,
        int BootstrapCallCount,
        bool SidecarPresent,
        bool JournalPresent,
        bool MetadataRevision);

    /// <summary>
    /// An MVCC-logical remote still ships the bootstrap as a raw page stream; only the incremental
    /// pulls that follow are logical. A fresh query bootstrap must therefore complete its mandatory
    /// logical catch-up before the connection opens, unchanged by how the page set was selected.
    /// </summary>
    [Test]
    public void QueryBootstrapOfAnMvccLogicalRemoteStillCatchesUpBeforeOpening()
    {
        var path = NewReplicaPath("managed-replica-query-mvcc");
        var databaseImage = CreateMultiPageDatabaseImage(path + ".source");
        var handler = new QueryBootstrapPullHandler(
            "revision-query",
            databaseImage,
            bootstrapPages: [2u, 0u],
            protocol: 2);
        var options = CreateOptions(
            path,
            handler,
            partialBootstrap: AhtolaPartialBootstrapOptions.QueryPages(BootstrapQuery));

        try
        {
            using var connection = AhtolaConnection.CreateReplica(options);
            connection.Open();
            ReadBootstrapMarker(connection).Should().Be(42);

            handler.BootstrapCallCount.Should().Be(1);
            handler.LogicalCatchUpCallCount.Should().Be(
                1,
                "a fresh MVCC bootstrap must be followed by exactly one catch-up pull");
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    /// <summary>
    /// Publication ordering is the shared bootstrap sequence, not a query-specific one: the safety
    /// state (metadata + page-state sidecar) becomes durable strictly before the sparse database, and
    /// an interruption at any boundary rolls the whole thing back for a clean retry.
    /// </summary>
    [TestCase(BootstrapStagedDatabaseBoundary)]
    [TestCase(ReplicaApplyLockAcquiredBoundary)]
    [TestCase(BootstrapSafetyStatePublishedBoundary)]
    [TestCase(BootstrapDatabasePublishedBoundary)]
    public void InterruptedQueryBootstrapPublishesNothingAtEveryDurableBoundary(int boundaryValue)
    {
        var boundary = (ManagedReplicaDurableBoundary)boundaryValue;
        var path = NewReplicaPath($"managed-replica-query-boundary-{boundary}");
        var databaseImage = CreateMultiPageDatabaseImage(path + ".source");
        var handler = new QueryBootstrapPullHandler(
            "revision-query",
            databaseImage,
            bootstrapPages: [2u, 0u]);
        var options = CreateOptions(
            path,
            handler,
            partialBootstrap: AhtolaPartialBootstrapOptions.QueryPages(BootstrapQuery));
        var observedSafeOrdering = boundary != ManagedReplicaDurableBoundary.BootstrapSafetyStatePublished;

        try
        {
            using (ManagedReplicaFaultInjection.Push(point =>
                   {
                       if (point != boundary)
                           return;
                       if (point == ManagedReplicaDurableBoundary.BootstrapSafetyStatePublished)
                       {
                           observedSafeOrdering = !File.Exists(path)
                               && File.Exists(path + ManagedReplicaBootstrapper.MetadataSuffix)
                               && File.Exists(path + ManagedReplicaPageMaterializingFileSystem.StateSuffix);
                       }

                       throw new InvalidOperationException("Injected query bootstrap interruption.");
                   }))
            {
                Assert.ThrowsAsync<InvalidOperationException>(
                    () => ManagedReplicaBootstrapper.BootstrapAsync(options, CancellationToken.None));
            }

            observedSafeOrdering.Should().BeTrue();
            File.Exists(path).Should().BeFalse();
            File.Exists(path + ManagedReplicaBootstrapper.MetadataSuffix).Should().BeFalse();
            File.Exists(path + ManagedReplicaPageMaterializingFileSystem.StateSuffix).Should().BeFalse();

            // A clean retry still works and still sends the query exactly once more.
            var bootstrapsBeforeRetry = handler.BootstrapCallCount;
            using var retried = AhtolaConnection.CreateReplica(options);
            retried.Open();
            ReadBootstrapMarker(retried).Should().Be(42);
            handler.BootstrapCallCount.Should().Be(bootstrapsBeforeRetry + 1);
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    /// <summary>
    /// Cancelling a query bootstrap must leave no replica files behind.
    /// </summary>
    [Test]
    public void CancelledQueryBootstrapPublishesNothing()
    {
        var path = NewReplicaPath("managed-replica-query-cancelled");
        var databaseImage = CreateMultiPageDatabaseImage(path + ".source");
        var handler = new QueryBootstrapPullHandler(
            "revision-query",
            databaseImage,
            bootstrapPages: [2u, 0u]);
        var options = CreateOptions(
            path,
            handler,
            partialBootstrap: AhtolaPartialBootstrapOptions.QueryPages(BootstrapQuery));

        try
        {
            using var cancellation = new CancellationTokenSource();
            using (ManagedReplicaFaultInjection.Push(point =>
                   {
                       if (point == ManagedReplicaDurableBoundary.BootstrapStagedDatabase)
                           cancellation.Cancel();
                   }))
            {
                Assert.CatchAsync<OperationCanceledException>(
                    () => ManagedReplicaBootstrapper.BootstrapAsync(options, cancellation.Token));
            }

            File.Exists(path).Should().BeFalse();
            File.Exists(path + ManagedReplicaBootstrapper.MetadataSuffix).Should().BeFalse();
            File.Exists(path + ManagedReplicaPageMaterializingFileSystem.StateSuffix).Should().BeFalse();
            Directory
                .GetFiles(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.bootstrap-*.tmp")
                .Should()
                .BeEmpty();
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    /// <summary>
    /// Segment size and prefetch remain valid, kind-independent knobs. Prefetch is an optimization
    /// only: it may widen a fetch to segment boundaries but can never turn a failed fetch into
    /// zero-filled data, and it never expands past the database's page count. With a genuinely
    /// scattered query set most segments are only partially materialized, so segment-granular
    /// early-exit simply degrades toward per-page faults.
    /// </summary>
    [TestCase(false)]
    [TestCase(true)]
    public void QueryBootstrapHonoursSegmentPrefetchWithinTheDatabaseBounds(bool prefetch)
    {
        var path = NewReplicaPath($"managed-replica-query-prefetch-{prefetch}");
        var databaseImage = CreateMultiPageDatabaseImage(path + ".source");
        var pageCount = checked((uint)(databaseImage.Length / 4096));
        var handler = new QueryBootstrapPullHandler(
            "revision-query",
            databaseImage,
            bootstrapPages: [0u]);
        var options = CreateOptions(
            path,
            handler,
            partialBootstrap: AhtolaPartialBootstrapOptions.QueryPages(
                BootstrapQuery,
                segmentSize: 2 * 4096,
                prefetch: prefetch));

        try
        {
            using var connection = AhtolaConnection.CreateReplica(options);
            connection.Open();
            ReadBootstrapMarker(connection).Should().Be(42);

            handler.TargetedRequests.Should().NotBeEmpty();
            handler.TargetedRequests
                .SelectMany(static targeted => targeted.Pages)
                .Should()
                .OnlyContain(page => page < pageCount);
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    /// <summary>
    /// Ordinary revision-advancing sync completes the pinned image first and only then adopts the new
    /// revision, so a scattered query image never straddles two server revisions.
    /// </summary>
    [Test]
    public async Task RevisionAdvancingSyncCompletesTheQueryImageBeforeAdoptingTheNewRevision()
    {
        var path = NewReplicaPath("managed-replica-query-revision");
        var databaseImage = CreateMultiPageDatabaseImage(path + ".source");
        var handler = new QueryBootstrapPullHandler(
            "revision-query",
            databaseImage,
            bootstrapPages: [2u, 0u]);
        var options = CreateOptions(
            path,
            handler,
            partialBootstrap: AhtolaPartialBootstrapOptions.QueryPages(BootstrapQuery));

        try
        {
            using var connection = AhtolaConnection.CreateReplica(options);
            connection.Open();
            File.Exists(path + ManagedReplicaPageMaterializingFileSystem.StateSuffix).Should().BeTrue();

            var result = await connection.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None);

            result.Outcome.Should().Be(AhtolaSyncOutcome.UpToDate);
            File.Exists(path + ManagedReplicaPageMaterializingFileSystem.StateSuffix).Should().BeFalse();
            ReadBootstrapMarker(connection).Should().Be(42);

            // Every completion fetch stayed pinned to the bootstrap revision and used page ids only.
            handler.TargetedRequests.Should().AllSatisfy(static targeted =>
            {
                targeted.Revision.Should().Be("revision-query");
                targeted.Query.Should().BeNull();
            });
            handler.BootstrapCallCount.Should().Be(1);
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    private static byte[] CreateMultiPageDatabaseImage(string path)
    {
        try
        {
            CreateInitializedDatabase(path);
            using (var connection = new AhtolaConnection($"Data Source={path};Local Provider=Managed"))
            {
                connection.Open();
                connection.ExecuteNonQuery("CREATE TABLE bootstrap_payload(value BLOB NOT NULL);");
                connection.ExecuteNonQuery("INSERT INTO bootstrap_payload VALUES (zeroblob(12000));");
            }

            return File.ReadAllBytes(path);
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    private static byte[] CreateMultiPageJournalDatabaseImage(string path)
    {
        try
        {
            CreateJournalDatabase(path);
            using (var connection = new AhtolaConnection($"Data Source={path};Local Provider=Managed"))
            {
                connection.Open();
                connection.ExecuteNonQuery("CREATE TABLE bootstrap_payload(value BLOB NOT NULL);");
                connection.ExecuteNonQuery("INSERT INTO bootstrap_payload VALUES (zeroblob(12000));");
            }

            return File.ReadAllBytes(path);
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    /// <summary>
    /// A response whose page payload is not exactly 4 KiB. Query bootstrap must reject it exactly
    /// like every other page stream.
    /// </summary>
    private static byte[] CreateShortPageSubsetResponse(string revision, byte[] databaseImage)
    {
        var header = new List<byte>();
        WriteLengthDelimitedField(header, 1, Encoding.UTF8.GetBytes(revision));
        WriteVarintField(header, 2, checked((ulong)(databaseImage.Length / 4096)));
        WriteLengthDelimitedField(header, 3, []);
        WriteVarintField(header, 5, 0);
        WriteVarintField(header, 6, 1);
        WriteVarintField(header, 8, 1);

        var response = new List<byte>();
        WriteDelimitedMessage(response, header);
        var page = new List<byte>();
        WriteLengthDelimitedField(page, 2, databaseImage.AsSpan(0, 2048));
        WriteDelimitedMessage(response, page);
        return response.ToArray();
    }

    private static ulong? ReadVarintField(byte[] payload, int requestedField)
    {
        var offset = 0;
        while (offset < payload.Length)
        {
            var key = ReadVarint(payload, ref offset);
            var field = checked((int)(key >> 3));
            switch (key & 7)
            {
                case 0:
                    var value = ReadVarint(payload, ref offset);
                    if (field == requestedField)
                        return value;
                    break;
                case 2:
                    var length = checked((int)ReadVarint(payload, ref offset));
                    offset += length;
                    break;
                default:
                    throw new InvalidOperationException("Unsupported test protobuf wire type.");
            }
        }

        return null;
    }

    private static int IndexOfSubsequence(byte[] payload, byte[] needle)
    {
        for (var start = 0; start + needle.Length <= payload.Length; start++)
        {
            if (payload.AsSpan(start, needle.Length).SequenceEqual(needle))
                return start;
        }

        return -1;
    }

    public enum QueryBootstrapProtocolViolation
    {
        DuplicatePage,
        OutOfRangePage,
        MissingHeaderPage,
        NoPagesAtAll,
        ShortPage,
        LogicalStreamKind,
        Zstd,
    }

    private readonly record struct TargetedPagePullRequest(
        string? Revision,
        string? Query,
        IReadOnlyList<uint> Pages);

    /// <summary>
    /// Hand-written fake sync server that implements query-selected bootstrap. See this file's class
    /// remarks: Turso's vendored dev server cannot do this, so its behavior here is a test fixture,
    /// not a compatibility claim.
    /// </summary>
    private sealed class QueryBootstrapPullHandler : HttpMessageHandler
    {
        private readonly object _gate = new();
        private readonly string _revision;
        private readonly byte[] _databaseImage;
        private readonly uint[] _bootstrapPages;
        private readonly ulong _protocol;
        private readonly bool _gateTargetedPulls;
        private readonly Func<HttpResponseMessage>? _pushResponse;
        private readonly List<byte[]> _requests = [];
        private readonly List<string> _bootstrapQueries = [];
        private readonly List<TargetedPagePullRequest> _targetedRequests = [];
        private int _bootstrapRequestsCarryingAPageSelector;
        private int _bootstrapCallCount;
        private int _logicalCatchUpCallCount;
        private int _totalPullCallCount;
        private int _pushCallCount;
        private int _activeTargetedPulls;
        private int _maximumConcurrentTargetedPulls;

        public QueryBootstrapPullHandler(
            string revision,
            byte[] databaseImage,
            IReadOnlyList<uint> bootstrapPages,
            ulong protocol = 1,
            bool gateTargetedPulls = false,
            Func<HttpResponseMessage>? pushResponse = null)
        {
            _revision = revision;
            _databaseImage = databaseImage;
            _bootstrapPages = bootstrapPages.ToArray();
            _protocol = protocol;
            _gateTargetedPulls = gateTargetedPulls;
            _pushResponse = pushResponse;
        }

        public int BootstrapCallCount => Volatile.Read(ref _bootstrapCallCount);

        public int LogicalCatchUpCallCount => Volatile.Read(ref _logicalCatchUpCallCount);

        public int TotalPullCallCount => Volatile.Read(ref _totalPullCallCount);

        public int PushCallCount => Volatile.Read(ref _pushCallCount);

        public int BootstrapRequestsCarryingAPageSelector =>
            Volatile.Read(ref _bootstrapRequestsCarryingAPageSelector);

        public int MaximumConcurrentTargetedPulls => Volatile.Read(ref _maximumConcurrentTargetedPulls);

        public IReadOnlyList<byte[]> Requests
        {
            get
            {
                lock (_gate)
                    return _requests.ToArray();
            }
        }

        public IReadOnlyList<string> BootstrapQueries
        {
            get
            {
                lock (_gate)
                    return _bootstrapQueries.ToArray();
            }
        }

        public IReadOnlyList<TargetedPagePullRequest> TargetedRequests
        {
            get
            {
                lock (_gate)
                    return _targetedRequests.ToArray();
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("/pull-updates", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _pushCallCount);
                return _pushResponse is null
                    ? ReplicaPushHandler.SuccessfulBatchResponse(stepCount: 5)
                    : _pushResponse();
            }

            var payload = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            Interlocked.Increment(ref _totalPullCallCount);
            lock (_gate)
                _requests.Add(payload);

            byte[] response;
            if (TryReadLengthDelimitedField(payload, 7, out var queryBytes))
            {
                Interlocked.Increment(ref _bootstrapCallCount);
                if (TryReadLengthDelimitedField(payload, 5, out _))
                    Interlocked.Increment(ref _bootstrapRequestsCarryingAPageSelector);
                lock (_gate)
                    _bootstrapQueries.Add(Encoding.UTF8.GetString(queryBytes));
                response = CreatePageSubsetPullResponse(
                    _revision,
                    _databaseImage,
                    _bootstrapPages,
                    protocol: _protocol);
            }
            else if (TryReadLengthDelimitedField(payload, 5, out var selector))
            {
                var active = Interlocked.Increment(ref _activeTargetedPulls);
                UpdateMaximum(active);
                try
                {
                    if (_gateTargetedPulls)
                        await Task.Delay(25, cancellationToken);

                    var cookie = BinaryPrimitives.ReadUInt32LittleEndian(selector);
                    var pages = (checked((ushort)(cookie & ushort.MaxValue)) == 12347
                            ? DecodeRoaringPageSelector(selector)
                            : ReadPortableRoaringBitmap(selector))
                        .ToArray();
                    var revision = TryReadLengthDelimitedField(payload, 2, out var revisionBytes)
                        ? Encoding.UTF8.GetString(revisionBytes)
                        : null;
                    lock (_gate)
                    {
                        _targetedRequests.Add(new TargetedPagePullRequest(
                            revision,
                            Query: null,
                            pages));
                    }

                    response = CreatePageSubsetPullResponse(
                        _revision,
                        _databaseImage,
                        pages,
                        protocol: _protocol);
                }
                finally
                {
                    Interlocked.Decrement(ref _activeTargetedPulls);
                }
            }
            else if (_protocol == 2)
            {
                Interlocked.Increment(ref _logicalCatchUpCallCount);
                response = CreateLogicalPullResponse(
                    _revision,
                    body: [],
                    declaredPages: checked((ulong)(_databaseImage.Length / 4096)));
            }
            else
            {
                response = CreatePullResponse(
                    _revision,
                    [],
                    declaredPages: checked((ulong)(_databaseImage.Length / 4096)));
            }

            var message = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(response),
            };
            message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/protobuf");
            return message;
        }

        private void UpdateMaximum(int observed)
        {
            var current = Volatile.Read(ref _maximumConcurrentTargetedPulls);
            while (observed > current)
            {
                var exchanged = Interlocked.CompareExchange(
                    ref _maximumConcurrentTargetedPulls,
                    observed,
                    current);
                if (exchanged == current)
                    return;
                current = exchanged;
            }
        }
    }
}
