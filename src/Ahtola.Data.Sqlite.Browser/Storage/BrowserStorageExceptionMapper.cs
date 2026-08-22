using System.Runtime.InteropServices.JavaScript;

namespace Ahtola.Data.Sqlite.Browser.Storage;

internal static class BrowserStorageExceptionMapper
{
    public static Exception Map(JSException exception, string path, string operation)
    {
        if (HasName(exception, "NotFoundError"))
            return new FileNotFoundException($"OPFS could not {operation} '{path}' because it does not exist.", path, exception);
        if (HasName(exception, "QuotaExceededError"))
            return new IOException($"OPFS quota was exceeded while trying to {operation} '{path}'.", exception);
        if (HasName(exception, "NoModificationAllowedError"))
            return new UnauthorizedAccessException($"OPFS denied {operation} access to '{path}'.", exception);
        if (HasName(exception, "InvalidModificationError"))
            return new IOException($"OPFS could not {operation} '{path}' because the destination already exists.", exception);
        if (HasName(exception, "InvalidStateError"))
            return new IOException($"OPFS could not {operation} '{path}' because its handle is not in a valid state.", exception);

        return new IOException($"OPFS failed to {operation} '{path}': {exception.Message}", exception);
    }

    private static bool HasName(JSException exception, string name)
        => exception.Message.Contains(name, StringComparison.Ordinal);
}
