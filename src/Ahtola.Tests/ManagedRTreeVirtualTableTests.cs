using System.Buffers.Binary;
using AwesomeAssertions;
using Ahtola.Core;
using MsData = Microsoft.Data.Sqlite;

namespace Ahtola.Tests;

[NonParallelizable]
public sealed class ManagedRTreeVirtualTableTests
{
    [Test]
    public void DeclarationAcceptanceMatchesMicrosoftDataSqlite()
    {
        var maximumColumns = "id,min,max," + string.Join(',', Enumerable.Range(0, 97).Select(index => $"+a{index}"));
        var cases = new[]
        {
            "CREATE VIRTUAL TABLE r USING rtree(id,min,max);",
            "CREATE VIRTUAL TABLE r USING rtree(id,a0,a1,b0,b1,c0,c1,d0,d1,e0,e1);",
            "CREATE VIRTUAL TABLE r USING rtree(id,a0,a1,b0,b1,c0,c1,d0,d1,e0,e1,f0,f1);",
            "CREATE VIRTUAL TABLE r USING rtree(id,min,max,+aux);",
            "CREATE VIRTUAL TABLE r USING rtree(id,min,max,+aux,min2,max2);",
            $"CREATE VIRTUAL TABLE r USING rtree({maximumColumns});",
            $"CREATE VIRTUAL TABLE r USING rtree({maximumColumns},+overflow);",
        };

        foreach (var sql in cases)
        {
            using var database = new EmbeddedDatabase();
            using var managed = database.Connect();
            using var sqlite = new MsData.SqliteConnection("Data Source=:memory:;Pooling=False");
            sqlite.Open();

            var managedSucceeded = TryExecute(managed, sql);
            var sqliteSucceeded = TryExecute(sqlite, sql);
            managedSucceeded.Should().Be(sqliteSucceeded, because: sql);
        }
    }

    [Test]
    public void ManagedRTreeObservableValuesMatchMicrosoftDataSqlite()
    {
        using var database = new EmbeddedDatabase();
        using var managed = database.Connect();
        using var sqlite = new MsData.SqliteConnection("Data Source=:memory:;Pooling=False");
        sqlite.Open();

        const string create = """
            CREATE VIRTUAL TABLE boxes USING rtree(
                "box id" INTEGER,
                [min x] FLOAT,
                `max x` ignored,
                +payload ANY
            );
            """;
        Execute(managed, create);
        Execute(sqlite, create);
        var statements = new[]
        {
            "INSERT INTO boxes VALUES(1,NULL,'abc',x'CAFE');",
            "INSERT INTO boxes VALUES(2,0.1,0.1,'fraction');",
            "INSERT INTO boxes VALUES(3,-1e999,1e999,NULL);",
            "INSERT OR IGNORE INTO boxes VALUES(2,9,10,'ignored');",
            "INSERT OR REPLACE INTO boxes VALUES(1,2,3,42);",
        };
        foreach (var statement in statements)
        {
            Execute(managed, statement);
            Execute(sqlite, statement);
        }

        foreach (var query in new[]
                 {
                     "PRAGMA table_info(boxes);",
                     "PRAGMA table_xinfo(boxes);",
                     "PRAGMA table_list('boxes');",
                     "SELECT rowid,* FROM boxes ORDER BY rowid;",
                     "SELECT \"box id\" FROM boxes WHERE \"max x\">=2.5 AND \"min x\"<=2.5 ORDER BY 1;",
                     "SELECT \"box id\" FROM boxes WHERE \"min x\"<'abc' ORDER BY 1;",
                     "SELECT \"box id\" FROM boxes WHERE \"min x\" IS NOT NULL AND rowid!=2 ORDER BY 1;",
                 })
        {
            ReadRows(managed, query).Select(Join)
                .Should().Equal(ReadRows(sqlite, query).Select(Join), because: query);
        }

        const string returning =
            "INSERT INTO boxes(\"box id\",\"min x\",\"max x\",payload) "
            + "VALUES(NULL,'abc','2.5','returning') RETURNING rowid,*;";
        ReadRows(managed, returning).Select(Join)
            .Should().Equal(ReadRows(sqlite, returning).Select(Join));
    }

