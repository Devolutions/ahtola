using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;

namespace Ahtola.Data.Sqlite.Browser;

/// <summary>Initializes and inspects the browser runtime used by Ahtola OPFS storage.</summary>
[SupportedOSPlatform("browser")]
public static class AhtolaBrowserRuntime
{
    private const string ModuleName = "Devolutions.Ahtola.Data.Sqlite.Browser";
    private const string ModuleUrl =
        "../_content/Devolutions.Ahtola.Data.Sqlite.Browser/ahtola-opfs.mjs";
    private static readonly object Gate = new();
    private static Task? s_moduleInitialization;

    /// <summary>Loads the packaged JavaScript module once for the current browser runtime.</summary>
    public static async Task InitializeAsync()
    {
        if (!OperatingSystem.IsBrowser())
        {
            throw new PlatformNotSupportedException(
                "Ahtola browser storage is supported only by a .NET browser WebAssembly runtime.");
        }

        Task initialization;
        lock (Gate)
            initialization = s_moduleInitialization ??= JSHost.ImportAsync(ModuleName, ModuleUrl);

        try
        {
            await initialization.ConfigureAwait(false);
        }
        catch
        {
            lock (Gate)
            {
                if (ReferenceEquals(s_moduleInitialization, initialization))
                    s_moduleInitialization = null;
            }
            throw;
        }
    }

    /// <summary>Returns the browser features available to the OPFS backend.</summary>
    public static async ValueTask<AhtolaBrowserCapabilities> GetCapabilitiesAsync()
    {
        await InitializeAsync().ConfigureAwait(false);
        var mask = BrowserInterop.GetCapabilityMask();
        return new AhtolaBrowserCapabilities(
            IsCrossOriginIsolated: (mask & BrowserInterop.CrossOriginIsolated) != 0,
            HasSharedArrayBuffer: (mask & BrowserInterop.SharedArrayBuffer) != 0,
            HasOriginPrivateFileSystem: (mask & BrowserInterop.OriginPrivateFileSystem) != 0,
            HasSynchronousAccessHandles: (mask & BrowserInterop.SynchronousAccessHandle) != 0,
            HasWebLocks: (mask & BrowserInterop.WebLocks) != 0);
    }
}

[SupportedOSPlatform("browser")]
internal static partial class BrowserInterop
{
    internal const int CrossOriginIsolated = 1 << 0;
    internal const int SharedArrayBuffer = 1 << 1;
    internal const int OriginPrivateFileSystem = 1 << 2;
    internal const int SynchronousAccessHandle = 1 << 3;
    internal const int WebLocks = 1 << 4;
    private const string ModuleName = "Devolutions.Ahtola.Data.Sqlite.Browser";

    [JSImport("getCapabilityMask", ModuleName)]
    internal static partial int GetCapabilityMask();

    [JSImport("createContext", ModuleName)]
    internal static partial Task<int> CreateContextAsync(string lockName, int sharedBufferSize);

    [JSImport("disposeContext", ModuleName)]
    internal static partial Task DisposeContextAsync(int contextId);

    [JSImport("fileExists", ModuleName)]
    internal static partial Task<bool> FileExistsAsync(int contextId, string path);

    [JSImport("openFile", ModuleName)]
    internal static partial Task<int> OpenFileAsync(
        int contextId,
        string path,
        int mode,
        bool readOnly);

    [JSImport("getLength", ModuleName)]
    internal static partial Task<double> GetLengthAsync(int contextId, int handleId);

    [JSImport("readFile", ModuleName)]
    internal static partial Task<int> ReadFileAsync(
        int contextId,
        int handleId,
        double position,
        int length);

    [JSImport("copyFromSharedBuffer", ModuleName)]
    internal static partial int CopyFromSharedBuffer(
        int contextId,
        [JSMarshalAs<JSType.MemoryView>] ArraySegment<byte> destination,
        int length);

    [JSImport("copyToSharedBuffer", ModuleName)]
    internal static partial int CopyToSharedBuffer(
        int contextId,
        [JSMarshalAs<JSType.MemoryView>] ArraySegment<byte> source);

    [JSImport("writeFile", ModuleName)]
    internal static partial Task<int> WriteFileAsync(
        int contextId,
        int handleId,
        double position,
        int length);

    [JSImport("setLength", ModuleName)]
    internal static partial Task SetLengthAsync(int contextId, int handleId, double length);

    [JSImport("flushFile", ModuleName)]
    internal static partial Task FlushFileAsync(int contextId, int handleId);

    [JSImport("closeFile", ModuleName)]
    internal static partial Task CloseFileAsync(int contextId, int handleId);

    [JSImport("deleteFile", ModuleName)]
    internal static partial Task DeleteFileAsync(int contextId, string path);

    [JSImport("listFiles", ModuleName)]
    internal static partial Task<string> ListFilesAsync(int contextId, string directoryPath);

    [JSImport("replaceFileAtomically", ModuleName)]
    internal static partial Task ReplaceFileAtomicallyAsync(
        int contextId,
        int operationId,
        string sourcePath,
        string destinationPath,
        bool replaceEmptyDestination);

    [JSImport("allocateOperationId", ModuleName)]
    internal static partial int AllocateOperationId(int contextId);

    [JSImport("cancelOperation", ModuleName)]
    internal static partial void CancelOperation(int contextId, int operationId);
}
