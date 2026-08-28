using System.Text.RegularExpressions;

namespace Ahtola.Tests.Oracle;

internal static partial class DependencyAwareTraceMinimizer
{
    public static IReadOnlyList<ReplayOperation> Minimize(
        IReadOnlyList<ReplayOperation> operations,
        Func<IReadOnlyList<ReplayOperation>, string?> classifyFailure)
    {
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(classifyFailure);
        ValidateDependencies(operations);

        var expectedFingerprint = classifyFailure(operations)
            ?? throw new ArgumentException("The original replay trace does not fail.", nameof(operations));
        var current = operations.ToList();
        var granularity = 2;

        while (current.Count > 1)
        {
            var chunkSize = (int)Math.Ceiling(current.Count / (double)granularity);
            var reduced = false;
            for (var start = 0; start < current.Count; start += chunkSize)
            {
                var removedIds = current
                    .Skip(start)
                    .Take(chunkSize)
                    .Select(static operation => operation.Index)
                    .ToHashSet();
                var candidate = RemoveOperationsAndDependents(current, removedIds);
                if (candidate.Count == current.Count
                    || classifyFailure(candidate) != expectedFingerprint)
                {
                    continue;
                }

                current = candidate;
                granularity = Math.Max(2, granularity - 1);
                reduced = true;
                break;
            }

            if (reduced)
                continue;
            if (granularity >= current.Count)
                break;
            granularity = Math.Min(current.Count, granularity * 2);
        }

        ValidateDependencies(current);
        return current;
    }

    public static string NormalizeFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var firstLine = exception.Message.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault() ?? string.Empty;
        var normalized = GuidPattern().Replace(firstLine, "<guid>");
        normalized = HexPattern().Replace(normalized, "0x<number>");
        normalized = IntegerPattern().Replace(normalized, "<number>");
        return $"{exception.GetType().FullName}:{normalized}";
    }

    internal static void ValidateDependencies(IReadOnlyList<ReplayOperation> operations)
    {
        var seen = new HashSet<int>();
        foreach (var operation in operations)
        {
            if (!seen.Add(operation.Index))
                throw new InvalidOperationException($"Replay operation id {operation.Index} is duplicated.");
            foreach (var dependency in operation.Dependencies ?? [])
            {
                if (!seen.Contains(dependency))
                {
                    throw new InvalidOperationException(
                        $"Replay operation {operation.Index} depends on missing or later operation {dependency}.");
                }
            }
        }
    }

    private static List<ReplayOperation> RemoveOperationsAndDependents(
        IReadOnlyList<ReplayOperation> operations,
        HashSet<int> removedIds)
    {
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var operation in operations)
            {
                if (removedIds.Contains(operation.Index)
                    || !(operation.Dependencies?.Any(removedIds.Contains) ?? false))
                {
                    continue;
                }

                changed = removedIds.Add(operation.Index) || changed;
            }
        }

        return operations.Where(operation => !removedIds.Contains(operation.Index)).ToList();
    }

    [GeneratedRegex(@"(?i)\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b")]
    private static partial Regex GuidPattern();

    [GeneratedRegex(@"(?i)\b0x[0-9a-f]+\b")]
    private static partial Regex HexPattern();

    [GeneratedRegex(@"(?<![A-Za-z_])\d+(?![A-Za-z_])")]
    private static partial Regex IntegerPattern();
}
