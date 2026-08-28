using Ahtola.Core;
using AwesomeAssertions;

namespace Ahtola.Tests;

public sealed class ManagedReplicaLogicalReplayerTests
{
    [Test]
    public void CreateTableSchemaOpCreatesTheTableWhenAbsent()
    {
        using var connection = OpenConnection();
        var txn = SingleOpTxn(SchemaOp(
            ManagedReplicaLogicalSchemaAction.Create,
            ManagedReplicaLogicalSchemaKind.Table,
            "widgets",
            "CREATE TABLE widgets(id INTEGER PRIMARY KEY, name TEXT)"));

        Apply(connection, [txn]);

        ColumnNames(connection, "widgets").Should().Equal("id", "name");
    }

    [Test]
    public void CreateTableSchemaOpIsIdempotentAndOnlyAddsMissingColumns()
    {
        using var connection = OpenConnection();
        Exec(connection, "CREATE TABLE widgets(id INTEGER PRIMARY KEY, name TEXT)");
        Exec(connection, "INSERT INTO widgets VALUES (1, 'x')");

        var txn = SingleOpTxn(SchemaOp(
            ManagedReplicaLogicalSchemaAction.Refresh,
            ManagedReplicaLogicalSchemaKind.Table,
            "widgets",
            "CREATE TABLE widgets(id INTEGER PRIMARY KEY, name TEXT, note TEXT)"));

        Apply(connection, [txn]);

        ColumnNames(connection, "widgets").Should().Equal("id", "name", "note");
        // Pre-existing data must survive the "grow" migration.
        Scalar(connection, "SELECT name FROM widgets WHERE id = 1").AsText().Should().Be("x");
    }

    [Test]
    public void RefreshOnAnIndexDropsAndRecreatesIt()
    {
        using var connection = OpenConnection();
        Exec(connection, "CREATE TABLE t(x TEXT)");
        Exec(connection, "CREATE INDEX idx_t_x ON t(x)");

        var txn = SingleOpTxn(SchemaOp(
            ManagedReplicaLogicalSchemaAction.Refresh,
            ManagedReplicaLogicalSchemaKind.Index,
            "idx_t_x",
            "CREATE INDEX idx_t_x ON t(x COLLATE NOCASE)"));

        Apply(connection, [txn]);

        Scalar(connection, "SELECT sql FROM sqlite_schema WHERE name = 'idx_t_x'").AsText()
            .Should().Contain("NOCASE");
    }

    [Test]
    public void CreateIndexSchemaOpIsIdempotentWhenAlreadyPresent()
    {
        using var connection = OpenConnection();
        Exec(connection, "CREATE TABLE t(x TEXT)");
        Exec(connection, "CREATE INDEX idx_t_x ON t(x)");

        var txn = SingleOpTxn(SchemaOp(
            ManagedReplicaLogicalSchemaAction.Create,
            ManagedReplicaLogicalSchemaKind.Index,
            "idx_t_x",
            "CREATE INDEX idx_t_x ON t(x)"));

        Action act = () => Apply(connection, [txn]);
        act.Should().NotThrow("a second Create for an already-present index must be a no-op");
    }

    [Test]
    public void DropSchemaOpIsIdempotent()
    {
        using var connection = OpenConnection();
        Exec(connection, "CREATE TABLE t(x TEXT)");

        var drop = SingleOpTxn(SchemaOp(ManagedReplicaLogicalSchemaAction.Drop, ManagedReplicaLogicalSchemaKind.Table, "t", string.Empty));
        Apply(connection, [drop]);
        TableExists(connection, "t").Should().BeFalse();

        // Replaying the same drop again must not throw (DROP ... IF EXISTS).
        Action act = () => Apply(connection, [drop]);
        act.Should().NotThrow();
    }

    [Test]
    public void TableRefreshRejectsARenamedColumnInsteadOfSilentlyDiverging()
    {
        using var connection = OpenConnection();
        Exec(connection, "CREATE TABLE widgets(id INTEGER PRIMARY KEY, name TEXT)");
        Exec(connection, "INSERT INTO widgets VALUES (1, 'a')");

        // The remote renamed "name" to "label"; additive column-diffing alone cannot express a
        // rename (it would just ADD a new "label" column and leave "name" stale).
        var txn = SingleOpTxn(SchemaOp(
            ManagedReplicaLogicalSchemaAction.Refresh,
            ManagedReplicaLogicalSchemaKind.Table,
            "widgets",
            "CREATE TABLE widgets(id INTEGER PRIMARY KEY, label TEXT)"));

        Action act = () => Apply(connection, [txn]);
        act.Should().Throw<InvalidDataException>().WithMessage("*removes or renames column*");
        // Nothing must have been mutated: the original column and data are untouched.
        ColumnNames(connection, "widgets").Should().Equal("id", "name");
        Scalar(connection, "SELECT name FROM widgets WHERE id = 1").AsText().Should().Be("a");
    }

    [Test]
    public void TableRefreshRejectsADroppedColumn()
    {
        using var connection = OpenConnection();
        Exec(connection, "CREATE TABLE widgets(id INTEGER PRIMARY KEY, name TEXT, note TEXT)");

        var txn = SingleOpTxn(SchemaOp(
            ManagedReplicaLogicalSchemaAction.Refresh,
            ManagedReplicaLogicalSchemaKind.Table,
            "widgets",
            "CREATE TABLE widgets(id INTEGER PRIMARY KEY, name TEXT)"));

        Action act = () => Apply(connection, [txn]);
        act.Should().Throw<InvalidDataException>().WithMessage("*removes or renames column*");
    }

    [Test]
    public void TableRefreshRejectsAChangedColumnDefinition()
    {
        using var connection = OpenConnection();
        Exec(connection, "CREATE TABLE widgets(id INTEGER PRIMARY KEY, name TEXT)");

        var txn = SingleOpTxn(SchemaOp(
            ManagedReplicaLogicalSchemaAction.Refresh,
            ManagedReplicaLogicalSchemaKind.Table,
            "widgets",
            "CREATE TABLE widgets(id INTEGER PRIMARY KEY, name TEXT NOT NULL)"));

        Action act = () => Apply(connection, [txn]);
        act.Should().Throw<InvalidDataException>().WithMessage("*changes the definition of*");
    }

