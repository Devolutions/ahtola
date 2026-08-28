using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Ahtola.Tests.Infrastructure;

internal sealed record ProcessLifecycleEvent(
    long Sequence,
    string Kind,
    string Actor,
    string? Operation,
    int? ExitCode,
    string? Detail);

/// <summary>
/// JSONL lifecycle trace for child-process tests. Sequence numbers are logical timestamps so
/// traces remain comparable without depending on wall-clock scheduling.
/// </summary>
internal sealed class ProcessLifecycleTrace
{
    private readonly object _gate = new();
    private readonly Dictionary<string, int> _starts = new(StringComparer.Ordinal);
    private readonly List<ProcessLifecycleEvent> _events = [];
    private long _sequence;

    internal ProcessLifecycleTrace(string path)
    {
        Path = path;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
    }

    internal string Path { get; }

    internal IReadOnlyList<ProcessLifecycleEvent> Events
    {
        get
        {
            lock (_gate)
                return _events.ToArray();
        }
    }

    internal void RecordStart(string actor, string operation)
    {
        lock (_gate)
        {
            if (_starts.TryGetValue(actor, out var starts) && starts > 0)
                AppendLocked("restart", actor, operation, null, $"attempt={starts + 1}");

            _starts[actor] = starts + 1;
            AppendLocked("start", actor, operation, null, null);
        }
    }

    internal void RecordOperation(string actor, string operation, string? detail = null)
        => Append("operation", actor, operation, null, detail);

    internal void RecordExit(string actor, string operation, int exitCode)
        => Append("exit", actor, operation, exitCode, null);

    internal void RecordTimeout(string actor, string operation, string? detail = null)
        => Append("timeout", actor, operation, null, detail);

    internal void WaitForExit(
        Process process,
        TimeSpan timeout,
        string actor,
        string operation,
        Func<string>? output = null)
    {
        RecordOperation(actor, "wait-for-exit", operation);
        if (!process.WaitForExit(timeout))
        {
            var detail = output?.Invoke();
            RecordTimeout(actor, operation, detail);
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            process.WaitForExit();
            throw new TimeoutException(
                $"Process '{actor}' timed out during '{operation}'.{Environment.NewLine}{ReplayDiagnostics()}");
        }

        process.WaitForExit();
        RecordExit(actor, operation, process.ExitCode);
    }

    internal void WaitUntil(
        Func<bool> predicate,
        TimeSpan timeout,
        string actor,
        string operation,
        Process? process = null,
        Func<string>? output = null)
    {
        RecordOperation(actor, operation);
        var stopwatch = Stopwatch.StartNew();
        while (!predicate())
        {
            if (process?.HasExited == true)
            {
                process.WaitForExit();
                RecordExit(actor, operation, process.ExitCode);
                throw new InvalidOperationException(
                    $"Process '{actor}' exited before '{operation}'.{Environment.NewLine}{ReplayDiagnostics()}"
                    + Environment.NewLine
                    + output?.Invoke());
            }

            if (stopwatch.Elapsed >= timeout)
            {
                var detail = output?.Invoke();
                RecordTimeout(actor, operation, detail);
                if (process is { HasExited: false })
                    process.Kill(entireProcessTree: true);
                throw new TimeoutException(
                    $"Process '{actor}' timed out during '{operation}'.{Environment.NewLine}{ReplayDiagnostics()}");
            }

            Thread.Sleep(TimeSpan.FromMilliseconds(10));
        }
    }

    internal string ReplayDiagnostics()
    {
        lock (_gate)
        {
            var result = new StringBuilder($"Replay trace: {Path}");
            foreach (var item in _events)
                result.AppendLine().Append(JsonSerializer.Serialize(item));
            return result.ToString();
        }
    }

    private void Append(string kind, string actor, string? operation, int? exitCode, string? detail)
    {
        lock (_gate)
            AppendLocked(kind, actor, operation, exitCode, detail);
    }

    private void AppendLocked(string kind, string actor, string? operation, int? exitCode, string? detail)
    {
        var item = new ProcessLifecycleEvent(++_sequence, kind, actor, operation, exitCode, detail);
        _events.Add(item);
        File.AppendAllText(Path, JsonSerializer.Serialize(item) + Environment.NewLine, Encoding.UTF8);
    }
}
