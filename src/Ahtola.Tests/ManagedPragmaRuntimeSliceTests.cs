using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

public sealed class ManagedPragmaRuntimeSliceTests
{
    [Test]
    public void DirectManagedRuntimePragmasExposeSqliteMetadataShapesAndTypes()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        ColumnNames(connection, "PRAGMA database_list;").Should().Equal("seq", "name", "file");
        var databases = ReadRows(connection, "PRAGMA database_list;");
        databases.Should().HaveCount(1);
        databases[0].Should().Equal(SqlValue.Integer(0), SqlValue.Text("main"), SqlValue.Text(string.Empty));
        databases[0].Select(value => value.Kind).Should().Equal(
            SqlValueKind.Integer,
            SqlValueKind.Text,
            SqlValueKind.Text);

        ColumnNames(connection, "PRAGMA encoding;").Should().Equal("encoding");
        ReadRows(connection, "PRAGMA encoding;").Should().ContainSingle()
            .Which.Should().Equal(SqlValue.Text("UTF-8"));

        ColumnNames(connection, "PRAGMA query_only;").Should().Equal("query_only");
        ReadRows(connection, "PRAGMA query_only;").Should().ContainSingle()
            .Which.Should().Equal(SqlValue.Integer(0));
    }

    [Test]
    public void CatalogPragmasExposeGeneratedColumnsDefaultsAndPersistedCatalog()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(
            connection,
            "CREATE TABLE widgets(id INTEGER PRIMARY KEY, label TEXT NOT NULL DEFAULT 'ready', generated_label TEXT GENERATED ALWAYS AS (label || '!') VIRTUAL);");
        Execute(connection, "CREATE VIEW widget_labels AS SELECT label FROM widgets;");

        ColumnNames(connection, "PRAGMA table_info(widgets);").Should()
            .Equal("cid", "name", "type", "notnull", "dflt_value", "pk");
        var tableInfo = ReadRows(connection, "PRAGMA table_info(widgets);");
        tableInfo.Should().HaveCount(2);
        tableInfo[1].Should().Equal(
            SqlValue.Integer(1),
            SqlValue.Text("label"),
            SqlValue.Text("TEXT"),
            SqlValue.Integer(1),
            SqlValue.Text("'ready'"),
            SqlValue.Integer(0));

        ColumnNames(connection, "PRAGMA table_xinfo(widgets);").Should()
            .Equal("cid", "name", "type", "notnull", "dflt_value", "pk", "hidden");
        var tableXInfo = ReadRows(connection, "PRAGMA table_xinfo(widgets);");
        tableXInfo.Should().HaveCount(3);
        tableXInfo[2].Should().Equal(
            SqlValue.Integer(2),
            SqlValue.Text("generated_label"),
            SqlValue.Text("TEXT"),
            SqlValue.Integer(0),
            SqlValue.Null,
            SqlValue.Integer(0),
            SqlValue.Integer(2));

        ColumnNames(connection, "PRAGMA table_list;").Should()
            .Equal("schema", "name", "type", "ncol", "wr", "strict");
        var tableList = ReadRows(connection, "PRAGMA table_list;");
        FindCatalogEntry(tableList, "sqlite_schema").Should().Equal(
            SqlValue.Text("main"),
            SqlValue.Text("sqlite_schema"),
            SqlValue.Text("table"),
            SqlValue.Integer(5),
            SqlValue.Integer(0),
            SqlValue.Integer(0));
        FindCatalogEntry(tableList, "widgets").Should().Equal(
            SqlValue.Text("main"),
            SqlValue.Text("widgets"),
            SqlValue.Text("table"),
            SqlValue.Integer(3),
            SqlValue.Integer(0),
            SqlValue.Integer(0));
        FindCatalogEntry(tableList, "widget_labels").Should().Equal(
            SqlValue.Text("main"),
            SqlValue.Text("widget_labels"),
            SqlValue.Text("view"),
            SqlValue.Integer(1),
            SqlValue.Integer(0),
            SqlValue.Integer(0));

        var fileSystem = new InMemoryFileSystem();
        using (var fileDatabase = EmbeddedDatabase.OpenFile("pragma-catalog.db", fileSystem))
        using (var fileConnection = fileDatabase.Connect())
        {
            Execute(fileConnection, "CREATE TABLE persisted(id INTEGER PRIMARY KEY, value TEXT);");
        }

        using var reopenedDatabase = EmbeddedDatabase.OpenFile("pragma-catalog.db", fileSystem);
        using var reopenedConnection = reopenedDatabase.Connect();
        FindCatalogEntry(ReadRows(reopenedConnection, "PRAGMA table_list;"), "persisted")[3]
            .Should().Be(SqlValue.Integer(2));
        ReadRows(reopenedConnection, "PRAGMA database_list;")[0][2].Should()
            .Be(SqlValue.Text("pragma-catalog.db"));
    }

    [Test]
    public void QueryOnlyIsConnectionLocalBlocksWritesAndIsNotTransactionState()
    {
        using var database = new EmbeddedDatabase();
        using var primary = database.Connect();
        using var sibling = database.Connect();

        using (var setter = primary.Prepare("PRAGMA query_only = ON;"))
        {
            setter.GetColumnCount().Should().Be(0);
            setter.Step().Should().Be(StatementStepResult.Done);
        }

        ReadRows(primary, "PRAGMA query_only;").Should().ContainSingle()
            .Which.Should().Equal(SqlValue.Integer(1));
        ReadRows(sibling, "PRAGMA query_only;").Should().ContainSingle()
            .Which.Should().Equal(SqlValue.Integer(0));

        var write = () => Execute(primary, "CREATE TABLE rejected(value INTEGER);");
        write.Should().Throw<EmbeddedSqlException>().WithMessage("attempt to write a readonly database");
        ReadRows(primary, "PRAGMA table_list;").Should()
            .NotContain(row => row[1].AsText() == "rejected");

        Execute(primary, "BEGIN;");
        Execute(primary, "PRAGMA query_only = OFF;");
        Execute(primary, "ROLLBACK;");
        ReadRows(primary, "PRAGMA query_only;").Should().ContainSingle()
            .Which.Should().Equal(SqlValue.Integer(0));

        Execute(primary, "SAVEPOINT pragma_state;");
        Execute(primary, "PRAGMA query_only = ON;");
        Execute(primary, "ROLLBACK TO pragma_state;");
        ReadRows(primary, "PRAGMA query_only;").Should().ContainSingle()
            .Which.Should().Equal(SqlValue.Integer(1));
        Execute(primary, "RELEASE pragma_state;");
        Execute(primary, "PRAGMA query_only = OFF;");
        Execute(primary, "CREATE TABLE accepted(value INTEGER);");
        FindCatalogEntry(ReadRows(primary, "PRAGMA table_list;"), "accepted")[2]
            .Should().Be(SqlValue.Text("table"));
    }

    [Test]
    public void RecursiveTriggersAreConnectionLocalAndAllowNonRecursiveTriggerChains()
    {
        using var database = new EmbeddedDatabase();
        using var primary = database.Connect();
        using var sibling = database.Connect();

        ReadValue(primary, "PRAGMA recursive_triggers;").Should().Be(SqlValue.Integer(0));
        Execute(primary, "PRAGMA recursive_triggers = ON;");
        ReadValue(primary, "PRAGMA recursive_triggers;").Should().Be(SqlValue.Integer(1));
        ReadValue(sibling, "PRAGMA recursive_triggers;").Should().Be(SqlValue.Integer(0));

        Execute(primary, "BEGIN;");
        Execute(primary, "PRAGMA recursive_triggers = OFF;");
        Execute(primary, "ROLLBACK;");
        ReadValue(primary, "PRAGMA recursive_triggers;").Should().Be(SqlValue.Integer(0));

        Execute(primary, "CREATE TABLE source(value INTEGER);");
        Execute(primary, "CREATE TABLE intermediate(value INTEGER);");
        Execute(primary, "CREATE TABLE destination(value INTEGER);");
        Execute(
            primary,
            "CREATE TRIGGER source_to_intermediate AFTER INSERT ON source BEGIN INSERT INTO intermediate VALUES (1); END;");
        Execute(
            primary,
            "CREATE TRIGGER intermediate_to_destination AFTER INSERT ON intermediate BEGIN INSERT INTO destination VALUES (1); END;");
        Execute(primary, "INSERT INTO source VALUES (1);");

        ReadValue(primary, "SELECT COUNT(*) FROM intermediate;").Should().Be(SqlValue.Integer(1));
        ReadValue(primary, "SELECT COUNT(*) FROM destination;").Should().Be(SqlValue.Integer(1));

        Execute(primary, "CREATE TABLE self_referencing(value INTEGER);");
        Execute(
            primary,
            "CREATE TRIGGER self_repeat AFTER INSERT ON self_referencing BEGIN INSERT INTO self_referencing VALUES (2); END;");
        Execute(primary, "INSERT INTO self_referencing VALUES (1);");
        ReadValue(primary, "SELECT COUNT(*) FROM self_referencing;").Should().Be(SqlValue.Integer(2));
    }

    [Test]
    public void InMemoryHeaderPragmasAreTransactionalAndSchemaVersionTracksDdl()
    {
        using var database = new EmbeddedDatabase();
        using var primary = database.Connect();
        using var sibling = database.Connect();

        ColumnNames(primary, "PRAGMA schema_version;").Should().Equal("schema_version");
        ColumnNames(primary, "PRAGMA user_version;").Should().Equal("user_version");
        ColumnNames(primary, "PRAGMA application_id;").Should().Equal("application_id");
        ReadValue(primary, "PRAGMA schema_version;").Should().Be(SqlValue.Integer(0));

        Execute(primary, "CREATE TABLE initial(value INTEGER);");
        ReadValue(primary, "PRAGMA schema_version;").Should().Be(SqlValue.Integer(1));
        Execute(primary, "PRAGMA user_version = 456;");
        Execute(primary, "PRAGMA application_id(789);");
        ReadValue(sibling, "PRAGMA user_version;").Should().Be(SqlValue.Integer(456));
        ReadValue(sibling, "PRAGMA application_id;").Should().Be(SqlValue.Integer(789));

        Execute(primary, "BEGIN;");
        Execute(primary, "PRAGMA schema_version = 40;");
        Execute(primary, "PRAGMA user_version = 457;");
        Execute(primary, "CREATE TABLE committed(value INTEGER);");
        Execute(primary, "SAVEPOINT pragma_headers;");
        Execute(primary, "PRAGMA application_id = 790;");
        Execute(primary, "PRAGMA user_version = 458;");
        Execute(primary, "ROLLBACK TO pragma_headers;");
        Execute(primary, "RELEASE pragma_headers;");
        ReadValue(primary, "PRAGMA schema_version;").Should().Be(SqlValue.Integer(41));
        ReadValue(primary, "PRAGMA user_version;").Should().Be(SqlValue.Integer(457));
        ReadValue(primary, "PRAGMA application_id;").Should().Be(SqlValue.Integer(789));
        Execute(primary, "COMMIT;");

        ReadValue(sibling, "PRAGMA schema_version;").Should().Be(SqlValue.Integer(41));
        ReadValue(sibling, "PRAGMA user_version;").Should().Be(SqlValue.Integer(457));
        ReadValue(sibling, "PRAGMA application_id;").Should().Be(SqlValue.Integer(789));

        Execute(primary, "PRAGMA query_only = ON;");
        var writeWhileQueryOnly = () => Execute(primary, "PRAGMA user_version = 999;");
        writeWhileQueryOnly.Should().Throw<EmbeddedSqlException>()
            .WithMessage("attempt to write a readonly database");
        ReadValue(primary, "PRAGMA user_version;").Should().Be(SqlValue.Integer(457));
    }

    [Test]
    public void JournalAndPageSizePragmasFollowManagedStorageCapabilities()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        ColumnNames(connection, "PRAGMA journal_mode;").Should().Equal("journal_mode");
        ReadRows(connection, "PRAGMA journal_mode;").Should().ContainSingle()
            .Which.Should().Equal(SqlValue.Text("memory"));
        using (var setter = connection.Prepare("PRAGMA journal_mode = MEMORY;"))
        {
            setter.GetColumnCount().Should().Be(1);
            setter.HasRows().Should().BeTrue();
            setter.Step().Should().Be(StatementStepResult.Row);
            setter.GetValue(0).Should().Be(SqlValue.Text("memory"));
            setter.Step().Should().Be(StatementStepResult.Done);
        }

        ReadValue(connection, "PRAGMA journal_mode = WAL;").Should().Be(SqlValue.Text("memory"));
        ReadValue(connection, "PRAGMA journal_mode;").Should().Be(SqlValue.Text("memory"));

        ColumnNames(connection, "PRAGMA page_size;").Should().Equal("page_size");
        ReadValue(connection, "PRAGMA page_size;").Should().Be(SqlValue.Integer(4_096));
        Execute(connection, "PRAGMA page_size = 8192;");
        // An uninitialized in-memory database accepts a new page size and keeps it
        // (SQLite semantics: page_size changes only fail after the first page exists).
        ReadValue(connection, "PRAGMA page_size;").Should().Be(SqlValue.Integer(8_192));

        Execute(connection, "PRAGMA cache_size = 1;");
        ReadValue(connection, "PRAGMA cache_size;").Should().Be(SqlValue.Integer(200));
        // SQLite silently ignores unrecognized pragmas; page_count and freelist_count
        // report the managed in-memory page model (zero pages before initialization).
        Execute(connection, "PRAGMA synchronous = 1;");
        ReadValue(connection, "PRAGMA page_count;").Should().Be(SqlValue.Integer(0));
        ReadValue(connection, "PRAGMA temp.freelist_count;").Should().Be(SqlValue.Integer(0));
    }

    [Test]
    public void PagerMetadataPragmasAreScopedPerAttachedDatabase()
    {
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile("pragma-state-main.db", fileSystem);
        using var connection = database.Connect();
        Execute(connection, "ATTACH 'pragma-state-aux.db' AS aux;");

        Execute(connection, "PRAGMA main.cache_size=400;");
        Execute(connection, "PRAGMA aux.cache_size=800;");
        Execute(connection, "PRAGMA main.cache_spill=OFF;");
        Execute(connection, "PRAGMA aux.cache_spill=ON;");
        Execute(connection, "PRAGMA main.synchronous=OFF;");
        Execute(connection, "PRAGMA aux.synchronous=EXTRA;");
        ReadValue(connection, "PRAGMA main.locking_mode=NORMAL;").Should().Be(SqlValue.Text("normal"));
        ReadValue(connection, "PRAGMA aux.locking_mode=EXCLUSIVE;").Should().Be(SqlValue.Text("exclusive"));

        ReadValue(connection, "PRAGMA main.cache_size;").Should().Be(SqlValue.Integer(400));
        ReadValue(connection, "PRAGMA aux.cache_size;").Should().Be(SqlValue.Integer(800));
        ReadValue(connection, "PRAGMA main.cache_spill;").Should().Be(SqlValue.Integer(0));
        ReadValue(connection, "PRAGMA aux.cache_spill;").Should().Be(SqlValue.Integer(1));
        ReadValue(connection, "PRAGMA main.synchronous;").Should().Be(SqlValue.Integer(0));
        ReadValue(connection, "PRAGMA aux.synchronous;").Should().Be(SqlValue.Integer(3));
        ReadValue(connection, "PRAGMA main.locking_mode;").Should().Be(SqlValue.Text("normal"));
        ReadValue(connection, "PRAGMA aux.locking_mode;").Should().Be(SqlValue.Text("exclusive"));
    }

    [Test]
    public void PoolResetRestoresConnectionPragmaDefaultsAndDatabaseBusyTimeout()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "PRAGMA cache_size=777;");
        Execute(connection, "PRAGMA cache_spill=OFF;");
        Execute(connection, "PRAGMA busy_timeout=5000;");

        connection.ResetForPooling();

        ReadValue(connection, "PRAGMA cache_size;").Should().Be(SqlValue.Integer(-2000));
        ReadValue(connection, "PRAGMA cache_spill;").Should().Be(SqlValue.Integer(1));
        ReadValue(connection, "PRAGMA busy_timeout;").Should().Be(SqlValue.Integer(0));
        database.BusyTimeout.Should().Be(TimeSpan.Zero);
    }

    [Test]
    public void FileBackedHeaderPragmaWritesAreDurableTransactionalAndSchemaScoped()
    {
        var fileSystem = new InMemoryFileSystem();
        using (var database = EmbeddedDatabase.OpenFile("pragma-header-writes.db", fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE data(value INTEGER);");
            ReadValue(connection, "PRAGMA schema_version;").Should().Be(SqlValue.Integer(1));
            Execute(connection, "PRAGMA schema_version = 40;");
            Execute(connection, "PRAGMA user_version = 456;");
            Execute(connection, "PRAGMA application_id = 789;");
            Execute(connection, "CREATE TABLE committed(value INTEGER);");
            Execute(connection, "ATTACH 'pragma-header-attached.db' AS aux;");
            Execute(connection, "PRAGMA aux.user_version = 12;");
            Execute(connection, "PRAGMA aux.application_id = 34;");
            ReadValue(connection, "PRAGMA schema_version;").Should().Be(SqlValue.Integer(41));

            Execute(connection, "BEGIN;");
            Execute(connection, "PRAGMA main.user_version = 999;");
            Assert.Throws<EmbeddedSqlException>(() => Execute(connection, "PRAGMA aux.user_version = 999;"))!
                .Message.Should().Contain("cannot modify more than one database");
            Execute(connection, "ROLLBACK;");
            ReadValue(connection, "PRAGMA main.user_version;").Should().Be(SqlValue.Integer(456));
            ReadValue(connection, "PRAGMA aux.user_version;").Should().Be(SqlValue.Integer(12));

            Execute(connection, "BEGIN;");
            Execute(connection, "PRAGMA aux.user_version = 56;");
            Execute(connection, "COMMIT;");
            ReadValue(connection, "PRAGMA aux.user_version;").Should().Be(SqlValue.Integer(56));
        }

        using (var reopened = EmbeddedDatabase.OpenFile("pragma-header-writes.db", fileSystem))
        using (var connection = reopened.Connect())
        {
            ReadValue(connection, "PRAGMA schema_version;").Should().Be(SqlValue.Integer(41));
            ReadValue(connection, "PRAGMA user_version;").Should().Be(SqlValue.Integer(456));
            ReadValue(connection, "PRAGMA application_id;").Should().Be(SqlValue.Integer(789));
        }
        using (var attached = EmbeddedDatabase.OpenFile("pragma-header-attached.db", fileSystem))
        using (var connection = attached.Connect())
        {
            ReadValue(connection, "PRAGMA user_version;").Should().Be(SqlValue.Integer(56));
            ReadValue(connection, "PRAGMA application_id;").Should().Be(SqlValue.Integer(34));
        }
        using (var readOnlyDatabase = EmbeddedDatabase.OpenFile(
                   "pragma-header-writes.db",
                   fileSystem,
                   readOnly: true))
        using (var readOnlyConnection = readOnlyDatabase.Connect())
        {
            var readOnlyWrite = () => Execute(readOnlyConnection, "PRAGMA user_version = 99;");
            readOnlyWrite.Should().Throw<EmbeddedSqlException>()
                .WithMessage("attempt to write a readonly database");
        }
    }

    [Test]
    public void IntrospectionAndUnknownPragmasFollowTursoAndSqliteBehavior()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        connection.RegisterScalarFunction("managed_scalar", 2, values => values[0]);
        connection.RegisterAggregateFunction(
            "managed_aggregate",
            1,
            SqlValue.Integer(0),
            (state, _) => SqlValue.Integer(state.AsInteger() + 1),
            state => state);

        ColumnNames(connection, "PRAGMA function_list;")
            .Should().Equal("name", "builtin", "type", "enc", "narg", "flags");
        var functions = ReadRows(connection, "PRAGMA function_list;");
        functions.Should().Contain(row =>
            row[0] == SqlValue.Text("abs")
            && row[1] == SqlValue.Integer(1)
            && row[2] == SqlValue.Text("s")
            && row[3] == SqlValue.Text("utf8")
            && row[4] == SqlValue.Integer(1)
            && row[5] == SqlValue.Integer(0x200800));
        functions.Should().Contain(row =>
            row[0] == SqlValue.Text("count")
            && row[2] == SqlValue.Text("w")
            && row[4] == SqlValue.Integer(0)
            && row[5] == SqlValue.Integer(0x200000));
        functions.Should().Contain(row =>
            row[0] == SqlValue.Text("count")
            && row[2] == SqlValue.Text("w")
            && row[4] == SqlValue.Integer(1)
            && row[5] == SqlValue.Integer(0x200000));
        functions.Should().Contain(row =>
            row[0] == SqlValue.Text("round")
            && row[4] == SqlValue.Integer(2));
        functions.Should().Contain(row =>
            row[0] == SqlValue.Text("min")
            && row[2] == SqlValue.Text("s")
            && row[4] == SqlValue.Integer(-1));
        functions.Should().Contain(row =>
            row[0] == SqlValue.Text("min")
            && row[2] == SqlValue.Text("w")
            && row[4] == SqlValue.Integer(1));
        functions.Should().Contain(row =>
            row[0] == SqlValue.Text("managed_scalar")
            && row[1] == SqlValue.Integer(0)
            && row[2] == SqlValue.Text("s")
            && row[4] == SqlValue.Integer(2));
        functions.Should().Contain(row =>
            row[0] == SqlValue.Text("managed_aggregate")
            && row[1] == SqlValue.Integer(0)
            && row[2] == SqlValue.Text("a")
            && row[4] == SqlValue.Integer(1));
        ColumnNames(connection, "PRAGMA module_list;").Should().Equal("name");
        ReadRows(connection, "PRAGMA module_list;")
            .Should().Contain(row => row[0] == SqlValue.Text("generate_series"));

        // Unrecognized pragmas follow SQLite's silent no-op behavior.
        Execute(connection, "PRAGMA automatic_index;");

        ReadRows(connection, "PRAGMA temp.database_list;")
            .Select(row => row[1].AsText())
            .Should().Equal("main", "temp");
        using (var tempTableList = connection.Prepare("PRAGMA temp.table_list;"))
        {
            tempTableList.Step().Should().Be(StatementStepResult.Row);
            tempTableList.GetValue(0).Should().Be(SqlValue.Text("temp"));
            tempTableList.GetValue(1).Should().Be(SqlValue.Text("sqlite_temp_schema"));
        }

        ReadValue(connection, "PRAGMA temp.journal_mode;").Should().Be(SqlValue.Text("wal"));
        ReadValue(connection, "PRAGMA temp.journal_mode = MVCC;").Should().Be(SqlValue.Text("wal"));
    }

    [Test]
    public void ConnectionMetadataPragmasRoundTrip()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        Execute(connection, "PRAGMA synchronous = OFF;");
        ReadValue(connection, "PRAGMA synchronous;").Should().Be(SqlValue.Integer(0));
        Execute(connection, "PRAGMA synchronous = NORMAL;");
        ReadValue(connection, "PRAGMA synchronous;").Should().Be(SqlValue.Integer(1));
        Execute(connection, "PRAGMA synchronous = FULL;");
        ReadValue(connection, "PRAGMA synchronous;").Should().Be(SqlValue.Integer(2));

        ReadValue(connection, "PRAGMA locking_mode = EXCLUSIVE;")
            .Should().Be(SqlValue.Text("exclusive"));
        ReadValue(connection, "PRAGMA locking_mode;").Should().Be(SqlValue.Text("exclusive"));
        ReadValue(connection, "PRAGMA locking_mode = NORMAL;").Should().Be(SqlValue.Text("normal"));

        ReadValue(connection, "PRAGMA auto_vacuum;").Should().Be(SqlValue.Integer(0));
        Execute(connection, "PRAGMA auto_vacuum = NONE;");

        Execute(connection, "PRAGMA data_sync_retry = ON;");
        ReadValue(connection, "PRAGMA data_sync_retry;").Should().Be(SqlValue.Integer(1));
        Execute(connection, "PRAGMA data_sync_retry = OFF;");
        ReadValue(connection, "PRAGMA data_sync_retry;").Should().Be(SqlValue.Integer(0));
        ReadValue(connection, "PRAGMA temp.journal_mode;").Should().Be(SqlValue.Text("wal"));
        using var unknownSchema = connection.Prepare("PRAGMA missing.page_count;");
        Assert.Throws<EmbeddedSqlException>(() => unknownSchema.Step())!
            .Message.Should().Be("no such database: missing");
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Done);
    }

    private static string[] ColumnNames(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        var columns = new string[statement.GetColumnCount()];
        for (var index = 0; index < columns.Length; index++)
            columns[index] = statement.GetColumnName(index);

        return columns;
    }

    private static List<SqlValue[]> ReadRows(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        var rows = new List<SqlValue[]>();
        while (statement.Step() == StatementStepResult.Row)
        {
            var row = new SqlValue[statement.GetColumnCount()];
            for (var index = 0; index < row.Length; index++)
                row[index] = statement.GetValue(index);

            rows.Add(row);
        }

        return rows;
    }

    private static SqlValue ReadValue(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Row);
        var value = statement.GetValue(0);
        statement.Step().Should().Be(StatementStepResult.Done);
        return value;
    }

    private static SqlValue[] FindCatalogEntry(IEnumerable<SqlValue[]> rows, string name)
        => rows.Single(row => row[1].AsText() == name);
}
