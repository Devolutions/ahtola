using System.Diagnostics;
using System.Security.Cryptography;
using Ahtola.Core;
using Ahtola.Core.Storage;

namespace Ahtola;

/// <summary>
/// Bridges <see cref="AhtolaRemoteEncryptionOptions"/> (the Cloud replica wire configuration) to
/// the managed storage engine's own <see cref="AhtolaEncryptionOptions"/>/<see cref="AhtolaEncryptionFileSystem"/>,
/// so managed replica bootstrap/pull opens an encrypted database page stream the same way
/// <see cref="AhtolaConnection"/> opens any other encrypted managed database -- reusing the
/// storage layer's existing encrypted-header and reserved-byte validation instead of
/// duplicating it. Mirrors Turso's <c>remote_encryption_key</c> plumbing in
/// <c>database_sync_engine.rs</c>: every cipher that Turso format version 0 assigns an on-disk
/// cipher id to (the two AES-GCM variants plus the six AEGIS variants) is supported, and every
/// other remote cipher fails closed rather than being silently accepted or weakened.
/// </summary>
internal static class ManagedReplicaEncryption
{
    /// <summary>
    /// Throws <see cref="NotSupportedException"/> unless <paramref name="cipher"/> has an
    /// on-disk cipher id in Turso format version 0. Called eagerly from
    /// <see cref="ManagedReplicaSupportMatrix.ValidateOptions"/> so an unsupported cipher fails
    /// before any network request, not partway through a bootstrap.
    /// </summary>
    /// <remarks>
    /// <see cref="AhtolaRemoteEncryptionCipher.ChaCha20Poly1305"/> is deliberately excluded.
    /// Turso only ever sends that name to Turso Cloud, which performs the crypto server-side; it
    /// has no cipher id, no page-1 header byte, and no page framing anywhere in the pinned Rust
    /// engine. A managed embedded replica has to decode pages locally, so accepting it would mean
    /// inventing a wire format that no Turso build could read.
    /// </remarks>
    public static void EnsureSupportedCipher(AhtolaRemoteEncryptionCipher cipher)
    {
        if (TryMapToStorageCipher(cipher, out _))
            return;

        if (cipher == AhtolaRemoteEncryptionCipher.ChaCha20Poly1305)
        {
            throw new NotSupportedException(
                "Turso encrypted-page format version 0 defines no on-disk cipher id for ChaCha20-Poly1305; "
                + "it is a Turso Cloud server-side cipher only. A managed embedded replica decrypts pages "
                + "locally, so it cannot open a database configured with it.");
        }

        throw new NotSupportedException(
            $"Managed embedded replicas support the remote encryption ciphers that Turso format version 0 "
            + $"assigns a cipher id to (Aes128Gcm, Aes256Gcm, Aegis256, Aegis256X2, Aegis256X4, Aegis128L, "
            + $"Aegis128X2, Aegis128X4); '{cipher}' is not one of them.");
    }

