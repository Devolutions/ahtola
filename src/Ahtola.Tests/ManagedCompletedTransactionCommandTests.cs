using AwesomeAssertions;
using Ahtola.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Ahtola.Tests;

public sealed class ManagedCompletedTransactionCommandTests
{
    [Test]
    public void CommandWithCompletedTransactionReference_ExecutesInAutocommit_WhenConnectionHasNoActiveTransaction()
    {
        using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();
        connection.ExecuteNonQuery("CREATE TABLE t(id INTEGER);");

        var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO t VALUES (1);";
        command.Transaction = transaction;
        command.ExecuteNonQuery();

        transaction.Commit();

        // Simulate EF leftover: command still points at completed txn, connection has none.
        command.Transaction.Should().NotBeNull();
        connection.Transaction.Should().BeNull();

        command.CommandText = "DELETE FROM t WHERE id = 1;";
        var act = () => command.ExecuteNonQuery();
        // Preferred: treat completed txn + no active connection txn as autocommit
        // so EF migrations lock release after Commit does not fail.
        act.Should().NotThrow();
    }

    [Test]
    public void FreshCommandAfterCommit_ExecutesWithoutTransaction()
    {
        using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();
        connection.ExecuteNonQuery("CREATE TABLE t(id INTEGER); INSERT INTO t VALUES (1);");

        using (var transaction = connection.BeginTransaction())
        {
            connection.ExecuteNonQuery("INSERT INTO t VALUES (2);");
            transaction.Commit();
        }

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM t WHERE id = 1;";
        command.ExecuteNonQuery().Should().Be(1);
    }

    [Test]
    public void EnsureCreatedThenMigrate_OnFileDatabase_Succeeds()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ahtola-ef-migrate-{Guid.NewGuid():N}.db");
        try
        {
            var cs = $"Data Source={path};Local Provider=Managed";
            var options = new DbContextOptionsBuilder<FileMigrateContext>()
                .UseAhtola(cs)
                .Options;

            using (var ctx = new FileMigrateContext(options))
            {
                ctx.Database.Migrate();
            }

            using (var ctx = new FileMigrateContext(options))
            {
                ctx.Database.Migrate();
                ctx.Items.Add(new FileMigrateItem { Id = 1, Name = "x" });
                ctx.SaveChanges();
                ctx.Items.Count().Should().Be(1);
            }
        }
        finally
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                var f = path + suffix;
                if (File.Exists(f))
                    File.Delete(f);
            }
        }
    }

    [Test]
    public void Migrate_WithTransactionSuppressedDdlAndCollation_OnFileDatabase_Succeeds()
    {
        // PSU-like: NOCASE model collation + many CREATE TABLE ops (TransactionSuppressed)
        // + explicit migrations lock release after Commit.
        var path = Path.Combine(Path.GetTempPath(), $"ahtola-ef-migrate-ddl-{Guid.NewGuid():N}.db");
        try
        {
            var cs = $"Data Source={path};Local Provider=Managed";
            var options = new DbContextOptionsBuilder<DdlMigrateContext>()
                .UseAhtola(cs)
                .Options;

            using (var ctx = new DdlMigrateContext(options))
            {
                ctx.Database.Migrate();
            }

            using (var ctx = new DdlMigrateContext(options))
            {
                ctx.Database.Migrate();
                ctx.Items.Add(new DdlMigrateItem { Id = 1, Name = "x" });
                ctx.SaveChanges();
                ctx.Items.Count().Should().Be(1);
            }
        }
        finally
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                var f = path + suffix;
                if (File.Exists(f))
                    File.Delete(f);
            }
        }
    }

    private sealed class FileMigrateContext : DbContext
    {
        public FileMigrateContext(DbContextOptions<FileMigrateContext> options)
            : base(options)
        {
        }

        public DbSet<FileMigrateItem> Items => Set<FileMigrateItem>();
    }

    private sealed class FileMigrateItem
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
    }

    [DbContext(typeof(FileMigrateContext))]
    [Migration("20260101000000_FileMigrateInit")]
    public sealed class FileMigrateInit : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
            => migrationBuilder.CreateTable(
                name: "Items",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table => table.PrimaryKey("PK_Items", x => x.Id));

        protected override void Down(MigrationBuilder migrationBuilder)
            => migrationBuilder.DropTable("Items");
    }

    private sealed class DdlMigrateContext : DbContext
    {
        public DdlMigrateContext(DbContextOptions<DdlMigrateContext> options)
            : base(options)
        {
        }

        public DbSet<DdlMigrateItem> Items => Set<DdlMigrateItem>();
        public DbSet<DdlMigrateOther> Others => Set<DdlMigrateOther>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.UseCollation("NOCASE");
            modelBuilder.Entity<DdlMigrateItem>(e =>
            {
                e.Property(x => x.Name).UseCollation("NOCASE");
            });
        }
    }

    private sealed class DdlMigrateItem
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
    }

    private sealed class DdlMigrateOther
    {
        public int Id { get; set; }
        public string Label { get; set; } = "";
    }

    [DbContext(typeof(DdlMigrateContext))]
    [Migration("20260101000001_DdlMigrateInit")]
    public sealed class DdlMigrateInit : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Items",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE")
                },
                constraints: table => table.PrimaryKey("PK_Items", x => x.Id));

            migrationBuilder.CreateTable(
                name: "Others",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    Label = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table => table.PrimaryKey("PK_Others", x => x.Id));

            migrationBuilder.Sql("CREATE INDEX IF NOT EXISTS IX_Items_Name ON Items(Name);");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable("Others");
            migrationBuilder.DropTable("Items");
        }
    }
}
