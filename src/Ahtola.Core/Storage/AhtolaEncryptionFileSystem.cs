namespace Ahtola.Core.Storage;

/// <summary>
/// Supplies Ahtola page-encryption options to a managed file database opened
/// through <see cref="IFileSystem"/>.
/// </summary>
/// <remarks>
/// This wrapper does not transform file I/O itself. The managed pager obtains
/// an owned snapshot while it opens the database and WAL. That snapshot stays
/// available for pager opens performed later by the opened database, while each
/// page store and WAL owns its own key copy. The supplied options may therefore
/// be disposed once this wrapper is constructed.
/// </remarks>
public sealed class AhtolaEncryptionFileSystem : IFileSystem, IDisposable
{
    private readonly IFileSystem _inner;
    private readonly bool _ownsEncryption;
    private AhtolaEncryptionOptions? _encryption;

    /// <summary>
    /// Wraps <paramref name="inner"/> so a managed pager opened through it uses
    /// <paramref name="encryption"/> for both its database and WAL files.
    /// </summary>
    public AhtolaEncryptionFileSystem(IFileSystem inner, AhtolaEncryptionOptions encryption)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(encryption);
        _inner = inner;
        _encryption = encryption.CreateOwnedCopy();
        _ownsEncryption = true;
    }

    private AhtolaEncryptionFileSystem(
        IFileSystem inner,
        AhtolaEncryptionOptions encryption,
        bool ownsEncryption)
    {
        _inner = inner;
        _encryption = encryption;
        _ownsEncryption = ownsEncryption;
    }

    /// <summary>
    /// The wrapper-owned encryption snapshot used by managed pager opens.
    /// Disposing the wrapper invalidates this snapshot.
    /// </summary>
    public AhtolaEncryptionOptions Encryption
        => _encryption ?? throw new ObjectDisposedException(nameof(AhtolaEncryptionFileSystem));

    /// <inheritdoc />
    public bool FileExists(string path) => _inner.FileExists(path);

    /// <inheritdoc />
    public IFile OpenFile(string path, FileOpenMode mode, bool readOnly = false)
        => _inner.OpenFile(path, mode, readOnly);

    /// <inheritdoc />
    public void DeleteFile(string path) => _inner.DeleteFile(path);

    /// <summary>Zeros this wrapper's independent key snapshot.</summary>
    public void Dispose()
    {
        var encryption = Interlocked.Exchange(ref _encryption, null);
        if (_ownsEncryption)
            encryption?.Dispose();

        GC.SuppressFinalize(this);
    }

    internal IFileSystem Inner => _inner;

    internal AhtolaEncryptionFileSystem WithInner(IFileSystem inner)
        => new(inner, Encryption, ownsEncryption: false);

    internal static IFileSystem Unwrap(IFileSystem fileSystem)
            => fileSystem switch
            {
                AhtolaEncryptionFileSystem encrypted => Unwrap(encrypted._inner),
                AhtolaPageCodecFileSystem codec => Unwrap(codec.Inner),
                _ => fileSystem,
            };

    ~AhtolaEncryptionFileSystem() => Dispose();
}
