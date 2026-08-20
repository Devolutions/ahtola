using System.Data;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using Ahtola.Data.Sqlite;
using AhtolaSqliteConnection = Ahtola.Data.Sqlite.SqliteConnection;

namespace Ahtola.Tests;

/// <summary>
/// <see cref="Ahtola.EntityFrameworkCore.Sqlite.Storage.Internal.AhtolaSqliteDatabaseCreator"/>
/// must behave safely and correctly for a direct remote Hrana connection: <c>Exists</c> uses a
/// lightweight server query (it cannot enforce Mode=ReadOnly, unlike local connections),
/// <c>Create</c>/<c>EnsureCreated</c> send real DDL rather than assuming a local file,
/// <c>Delete</c>/<c>EnsureDeleted</c> never attempt to delete the remote database, and basic
/// migrations work end to end using EF Core's stock SQLite history/migrations services.
/// </summary>
public sealed class RemoteEfDatabaseCreatorTests
{
    private const string ConnectionString = "Data Source=turso://database.example;Auth Token=token";

    [SetUp]
    public void InstallHandler() => _priorFactory = AhtolaSqliteConnection.RemoteMessageHandlerFactory;

    [TearDown]
    public void RestoreHandler() => AhtolaSqliteConnection.RemoteMessageHandlerFactory = _priorFactory;

    private Func<HttpMessageHandler?>? _priorFactory;

    [Test]
    public void Exists_SendsLightweightProbeQuery_WithoutEnforcingLocalReadOnlyMode()
    {
        using var handler = new ScriptedHranaHandler();
        using var context = CreateContext(handler);

        var exists = context.GetService<IRelationalDatabaseCreator>().Exists();

        exists.Should().BeTrue();
        handler.SqlLog.Should().ContainSingle();
        handler.SqlLog[0].Should().Be("SELECT 1;");
    }

    [Test]
    public async Task ExistsAsync_SendsLightweightProbeQuery()
    {
        using var handler = new ScriptedHranaHandler();
        using var context = CreateContext(handler);

        var exists = await context.GetService<IRelationalDatabaseCreator>().ExistsAsync();

        exists.Should().BeTrue();
        handler.SqlLog.Should().ContainSingle();
        handler.SqlLog[0].Should().Be("SELECT 1;");
    }

    [Test]
    public void Exists_ReturnsFalse_WhenTheServerReportsHttp404NotFound()
    {
        using var handler = new ScriptedHranaHandler();
        handler.HttpErrorAt(0, HttpStatusCode.NotFound);
        using var context = CreateContext(handler);

        var exists = context.GetService<IRelationalDatabaseCreator>().Exists();

        exists.Should().BeFalse("HTTP 404 is the only not-found signal Exists() may fold into false");
    }

    [Test]
    public async Task ExistsAsync_ReturnsFalse_WhenTheServerReportsHttp404NotFound()
    {
        using var handler = new ScriptedHranaHandler();
        handler.HttpErrorAt(0, HttpStatusCode.NotFound);
        using var context = CreateContext(handler);

        var exists = await context.GetService<IRelationalDatabaseCreator>().ExistsAsync();

        exists.Should().BeFalse();
    }

