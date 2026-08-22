namespace Ahtola.Data.Sqlite.Browser;

/// <summary>Browser features required by the Ahtola OPFS backend.</summary>
public readonly record struct AhtolaBrowserCapabilities(
    bool IsCrossOriginIsolated,
    bool HasSharedArrayBuffer,
    bool HasOriginPrivateFileSystem,
    bool HasSynchronousAccessHandles,
    bool HasWebLocks)
{
    /// <summary>Whether every required browser feature is available.</summary>
    public bool IsSupported =>
        IsCrossOriginIsolated
        && HasSharedArrayBuffer
        && HasOriginPrivateFileSystem
        && HasSynchronousAccessHandles
        && HasWebLocks;
}
