using Ahtola.Core;
using Ahtola.Core.Indexing;
using Ahtola.Core.Search;
using AwesomeAssertions;
using static Ahtola.Tests.ManagedIndexMethodTestHarness;

namespace Ahtola.Tests;

/// <summary>
/// File-backed durability for method indexes: create, reopen, crash, VACUUM, backup, ATTACH, and
/// the fail-closed corruption and version matrix.
/// </summary>
[NonParallelizable]
public sealed class ManagedIndexMethodDurabilityTests
{
    [Test]
    public void MethodIndexSurvivesReopen()
    {
        var path = CreateDatabasePath("managed-index-method-durability");
        try
        {
            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                SeedCorpus(connection);
            }

            using var reopened = EmbeddedDatabase.OpenFile(path);
            using var reopenedConnection = reopened.Connect();
            QueryIntegers(
                    reopenedConnection,
                    "SELECT id FROM docs WHERE fts_match(title, body, 'fox') ORDER BY id;")
                .Should().Equal(1, 3);
            QueryTexts(reopenedConnection, "SELECT sql FROM sqlite_master WHERE name = 'docs_fts';")
                .Single().Should().Contain("USING").And.NotContain("ahtola-index-method");
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [Test]
    public void UncommittedWorkIsNotVisibleAfterCrashAndReopen()
    {
        var path = CreateDatabasePath("managed-index-method-durability");
        try
        {
            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                SeedCorpus(connection);
                Execute(connection, "BEGIN;");
                Execute(connection, "INSERT INTO docs(id, title, body) VALUES (7, 'crash', 'crashing kangaroo');");
                // Dispose without COMMIT: the pager rolls the statement transaction back.
            }

            using var reopened = EmbeddedDatabase.OpenFile(path);
            using var reopenedConnection = reopened.Connect();
            QueryIntegers(reopenedConnection, "SELECT id FROM docs WHERE fts_match(title, body, 'kangaroo');")
                .Should().BeEmpty();
            QueryIntegers(reopenedConnection, "SELECT id FROM docs WHERE fts_match(title, body, 'fox') ORDER BY id;")
                .Should().Equal(1, 3);
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [Test]
    public void VacuumPreservesTheMethodIndex()
    {
        var path = CreateDatabasePath("managed-index-method-durability");
        try
        {
            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                SeedCorpus(connection);
                Execute(connection, "DELETE FROM docs WHERE id = 4;");
                Execute(connection, "VACUUM;");
                QueryIntegers(connection, "SELECT id FROM docs WHERE fts_match(title, body, 'fox') ORDER BY id;")
                    .Should().Equal(1, 3);
            }

            using var reopened = EmbeddedDatabase.OpenFile(path);
            using var reopenedConnection = reopened.Connect();
            QueryIntegers(reopenedConnection, "SELECT id FROM docs WHERE fts_match(title, body, 'fox') ORDER BY id;")
                .Should().Equal(1, 3);
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [Test]
    public void WalModeCommitsAreVisibleAfterReopen()
    {
        var path = CreateDatabasePath("managed-index-method-durability");
        try
        {
            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                Execute(connection, "PRAGMA journal_mode = WAL;");
                SeedCorpus(connection);
                Execute(connection, "INSERT INTO docs(id, title, body) VALUES (8, 'wal', 'walrus in the wal');");
            }

            using var reopened = EmbeddedDatabase.OpenFile(path);
            using var reopenedConnection = reopened.Connect();
            QueryIntegers(reopenedConnection, "SELECT id FROM docs WHERE fts_match(title, body, 'walrus');")
                .Should().Equal(8);
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [Test]
    public void AttachedDatabasesKeepTheirOwnMethodIndexes()
    {
        var main = CreateDatabasePath("managed-index-method-durability");
        var secondary = CreateDatabasePath("managed-index-method-durability");
        try
        {
            using (var database = EmbeddedDatabase.OpenFile(secondary))
            using (var connection = database.Connect())
            {
                SeedCorpus(connection);
            }

            using var mainDatabase = EmbeddedDatabase.OpenFile(main);
            using var mainConnection = mainDatabase.Connect();
            Execute(mainConnection, "CREATE TABLE local(id INTEGER PRIMARY KEY, body TEXT);");
            Execute(mainConnection, $"ATTACH DATABASE '{secondary.Replace("'", "''")}' AS other;");

            QueryIntegers(mainConnection, "SELECT count(*) FROM other.docs;").Should().Equal(4);
            QueryIntegers(
                    mainConnection,
                    "SELECT id FROM other.docs WHERE fts_match(title, body, 'fox') ORDER BY id;")
                .Should().Equal(1, 3);
        }
        finally
        {
            DeleteDatabase(main);
            DeleteDatabase(secondary);
        }
    }

    [Test]
    public void StateEnvelopeRoundTripsAndRejectsCorruptOrNewerVersions()
    {
        var declaration = "CREATE INDEX \"i\" ON \"t\" USING fts (\"body\")";
        var encoded = ManagedIndexMethodStateSql.Append(declaration, 1, [1, 2, 3]);

        ManagedIndexMethodStateSql.HasStateMarker(encoded).Should().BeTrue();
        var (parsed, version, state) = ManagedIndexMethodStateSql.Split(encoded);
        parsed.Should().Be(declaration);
        version.Should().Be(1);
        state.Should().Equal([1, 2, 3]);

        ManagedIndexMethodStateSql.HasStateMarker(declaration).Should().BeFalse();
        ManagedIndexMethodStateSql.Split(declaration).DeclarationSql.Should().Be(declaration);

        var badBase64 = declaration + " /*ahtola-index-method:1:not base64!*/";
        var badBase64Act = () => ManagedIndexMethodStateSql.Split(badBase64);
        badBase64Act.Should().Throw<EmbeddedSqlException>()
            .WithMessage("*not valid base64*");

        var badVersion = declaration + " /*ahtola-index-method:0:*/";
        var badVersionAct = () => ManagedIndexMethodStateSql.Split(badVersion);
        badVersionAct.Should().Throw<EmbeddedSqlException>()
            .WithMessage("*state version is invalid*");
    }

    [Test]
    public void NewerStateVersionFailsClosed()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, body TEXT);");
        Execute(connection, "CREATE INDEX t_fts ON t USING fts (body);");

        var configuration = new ManagedIndexMethodConfiguration(
            "t",
            "t_fts",
            [new ManagedIndexMethodColumn("body", 1)],
            []);
        var attachment = ManagedIndexMethodRegistry.Resolve("fts").Attach(configuration);

        var act = () => attachment.LoadState(int.MaxValue, attachment.SaveState());
        act.Should().Throw<EmbeddedSqlException>()
            .WithMessage("*was written by a newer managed index method*");
    }

    [Test]
    public void MalformedStateFailsClosed()
    {
        var configuration = new ManagedIndexMethodConfiguration(
            "t",
            "t_fts",
            [new ManagedIndexMethodColumn("body", 1)],
            []);
        var attachment = ManagedIndexMethodRegistry.Resolve("fts").Attach(configuration);

        var truncated = () => attachment.LoadState(1, new byte[3]);
        truncated.Should().Throw<EmbeddedSqlException>().WithMessage("*truncated state*");

        var wrongColumns = attachment.SaveState();
        wrongColumns[0] = 9;
        var mismatched = () => attachment.LoadState(ManagedFtsIndexMethod.StateVersion, wrongColumns);
        mismatched.Should().Throw<EmbeddedSqlException>().WithMessage("*state declares 9 columns*");
    }

    [Test]
    public void MissingStateRebuildsSilently()
    {
        var configuration = new ManagedIndexMethodConfiguration(
            "t",
            "t_fts",
            [new ManagedIndexMethodColumn("body", 1)],
            []);
        var attachment = ManagedIndexMethodRegistry.Resolve("fts").Attach(configuration);

        var act = () => attachment.LoadState(0, []);
        act.Should().NotThrow();
    }

    [Test]
    public void UnknownMethodInAStoredCatalogFailsClosedAtOpen()
    {
        var path = CreateDatabasePath("managed-index-method-durability");
        try
        {
            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                SeedCorpus(connection);
            }

            RewriteStoredIndexMethod(path, "USING fts ", "USING zzz ");

            var act = () =>
            {
                using var reopened = EmbeddedDatabase.OpenFile(path);
                using var connection = reopened.Connect();
                Execute(connection, "SELECT 1;");
            };
            act.Should().Throw<EmbeddedSqlException>().WithMessage("*no such index method: zzz*");
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    /// <summary>
    /// Rewrites one occurrence of a token inside the stored <c>sqlite_schema</c> text. The
    /// replacement must be the same length so no page layout changes.
    /// </summary>
    private static void RewriteStoredIndexMethod(string path, string search, string replacement)
    {
        replacement.Length.Should().Be(search.Length);
        var bytes = File.ReadAllBytes(path);
        var searchBytes = System.Text.Encoding.UTF8.GetBytes(search);
        var replacementBytes = System.Text.Encoding.UTF8.GetBytes(replacement);
        for (var offset = 0; offset + searchBytes.Length <= bytes.Length; offset++)
        {
            var matched = true;
            for (var index = 0; index < searchBytes.Length; index++)
            {
                if (bytes[offset + index] != searchBytes[index])
                {
                    matched = false;
                    break;
                }
            }

            if (!matched)
                continue;

            replacementBytes.CopyTo(bytes, offset);
            File.WriteAllBytes(path, bytes);
            return;
        }

        Assert.Fail($"The stored catalog does not contain '{search}'.");
    }
}
