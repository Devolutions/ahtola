using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Execution;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

/// <summary>
/// Direct coverage for the transaction-local schema state a DDL program runs against: the shared ordered
/// <c>sqlite_schema</c> row model, the staged header cookies and root plan, and the concrete
/// <see cref="ManagedSchemaOperations"/> binding that applies schema opcodes to them.
/// </summary>
/// <remarks>
/// No SQL DDL is routed through this path yet, so every case drives the stage and its operations directly.
/// The point of these tests is the boundary: staging must be complete enough to execute real schema
/// opcodes, and discardable enough that a failed program leaves the live catalog, the header and the file
/// exactly as it found them.
/// </remarks>
public sealed class ManagedSchemaStagingTests
{
    // ---------------------------------------------------------------- ordered row model

    [Test]
    public void SchemaRowsKeepInsertionOrderAndAssignSequentialRowids()
    {
        var rows = new ManagedSchemaRowSet();

        var first = rows.Add(new ManagedSchemaRow("table", "orders", "orders", 2, "CREATE TABLE orders(id)"));
        var second = rows.Add(new ManagedSchemaRow("index", "orders_id", "orders", 3, "CREATE INDEX orders_id ON orders(id)"));
        var third = rows.Add(new ManagedSchemaRow("view", "orders_view", "orders_view", 0, "CREATE VIEW orders_view AS SELECT 1"));

        first.RowId.Should().Be(1);
        second.RowId.Should().Be(2);
        third.RowId.Should().Be(3);
        rows.Rows.Select(row => row.Name).Should().Equal("orders", "orders_id", "orders_view");
        rows.NextRowId.Should().Be(4);
    }

    [Test]
    public void RemovingASchemaRowClosesItsSlotWithoutRenumberingTheSurvivors()
    {
        var rows = ManagedSchemaRowSet.FromOrderedRows(
        [
            new ManagedSchemaRow("table", "a", "a", 2, "CREATE TABLE a(id)"),
            new ManagedSchemaRow("table", "b", "b", 3, "CREATE TABLE b(id)"),
            new ManagedSchemaRow("table", "c", "c", 4, "CREATE TABLE c(id)"),
        ]);

        rows.Remove("b").Should().BeTrue();
        rows.Remove("b").Should().BeFalse();

        rows.Rows.Select(row => (row.Name, row.RowId)).Should().Equal(("a", 1L), ("c", 3L));
        rows.TryGet("c", out var moved).Should().BeTrue();
        moved.RowId.Should().Be(3);
        rows.NextRowId.Should().Be(4);
    }

    [Test]
    public void ReplacingASchemaRowKeepsItsSlotAndRowid()
    {
        var rows = ManagedSchemaRowSet.FromOrderedRows(
        [
            new ManagedSchemaRow("table", "a", "a", 2, "CREATE TABLE a(id)"),
            new ManagedSchemaRow("table", "b", "b", 3, "CREATE TABLE b(id)"),
        ]);

        var replaced = rows.Replace(new ManagedSchemaRow("table", "a", "a", 2, "CREATE TABLE a(id, extra)"));

        replaced.RowId.Should().Be(1);
        rows.Rows.Select(row => row.Name).Should().Equal("a", "b");
        rows.Rows[0].Sql.Should().Be("CREATE TABLE a(id, extra)");
    }

    [Test]
    public void RenamingASchemaRowRewritesEveryRowThatPointedAtTheOldName()
    {
        var rows = ManagedSchemaRowSet.FromOrderedRows(
        [
            new ManagedSchemaRow("table", "orders", "orders", 2, "CREATE TABLE orders(id)"),
            new ManagedSchemaRow("index", "orders_id", "orders", 3, "CREATE INDEX orders_id ON orders(id)"),
            new ManagedSchemaRow("trigger", "orders_audit", "orders", 0, "CREATE TRIGGER orders_audit AFTER INSERT ON orders BEGIN SELECT 1; END"),
        ]);

        rows.Rename("orders", "invoices");

        rows.Rows.Select(row => (row.Name, row.TableName, row.RowId)).Should().Equal(
            ("invoices", "invoices", 1L),
            ("orders_id", "invoices", 2L),
            ("orders_audit", "invoices", 3L));
    }

    [Test]
    public void SchemaRowSetRejectsDuplicateNamesAndStructurallyInvalidRows()
    {
        var rows = ManagedSchemaRowSet.FromOrderedRows(
            [new ManagedSchemaRow("table", "a", "a", 2, "CREATE TABLE a(id)")]);

        Action duplicate = () => rows.Add(new ManagedSchemaRow("view", "a", "a", 0, "CREATE VIEW a AS SELECT 1"));
        Action schemaRootPage = () => rows.Add(new ManagedSchemaRow("table", "b", "b", 1, "CREATE TABLE b(id)"));
        Action rootlessIndex = () => rows.Add(new ManagedSchemaRow("index", "i", "a", 0, "CREATE INDEX i ON a(id)"));
        Action rootedView = () => rows.Add(new ManagedSchemaRow("view", "v", "v", 5, "CREATE VIEW v AS SELECT 1"));
        Action unknownType = () => rows.Add(new ManagedSchemaRow("sequence", "s", "s", 0, null));
        Action missingReplace = () => rows.Replace(new ManagedSchemaRow("table", "z", "z", 9, "CREATE TABLE z(id)"));

        duplicate.Should().Throw<ManagedSchemaRowException>().WithMessage("*already contains an object named 'a'*");
        schemaRootPage.Should().Throw<ManagedSchemaRowException>().WithMessage("*cannot use page 1*");
        rootlessIndex.Should().Throw<ManagedSchemaRowException>().WithMessage("*index 'i' must have a rootpage*");
        rootedView.Should().Throw<ManagedSchemaRowException>().WithMessage("*view 'v' must have rootpage 0*");
        unknownType.Should().Throw<ManagedSchemaRowException>().WithMessage("*unsupported type 'sequence'*");
        missingReplace.Should().Throw<ManagedSchemaRowException>().WithMessage("*no object named 'z' to replace*");
    }

