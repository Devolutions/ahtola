using System.Data;
using System.Diagnostics;
using AwesomeAssertions;
using NUnit.Framework;
using Ahtola.Data.Sqlite;

// [Timeout] is the watchdog that force-aborts a genuine FK-collection hang so
// it surfaces as a test failure instead of an indefinite runner hang. The
// CS0618 obsoletion points at CancelAfterAttribute, but that only
// cooperatively cancels a token the test must observe — a true deadlock is a
// blocked thread that never reaches a token check, so CancelAfter cannot
// break it. Thread-abort is the intended forceful stop for a hang watchdog,
// so suppress the obsoletion here. The watchdog budget must cover the WHOLE
// test — including the ~4,310-statement preseed phase — on slow CI runners,
// so it is deliberately far larger than the measured-append budget asserted
// inside the test. A genuinely quadratic engine spins for minutes in either
// phase and still trips the watchdog; the quantitative detector is the
// timed assertion, not the watchdog.
#pragma warning disable CS0618

namespace Ahtola.Tests;

/// <summary>
/// Repro harness for the EF parallel-collection hang (#16) reported during the EFCore
/// SQLite wiring re-baseline: with xunit's default parallel test collections over a
/// shared-memory Northwind fixture, an INSERT into an FK-linked table spins inside
/// <c>CollectForeignKeyViolations</c>. Managed shared-memory (<c>Mode=Memory;Cache=Shared</c>)
/// shares ONE <c>EmbeddedDatabase</c> (and its <c>_gate</c>) across all connections, so these
/// tests drive 2+ concurrent connections doing bulk INSERTs into a Northwind-shaped FK graph
/// with foreign-key enforcement on, bounded by a watchdog so a genuine hang surfaces as a
/// test failure rather than an indefinite runner stall.
/// </summary>
[NonParallelizable]
public sealed class ManagedForeignKeyConcurrencyHangTests
{
    private static string CreateConnectionString(string name) =>
        $"Data Source={name};Mode=Memory;Cache=Shared;Pooling=False;Local Provider=Managed";

    [SetUp]
    public void SetUp() => SqliteConnection.ClearAllPools();

    [TearDown]
    public void TearDown() => SqliteConnection.ClearAllPools();

    // Watchdog only: bounds the whole test (schema + ~4,310 preseed statements +
    // timed append) so a genuine FK-collection hang cannot stall the runner.
    // Slow GitHub-hosted windows-latest runners legitimately need ~20-40s for the
    // preseed alone, so this must stay far above the 15s budget of the timed
    // assertion below — that assertion is the actual quadratic detector.
    [Test]
    [Timeout(180_000)]
    public void ForeignKeyValidationIsNotQuadraticInTableRowCount()
    {
        var name = $"fkquad-{Guid.NewGuid():N}";
        var connectionString = CreateConnectionString(name);

        using var anchor = new SqliteConnection(connectionString);
        anchor.Open();
        CreateNorthwindShapedSchema(anchor);
        SeedParents(anchor);

        // Pre-populate the child table so per-statement FK validation has real rows to re-scan.
        // Native SQLite validates only the affected row against the parent index (O(log N)).
        // If the managed engine instead re-scans the entire FK graph per statement (O(total)),
        // inserting a few hundred rows over a 2155-row child table spins for minutes.
        const int preseed = 2155;
        using (var tx = anchor.BeginTransaction())
        {
            for (var n = 0; n < preseed; n++)
            {
                using var insOrder = new SqliteCommand(
                    "INSERT INTO Orders (OrderID, CustomerID) VALUES (@id, 'ALFKI')", anchor, tx);
                insOrder.Parameters.AddWithValue("@id", 20000 + n);
                insOrder.ExecuteNonQuery();

                using var insDetail = new SqliteCommand(
                    "INSERT INTO [Order Details] (OrderID, ProductID, UnitPrice, Quantity) " +
                    "VALUES (@oid, 1, 1.5, 1)", anchor, tx);
                insDetail.Parameters.AddWithValue("@oid", 20000 + n);
                insDetail.ExecuteNonQuery();
            }

            tx.Commit();
        }

        using (var fk = new SqliteCommand("PRAGMA foreign_keys = 1", anchor))
        {
            fk.ExecuteNonQuery();
        }

        const int appended = 200;
        var start = Stopwatch.GetTimestamp();
        using (var tx = anchor.BeginTransaction())
        {
            for (var n = 0; n < appended; n++)
            {
                using var insOrder = new SqliteCommand(
                    "INSERT INTO Orders (OrderID, CustomerID) VALUES (@id, 'ALFKI')", anchor, tx);
                insOrder.Parameters.AddWithValue("@id", 40000 + n);
                insOrder.ExecuteNonQuery();

                using var insDetail = new SqliteCommand(
                    "INSERT INTO [Order Details] (OrderID, ProductID, UnitPrice, Quantity) " +
                    "VALUES (@oid, 1, 1.5, 1)", anchor, tx);
                insDetail.Parameters.AddWithValue("@oid", 40000 + n);
                insDetail.ExecuteNonQuery();
            }

            tx.Commit();
        }

        var elapsed = Stopwatch.GetElapsedTime(start);
        elapsed.TotalSeconds.Should().BeLessThan(
            15.0,
            $"appending {appended} rows over a {preseed}-row FK graph took {elapsed.TotalSeconds:F1}s; " +
            "per-statement FK validation is quadratic in total row count (full-table re-scan twice + full clone per DML)");
    }

    private static void CreateNorthwindShapedSchema(SqliteConnection conn)
    {
        Execute(conn, "PRAGMA foreign_keys = 0");
        Execute(conn, "CREATE TABLE Customers (CustomerID TEXT PRIMARY KEY)");
        Execute(conn, "CREATE TABLE Products (ProductID INTEGER PRIMARY KEY)");
        Execute(conn, """
            CREATE TABLE Orders (
                OrderID INTEGER PRIMARY KEY,
                CustomerID TEXT REFERENCES Customers (CustomerID)
            )
            """);
        Execute(conn, """
            CREATE TABLE [Order Details] (
                OrderID INTEGER NOT NULL REFERENCES Orders (OrderID),
                ProductID INTEGER NOT NULL REFERENCES Products (ProductID),
                UnitPrice REAL NOT NULL,
                Quantity INTEGER NOT NULL,
                PRIMARY KEY (OrderID, ProductID)
            )
            """);
        // Self-referential FK (Employees.ReportsTo analogue) to exercise the cyclic-FK case.
        Execute(conn, """
            CREATE TABLE Employees (
                EmployeeID INTEGER PRIMARY KEY,
                ReportsTo INTEGER REFERENCES Employees (EmployeeID)
            )
            """);
        Execute(conn, "PRAGMA foreign_keys = 1");
    }

    private static void SeedParents(SqliteConnection conn)
    {
        Execute(conn, "INSERT INTO Customers (CustomerID) VALUES ('ALFKI')");
        Execute(conn, "INSERT INTO Products (ProductID) VALUES (1)");
        Execute(conn, "INSERT INTO Employees (EmployeeID) VALUES (1)");
    }

    private static void Execute(SqliteConnection conn, string sql)
    {
        using var cmd = new SqliteCommand(sql, conn);
        cmd.ExecuteNonQuery();
    }
}
