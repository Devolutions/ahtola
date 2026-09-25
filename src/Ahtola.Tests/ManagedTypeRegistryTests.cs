using Ahtola.Core;
using Ahtola.Core.Parsing;
using Ahtola.Core.Storage;
using Ahtola.Tests.Sqltest;
using AwesomeAssertions;
using MsData = Microsoft.Data.Sqlite;

namespace Ahtola.Tests;

[NonParallelizable]
public sealed class ManagedTypeRegistryTests
{
    [Test]
    public void DeclarationsRequireOptInAndUnsupportedTypedColumnsFailClosed()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        Action disabled = () => Execute(connection, "CREATE DOMAIN positive AS INTEGER NOT NULL CHECK (value > 0)");
        disabled.Should().Throw<EmbeddedSqlException>().WithMessage("*not enabled*");
        Action reserved = () => Execute(connection, "CREATE TABLE main.__turso_internal_types(name TEXT)");
        reserved.Should().Throw<EmbeddedSqlException>().WithMessage("*reserved*");
        connection.ExperimentalCustomTypesEnabled = true;

        Execute(connection, "CREATE DOMAIN positive AS INTEGER DEFAULT 7 NOT NULL CHECK (value > 0)");
        Execute(connection, "CREATE TYPE counter BASE INTEGER");
        Scalar(connection, "SELECT count(*) FROM __turso_internal_types").Should().Be(2);
        database.LiveCatalog.TypeDefinitions.Keys.Should().BeEquivalentTo(["positive", "counter"]);