    [Test]
    public void SchemaRowValuesUseTheOnDiskColumnOrderAndNullSqlForImplicitIndexes()
    {
        var explicitRow = new ManagedSchemaRow("index", "orders_id", "orders", 7, "CREATE INDEX orders_id ON orders(id)");
        var implicitRow = new ManagedSchemaRow("index", "sqlite_autoindex_orders_1", "orders", 8, null);

        explicitRow.ToValues().Should().Equal(
            SqlValue.Text("index"),
            SqlValue.Text("orders_id"),
            SqlValue.Text("orders"),
            SqlValue.Integer(7),
            SqlValue.Text("CREATE INDEX orders_id ON orders(id)"));
        implicitRow.ToValues()[4].Should().Be(SqlValue.Null);
        implicitRow.IsImplicitIndex.Should().BeTrue();
        ManagedSchemaRow.CreateColumnNames().Should().Equal("type", "name", "tbl_name", "rootpage", "sql");
    }

    [Test]
    public void SchemaRowSetCloneIsIndependentOfItsSource()
    {
        var rows = ManagedSchemaRowSet.FromOrderedRows(
            [new ManagedSchemaRow("table", "a", "a", 2, "CREATE TABLE a(id)")]);
        var clone = rows.Clone();

        clone.Add(new ManagedSchemaRow("table", "b", "b", 3, "CREATE TABLE b(id)"));
        rows.Remove("a");

        clone.Rows.Select(row => row.Name).Should().Equal("a", "b");
        rows.Count.Should().Be(0);
    }

    // ---------------------------------------------------------------- sqlite_schema reads

    [Test]
    public void SqliteSchemaReadsListEachTableFollowedByItsIndexesThenViewsThenTriggers()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE orders(id INTEGER PRIMARY KEY, total INTEGER);");
        Execute(connection, "CREATE INDEX orders_total ON orders(total);");
        Execute(connection, "CREATE VIEW orders_view AS SELECT total FROM orders;");
        Execute(connection, "CREATE TRIGGER orders_audit AFTER INSERT ON orders BEGIN SELECT 1; END;");

