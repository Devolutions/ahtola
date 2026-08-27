using Ahtola.EntityFrameworkCore.Sqlite;
using Microsoft.EntityFrameworkCore;

var options = new DbContextOptionsBuilder<ProbeContext>()
    .UseAhtola("Data Source=:memory:;Mode=Memory;Local Provider=Managed")
    .Options;

await using var context = new ProbeContext(options);
await context.Database.EnsureCreatedAsync();
context.Rows.Add(new ProbeRow { Id = 1, Value = 42 });
await context.SaveChangesAsync();

var value = await context.Rows
    .Where(static row => row.Id == 1)
    .Select(static row => row.Value)
    .SingleAsync();
if (value != 42) {
    throw new InvalidOperationException($"EF browser probe returned {value} instead of 42.");
}

internal sealed class ProbeContext(DbContextOptions<ProbeContext> options) : DbContext(options)
{
    internal DbSet<ProbeRow> Rows => Set<ProbeRow>();
}

internal sealed class ProbeRow
{
    public int Id { get; set; }

    public long Value { get; set; }
}
