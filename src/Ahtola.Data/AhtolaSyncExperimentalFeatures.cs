namespace Ahtola;

/// <summary>
/// Maps Turso's experimental feature names (sdk-kit <c>TursoDatabaseConfig::database_opts</c>)
/// onto the managed engine used by an embedded replica.
/// </summary>
/// <remarks>
/// Turso ignores names it does not know. The managed replica rejects them instead, together
/// with features its engine cannot provide, so a connection string never appears to enable
/// something that has no effect.
/// </remarks>
internal static class AhtolaSyncExperimentalFeatures
{
    /// <summary>
    /// Features Turso gates behind a flag that the managed engine always provides.
    /// </summary>
    internal static IReadOnlyList<string> AlwaysEnabled { get; } =
    [
        "strict",
        "index_method",
        "vacuum",
        "encryption",
        "attach",
        "generated_columns",
        "without_rowid",
        "multiprocess_wal",
    ];

    /// <summary>
    /// Enables the engine's restricted TYPE/DOMAIN subset (STRICT INTEGER domains and identity
    /// INTEGER types); general typed values remain unsupported.
    /// </summary>
    internal const string CustomTypes = "custom_types";

    /// <summary>
    /// Turso feature names the managed engine does not implement: incremental materialized
    /// views (<c>views</c>), autovacuum, and the MVCC passive checkpoint.
    /// </summary>
    internal static IReadOnlyList<string> Unsupported { get; } =
    [
        "views",
        "autovacuum",
        "mvcc_passive_checkpoint",
    ];

    /// <summary>
    /// Validates <paramref name="features"/> for the managed replica and reports whether it
    /// enables <see cref="CustomTypes"/>.
    /// </summary>
    /// <exception cref="NotSupportedException">A name the managed engine does not implement.</exception>
    /// <exception cref="ArgumentException">A name Turso does not define.</exception>
    internal static bool ResolveManaged(string? features)
    {
        if (string.IsNullOrWhiteSpace(features))
            return false;

        var customTypes = false;
        foreach (var feature in features.Split(','))
        {
            var name = feature.Trim();
            if (name.Length == 0 || Contains(AlwaysEnabled, name))
                continue;
            if (name.Equals(CustomTypes, StringComparison.OrdinalIgnoreCase))
            {
                customTypes = true;
                continue;
            }
            if (Contains(Unsupported, name))
            {
                throw new NotSupportedException(
                    $"Sync Experimental Features '{name}' is not supported by the managed embedded replica.");
            }

            throw new ArgumentException(
                $"Sync Experimental Features has unknown feature '{name}'. Known features: "
                + string.Join(", ", AlwaysEnabled.Append(CustomTypes).Concat(Unsupported))
                + ".");
        }

        return customTypes;
    }

    private static bool Contains(IReadOnlyList<string> names, string name)
    {
        foreach (var candidate in names)
        {
            if (candidate.Equals(name, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