    [Test]
    public void ConflictDmlAndTransactionResultsMatchMicrosoftDataSqlite()
    {
        using var database = new EmbeddedDatabase();
        using var managed = database.Connect();
        using var sqlite = new MsData.SqliteConnection("Data Source=:memory:;Pooling=False");
        sqlite.Open();

        foreach (var sql in new[]
                 {
                     "CREATE VIRTUAL TABLE boxes USING rtree(id,min,max,+payload);",
                     "CREATE TABLE source(id,min,max,payload);",
                     "INSERT INTO source VALUES(5,5,6,'five');",
                     "INSERT INTO boxes VALUES(1,0,1,'one');",
                     "INSERT OR IGNORE INTO boxes VALUES(1,2,3,'ignored');",
                     "INSERT OR REPLACE INTO boxes VALUES(1,2,3,'replacement');",
                     "INSERT INTO boxes SELECT * FROM source;",
                     "UPDATE boxes SET rowid='ignored' WHERE id=1;",
                 })
        {
            Execute(managed, sql);
            Execute(sqlite, sql);
        }

        const string abort =
            "INSERT OR ABORT INTO boxes VALUES(2,0,1,'two'),(1,0,1,'duplicate');";
        TryExecute(managed, abort).Should().BeFalse();
        TryExecute(sqlite, abort).Should().BeFalse();
        ReadRows(managed, "SELECT rowid,* FROM boxes ORDER BY id;").Select(Join)
            .Should().Equal(ReadRows(sqlite, "SELECT rowid,* FROM boxes ORDER BY id;").Select(Join));

        const string fail =
            "INSERT OR FAIL INTO boxes VALUES(2,0,1,'two'),(1,0,1,'duplicate');";
        TryExecute(managed, fail).Should().BeFalse();
        TryExecute(sqlite, fail).Should().BeFalse();

        foreach (var sql in new[]
                 {
                     "BEGIN;",
                     "INSERT INTO boxes VALUES(9,9,10,'nine');",
                 })
        {
            Execute(managed, sql);
            Execute(sqlite, sql);
        }
        const string rollback = "INSERT OR ROLLBACK INTO boxes VALUES(1,0,1,'duplicate');";
        TryExecute(managed, rollback).Should().BeFalse();
        TryExecute(sqlite, rollback).Should().BeFalse();

        foreach (var query in new[]
                 {
                     "SELECT rowid,* FROM boxes ORDER BY id;",
                     "SELECT rtreecheck('boxes');",
                     "PRAGMA integrity_check(boxes);",
                 })
        {
            ReadRows(managed, query).Select(Join)
                .Should().Equal(ReadRows(sqlite, query).Select(Join), because: query);
        }
    }

    [Test]
    public void DeclarationDimensionsAuxiliaryColumnsAndMetadataMatchSQLite()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        Execute(
            connection,
            """
            CREATE VIRTUAL TABLE boxes USING rtree(
                "box id" INTEGER PRIMARY KEY,
                [min x] FLOAT NOT NULL,
                `max x` ignored,
                +"payload value" TEXT
            );
            """);

        ReadRows(connection, "PRAGMA table_info(boxes);")
            .Select(Join)
            .Should().Equal(
                "0\u001Fbox id\u001FINT\u001F0\u001FNULL\u001F0",
                "1\u001Fmin x\u001FREAL\u001F0\u001FNULL\u001F0",
                "2\u001Fmax x\u001FREAL\u001F0\u001FNULL\u001F0",
                "3\u001Fpayload value\u001F\u001F0\u001FNULL\u001F0");
        ReadRows(connection, "PRAGMA table_xinfo(boxes);").Should().HaveCount(4);
        ReadRows(connection, "PRAGMA table_list('boxes');")
            .Should().ContainSingle()
            .Which.Should().Equal(
                SqlValue.Text("main"),
                SqlValue.Text("boxes"),
                SqlValue.Text("virtual"),
                SqlValue.Integer(4),
                SqlValue.Integer(0),
                SqlValue.Integer(0));