    [Test]
    public void TableRefreshRejectsAChangedTableLevelConstraint()
    {
        using var connection = OpenConnection();
        Exec(connection, "CREATE TABLE widgets(x TEXT, y TEXT, PRIMARY KEY(x, y))");

        var txn = SingleOpTxn(SchemaOp(
            ManagedReplicaLogicalSchemaAction.Refresh,
            ManagedReplicaLogicalSchemaKind.Table,
            "widgets",
            "CREATE TABLE widgets(x TEXT, y TEXT, UNIQUE(x, y))"));

        Action act = () => Apply(connection, [txn]);
        act.Should().Throw<InvalidDataException>().WithMessage("*table-level constraints*");
    }

    [Test]
    public void TableRefreshRejectsAddingStrict()
    {
        using var connection = OpenConnection();
        Exec(connection, "CREATE TABLE widgets(id INTEGER PRIMARY KEY, name TEXT)");

        var txn = SingleOpTxn(SchemaOp(
            ManagedReplicaLogicalSchemaAction.Refresh,
            ManagedReplicaLogicalSchemaKind.Table,
            "widgets",
            "CREATE TABLE widgets(id INTEGER PRIMARY KEY, name TEXT) STRICT"));

        Action act = () => Apply(connection, [txn]);
        act.Should().Throw<InvalidDataException>().WithMessage("*STRICT/WITHOUT ROWID*");
    }

    [Test]
    public void TableRefreshRejectsAddingWithoutRowid()
    {
        using var connection = OpenConnection();
        Exec(connection, "CREATE TABLE widgets(id TEXT PRIMARY KEY, name TEXT)");

        var txn = SingleOpTxn(SchemaOp(
            ManagedReplicaLogicalSchemaAction.Refresh,
            ManagedReplicaLogicalSchemaKind.Table,
            "widgets",
            "CREATE TABLE widgets(id TEXT PRIMARY KEY, name TEXT) WITHOUT ROWID"));

        Action act = () => Apply(connection, [txn]);
        act.Should().Throw<InvalidDataException>().WithMessage("*STRICT/WITHOUT ROWID*");
    }

    [Test]
    public void TableRefreshStillAllowsAPurelyAdditiveColumn()
    {
        // A genuinely additive refresh (new column, all existing columns/constraints unchanged)
        // must still succeed; issue-4's fix only rejects NON-additive changes.
        using var connection = OpenConnection();
        Exec(connection, "CREATE TABLE widgets(id INTEGER PRIMARY KEY, name TEXT)");
        Exec(connection, "INSERT INTO widgets VALUES (1, 'a')");

        var txn = SingleOpTxn(SchemaOp(
            ManagedReplicaLogicalSchemaAction.Refresh,
            ManagedReplicaLogicalSchemaKind.Table,
            "widgets",
            "CREATE TABLE widgets(id INTEGER PRIMARY KEY, name TEXT, note TEXT)"));

        Action act = () => Apply(connection, [txn]);
        act.Should().NotThrow();
        ColumnNames(connection, "widgets").Should().Equal("id", "name", "note");
        Scalar(connection, "SELECT name FROM widgets WHERE id = 1").AsText().Should().Be("a");
    }

    [Test]
    public void UpsertRowIntoARowidOnlyTablePreservesTheRowid()
    {
        using var connection = OpenConnection();
        Exec(connection, "CREATE TABLE t(a TEXT, b TEXT)");

        var record = SqliteRecordCodecTestHelper.EncodeRow(SqlValue.Text("va"), SqlValue.Text("vb"));
        var txn = SingleOpTxn(RowOp(ManagedReplicaLogicalOpType.UpsertRow, "t", rowId: 42, record: record));
        Apply(connection, [txn]);

        Scalar(connection, "SELECT rowid FROM t").AsInteger().Should().Be(42);
        Scalar(connection, "SELECT a FROM t WHERE rowid = 42").AsText().Should().Be("va");
    }

    [Test]
    public void ReplayingTheSameUpsertTwiceIsIdempotentForARowidOnlyTable()
    {
        using var connection = OpenConnection();
        Exec(connection, "CREATE TABLE t(a TEXT)");
        var record = SqliteRecordCodecTestHelper.EncodeRow(SqlValue.Text("va"));
        var txn = SingleOpTxn(RowOp(ManagedReplicaLogicalOpType.UpsertRow, "t", 1, record));

        Apply(connection, [txn]);
        Apply(connection, [txn]);

        RowCount(connection, "t").Should().Be(1);
    }

    [Test]
    public void UpsertRowWithAnIntegerPrimaryKeyForcesTheRowidAliasColumn()
    {
        using var connection = OpenConnection();
        Exec(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, name TEXT)");

        // The wire record encodes NULL for the rowid-alias column (as SQLite itself would), and the
        // replay engine must substitute the real rowid, not the NULL placeholder.
        var record = SqliteRecordCodecTestHelper.EncodeRow(SqlValue.Null, SqlValue.Text("alice"));
        var txn = SingleOpTxn(RowOp(ManagedReplicaLogicalOpType.UpsertRow, "t", rowId: 5, record));
        Apply(connection, [txn]);

        Scalar(connection, "SELECT id FROM t").AsInteger().Should().Be(5);
        Scalar(connection, "SELECT name FROM t WHERE id = 5").AsText().Should().Be("alice");
    }

    [Test]
    public void UpsertRowWithADeclaredNonIntegerPrimaryKeyPrefersThePrimaryKeyOverTheRowid()
    {
        using var connection = OpenConnection();
        Exec(connection, "CREATE TABLE t(x TEXT PRIMARY KEY, y TEXT)");
        Exec(connection, "INSERT INTO t VALUES ('local', 'old')"); // gets a local rowid, e.g. 1

        var record = SqliteRecordCodecTestHelper.EncodeRow(SqlValue.Text("local"), SqlValue.Text("new"));
        // Remote rowid (99) differs from whatever local rowid 'local' actually has; the PK must win.
        var txn = SingleOpTxn(RowOp(ManagedReplicaLogicalOpType.UpsertRow, "t", rowId: 99, record));
        Apply(connection, [txn]);

        RowCount(connection, "t").Should().Be(1, "the upsert must replace the existing PK row, not insert a second one");
        Scalar(connection, "SELECT y FROM t WHERE x = 'local'").AsText().Should().Be("new");
    }

