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
/// <c>database_sync_engine.rs</c>: only the AES-GCM cipher family the managed engine actually
/// implements (<see cref="AhtolaEncryptionCipher.Aes128Gcm"/>/<see cref="AhtolaEncryptionCipher.Aes256Gcm"/>)
/// is supported; every other remote cipher fails closed rather than being silently accepted or
/// weakened.
/// </summary>
internal static class ManagedReplicaEncryption
{
    /// <summary>
    /// Throws <see cref="NotSupportedException"/> unless <paramref name="cipher"/> is one of the
    /// AES-GCM variants the managed storage engine implements. Called eagerly from
    /// <see cref="ManagedReplicaSupportMatrix.ValidateOptions"/> so an unsupported cipher fails
    /// before any network request, not partway through a bootstrap.
    /// </summary>
    public static void EnsureSupportedCipher(AhtolaRemoteEncryptionCipher cipher)
    {
        if (cipher is not (AhtolaRemoteEncryptionCipher.Aes128Gcm or AhtolaRemoteEncryptionCipher.Aes256Gcm))
        {
            throw new NotSupportedException(
                $"Managed embedded replicas support remote encryption ciphers Aes128Gcm and Aes256Gcm only; "
                + $"'{cipher}' is not implemented by the managed storage engine.");
        }
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
            // AhtolaRemoteEncryptionCipher.Aes128Gcm/Aes256Gcm share their names with
            // AhtolaEncryptionCipher's members, so the Enum-accepting constructor resolves them
            // directly; any other (already-rejected) cipher would throw there too as a backstop.
            return new AhtolaEncryptionOptions(remoteEncryption.Cipher, key);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
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
