using System.Security.Cryptography;
using Ahtola.Core.Storage;
using Ahtola.Data.Sqlite.Browser;
using Ahtola.Data.Sqlite.Browser.Storage;

#pragma warning disable CA1416

namespace Ahtola.Tests;

/// <summary>
/// A desktop AES-GCM implementation of the browser's asynchronous page cipher.
/// Web Crypto is unavailable off-browser, so this stands in for it and lets the
/// suite prove that the browser transforms produce byte-identical AHTLA output.
/// </summary>
internal sealed class DesktopAsyncPageCipher(Core.Storage.AhtolaEncryptionCipher cipher, byte[] key)
    : IAhtolaAsyncPageCipher
{
    private byte[]? _key = key.AsSpan().ToArray();

    public Core.Storage.AhtolaEncryptionCipher Cipher { get; } = cipher;

    public int EncryptCount { get; private set; }

    public int DecryptCount { get; private set; }

    public bool IsReleased => _key is null;

    public ValueTask<AhtolaBrowserAesGcmResult> EncryptAsync(
        ReadOnlyMemory<byte> plaintext,
        ReadOnlyMemory<byte> nonce,
        ReadOnlyMemory<byte> associatedData,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EncryptCount++;
        using var aes = new AesGcm(RequireKey(), 16);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        aes.Encrypt(nonce.Span, plaintext.Span, ciphertext, tag, associatedData.Span);
        return ValueTask.FromResult(new AhtolaBrowserAesGcmResult(ciphertext, tag));
    }

    public ValueTask<byte[]> DecryptAsync(
        ReadOnlyMemory<byte> ciphertext,
        ReadOnlyMemory<byte> tag,
        ReadOnlyMemory<byte> nonce,
        ReadOnlyMemory<byte> associatedData,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DecryptCount++;
        using var aes = new AesGcm(RequireKey(), 16);
        var plaintext = new byte[ciphertext.Length];
        aes.Decrypt(nonce.Span, ciphertext.Span, tag.Span, plaintext, associatedData.Span);
        return ValueTask.FromResult(plaintext);
    }

    public ValueTask DisposeAsync()
    {
        var released = Interlocked.Exchange(ref _key, null);
        if (released is not null)
            CryptographicOperations.ZeroMemory(released);
        return ValueTask.CompletedTask;
    }

    private byte[] RequireKey()
        => _key ?? throw new ObjectDisposedException(nameof(DesktopAsyncPageCipher));
}

/// <summary>
/// Builds a browser mirror for an arbitrary AHTLA cipher, routing exactly the way
/// <see cref="AhtolaBrowserPageCipherFactory"/> does inside the package: the
/// AES-GCM ciphers through the Web Crypto stand-in, every AEGIS variant through
/// the same pure-managed <see cref="AhtolaManagedAegisPageCipher"/> the browser
/// uses. Off-browser there is no SubtleCrypto, so the stand-in is the only part
/// that differs from production.
/// </summary>
internal sealed class BrowserCipherHarness : IAsyncDisposable
{
    private readonly BrowserEncryptedPersistence _persistence;

    private BrowserCipherHarness(BrowserMirroredFileSystem mirror, BrowserEncryptedPersistence persistence)
    {
        Mirror = mirror;
        _persistence = persistence;
    }

    public BrowserMirroredFileSystem Mirror { get; }

    public static async ValueTask<BrowserCipherHarness> CreateAsync(
        FakeBrowserPersistentStore store,
        Core.Storage.AhtolaEncryptionCipher cipher,
        string hexKey,
        string ownedDirectory)
    {
        var key = Convert.FromHexString(hexKey);
        IAhtolaAsyncPageCipher pageCipher =
            cipher is Core.Storage.AhtolaEncryptionCipher.Aes128Gcm
                or Core.Storage.AhtolaEncryptionCipher.Aes256Gcm
                ? new DesktopAsyncPageCipher(cipher, key)
                : new AhtolaManagedAegisPageCipher(cipher, key);
        CryptographicOperations.ZeroMemory(key);

        var persistence = new BrowserEncryptedPersistence(new AhtolaAsyncPageTransformer(pageCipher));
        try
        {
            var mirror = await BrowserMirroredFileSystem.CreateAsync(
                store,
                ownedDirectory,
                ownsPersistent: false,
                encryption: persistence);
            return new BrowserCipherHarness(mirror, persistence);
        }
        catch
        {
            await persistence.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Mirror.DisposeAsync();
        }
        finally
        {
            await _persistence.DisposeAsync();
        }
    }
}