    [Test]
    public void Exists_PropagatesAuthenticationFailure_WhenTheServerReturnsHttp401()
    {
        using var handler = new ScriptedHranaHandler();
        handler.HttpErrorAt(0, HttpStatusCode.Unauthorized);
        using var context = CreateContext(handler);

        var exists = () => context.GetService<IRelationalDatabaseCreator>().Exists();

        // An authentication failure is not "the database doesn't exist" — Exists() must never
        // swallow it into false, or callers could mistake a bad token for a missing database.
        exists.Should().Throw<SqliteRemoteException>()
            .Which.HttpStatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task ExistsAsync_PropagatesAuthenticationFailure_WhenTheServerReturnsHttp403()
    {
        using var handler = new ScriptedHranaHandler();
        handler.HttpErrorAt(0, HttpStatusCode.Forbidden);
        using var context = CreateContext(handler);

        var exists = async () => await context.GetService<IRelationalDatabaseCreator>().ExistsAsync();

        (await exists.Should().ThrowAsync<SqliteRemoteException>())
            .Which.HttpStatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Test]
    public void Exists_PropagatesTransientFailure_WhenTheServerReturnsHttp503()
    {
        using var handler = new ScriptedHranaHandler();
        handler.HttpErrorAt(0, HttpStatusCode.ServiceUnavailable);
        using var context = CreateContext(handler);

        var exists = () => context.GetService<IRelationalDatabaseCreator>().Exists();

        // A transient server-side failure must propagate so retry policies can see it — folding
        // it into false would make a temporarily-unavailable server look like a missing database.
        var thrown = exists.Should().Throw<SqliteRemoteException>().Which;
        thrown.HttpStatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        SqliteRemoteExceptionClassifier.IsTransient(thrown).Should().BeTrue();
    }

    [Test]
    public async Task ExistsAsync_PropagatesTransientFailure_WhenTheServerReturnsHttp429()
    {
        using var handler = new ScriptedHranaHandler();
        handler.HttpErrorAt(0, HttpStatusCode.TooManyRequests);
        using var context = CreateContext(handler);

        var exists = async () => await context.GetService<IRelationalDatabaseCreator>().ExistsAsync();

        var thrown = (await exists.Should().ThrowAsync<SqliteRemoteException>()).Which;
        thrown.HttpStatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        SqliteRemoteExceptionClassifier.IsTransient(thrown).Should().BeTrue();
    }

    [Test]
    public void Exists_PropagatesProtocolError_WhenTheServerReportsANonNotFoundSqliteError()
    {
        using var handler = new ScriptedHranaHandler();
        handler.ErrorAt(0, "remote database not reachable");
        using var context = CreateContext(handler);

        var exists = () => context.GetService<IRelationalDatabaseCreator>().Exists();

        // A generic Hrana-level protocol error (HTTP 200 with a JSON error body, not mapped to
        // SQLITE_CANTOPEN) is a permanent failure, but it is not "not-found" either — it must
        // still propagate rather than be reported as a clean false.
        exists.Should().Throw<SqliteRemoteException>();
    }

    [Test]
    public void EnsureCreated_ChecksExistenceAndTables_ThenSendsCreateTableDdl()
    {
        using var handler = new ScriptedHranaHandler();
        using var context = CreateContext(handler);

        var created = context.Database.EnsureCreated();

        created.Should().BeTrue();
        handler.SqlLog.Should().Contain(sql => sql.Equals("SELECT 1;", StringComparison.Ordinal));
        handler.SqlLog.Should().Contain(sql => sql.Contains("sqlite_master", StringComparison.OrdinalIgnoreCase));
        handler.SqlLog.Should().Contain(sql =>
            sql.Contains("CREATE TABLE", StringComparison.OrdinalIgnoreCase) && sql.Contains("\"Widgets\""));
    }

    [Test]
    public void Create_ForRemoteConnection_DoesNotSendJournalModePragma()
    {
        using var handler = new ScriptedHranaHandler();
        using var context = CreateContext(handler);

        context.GetService<IRelationalDatabaseCreator>().Create();

        // Create() opens/closes the connection but must not attempt PRAGMA journal_mode=wal
        // against a remote endpoint (there is no local file for this process to journal).
        handler.SqlLog.Should().NotContain(sql => sql.Contains("journal_mode", StringComparison.OrdinalIgnoreCase));
    }

    [Test]
    public void EnsureDeleted_ForDirectRemoteConnection_NeverSendsDestructiveSql()
    {
        using var handler = new ScriptedHranaHandler();
        using var context = CreateContext(handler);

        context.Database.EnsureDeleted();

        handler.SqlLog.Should().NotContain(sql => sql.Contains("DROP", StringComparison.OrdinalIgnoreCase));
        handler.SqlLog.Should().NotContain(sql => sql.Contains("DELETE FROM", StringComparison.OrdinalIgnoreCase));
    }

    [Test]
    public async Task DeleteAsync_ForDirectRemoteConnection_NeverSendsDestructiveSql()
    {
        using var handler = new ScriptedHranaHandler();
        using var context = CreateContext(handler);

        await context.GetService<IRelationalDatabaseCreator>().DeleteAsync();

        handler.SqlLog.Should().BeEmpty("a direct remote Delete must be a safe no-op");
    }

    [Test]
    public void Exists_ForEmbeddedReplica_ReturnsFalse_WithoutAnyHttpRequests_WhenLocalFileIsAbsent()
    {
        var path = NewReplicaPath();
        using var handler = new ScriptedHranaHandler();
        using var context = CreateReplicaContext(path, handler);

        var exists = context.GetService<IRelationalDatabaseCreator>().Exists();

        exists.Should().BeFalse();
        // Opening an embedded-replica connection when the local database is absent triggers a
        // full remote bootstrap/download; Exists() must determine local existence from on-disk
        // state alone and never do that as a side effect of merely checking.
        handler.RequestCount.Should().Be(0);
    }

    [Test]
    public async Task ExistsAsync_ForEmbeddedReplica_ReturnsFalse_WithoutAnyHttpRequests_WhenLocalFileIsAbsent()
    {
        var path = NewReplicaPath();
        using var handler = new ScriptedHranaHandler();
        using var context = CreateReplicaContext(path, handler);

        var exists = await context.GetService<IRelationalDatabaseCreator>().ExistsAsync();

        exists.Should().BeFalse();
        handler.RequestCount.Should().Be(0);
    }

    [Test]
    public void Exists_ForEmbeddedReplica_ReturnsTrue_WithoutAnyHttpRequests_WhenLocalPairIsValid()
    {
        var path = NewReplicaPath();
        CreateLocalReplicaArtifacts(path, includeMetadata: true);
        try
        {
            using var handler = new ScriptedHranaHandler();
            using var context = CreateReplicaContext(path, handler);

            var exists = context.GetService<IRelationalDatabaseCreator>().Exists();

            exists.Should().BeTrue();
            handler.RequestCount.Should().Be(0);
        }
        finally
        {
            DeleteAllReplicaArtifacts(path);
        }
    }

    [Test]
    public void Exists_ForEmbeddedReplica_Throws_WhenLocalMetadataExistsWithoutDatabase()
    {
        var path = NewReplicaPath();
        File.WriteAllText(path + ManagedReplicaBootstrapper.MetadataSuffix, "orphaned-metadata");
        try
        {
            using var handler = new ScriptedHranaHandler();
            using var context = CreateReplicaContext(path, handler);

            var exists = () => context.GetService<IRelationalDatabaseCreator>().Exists();

            // This is the same inconsistent pair state ManagedReplicaBootstrapper itself refuses
            // to resolve automatically; Exists() must surface it as an error, never silently
            // report true/false or repair it (which could mask data loss/corruption).
            exists.Should().Throw<InvalidOperationException>();
            handler.RequestCount.Should().Be(0);
        }
        finally
        {
            DeleteAllReplicaArtifacts(path);
        }
    }

    [Test]
    public void EnsureDeleted_ForEmbeddedReplica_RecoversFromInconsistentLocalState_WithoutGoingThroughExistsFirst()
    {
        // The exact recovery path an inconsistent-pair Exists() failure points callers to
        // (EnsureDeleted()) must actually be reachable: the base RelationalDatabaseCreator's
        // EnsureDeleted() calls Exists() *before* Delete(), so if Exists() throws for this state
        // (as it must — see Exists_ForEmbeddedReplica_Throws_WhenLocalMetadataExistsWithoutDatabase
        // above), going through that base implementation here would throw the very same error
        // instead of ever reaching Delete(). AhtolaSqliteDatabaseCreator's EnsureDeleted override
        // must detect "is there anything local to remove" directly, bypassing that gate.
        var path = NewReplicaPath();
        File.WriteAllText(path + ManagedReplicaBootstrapper.MetadataSuffix, "orphaned-metadata");
        try
        {
            using var handler = new ScriptedHranaHandler();
            using var context = CreateReplicaContext(path, handler);

            var deleted = false;
            Action attempt = () => deleted = context.Database.EnsureDeleted();

            attempt.Should().NotThrow();
            deleted.Should().BeTrue();
            handler.RequestCount.Should().Be(0, "a local replica delete must never contact the remote endpoint");

            // With the orphaned metadata gone, local state must cleanly report "absent" —
            // proving the recovery is complete and a caller can proceed to bootstrap again
            // (Exists()/ExistsAsync() no longer throw, which is the precondition EnsureCreated()
            // itself relies on before attempting a fresh Create()).
            ManagedReplicaBootstrapper.GetLocalState(path).Should().Be(ManagedReplicaLocalState.Absent);
            context.GetService<IRelationalDatabaseCreator>().Exists().Should().BeFalse();
        }
        finally
        {
            DeleteAllReplicaArtifacts(path);
        }
    }

    [Test]
    public void EnsureDeleted_ForEmbeddedReplica_RemovesEveryLocalArtifact_AllowingCleanBootstrapRetry()
    {
        var path = NewReplicaPath();
        CreateLocalReplicaArtifacts(path, includeMetadata: true);
        // Simulate leftover sidecars a real replica session could plausibly have left behind,
        // to prove Delete removes the full matched artifact set, not just DB/-wal/-shm.
        File.WriteAllText(path + "-journal", "stale-journal");
        File.WriteAllText(path + ManagedReplicaChangeJournal.Suffix, "stale-change-journal");
        try
        {
            using var handler = new ScriptedHranaHandler();
            using var context = CreateReplicaContext(path, handler);

            context.Database.EnsureDeleted();

            foreach (var artifactPath in ManagedReplicaBootstrapper.GetLocalArtifactPaths(path))
                File.Exists(artifactPath).Should().BeFalse($"'{artifactPath}' must be removed by EnsureDeleted()");

            // With every artifact gone, local existence must cleanly report "absent" again
            // rather than an inconsistent-pair error — proving Delete didn't leave a stale
            // partial pair that would break a subsequent EnsureCreated/bootstrap attempt.
            ManagedReplicaBootstrapper.GetLocalState(path).Should().Be(ManagedReplicaLocalState.Absent);
            handler.RequestCount.Should().Be(0, "a local replica delete must never contact the remote endpoint");
        }
        finally
        {
            DeleteAllReplicaArtifacts(path);
        }
    }

    [Test]
    public async Task DeleteAsync_ForEmbeddedReplica_RemovesEveryLocalArtifact()
    {
        var path = NewReplicaPath();
        CreateLocalReplicaArtifacts(path, includeMetadata: true);
        File.WriteAllText(path + "-journal", "stale-journal");
        File.WriteAllText(path + ManagedReplicaChangeJournal.Suffix, "stale-change-journal");
        try
        {
            using var handler = new ScriptedHranaHandler();
            using var context = CreateReplicaContext(path, handler);

            await context.GetService<IRelationalDatabaseCreator>().DeleteAsync();

            foreach (var artifactPath in ManagedReplicaBootstrapper.GetLocalArtifactPaths(path))
                File.Exists(artifactPath).Should().BeFalse($"'{artifactPath}' must be removed by DeleteAsync()");
        }
        finally
        {
            DeleteAllReplicaArtifacts(path);
        }
    }

    [Test]
    public void EnsureDeleted_ForEmbeddedReplica_WhenConnectionWasOpen_LeavesItClosed_WithoutRebootstrapping()
    {
        // Regression test for the reopen-undoes-delete lifecycle bug: DeleteReplicaArtifacts
        // used to reopen the connection to restore the "was open" state, but opening an
        // embedded-replica connection when the local database is absent triggers a full remote
        // bootstrap/download — silently re-materializing everything EnsureDeleted() just deleted
        // (or throwing after a successful delete, if the remote happens to be unreachable). The
        // connection must instead be left closed; only the caller's own later, explicit Open may
        // legitimately bootstrap fresh.
        var path = NewReplicaPath();
        CreateValidLocalReplicaPair(path);
        var fixturePath = NewReplicaPath();
        var remoteImage = CreateReplicaFixtureImage(fixturePath, includeWidgetsSchema: true);
        using var handler = new ScriptedBootstrapHandler(remoteImage);
        var priorFactory = AhtolaSqliteConnection.RemoteMessageHandlerFactory;
        AhtolaSqliteConnection.RemoteMessageHandlerFactory = () => handler;
        try
        {
            var options = new DbContextOptionsBuilder<RemoteWidgetContext>()
                .UseAhtola($"Data Source=turso://database.example;Replica Path={path};Auth Token=token")
                .Options;
            using var context = new RemoteWidgetContext(options);

            context.Database.OpenConnection();
            context.Database.GetDbConnection().State.Should().Be(ConnectionState.Open);

            var deleted = context.Database.EnsureDeleted();

            deleted.Should().BeTrue();
            context.Database.GetDbConnection().State.Should().Be(
                ConnectionState.Closed,
                "the connection must not be silently reopened (and thereby rebootstrapped) after delete");
            ManagedReplicaBootstrapper.GetLocalState(path).Should().Be(ManagedReplicaLocalState.Absent);
            handler.RequestCount.Should().Be(0, "no bootstrap attempt may happen as a side effect of EnsureDeleted()");

            // The caller's own subsequent, explicit Open is a different matter and may
            // legitimately trigger a fresh bootstrap now that the local database is gone.
            context.Database.OpenConnection();
            handler.RequestCount.Should().Be(1, "an explicit reopen after delete is allowed to bootstrap fresh");
        }
        finally
        {
            AhtolaSqliteConnection.RemoteMessageHandlerFactory = priorFactory;
            DeleteAllReplicaArtifacts(path);
        }
    }

    [Test]
    public async Task EnsureDeletedAsync_ForEmbeddedReplica_WhenConnectionWasOpen_LeavesItClosed_WithoutRebootstrapping()
    {
        var path = NewReplicaPath();
        CreateValidLocalReplicaPair(path);
        var fixturePath = NewReplicaPath();
        var remoteImage = CreateReplicaFixtureImage(fixturePath, includeWidgetsSchema: true);
        using var handler = new ScriptedBootstrapHandler(remoteImage);
        var priorFactory = AhtolaSqliteConnection.RemoteMessageHandlerFactory;
        AhtolaSqliteConnection.RemoteMessageHandlerFactory = () => handler;
        try
        {
            var options = new DbContextOptionsBuilder<RemoteWidgetContext>()
                .UseAhtola($"Data Source=turso://database.example;Replica Path={path};Auth Token=token")
                .Options;
            using var context = new RemoteWidgetContext(options);

            await context.Database.OpenConnectionAsync();
            context.Database.GetDbConnection().State.Should().Be(ConnectionState.Open);

            var deleted = await context.GetService<IRelationalDatabaseCreator>().EnsureDeletedAsync();

            deleted.Should().BeTrue();
            context.Database.GetDbConnection().State.Should().Be(
                ConnectionState.Closed,
                "the connection must not be silently reopened (and thereby rebootstrapped) after delete");
            ManagedReplicaBootstrapper.GetLocalState(path).Should().Be(ManagedReplicaLocalState.Absent);
            handler.RequestCount.Should().Be(0, "no bootstrap attempt may happen as a side effect of EnsureDeletedAsync()");

            await context.Database.OpenConnectionAsync();
            handler.RequestCount.Should().Be(1, "an explicit reopen after delete is allowed to bootstrap fresh");
        }
        finally
        {
            AhtolaSqliteConnection.RemoteMessageHandlerFactory = priorFactory;
            DeleteAllReplicaArtifacts(path);
        }
    }

    [Test]
    public void EnsureCreated_ForEmbeddedReplica_ColdWithNonEmptyRemote_BootstrapsWithoutDuplicatingSchema()
    {
        // Regression test for the "cold EnsureCreated" bug: Exists() correctly reports false for
        // a brand-new local replica path without any bootstrap side effect (see the Exists()
        // tests above), but Create() DOES bootstrap a full snapshot from the remote — and if
        // that snapshot already has the model's tables (a non-empty remote), EnsureCreated()
        // must not attempt a second, duplicate CREATE TABLE against them (which would throw a
        // local "table already exists" error under the pre-fix unconditional-CreateTables()
        // algorithm).
        var path = NewReplicaPath();
        var fixturePath = NewReplicaPath();
        var remoteImage = CreateReplicaFixtureImage(fixturePath, includeWidgetsSchema: true);
        using var handler = new ScriptedBootstrapHandler(remoteImage);
        var priorFactory = AhtolaSqliteConnection.RemoteMessageHandlerFactory;
        AhtolaSqliteConnection.RemoteMessageHandlerFactory = () => handler;
        try
        {
            var options = new DbContextOptionsBuilder<RemoteWidgetContext>()
                .UseAhtola($"Data Source=turso://database.example;Replica Path={path};Auth Token=token")
                .Options;
            using var context = new RemoteWidgetContext(options);

            var created = false;
            Action ensureCreated = () => created = context.Database.EnsureCreated();

            ensureCreated.Should().NotThrow();
            created.Should().BeTrue("bootstrapping a brand-new local replica is itself a create operation");
            handler.RequestCount.Should().Be(
                1,
                "HasTables()/CreateTables() operate on the now-local replica file directly and must "
                + "never make a further remote request once bootstrapped");

            // The bootstrapped schema must actually be usable afterward — proves the fixture
            // image was applied, not merely that no exception happened to occur.
            context.Widgets.Add(new RemoteWidget { Name = "Widget A" });
            Action saveLocally = () => context.SaveChanges();
            saveLocally.Should().NotThrow();
            handler.RequestCount.Should().Be(1, "an unconfigured replica buffers writes locally rather than pushing immediately");
        }
        finally
        {
            AhtolaSqliteConnection.RemoteMessageHandlerFactory = priorFactory;
            DeleteAllReplicaArtifacts(path);
        }
    }

    [Test]
    public void EnsureCreated_ForEmbeddedReplica_ColdWithEmptyRemote_CreatesTablesAfterBootstrapping()
    {
        // The companion "empty remote" scenario: Create() bootstraps a schema-less snapshot, so
        // EnsureCreated() must still create the model's tables afterward — proving the fix
        // preserves the original behavior for a genuinely empty remote, not just that it avoids
        // the duplicate-schema regression for a non-empty one.
        var path = NewReplicaPath();
        var fixturePath = NewReplicaPath();
        var remoteImage = CreateReplicaFixtureImage(fixturePath, includeWidgetsSchema: false);
        using var handler = new ScriptedBootstrapHandler(remoteImage);
        var priorFactory = AhtolaSqliteConnection.RemoteMessageHandlerFactory;
        AhtolaSqliteConnection.RemoteMessageHandlerFactory = () => handler;
        try
        {
            var options = new DbContextOptionsBuilder<RemoteWidgetContext>()
                .UseAhtola($"Data Source=turso://database.example;Replica Path={path};Auth Token=token")
                .Options;
            using var context = new RemoteWidgetContext(options);

            var created = context.Database.EnsureCreated();

            created.Should().BeTrue();
            handler.RequestCount.Should().Be(1, "CreateTables() runs against the now-local replica file, not over the wire");
            context.GetService<IRelationalDatabaseCreator>().HasTables()
                .Should().BeTrue("CreateTables() must have run against the empty bootstrapped schema");

            context.Widgets.Add(new RemoteWidget { Name = "Widget A" });
            Action saveLocally = () => context.SaveChanges();
            saveLocally.Should().NotThrow();
        }
        finally
        {
            AhtolaSqliteConnection.RemoteMessageHandlerFactory = priorFactory;
            DeleteAllReplicaArtifacts(path);
        }
    }

    [Test]
    public void Migrate_AppliesMigrations_UsingStockSqliteServicesOverRemote()
    {
        using var handler = new ScriptedHranaHandler();
        AhtolaSqliteConnection.RemoteMessageHandlerFactory = () => handler;
        var options = new DbContextOptionsBuilder<MigratedWidgetContext>()
            .UseAhtola(ConnectionString)
            .Options;
        using var context = new MigratedWidgetContext(options);

        context.Database.Migrate();

        handler.SqlLog.Should().Contain(sql => sql.Contains("__EFMigrationsHistory", StringComparison.Ordinal));
        handler.SqlLog.Should().Contain(sql =>
            sql.Contains("CREATE TABLE", StringComparison.OrdinalIgnoreCase) && sql.Contains("\"Widgets\""));
        handler.SqlLog.Should().Contain(sql => sql.Contains("INSERT INTO \"__EFMigrationsHistory\"", StringComparison.Ordinal));
    }

    [Test]
    public void GenerateIdempotentScript_ForRemoteConnection_UsesStockSqliteDiagnosticNotManagedOne()
    {
        // RemoteHrana uses EF Core's stock SqliteHistoryRepository/SqliteMigrationsSqlGenerator
        // rather than the managed-engine-restricted variants. SQLite itself cannot run
        // idempotent scripts regardless of engine, so this still throws NotSupportedException —
        // but it must be EF Core's own stock-provider diagnostic, never Ahtola's
        // managed-provider-specific "does not support idempotent migration scripts" message.
        var options = new DbContextOptionsBuilder<MigratedWidgetContext>()
            .UseAhtola(ConnectionString)
            .Options;
        using var context = new MigratedWidgetContext(options);

        var generate = () => context.GetService<IMigrator>()
            .GenerateScript(options: MigrationsSqlGenerationOptions.Idempotent);

        generate.Should().Throw<NotSupportedException>()
            .Which.Message.Should().NotContain("managed local provider");
    }

    private static RemoteWidgetContext CreateContext(ScriptedHranaHandler handler)
    {
        AhtolaSqliteConnection.RemoteMessageHandlerFactory = () => handler;
        var options = new DbContextOptionsBuilder<RemoteWidgetContext>()
            .UseAhtola(ConnectionString)
            .Options;
        return new RemoteWidgetContext(options);
    }

    private static RemoteWidgetContext CreateReplicaContext(string replicaPath, ScriptedHranaHandler handler)
    {
        AhtolaSqliteConnection.RemoteMessageHandlerFactory = () => handler;
        var options = new DbContextOptionsBuilder<RemoteWidgetContext>()
            .UseAhtola($"Data Source=turso://database.example;Replica Path={replicaPath};Auth Token=token")
            .Options;
        return new RemoteWidgetContext(options);
    }

    private static string NewReplicaPath()
        => Path.Combine(TestContext.CurrentContext.WorkDirectory, $"remote-ef-replica-{Guid.NewGuid():N}.db");

    /// <summary>Writes a local replica database file (and, when requested, its metadata
    /// sidecar) directly to disk — deliberately as plain placeholder bytes, not a real SQLite
    /// image, so any code path that accidentally tries to open/parse it as a database fails
    /// loudly instead of silently succeeding.</summary>
    private static void CreateLocalReplicaArtifacts(string path, bool includeMetadata)
    {
        File.WriteAllText(path, "placeholder-database");
        if (includeMetadata)
            File.WriteAllText(path + ManagedReplicaBootstrapper.MetadataSuffix, "placeholder-metadata");
    }

    /// <summary>Creates a genuinely valid local replica pair at <paramref name="path"/>: a real
    /// managed SQLite database file (so a normal connection Open() actually succeeds against
    /// it, unlike <see cref="CreateLocalReplicaArtifacts"/>'s deliberately invalid placeholder
    /// bytes) plus a well-formed metadata sidecar matching its fingerprint — a plain connection
    /// Open() against an embedded replica DOES load and validate this metadata (unlike a bare
    /// Exists() filesystem check), so unlike CreateLocalReplicaArtifacts's placeholder text, it
    /// must actually parse.</summary>
    private static void CreateValidLocalReplicaPair(string path)
    {
        using (var connection = new AhtolaSqliteConnection($"Data Source={path};Local Provider=Managed;Pooling=False"))
            connection.Open();

        AhtolaSqliteConnection.ClearAllPools();
        var fingerprint = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        File.WriteAllText(
            path + ManagedReplicaBootstrapper.MetadataSuffix,
            $"version=2\nserver_revision_base64={Convert.ToBase64String(Encoding.UTF8.GetBytes("rev"))}\n"
            + $"database_sha256={fingerprint}\nclient_id={Guid.NewGuid():N}\n");
    }

    private static void DeleteAllReplicaArtifacts(string path)
    {
        foreach (var artifactPath in ManagedReplicaBootstrapper.GetLocalArtifactPaths(path))
        {
            if (File.Exists(artifactPath))
                File.Delete(artifactPath);
        }
    }

    /// <summary>Creates a real (tiny) managed SQLite database file at <paramref name="path"/> —
    /// either with the <see cref="RemoteWidgetContext"/> model's schema already applied (a
    /// "non-empty remote" fixture) or genuinely blank (an "empty remote" fixture) — returns its
    /// raw bytes, and deletes the local scratch file. Used as a bootstrap fixture image with
    /// <see cref="ScriptedBootstrapHandler"/>: a real database file's size is always a whole
    /// number of 4096-byte pages, matching the bootstrap wire format's page-size assumption.</summary>
    private static byte[] CreateReplicaFixtureImage(string path, bool includeWidgetsSchema)
    {
        try
        {
            if (includeWidgetsSchema)
            {
                var options = new DbContextOptionsBuilder<RemoteWidgetContext>()
                    .UseAhtola($"Data Source={path};Local Provider=Managed;Pooling=False")
                    .Options;
                using var context = new RemoteWidgetContext(options);
                context.Database.EnsureCreated();
            }
            else
            {
                using var connection = new AhtolaSqliteConnection($"Data Source={path};Local Provider=Managed;Pooling=False");
                connection.Open();
            }

            AhtolaSqliteConnection.ClearAllPools();
            return File.ReadAllBytes(path);
        }
        finally
        {
            foreach (var artifact in new[] { path, path + "-wal", path + "-shm", path + "-journal" })
            {
                if (File.Exists(artifact))
                    File.Delete(artifact);
            }
        }
    }

    private sealed class MigratedWidgetContext(DbContextOptions<MigratedWidgetContext> options) : DbContext(options)
    {
        public DbSet<RemoteWidget> Widgets => Set<RemoteWidget>();
    }

    [DbContext(typeof(MigratedWidgetContext))]
    [Migration("20260101000000_CreateWidgets")]
    public sealed class CreateWidgetsMigration : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
            => migrationBuilder.CreateTable(
                name: "Widgets",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false, defaultValue: ""),
                },
                constraints: table => table.PrimaryKey("PK_Widgets", x => x.Id));

        protected override void Down(MigrationBuilder migrationBuilder)
            => migrationBuilder.DropTable("Widgets");
    }
}
