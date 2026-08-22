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

    /// <summary>
    /// Names of the required browser features that are not available. Used
    /// to produce a precise diagnostic instead of a single all-or-nothing
    /// message, so a caller (including CI tooling) can distinguish "this
    /// browser truly lacks OPFS" from a broader, unexpected regression.
    /// </summary>
    public IReadOnlyList<string> MissingCapabilities
    {
        get
        {
            var missing = new List<string>();
            if (!IsCrossOriginIsolated)
                missing.Add("cross-origin isolation");
            if (!HasSharedArrayBuffer)
                missing.Add("SharedArrayBuffer");
            if (!HasOriginPrivateFileSystem)
                missing.Add("Origin Private File System");
            if (!HasSynchronousAccessHandles)
                missing.Add("Origin Private File System synchronous access handles");
            if (!HasWebLocks)
                missing.Add("Web Locks");
            return missing;
        }
    }
}
