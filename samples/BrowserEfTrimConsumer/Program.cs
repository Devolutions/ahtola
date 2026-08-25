using Ahtola.EntityFrameworkCore.Sqlite;
using Microsoft.EntityFrameworkCore;

var options = new DbContextOptionsBuilder<ProbeContext>()
    .UseAhtola("Data Source=:memory:;Mode=Memory")
    .Options;

await using var context = new ProbeContext(options);
_ = context.Rows.Where(static row => row.Value > 0).Select(static row => row.Value);

internal sealed class ProbeContext(DbContextOptions<ProbeContext> options) : DbContext(options)
{
    internal DbSet<ProbeRow> Rows => Set<ProbeRow>();
}

internal sealed class ProbeRow
{
    public int Id { get; set; }

    public long Value { get; set; }
}