        Execute(
            connection,
            "CREATE VIRTUAL TABLE five_d USING rtree(id,a0,a1,b0,b1,c0,c1,d0,d1,e0,e1);");
        Action sixDimensions = () => Execute(
            connection,
            "CREATE VIRTUAL TABLE six_d USING rtree(id,a0,a1,b0,b1,c0,c1,d0,d1,e0,e1,f0,f1);");
        sixDimensions.Should().Throw<EmbeddedSqlException>().WithMessage("Too many columns for an rtree table");

        Action auxiliaryNotLast = () => Execute(
            connection,
            "CREATE VIRTUAL TABLE bad_aux USING rtree(id,min,max,+payload,min2,max2);");
        auxiliaryNotLast.Should().Throw<EmbeddedSqlException>().WithMessage("Auxiliary rtree columns must be last");

        var maximumColumns = "id,min,max," + string.Join(',', Enumerable.Range(0, 97).Select(index => $"+a{index}"));
        Execute(connection, $"CREATE VIRTUAL TABLE max_columns USING rtree({maximumColumns});");
        Action tooMany = () => Execute(connection, $"CREATE VIRTUAL TABLE too_many USING rtree({maximumColumns},+overflow);");
        tooMany.Should().Throw<EmbeddedSqlException>().WithMessage("Too many columns for an rtree table");