/// <summary>An in-memory stand-in for the browser's OPFS-backed persistent store.</summary>
internal sealed class FakeBrowserPersistentStore : IBrowserPersistentStore
{
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);

    public int Disposals { get; private set; }

    public IReadOnlyCollection<string> Paths => _files.Keys;

    public List<string> FlushPaths { get; } = [];

    public byte[] Read(string path) => _files[path].AsSpan().ToArray();

    /// <summary>
    /// Optional gate invoked before every persisted write. A test can park a flush inside the
    /// store to observe the mirror while durable work is genuinely in flight.
    /// </summary>
    public Func<string, CancellationToken, ValueTask>? BeforeWrite { get; set; }

    public bool Contains(string path) => _files.ContainsKey(path);

    public void Seed(string path, byte[] content) => _files[path] = content.AsSpan().ToArray();

    public ValueTask<IReadOnlyList<string>> ListFilesAsync(
        string directory,
        CancellationToken cancellationToken = default)
    {
        var prefix = directory.TrimEnd('/') + "/";
        IReadOnlyList<string> matches = _files.Keys
            .Where(path => path.StartsWith(prefix, StringComparison.Ordinal))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        return ValueTask.FromResult(matches);
    }

    public ValueTask<IAsyncFile> OpenFileAsync(
        string path,
        FileOpenMode mode,
        bool readOnly = false,
        CancellationToken cancellationToken = default)
    {
        if (!_files.ContainsKey(path))
        {
            if (mode == FileOpenMode.OpenExisting)
                throw new FileNotFoundException($"Persisted file '{path}' does not exist.", path);
            _files[path] = [];
        }

        return ValueTask.FromResult<IAsyncFile>(new FakeFile(this, path, readOnly));
    }

    public ValueTask DeleteFileAsync(string path, CancellationToken cancellationToken = default)
    {
        _files.Remove(path);
        return ValueTask.CompletedTask;
    }

    public ValueTask ReplaceFileAtomicallyAsync(
        string sourcePath,
        string destinationPath,
        bool replaceEmptyDestination,
        CancellationToken cancellationToken = default)
    {
        if (!_files.Remove(sourcePath, out var content))
            throw new FileNotFoundException($"Persisted file '{sourcePath}' does not exist.", sourcePath);
        _files[destinationPath] = content;
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Disposals++;
        return ValueTask.CompletedTask;
    }

    private sealed class FakeFile(FakeBrowserPersistentStore owner, string path, bool readOnly) : IAsyncFile
    {
        public bool IsReadOnly => readOnly;

        public ValueTask<long> GetLengthAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult((long)owner._files[path].Length);

        public ValueTask<int> ReadAsync(
            long position,
            Memory<byte> destination,
            CancellationToken cancellationToken = default)
        {
            var content = owner._files[path];
            if (position >= content.Length)
                return ValueTask.FromResult(0);
            var count = (int)Math.Min(destination.Length, content.Length - position);
            content.AsSpan((int)position, count).CopyTo(destination.Span);
            return ValueTask.FromResult(count);
        }

        public ValueTask WriteAsync(
            long position,
            ReadOnlyMemory<byte> source,
            CancellationToken cancellationToken = default)
        {
            if (owner.BeforeWrite is { } gate)
                return WriteGatedAsync(gate, position, source, cancellationToken);

            WriteCore(position, source);
            return ValueTask.CompletedTask;
        }

        private async ValueTask WriteGatedAsync(
            Func<string, CancellationToken, ValueTask> gate,
            long position,
            ReadOnlyMemory<byte> source,
            CancellationToken cancellationToken)
        {
            await gate(path, cancellationToken);
            WriteCore(position, source);
        }

        private void WriteCore(long position, ReadOnlyMemory<byte> source)
        {
            var content = owner._files[path];
            var required = (int)position + source.Length;
            if (content.Length < required)
                Array.Resize(ref content, required);
            source.Span.CopyTo(content.AsSpan((int)position));
            owner._files[path] = content;
        }

        public ValueTask SetLengthAsync(long length, CancellationToken cancellationToken = default)
        {
            var content = owner._files[path];
            Array.Resize(ref content, (int)length);
            owner._files[path] = content;
            return ValueTask.CompletedTask;
        }

        public ValueTask FlushToDiskAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            owner.FlushPaths.Add(path);
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
