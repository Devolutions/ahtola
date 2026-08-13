using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

public sealed class ManagedSchemaDefinitionRecoverySafetyTests
{
    private const string Aes256Key = "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F";

    [Test]
    public void StatementLevelAfterTriggerCommitsThroughWalFailureAndRunsAfterReopen()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        const string path = "trigger-recovery.db";

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE events(id INTEGER PRIMARY KEY);");
            Execute(connection, "CREATE TABLE audit(note TEXT);");

            faults.FailNext(FileSystemOperation.SetLength);
            Assert.Throws<EmbeddedPostCommitMaintenanceException>(() => Execute(
                connection,
                "CREATE TRIGGER events_audit AFTER INSERT ON events BEGIN INSERT INTO audit VALUES ('created'); END;"));
        }

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopenedConnection = reopened.Connect();
        Execute(reopenedConnection, "INSERT INTO events VALUES (1);");
        Scalar(reopenedConnection, "SELECT note FROM audit;").Should().Be("created");
    }

    [Test]
    public void RuntimeDependentViewIsRejectedBeforePublishingCatalogOrPages()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "unsupported-view-definition.db";

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE entries(value INTEGER);");
            Execute(connection, "CREATE TABLE audit(value INTEGER);");
            Execute(connection, "INSERT INTO entries VALUES (7);");
            connection.RegisterScalarFunction("CUSTOM_ABS", 1, arguments => arguments[0]);

            Assert.Throws<EmbeddedSqlException>(() => Execute(
                connection,
                "CREATE VIEW function_entries AS SELECT CUSTOM_ABS(value) AS value FROM entries;"))!
                .Message.Should().Contain("function CUSTOM_ABS()");
            Assert.Throws<EmbeddedSqlException>(() => Execute(
                connection,
                "CREATE TRIGGER function_trigger AFTER INSERT ON entries BEGIN INSERT INTO audit VALUES (CUSTOM_ABS(1)); END"))!
                .Message.Should().Contain("function CUSTOM_ABS()");

            ScalarInteger(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'view';").Should().Be(0);
            ScalarInteger(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'trigger';").Should().Be(0);
        }

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopenedConnection = reopened.Connect();
        ScalarInteger(reopenedConnection, "SELECT value FROM entries;").Should().Be(7);
        ScalarInteger(reopenedConnection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'view';").Should().Be(0);
        ScalarInteger(reopenedConnection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'trigger';").Should().Be(0);
    }

    [Test]
    public void BuiltinFunctionViewAndTriggerPersistAcrossReopen()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "builtin-function-schema.db";

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE products(name TEXT, price REAL);");
            Execute(connection, "CREATE TABLE audit(note TEXT);");
            Execute(connection, "INSERT INTO products VALUES ('widget', 10.0), ('gadget', 20.0), ('sprocket', 30.0);");
            // Mirrors the classic Northwind view 'Products Above Average Price'.
            Execute(
                connection,
                """
                CREATE VIEW above_average AS
                SELECT upper(name) AS name, price FROM products
                WHERE price > (SELECT avg(price) FROM products);
                """);
            Execute(
                connection,
                "CREATE TRIGGER products_audit AFTER INSERT ON products BEGIN INSERT INTO audit VALUES (trim(NEW.name)); END");
            Execute(
                connection,
                """
                CREATE VIEW running_total AS
                SELECT name, sum(price) OVER (ORDER BY price) AS running FROM products;
                """);
        }

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopenedConnection = reopened.Connect();
        Scalar(reopenedConnection, "SELECT name FROM above_average;").Should().Be("SPROCKET");
        Execute(reopenedConnection, "INSERT INTO products VALUES ('  cog  ', 5.0);");
        Scalar(reopenedConnection, "SELECT note FROM audit;").Should().Be("cog");
        // Includes the post-reopen 'cog' row (5.0): 5 + 10 + 20.
        ScalarInteger(reopenedConnection, "SELECT CAST(running AS INTEGER) FROM running_total WHERE name = 'gadget';").Should().Be(35);
    }

    [Test]
    public void BuiltinFunctionViewStillRejectsOtherRuntimeDependencies()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "builtin-view-remaining-dependencies.db";

        using var database = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE entries(value INTEGER);");

        using (var statement = connection.Prepare("CREATE VIEW bound AS SELECT abs(?1) AS value FROM entries;"))
        {
            statement.Bind(1, SqlValue.Integer(1));
            var create = () => statement.Step();
            create.Should().Throw<EmbeddedSqlException>()
                .WithMessage("*cannot persist view 'bound' because it uses a bind parameter*");
        }

        Assert.Throws<EmbeddedSqlException>(() => Execute(
            connection,
            "CREATE VIEW unknown_fn AS SELECT no_such_fn(value) AS value FROM entries;"))!
            .Message.Should().Contain("function NO_SUCH_FN()");

        ScalarInteger(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'view';").Should().Be(0);
    }

    [Test]
    public void BuiltinFunctionNameSetMatchesEvaluatorDispatch()
    {
        using var database = EmbeddedDatabase.OpenFile("parity.db", new InMemoryFileSystem());
        using var connection = database.Connect();

        foreach (var name in SqliteBuiltinFunctions.All)
        {
            if (SqliteBuiltinFunctions.IsWindowOnly(name))
                continue;

            try
            {
                using var statement = connection.Prepare($"SELECT {name}(1);");
                while (statement.Step() == StatementStepResult.Row)
                {
                }
            }
            catch (EmbeddedSqlException exception)
            {
                // Arity and context errors are fine: they prove the engine dispatched the
                // name. Only "no such function" means the persistence allow-list drifted.
                exception.Message.Should().NotContain(
                    $"no such function: {name}",
                    because: $"{name} is allow-listed for persisted schema but the engine does not implement it");
            }
        }
    }

    [Test]
    public void CorruptedPersistedTriggerDefinitionFailsClosedDuringReopen()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "corrupted-trigger-definition.db";

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE events(id INTEGER PRIMARY KEY);");
            Execute(connection, "CREATE TABLE audit(note TEXT);");
            Execute(
                connection,
                "CREATE TRIGGER events_audit AFTER INSERT ON events BEGIN INSERT INTO audit VALUES ('created'); END;");
        }

        CorruptTriggerSql(fileSystem, path);

        Assert.Throws<EmbeddedSqlException>(() => EmbeddedDatabase.OpenFile(path, fileSystem));
    }

    [Test]
    public void EncryptedReadOnlyReopenExecutesPersistedTriggerAndRefusesMutation()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "encrypted-read-only-trigger.db";

        using (var encryption = AhtolaEncryptionOptions.FromHex(AhtolaEncryptionCipher.Aes256Gcm, Aes256Key))
        using (var database = EmbeddedDatabase.OpenFile(path, new AhtolaEncryptionFileSystem(fileSystem, encryption)))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE events(id INTEGER PRIMARY KEY);");
            Execute(connection, "CREATE TABLE audit(note TEXT);");
            Execute(
                connection,
                "CREATE TRIGGER events_audit AFTER INSERT ON events BEGIN INSERT INTO audit VALUES ('created'); END;");
            Execute(connection, "INSERT INTO events VALUES (1);");
        }

        using var reopenEncryption = AhtolaEncryptionOptions.FromHex(AhtolaEncryptionCipher.Aes256Gcm, Aes256Key);
        using var reopened = EmbeddedDatabase.OpenFile(
            path,
            new AhtolaEncryptionFileSystem(fileSystem, reopenEncryption),
            readOnly: true);
        using var reopenedConnection = reopened.Connect();
        Scalar(reopenedConnection, "SELECT note FROM audit;").Should().Be("created");

        Assert.Throws<EmbeddedSqlException>(() => Execute(reopenedConnection, "INSERT INTO events VALUES (2);"))!
            .Message.Should().Be("attempt to write a readonly database");
        ScalarInteger(reopenedConnection, "SELECT COUNT(*) FROM events;").Should().Be(1);
    }

    [Test]
    public void OversizedEncryptedSchemaSqlPersistsThroughOverflowPages()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "oversized-encrypted-schema.db";
        var oversizedDefault = new string('x', 5000);

        using (var encryption = AhtolaEncryptionOptions.FromHex(AhtolaEncryptionCipher.Aes256Gcm, Aes256Key))
        using (var encryptedFileSystem = new AhtolaEncryptionFileSystem(fileSystem, encryption))
        using (var database = EmbeddedDatabase.OpenFile(path, encryptedFileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE durable(id INTEGER PRIMARY KEY, value TEXT);");
            Execute(connection, "INSERT INTO durable VALUES (1, 'before');");
            Execute(connection, $"CREATE TABLE oversized(value TEXT DEFAULT '{oversizedDefault}');");
            Execute(connection, "INSERT INTO oversized DEFAULT VALUES;");

            ScalarInteger(connection, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'oversized';").Should().Be(1);
            ScalarInteger(connection, "SELECT length(value) FROM oversized;").Should().Be(oversizedDefault.Length);
            Scalar(connection, "SELECT value FROM durable WHERE id = 1;").Should().Be("before");
        }

        using var reopenEncryption = AhtolaEncryptionOptions.FromHex(AhtolaEncryptionCipher.Aes256Gcm, Aes256Key);
        using var reopenedFileSystem = new AhtolaEncryptionFileSystem(fileSystem, reopenEncryption);
        using var reopened = EmbeddedDatabase.OpenFile(path, reopenedFileSystem);
        using var reopenedConnection = reopened.Connect();
        ScalarInteger(reopenedConnection, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'oversized';").Should().Be(1);
        ScalarInteger(reopenedConnection, "SELECT length(value) FROM oversized;").Should().Be(oversizedDefault.Length);
        Scalar(reopenedConnection, "SELECT value FROM durable WHERE id = 1;").Should().Be("before");
    }

    private static void CorruptTriggerSql(IFileSystem fileSystem, string path)
    {
        using var file = fileSystem.OpenFile(path, FileOpenMode.OpenExisting);
        var headerBytes = new byte[SqliteDatabaseHeader.Size];
        file.Read(0, headerBytes).Should().Be(headerBytes.Length);
        var header = SqliteDatabaseHeader.Parse(headerBytes);
        var page = new byte[header.PageSize];
        file.Read(0, page).Should().Be(page.Length);

        var schema = SqliteTableLeafPageView.Parse(page, header.UsableSpace, isFirstPage: true);
        var triggerCell = schema.Cells.Single(cell =>
        {
            var values = SqliteRecordCodec.Decode(cell.Cell.LocalPayload.Span, header.TextEncoding);
            return values[0].AsText() == "trigger";
        });
        SqliteVarint.TryRead(page.AsSpan(triggerCell.Offset), out _, out var payloadLengthBytes).Should().BeTrue();
        SqliteVarint.TryRead(
            page.AsSpan(triggerCell.Offset + payloadLengthBytes),
            out _,
            out var rowIdBytes).Should().BeTrue();

        var payloadOffset = triggerCell.Offset + payloadLengthBytes + rowIdBytes;
        var payload = page.AsSpan(payloadOffset, triggerCell.Cell.LocalPayload.Length);
        var markerOffset = payload.IndexOf("CREATE TRIGGER"u8);
        markerOffset.Should().BeGreaterThanOrEqualTo(0);
        payload[markerOffset] = (byte)'X';

        file.Write(0, page);
        file.FlushToDisk();
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Done);
    }

    private static string Scalar(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Row);
        return statement.GetValue(0).AsText();
    }

    private static long ScalarInteger(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Row);
        return statement.GetValue(0).AsInteger();
    }
}