        Action createIndex = () => Execute(connection, "CREATE INDEX boxes_min ON boxes([min x]);");
        createIndex.Should().Throw<EmbeddedSqlException>().WithMessage("virtual tables may not be indexed");
        Action createTrigger = () => Execute(
            connection,
            "CREATE TRIGGER boxes_insert AFTER INSERT ON boxes BEGIN SELECT 1; END;");
        createTrigger.Should().Throw<EmbeddedSqlException>().WithMessage("cannot create triggers on virtual tables");
        Action addColumn = () => Execute(connection, "ALTER TABLE boxes ADD COLUMN other;");
        addColumn.Should().Throw<EmbeddedSqlException>().WithMessage("virtual tables may not be altered");
        Action renameColumn = () => Execute(connection, "ALTER TABLE boxes RENAME COLUMN [min x] TO lower;");
        renameColumn.Should().Throw<EmbeddedSqlException>()
            .WithMessage("cannot rename columns of virtual table \"boxes\"");
        Action dropColumn = () => Execute(connection, "ALTER TABLE boxes DROP COLUMN [min x];");
        dropColumn.Should().Throw<EmbeddedSqlException>()
            .WithMessage("cannot drop column from virtual table \"boxes\"");
    }

    [Test]
    public void RTreeNumericConversionAndOutwardRoundingMatchSQLite()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE VIRTUAL TABLE boxes USING rtree(id,min,max,+payload);");

        Execute(connection, "INSERT INTO boxes VALUES(1,NULL,'abc',x'CAFE');");
        Execute(connection, "INSERT INTO boxes VALUES(2,0.1,0.1,'fraction');");
        Execute(connection, "INSERT INTO boxes VALUES(3,-1e999,1e999,'infinite');");
        Execute(connection, "INSERT INTO boxes VALUES(4,16777217,16777217,'large');");
        Execute(connection, "INSERT INTO boxes VALUES(5,-16777217,-16777217,'negative');");

        var rows = ReadRows(connection, "SELECT id,min,max,payload FROM boxes ORDER BY id;");
        rows[0].Should().Equal(
            SqlValue.Integer(1),
            SqlValue.Real(0),
            SqlValue.Real(0),
            SqlValue.Blob([0xCA, 0xFE]));
        rows[1].Should().Equal(
            SqlValue.Integer(2),
            SqlValue.Real(0.09999998658895493),
            SqlValue.Real(0.10000000149011612),
            SqlValue.Text("fraction"));
        rows[2][1].AsReal().Should().Be(double.NegativeInfinity);
        rows[2][2].AsReal().Should().Be(double.PositiveInfinity);
        rows[3][1].Should().Be(SqlValue.Real(16777216));
        rows[3][2].Should().Be(SqlValue.Real(16777220));
        rows[4][1].Should().Be(SqlValue.Real(-16777220));
        rows[4][2].Should().Be(SqlValue.Real(-16777216));

        Action inverted = () => Execute(connection, "INSERT INTO boxes VALUES(6,10,9,NULL);");
        inverted.Should().Throw<EmbeddedSqlException>()
            .WithMessage("rtree constraint failed: boxes.(min<=max)");
    }

    [Test]
    public void RTreeI32UsesSqliteInt32Conversion()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE VIRTUAL TABLE boxes USING rtree_i32(id,min,max);");

        Execute(connection, "INSERT INTO boxes VALUES(1,'1.9','2.9');");
        Execute(connection, "INSERT INTO boxes VALUES(2,2147483648,2147483648);");
        Execute(connection, "INSERT INTO boxes VALUES(3,'abc',NULL);");

        ReadRows(connection, "SELECT * FROM boxes ORDER BY id;")
            .Select(Join)
            .Should().Equal(
                "1\u001F1\u001F2",
                "2\u001F-2147483648\u001F-2147483648",
                "3\u001F0\u001F0");
    }

    [Test]
    public void DuplicateIdsHonorEveryConflictPolicy()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE VIRTUAL TABLE boxes USING rtree(id,min,max,+payload);");
        Execute(connection, "INSERT INTO boxes VALUES(1,0,1,'original');");

        Execute(connection, "INSERT OR IGNORE INTO boxes VALUES(1,2,3,'ignored');");
        ReadScalar(connection, "SELECT payload FROM boxes WHERE id=1;").Should().Be(SqlValue.Text("original"));

        Execute(connection, "INSERT OR REPLACE INTO boxes VALUES(1,2,3,'replacement');");
        ReadScalar(connection, "SELECT payload FROM boxes WHERE id=1;").Should().Be(SqlValue.Text("replacement"));

        Action abort = () => Execute(
            connection,
            "INSERT OR ABORT INTO boxes VALUES(2,0,1,'two'),(1,0,1,'duplicate'),(3,0,1,'three');");
        abort.Should().Throw<EmbeddedSqlException>().WithMessage("UNIQUE constraint failed: boxes.id");
        ReadRows(connection, "SELECT id FROM boxes ORDER BY id;").Select(Join).Should().Equal("1");

        Action fail = () => Execute(
            connection,
            "INSERT OR FAIL INTO boxes VALUES(2,0,1,'two'),(1,0,1,'duplicate'),(3,0,1,'three');");
        fail.Should().Throw<EmbeddedSqlException>().WithMessage("UNIQUE constraint failed: boxes.id");
        ReadRows(connection, "SELECT id FROM boxes ORDER BY id;").Select(Join).Should().Equal("1", "2");

        Execute(connection, "BEGIN;");
        Execute(connection, "INSERT INTO boxes VALUES(4,0,1,'four');");
        Action rollback = () => Execute(connection, "INSERT OR ROLLBACK INTO boxes VALUES(1,0,1,'duplicate');");
        rollback.Should().Throw<EmbeddedSqlException>().WithMessage("UNIQUE constraint failed: boxes.id");
        ReadRows(connection, "SELECT id FROM boxes ORDER BY id;").Select(Join).Should().Equal("1", "2");
    }

    [Test]
    public void UpdateIdConflictsHonorIgnoreReplaceAndFail()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE VIRTUAL TABLE boxes USING rtree(id,min,max,+payload);");
        Execute(connection, "INSERT INTO boxes VALUES(1,0,1,'one'),(2,2,3,'two'),(3,4,5,'three');");

        Execute(connection, "UPDATE OR IGNORE boxes SET id=2,payload='ignored' WHERE id=1;");
        ReadScalar(connection, "SELECT payload FROM boxes WHERE id=1;").Should().Be(SqlValue.Text("one"));

        Execute(connection, "UPDATE OR REPLACE boxes SET id=2,payload='replaced' WHERE id=1;");
        ReadRows(connection, "SELECT id,payload FROM boxes ORDER BY id;")
            .Select(Join).Should().Equal("2\u001Freplaced", "3\u001Fthree");

        Execute(connection, "INSERT INTO boxes VALUES(1,0,1,'one');");
        Action fail = () => Execute(
            connection,
            "UPDATE OR FAIL boxes SET id=CASE id WHEN 1 THEN 4 ELSE 4 END WHERE id IN (1,2);");
        fail.Should().Throw<EmbeddedSqlException>().WithMessage("UNIQUE constraint failed: boxes.id");
        ReadRows(connection, "SELECT id,payload FROM boxes ORDER BY id;")
            .Select(Join).Should().Equal("2\u001Freplaced", "3\u001Fthree", "4\u001Fone");
    }

    [Test]
    public void InsertSelectDefaultValuesReturningAndAuxiliaryValuesWork()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE source(id,min,max,payload);");
        Execute(connection, "INSERT INTO source VALUES(5,1,2,x'0102');");
        Execute(connection, "CREATE VIRTUAL TABLE boxes USING rtree(id,min,max,+payload);");

        Execute(connection, "INSERT INTO boxes SELECT * FROM source;");
        var returned = ReadRows(
            connection,
            "INSERT INTO boxes(id,min,max,payload) VALUES(NULL,'abc','2.5','text') RETURNING rowid,id,min,max,payload;");
        returned.Should().ContainSingle().Which.Should().Equal(
            SqlValue.Integer(-1),
            SqlValue.Null,
            SqlValue.Text("abc"),
            SqlValue.Real(2.5),
            SqlValue.Text("text"));
        Execute(connection, "INSERT INTO boxes DEFAULT VALUES;");

        ReadRows(connection, "SELECT id,min,max,typeof(payload),payload FROM boxes ORDER BY id;")
            .Select(Join)
            .Should().Equal(
                "5\u001F1\u001F2\u001Fblob\u001F0102",
                "6\u001F0\u001F2.5\u001Ftext\u001Ftext",
                "7\u001F0\u001F0\u001Fnull\u001FNULL");
    }

    [Test]
    public void UpdateDeleteLimitFromAndAuthoritativeIdSemanticsWork()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE VIRTUAL TABLE boxes USING rtree(id,min,max,+payload);");
        Execute(connection, "INSERT INTO boxes VALUES(1,0,1,'a'),(2,2,3,'b'),(3,4,5,'c');");
        Execute(connection, "CREATE TABLE updates(id INTEGER,value TEXT);");
        Execute(connection, "INSERT INTO updates VALUES(1,'one'),(2,'two');");

        Execute(connection, "UPDATE boxes SET payload=updates.value FROM updates WHERE boxes.id=updates.id;");
        Execute(connection, "UPDATE boxes SET rowid='ignored' WHERE id=1;");
        ReadScalar(connection, "SELECT rowid FROM boxes WHERE id=1;").Should().Be(SqlValue.Integer(1));
        Execute(connection, "UPDATE boxes SET id=10 WHERE id=1;");
        ReadScalar(connection, "SELECT rowid FROM boxes WHERE id=10;").Should().Be(SqlValue.Integer(10));

        Execute(connection, "UPDATE boxes SET payload='limited' ORDER BY id DESC LIMIT 1;");
        ReadScalar(connection, "SELECT payload FROM boxes WHERE id=10;").Should().Be(SqlValue.Text("limited"));
        Execute(connection, "DELETE FROM boxes ORDER BY id LIMIT 1;");
        ReadRows(connection, "SELECT id FROM boxes ORDER BY id;").Select(Join).Should().Equal("3", "10");

        Action updateReturning = () => ReadRows(connection, "UPDATE boxes SET payload='x' RETURNING *;");
        updateReturning.Should().Throw<EmbeddedSqlException>()
            .WithMessage("UPDATE RETURNING is not available on virtual tables");
        Action deleteReturning = () => ReadRows(connection, "DELETE FROM boxes RETURNING *;");
        deleteReturning.Should().Throw<EmbeddedSqlException>()
            .WithMessage("DELETE RETURNING is not available on virtual tables");
    }

    [Test]
    public void NullIdAllocationUsesCurrentMaximumAndHandlesLongMaxValue()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE VIRTUAL TABLE boxes USING rtree(id,min,max);");

        Execute(connection, "INSERT INTO boxes VALUES(-5,0,1);");
        Execute(connection, "INSERT INTO boxes(rowid,id,min,max) VALUES(99,-4,0,1);");
        ReadScalar(connection, "SELECT rowid FROM boxes WHERE id=-4;").Should().Be(SqlValue.Integer(-4));
        Execute(connection, "DELETE FROM boxes WHERE id=-4;");
        Execute(connection, "INSERT INTO boxes VALUES(NULL,0,1);");
        ReadScalar(connection, "SELECT max(id) FROM boxes;").Should().Be(SqlValue.Integer(-4));
        Execute(connection, "DELETE FROM boxes WHERE id=-4;");
        Execute(connection, "INSERT INTO boxes VALUES(NULL,0,1);");
        ReadScalar(connection, "SELECT max(id) FROM boxes;").Should().Be(SqlValue.Integer(-4));

        Execute(connection, "INSERT INTO boxes VALUES(9223372036854775807,0,1);");
        Execute(connection, "INSERT INTO boxes VALUES(NULL,0,1);");
        var generated = ReadScalar(
            connection,
            "SELECT id FROM boxes WHERE id NOT IN (-5,-4,9223372036854775807);").AsInteger();
        generated.Should().BePositive().And.BeLessThan(long.MaxValue);
    }

    [Test]
    public void PredicatesAliasesIntegrityAndDiagnosticFunctionsWork()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE VIRTUAL TABLE boxes USING rtree(id,min,max);");
        Execute(connection, "INSERT INTO boxes VALUES(1,0,1),(2,2,3),(3,4,5);");
        Execute(connection, "CREATE TABLE probes(value REAL);");
        Execute(connection, "INSERT INTO probes VALUES(2.5);");

        ReadRows(connection, "SELECT id FROM boxes WHERE rowid=2;").Select(Join).Should().Equal("2");
        ReadRows(connection, "SELECT id FROM boxes WHERE max>=2.5 AND min<=2.5;").Select(Join).Should().Equal("2");
        ReadRows(connection, "SELECT id FROM boxes WHERE min<'abc' ORDER BY id;").Select(Join).Should().Equal("1", "2", "3");
        ReadRows(connection, "SELECT id FROM boxes WHERE min IS NULL;").Should().BeEmpty();
        ReadRows(connection, "SELECT id FROM boxes WHERE min IS NOT NULL AND id!=2 ORDER BY id;")
            .Select(Join).Should().Equal("1", "3");
        ReadRows(
                connection,
                "SELECT boxes.id FROM boxes JOIN probes ON boxes.max>=probes.value AND boxes.min<=probes.value;")
            .Select(Join).Should().Equal("2");
        ReadRows(connection, "EXPLAIN QUERY PLAN SELECT id FROM boxes WHERE rowid=2;")
            .Single()[3].AsText().Should().Contain("VIRTUAL TABLE INDEX 1:");

        ReadScalar(connection, "SELECT rtreecheck('boxes');").Should().Be(SqlValue.Text("ok"));
        ReadScalar(connection, "SELECT rtreecheck('main','boxes');").Should().Be(SqlValue.Text("ok"));
        ReadScalar(connection, "PRAGMA integrity_check(boxes);").Should().Be(SqlValue.Text("ok"));
        ReadScalar(connection, "PRAGMA quick_check;").Should().Be(SqlValue.Text("ok"));
        ReadScalar(connection, "SELECT rtreedepth(x'00010000');").Should().Be(SqlValue.Integer(1));
        ReadScalar(
            connection,
            "SELECT rtreenode(1,x'000000010000000000000001000000003F800000');")
            .Should().Be(SqlValue.Text("{1 0 1}"));
    }

    [Test]
    public void TransactionsSavepointsAndReopenPreserveCoordinatesAndAuxiliaryValues()
    {
        var path = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            $"managed-rtree-{Guid.NewGuid():N}.db");
        try
        {
            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                Execute(connection, "CREATE VIRTUAL TABLE boxes USING rtree(id,min,max,+n,+i,+r,+t,+b);");
                Execute(connection, "BEGIN;");
                Execute(connection, "INSERT INTO boxes VALUES(1,0.1,0.1,NULL,42,1.25,'text',x'0102');");
                Execute(connection, "SAVEPOINT nested;");
                Execute(connection, "INSERT INTO boxes VALUES(2,2,3,NULL,0,0,'rolled back',NULL);");
                Execute(connection, "ROLLBACK TO nested;");
                Execute(connection, "RELEASE nested;");
                Execute(connection, "COMMIT;");
            }

            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                ReadRows(connection, "SELECT id,min,max,n,i,r,t,hex(b) FROM boxes;")
                    .Should().ContainSingle()
                    .Which.Should().Equal(
                        SqlValue.Integer(1),
                        SqlValue.Real(0.09999998658895493),
                        SqlValue.Real(0.10000000149011612),
                        SqlValue.Null,
                        SqlValue.Integer(42),
                        SqlValue.Real(1.25),
                        SqlValue.Text("text"),
                        SqlValue.Text("0102"));
                ReadScalar(connection, "SELECT rtreecheck('boxes');").Should().Be(SqlValue.Text("ok"));
            }
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
            if (File.Exists(path + "-wal"))
                File.Delete(path + "-wal");
            if (File.Exists(path + "-shm"))
                File.Delete(path + "-shm");
        }
    }

    [Test]
    public void VersionedPayloadRejectsShapeCorruptionNanTruncationAndTrailingBytes()
    {
        var module = ManagedVirtualTableModuleRegistry.Resolve("rtree");
        var context = new ManagedVirtualTableCreateContext("boxes", ["id", "min", "max", "+payload"]);
        var table = module.Create(context);
        table.Update(
        [
            SqlValue.Null,
            SqlValue.Null,
            SqlValue.Integer(1),
            SqlValue.Real(0),
            SqlValue.Real(1),
            SqlValue.Text("payload"),
        ]);
        var payload = table.GetPersistencePayload();

        var trailing = payload.Bytes.ToArray();
        Array.Resize(ref trailing, trailing.Length + 1);
        Action restoreTrailing = () => module.Create(
            context,
            new ManagedVirtualTablePersistencePayload(payload.Version, trailing));
        restoreTrailing.Should().Throw<EmbeddedSqlException>()
            .WithMessage("*trailing bytes*");

        var nan = payload.Bytes.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(nan.AsSpan(28, sizeof(int)), 0x7fc00000);
        Action restoreNan = () => module.Create(
            context,
            new ManagedVirtualTablePersistencePayload(payload.Version, nan));
        restoreNan.Should().Throw<EmbeddedSqlException>()
            .WithMessage("*NaN coordinate*");

        Action restoreTruncated = () => module.Create(
            context,
            new ManagedVirtualTablePersistencePayload(payload.Version, payload.Bytes.Span[..^1]));
        restoreTruncated.Should().Throw<EmbeddedSqlException>()
            .WithMessage("*truncated managed virtual-table persistence payload*");

        var wrongDimensions = payload.Bytes.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(wrongDimensions.AsSpan(sizeof(int), sizeof(int)), 2);
        Action restoreWrongShape = () => module.Create(
            context,
            new ManagedVirtualTablePersistencePayload(payload.Version, wrongDimensions));
        restoreWrongShape.Should().Throw<EmbeddedSqlException>()
            .WithMessage("*shape does not match*");

        var legacyWriter = new ManagedVirtualTablePayloadWriter();
        legacyWriter.WriteInt32(3);
        legacyWriter.WriteInt64(2);
        legacyWriter.WriteInt32(1);
        legacyWriter.WriteInt64(1);
        legacyWriter.WriteDouble(0.1);
        legacyWriter.WriteDouble(0.1);
        var upgraded = module.Create(
            new ManagedVirtualTableCreateContext("legacy", ["id", "min", "max"]),
            new ManagedVirtualTablePersistencePayload(1, legacyWriter.ToArray()));
        using var cursor = upgraded.Open();
        cursor.Filter(new ManagedVirtualTablePlan([]), []).Should().BeTrue();
        cursor.Column(1).Should().Be(SqlValue.Real(0.09999998658895493));
        cursor.Column(2).Should().Be(SqlValue.Real(0.10000000149011612));
        upgraded.GetPersistencePayload().Version.Should().Be(2);
    }

    [Test]
    public void ForeignShadowLayoutsFailClosedAndManagedPayloadIsNotClaimedPortable()
    {
        var sqlitePath = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            $"sqlite-rtree-{Guid.NewGuid():N}.db");
        var managedPath = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            $"ahtola-rtree-{Guid.NewGuid():N}.db");
        try
        {
            using (var sqlite = new MsData.SqliteConnection($"Data Source={sqlitePath};Pooling=False"))
            {
                sqlite.Open();
                using var command = sqlite.CreateCommand();
                command.CommandText = "CREATE VIRTUAL TABLE boxes USING rtree(id,min,max); INSERT INTO boxes VALUES(1,0,1);";
                command.ExecuteNonQuery();
            }

            Action openForeign = () =>
            {
                using var database = EmbeddedDatabase.OpenFile(sqlitePath);
            };
            openForeign.Should().Throw<EmbeddedSqlException>()
                .WithMessage("*foreign SQLite R-Tree shadow-table layouts are not supported*");

            using (var database = EmbeddedDatabase.OpenFile(managedPath))
            using (var connection = database.Connect())
            {
                Execute(connection, "CREATE VIRTUAL TABLE boxes USING rtree(id,min,max);");
                Execute(connection, "INSERT INTO boxes VALUES(1,0,1);");
            }

            using var sqliteReader = new MsData.SqliteConnection($"Data Source={managedPath};Pooling=False");
            sqliteReader.Open();
            using var read = sqliteReader.CreateCommand();
            read.CommandText = "SELECT * FROM boxes;";
            Action queryManagedPayload = () => read.ExecuteNonQuery();
            queryManagedPayload.Should().Throw<MsData.SqliteException>();
        }
        finally
        {
            DeleteDatabase(sqlitePath);
            DeleteDatabase(managedPath);
        }
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        while (statement.Step() != StatementStepResult.Done)
        {
        }
    }

    private static void Execute(MsData.SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static bool TryExecute(EmbeddedConnection connection, string sql)
    {
        try
        {
            Execute(connection, sql);
            return true;
        }
        catch (EmbeddedSqlException)
        {
            return false;
        }
    }

    private static bool TryExecute(MsData.SqliteConnection connection, string sql)
    {
        try
        {
            Execute(connection, sql);
            return true;
        }
        catch (MsData.SqliteException)
        {
            return false;
        }
    }

    private static IReadOnlyList<SqlValue[]> ReadRows(EmbeddedConnection connection, string sql)
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

    private static SqlValue ReadScalar(EmbeddedConnection connection, string sql)
        => ReadRows(connection, sql).Single().Single();

    private static IReadOnlyList<SqlValue[]> ReadRows(MsData.SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var rows = new List<SqlValue[]>();
        while (reader.Read())
        {
            var row = new SqlValue[reader.FieldCount];
            for (var index = 0; index < row.Length; index++)
            {
                row[index] = reader.GetValue(index) switch
                {
                    null or DBNull => SqlValue.Null,
                    long integer => SqlValue.Integer(integer),
                    int integer => SqlValue.Integer(integer),
                    double real => SqlValue.Real(real),
                    float real => SqlValue.Real(real),
                    string text => SqlValue.Text(text),
                    byte[] blob => SqlValue.Blob(blob),
                    var value => throw new InvalidOperationException(
                        $"Unexpected SQLite value type {value.GetType().Name}."),
                };
            }
            rows.Add(row);
        }
        return rows;
    }

    private static string Join(IEnumerable<SqlValue> row)
        => string.Join('\u001F', row.Select(value => value.Kind switch
        {
            SqlValueKind.Null => "NULL",
            SqlValueKind.Integer => value.AsInteger().ToString(System.Globalization.CultureInfo.InvariantCulture),
            SqlValueKind.Real => value.AsReal().ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            SqlValueKind.Text => value.AsText(),
            SqlValueKind.Blob => Convert.ToHexString(value.AsBlob().Span),
            _ => throw new InvalidOperationException(),
        }));

    private static void DeleteDatabase(string path)
    {
        foreach (var candidate in new[] { path, path + "-wal", path + "-shm" })
        {
            if (File.Exists(candidate))
                File.Delete(candidate);
        }
    }
}
