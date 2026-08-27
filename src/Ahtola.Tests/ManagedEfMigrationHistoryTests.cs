using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Ahtola.Data.Sqlite;

namespace Ahtola.Tests;

public sealed class ManagedEfMigrationHistoryTests
{
    [Test]
    public async Task MigrateCreatesHistoryAndDoesNotReapplyRecordedMigrations()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MigrationHistoryContext>()
            .UseAhtola(connection)
            .Options;
        await using var context = new MigrationHistoryContext(options);

        await context.Database.MigrateAsync();
        await context.Database.MigrateAsync();

        await using var history = connection.CreateCommand();
        history.CommandText =
            "SELECT COUNT(*) FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = '20260723220000_CreateHistoryItem';";
        (await history.ExecuteScalarAsync()).Should().Be(1L);

        await using (var insert = connection.CreateCommand())
        {
            insert.CommandText = "INSERT INTO \"HistoryItems\" (\"Id\") VALUES (1);";
            await insert.ExecuteNonQueryAsync();
        }

        await using var defaultValue = connection.CreateCommand();
        defaultValue.CommandText = "SELECT \"State\" FROM \"HistoryItems\" WHERE \"Id\" = 1;";
        (await defaultValue.ExecuteScalarAsync()).Should().Be("ready");
    }

    [Test]
    public void GenerateScriptIncludesHistoryBootstrapAndHistoryRow()
    {
        var options = new DbContextOptionsBuilder<MigrationHistoryContext>()
            .UseAhtola("Data Source=:memory:;Local Provider=Managed")
            .Options;
        using var context = new MigrationHistoryContext(options);

        var script = context.GetService<IMigrator>().GenerateScript();

        script.Should().Contain("CREATE TABLE IF NOT EXISTS \"__EFMigrationsHistory\"")
            .And.Contain("CREATE TABLE \"HistoryItems\"")
            .And.Contain("INSERT OR IGNORE INTO \"__EFMigrationsHistory\"");
    }

    [Test]
    public async Task GenerateIdempotentScriptCanBeAppliedRepeatedly()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MigrationHistoryContext>()
            .UseAhtola(connection)
            .Options;
        await using var context = new MigrationHistoryContext(options);

        var script = context.GetService<IMigrator>()
            .GenerateScript(options: MigrationsSqlGenerationOptions.Idempotent);

        script.Should().Contain("CREATE TABLE IF NOT EXISTS \"HistoryItems\"")
            .And.Contain("INSERT OR IGNORE INTO \"__EFMigrationsHistory\"");

        await ExecuteAsync(connection, script);
        await ExecuteAsync(connection, script);

        await using var history = connection.CreateCommand();
        history.CommandText =
            "SELECT COUNT(*) FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = '20260723220000_CreateHistoryItem';";
        (await history.ExecuteScalarAsync()).Should().Be(1L);
    }

    [Test]
    public void IdempotentGenerationRejectsShapesSqliteCannotConditionallyExecute()
    {
        var options = new DbContextOptionsBuilder<MigrationHistoryContext>()
            .UseAhtola("Data Source=:memory:;Local Provider=Managed")
            .Options;
        using var context = new MigrationHistoryContext(options);
        var generator = context.GetService<IMigrationsSqlGenerator>();

        var addColumn = () => generator.Generate(
            [
                new AddColumnOperation
                {
                    Table = "HistoryItems",
                    Name = "UnsafeToRepeat",
                    ClrType = typeof(string),
                    ColumnType = "TEXT",
                    IsNullable = true
                }
            ],
            options: MigrationsSqlGenerationOptions.Idempotent);
        addColumn.Should().Throw<NotSupportedException>()
            .WithMessage("*honest idempotent script*AddColumnOperation*");

        var rename = () => generator.Generate(
            [
                new RenameColumnOperation
                {
                    Table = "HistoryItems",
                    Name = "State",
                    NewName = "RenamedState"
                }
            ],
            options: MigrationsSqlGenerationOptions.Idempotent);
        rename.Should().Throw<NotSupportedException>()
            .WithMessage("*honest idempotent script*RenameColumnOperation*");

        var dropTable = () => generator.Generate(
            [new DropTableOperation { Name = "HistoryItems" }],
            options: MigrationsSqlGenerationOptions.Script | MigrationsSqlGenerationOptions.Idempotent);
        dropTable.Should().Throw<NotSupportedException>()
            .WithMessage("*honest standalone idempotent script*DropTableOperation*__EFMigrationsHistory guard*");

        var dropIndex = () => generator.Generate(
            [new DropIndexOperation { Name = "IX_HistoryItems_State", Table = "HistoryItems" }],
            options: MigrationsSqlGenerationOptions.Script | MigrationsSqlGenerationOptions.Idempotent);
        dropIndex.Should().Throw<NotSupportedException>()
            .WithMessage("*honest standalone idempotent script*DropIndexOperation*__EFMigrationsHistory guard*");
    }

    [Test]
    public void FullIdempotentScriptRejectsDestructiveMigrationWithoutHistoryGuard()
    {
        var options = new DbContextOptionsBuilder<UnsafeIdempotentHistoryContext>()
            .UseAhtola("Data Source=:memory:;Local Provider=Managed")
            .Options;
        using var context = new UnsafeIdempotentHistoryContext(options);

        var generate = () => context.GetService<IMigrator>()
            .GenerateScript(options: MigrationsSqlGenerationOptions.Idempotent);

        generate.Should().Throw<NotSupportedException>()
            .WithMessage("*honest standalone idempotent script*DropTableOperation*__EFMigrationsHistory guard*");
    }

    [Test]
    public async Task FileMigrationCanBeAppliedReopenedAndReverted()
    {
        var path = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            $"managed-ef-migration-{Guid.NewGuid():N}.db");
        try
        {
            await using (var connection = new SqliteConnection($"Data Source={path};Local Provider=Managed"))
            {
                await connection.OpenAsync();
                var options = new DbContextOptionsBuilder<MigrationHistoryContext>()
                    .UseAhtola(connection)
                    .Options;
                await using var context = new MigrationHistoryContext(options);
                await context.Database.MigrateAsync();
                await ExecuteAsync(connection, "INSERT INTO \"HistoryItems\" (\"Id\") VALUES (1);");
            }

            await using (var connection = new SqliteConnection($"Data Source={path};Local Provider=Managed"))
            {
                await connection.OpenAsync();
                await using var count = connection.CreateCommand();
                count.CommandText = "SELECT COUNT(*) FROM \"HistoryItems\";";
                (await count.ExecuteScalarAsync()).Should().Be(1L);

                var options = new DbContextOptionsBuilder<MigrationHistoryContext>()
                    .UseAhtola(connection)
                    .Options;
                await using var context = new MigrationHistoryContext(options);
                await context.Database.MigrateAsync("0");

                await using var table = connection.CreateCommand();
                table.CommandText =
                    "SELECT COUNT(*) FROM \"sqlite_master\" WHERE \"type\" = 'table' AND \"name\" = 'HistoryItems';";
                (await table.ExecuteScalarAsync()).Should().Be(0L);
            }
        }
        finally
        {
            DeleteDatabaseArtifacts(path);
        }
    }

    [Test]
    public async Task DatabaseMigrateAppliesTableRebuildWithEfOwnedTransaction()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<RebuildMigrationHistoryContext>()
            .UseAhtola(connection)
            .Options;
        await using var context = new RebuildMigrationHistoryContext(options);

        await context.Database.MigrateAsync("20260723223000_CreateRebuildHistoryItem");
        await ExecuteAsync(
            connection,
            "INSERT INTO \"RebuildHistoryItems\" (\"Id\", \"State\") VALUES (1, 'ready');");
        await context.Database.MigrateAsync();

        await using var item = connection.CreateCommand();
        item.CommandText = "SELECT \"State\" FROM \"RebuildHistoryItems\" WHERE \"Id\" = 1;";
        (await item.ExecuteScalarAsync()).Should().Be("ready");
        await using var history = connection.CreateCommand();
        history.CommandText =
            "SELECT COUNT(*) FROM \"__EFMigrationsHistory\" "
            + "WHERE \"MigrationId\" = '20260723224000_CheckRebuildHistoryItem';";
        (await history.ExecuteScalarAsync()).Should().Be(1L);
        await using var temporary = connection.CreateCommand();
        temporary.CommandText =
            "SELECT COUNT(*) FROM \"sqlite_master\" WHERE \"type\" = 'table' "
            + "AND \"name\" = 'ef_temp_RebuildHistoryItems';";
        (await temporary.ExecuteScalarAsync()).Should().Be(0L);
    }

    [Test]
    public async Task DatabaseMigrateRejectsCallerOwnedTransactionBeforeRebuild()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<RebuildMigrationHistoryContext>()
            .UseAhtola(connection)
            .Options;
        await using var context = new RebuildMigrationHistoryContext(options);
        await context.Database.MigrateAsync("20260723223000_CreateRebuildHistoryItem");
        await ExecuteAsync(
            connection,
            "INSERT INTO \"RebuildHistoryItems\" (\"Id\", \"State\") VALUES (1, '');");
        await using var transaction = await context.Database.BeginTransactionAsync();

        Func<Task> migrate = () => context.Database.MigrateAsync();
        await migrate.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*cannot run inside an existing transaction*foreign key enforcement*");

        context.Database.CurrentTransaction.Should().BeSameAs(transaction);
        await using var item = connection.CreateCommand();
        item.CommandText = "SELECT \"State\" FROM \"RebuildHistoryItems\" WHERE \"Id\" = 1;";
        (await item.ExecuteScalarAsync()).Should().Be("");
        await using var history = connection.CreateCommand();
        history.CommandText =
            "SELECT COUNT(*) FROM \"__EFMigrationsHistory\" "
            + "WHERE \"MigrationId\" = '20260723224000_CheckRebuildHistoryItem';";
        (await history.ExecuteScalarAsync()).Should().Be(0L);
        await transaction.RollbackAsync();
    }

    private sealed class MigrationHistoryContext(
        DbContextOptions<MigrationHistoryContext> options) : DbContext(options);

    private sealed class UnsafeIdempotentHistoryContext(
        DbContextOptions<UnsafeIdempotentHistoryContext> options) : DbContext(options);

    private sealed class RebuildMigrationHistoryContext(
        DbContextOptions<RebuildMigrationHistoryContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => ConfigureRebuildHistoryItem(modelBuilder, includeCheck: true);
    }

    [DbContext(typeof(MigrationHistoryContext))]
    [Migration("20260723220000_CreateHistoryItem")]
    public sealed class CreateHistoryItemMigration : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
            => migrationBuilder.CreateTable(
                name: "HistoryItems",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false),
                    State = table.Column<string>(
                        type: "TEXT",
                        nullable: false,
                        defaultValue: "ready")
                },
                constraints: table => table.PrimaryKey("PK_HistoryItems", item => item.Id));

        protected override void Down(MigrationBuilder migrationBuilder)
            => migrationBuilder.DropTable("HistoryItems");
    }

    [DbContext(typeof(UnsafeIdempotentHistoryContext))]
    [Migration("20260723221000_CreateObsoleteItem")]
    public sealed class CreateObsoleteItemMigration : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
            => migrationBuilder.CreateTable(
                name: "ObsoleteItems",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table => table.PrimaryKey("PK_ObsoleteItems", value => value.Id));

        protected override void Down(MigrationBuilder migrationBuilder)
            => migrationBuilder.DropTable("ObsoleteItems");
    }

    [DbContext(typeof(UnsafeIdempotentHistoryContext))]
    [Migration("20260723222000_DropObsoleteItem")]
    public sealed class DropObsoleteItemMigration : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
            => migrationBuilder.DropTable("ObsoleteItems");

        protected override void Down(MigrationBuilder migrationBuilder)
            => migrationBuilder.CreateTable(
                name: "ObsoleteItems",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table => table.PrimaryKey("PK_ObsoleteItems", value => value.Id));
    }

    [DbContext(typeof(RebuildMigrationHistoryContext))]
    [Migration("20260723223000_CreateRebuildHistoryItem")]
    public sealed class CreateRebuildHistoryItemMigration : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
            => migrationBuilder.CreateTable(
                name: "RebuildHistoryItems",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table => table.PrimaryKey("PK_RebuildHistoryItems", item => item.Id));

        protected override void Down(MigrationBuilder migrationBuilder)
            => migrationBuilder.DropTable("RebuildHistoryItems");

        protected override void BuildTargetModel(ModelBuilder modelBuilder)
            => ConfigureRebuildHistoryItem(modelBuilder, includeCheck: false);
    }

    [DbContext(typeof(RebuildMigrationHistoryContext))]
    [Migration("20260723224000_CheckRebuildHistoryItem")]
    public sealed class CheckRebuildHistoryItemMigration : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
            => migrationBuilder.AddCheckConstraint(
                name: "CK_RebuildHistoryItems_State",
                table: "RebuildHistoryItems",
                sql: "\"State\" <> ''");

        protected override void Down(MigrationBuilder migrationBuilder)
            => migrationBuilder.DropCheckConstraint(
                name: "CK_RebuildHistoryItems_State",
                table: "RebuildHistoryItems");

        protected override void BuildTargetModel(ModelBuilder modelBuilder)
            => ConfigureRebuildHistoryItem(modelBuilder, includeCheck: true);
    }

    private static void ConfigureRebuildHistoryItem(ModelBuilder modelBuilder, bool includeCheck)
    {
        var item = modelBuilder.Entity<RebuildHistoryItem>();
        item.ToTable(
            "RebuildHistoryItems",
            table =>
            {
                if (includeCheck)
                    table.HasCheckConstraint("CK_RebuildHistoryItems_State", "\"State\" <> ''");
            });
        item.HasKey(value => value.Id);
        item.Property(value => value.State).HasColumnType("TEXT");
    }

    private sealed class RebuildHistoryItem
    {
        public long Id { get; set; }

        public string State { get; set; } = "";
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static void DeleteDatabaseArtifacts(string path)
    {
        foreach (var candidate in new[] { path, path + "-wal", path + "-shm", path + "-journal" })
        {
            if (File.Exists(candidate))
                File.Delete(candidate);
        }
    }
}
