namespace Ahtola.Core.Execution;

/// <summary>
/// Caches <see cref="Path.GetTempPath"/> for the engine's per-statement spill directory.
/// </summary>
/// <remarks>
/// On Windows <c>TEMP</c>/<c>TMP</c> commonly hold the 8.3 short form of the profile path
/// (<c>C:\Users\ABCDEF~1\...</c>), and <see cref="Path.GetTempPath"/> expands it through the
/// file system on every call, costing milliseconds. The cache is keyed on the raw environment
/// values the resolution depends on, so a process that changes them still observes the change.
/// </remarks>
internal static class TemporaryDirectory
{
    private static Entry? s_cached;

    public static string Get()
    {
        var key = ReadEnvironmentKey();
        var cached = Volatile.Read(ref s_cached);
        if (cached is not null && string.Equals(cached.Key, key, StringComparison.Ordinal))
            return cached.Path;

        var path = Path.GetTempPath();
        Volatile.Write(ref s_cached, new Entry(key, path));
        return path;
    }

    private static string ReadEnvironmentKey()
        => OperatingSystem.IsWindows()
            ? string.Concat(
                Environment.GetEnvironmentVariable("TMP"),
                "\0",
                Environment.GetEnvironmentVariable("TEMP"),
                "\0",
                Environment.GetEnvironmentVariable("USERPROFILE"))
            : Environment.GetEnvironmentVariable("TMPDIR") ?? string.Empty;

    private sealed record Entry(string Key, string Path);
}