        ReadSchemaRows(connection).Should().Equal(
            "table|orders|orders",
            "index|orders_total|orders",
            "view|orders_view|orders_view",
            "trigger|orders_audit|orders");
    }

    [Test]
    public void SqliteSchemaReadsAndTheFileStoreAgreeOnRowShapeAcrossAReopen()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "schema-row-shape.db";

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE orders(id INTEGER PRIMARY KEY, total INTEGER, code TEXT UNIQUE);");
            Execute(connection, "CREATE INDEX orders_total ON orders(total);");
            Execute(connection, "CREATE VIEW orders_view AS SELECT total FROM orders;");
            Execute(connection, "CREATE TRIGGER orders_audit AFTER INSERT ON orders BEGIN SELECT 1; END;");
        }

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopenedConnection = reopened.Connect();

        ReadSchemaRows(reopenedConnection).Should().Equal(
            "table|orders|orders",
            "index|sqlite_autoindex_orders_1|orders",
            "index|orders_total|orders",
            "view|orders_view|orders_view",
            "trigger|orders_audit|orders");
    }

    // ---------------------------------------------------------------- stage construction

    [Test]
    public void TheStageProjectsTheCatalogIntoSchemaOrderedRows()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE orders(id INTEGER PRIMARY KEY, total INTEGER);");
        Execute(connection, "CREATE INDEX orders_total ON orders(total);");
        Execute(connection, "CREATE VIEW orders_view AS SELECT total FROM orders;");
        Execute(connection, "CREATE TRIGGER orders_audit AFTER INSERT ON orders BEGIN SELECT 1; END;");

        var stage = CreateStage(database);

        stage.Rows.Rows.Select(row => $"{row.Type}|{row.Name}|{row.TableName}").Should().Equal(
            "table|orders|orders",
            "index|orders_total|orders",
            "view|orders_view|orders_view",
            "trigger|orders_audit|orders");
        stage.Rows.Rows.Select(row => row.RowId).Should().Equal(1L, 2L, 3L, 4L);
        stage.HasStagedChanges.Should().BeFalse();
    }

    [Test]
    public void AnInMemoryStageGivesEveryBtreeObjectAStableLogicalRoot()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE orders(id INTEGER PRIMARY KEY, total INTEGER);");
        Execute(connection, "CREATE INDEX orders_total ON orders(total);");

        var stage = CreateStage(database);

        var roots = stage.Rows.Rows
            .Where(row => row.Type is "table" or "index")
            .Select(row => row.RootPage)
            .ToArray();
        roots.Should().OnlyHaveUniqueItems();
        roots.Should().AllSatisfy(root => stage.RootPlan.IsLogicalRoot(root).Should().BeTrue());
        stage.RootPlan.Reservations.Should().BeEmpty("baseline roots are not this program's reservations");
    }

    // ---------------------------------------------------------------- cookies

    [Test]
    public void SetCookieStagesTheHeaderMetadataThatReadCookieObserves()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE orders(id INTEGER PRIMARY KEY);");
        var stage = CreateStage(database);
        var operations = new ManagedSchemaOperations(stage);
        var before = database.GetPragmaHeaderMetadata();

        operations.SetCookie(0, VdbeSchemaCookie.SchemaVersion, before.SchemaVersion + 1);
        operations.SetCookie(0, VdbeSchemaCookie.UserVersion, 9);
        operations.SetCookie(0, VdbeSchemaCookie.ApplicationId, 77);

        operations.ReadCookie(0, VdbeSchemaCookie.SchemaVersion).Should().Be(before.SchemaVersion + 1);
        operations.ReadCookie(0, VdbeSchemaCookie.UserVersion).Should().Be(9);
        operations.ReadCookie(0, VdbeSchemaCookie.ApplicationId).Should().Be(77);
        stage.PragmaHeader.Should().Be(new PragmaHeaderMetadata(before.SchemaVersion + 1, 9, 77));
        stage.HasStagedChanges.Should().BeTrue();
        database.GetPragmaHeaderMetadata().Should().Be(before, "staging must not touch the live header");
    }

    [Test]
    public void ResettingTheStageRestoresTheBaselineCookiesRowsAndRootPlan()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE orders(id INTEGER PRIMARY KEY);");
        var stage = CreateStage(database);
        var operations = new ManagedSchemaOperations(stage);
        var baselineHeader = stage.PragmaHeader;
        var baselineRows = Describe(stage);

        operations.SetCookie(0, VdbeSchemaCookie.UserVersion, 21);
        var reserved = operations.CreateBtree(0, VdbeCreateBtreeFlags.Table);
        stage.Rows.Add(new ManagedSchemaRow("table", "staged", "staged", (uint)reserved, "CREATE TABLE staged(id)"));
        operations.DropObject(0, VdbeSchemaObjectKind.Table, "orders");

        stage.HasStagedChanges.Should().BeTrue();
        stage.Reset();

        stage.PragmaHeader.Should().Be(baselineHeader);
        Describe(stage).Should().Equal(baselineRows);
        stage.Catalog.Tables.Keys.Should().Equal("orders");
        stage.RootPlan.Reservations.Should().BeEmpty();
        stage.RootPlan.IsLogicalRoot((uint)reserved).Should().BeFalse();
        stage.HasStagedChanges.Should().BeFalse();
    }

    [Test]
    public void ResettingTheOwningStatementDiscardsTheStagedCookie()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE orders(id INTEGER PRIMARY KEY);");
        var stage = CreateStage(database);
        var baseline = stage.PragmaHeader;
        var program = new VdbeProgram(
            registerCount: 4,
            cursorCount: 1,
            [
                new SetCookieInstruction(0, VdbeSchemaCookie.UserVersion, 31),
                new HaltInstruction(),
            ]);
        using var statement = ResumableStatement.CreateWithSchemaContext(
            program,
            ManagedSchemaOperations.CreateContext(stage));

        statement.Step().Should().Be(StatementStepResult.Done);
        stage.PragmaHeader.UserVersion.Should().Be(31);

        statement.Reset();

        stage.PragmaHeader.Should().Be(baseline);
    }

    [Test]
    public void DisposingTheOwningStatementDiscardsTheStageInsteadOfRebuildingIt()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE orders(id INTEGER PRIMARY KEY);");
        var stage = CreateStage(database);
        var program = new VdbeProgram(
            registerCount: 2,
            cursorCount: 0,
            [
                new SetCookieInstruction(0, VdbeSchemaCookie.UserVersion, 31),
                new HaltInstruction(),
            ]);
        var statement = ResumableStatement.CreateWithSchemaContext(
            program,
            ManagedSchemaOperations.CreateContext(stage));
        statement.Step().Should().Be(StatementStepResult.Done);
        var working = stage.Catalog;

        statement.Dispose();

        // Rebuilding here would connect a fresh set of virtual-table instances that nothing would ever
        // disconnect, so disposal releases the working catalog rather than replacing it.
        stage.Catalog.Should().BeSameAs(working);
    }

    [Test]
    public void SetCookieAcceptsAnAssertionOfAFixedCookieButRejectsAChange()
    {
        using var database = new EmbeddedDatabase();
        var stage = CreateStage(database);
        var operations = new ManagedSchemaOperations(stage);

        operations.ReadCookie(0, VdbeSchemaCookie.DatabaseFormat).Should().Be(4);
        operations.ReadCookie(0, VdbeSchemaCookie.DatabaseTextEncoding).Should().Be((long)SqliteTextEncoding.Utf8);

        Action assertCurrent = () => operations.SetCookie(0, VdbeSchemaCookie.DatabaseFormat, 4);
        Action change = () => operations.SetCookie(0, VdbeSchemaCookie.DatabaseFormat, 3);

        assertCurrent.Should().NotThrow();
        change.Should().Throw<VdbeSchemaExecutionException>()
            .WithMessage("*cannot change the DatabaseFormat cookie*from 4 to 3*");
        stage.HasStagedChanges.Should().BeFalse();
    }

    [Test]
    public void CookieOperationsRejectAnUnboundDatabaseAndAnOutOfRangeValue()
    {
        using var database = new EmbeddedDatabase();
        var operations = new ManagedSchemaOperations(CreateStage(database));

        Action otherDatabase = () => operations.ReadCookie(1, VdbeSchemaCookie.SchemaVersion);
        Action tooLarge = () => operations.SetCookie(0, VdbeSchemaCookie.UserVersion, long.MaxValue);

        otherDatabase.Should().Throw<VdbeSchemaExecutionException>()
            .WithMessage("*addresses database 1, but this schema binding serves database 0*");
        tooLarge.Should().Throw<VdbeSchemaExecutionException>()
            .WithMessage("*cannot store 9223372036854775807 in the 32-bit UserVersion cookie*");
    }

    // ---------------------------------------------------------------- root plan

    [Test]
    public void CreateBtreeReservesLogicalRootsThatDestroyCanCancel()
    {
        using var database = new EmbeddedDatabase();
        var stage = CreateStage(database);
        var operations = new ManagedSchemaOperations(stage);

        var table = operations.CreateBtree(0, VdbeCreateBtreeFlags.Table);
        var index = operations.CreateBtree(0, VdbeCreateBtreeFlags.Index);

        stage.RootPlan.Reservations.Should().Equal(
            new ManagedSchemaRootReservation((uint)table, ManagedSchemaRootKind.Table),
            new ManagedSchemaRootReservation((uint)index, ManagedSchemaRootKind.Index));

        operations.Destroy(0, index, isTemporary: false).Should().Be(0);

        stage.RootPlan.Reservations.Should().Equal(
            new ManagedSchemaRootReservation((uint)table, ManagedSchemaRootKind.Table));
        stage.RootPlan.DestroyedRoots.Should().BeEmpty("cancelling an uncommitted reservation reclaims nothing");
        stage.RootPlan.IsLogicalRoot((uint)index).Should().BeFalse();
    }

    [Test]
    public void ClearAndDestroyRecordIntentsAgainstPreExistingRoots()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE orders(id INTEGER PRIMARY KEY);");
        var stage = CreateStage(database);
        var operations = new ManagedSchemaOperations(stage);
        var ordersRoot = stage.Rows.Rows.Single(row => row.Name == "orders").RootPage;

        operations.ClearBtree(0, ordersRoot);
        operations.Destroy(0, ordersRoot, isTemporary: false);

        stage.RootPlan.ClearedRoots.Should().Equal(ordersRoot);
        stage.RootPlan.DestroyedRoots.Should().Equal(ordersRoot);
    }

    [TestCase(0L)]
    [TestCase(1L)]
    public void RootOperationsRejectPagesThatCannotHostABtree(long rootPage)
    {
        using var database = new EmbeddedDatabase();
        var operations = new ManagedSchemaOperations(CreateStage(database));

        Action clear = () => operations.ClearBtree(0, rootPage);
        Action destroy = () => operations.Destroy(0, rootPage, isTemporary: false);

        clear.Should().Throw<VdbeSchemaExecutionException>().WithMessage("*not an allocatable b-tree root*");
        destroy.Should().Throw<VdbeSchemaExecutionException>().WithMessage("*not an allocatable b-tree root*");
    }

    [Test]
    public void CreateBtreeRequiresExactlyOneKind()
    {
        using var database = new EmbeddedDatabase();
        var operations = new ManagedSchemaOperations(CreateStage(database));

        Action none = () => operations.CreateBtree(0, VdbeCreateBtreeFlags.None);
        Action both = () => operations.CreateBtree(
            0,
            VdbeCreateBtreeFlags.Table | VdbeCreateBtreeFlags.Index);

        none.Should().Throw<VdbeSchemaExecutionException>().WithMessage("*exactly one of Table or Index*");
        both.Should().Throw<VdbeSchemaExecutionException>().WithMessage("*exactly one of Table or Index*");
    }

    // ---------------------------------------------------------------- ParseSchema

    [Test]
    public void ParseSchemaAdoptsANewlyStagedTableRowIntoTheWorkingCatalog()
    {
        using var database = new EmbeddedDatabase();
        var stage = CreateStage(database);
        var operations = new ManagedSchemaOperations(stage);
        var root = operations.CreateBtree(0, VdbeCreateBtreeFlags.Table);
        stage.Rows.Add(new ManagedSchemaRow(
            "table",
            "orders",
            "orders",
            (uint)root,
            "CREATE TABLE orders(id INTEGER PRIMARY KEY, total INTEGER)"));

        operations.ParseSchema(0, "tbl_name = 'orders' AND type != 'trigger'", null);

        stage.Catalog.Tables.Keys.Should().Equal("orders");
        stage.Catalog.Tables["orders"].Columns.Should().Equal("id", "total");
        database.LiveCatalog.Tables.Should().BeEmpty("adoption lands on the working clone only");
    }

    [Test]
    public void ParseSchemaKeepsTheRowsOfATableItRewrites()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE orders(id INTEGER PRIMARY KEY, total INTEGER);");
        Execute(connection, "INSERT INTO orders VALUES (1, 100), (2, 200);");
        var stage = CreateStage(database);
        var operations = new ManagedSchemaOperations(stage);
        var current = stage.Rows.Rows.Single(row => row.Name == "orders");
        stage.Rows.Replace(current with
        {
            Sql = "CREATE TABLE orders(id INTEGER PRIMARY KEY, amount INTEGER)",
        });

        operations.ParseSchema(0, "type = 'table' AND name = 'orders'", null);

        var table = stage.Catalog.Tables["orders"];
        table.Columns.Should().Equal("id", "amount");
        table.Rows.Count.Should().Be(2);
        table.RowIds.Should().Equal(1L, 2L);
    }

    [Test]
    public void ParseSchemaAdoptsIndexViewAndTriggerRows()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE orders(id INTEGER PRIMARY KEY, total INTEGER);");
        var stage = CreateStage(database);
        var operations = new ManagedSchemaOperations(stage);
        var indexRoot = operations.CreateBtree(0, VdbeCreateBtreeFlags.Index);
        stage.Rows.Add(new ManagedSchemaRow(
            "index",
            "orders_total",
            "orders",
            (uint)indexRoot,
            "CREATE INDEX orders_total ON orders(total)"));
        stage.Rows.Add(new ManagedSchemaRow(
            "view",
            "orders_view",
            "orders_view",
            0,
            "CREATE VIEW orders_view AS SELECT total FROM orders"));
        stage.Rows.Add(new ManagedSchemaRow(
            "trigger",
            "orders_audit",
            "orders",
            0,
            "CREATE TRIGGER orders_audit AFTER INSERT ON orders BEGIN SELECT 1; END"));

        operations.ParseSchema(0, null, null);

        stage.Catalog.Tables["orders"].Indexes.Select(index => index.Name).Should().Contain("orders_total");
        stage.Catalog.Views.Keys.Should().Equal("orders_view");
        stage.Catalog.Triggers.Keys.Should().Equal("orders_audit");
        stage.Catalog.Triggers["orders_audit"].TableName.Should().Be("orders");
    }

    [Test]
    public void ParseSchemaOnlyAdoptsTheRowsItsClauseSelects()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE orders(id INTEGER PRIMARY KEY, total INTEGER);");
        var stage = CreateStage(database);
        var operations = new ManagedSchemaOperations(stage);
        stage.Rows.Add(new ManagedSchemaRow(
            "view",
            "orders_view",
            "orders_view",
            0,
            "CREATE VIEW orders_view AS SELECT total FROM orders"));
        stage.Rows.Add(new ManagedSchemaRow(
            "trigger",
            "orders_audit",
            "orders",
            0,
            "CREATE TRIGGER orders_audit AFTER INSERT ON orders BEGIN SELECT 1; END"));

        operations.ParseSchema(0, "type = 'trigger' AND tbl_name = 'orders'", null);

        stage.Catalog.Triggers.Keys.Should().Equal("orders_audit");
        stage.Catalog.Views.Should().BeEmpty("the clause excluded the view row");
    }

    [Test]
    public void ParseSchemaLeavesTheCatalogUntouchedWhenAnyMatchedRowFails()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE orders(id INTEGER PRIMARY KEY, total INTEGER);");
        var stage = CreateStage(database);
        var operations = new ManagedSchemaOperations(stage);
        var before = Describe(stage.Catalog);
        stage.Rows.Add(new ManagedSchemaRow(
            "view",
            "orders_view",
            "orders_view",
            0,
            "CREATE VIEW orders_view AS SELECT total FROM orders"));
        stage.Rows.Add(new ManagedSchemaRow(
            "trigger",
            "broken",
            "missing_table",
            0,
            "CREATE TRIGGER broken AFTER INSERT ON missing_table BEGIN SELECT 1; END"));

        Action parse = () => operations.ParseSchema(0, null, null);

        parse.Should().Throw<EmbeddedSqlException>().WithMessage("*references missing target*");
        Describe(stage.Catalog).Should().Equal(before);
    }

    [Test]
    public void ParseSchemaLeavesAnAdoptedIndexOutOfTheCatalogWhenALaterRowFails()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE orders(id INTEGER PRIMARY KEY, total INTEGER);");
        var stage = CreateStage(database);
        var operations = new ManagedSchemaOperations(stage);
        var indexRoot = operations.CreateBtree(0, VdbeCreateBtreeFlags.Index);
        stage.Rows.Add(new ManagedSchemaRow(
            "index",
            "orders_total",
            "orders",
            (uint)indexRoot,
            "CREATE INDEX orders_total ON orders(total)"));
        stage.Rows.Add(new ManagedSchemaRow(
            "trigger",
            "broken",
            "missing_table",
            0,
            "CREATE TRIGGER broken AFTER INSERT ON missing_table BEGIN SELECT 1; END"));

        Action parse = () => operations.ParseSchema(0, null, null);

        parse.Should().Throw<EmbeddedSqlException>();
        stage.Catalog.Tables["orders"].Indexes
            .Select(index => index.Name)
            .Should().NotContain("orders_total", "index adoption is part of the same all-or-nothing batch");
    }

    [Test]
    public void SqliteSchemaReadsPreferTheStagedRowSetOverTheCatalogProjection()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE orders(id INTEGER PRIMARY KEY);");
        var stage = CreateStage(database);
        stage.Rows.Add(new ManagedSchemaRow(
            "view",
            "orders_view",
            "orders_view",
            0,
            "CREATE VIEW orders_view AS SELECT id FROM orders"));
        var context = new EmbeddedDatabase.QueryContext(
            database.LiveCatalog.Tables,
            new Dictionary<string, SourceData>());

        EmbeddedDatabase.EnumerateSchemaRows(context)
            .Select(row => $"{row.Type}|{row.Name}")
            .Should().Equal("table|orders");
        EmbeddedDatabase.EnumerateSchemaRows(context with { StagedSchemaRows = stage.Rows })
            .Select(row => $"{row.Type}|{row.Name}")
            .Should().Equal("table|orders", "view|orders_view");
    }

    [Test]
    public void ParseSchemaRejectsAnIndexRowWhoseTableIsNotInTheSchema()
    {
        using var database = new EmbeddedDatabase();
        var stage = CreateStage(database);
        var operations = new ManagedSchemaOperations(stage);
        stage.Rows.Add(new ManagedSchemaRow(
            "index",
            "orphan",
            "missing",
            9,
            "CREATE INDEX orphan ON missing(id)"));

        Action parse = () => operations.ParseSchema(0, "type = 'index'", null);

        parse.Should().Throw<VdbeSchemaExecutionException>()
            .WithMessage("*index 'orphan' because its table 'missing' is not in the schema*");
    }

    [Test]
    public void ParseSchemaRejectsAnImplicitIndexRowTheParsedTableDoesNotDeclare()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE orders(id INTEGER PRIMARY KEY, total INTEGER);");
        var stage = CreateStage(database);
        var operations = new ManagedSchemaOperations(stage);
        stage.Rows.Add(new ManagedSchemaRow("index", "sqlite_autoindex_orders_1", "orders", 9, null));

        Action parse = () => operations.ParseSchema(0, "type = 'index'", null);

        parse.Should().Throw<VdbeSchemaExecutionException>()
            .WithMessage("*declares no matching UNIQUE or PRIMARY KEY constraint*");
    }

    [Test]
    public void ParseSchemaWithAClauseThatMatchesNothingAdoptsNothing()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE orders(id INTEGER PRIMARY KEY);");
        var stage = CreateStage(database);
        var operations = new ManagedSchemaOperations(stage);
        var before = Describe(stage.Catalog);

        operations.ParseSchema(0, "name = 'nothing_here'", null);

        Describe(stage.Catalog).Should().Equal(before);
    }

    [Test]
    public void ParseSchemaRejectsAClauseOutsideTheGrammarDdlProgramsEmit()
    {
        using var database = new EmbeddedDatabase();
        var operations = new ManagedSchemaOperations(CreateStage(database));

        Action unknownColumn = () => operations.ParseSchema(0, "rootpage = '3'", null);
        Action unsupportedOperator = () => operations.ParseSchema(0, "name LIKE 'a%'", null);
        Action unquoted = () => operations.ParseSchema(0, "name = orders", null);
        Action disjunction = () => operations.ParseSchema(0, "name = 'a' OR name = 'b'", null);

        unknownColumn.Should().Throw<VdbeSchemaExecutionException>()
            .WithMessage("*'rootpage' is not a filterable sqlite_schema column*");
        unsupportedOperator.Should().Throw<VdbeSchemaExecutionException>()
            .WithMessage("*no supported comparison operator*");
        unquoted.Should().Throw<VdbeSchemaExecutionException>()
            .WithMessage("*is not a single-quoted string literal*");
        disjunction.Should().Throw<VdbeSchemaExecutionException>()
            .WithMessage("*the accepted grammar joins terms with AND only*");
    }

    [Test]
    public void ParseSchemaRejectsATriggerTargetDatabaseTheBindingDoesNotServe()
    {
        using var database = new EmbeddedDatabase();
        var operations = new ManagedSchemaOperations(CreateStage(database));

        Action parse = () => operations.ParseSchema(0, null, 1);

        parse.Should().Throw<VdbeSchemaExecutionException>()
            .WithMessage("*trigger target database 1, but the schema context is bound to 0*");
    }

    [Test]
    public void ParseSchemaRunsFromASchemaOpcodeThroughTheInterpreter()
    {
        using var database = new EmbeddedDatabase();
        var stage = CreateStage(database);
        var context = ManagedSchemaOperations.CreateContext(stage);

        // The Yield splits the program where a real CREATE TABLE program writes its sqlite_schema row with
        // ordinary cursor bytecode, so the test can stand in for that write and still prove ParseSchema
        // observes it from inside the interpreter rather than from a direct call.
        var program = new VdbeProgram(
            registerCount: 4,
            cursorCount: 0,
            [
                new CreateBtreeInstruction(0, new Register(1), VdbeCreateBtreeFlags.Table),
                new SetCookieInstruction(0, VdbeSchemaCookie.SchemaVersion, 1),
                new YieldInstruction(),
                new ParseSchemaInstruction(0, "type = 'table' AND name = 'orders'"),
                new HaltInstruction(),
            ]);
        using var statement = ResumableStatement.CreateWithSchemaContext(program, context);

        statement.StepResumable().Should().Be(ResumableStatementStepResult.Yielded);
        var reserved = statement.GetRegister(new Register(1)).AsInteger();
        stage.Rows.Add(new ManagedSchemaRow(
            "table",
            "orders",
            "orders",
            (uint)reserved,
            "CREATE TABLE orders(id INTEGER PRIMARY KEY)"));
        statement.Resume();

        statement.StepResumable().Should().Be(ResumableStatementStepResult.Done);

        stage.Catalog.Tables.Keys.Should().Equal("orders");
        stage.PragmaHeader.SchemaVersion.Should().Be(1);
        context.ReservedRootPages.Should().Equal(reserved);
    }

    // ---------------------------------------------------------------- drop

    [Test]
    public void DropOpcodesEvictOnlyTheirOwnObjectFromTheWorkingCatalog()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE orders(id INTEGER PRIMARY KEY, total INTEGER);");
        Execute(connection, "CREATE INDEX orders_total ON orders(total);");
        Execute(connection, "CREATE VIEW orders_view AS SELECT total FROM orders;");
        Execute(connection, "CREATE TRIGGER orders_audit AFTER INSERT ON orders BEGIN SELECT 1; END;");
        var stage = CreateStage(database);
        var operations = new ManagedSchemaOperations(stage);

        operations.DropObject(0, VdbeSchemaObjectKind.Index, "orders_total");
        operations.DropObject(0, VdbeSchemaObjectKind.Trigger, "orders_audit");
        operations.DropObject(0, VdbeSchemaObjectKind.View, "orders_view");

        stage.Catalog.Tables["orders"].Indexes.Select(index => index.Name).Should().NotContain("orders_total");
        stage.Catalog.Triggers.Should().BeEmpty();
        stage.Catalog.Views.Should().BeEmpty();
        stage.Catalog.Tables.Keys.Should().Equal("orders");

        operations.DropObject(0, VdbeSchemaObjectKind.Table, "orders");
        stage.Catalog.Tables.Should().BeEmpty();

        database.LiveCatalog.Tables.Keys.Should().Equal("orders");
        database.LiveCatalog.Views.Keys.Should().Equal("orders_view");
        database.LiveCatalog.Triggers.Keys.Should().Equal("orders_audit");
    }

    [Test]
    public void DropOpcodesDoNotDeleteSchemaRows()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE orders(id INTEGER PRIMARY KEY);");
        var stage = CreateStage(database);
        var operations = new ManagedSchemaOperations(stage);

        operations.DropObject(0, VdbeSchemaObjectKind.Table, "orders");

        stage.Rows.Contains("orders").Should()
            .BeTrue("row deletion is ordinary cursor bytecode, not a Drop opcode effect");
    }

    [Test]
    public void DropOpcodesFailWhenTheObjectIsNotInTheWorkingCatalog()
    {
        using var database = new EmbeddedDatabase();
        var operations = new ManagedSchemaOperations(CreateStage(database));

        Action dropTable = () => operations.DropObject(0, VdbeSchemaObjectKind.Table, "missing");
        Action dropIndex = () => operations.DropObject(0, VdbeSchemaObjectKind.Index, "missing");
        Action dropView = () => operations.DropObject(0, VdbeSchemaObjectKind.View, "missing");
        Action dropTrigger = () => operations.DropObject(0, VdbeSchemaObjectKind.Trigger, "missing");

        dropTable.Should().Throw<VdbeSchemaExecutionException>().WithMessage("DropTable cannot evict 'missing'*");
        dropIndex.Should().Throw<VdbeSchemaExecutionException>().WithMessage("DropIndex cannot evict 'missing'*");
        dropView.Should().Throw<VdbeSchemaExecutionException>().WithMessage("DropView cannot evict 'missing'*");
        dropTrigger.Should().Throw<VdbeSchemaExecutionException>().WithMessage("DropTrigger cannot evict 'missing'*");
    }

    // ---------------------------------------------------------------- alter

    [Test]
    public void RenameTableRenamesTheTableItsConstraintIndexesAndItsTriggerTargets()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE orders(id INTEGER PRIMARY KEY, code TEXT UNIQUE);");
        Execute(connection, "CREATE TRIGGER orders_audit AFTER INSERT ON orders BEGIN SELECT 1; END;");
        var stage = CreateStage(database);
        var operations = new ManagedSchemaOperations(stage);

        operations.RenameTable(0, "orders", "invoices");

        stage.Catalog.Tables.Keys.Should().Equal("invoices");
        stage.Catalog.Tables["invoices"].Name.Should().Be("invoices");
        stage.Catalog.Tables["invoices"].Indexes
            .Select(index => index.Name)
            .Should().Contain("sqlite_autoindex_invoices_1");
        stage.Catalog.Triggers["orders_audit"].TableName.Should().Be("invoices");
        database.LiveCatalog.Tables.Keys.Should().Equal("orders");
    }

    [Test]
    public void RenameTableRejectsAnUnknownSourceAndAnOccupiedTarget()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE orders(id INTEGER PRIMARY KEY);");
        Execute(connection, "CREATE TABLE invoices(id INTEGER PRIMARY KEY);");
        var operations = new ManagedSchemaOperations(CreateStage(database));

        Action unknown = () => operations.RenameTable(0, "missing", "whatever");
        Action occupied = () => operations.RenameTable(0, "orders", "invoices");

        unknown.Should().Throw<VdbeSchemaExecutionException>().WithMessage("*has no such table*");
        occupied.Should().Throw<VdbeSchemaExecutionException>()
            .WithMessage("there is already an object named invoices");
    }

    [Test]
    public void AddColumnAppendsTheParsedColumnAndBackfillsExistingRows()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE orders(id INTEGER PRIMARY KEY);");
        Execute(connection, "INSERT INTO orders VALUES (1);");
        var stage = CreateStage(database);
        var operations = new ManagedSchemaOperations(stage);

        operations.AddColumn(0, "orders", "note", "note TEXT DEFAULT 'n/a'", "note TEXT DEFAULT 'n/a'");

        var table = stage.Catalog.Tables["orders"];
        table.Columns.Should().Equal("id", "note");
        table.Rows[0][1].Should().Be(SqlValue.Text("n/a"));
        database.LiveCatalog.Tables["orders"].Columns.Should().Equal("id");
    }

    [Test]
    public void AddColumnRejectsAMismatchedNameAndAnUnparsableDefinition()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE orders(id INTEGER PRIMARY KEY);");
        var operations = new ManagedSchemaOperations(CreateStage(database));

        Action mismatched = () => operations.AddColumn(0, "orders", "note", "other TEXT", "other TEXT");
        Action unparsable = () => operations.AddColumn(0, "orders", "note", "note TEXT DEFAULT (", null);
        Action unknownTable = () => operations.AddColumn(0, "missing", "note", "note TEXT", "note TEXT");

        mismatched.Should().Throw<VdbeSchemaExecutionException>()
            .WithMessage("AddColumn names column 'note' but its definition declares 'other'.");
        unparsable.Should().Throw<VdbeSchemaExecutionException>()
            .WithMessage("AddColumn cannot parse the column definition*");
        unknownTable.Should().Throw<VdbeSchemaExecutionException>()
            .WithMessage("AddColumn cannot alter 'missing'*");
    }

    [Test]
    public void DropColumnRemovesTheColumnAtTheGivenOrdinal()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE orders(id INTEGER PRIMARY KEY, note TEXT, total INTEGER);");
        Execute(connection, "INSERT INTO orders VALUES (1, 'x', 5);");
        var stage = CreateStage(database);
        var operations = new ManagedSchemaOperations(stage);

        operations.DropColumn(0, "orders", 1);

        stage.Catalog.Tables["orders"].Columns.Should().Equal("id", "total");
        stage.Catalog.Tables["orders"].Rows[0].Should().Equal(SqlValue.Integer(1), SqlValue.Integer(5));
        database.LiveCatalog.Tables["orders"].Columns.Should().Equal("id", "note", "total");
    }

    [Test]
    public void AlterColumnRewritesTheColumnAtTheGivenOrdinal()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE orders(id INTEGER PRIMARY KEY, note TEXT);");
        var stage = CreateStage(database);
        var operations = new ManagedSchemaOperations(stage);

        operations.AlterColumn(0, "orders", 1, "note TEXT NOT NULL DEFAULT ''", rename: false, quoteNewName: false);

        stage.Catalog.Tables["orders"].ColumnDefinitions[1].NotNull.Should().BeTrue();
        database.LiveCatalog.Tables["orders"].ColumnDefinitions[1].NotNull.Should().BeFalse();
    }

    [Test]
    public void AlterColumnRenamesThroughAFullReplacementAndThroughARenameOnly()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE orders(id INTEGER PRIMARY KEY, note TEXT);");
        Execute(connection, "CREATE TABLE parts(id INTEGER PRIMARY KEY, label TEXT);");
        var stage = CreateStage(database);
        var operations = new ManagedSchemaOperations(stage);

        // A full replacement carries the whole declaration, so renaming the column is part of replacing it.
        operations.AlterColumn(0, "orders", 1, "memo TEXT", rename: false, quoteNewName: false);
        // A rename carries nothing but the new name, which is what upstream's RenameColumn arm builds.
        operations.AlterColumn(0, "parts", 1, "caption", rename: true, quoteNewName: false);

        stage.Catalog.Tables["orders"].Columns.Should().Equal("id", "memo");
        stage.Catalog.Tables["parts"].Columns.Should().Equal("id", "caption");
        database.LiveCatalog.Tables["orders"].Columns.Should().Equal("id", "note");
        database.LiveCatalog.Tables["parts"].Columns.Should().Equal("id", "label");
    }

    [Test]
    public void ColumnOpcodesRejectAnOrdinalTheTableDoesNotHave()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE orders(id INTEGER PRIMARY KEY);");
        var operations = new ManagedSchemaOperations(CreateStage(database));

        Action drop = () => operations.DropColumn(0, "orders", 3);
        Action alter = () => operations.AlterColumn(0, "orders", -1, "id INTEGER", rename: false, quoteNewName: false);

        drop.Should().Throw<VdbeSchemaExecutionException>()
            .WithMessage("DropColumn addresses column 3 of 'orders', which has 1 column(s).");
        alter.Should().Throw<VdbeSchemaExecutionException>()
            .WithMessage("AlterColumn addresses column -1 of 'orders', which has 1 column(s).");
    }

    // ---------------------------------------------------------------- publication boundary

    [Test]
    public void StagingAWholeDdlProgramLeavesTheFileAndItsLiveCatalogUntouched()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "staged-ddl-is-not-durable.db";

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        {
            using (var connection = database.Connect())
                Execute(connection, "CREATE TABLE stable(id INTEGER PRIMARY KEY);");

            var stage = CreateStage(database);
            var operations = new ManagedSchemaOperations(stage);
            var root = operations.CreateBtree(0, VdbeCreateBtreeFlags.Table);
            stage.Rows.Add(new ManagedSchemaRow(
                "table",
                "staged",
                "staged",
                (uint)root,
                "CREATE TABLE staged(id INTEGER PRIMARY KEY)"));
            operations.ParseSchema(0, "type = 'table' AND name = 'staged'", null);
            operations.SetCookie(0, VdbeSchemaCookie.SchemaVersion, 99);
            operations.SetCookie(0, VdbeSchemaCookie.UserVersion, 99);
            operations.DropObject(0, VdbeSchemaObjectKind.Table, "stable");

            stage.HasStagedChanges.Should().BeTrue();
            database.LiveCatalog.Tables.Keys.Should().Equal("stable");
            database.GetPragmaHeaderMetadata().UserVersion.Should().Be(0);

            using var stillLive = database.Connect();
            ReadSchemaRows(stillLive).Should().Equal("table|stable|stable");

            stage.Discard();
        }

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopenedConnection = reopened.Connect();
        ReadSchemaRows(reopenedConnection).Should().Equal("table|stable|stable");
        ReadScalar(reopenedConnection, "PRAGMA user_version;").Should().Be(0);
    }

    [Test]
    public void PublicationValidationRejectsRowsThatStillCarryALogicalRoot()
    {
        using var database = new EmbeddedDatabase();
        var stage = CreateStage(database);
        var operations = new ManagedSchemaOperations(stage);
        var root = operations.CreateBtree(0, VdbeCreateBtreeFlags.Table);
        stage.Rows.Add(new ManagedSchemaRow(
            "table",
            "staged",
            "staged",
            (uint)root,
            "CREATE TABLE staged(id INTEGER PRIMARY KEY)"));
        operations.ParseSchema(0, "type = 'table' AND name = 'staged'", null);

        Action publish = () => stage.ValidatePublishable();

        publish.Should().Throw<ManagedSchemaRowException>()
            .WithMessage($"*'staged' still carries logical root {root}*");
    }

    [Test]
    public void PublicationValidationRejectsRowsAndCatalogThatDisagree()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE orders(id INTEGER PRIMARY KEY);");
        var stage = CreateStage(database, firstLogicalRoot: 2);
        var operations = new ManagedSchemaOperations(stage);

        // A Drop opcode evicts from the catalog; the matching row delete is separate bytecode. Skipping it
        // is exactly the divergence publication must refuse.
        operations.DropObject(0, VdbeSchemaObjectKind.Table, "orders");
        RewriteRootsAsPhysical(stage);

        Action publish = () => stage.ValidatePublishable();

        publish.Should().Throw<ManagedSchemaRowException>()
            .WithMessage("*do not describe the staged catalog*Unexpected rows: table/orders/orders*");
    }

    [Test]
    public void PublicationValidationAcceptsAConsistentStage()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE orders(id INTEGER PRIMARY KEY, total INTEGER);");
        Execute(connection, "CREATE INDEX orders_total ON orders(total);");
        Execute(connection, "CREATE VIEW orders_view AS SELECT total FROM orders;");
        var stage = CreateStage(database);
        var operations = new ManagedSchemaOperations(stage);

        operations.DropObject(0, VdbeSchemaObjectKind.View, "orders_view");
        stage.Rows.Remove("orders_view").Should().BeTrue();
        RewriteRootsAsPhysical(stage);

        Action publish = () => stage.ValidatePublishable();

        publish.Should().NotThrow();
    }

    [Test]
    public void RenamingATableOnBothSidesStaysPublishable()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE orders(id INTEGER PRIMARY KEY, code TEXT UNIQUE);");
        Execute(connection, "CREATE TRIGGER orders_audit AFTER INSERT ON orders BEGIN SELECT 1; END;");
        var stage = CreateStage(database);
        var operations = new ManagedSchemaOperations(stage);

        // A rename has to move both sides: the catalog through the opcode, the rows through the row set.
        // Publication validation is what proves the two halves describe the same schema.
        operations.RenameTable(0, "orders", "invoices");
        stage.Rows.Rename("orders", "invoices");
        stage.Rows.Replace(stage.Rows.Rows.Single(row => row.Name == "invoices") with
        {
            Sql = "CREATE TABLE invoices(id INTEGER PRIMARY KEY, code TEXT UNIQUE)",
        });
        stage.Rows.Rename("sqlite_autoindex_orders_1", "sqlite_autoindex_invoices_1");
        RewriteRootsAsPhysical(stage);

        stage.ValidatePublishable();
        stage.Rows.Rows.Select(row => $"{row.Type}|{row.Name}|{row.TableName}").Should().Equal(
            "table|invoices|invoices",
            "index|sqlite_autoindex_invoices_1|invoices",
            "trigger|orders_audit|invoices");
    }

    [Test]
    public void MappingALogicalRootRetiresItEvenWhenThePhysicalRootReusesTheSameNumber()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE orders(id INTEGER PRIMARY KEY);");
        var stage = CreateStage(database, firstLogicalRoot: 2);
        var logicalRoot = stage.Rows.Rows.Single(row => row.Name == "orders").RootPage;

        // The managed commit assigns real pages from 2 upward, so a physical root routinely collides
        // numerically with an identifier this plan handed out. Retiring on map is what keeps the answer to
        // "is this still logical" exact instead of a range guess.
        stage.MapLogicalRoots(_ => logicalRoot);

        stage.RootPlan.IsLogicalRoot(logicalRoot).Should().BeFalse();
        stage.RootPlan.PublishedRoots.Should().Equal(
            new Dictionary<uint, uint> { [logicalRoot] = logicalRoot });
        stage.Rows.Rows.Single(row => row.Name == "orders").RootPage.Should().Be(logicalRoot);
        stage.ValidatePublishable();
    }

    [Test]
    public void MappingRejectsARootThatIsNotOutstandingAndATargetThatIsStillLogical()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE orders(id INTEGER PRIMARY KEY);");
        Execute(connection, "CREATE TABLE invoices(id INTEGER PRIMARY KEY);");
        var stage = CreateStage(database);
        var invoicesRoot = stage.Rows.Rows.Single(row => row.Name == "invoices").RootPage;

        Action unknown = () => stage.RootPlan.MapToPhysicalRoot(9_000, 2);
        Action ontoOutstanding = () => stage.MapLogicalRoots(_ => invoicesRoot);

        unknown.Should().Throw<VdbeSchemaExecutionException>()
            .WithMessage("Root 9000 is not an outstanding logical root*");
        ontoOutstanding.Should().Throw<VdbeSchemaExecutionException>()
            .WithMessage($"*cannot map logical root*onto {invoicesRoot}, which is still an outstanding logical root*");
    }

    [Test]
    public void ResettingTheStageRestoresTheBaselineLogicalRootsThatPublicationRetired()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE orders(id INTEGER PRIMARY KEY);");
        var stage = CreateStage(database);
        var logicalRoot = stage.Rows.Rows.Single(row => row.Name == "orders").RootPage;

        RewriteRootsAsPhysical(stage);
        stage.RootPlan.IsLogicalRoot(logicalRoot).Should().BeFalse();

        stage.Reset();

        stage.RootPlan.IsLogicalRoot(logicalRoot).Should().BeTrue();
        stage.RootPlan.PublishedRoots.Should().BeEmpty();
        stage.Rows.Rows.Single(row => row.Name == "orders").RootPage.Should().Be(logicalRoot);
    }

    // ---------------------------------------------------------------- helpers

    private static ManagedSchemaStage CreateStage(EmbeddedDatabase database, uint firstLogicalRoot = 2)
        => ManagedSchemaStage.Create(
            "main",
            database.SnapshotCatalog,
            database.GetPragmaHeaderMetadata(),
            ManagedSchemaFixedCookies.Default,
            firstLogicalRoot: firstLogicalRoot);

    /// <summary>
    /// Stands in for the publication step the outer full-rewrite commit performs, so publication validation
    /// can be exercised without a real persist.
    /// </summary>
    private static void RewriteRootsAsPhysical(ManagedSchemaStage stage)
    {
        uint next = 2;
        stage.MapLogicalRoots(_ => next++);
    }

    private static string[] Describe(ManagedSchemaStage stage)
        => [.. stage.Rows.Rows.Select(row => $"{row.RowId}|{row.Type}|{row.Name}|{row.TableName}|{row.Sql}")];

    private static string[] Describe(EmbeddedDatabase.SchemaCatalog catalog)
        =>
        [
            .. catalog.Tables.Select(entry => $"table|{entry.Key}|{string.Join(',', entry.Value.Columns)}").Order(StringComparer.Ordinal),
            .. catalog.Views.Keys.Select(name => $"view|{name}").Order(StringComparer.Ordinal),
            .. catalog.Triggers.Keys.Select(name => $"trigger|{name}").Order(StringComparer.Ordinal),
        ];

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Done);
    }

    private static long ReadScalar(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Row);
        return statement.GetValue(0).AsInteger();
    }

    private static string[] ReadSchemaRows(EmbeddedConnection connection)
    {
        using var statement = connection.Prepare("SELECT type, name, tbl_name FROM sqlite_schema;");
        var rows = new List<string>();
        while (statement.Step() == StatementStepResult.Row)
        {
            rows.Add(string.Join(
                '|',
                statement.GetValue(0).AsText(),
                statement.GetValue(1).AsText(),
                statement.GetValue(2).AsText()));
        }

        return [.. rows];
    }
}
