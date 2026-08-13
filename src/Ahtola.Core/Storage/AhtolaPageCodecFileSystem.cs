namespace Ahtola.Core.Storage;

/// <summary>
/// Carries a page codec alongside an <see cref="IFileSystem"/> so open paths can
/// apply an external transform without threading codec state through every call
/// site. The codec is not a secret; do not embed keys in <see cref="IPageCodec.CodecId"/>.
/// </summary>
/// <remarks>
/// Built-in encryption uses <see cref="AhtolaEncryptionFileSystem"/> instead.
/// Combining both wrappers (or encryption options with an external codec) is
/// rejected at open time. External codecs currently have the same product
/// limitations documented for Turso's C ABI: no simultaneous built-in encryption,
/// no ATTACH, no multi-process WAL consumers that lack the same codec, and no
/// MVCC path that assumes plaintext page images.
/// </remarks>
public sealed class AhtolaPageCodecFileSystem : IFileSystem, IDisposable
{
    private readonly IFileSystem _inner;
    private readonly bool _ownsInner;
    private readonly bool _ownsCodec;
    private bool _disposed;

    /// <summary>Wraps <paramref name="inner"/> with the given page codec.</summary>
    public AhtolaPageCodecFileSystem(IFileSystem inner, IPageCodec pageCodec, bool ownsInner = false)
        : this(inner, pageCodec, ownsInner, ownsCodec: false)
    {
    }

    private AhtolaPageCodecFileSystem(
        IFileSystem inner,
        IPageCodec pageCodec,
        bool ownsInner,
        bool ownsCodec)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(pageCodec);
        PageCodecSupport.ValidateExternalCodec(pageCodec);
        if (inner is AhtolaEncryptionFileSystem)
        {
            throw new ArgumentException(
                "Built-in encryption cannot be combined with an external page codec.",
                nameof(inner));
        }

        if (inner is AhtolaPageCodecFileSystem)
        {
            throw new ArgumentException(
                "Nested page-codec file systems are not supported.",
                nameof(inner));
        }

        _inner = inner;
        PageCodec = pageCodec;
        _ownsInner = ownsInner;
        _ownsCodec = ownsCodec;
    }

    /// <summary>The page codec applied when opening databases through this file system.</summary>
    public IPageCodec PageCodec { get; }

    /// <summary>The underlying file system.</summary>
    public IFileSystem Inner
    {
        get
        {
            ThrowIfDisposed();
            return _inner;
        }
    }

    internal AhtolaPageCodecFileSystem WithInner(IFileSystem inner)
        => new(inner, PageCodec, ownsInner: false, ownsCodec: false);

    /// <inheritdoc />
    public bool FileExists(string path)
    {
        ThrowIfDisposed();
        return _inner.FileExists(path);
    }

    /// <inheritdoc />
    public IFile OpenFile(string path, FileOpenMode mode, bool readOnly = false)
    {
        ThrowIfDisposed();
        return _inner.OpenFile(path, mode, readOnly);
    }

    /// <inheritdoc />
    public void DeleteFile(string path)
    {
        ThrowIfDisposed();
        _inner.DeleteFile(path);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
                if (_ownsCodec && PageCodec is IDisposable disposableCodec)
            disposableCodec.Dispose();
        if (_ownsInner && _inner is IDisposable disposableInner)
            disposableInner.Dispose();
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