    [Test]
    public void UpsertRowAgainstAPreAlterSchemaOnlyBindsColumnsPresentInTheRecord()
    {
        using var connection = OpenConnection();
        Exec(connection, "CREATE TABLE t(x TEXT PRIMARY KEY, y TEXT)");
        Exec(connection, "INSERT INTO t VALUES ('a', 'y1')");
        Exec(connection, "ALTER TABLE t ADD COLUMN z TEXT DEFAULT 'z-default'");

        // A record captured before the ALTER only has 2 columns (x, y); replaying it must not
        // touch column z at all (preserves the post-ALTER default).
        var record = SqliteRecordCodecTestHelper.EncodeRow(SqlValue.Text("a"), SqlValue.Text("y2"));
        var txn = SingleOpTxn(RowOp(ManagedReplicaLogicalOpType.UpsertRow, "t", rowId: 1, record));
        Apply(connection, [txn]);

        Scalar(connection, "SELECT z FROM t WHERE x = 'a'").AsText().Should().Be("z-default");
        Scalar(connection, "SELECT y FROM t WHERE x = 'a'").AsText().Should().Be("y2");
    }

    [Test]
    public void DeleteRowByRowidRemovesTheRow()
    {
        using var connection = OpenConnection();
        Exec(connection, "CREATE TABLE t(a TEXT)");
        Exec(connection, "INSERT INTO t VALUES ('x')");

        var txn = SingleOpTxn(RowOp(ManagedReplicaLogicalOpType.DeleteRow, "t", rowId: 1, record: []));
        Apply(connection, [txn]);

        RowCount(connection, "t").Should().Be(0);
    }

    [Test]
    public void DeleteRowByRowidOnAnAlreadyAbsentRowIsANoOp()
    {
        using var connection = OpenConnection();
        Exec(connection, "CREATE TABLE t(a TEXT)");

        var txn = SingleOpTxn(RowOp(ManagedReplicaLogicalOpType.DeleteRow, "t", rowId: 999, record: []));
        Action act = () => Apply(connection, [txn]);
        act.Should().NotThrow();
    }

    [Test]
    public void DeleteRowWithoutAKeyOnATableWhosePrimaryKeyIsNotTheRowidIsRefused()
    {
        using var connection = OpenConnection();
        Exec(connection, "CREATE TABLE q(x TEXT PRIMARY KEY)");
        Exec(connection, "INSERT INTO q VALUES ('1')");

        var txn = SingleOpTxn(RowOp(ManagedReplicaLogicalOpType.DeleteRow, "q", rowId: 8, record: []));
        Action act = () => Apply(connection, [txn]);
        act.Should().Throw<InvalidDataException>().WithMessage("*refusing rowid-based replay*");
    }

    [Test]
    public void DeleteRowWithAKeyProjectionWinsOverAStaleRowid()
    {
        using var connection = OpenConnection();
        Exec(connection, "CREATE TABLE q(x TEXT PRIMARY KEY)");
        Exec(connection, "INSERT INTO q VALUES ('1')");

        var key = SqliteRecordCodecTestHelper.EncodeRow(SqlValue.Text("1"));
        // rowid 99 is deliberately wrong; the PK projection must be what actually deletes the row.
        var txn = SingleOpTxn(RowOp(ManagedReplicaLogicalOpType.DeleteRow, "q", rowId: 99, record: key));
        Apply(connection, [txn]);

        RowCount(connection, "q").Should().Be(0);
    }

    [Test]
    public void DeleteRowWithoutAKeyOnATableWithNoPrimaryKeyUsesRowidSafely()
    {
        using var connection = OpenConnection();
        Exec(connection, "CREATE TABLE nopk(a TEXT, b TEXT)");
        Exec(connection, "INSERT INTO nopk VALUES ('a', 'b')");

        var txn = SingleOpTxn(RowOp(ManagedReplicaLogicalOpType.DeleteRow, "nopk", rowId: 1, record: []));
        Apply(connection, [txn]);

        RowCount(connection, "nopk").Should().Be(0);
    }

    [Test]
    public void ColumnLevelIntegerPrimaryKeyDescIsNotARowidAlias()
    {
        // SQLite's documented quirk (sqlite.org/lang_createtable.html#rowid): a COLUMN-level
        // "PRIMARY KEY DESC" constraint does NOT alias the rowid (unlike ASC/unspecified, and
        // unlike the equivalent table-level "PRIMARY KEY(id DESC)" form, which still does). The
        // decoded record's own id value must be used as-is, never overwritten by the wire rowid.
        using var connection = OpenConnection();
        Exec(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY DESC, name TEXT)");

        var record = SqliteRecordCodecTestHelper.EncodeRow(SqlValue.Integer(99), SqlValue.Text("alice"));
        var txn = SingleOpTxn(RowOp(ManagedReplicaLogicalOpType.UpsertRow, "t", rowId: 5, record));
        Apply(connection, [txn]);

        Scalar(connection, "SELECT id FROM t").AsInteger().Should().Be(
            99, "id is not a rowid alias for the column-level DESC form, so its decoded value must not be overwritten by the wire rowid");
    }

    [Test]
    public void TableLevelPrimaryKeyDescStillCountsAsTheRowidAlias()
    {
        // The table-constraint form PRIMARY KEY(id DESC) is NOT subject to the column-level
        // DESC exception and still aliases the rowid, per SQLite's documented behavior: only
        // three example declarations lose the alias, and PRIMARY KEY(x DESC) as a table
        // constraint is explicitly NOT one of them.
        using var connection = OpenConnection();
        Exec(connection, "CREATE TABLE t(id INTEGER, name TEXT, PRIMARY KEY(id DESC))");

        var record = SqliteRecordCodecTestHelper.EncodeRow(SqlValue.Null, SqlValue.Text("alice"));
        var txn = SingleOpTxn(RowOp(ManagedReplicaLogicalOpType.UpsertRow, "t", rowId: 5, record));
        Apply(connection, [txn]);

        Scalar(connection, "SELECT id FROM t").AsInteger().Should().Be(
            5, "PRIMARY KEY(id DESC) as a table constraint still aliases the rowid");
    }

