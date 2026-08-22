namespace Ahtola.Core.Storage;

/// <summary>
/// A monitor-adjacent asynchronous condition signal. Callers capture and pulse
/// while holding their own state lock, then await the captured task after
/// leaving that lock.
/// </summary>
internal sealed class AsyncConditionPulse
{
    private TaskCompletionSource _source = CreateSource();

    internal Task Capture() => _source.Task;

    internal void PulseAll()
    {
        var source = _source;
        _source = CreateSource();
        source.TrySetResult();
    }

    private static TaskCompletionSource CreateSource()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
