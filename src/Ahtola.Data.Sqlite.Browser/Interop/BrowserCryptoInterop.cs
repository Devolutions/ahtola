using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;

namespace Ahtola.Data.Sqlite.Browser.Interop;

[SupportedOSPlatform("browser")]
internal static class AhtolaBrowserCryptoRuntime
{
    private const string ModuleName = "Devolutions.Ahtola.Data.Sqlite.Browser.Crypto";
    private const string ModuleUrl =
        "../_content/Devolutions.Ahtola.Data.Sqlite.Browser/ahtola-crypto.mjs";
    private static readonly object Gate = new();
    private static Task? s_moduleInitialization;

    internal static Task InitializeAsync()
    {
        if (!OperatingSystem.IsBrowser())
        {
            throw new PlatformNotSupportedException(
                "Ahtola browser cryptography is supported only by a .NET browser WebAssembly runtime.");
        }

        lock (Gate)
            return s_moduleInitialization ??= JSHost.ImportAsync(ModuleName, ModuleUrl);
    }
}

[SupportedOSPlatform("browser")]
internal static partial class BrowserCryptoInterop
{
    private const string ModuleName = "Devolutions.Ahtola.Data.Sqlite.Browser.Crypto";

    [JSImport("createPasswordKey", ModuleName)]
    internal static partial Task<int> CreatePasswordKeyAsync(
        string password,
        string salt,
        int iterations,
        int keyLengthBits);

    [JSImport("derivePasswordBits", ModuleName)]
    [return: JSMarshalAs<JSType.Promise<JSType.Object>>]
    internal static partial Task<JSObject> DerivePasswordBitsAsync(
        string password,
        string salt,
        int iterations,
        int outputLengthBits);

    [JSImport("importAesGcmKey", ModuleName)]
    internal static partial Task<int> ImportAesGcmKeyAsync(byte[] key);

    [JSImport("encryptAesGcm", ModuleName)]
    [return: JSMarshalAs<JSType.Promise<JSType.Object>>]
    internal static partial Task<JSObject> EncryptAesGcmAsync(
        int keyHandle,
        byte[] nonce,
        byte[] plaintext,
        byte[] associatedData);

    [JSImport("decryptAesGcm", ModuleName)]
    [return: JSMarshalAs<JSType.Promise<JSType.Object>>]
    internal static partial Task<JSObject> DecryptAesGcmAsync(
        int keyHandle,
        byte[] nonce,
        byte[] ciphertext,
        byte[] tag,
        byte[] associatedData);

    [JSImport("consumeByteArray", ModuleName)]
    [return: JSMarshalAs<JSType.Array<JSType.Number>>]
    internal static partial byte[] ConsumeByteArray(JSObject value);

    [JSImport("releaseKey", ModuleName)]
    internal static partial void ReleaseKey(int keyHandle);

    [JSImport("getRetainedKeyCount", ModuleName)]
    internal static partial int GetRetainedKeyCount();
}
