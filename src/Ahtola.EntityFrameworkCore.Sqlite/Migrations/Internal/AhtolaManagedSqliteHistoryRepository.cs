using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Sqlite.Migrations.Internal;
using Microsoft.EntityFrameworkCore.Storage;

namespace Ahtola.EntityFrameworkCore.Sqlite.Migrations.Internal;

public sealed class AhtolaManagedSqliteHistoryRepository(HistoryRepositoryDependencies dependencies)
    : SqliteHistoryRepository(dependencies), IHistoryRepository
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);

    public override string GetBeginIfNotExistsScript(string migrationId)
        => throw IdempotentScriptsNotSupported();

    public override string GetBeginIfExistsScript(string migrationId)
        => throw IdempotentScriptsNotSupported();

    public override string GetEndIfScript()
        => throw IdempotentScriptsNotSupported();

    bool IHistoryRepository.CreateIfNotExists()
    {
        if (Exists())
            return false;

        Create();
        return true;
    }

    async Task<bool> IHistoryRepository.CreateIfNotExistsAsync(CancellationToken cancellationToken)
    {
        if (await ExistsAsync(cancellationToken).ConfigureAwait(false))
            return false;

        await CreateAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public override IMigrationsDatabaseLock AcquireDatabaseLock()
    {
        Dependencies.MigrationsLogger.AcquiringMigrationLock();
        var parameters = CreateRelationalCommandParameters();
        CreateLockTableCommand().ExecuteNonQuery(parameters);

        while (true)
        {
            if (CreateInsertLockCommand().ExecuteNonQuery(parameters) == 1)
            {
                return new SqliteMigrationDatabaseLock(
                    CreateDeleteLockCommand(),
                    parameters,
                    this);
            }

            Thread.Sleep(RetryDelay);
        }
    }

    public override async Task<IMigrationsDatabaseLock> AcquireDatabaseLockAsync(
        CancellationToken cancellationToken = default)
    {
        Dependencies.MigrationsLogger.AcquiringMigrationLock();
        var parameters = CreateRelationalCommandParameters();
        await CreateLockTableCommand()
            .ExecuteNonQueryAsync(parameters, cancellationToken)
            .ConfigureAwait(false);

        while (true)
        {
            if (await CreateInsertLockCommand()
                    .ExecuteNonQueryAsync(parameters, cancellationToken)
                    .ConfigureAwait(false) == 1)
            {
                return new SqliteMigrationDatabaseLock(
                    CreateDeleteLockCommand(),
                    parameters,
                    this);
            }

            await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
        }
    }

    private IRelationalCommand CreateLockTableCommand()
        => Dependencies.RawSqlCommandBuilder.Build(
            """
            CREATE TABLE IF NOT EXISTS "__EFMigrationsLock" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK___EFMigrationsLock" PRIMARY KEY,
                "Timestamp" TEXT NOT NULL
            );
            """);

    private IRelationalCommand CreateInsertLockCommand()
    {
        var timestamp = Dependencies.TypeMappingSource
            .GetMapping(typeof(DateTimeOffset))
            .GenerateSqlLiteral(DateTimeOffset.UtcNow);

        return Dependencies.RawSqlCommandBuilder.Build(
            $"""INSERT OR IGNORE INTO "__EFMigrationsLock" ("Id", "Timestamp") VALUES (1, {timestamp});""");
    }

    private IRelationalCommand CreateDeleteLockCommand()
        => Dependencies.RawSqlCommandBuilder.Build(
            """DELETE FROM "__EFMigrationsLock" WHERE "Id" = 1;""");

    private RelationalCommandParameterObject CreateRelationalCommandParameters()
        => new(
            Dependencies.Connection,
            null,
            null,
            Dependencies.CurrentContext.Context,
            Dependencies.CommandLogger,
            CommandSource.Migrations);

    private static NotSupportedException IdempotentScriptsNotSupported()
        => new(
            "The managed local provider does not support idempotent migration scripts because the engine cannot conditionally execute DDL blocks.");
}
