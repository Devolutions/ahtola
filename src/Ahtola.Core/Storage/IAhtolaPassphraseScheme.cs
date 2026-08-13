namespace Ahtola.Core.Storage;

/// <summary>
/// Derives built-in Ahtola page-encryption keys from a connection-string passphrase.
/// </summary>
/// <remarks>
/// <para>
/// Passphrase schemes are a <b>key-derivation</b> layer only. They always produce
/// <see cref="AhtolaEncryptionOptions"/> for the on-disk AHTLA page format.
/// They do not open SEE, SQLCipher, or other foreign page layouts — those need a
/// separate <see cref="IPageCodec"/> (or future format support).
/// </para>
/// <para>
/// Scheme <see cref="Id"/> values are stable public contracts. Changing derivation
/// bytes requires a new id (for example <c>Ahtola.Password.v2</c>), not a silent
/// change to an existing id. Consumers such as RDM should set
/// <c>Password Scheme=&lt;id&gt;</c> explicitly when they depend on a specific recipe.
/// </para>
/// </remarks>
public interface IAhtolaPassphraseScheme
{
    /// <summary>
    /// Stable scheme identifier (for example <c>Ahtola.Password.v1</c>).
    /// Used in connection strings as <c>Password Scheme</c>.
    /// </summary>
    string Id { get; }

    /// <summary>Short human-readable description for errors and diagnostics.</summary>
    string Description { get; }

    /// <summary>
    /// Page cipher this scheme always emits. Connection-string
    /// <c>Encryption Cipher</c>, when present, must match or be omitted.
    /// </summary>
    AhtolaEncryptionCipher PageCipher { get; }

    /// <summary>
    /// Derives disposable encryption options from a UTF-8 passphrase.
    /// Callers own and must dispose the returned instance.
    /// </summary>
    AhtolaEncryptionOptions DeriveEncryptionOptions(string password);
}