    [Test]
    public void UpsertIntoAWithoutRowidTableUsesTheDeclaredPrimaryKeyNotTheWireRowid()
    {
        using var connection = OpenConnection();
        Exec(connection, "CREATE TABLE t(x TEXT PRIMARY KEY, y TEXT) WITHOUT ROWID");

        // WITHOUT ROWID never has a rowid alias, even though the PK is a single column: the wire
        // rowid (99, deliberately implausible) must NOT be substituted into any column value.
        var record = SqliteRecordCodecTestHelper.EncodeRow(SqlValue.Text("k1"), SqlValue.Text("v1"));
        var txn = SingleOpTxn(RowOp(ManagedReplicaLogicalOpType.UpsertRow, "t", rowId: 99, record));
        Apply(connection, [txn]);

        Scalar(connection, "SELECT y FROM t WHERE x = 'k1'").AsText().Should().Be("v1");
    }

    [Test]
    public void DeleteWithoutAKeyOnAWithoutRowidTableIsRefused()
    {
        // A WITHOUT ROWID table has no rowid at all to delete by; a delete that arrives without a
        // primary-key projection (and no before image) must fail rather than guess.
        using var connection = OpenConnection();
        Exec(connection, "CREATE TABLE t(x TEXT PRIMARY KEY) WITHOUT ROWID");
        Exec(connection, "INSERT INTO t VALUES ('k1')");

        var txn = SingleOpTxn(RowOp(ManagedReplicaLogicalOpType.DeleteRow, "t", rowId: 1, record: []));
        Action act = () => Apply(connection, [txn]);
        act.Should().Throw<InvalidDataException>().WithMessage("*WITHOUT ROWID*");
    }

    [Test]
    public void UpsertHandlesATableWithAGenuineRowidNamedColumnThatIsNotTheAlias()
    {
        // A table with no INTEGER PRIMARY KEY alias may still declare a real column literally
        // named "rowid"; SQLite then requires "_rowid_"/"oid" to reach the true pseudo-column.
        using var connection = OpenConnection();
        Exec(connection, "CREATE TABLE t(\"rowid\" TEXT, y TEXT)");

        var record = SqliteRecordCodecTestHelper.EncodeRow(SqlValue.Text("not-the-alias"), SqlValue.Text("v1"));
        var txn = SingleOpTxn(RowOp(ManagedReplicaLogicalOpType.UpsertRow, "t", rowId: 42, record));
        Apply(connection, [txn]);

        Scalar(connection, "SELECT \"rowid\" FROM t WHERE _rowid_ = 42").AsText().Should().Be("not-the-alias");
    }

    [Test]
    public void DeleteWithANullPrimaryKeyValueMatchesTheNullRowNullSafely()
    {
        // Non-STRICT SQLite does not implicitly forbid NULL in a declared (non-rowid-alias)
        // PRIMARY KEY column. An ordinary "col = ?" predicate would silently match zero rows for
        // a NULL key (three-valued SQL logic); the delete must use NULL-safe equality instead.
        using var connection = OpenConnection();
        Exec(connection, "CREATE TABLE t(x TEXT, y TEXT, PRIMARY KEY(x, y))");
        Exec(connection, "INSERT INTO t VALUES (NULL, 'only-y')");

        var key = SqliteRecordCodecTestHelper.EncodeRow(SqlValue.Null, SqlValue.Text("only-y"));
        var txn = SingleOpTxn(RowOp(ManagedReplicaLogicalOpType.DeleteRow, "t", rowId: 0, record: key));
        Apply(connection, [txn]);

        RowCount(connection, "t").Should().Be(0);
    }

    [Test]
    public void KeyChangingReplayDeletesTheOldCompositeKeyBeforeUpsertingTheNewKey()
    {
        using var connection = OpenConnection();
        Exec(connection, "CREATE TABLE t(tenant TEXT, code TEXT, value TEXT, PRIMARY KEY(tenant, code))");
        Exec(connection, "INSERT INTO t VALUES ('a', 'old', 'local'), ('a', 'keep', 'untouched')");

        var oldKey = SqliteRecordCodecTestHelper.EncodeRow(SqlValue.Text("a"), SqlValue.Text("old"));
        var newRecord = SqliteRecordCodecTestHelper.EncodeRow(
            SqlValue.Text("a"),
            SqlValue.Text("new"),
            SqlValue.Text("remote"));
        var txn = new ManagedReplicaLogicalTxn(
            2,
            1,
            [
                RowOp(ManagedReplicaLogicalOpType.DeleteRow, "t", rowId: 999, oldKey),
                RowOp(ManagedReplicaLogicalOpType.UpsertRow, "t", rowId: 999, newRecord),
            ],
            string.Empty);

        Apply(connection, [txn]);

        Scalar(connection, "SELECT COUNT(*) FROM t WHERE tenant = 'a' AND code = 'old'").AsInteger()
            .Should().Be(0);
        Scalar(connection, "SELECT value FROM t WHERE tenant = 'a' AND code = 'new'").AsText()
            .Should().Be("remote");
        Scalar(connection, "SELECT value FROM t WHERE tenant = 'a' AND code = 'keep'").AsText()
            .Should().Be("untouched");
    }

    [Test]
    public void HeaderUpdateReplaysBothPragmasWhenBothArePresent()
    {
        using var connection = OpenConnection();
        var txn = SingleOpTxn(new ManagedReplicaLogicalOp(
            ManagedReplicaLogicalOpType.UpdateHeader,
            TableName: string.Empty,
            RowId: 0,
            Record: [],
            Sql: string.Empty,
            UserVersion: 7,
            ApplicationId: 99,
            SchemaAction: null,
            SchemaKind: null,
            SchemaName: string.Empty,
            StableTableId: 0));

        Apply(connection, [txn]);

        Scalar(connection, "PRAGMA user_version").AsInteger().Should().Be(7);
        Scalar(connection, "PRAGMA application_id").AsInteger().Should().Be(99);
    }

    [Test]
    public void HeaderUpdateReplaysNegativeValuesCorrectly()
    {
        // user_version/application_id are signed int32 in real SQLite; replaying -1/int.MinValue
        // must round-trip through PRAGMA exactly, not wrap into a huge unsigned value.
        using var connection = OpenConnection();
        var txn = SingleOpTxn(new ManagedReplicaLogicalOp(
            ManagedReplicaLogicalOpType.UpdateHeader,
            TableName: string.Empty,
            RowId: 0,
            Record: [],
            Sql: string.Empty,
            UserVersion: -1,
            ApplicationId: int.MinValue,
            SchemaAction: null,
            SchemaKind: null,
            SchemaName: string.Empty,
            StableTableId: 0));

        Apply(connection, [txn]);

        Scalar(connection, "PRAGMA user_version").AsInteger().Should().Be(-1);
        Scalar(connection, "PRAGMA application_id").AsInteger().Should().Be(int.MinValue);
    }