        Action typed = () => Execute(connection, "CREATE TABLE entries(value positive)");
        typed.Should().Throw<EmbeddedSqlException>().WithMessage("*require STRICT tables*");
        Execute(connection, "CREATE TABLE strict_entries(value positive) STRICT");
        Action quoted = () => Execute(connection, "CREATE TABLE quoted_entries(value \"positive\")");
        quoted.Should().Throw<EmbeddedSqlException>().WithMessage("*require STRICT tables*");
        Execute(connection, "CREATE TABLE counters(value counter) STRICT");
        Execute(connection, "INSERT INTO counters VALUES ('8')");
        Scalar(connection, "SELECT value FROM counters").Should().Be(8);
        Execute(connection, "CREATE TABLE ordinary(value INTEGER)");
        Action altered = () => Execute(connection, "ALTER TABLE ordinary ADD COLUMN typed \"positive\"");
        altered.Should().Throw<EmbeddedSqlException>().WithMessage("*not yet supported*");
        Action directWrite = () => Execute(connection,
            "INSERT INTO __turso_internal_types VALUES ('forged', 'CREATE TYPE forged BASE INTEGER')");
        directWrite.Should().Throw<EmbeddedSqlException>().WithMessage("*cannot be modified directly*");
        Action directDrop = () => Execute(connection, "DROP TABLE __turso_internal_types");
        directDrop.Should().Throw<EmbeddedSqlException>().WithMessage("*cannot be modified directly*");
        Scalar(connection, "SELECT count(*) FROM __turso_internal_types").Should().Be(2);
    }

    [Test]
    public void StrictIdentityIntegerTypeValidatesWritesTransactionsAndReopen()
    {
        var path = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"{Guid.NewGuid():N}.db");
        try
        {
            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                connection.ExperimentalCustomTypesEnabled = true;
                Execute(connection, "CREATE TYPE counter BASE INTEGER");
                Action nonStrict = () => Execute(connection, "CREATE TABLE loose(value counter)");
                nonStrict.Should().Throw<EmbeddedSqlException>().WithMessage("*require STRICT tables*");
                Action typedKey = () => Execute(connection, "CREATE TABLE keyed(value counter PRIMARY KEY) STRICT");
                typedKey.Should().Throw<EmbeddedSqlException>().WithMessage("*cannot be generated, a primary key*");
                Execute(connection, "CREATE TABLE entries(value counter NOT NULL UNIQUE) STRICT");
                Execute(connection, "INSERT INTO entries VALUES ('9')");
                Scalar(connection, "SELECT value FROM entries").Should().Be(9);
                Action duplicate = () => Execute(connection, "INSERT INTO entries VALUES (9)");
                duplicate.Should().Throw<EmbeddedSqlException>().WithMessage("*UNIQUE constraint failed*");
                Action invalid = () => Execute(connection, "INSERT INTO entries VALUES (x'01')");
                invalid.Should().Throw<EmbeddedSqlException>().WithMessage("*cannot store BLOB*INTEGER column*");
                Scalar(connection, "SELECT count(*) FROM entries").Should().Be(1);

                Execute(connection, "BEGIN");
                Execute(connection, "SAVEPOINT before_update");
                Execute(connection, "UPDATE entries SET value = 10");
                Execute(connection, "ROLLBACK TO before_update");
                Scalar(connection, "SELECT value FROM entries").Should().Be(9);
                Execute(connection, "COMMIT");
            }

            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                connection.ExperimentalCustomTypesEnabled = true;
                Scalar(connection, "SELECT value FROM entries").Should().Be(9);
                Execute(connection, "UPDATE entries SET value = '11'");
                Scalar(connection, "SELECT value FROM entries").Should().Be(11);
                Action altered = () => Execute(connection, "ALTER TABLE entries ADD COLUMN another INTEGER");
                altered.Should().Throw<EmbeddedSqlException>().WithMessage("*not yet supported*");
                Scalar(connection, "SELECT value FROM entries").Should().Be(11);
            }
        }
        finally
        {
            DeleteDatabaseFiles(path);
        }
    }

    [Test]
    public void FileBackedDefinitionsReloadAndAdvanceTheSchemaCookie()
    {
        var path = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"{Guid.NewGuid():N}.db");
        try
        {
            long before;
            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                connection.ExperimentalCustomTypesEnabled = true;
                before = Scalar(connection, "PRAGMA schema_version");
                Execute(connection, "CREATE DOMAIN scored AS INTEGER DEFAULT 3 NOT NULL CONSTRAINT positive CHECK (value > 0)");
                Scalar(connection, "PRAGMA schema_version").Should().Be(before + 1);
                Execute(connection, "CREATE TYPE counter BASE INTEGER");
                Scalar(connection, "PRAGMA schema_version").Should().Be(before + 2);
                Execute(connection, "CREATE DOMAIN IF NOT EXISTS scored AS INTEGER");
                Scalar(connection, "PRAGMA schema_version").Should().Be(before + 2);
            }

            using var reopened = EmbeddedDatabase.OpenFile(path);
            using var reader = reopened.Connect();
            reopened.LiveCatalog.TypeDefinitions.Keys.Should().BeEquivalentTo(["scored", "counter"]);
            var domain = reopened.LiveCatalog.TypeDefinitions["scored"]
                .Should().BeOfType<CreateDomainStatement>().Which;
            domain.NotNull.Should().BeTrue();
            domain.Default.Should().NotBeNull();
            domain.Checks.Should().ContainSingle().Which.Name.Should().Be("positive");
            Scalar(reader, "SELECT count(*) FROM __turso_internal_types").Should().Be(2);
            Scalar(reader, "PRAGMA schema_version").Should().BeGreaterThan(0);
            reader.ExperimentalCustomTypesEnabled = true;
            Action duplicate = () => Execute(reader, "CREATE DOMAIN scored AS INTEGER");
            duplicate.Should().Throw<EmbeddedSqlException>().WithMessage("*already exists*");
            Execute(reader, "CREATE TABLE amounts(value scored) STRICT");
            Execute(reader, "INSERT INTO amounts DEFAULT VALUES");
            Scalar(reader, "SELECT value FROM amounts").Should().Be(3);
            Execute(reader, "CREATE TABLE ordinary(value INTEGER)");
            Execute(reader, "INSERT INTO ordinary VALUES (4)");
            Scalar(reader, "SELECT value FROM ordinary").Should().Be(4);
            Scalar(reader, "PRAGMA schema_version").Should().Be(before + 4);
        }
        finally
        {
            DeleteDatabaseFiles(path);
        }
    }

    [Test]
    public void RollbackAndSavepointDiscardDefinitions()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        connection.ExperimentalCustomTypesEnabled = true;
        Execute(connection, "BEGIN");
        Execute(connection, "CREATE DOMAIN outer_domain AS INTEGER");
        Execute(connection, "SAVEPOINT inner");
        Execute(connection, "CREATE DOMAIN inner_domain AS INTEGER");
        Scalar(connection, "SELECT count(*) FROM __turso_internal_types").Should().Be(2);
        Execute(connection, "ROLLBACK TO inner");
        Scalar(connection, "SELECT count(*) FROM __turso_internal_types").Should().Be(1);
        Execute(connection, "ROLLBACK");
        database.LiveCatalog.TypeDefinitions.Should().BeEmpty();
        Action missingBacking = () => Scalar(connection, "SELECT count(*) FROM __turso_internal_types");
        missingBacking.Should().Throw<EmbeddedSqlException>();
    }

    [Test]
    public void FileRollbackDoesNotPublishTheRegistryOrSchemaCookie()
    {
        var path = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"{Guid.NewGuid():N}.db");
        try
        {
            long before;
            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                connection.ExperimentalCustomTypesEnabled = true;
                before = Scalar(connection, "PRAGMA schema_version");
                Execute(connection, "BEGIN");
                Execute(connection, "CREATE DOMAIN uncommitted AS INTEGER NOT NULL");
                database.LiveCatalog.TypeDefinitions.Should().BeEmpty();
                Execute(connection, "ROLLBACK");
                database.LiveCatalog.TypeDefinitions.Should().BeEmpty();
            }

            using var reopened = EmbeddedDatabase.OpenFile(path);
            using var reader = reopened.Connect();
            reopened.LiveCatalog.TypeDefinitions.Should().BeEmpty();
            Scalar(reader, "PRAGMA schema_version").Should().Be(before);
        }
        finally
        {
            DeleteDatabaseFiles(path);
        }
    }

    [Test]
    public void APeerRefreshSeesCommittedDefinitionsWithoutSeeingRolledBackOnes()
    {
        var path = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"{Guid.NewGuid():N}.db");
        try
        {
            using var writerDatabase = EmbeddedDatabase.OpenFile(path);
            using var readerDatabase = EmbeddedDatabase.OpenFile(path);
            using var writer = writerDatabase.Connect();
            using var reader = readerDatabase.Connect();
            writer.ExperimentalCustomTypesEnabled = true;
            reader.ExperimentalCustomTypesEnabled = true;

            Execute(writer, "BEGIN");
            Execute(writer, "CREATE DOMAIN provisional AS INTEGER");
            readerDatabase.LiveCatalog.TypeDefinitions.Should().BeEmpty();
            Execute(writer, "ROLLBACK");
            Execute(writer, "CREATE DOMAIN committed AS INTEGER");
            Scalar(reader, "SELECT count(*) FROM __turso_internal_types").Should().Be(1);
            readerDatabase.LiveCatalog.TypeDefinitions.Keys.Should().ContainSingle().Which.Should().Be("committed");
        }
        finally
        {
            DeleteDatabaseFiles(path);
        }
    }

    [Test]
    public void ForeignTypedTableFailsOpenRatherThanWritingWithOrdinaryAffinity()
    {
        var path = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"{Guid.NewGuid():N}.db");
        try
        {
            using (var sqlite = new MsData.SqliteConnection($"Data Source={path};Pooling=False"))
            {
                sqlite.Open();
                using var command = sqlite.CreateCommand();
                command.CommandText = """
                    CREATE TABLE __turso_internal_types(name TEXT PRIMARY KEY, sql TEXT);
                    INSERT INTO __turso_internal_types(name, sql)
                    VALUES ('positive', 'CREATE DOMAIN positive AS INTEGER CHECK (value > 0)');
                    CREATE TABLE foreign_values(value positive);
                    """;
                command.ExecuteNonQuery();
            }

            Action open = () =>
            {
                using var database = EmbeddedDatabase.OpenFile(path);
            };
            open.Should().Throw<EmbeddedSqlException>().WithMessage("*require STRICT tables*");
        }
        finally
        {
            DeleteDatabaseFiles(path);
        }
    }

    [Test]
    public void StrictIntegerDomainEnforcesInheritedDefaultNotNullAndChecksOnAllWrites()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        connection.ExperimentalCustomTypesEnabled = true;
        Execute(connection, "CREATE DOMAIN positive AS INTEGER DEFAULT 7 NOT NULL CONSTRAINT positive_check CHECK (value > 0)");
        Execute(connection, "CREATE TABLE entries(id INTEGER PRIMARY KEY, value positive) STRICT");
        Execute(connection, "INSERT INTO entries(id) VALUES (1)");
        Scalar(connection, "SELECT value FROM entries WHERE id = 1").Should().Be(7);
        Execute(connection, "INSERT INTO entries VALUES (2, '8')");
        Scalar(connection, "SELECT value FROM entries WHERE id = 2").Should().Be(8);
        using (var stored = connection.Prepare("SELECT value FROM entries WHERE id = 2"))
        {
            stored.Step().Should().Be(StatementStepResult.Row);
            stored.GetValue(0).Kind.Should().Be(SqlValueKind.Integer);
        }

        Action nullInsert = () => Execute(connection, "INSERT INTO entries VALUES (3, NULL)");
        nullInsert.Should().Throw<EmbeddedSqlException>().WithMessage("*NOT NULL constraint failed: entries.value*");
        Action checkInsert = () => Execute(connection, "INSERT INTO entries VALUES (3, -1)");
        checkInsert.Should().Throw<EmbeddedSqlException>().WithMessage("*positive_check*");
        Action wrongType = () => Execute(connection, "INSERT INTO entries VALUES (3, 'abc')");
        wrongType.Should().Throw<EmbeddedSqlException>().WithMessage("*cannot store TEXT value in INTEGER column*");
        Action partialBatch = () => Execute(connection, "INSERT INTO entries VALUES (3, 4), (4, -1)");
        partialBatch.Should().Throw<EmbeddedSqlException>().WithMessage("*positive_check*");
        Action checkUpdate = () => Execute(connection, "UPDATE entries SET value = 0 WHERE id = 1");
        checkUpdate.Should().Throw<EmbeddedSqlException>().WithMessage("*positive_check*");
        Action nullUpdate = () => Execute(connection, "UPDATE entries SET value = NULL WHERE id = 2");
        nullUpdate.Should().Throw<EmbeddedSqlException>().WithMessage("*NOT NULL constraint failed: entries.value*");
        Scalar(connection, "SELECT count(*) FROM entries").Should().Be(2);
        Scalar(connection, "SELECT value FROM entries WHERE id = 1").Should().Be(7);
        Scalar(connection, "SELECT value FROM entries WHERE id = 2").Should().Be(8);
        Execute(connection, "UPDATE entries SET value = 9 WHERE id = 1");
        Scalar(connection, "SELECT value FROM entries WHERE id = 1").Should().Be(9);
    }

    [Test]
    public void DomainValuesSurviveReopenAndPeerRefreshWithoutLeakingRolledBackWrites()
    {
        var path = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"{Guid.NewGuid():N}.db");
        try
        {
            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                connection.ExperimentalCustomTypesEnabled = true;
                Execute(connection, "CREATE DOMAIN positive AS INTEGER DEFAULT 7 NOT NULL CHECK (value > 0)");
                Execute(connection, "CREATE TABLE entries(id INTEGER PRIMARY KEY, value positive) STRICT");
                Execute(connection, "INSERT INTO entries(id) VALUES (1)");
                Execute(connection, "SAVEPOINT attempt");
                Execute(connection, "UPDATE entries SET value = 8 WHERE id = 1");
                Execute(connection, "ROLLBACK TO attempt");
                Execute(connection, "RELEASE attempt");
                Scalar(connection, "SELECT value FROM entries").Should().Be(7);
                using var peerDatabase = EmbeddedDatabase.OpenFile(path);
                using var peer = peerDatabase.Connect();
                Scalar(peer, "SELECT value FROM entries").Should().Be(7);
                Action invalid = () => Execute(peer, "INSERT INTO entries VALUES (2, -1)");
                invalid.Should().Throw<EmbeddedSqlException>().WithMessage("*positive_0*");
                Scalar(peer, "SELECT count(*) FROM entries").Should().Be(1);
            }
            using var reopened = EmbeddedDatabase.OpenFile(path);
            using var reader = reopened.Connect();
            Scalar(reader, "SELECT value FROM entries").Should().Be(7);
            Action invalidAfterReopen = () => Execute(reader, "UPDATE entries SET value = -1");
            invalidAfterReopen.Should().Throw<EmbeddedSqlException>().WithMessage("*positive_0*");
            Scalar(reader, "SELECT value FROM entries").Should().Be(7);
        }
        finally
        {
            DeleteDatabaseFiles(path);
        }
    }

    [Test]
    public void UnsupportedDomainDefinitionsAndTypedSchemaChangesFailBeforePublication()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        connection.ExperimentalCustomTypesEnabled = true;
        Action otherBase = () => Execute(connection, "CREATE DOMAIN bad AS TEXT");
        otherBase.Should().Throw<EmbeddedSqlException>().WithMessage("*INTEGER*");
        Action structure = () => Execute(connection, "CREATE TYPE bad AS STRUCT(value INTEGER)");
        structure.Should().Throw<EmbeddedSqlException>().WithMessage("*STRUCT*not supported*");
        Action unsupportedDefault = () => Execute(connection, "CREATE DOMAIN bad AS INTEGER DEFAULT 'text'");
        unsupportedDefault.Should().Throw<EmbeddedSqlException>().WithMessage("*constant INTEGER*");
        Action unsupportedCheck = () => Execute(connection, "CREATE DOMAIN bad AS INTEGER CHECK (abs(value) > 0)");
        unsupportedCheck.Should().Throw<EmbeddedSqlException>().WithMessage("*Unsupported expression*");
        database.LiveCatalog.TypeDefinitions.Should().BeEmpty();
        Execute(connection, "CREATE DOMAIN positive AS INTEGER CHECK (value > 0)");
        Execute(connection, "CREATE TABLE entries(value positive) STRICT");
        Action altered = () => Execute(connection, "ALTER TABLE entries ADD COLUMN another INTEGER");
        altered.Should().Throw<EmbeddedSqlException>().WithMessage("*not yet supported*");
        Scalar(connection, "SELECT count(*) FROM entries").Should().Be(0);
    }

    [Test]
    public void DomainValidationCoversUpsertAndTriggerBodies()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        connection.ExperimentalCustomTypesEnabled = true;
        Execute(connection, "CREATE DOMAIN positive AS INTEGER DEFAULT 7 NOT NULL CHECK (value > 0)");
        Execute(connection, "CREATE TABLE entries(id INTEGER PRIMARY KEY, value positive) STRICT");
        Execute(connection, "INSERT INTO entries VALUES (1, 5)");
        Action upsert = () => Execute(connection,
            "INSERT INTO entries VALUES (1, 2) ON CONFLICT(id) DO UPDATE SET value = -1");
        upsert.Should().Throw<EmbeddedSqlException>().WithMessage("*positive_0*");
        Scalar(connection, "SELECT value FROM entries WHERE id = 1").Should().Be(5);
        Execute(connection,
            "CREATE TRIGGER invalidate AFTER INSERT ON entries BEGIN UPDATE entries SET value = -1 WHERE id = NEW.id; END");
        Action trigger = () => Execute(connection, "INSERT INTO entries VALUES (2, 5)");
        trigger.Should().Throw<EmbeddedSqlException>().WithMessage("*positive_0*");
        Scalar(connection, "SELECT count(*) FROM entries").Should().Be(1);
        Scalar(connection, "SELECT value FROM entries WHERE id = 1").Should().Be(5);
    }

    [Test]
    public void DomainChecksRemainEnforcedWithCheckPragmaAndInsertSelect()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        connection.ExperimentalCustomTypesEnabled = true;
        Execute(connection, "CREATE DOMAIN positive AS INTEGER DEFAULT 7 NOT NULL CHECK (value > 0)");
        Execute(connection, "CREATE TABLE entries(value positive) STRICT");
        Execute(connection, "PRAGMA ignore_check_constraints = ON");
        Action select = () => Execute(connection, "INSERT INTO entries(value) SELECT -1");
        select.Should().Throw<EmbeddedSqlException>().WithMessage("*positive_0*");
        Scalar(connection, "SELECT count(*) FROM entries").Should().Be(0);
        Execute(connection, "INSERT INTO entries DEFAULT VALUES");
        Action update = () => Execute(connection, "UPDATE entries SET value = 0");
        update.Should().Throw<EmbeddedSqlException>().WithMessage("*positive_0*");
        Scalar(connection, "SELECT value FROM entries").Should().Be(7);
        Execute(connection, "INSERT OR REPLACE INTO entries VALUES (NULL)");
        Scalar(connection, "SELECT count(*) FROM entries").Should().Be(2);
        Scalar(connection, "SELECT min(value) FROM entries").Should().Be(7);
    }

    [Test]
    public void DomainTablesDeclineCompiledReadAndWriteRoutes()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        connection.ExperimentalCustomTypesEnabled = true;
        Execute(connection, "CREATE DOMAIN positive AS INTEGER CHECK (value > 0)");
        Execute(connection, "CREATE TABLE entries(value positive) STRICT");
        foreach (var sql in new[]
                 {
                     "EXPLAIN QUERY PLAN SELECT value FROM entries",
                     "EXPLAIN QUERY PLAN INSERT INTO entries VALUES (1)",
                     "EXPLAIN QUERY PLAN UPDATE entries SET value = 2",
                 })
        {
            using var plan = connection.Prepare(sql);
            plan.Step().Should().Be(StatementStepResult.Row);
            plan.GetValue(3).AsText().Should().Be("MANAGED EVALUATOR FALLBACK");
        }
    }

    [Test]
    public void ReturningCastOnOrdinaryTableDoesNotBypassDomainRejection()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        connection.ExperimentalCustomTypesEnabled = true;
        Execute(connection, "CREATE DOMAIN positive AS INTEGER CHECK (value > 0)");
        Execute(connection, "CREATE TABLE ordinary(v INTEGER)");

        Action insert = () => Execute(connection,
            "INSERT INTO ordinary VALUES (-1) RETURNING CAST(v AS positive)");
        insert.Should().Throw<EmbeddedSqlException>().WithMessage("*CAST to custom type*");
        Scalar(connection, "SELECT count(*) FROM ordinary").Should().Be(0);

        Execute(connection, "INSERT INTO ordinary VALUES (5)");
        Action update = () => Execute(connection,
            "UPDATE ordinary SET v = -2 RETURNING CAST(v AS positive)");
        update.Should().Throw<EmbeddedSqlException>().WithMessage("*CAST to custom type*");
        Scalar(connection, "SELECT v FROM ordinary").Should().Be(5);

        Action delete = () => Execute(connection,
            "DELETE FROM ordinary RETURNING CAST(v AS positive)");
        delete.Should().Throw<EmbeddedSqlException>().WithMessage("*CAST to custom type*");
        Scalar(connection, "SELECT count(*) FROM ordinary").Should().Be(1);
    }

    [Test]
    public void CascadedForeignKeyUpdateCannotBypassDomainChecks()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        connection.ExperimentalCustomTypesEnabled = true;
        Execute(connection, "PRAGMA foreign_keys = ON");
        Execute(connection, "CREATE DOMAIN positive AS INTEGER CHECK (value > 0)");
        Execute(connection, "CREATE TABLE parents(id INTEGER PRIMARY KEY)");
        Execute(connection, "CREATE TABLE children(value positive REFERENCES parents(id) ON UPDATE CASCADE) STRICT");
        Execute(connection, "INSERT INTO parents VALUES (1)");
        Execute(connection, "INSERT INTO children VALUES (1)");
        Action cascade = () => Execute(connection, "UPDATE parents SET id = -1 WHERE id = 1");
        cascade.Should().Throw<EmbeddedSqlException>().WithMessage("*positive_0*");
        Scalar(connection, "SELECT id FROM parents").Should().Be(1);
        Scalar(connection, "SELECT value FROM children").Should().Be(1);
    }

    [Test]
    public void DomainTypedTableAndSchemaCookieRollBackTogether()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        connection.ExperimentalCustomTypesEnabled = true;
        Execute(connection, "CREATE DOMAIN positive AS INTEGER NOT NULL CHECK (value > 0)");
        var before = Scalar(connection, "PRAGMA schema_version");
        Execute(connection, "BEGIN");
        Execute(connection, "CREATE TABLE entries(value positive) STRICT");
        Execute(connection, "INSERT INTO entries VALUES (3)");
        Execute(connection, "SAVEPOINT write");
        Execute(connection, "UPDATE entries SET value = 4");
        Execute(connection, "ROLLBACK TO write");
        Scalar(connection, "SELECT value FROM entries").Should().Be(3);
        Execute(connection, "ROLLBACK");
        Scalar(connection, "PRAGMA schema_version").Should().Be(before);
        Action missing = () => Scalar(connection, "SELECT value FROM entries");
        missing.Should().Throw<EmbeddedSqlException>();
    }

    [Test]
    public void DomainCastFailsClosedInsteadOfUsingOrdinaryAffinity()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        connection.ExperimentalCustomTypesEnabled = true;
        Execute(connection, "CREATE DOMAIN positive AS INTEGER CHECK (value > 0)");
        Action cast = () => Scalar(connection, "SELECT CAST(-1 AS positive)");
        cast.Should().Throw<EmbeddedSqlException>().WithMessage("*CAST to custom type*not yet supported*");
        Action compound = () => Scalar(connection, "SELECT CAST(-1 AS positive) UNION ALL SELECT 1");
        compound.Should().Throw<EmbeddedSqlException>().WithMessage("*CAST to custom type*not yet supported*");
    }

    [Test]
    public void IdentityTypeCastKeepsStoredValueAcrossSelectAggregateAndReturning()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        connection.ExperimentalCustomTypesEnabled = true;
        Execute(connection, "CREATE TYPE counter BASE INTEGER");
        Execute(connection, "CREATE TABLE ordinary(value INTEGER)");

        Scalar(connection, "SELECT CAST(42 AS counter)").Should().Be(42);
        using (var text = connection.Prepare("SELECT CAST('7' AS counter)"))
        {
            text.Step().Should().Be(StatementStepResult.Row);
            text.GetValue(0).AsText().Should().Be("7");
            text.Step().Should().Be(StatementStepResult.Done);
        }

        using (var insert = connection.Prepare(
            "INSERT INTO ordinary VALUES (5) RETURNING CAST(value AS counter)"))
        {
            insert.Step().Should().Be(StatementStepResult.Row);
            insert.GetValue(0).AsInteger().Should().Be(5);
            insert.Step().Should().Be(StatementStepResult.Done);
        }
        Scalar(connection, "SELECT CAST(COUNT(*) AS counter) FROM ordinary").Should().Be(1);
        Scalar(connection, "SELECT value FROM ordinary").Should().Be(5);
    }

    [Test]
    public void IntegerDomainParametersUsePrimitiveAffinityAndStillValidateWrites()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        connection.ExperimentalCustomTypesEnabled = true;
        Execute(connection, "CREATE DOMAIN positive AS INTEGER CHECK (value > 0)");
        Execute(connection, "CREATE TABLE entries(value positive) STRICT");
        using (var insert = connection.Prepare("INSERT INTO entries VALUES (?)"))
        {
            insert.Bind(1, SqlValue.Text("3"));
            insert.Step().Should().Be(StatementStepResult.Done);
        }
        using (var select = connection.Prepare("SELECT value FROM entries WHERE value = ?"))
        {
            select.Bind(1, SqlValue.Integer(3));
            select.Step().Should().Be(StatementStepResult.Row);
            select.GetValue(0).AsInteger().Should().Be(3);
            select.Step().Should().Be(StatementStepResult.Done);
        }
        using (var rejected = connection.Prepare("INSERT INTO entries VALUES (?)"))
        {
            rejected.Bind(1, SqlValue.Integer(-1));
            Action invalid = () => rejected.Step();
            invalid.Should().Throw<EmbeddedSqlException>().WithMessage("*positive_0*");
        }
        Scalar(connection, "SELECT count(*) FROM entries").Should().Be(1);
    }

    [Test]
    [TestCase("domain-integer-default")]
    [TestCase("domain-integer-column-default")]
    [TestCase("domain-integer-not-null")]
    [TestCase("domain-integer-check")]
    public void TursoIntegerDomainSqltestsMatchExpectations(string name)
    {
        var file = SqltestParser.Parse("managed-domain.sqltest", """
            @database :memory:
            test domain-integer-default {
                CREATE DOMAIN positive AS INTEGER DEFAULT 7 NOT NULL CHECK (value > 0);
                CREATE TABLE amounts(value positive) STRICT;
                INSERT INTO amounts DEFAULT VALUES;
                INSERT INTO amounts VALUES (5);
                SELECT value FROM amounts ORDER BY value;
            }
            expect {
                5
                7
            }
            test domain-integer-column-default {
                CREATE DOMAIN positive AS INTEGER DEFAULT 7;
                CREATE TABLE amounts(value positive DEFAULT 11) STRICT;
                INSERT INTO amounts DEFAULT VALUES;
                SELECT value FROM amounts;
            }
            expect {
                11
            }
            test domain-integer-not-null {
                CREATE DOMAIN positive AS INTEGER NOT NULL;
                CREATE TABLE amounts(value positive) STRICT;
                INSERT INTO amounts VALUES (NULL);
            }
            expect error {
            }
            test domain-integer-check {
                CREATE DOMAIN positive AS INTEGER CHECK (value > 0);
                CREATE TABLE amounts(value positive) STRICT;
                INSERT INTO amounts VALUES (-1);
            }
            expect error {
            }
            """);
        var test = file.Tests.Single(test => test.Name == name);
        var outcome = SqltestManagedRunner.Run(file, test, enableCustomTypes: true);
        outcome.Matched.Should().BeTrue(outcome.Detail);
    }

    [Test]
    public async Task BoundedAsyncCatalogFailsClosedWhenTypeDefinitionsExist()
    {
        const string path = "typed-registry-async.db";
        var fileSystem = new InMemoryFileSystem();
        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            connection.ExperimentalCustomTypesEnabled = true;
            Execute(connection, "CREATE DOMAIN positive AS INTEGER");
            Execute(connection, "CREATE TABLE ordinary(value INTEGER)");
        }

        await using var pager = await AsyncSqlitePager.OpenAsync(
            AsyncFileSystemAdapter.Create(fileSystem),
            path,
            path + "-wal",
            readOnly: true);
        await using var readTransaction = await pager.BeginReadAsync();
        var cache = new BoundedAsyncPageCache(readTransaction, pager.UsableSpace, capacity: 32);

        var load = async () => await AsyncSchemaCatalogLoader.LoadAsync(cache, SqliteTextEncoding.Utf8);
        await load.Should().ThrowAsync<EmbeddedSqlException>()
            .WithMessage("*not yet supported*");
    }

    private static long Scalar(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Row);
        var value = statement.GetValue(0).AsInteger();
        statement.Step().Should().Be(StatementStepResult.Done);
        return value;
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        while (statement.Step() == StatementStepResult.Row)
        {
        }
    }

    private static void DeleteDatabaseFiles(string path)
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var file = path + suffix;
            if (File.Exists(file))
                File.Delete(file);
        }
    }
}
