using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage;
using Ahtola.Data.Sqlite;

namespace Ahtola.Tests;

public sealed class ManagedEfMigrationSurfaceTests
{
    [Test]
    public async Task FilteredIndexesAreCreatedAndPersisted()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        await connection.OpenAsync();
        await ExecuteAsync(
            connection,
            """
            CREATE TABLE "Items" ("Id" INTEGER NOT NULL PRIMARY KEY, "Rank" INTEGER NOT NULL);
            INSERT INTO "Items" VALUES (1, 0), (2, 10);
            """);

        await using var context = CreateContext(connection);
        var operation = new CreateIndexOperation
        {
            Name = "IX_Items_PositiveRank",
            Table = "Items",
            Columns = ["Rank"],
            Filter = "\"Rank\" > 0"
        };
        await ExecuteAsync(
            connection,
            context.GetService<IMigrationsSqlGenerator>().Generate(
                [operation],
                options: MigrationsSqlGenerationOptions.Idempotent));
        await ExecuteAsync(
            connection,
            context.GetService<IMigrationsSqlGenerator>().Generate(
                [operation],
                options: MigrationsSqlGenerationOptions.Idempotent));

        await using var schema = connection.CreateCommand();
        schema.CommandText =
            "SELECT \"sql\" FROM \"sqlite_master\" WHERE \"name\" = 'IX_Items_PositiveRank';";
        (await schema.ExecuteScalarAsync()).Should().Be(
            "CREATE INDEX \"IX_Items_PositiveRank\" ON \"Items\" (\"Rank\") WHERE \"Rank\" > 0");
    }

    [Test]
    public async Task StoredComputedColumnsPersistValuesAcrossReopen()
    {
        var path = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            $"managed-ef-computed-{Guid.NewGuid():N}.db");
        try
        {
            await using (var connection = new SqliteConnection($"Data Source={path};Local Provider=Managed"))
            {
                await connection.OpenAsync();
                await using var context = CreateContext(connection);

                var table = new CreateTableOperation { Name = "ComputedItems" };
                table.Columns.Add(new AddColumnOperation
                {
                    Table = table.Name,
                    Name = "Name",
                    ClrType = typeof(string),
                    ColumnType = "TEXT",
                    IsNullable = false
                });
                table.Columns.Add(new AddColumnOperation
                {
                    Table = table.Name,
                    Name = "NameLength",
                    ClrType = typeof(long),
                    ColumnType = "INTEGER",
                    IsNullable = true,
                    ComputedColumnSql = "length(\"Name\")",
                    IsStored = true
                });

                var commands = context.GetService<IMigrationsSqlGenerator>().Generate([table]);
                string.Concat(commands.Select(command => command.CommandText))
                    .Should().Contain("AS (length(\"Name\")) STORED");
                await ExecuteAsync(connection, commands);
                await ExecuteAsync(connection, "INSERT INTO \"ComputedItems\" (\"Name\") VALUES ('Ada');");
            }

            await using var reopened = new SqliteConnection($"Data Source={path};Local Provider=Managed");
            await reopened.OpenAsync();
            await using var value = reopened.CreateCommand();
            value.CommandText = "SELECT \"NameLength\" FROM \"ComputedItems\";";
            (await value.ExecuteScalarAsync()).Should().Be(3L);

            await using var schema = reopened.CreateCommand();
            schema.CommandText =
                "SELECT \"sql\" FROM \"sqlite_master\" WHERE \"type\" = 'table' AND \"name\" = 'ComputedItems';";
            (await schema.ExecuteScalarAsync()).Should().BeOfType<string>()
                .Which.Should().Contain("STORED");

        }
        finally
        {
            DeleteDatabaseArtifacts(path);
        }
    }

    [Test]
    public async Task StoredComputedColumnCanBeAddedToPopulatedFileTable()
    {
        var path = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            $"managed-ef-add-computed-{Guid.NewGuid():N}.db");
        try
        {
            await using (var connection = new SqliteConnection($"Data Source={path};Local Provider=Managed"))
            {
                await connection.OpenAsync();
                await ExecuteAsync(
                    connection,
                    """
                    CREATE TABLE "ComputedItems" (
                        "Id" INTEGER NOT NULL CONSTRAINT "PK_ComputedItems" PRIMARY KEY,
                        "Name" TEXT NOT NULL
                    );
                    INSERT INTO "ComputedItems" VALUES (1, 'Ada');
                    """);
                var options = new DbContextOptionsBuilder<StoredComputedContext>()
                    .UseAhtola(connection)
                    .Options;
                await using var context = new StoredComputedContext(options);
                var operation = new AddColumnOperation
                {
                    Table = "ComputedItems",
                    Name = "NameLength",
                    ClrType = typeof(long),
                    ColumnType = "INTEGER",
                    IsNullable = false,
                    ComputedColumnSql = "length(\"Name\")",
                    IsStored = true
                };
                var commands = context.GetService<IMigrationsSqlGenerator>().Generate(
                    [operation],
                    context.GetService<IDesignTimeModel>().Model,
                    MigrationsSqlGenerationOptions.Idempotent);

                string.Concat(commands.Select(command => command.CommandText))
                    .Should().Contain("ef_temp_ComputedItems")
                    .And.Contain("STORED");
                await context.GetService<IMigrationCommandExecutor>().ExecuteNonQueryAsync(
                    commands,
                    context.GetService<IRelationalConnection>());
                await context.GetService<IMigrationCommandExecutor>().ExecuteNonQueryAsync(
                    commands,
                    context.GetService<IRelationalConnection>());
                await ExecuteAsync(
                    connection,
                    "UPDATE \"ComputedItems\" SET \"Name\" = 'Grace' WHERE \"Id\" = 1;");
            }

            await using var reopened = new SqliteConnection($"Data Source={path};Local Provider=Managed");
            await reopened.OpenAsync();
            await using var value = reopened.CreateCommand();
            value.CommandText = "SELECT \"NameLength\" FROM \"ComputedItems\" WHERE \"Id\" = 1;";
            (await value.ExecuteScalarAsync()).Should().Be(5L);
        }
        finally
        {
            DeleteDatabaseArtifacts(path);
        }
    }

    [Test]
    public void StandaloneRebuildScriptsAreRejectedWithoutHistoryGuard()
    {
        using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        using var context = new StoredComputedContext(
            new DbContextOptionsBuilder<StoredComputedContext>()
                .UseAhtola(connection)
                .Options);
        var operation = new AddColumnOperation
        {
            Table = "ComputedItems",
            Name = "NameLength",
            ClrType = typeof(long),
            ColumnType = "INTEGER",
            IsNullable = false,
            ComputedColumnSql = "length(\"Name\")",
            IsStored = true
        };

        var generate = () => context.GetService<IMigrationsSqlGenerator>().Generate(
            [operation],
            context.GetService<IDesignTimeModel>().Model,
            MigrationsSqlGenerationOptions.Script | MigrationsSqlGenerationOptions.Idempotent);

        generate.Should().Throw<NotSupportedException>()
            .WithMessage("*honest standalone idempotent script*__EFMigrationsHistory guard*");
        connection.State.Should().Be(System.Data.ConnectionState.Closed);
    }

    [Test]
    public async Task UnsupportedGeneratedExpressionsFailBeforeAnyMutation()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        await connection.OpenAsync();
        await using var context = CreateContext(connection);

        var table = new CreateTableOperation { Name = "ShouldNotExist" };
        table.Columns.Add(new AddColumnOperation
        {
            Table = table.Name,
            Name = "Id",
            ClrType = typeof(long),
            ColumnType = "INTEGER",
            IsNullable = false
        });
        table.Columns.Add(new AddColumnOperation
        {
            Table = table.Name,
            Name = "Bad",
            ClrType = typeof(long),
            ColumnType = "INTEGER",
            IsNullable = true,
            ComputedColumnSql = "(SELECT 1)",
            IsStored = true
        });

        var generate = () => context.GetService<IMigrationsSqlGenerator>().Generate([table]);
        generate.Should().Throw<NotSupportedException>().WithMessage("*computed column*");

        await using var verify = connection.CreateCommand();
        verify.CommandText =
            "SELECT COUNT(*) FROM \"sqlite_master\" WHERE \"name\" = 'ShouldNotExist';";
        (await verify.ExecuteScalarAsync()).Should().Be(0L);
    }

    [Test]
    public async Task AttachedSchemaRenamesUseQualifiedSourceNames()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        await connection.OpenAsync();
        await ExecuteAsync(
            connection,
            """
            ATTACH DATABASE ':memory:' AS "aux";
            CREATE TABLE "aux"."Items" ("OldName" TEXT NOT NULL);
            INSERT INTO "aux"."Items" VALUES ('preserved');
            """);
        await using var context = CreateContext(connection);
        var generator = context.GetService<IMigrationsSqlGenerator>();

        var operations = new MigrationOperation[]
        {
            new RenameColumnOperation
            {
                Schema = "aux",
                Table = "Items",
                Name = "OldName",
                NewName = "Name"
            },
            new RenameTableOperation
            {
                Schema = "aux",
                Name = "Items",
                NewName = "RenamedItems"
            }
        };
        var first = generator.Generate(operations);
        var second = generator.Generate(operations);
        first.Select(command => command.CommandText)
            .Should().Equal(second.Select(command => command.CommandText));
        await ExecuteAsync(connection, first);

        await using var value = connection.CreateCommand();
        value.CommandText = "SELECT \"Name\" FROM \"aux\".\"RenamedItems\";";
        (await value.ExecuteScalarAsync()).Should().Be("preserved");
    }

    [Test]
    public async Task AttachedSchemaRebuildPreservesTriggers()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        await connection.OpenAsync();
        await ExecuteAsync(
            connection,
            """
            PRAGMA foreign_keys = ON;
            ATTACH DATABASE ':memory:' AS "aux";
            CREATE TABLE "aux"."Items" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_Items" PRIMARY KEY,
                "Name" TEXT NOT NULL
            );
            CREATE TABLE "aux"."Audit" ("Value" TEXT NOT NULL);
            CREATE TRIGGER "aux"."TR_Items_Update" AFTER UPDATE ON "Items"
            BEGIN
                INSERT INTO "Audit" VALUES (NEW."Name");
            END;
            INSERT INTO "aux"."Items" VALUES (1, 'before');
            """);
        var options = new DbContextOptionsBuilder<AttachedCheckedSurfaceContext>()
            .UseAhtola(connection)
            .Options;
        await using var context = new AttachedCheckedSurfaceContext(options);
        var commands = context.GetService<IMigrationsSqlGenerator>().Generate(
            [
                new AddCheckConstraintOperation
                {
                    Schema = "aux",
                    Name = "CK_Items_Name",
                    Table = "Items",
                    Sql = "\"Name\" <> ''"
                }
            ],
            context.GetService<IDesignTimeModel>().Model);

        await context.GetService<IMigrationCommandExecutor>().ExecuteNonQueryAsync(
            commands,
            context.GetService<IRelationalConnection>());
        await ExecuteAsync(
            connection,
            "UPDATE \"aux\".\"Items\" SET \"Name\" = 'after' WHERE \"Id\" = 1;");

        await using var audit = connection.CreateCommand();
        audit.CommandText = "SELECT \"Value\" FROM \"aux\".\"Audit\";";
        (await audit.ExecuteScalarAsync()).Should().Be("after");
        await using var foreignKeys = connection.CreateCommand();
        foreignKeys.CommandText = "PRAGMA foreign_keys;";
        (await foreignKeys.ExecuteScalarAsync()).Should().Be(1L);
    }

    [Test]
    public async Task SameNamedTablesInAttachedSchemasRebuildIndependently()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        await connection.OpenAsync();
        await ExecuteAsync(
            connection,
            """
            ATTACH DATABASE ':memory:' AS "aux";
            ATTACH DATABASE ':memory:' AS "archive";
            CREATE TABLE "aux"."Items" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_Items" PRIMARY KEY,
                "Name" TEXT NOT NULL
            );
            CREATE TABLE "archive"."Items" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_Items" PRIMARY KEY,
                "Name" TEXT NOT NULL
            );
            INSERT INTO "aux"."Items" VALUES (1, 'aux-value');
            INSERT INTO "archive"."Items" VALUES (1, 'archive-value');
            """);
        var options = new DbContextOptionsBuilder<DualAttachedCheckedSurfaceContext>()
            .UseAhtola(connection)
            .Options;
        await using var context = new DualAttachedCheckedSurfaceContext(options);
        var commands = context.GetService<IMigrationsSqlGenerator>().Generate(
            [
                new SqlOperation
                {
                    Sql = "SELECT 'INSERT INTO \"ef_temp_Items\" FROM \"Items\"';"
                },
                new AddCheckConstraintOperation
                {
                    Schema = "aux",
                    Name = "CK_AuxItems_Name",
                    Table = "Items",
                    Sql = "\"Name\" <> ''"
                },
                new AddCheckConstraintOperation
                {
                    Schema = "archive",
                    Name = "CK_ArchiveItems_Name",
                    Table = "Items",
                    Sql = "\"Name\" <> ''"
                }
            ],
            context.GetService<IDesignTimeModel>().Model);

        var sql = string.Concat(commands.Select(command => command.CommandText));
        sql.Should().Contain("INSERT INTO \"aux\".\"ef_temp_Items\"")
            .And.Contain("FROM \"aux\".\"Items\"")
            .And.Contain("INSERT INTO \"archive\".\"ef_temp_Items\"")
            .And.Contain("FROM \"archive\".\"Items\"");
        commands.Single(command => command.CommandText.Contains("SELECT 'INSERT", StringComparison.Ordinal))
            .CommandText.Should().Contain("INSERT INTO \"ef_temp_Items\" FROM \"Items\"")
            .And.NotContain("\"aux\".\"ef_temp_Items\"")
            .And.NotContain("\"archive\".\"ef_temp_Items\"");
        await ExecuteAsync(connection, commands);

        await using var auxValue = connection.CreateCommand();
        auxValue.CommandText = "SELECT \"Name\" FROM \"aux\".\"Items\";";
        (await auxValue.ExecuteScalarAsync()).Should().Be("aux-value");
        await using var archiveValue = connection.CreateCommand();
        archiveValue.CommandText = "SELECT \"Name\" FROM \"archive\".\"Items\";";
        (await archiveValue.ExecuteScalarAsync()).Should().Be("archive-value");

        await using var auxSchema = connection.CreateCommand();
        auxSchema.CommandText =
            "SELECT \"sql\" FROM \"aux\".\"sqlite_master\" WHERE \"name\" = 'Items';";
        (await auxSchema.ExecuteScalarAsync()).Should().BeOfType<string>()
            .Which.Should().Contain("CK_AuxItems_Name");
        await using var archiveSchema = connection.CreateCommand();
        archiveSchema.CommandText =
            "SELECT \"sql\" FROM \"archive\".\"sqlite_master\" WHERE \"name\" = 'Items';";
        (await archiveSchema.ExecuteScalarAsync()).Should().BeOfType<string>()
            .Which.Should().Contain("CK_ArchiveItems_Name");
    }

    [Test]
    public async Task RenameThenRebuildCapturesTriggersAfterRename()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        await connection.OpenAsync();
        await ExecuteAsync(
            connection,
            """
            CREATE TABLE "Items" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_Items" PRIMARY KEY,
                "Name" TEXT NOT NULL
            );
            CREATE TABLE "Audit" ("Value" TEXT NOT NULL);
            CREATE TRIGGER "TR_Items_Update" AFTER UPDATE ON "Items"
            BEGIN
                INSERT INTO "Audit" VALUES (NEW."Name");
            END;
            INSERT INTO "Items" VALUES (1, 'before');
            """);
        var options = new DbContextOptionsBuilder<RenamedCheckedSurfaceContext>()
            .UseAhtola(connection)
            .Options;
        await using var context = new RenamedCheckedSurfaceContext(options);
        var commands = context.GetService<IMigrationsSqlGenerator>().Generate(
            [
                new RenameTableOperation
                {
                    Name = "Items",
                    NewName = "RenamedItems"
                },
                new AddCheckConstraintOperation
                {
                    Name = "CK_RenamedItems_Name",
                    Table = "RenamedItems",
                    Sql = "\"Name\" <> ''"
                }
            ],
            context.GetService<IDesignTimeModel>().Model);

        await context.GetService<IMigrationCommandExecutor>().ExecuteNonQueryAsync(
            commands,
            context.GetService<IRelationalConnection>());
        await ExecuteAsync(
            connection,
            "UPDATE \"RenamedItems\" SET \"Name\" = 'after' WHERE \"Id\" = 1;");

        await using var audit = connection.CreateCommand();
        audit.CommandText = "SELECT \"Value\" FROM \"Audit\";";
        (await audit.ExecuteScalarAsync()).Should().Be("after");
    }

    [Test]
    public async Task TableRebuildSqlIsDeterministic()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<CheckedSurfaceContext>()
            .UseAhtola(connection)
            .Options;
        await using var context = new CheckedSurfaceContext(options);
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var model = context.GetService<IDesignTimeModel>().Model;
        var operations = new MigrationOperation[]
        {
            new AddCheckConstraintOperation
            {
                Name = "CK_Items_Name",
                Table = "Items",
                Sql = "\"Name\" <> ''"
            }
        };

        var first = generator.Generate(operations, model);
        var second = generator.Generate(operations, model);

        first.Select(command => command.CommandText)
            .Should().Equal(second.Select(command => command.CommandText));
        string.Concat(first.Select(command => command.CommandText))
            .Should().Contain("ef_temp_Items");
    }

    [Test]
    public async Task FailedTableRebuildRollsBackDataAndSchema()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        await connection.OpenAsync();
        await ExecuteAsync(
            connection,
            """
            CREATE TABLE "Items" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_Items" PRIMARY KEY,
                "Name" TEXT NOT NULL DEFAULT ''
            );
            CREATE INDEX "IX_Items_Name" ON "Items" ("Name");
            CREATE TABLE "Audit" ("Value" TEXT NOT NULL);
            CREATE TRIGGER "TR_Items_Update" AFTER UPDATE ON "Items"
            BEGIN
                INSERT INTO "Audit" VALUES (NEW."Name");
            END;
            INSERT INTO "Items" VALUES (1, '');
            """);

        var options = new DbContextOptionsBuilder<CheckedSurfaceContext>()
            .UseAhtola(connection)
            .Options;
        await using var context = new CheckedSurfaceContext(options);
        var commands = context.GetService<IMigrationsSqlGenerator>().Generate(
        [
            new AddCheckConstraintOperation
            {
                Name = "CK_Items_Name",
                Table = "Items",
                Sql = "\"Name\" <> ''"
            }
        ], context.GetService<IDesignTimeModel>().Model);

        Func<Task> apply = () => context.GetService<IMigrationCommandExecutor>()
            .ExecuteNonQueryAsync(commands, context.GetService<IRelationalConnection>());
        await apply.Should().ThrowAsync<Exception>();

        await using var verify = connection.CreateCommand();
        verify.CommandText =
            "SELECT (SELECT COUNT(*) FROM \"Items\") + "
            + "(SELECT COUNT(*) FROM \"sqlite_master\" WHERE \"name\" = 'IX_Items_Name') + "
            + "(SELECT COUNT(*) FROM \"sqlite_master\" WHERE \"name\" = 'TR_Items_Update') + "
            + "(SELECT COUNT(*) FROM \"sqlite_master\" WHERE \"name\" = 'ef_temp_Items');";
        (await verify.ExecuteScalarAsync()).Should().Be(3L);
    }

    [Test]
    public async Task RebuildRestoresIndexDependentTriggerAfterIndexes()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        await connection.OpenAsync();
        await ExecuteAsync(
            connection,
            """
            CREATE TABLE "Items" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_Items" PRIMARY KEY,
                "Name" TEXT NOT NULL DEFAULT ''
            );
            CREATE INDEX "IX_Items_Name" ON "Items" ("Name");
            CREATE TABLE "Audit" ("Value" TEXT NOT NULL);
            CREATE TRIGGER "TR_Items_Indexed" AFTER UPDATE ON "Items"
            BEGIN
                INSERT INTO "Audit"
                SELECT "Name" FROM "Items" INDEXED BY "IX_Items_Name" WHERE "Id" = NEW."Id";
            END;
            INSERT INTO "Items" VALUES (1, 'before');
            """);
        var options = new DbContextOptionsBuilder<CheckedSurfaceContext>()
            .UseAhtola(connection)
            .Options;
        await using var context = new CheckedSurfaceContext(options);
        var commands = context.GetService<IMigrationsSqlGenerator>().Generate(
            [
                new AddCheckConstraintOperation
                {
                    Name = "CK_Items_Name",
                    Table = "Items",
                    Sql = "\"Name\" <> ''"
                }
            ],
            context.GetService<IDesignTimeModel>().Model);

        await context.GetService<IMigrationCommandExecutor>()
            .ExecuteNonQueryAsync(commands, context.GetService<IRelationalConnection>());
        await ExecuteAsync(connection, "UPDATE \"Items\" SET \"Name\" = 'after' WHERE \"Id\" = 1;");

        await using var audit = connection.CreateCommand();
        audit.CommandText = "SELECT \"Value\" FROM \"Audit\";";
        (await audit.ExecuteScalarAsync()).Should().Be("after");
    }

    [Test]
    public async Task DroppingTriggerReferencedColumnRollsBackEntireRebuild()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        await connection.OpenAsync();
        await ExecuteAsync(
            connection,
            """
            PRAGMA foreign_keys = ON;
            CREATE TABLE "Items" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_Items" PRIMARY KEY,
                "Name" TEXT NOT NULL,
                "Legacy" TEXT NOT NULL
            );
            CREATE TABLE "Audit" ("Value" TEXT NOT NULL);
            CREATE TRIGGER "TR_Items_Legacy" AFTER UPDATE ON "Items"
            BEGIN
                INSERT INTO "Audit" VALUES (NEW."Legacy");
            END;
            INSERT INTO "Items" VALUES (1, 'before', 'legacy-before');
            """);
        var options = new DbContextOptionsBuilder<DropColumnSurfaceContext>()
            .UseAhtola(connection)
            .Options;
        await using var context = new DropColumnSurfaceContext(options);
        var commands = context.GetService<IMigrationsSqlGenerator>().Generate(
            [
                new DropColumnOperation
                {
                    Table = "Items",
                    Name = "Legacy"
                }
            ],
            context.GetService<IDesignTimeModel>().Model);

        Func<Task> apply = () => context.GetService<IMigrationCommandExecutor>()
            .ExecuteNonQueryAsync(commands, context.GetService<IRelationalConnection>());
        await apply.Should().ThrowAsync<Exception>().WithMessage("*no such column*Legacy*");

        await using var foreignKeys = connection.CreateCommand();
        foreignKeys.CommandText = "PRAGMA foreign_keys;";
        (await foreignKeys.ExecuteScalarAsync()).Should().Be(1L);
        await using var preserved = connection.CreateCommand();
        preserved.CommandText = "SELECT \"Legacy\" FROM \"Items\" WHERE \"Id\" = 1;";
        (await preserved.ExecuteScalarAsync()).Should().Be("legacy-before");
        await ExecuteAsync(
            connection,
            "UPDATE \"Items\" SET \"Legacy\" = 'legacy-after' WHERE \"Id\" = 1;");
        await using var audit = connection.CreateCommand();
        audit.CommandText = "SELECT \"Value\" FROM \"Audit\";";
        (await audit.ExecuteScalarAsync()).Should().Be("legacy-after");
        await using var schema = connection.CreateCommand();
        schema.CommandText =
            "SELECT (SELECT COUNT(*) FROM \"sqlite_master\" WHERE \"name\" = 'TR_Items_Legacy') + "
            + "(SELECT COUNT(*) FROM \"sqlite_master\" WHERE \"name\" = 'ef_temp_Items');";
        (await schema.ExecuteScalarAsync()).Should().Be(1L);
    }

    [Test]
    public async Task RebuildInsideExistingTransactionFailsBeforeMutatingReferencedTable()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        await connection.OpenAsync();
        await ExecuteAsync(
            connection,
            """
            PRAGMA foreign_keys = ON;
            CREATE TABLE "Parents" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_Parents" PRIMARY KEY,
                "Name" TEXT NOT NULL,
                "Legacy" TEXT NOT NULL
            );
            CREATE TABLE "Children" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_Children" PRIMARY KEY,
                "ParentId" INTEGER NOT NULL,
                CONSTRAINT "FK_Children_Parents" FOREIGN KEY ("ParentId") REFERENCES "Parents" ("Id")
            );
            INSERT INTO "Parents" VALUES (1, 'parent', 'legacy-before');
            INSERT INTO "Children" VALUES (1, 1);
            """);
        var options = new DbContextOptionsBuilder<AmbientTransactionSurfaceContext>()
            .UseAhtola(connection)
            .Options;
        await using var context = new AmbientTransactionSurfaceContext(options);
        var commands = context.GetService<IMigrationsSqlGenerator>().Generate(
            [
                new DropColumnOperation
                {
                    Table = "Parents",
                    Name = "Legacy"
                }
            ],
            context.GetService<IDesignTimeModel>().Model);
        await using var transaction = await context.Database.BeginTransactionAsync();

        Func<Task> apply = () => context.GetService<IMigrationCommandExecutor>()
            .ExecuteNonQueryAsync(commands, context.GetService<IRelationalConnection>());
        await apply.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*cannot run inside an existing transaction*foreign key enforcement*");

        context.Database.CurrentTransaction.Should().BeSameAs(transaction);
        await using var foreignKeys = connection.CreateCommand();
        foreignKeys.CommandText = "PRAGMA foreign_keys;";
        (await foreignKeys.ExecuteScalarAsync()).Should().Be(1L);
        await using var parent = connection.CreateCommand();
        parent.CommandText = "SELECT \"Legacy\" FROM \"Parents\" WHERE \"Id\" = 1;";
        (await parent.ExecuteScalarAsync()).Should().Be("legacy-before");
        await using var child = connection.CreateCommand();
        child.CommandText = "SELECT COUNT(*) FROM \"Children\" WHERE \"ParentId\" = 1;";
        (await child.ExecuteScalarAsync()).Should().Be(1L);
        await ExecuteAsync(
            connection,
            "UPDATE \"Parents\" SET \"Legacy\" = 'legacy-after' WHERE \"Id\" = 1;");
        await transaction.RollbackAsync();
    }

    [Test]
    public async Task ForeignKeyCheckRollsBackRebuildWhenPreexistingOrphanExists()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        await connection.OpenAsync();
        await ExecuteAsync(
            connection,
            """
            PRAGMA foreign_keys = OFF;
            CREATE TABLE "Parents" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_Parents" PRIMARY KEY,
                "Name" TEXT NOT NULL,
                "Legacy" TEXT NOT NULL
            );
            CREATE TABLE "Children" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_Children" PRIMARY KEY,
                "ParentId" INTEGER NOT NULL,
                CONSTRAINT "FK_Children_Parents" FOREIGN KEY ("ParentId") REFERENCES "Parents" ("Id")
            );
            INSERT INTO "Parents" VALUES (1, 'parent', 'legacy-before');
            INSERT INTO "Children" VALUES (1, 999);
            PRAGMA foreign_keys = ON;
            """);
        var options = new DbContextOptionsBuilder<AmbientTransactionSurfaceContext>()
            .UseAhtola(connection)
            .Options;
        await using var context = new AmbientTransactionSurfaceContext(options);
        var commands = context.GetService<IMigrationsSqlGenerator>().Generate(
            [
                new DropColumnOperation
                {
                    Table = "Parents",
                    Name = "Legacy"
                }
            ],
            context.GetService<IDesignTimeModel>().Model);

        Func<Task> apply = () => context.GetService<IMigrationCommandExecutor>()
            .ExecuteNonQueryAsync(commands, context.GetService<IRelationalConnection>());
        await apply.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*foreign key check*Children*Parents*");

        await using var foreignKeys = connection.CreateCommand();
        foreignKeys.CommandText = "PRAGMA foreign_keys;";
        (await foreignKeys.ExecuteScalarAsync()).Should().Be(1L);
        await using var parent = connection.CreateCommand();
        parent.CommandText = "SELECT \"Legacy\" FROM \"Parents\" WHERE \"Id\" = 1;";
        (await parent.ExecuteScalarAsync()).Should().Be("legacy-before");
        await using var child = connection.CreateCommand();
        child.CommandText = "SELECT \"ParentId\" FROM \"Children\" WHERE \"Id\" = 1;";
        (await child.ExecuteScalarAsync()).Should().Be(999L);
        await using var temporary = connection.CreateCommand();
        temporary.CommandText =
            "SELECT COUNT(*) FROM \"sqlite_master\" WHERE \"type\" = 'table' AND \"name\" = 'ef_temp_Parents';";
        (await temporary.ExecuteScalarAsync()).Should().Be(0L);
    }

    private static SurfaceContext CreateContext(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<SurfaceContext>()
            .UseAhtola(connection)
            .Options;
        return new SurfaceContext(options);
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        IReadOnlyList<MigrationCommand> commands)
    {
        foreach (var command in commands)
            await ExecuteAsync(connection, command.CommandText);
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private sealed class SurfaceContext(DbContextOptions<SurfaceContext> options) : DbContext(options);

    private sealed class StoredComputedContext(
        DbContextOptions<StoredComputedContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<StoredComputedItem>(entity =>
            {
                entity.ToTable("ComputedItems");
                entity.Property(item => item.NameLength)
                    .HasComputedColumnSql("length(\"Name\")", stored: true);
            });
        }
    }

    private sealed class CheckedSurfaceContext(
        DbContextOptions<CheckedSurfaceContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<CheckedItem>(entity =>
            {
                entity.ToTable(
                    "Items",
                    table => table.HasCheckConstraint("CK_Items_Name", "\"Name\" <> ''"));
                entity.HasIndex(item => item.Name).HasDatabaseName("IX_Items_Name");
                entity.Property(item => item.Name).HasDefaultValue("");
            });
        }
    }

    private sealed class AttachedCheckedSurfaceContext(
        DbContextOptions<AttachedCheckedSurfaceContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<CheckedItem>(entity =>
            {
                entity.ToTable(
                    "Items",
                    "aux",
                    table => table.HasCheckConstraint("CK_Items_Name", "\"Name\" <> ''"));
                entity.Property(item => item.Name);
            });
        }
    }

    private sealed class DualAttachedCheckedSurfaceContext(
        DbContextOptions<DualAttachedCheckedSurfaceContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<AuxCheckedItem>(entity =>
            {
                entity.ToTable(
                    "Items",
                    "aux",
                    table => table.HasCheckConstraint("CK_AuxItems_Name", "\"Name\" <> ''"));
                entity.Property(item => item.Name);
            });
            modelBuilder.Entity<ArchiveCheckedItem>(entity =>
            {
                entity.ToTable(
                    "Items",
                    "archive",
                    table => table.HasCheckConstraint("CK_ArchiveItems_Name", "\"Name\" <> ''"));
                entity.Property(item => item.Name);
            });
        }
    }

    private sealed class RenamedCheckedSurfaceContext(
        DbContextOptions<RenamedCheckedSurfaceContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<CheckedItem>(entity =>
            {
                entity.ToTable(
                    "RenamedItems",
                    table => table.HasCheckConstraint("CK_RenamedItems_Name", "\"Name\" <> ''"));
                entity.Property(item => item.Name);
            });
        }
    }

    private sealed class DropColumnSurfaceContext(
        DbContextOptions<DropColumnSurfaceContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<DropColumnItem>().ToTable("Items");
    }

    private sealed class DropColumnItem
    {
        public long Id { get; set; }

        public string Name { get; set; } = "";
    }

    private sealed class AmbientTransactionSurfaceContext(
        DbContextOptions<AmbientTransactionSurfaceContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<AmbientParent>(entity => entity.ToTable("Parents"));
            modelBuilder.Entity<AmbientChild>(entity =>
            {
                entity.ToTable("Children");
                entity.HasOne(child => child.Parent)
                    .WithMany(parent => parent.Children)
                    .HasForeignKey(child => child.ParentId);
            });
        }
    }

    private sealed class AmbientParent
    {
        public long Id { get; set; }

        public string Name { get; set; } = "";

        public ICollection<AmbientChild> Children { get; set; } = [];
    }

    private sealed class AmbientChild
    {
        public long Id { get; set; }

        public long ParentId { get; set; }

        public AmbientParent Parent { get; set; } = null!;
    }

    private sealed class AuxCheckedItem
    {
        public long Id { get; set; }

        public string Name { get; set; } = "";
    }

    private sealed class ArchiveCheckedItem
    {
        public long Id { get; set; }

        public string Name { get; set; } = "";
    }

    private sealed class CheckedItem
    {
        public long Id { get; set; }

        public string Name { get; set; } = "";
    }

    private sealed class StoredComputedItem
    {
        public long Id { get; set; }

        public string Name { get; set; } = "";

        public long NameLength { get; set; }
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