    [Test]
    public void HeaderUpdateReplaysNegativeValuesCorrectlyUnderANonInvariantCulture()
    {
        // sv-SE formats a negative number's sign as U+2212 (MINUS SIGN) rather than ASCII
        // U+002D (HYPHEN-MINUS) under default ToString()/interpolation formatting. The emitted
        // PRAGMA text must use the invariant culture explicitly, or this would produce invalid
        // SQL syntax for a negative user_version/application_id.
        var previousCulture = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("sv-SE");
        try
        {
            using var connection = OpenConnection();
            var txn = SingleOpTxn(new ManagedReplicaLogicalOp(
                ManagedReplicaLogicalOpType.UpdateHeader,
                TableName: string.Empty,
                RowId: 0,
                Record: [],
                Sql: string.Empty,
                UserVersion: -1,
                ApplicationId: -42,
                SchemaAction: null,
                SchemaKind: null,
                SchemaName: string.Empty,
                StableTableId: 0));

            Action act = () => Apply(connection, [txn]);
            act.Should().NotThrow();

            Scalar(connection, "PRAGMA user_version").AsInteger().Should().Be(-1);
            Scalar(connection, "PRAGMA application_id").AsInteger().Should().Be(-42);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [Test]
    public void InternalTablesAreNeverReplayed()
    {
        using var connection = OpenConnection();
        // No table created for turso_cdc/__turso_internal_x/sqlite_stat1: if the filter failed to
        // exclude them, replay would throw (missing table) instead of silently skipping.
        var ops = new[]
        {
            RowOp(ManagedReplicaLogicalOpType.UpsertRow, "turso_cdc", 1, []),
            RowOp(ManagedReplicaLogicalOpType.UpsertRow, "__turso_internal_x", 1, []),
            RowOp(ManagedReplicaLogicalOpType.UpsertRow, "sqlite_stat1", 1, []),
            RowOp(ManagedReplicaLogicalOpType.UpsertRow, "turso_sync_last_change_id", 1, []),
        };
        var txn = new ManagedReplicaLogicalTxn(1, 1, ops, string.Empty);

        Action act = () => Apply(connection, [txn]);
        act.Should().NotThrow();
    }

    [Test]
    public void TransactionsThatOriginateFromTheExcludedClientAreSkippedEntirely()
    {
        using var connection = OpenConnection();
        Exec(connection, "CREATE TABLE t(a TEXT)");

        var record = SqliteRecordCodecTestHelper.EncodeRow(SqlValue.Text("mine"));
        var txn = new ManagedReplicaLogicalTxn(
            1,
            1,
            [RowOp(ManagedReplicaLogicalOpType.UpsertRow, "t", 1, record)],
            OriginClientId: "client-a");

        var result = ManagedReplicaLogicalReplayer.Apply(
            connection,
            [txn],
            new Dictionary<ulong, string>(),
            excludedClientId: "client-a",
            CancellationToken.None);

        result.TransactionCount.Should().Be(0);
        RowCount(connection, "t").Should().Be(0);
    }

    [Test]
    public void TransactionsAcknowledgedViaTheTursoSyncLastChangeIdFallbackAreSkipped()
    {
        using var connection = OpenConnection();
        Exec(connection, "CREATE TABLE t(a TEXT)");

        var ackRecord = SqliteRecordCodecTestHelper.EncodeRow(SqlValue.Text("client-a"), SqlValue.Integer(0), SqlValue.Integer(5));
        var record = SqliteRecordCodecTestHelper.EncodeRow(SqlValue.Text("mine"));
        var txn = new ManagedReplicaLogicalTxn(
            1,
            1,
            [
                RowOp(ManagedReplicaLogicalOpType.UpsertRow, "t", 1, record),
                RowOp(ManagedReplicaLogicalOpType.UpsertRow, "turso_sync_last_change_id", 1, ackRecord),
            ],
            OriginClientId: string.Empty); // no portable client metadata; must fall back to the ack row

        var result = ManagedReplicaLogicalReplayer.Apply(
            connection,
            [txn],
            new Dictionary<ulong, string>(),
            excludedClientId: "client-a",
            CancellationToken.None);

        result.TransactionCount.Should().Be(0);
        RowCount(connection, "t").Should().Be(0);
    }

    [Test]
    public void TransactionsFromOtherClientsAreReplayedNormally()
    {
        using var connection = OpenConnection();
        Exec(connection, "CREATE TABLE t(a TEXT)");

        var record = SqliteRecordCodecTestHelper.EncodeRow(SqlValue.Text("theirs"));
        var txn = new ManagedReplicaLogicalTxn(
            1,
            1,
            [RowOp(ManagedReplicaLogicalOpType.UpsertRow, "t", 1, record)],
            OriginClientId: "client-b");

        var result = ManagedReplicaLogicalReplayer.Apply(
            connection,
            [txn],
            new Dictionary<ulong, string>(),
            excludedClientId: "client-a",
            CancellationToken.None);

        result.TransactionCount.Should().Be(1);
        RowCount(connection, "t").Should().Be(1);
    }

    [Test]
    public void RowOperationsResolveTheTableNameFromTheStableTableIdMap()
    {
        using var connection = OpenConnection();
        Exec(connection, "CREATE TABLE widgets(a TEXT)");

        var record = SqliteRecordCodecTestHelper.EncodeRow(SqlValue.Text("v"));
        var op = new ManagedReplicaLogicalOp(
            ManagedReplicaLogicalOpType.UpsertRow,
            TableName: string.Empty, // omitted; must resolve via stable_table_id
            RowId: 1,
            Record: record,
            Sql: string.Empty,
            UserVersion: null,
            ApplicationId: null,
            SchemaAction: null,
            SchemaKind: null,
            SchemaName: string.Empty,
            StableTableId: 42);
        var txn = new ManagedReplicaLogicalTxn(1, 1, [op], string.Empty);

        var initialMap = new Dictionary<ulong, string> { [42] = "widgets" };
        var result = ManagedReplicaLogicalReplayer.Apply(connection, [txn], initialMap, string.Empty, CancellationToken.None);

        result.TableNamesByStableId.Should().ContainKey(42).WhoseValue.Should().Be("widgets");
        RowCount(connection, "widgets").Should().Be(1);
    }

    [Test]
    public void RowOperationWithAnUnresolvableStableTableIdThrows()
    {
        using var connection = OpenConnection();
        var op = new ManagedReplicaLogicalOp(
            ManagedReplicaLogicalOpType.UpsertRow,
            TableName: string.Empty,
            RowId: 1,
            Record: [],
            Sql: string.Empty,
            UserVersion: null,
            ApplicationId: null,
            SchemaAction: null,
            SchemaKind: null,
            SchemaName: string.Empty,
            StableTableId: 7);
        var txn = new ManagedReplicaLogicalTxn(1, 1, [op], string.Empty);

        Action act = () => ManagedReplicaLogicalReplayer.Apply(
            connection, [txn], new Dictionary<ulong, string>(), string.Empty, CancellationToken.None);
        act.Should().Throw<InvalidDataException>().WithMessage("*unknown stable table id*");
    }

    [Test]
    public void SchemaOperationsUpdateAndRemoveTheStableTableIdMap()
    {
        using var connection = OpenConnection();

        var create = SingleOpTxn(new ManagedReplicaLogicalOp(
            ManagedReplicaLogicalOpType.Schema,
            TableName: string.Empty,
            RowId: 0,
            Record: [],
            Sql: "CREATE TABLE widgets(a TEXT)",
            UserVersion: null,
            ApplicationId: null,
            SchemaAction: ManagedReplicaLogicalSchemaAction.Create,
            SchemaKind: ManagedReplicaLogicalSchemaKind.Table,
            SchemaName: "widgets",
            StableTableId: 42));

        var afterCreate = ManagedReplicaLogicalReplayer.Apply(
            connection, [create], new Dictionary<ulong, string>(), string.Empty, CancellationToken.None);
        afterCreate.TableNamesByStableId.Should().ContainKey(42).WhoseValue.Should().Be("widgets");

        var drop = SingleOpTxn(new ManagedReplicaLogicalOp(
            ManagedReplicaLogicalOpType.Schema,
            TableName: string.Empty,
            RowId: 0,
            Record: [],
            Sql: string.Empty,
            UserVersion: null,
            ApplicationId: null,
            SchemaAction: ManagedReplicaLogicalSchemaAction.Drop,
            SchemaKind: ManagedReplicaLogicalSchemaKind.Table,
            SchemaName: "widgets",
            StableTableId: 42));

        var afterDrop = ManagedReplicaLogicalReplayer.Apply(
            connection, [drop], afterCreate.TableNamesByStableId, string.Empty, CancellationToken.None);
        afterDrop.TableNamesByStableId.Should().NotContainKey(42);
    }

    [Test]
    public void ExcludedTransactionsDoNotUpdateTheStableTableIdMap()
    {
        using var connection = OpenConnection();
        var create = SingleOpTxn(new ManagedReplicaLogicalOp(
            ManagedReplicaLogicalOpType.Schema,
            TableName: string.Empty,
            RowId: 0,
            Record: [],
            Sql: "CREATE TABLE widgets(a TEXT)",
            UserVersion: null,
            ApplicationId: null,
            SchemaAction: ManagedReplicaLogicalSchemaAction.Create,
            SchemaKind: ManagedReplicaLogicalSchemaKind.Table,
            SchemaName: "widgets",
            StableTableId: 42)) with
        { OriginClientId = "client-a" };

        var result = ManagedReplicaLogicalReplayer.Apply(
            connection, [create], new Dictionary<ulong, string>(), "client-a", CancellationToken.None);

        result.TableNamesByStableId.Should().NotContainKey(42);
        result.TransactionCount.Should().Be(0);
    }

    [Test]
    public void CapturePendingLocalRowChangesCapturesTheCurrentRowForANonDeleteFinalOperation()
    {
        using var connection = OpenConnection();
        Exec(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, name TEXT)");
        Exec(connection, "INSERT INTO t VALUES (1, 'local-value')");

        var pending = new[] { ReplicaLocalChange.Row(SqliteChangeOperation.Insert, "main", "t", 1) };
        var captured = ManagedReplicaLogicalReplayer.CapturePendingLocalRowChanges(connection, pending);

        captured.Should().ContainSingle();
        captured[0].TableName.Should().Be("t");
        captured[0].RowId.Should().Be(1);
        captured[0].IsDelete.Should().BeFalse();
        captured[0].CapturedValues.Should().NotBeNull();
        captured[0].CapturedValues![1].AsText().Should().Be("local-value");
    }

    [Test]
    public void CapturePendingLocalRowChangesCollapsesMultipleEntriesToTheFinalOperation()
    {
        using var connection = OpenConnection();
        Exec(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, name TEXT)");
        Exec(connection, "INSERT INTO t VALUES (1, 'final-value')");

        // Insert then update recorded separately in the journal: only the FINAL state matters.
        var pending = new[]
        {
            ReplicaLocalChange.Row(SqliteChangeOperation.Insert, "main", "t", 1),
            ReplicaLocalChange.Row(SqliteChangeOperation.Update, "main", "t", 1),
        };
        var captured = ManagedReplicaLogicalReplayer.CapturePendingLocalRowChanges(connection, pending);

        captured.Should().ContainSingle();
        captured[0].CapturedValues![1].AsText().Should().Be("final-value");
    }

    [Test]
    public void CapturePendingLocalRowChangesMarksATrailingDeleteWithoutReadingCurrentState()
    {
        using var connection = OpenConnection();
        Exec(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, name TEXT)");
        // Row already gone locally: the last recorded op for rowid 1 was a delete.

        var pending = new[]
        {
            ReplicaLocalChange.Row(SqliteChangeOperation.Insert, "main", "t", 1),
            ReplicaLocalChange.Row(SqliteChangeOperation.Delete, "main", "t", 1),
        };
        var captured = ManagedReplicaLogicalReplayer.CapturePendingLocalRowChanges(connection, pending);

        captured.Should().ContainSingle();
        captured[0].IsDelete.Should().BeTrue();
        captured[0].CapturedValues.Should().BeNull();
    }

    [Test]
    public void CapturePendingLocalRowChangesSkipsInternalTables()
    {
        using var connection = OpenConnection();
        var pending = new[]
        {
            ReplicaLocalChange.Row(SqliteChangeOperation.Insert, "main", "turso_cdc", 1),
            ReplicaLocalChange.Row(SqliteChangeOperation.Insert, "main", "sqlite_sequence", 1),
        };

        var captured = ManagedReplicaLogicalReplayer.CapturePendingLocalRowChanges(connection, pending);

        captured.Should().BeEmpty();
    }

    [Test]
    public void ReplayPendingLocalRowChangesReapliesAnUpsertOnTopOfARemoteOverwrite()
    {
        using var connection = OpenConnection();
        Exec(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, name TEXT)");
        Exec(connection, "INSERT INTO t VALUES (1, 'local-value')");

        var pending = new[] { ReplicaLocalChange.Row(SqliteChangeOperation.Insert, "main", "t", 1) };
        var captured = ManagedReplicaLogicalReplayer.CapturePendingLocalRowChanges(connection, pending);

        // Simulate the remote pull overwriting the same row with different data.
        Exec(connection, "UPDATE t SET name = 'remote-value' WHERE id = 1");

        ManagedReplicaLogicalReplayer.ReplayPendingLocalRowChanges(connection, captured, CancellationToken.None);

        // The precollected local value wins, since it is reapplied after the remote overwrite.
        Scalar(connection, "SELECT name FROM t WHERE id = 1").AsText().Should().Be("local-value");
    }

    [Test]
    public void ReplayPendingLocalRowChangesReappliesADeleteEvenIfARemotePullResurrectedTheRow()
    {
        using var connection = OpenConnection();
        Exec(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, name TEXT)");

        var pending = new[] { ReplicaLocalChange.Row(SqliteChangeOperation.Delete, "main", "t", 1) };
        var captured = ManagedReplicaLogicalReplayer.CapturePendingLocalRowChanges(connection, pending);

        // Simulate the remote pull inserting a row the server still had at that rowid.
        Exec(connection, "INSERT INTO t VALUES (1, 'remote-resurrected')");

        ManagedReplicaLogicalReplayer.ReplayPendingLocalRowChanges(connection, captured, CancellationToken.None);

        RowCount(connection, "t").Should().Be(0);
    }

    [Test]
    public void ReplayPendingLocalRowChangesLeavesUnrelatedRemoteRowsIntact()
    {
        using var connection = OpenConnection();
        Exec(connection, "CREATE TABLE local_items(id INTEGER PRIMARY KEY, x TEXT)");
        Exec(connection, "CREATE TABLE remote_items(id INTEGER PRIMARY KEY, x TEXT)");
        Exec(connection, "INSERT INTO local_items VALUES (1, 'local')");

        var pending = new[] { ReplicaLocalChange.Row(SqliteChangeOperation.Insert, "main", "local_items", 1) };
        var captured = ManagedReplicaLogicalReplayer.CapturePendingLocalRowChanges(connection, pending);

        // Simulate the remote apply touching a disjoint table.
        Exec(connection, "INSERT INTO remote_items VALUES (2, 'remote')");

        ManagedReplicaLogicalReplayer.ReplayPendingLocalRowChanges(connection, captured, CancellationToken.None);

        Scalar(connection, "SELECT x FROM local_items WHERE id = 1").AsText().Should().Be("local");
        Scalar(connection, "SELECT x FROM remote_items WHERE id = 2").AsText().Should().Be("remote");
    }

    [Test]
    public void CapturePendingLocalRowChangesRejectsATextPrimaryKeyDeleteWhoseRowidCanBeRecycled()
    {
        // A declared non-alias (TEXT) PRIMARY KEY: after deleting row "b" at rowid 2, a remote
        // insert of a different key "c" can reuse rowid 2. Retaining only the deleted rowid would
        // make post-pull reconciliation delete "c", so capture must fail closed before mutation
        // and wait until the pending delete has been pushed.
        using var connection = OpenConnection();
        Exec(connection, "CREATE TABLE t(x TEXT PRIMARY KEY, y TEXT)");
        Exec(connection, "INSERT INTO t VALUES ('a', 'va'), ('b', 'vb')");
        var localRowId = Scalar(connection, "SELECT rowid FROM t WHERE x = 'b'").AsInteger();
        localRowId.Should().Be(2);
        Exec(connection, "DELETE FROM t WHERE x = 'b'");

        var pending = new[] { ReplicaLocalChange.Row(SqliteChangeOperation.Delete, "main", "t", localRowId) };
        Action act = () => ManagedReplicaLogicalReplayer.CapturePendingLocalRowChanges(connection, pending);

        act.Should().Throw<NotSupportedException>()
            .WithMessage("*pending local delete*schema does not prove rowid*Push*retry*");
        Scalar(connection, "SELECT COUNT(*) FROM t WHERE x = 'a'").AsInteger().Should().Be(1);
        Scalar(connection, "SELECT COUNT(*) FROM t WHERE x = 'b'").AsInteger().Should().Be(0);
    }

    [Test]
    public void ReplayPendingLocalRowChangesUsesAJournaledPrimaryKeyForANonAliasDelete()
    {
        using var connection = OpenConnection();
        Exec(connection, "CREATE TABLE t(x TEXT PRIMARY KEY, y TEXT)");
        Exec(connection, "INSERT INTO t VALUES ('a', 'va'), ('b', 'vb')");
        var localRowId = Scalar(connection, "SELECT rowid FROM t WHERE x = 'b'").AsInteger();
        Exec(connection, "DELETE FROM t WHERE x = 'b'");

        var beforeRecord = SqliteRecordCodecTestHelper.EncodeRow(SqlValue.Text("b"), SqlValue.Text("vb"));
        ReplicaLocalChange[] pending =
        [
            ReplicaLocalChange.Row(
                SqliteChangeOperation.Delete,
                "main",
                "t",
                localRowId,
                beforeRecord),
        ];
        var captured = ManagedReplicaLogicalReplayer.CapturePendingLocalRowChanges(connection, pending);

        // The remote may reuse the deleted row's old rowid for a different primary key. The
        // journaled key must delete only "b", which is already absent, not the new row "c".
        Exec(connection, "INSERT INTO t VALUES ('c', 'vc')");
        Scalar(connection, "SELECT rowid FROM t WHERE x = 'c'").AsInteger().Should().Be(localRowId);

        ManagedReplicaLogicalReplayer.ReplayPendingLocalRowChanges(connection, captured, CancellationToken.None);

        Scalar(connection, "SELECT y FROM t WHERE x = 'c'").AsText().Should().Be("vc");
        RowCount(connection, "t").Should().Be(2);
    }

    [Test]
    public void ReplayPendingLocalRowChangesSkipsReconciliationWhenTheRemoteDroppedTheTable()
    {
        using var connection = OpenConnection();
        Exec(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, name TEXT)");
        Exec(connection, "INSERT INTO t VALUES (1, 'local-value')");

        var pending = new[] { ReplicaLocalChange.Row(SqliteChangeOperation.Insert, "main", "t", 1) };
        var captured = ManagedReplicaLogicalReplayer.CapturePendingLocalRowChanges(connection, pending);

        // Simulate the remote transaction dropping the table entirely.
        Exec(connection, "DROP TABLE t");

        Action act = () => ManagedReplicaLogicalReplayer.ReplayPendingLocalRowChanges(connection, captured, CancellationToken.None);
        act.Should().NotThrow("reconciliation must skip a captured change whose table no longer exists, not abort the whole apply");
        TableExists(connection, "t").Should().BeFalse();
    }

    [Test]
    public void ReplayPendingLocalRowChangesSkipsADeleteReconciliationWhenTheRemoteDroppedTheTable()
    {
        using var connection = OpenConnection();
        Exec(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, name TEXT)");
        Exec(connection, "INSERT INTO t VALUES (1, 'local-value')");
        Exec(connection, "DELETE FROM t WHERE id = 1");

        var pending = new[] { ReplicaLocalChange.Row(SqliteChangeOperation.Delete, "main", "t", 1) };
        var captured = ManagedReplicaLogicalReplayer.CapturePendingLocalRowChanges(connection, pending);

        Exec(connection, "DROP TABLE t");

        Action act = () => ManagedReplicaLogicalReplayer.ReplayPendingLocalRowChanges(connection, captured, CancellationToken.None);
        act.Should().NotThrow();
    }

    [Test]
    public void ApplyIgnoresPendingLocalExtraColumnsDuringTableRefreshThenReappliesThem()
    {
        using var connection = OpenConnection();
        Exec(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, x TEXT)");
        Exec(connection, "ALTER TABLE t ADD COLUMN extra TEXT");
        Exec(connection, "INSERT INTO t(id, x, extra) VALUES (1, 'local', 'kept')");

        var pending = ManagedReplicaLogicalReplayer.CollectPendingAddColumns(
        [
            ReplicaLocalChange.Schema("ALTER TABLE t ADD COLUMN extra TEXT"),
        ]);
        pending.Should().ContainSingle();

        var schemaOp = SchemaOp(
            ManagedReplicaLogicalSchemaAction.Refresh,
            ManagedReplicaLogicalSchemaKind.Table,
            "t",
            "CREATE TABLE t(id INTEGER PRIMARY KEY, x TEXT)");
        var remoteRecord = SqliteRecordCodecTestHelper.EncodeRow(SqlValue.Integer(2), SqlValue.Text("remote"));
        var rowOp = RowOp(ManagedReplicaLogicalOpType.UpsertRow, "t", 2, remoteRecord);
        var txn = new ManagedReplicaLogicalTxn(1, 1, [schemaOp, rowOp], string.Empty);

        Action apply = () => ManagedReplicaLogicalReplayer.Apply(
            connection,
            [txn],
            new Dictionary<ulong, string>(),
            string.Empty,
            CancellationToken.None,
            pending);
        apply.Should().NotThrow();

        ManagedReplicaLogicalReplayer.ReplayPendingLocalAddColumns(connection, pending, CancellationToken.None);

        ColumnNames(connection, "t").Should().Equal("id", "x", "extra");
        Scalar(connection, "SELECT extra FROM t WHERE id = 1").AsText().Should().Be("kept");
        Scalar(connection, "SELECT x FROM t WHERE id = 2").AsText().Should().Be("remote");
    }

    // --- helpers ---

    private static IManagedConnectionAdapter OpenConnection()
    {
        var database = ManagedDatabaseAdapter.Open(":memory:");
        return database.Connect();
    }

    private static void Apply(IManagedConnectionAdapter connection, IReadOnlyList<ManagedReplicaLogicalTxn> transactions)
        => ManagedReplicaLogicalReplayer.Apply(
            connection, transactions, new Dictionary<ulong, string>(), string.Empty, CancellationToken.None);

    private static ManagedReplicaLogicalTxn SingleOpTxn(ManagedReplicaLogicalOp op)
        => new(1, 1, [op], string.Empty);

    private static ManagedReplicaLogicalOp SchemaOp(
        ManagedReplicaLogicalSchemaAction action,
        ManagedReplicaLogicalSchemaKind kind,
        string name,
        string sql)
        => new(
            ManagedReplicaLogicalOpType.Schema,
            TableName: string.Empty,
            RowId: 0,
            Record: [],
            Sql: sql,
            UserVersion: null,
            ApplicationId: null,
            SchemaAction: action,
            SchemaKind: kind,
            SchemaName: name,
            StableTableId: 0);

    private static ManagedReplicaLogicalOp RowOp(ManagedReplicaLogicalOpType opType, string table, long rowId, byte[] record)
        => new(
            opType,
            TableName: table,
            RowId: rowId,
            Record: record,
            Sql: string.Empty,
            UserVersion: null,
            ApplicationId: null,
            SchemaAction: null,
            SchemaKind: null,
            SchemaName: string.Empty,
            StableTableId: 0);

    private static void Exec(IManagedConnectionAdapter connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step();
    }

    private static SqlValue Scalar(IManagedConnectionAdapter connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Row);
        return statement.GetValue(0);
    }

    private static long RowCount(IManagedConnectionAdapter connection, string table)
        => Scalar(connection, $"SELECT COUNT(*) FROM \"{table}\"").AsInteger();

    private static bool TableExists(IManagedConnectionAdapter connection, string table)
    {
        using var statement = connection.Prepare("SELECT 1 FROM sqlite_schema WHERE type = 'table' AND name = ?");
        statement.Bind(1, SqlValue.Text(table));
        return statement.Step() == StatementStepResult.Row;
    }

    private static List<string> ColumnNames(IManagedConnectionAdapter connection, string table)
    {
        using var statement = connection.Prepare("SELECT name FROM pragma_table_info(?)");
        statement.Bind(1, SqlValue.Text(table));
        var names = new List<string>();
        while (statement.Step() == StatementStepResult.Row)
            names.Add(statement.GetValue(0).AsText());
        return names;
    }
}