    /// <summary>
    /// Maps a validated <see cref="AhtolaRemoteEncryptionOptions"/> to the managed storage
    /// engine's own <see cref="AhtolaEncryptionOptions"/>. The returned instance owns a copy of
    /// the decoded key and must be disposed by the caller once it is no longer needed.
    /// </summary>
    public static AhtolaEncryptionOptions CreateManagedOptions(AhtolaRemoteEncryptionOptions remoteEncryption)
    {
        ArgumentNullException.ThrowIfNull(remoteEncryption);
        EnsureSupportedCipher(remoteEncryption.Cipher);
        if (!TryMapToStorageCipher(remoteEncryption.Cipher, out var storageCipher))
            throw new UnreachableException("EnsureSupportedCipher already rejected unmapped ciphers.");

        byte[] key;
        try
        {
            key = Convert.FromBase64String(remoteEncryption.Base64Key);
        }
        catch (FormatException exception)
        {
            throw new ArgumentException(
                "The remote encryption key is not valid base64.", nameof(remoteEncryption), exception);
        }

        try
        {
            return new AhtolaEncryptionOptions(storageCipher, key);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>
    /// Maps the Cloud cipher enumeration onto the storage cipher whose numeric value is the
    /// on-disk cipher id. The mapping is explicit rather than name-based because the two
    /// enumerations spell their AEGIS members differently (<c>Aegis128L</c> versus
    /// <c>Aegis128l</c>), so a <see cref="Enum.ToString()"/> match would be brittle.
    /// </summary>
    internal static bool TryMapToStorageCipher(
        AhtolaRemoteEncryptionCipher cipher,
        out Core.Storage.AhtolaEncryptionCipher storageCipher)
    {
        switch (cipher)
        {
            case AhtolaRemoteEncryptionCipher.Aes128Gcm:
                storageCipher = Core.Storage.AhtolaEncryptionCipher.Aes128Gcm;
                return true;
            case AhtolaRemoteEncryptionCipher.Aes256Gcm:
                storageCipher = Core.Storage.AhtolaEncryptionCipher.Aes256Gcm;
                return true;
            case AhtolaRemoteEncryptionCipher.Aegis256:
                storageCipher = Core.Storage.AhtolaEncryptionCipher.Aegis256;
                return true;
            case AhtolaRemoteEncryptionCipher.Aegis256X2:
                storageCipher = Core.Storage.AhtolaEncryptionCipher.Aegis256X2;
                return true;
            case AhtolaRemoteEncryptionCipher.Aegis256X4:
                storageCipher = Core.Storage.AhtolaEncryptionCipher.Aegis256X4;
                return true;
            case AhtolaRemoteEncryptionCipher.Aegis128L:
                storageCipher = Core.Storage.AhtolaEncryptionCipher.Aegis128L;
                return true;
            case AhtolaRemoteEncryptionCipher.Aegis128X2:
                storageCipher = Core.Storage.AhtolaEncryptionCipher.Aegis128X2;
                return true;
            case AhtolaRemoteEncryptionCipher.Aegis128X4:
                storageCipher = Core.Storage.AhtolaEncryptionCipher.Aegis128X4;
                return true;
            default:
                storageCipher = default;
                return false;
        }
    }

    /// <summary>
    /// Opens a managed database at <paramref name="path"/>, wiring in
    /// <paramref name="remoteEncryption"/> when configured so the storage layer performs its
    /// existing encrypted-header/reserved-byte validation on open. Returns both the database and
    /// the (possibly null) encryption file system backing it; the caller owns disposing both --
    /// for a long-lived database the file system must be kept alive and disposed alongside it,
    /// since it is consulted on every subsequent page read/write, not only at open time.
    /// </summary>
    public static ManagedReplicaOpenedDatabase OpenDatabase(string path, AhtolaRemoteEncryptionOptions? remoteEncryption)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (remoteEncryption is null)
            return new ManagedReplicaOpenedDatabase(ManagedDatabaseAdapter.Open(path), null);

        AhtolaEncryptionFileSystem? fileSystem = null;
        IManagedDatabaseAdapter? database = null;
        try
        {
            using var managedOptions = CreateManagedOptions(remoteEncryption);
            fileSystem = new AhtolaEncryptionFileSystem(PhysicalFileSystem.Instance, managedOptions);
            database = ManagedDatabaseAdapter.OpenFile(path, fileSystem, readOnly: false, foreignReadOnly: false);
            return new ManagedReplicaOpenedDatabase(database, fileSystem);
        }
        catch
        {
            database?.Dispose();
            fileSystem?.Dispose();
            throw;
        }
    }
}

/// <summary>
/// Pairs a managed database adapter with the (possibly null) <see cref="AhtolaEncryptionFileSystem"/>
/// backing it so both are disposed together. See <see cref="ManagedReplicaEncryption.OpenDatabase"/>.
/// </summary>
internal readonly struct ManagedReplicaOpenedDatabase(IManagedDatabaseAdapter database, AhtolaEncryptionFileSystem? fileSystem)
    : IDisposable
{
    public IManagedDatabaseAdapter Database { get; } = database;

    public AhtolaEncryptionFileSystem? FileSystem { get; } = fileSystem;

    public void Dispose()
    {
        Database.Dispose();
        FileSystem?.Dispose();
    }
}
