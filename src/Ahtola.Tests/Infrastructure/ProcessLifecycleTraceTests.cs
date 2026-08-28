using System.Text.Json;
using AwesomeAssertions;

namespace Ahtola.Tests.Infrastructure;

public sealed class ProcessLifecycleTraceTests
{
    [Test]
    public void JsonlTraceUsesDeterministicSequencesAndRecordsRestarts()
    {
        var root = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "process-lifecycle-trace",
            Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "trace.jsonl");

        try
        {
            var trace = new ProcessLifecycleTrace(path);
            trace.RecordStart("worker", "write");
            trace.RecordOperation("worker", "publish");
            trace.RecordExit("worker", "write", 0);
            trace.RecordStart("worker", "recover");

            var events = File.ReadLines(path)
                .Select(line => JsonSerializer.Deserialize<ProcessLifecycleEvent>(line))
                .ToArray();

            events.Should().NotContainNulls();
            events.Select(item => item!.Sequence).Should().Equal(1, 2, 3, 4, 5);
            events.Select(item => item!.Kind).Should().Equal(
                "start",
                "operation",
                "exit",
                "restart",
                "start");
            trace.ReplayDiagnostics().Should().Contain("\"Kind\":\"restart\"");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void BoundedWaitRecordsTimeoutAndIncludesReplayDiagnostics()
    {
        var root = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "process-lifecycle-trace",
            Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "trace.jsonl");

        try
        {
            var trace = new ProcessLifecycleTrace(path);
            trace.RecordStart("worker", "wait");

            Action wait = () => trace.WaitUntil(
                () => false,
                TimeSpan.FromMilliseconds(20),
                "worker",
                "never-signaled");

            wait.Should().Throw<TimeoutException>()
                .WithMessage("*process-lifecycle*\"Kind\":\"timeout\"*");
            trace.Events.Select(item => item.Kind).Should().ContainInOrder("start", "operation", "timeout");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
