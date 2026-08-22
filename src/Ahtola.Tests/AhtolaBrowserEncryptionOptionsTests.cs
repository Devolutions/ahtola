using System.Text;
using AwesomeAssertions;
using Ahtola.Core.Storage;
using Ahtola.Data.Sqlite;
using Ahtola.Data.Sqlite.Browser;

#pragma warning disable CA1416

namespace Ahtola.Tests;

/// <summary>
/// Covers the public browser encryption option surface: key material shapes,
/// defensive copying, disposal, and the guarantee that no secret ever reaches a
/// connection string.
/// </summary>
public sealed class AhtolaBrowserEncryptionOptionsTests
{
    private const string Aes256Key = "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F";
    private const string Aes128Key = "000102030405060708090A0B0C0D0E0F";
    private const string Password = "correct horse battery staple";

    [Test]
    public void PasswordOptionsUseTheAhtolaPasswordV1Scheme()
    {
        using var options = AhtolaBrowserEncryptionOptions.FromPassword(Password);

        options.IsPasswordDerived.Should().BeTrue();
        options.Cipher.Should().Be(AhtolaEncryptionCipher.Aes256Gcm);
        options.PasswordSchemeId.Should().Be(AhtolaPasswordEncryption.SchemeIdV1);
        AhtolaBrowserCryptoParameters.PasswordIterations.Should().Be(AhtolaPasswordEncryption.Pbkdf2IterationsV1);
        AhtolaBrowserCryptoParameters.PasswordSalt.Should().Be(AhtolaPasswordEncryption.DomainSaltV1);
    }

    [TestCase(Aes128Key, AhtolaEncryptionCipher.Aes128Gcm)]
    [TestCase(Aes256Key, AhtolaEncryptionCipher.Aes256Gcm)]
    public void HexAndRawKeysProduceEquivalentOptions(string hexKey, AhtolaEncryptionCipher cipher)
    {
        using var fromHex = AhtolaBrowserEncryptionOptions.FromHex(cipher, hexKey);
        using var fromKey = AhtolaBrowserEncryptionOptions.FromKey(cipher, Convert.FromHexString(hexKey));

        fromHex.Cipher.Should().Be(cipher);
        fromKey.Cipher.Should().Be(cipher);
        fromHex.IsPasswordDerived.Should().BeFalse();
        fromHex.PasswordSchemeId.Should().BeNull();
    }

    [TestCase(AhtolaEncryptionCipher.Aes128Gcm, 15)]
    [TestCase(AhtolaEncryptionCipher.Aes128Gcm, 32)]
    [TestCase(AhtolaEncryptionCipher.Aes256Gcm, 16)]
    public void ExactKeyLengthIsRequired(AhtolaEncryptionCipher cipher, int keyLength)
    {
        var action = () => AhtolaBrowserEncryptionOptions.FromKey(cipher, new byte[keyLength]);

        action.Should().Throw<ArgumentException>();
    }

    [TestCase(AhtolaEncryptionCipher.Aegis256)]
    [TestCase(AhtolaEncryptionCipher.Aegis128l)]
    public void NonAesGcmCiphersAreRejected(AhtolaEncryptionCipher cipher)
    {
        var action = () => AhtolaBrowserEncryptionOptions.FromKey(cipher, new byte[32]);

        action.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Test]
    public void NonHexadecimalKeysAreRejected()
    {
        var action = () => AhtolaBrowserEncryptionOptions.FromHex(
            AhtolaEncryptionCipher.Aes256Gcm,
            "not-hexadecimal");

        action.Should().Throw<ArgumentException>();
    }

    [Test]
    public void ConnectionStringNeverCarriesKeyMaterial()
    {
        using var keyed = AhtolaBrowserEncryptionOptions.FromHex(AhtolaEncryptionCipher.Aes256Gcm, Aes256Key);
        using var keyedOptions = new AhtolaBrowserOptions("owned/data.db", "owned", encryption: keyed);
        using var passworded = AhtolaBrowserEncryptionOptions.FromPassword(Password);
        using var passwordOptions = new AhtolaBrowserOptions("owned/data.db", "owned", encryption: passworded);

        keyedOptions.IsEncrypted.Should().BeTrue();
        passwordOptions.IsEncrypted.Should().BeTrue();
        foreach (var connectionString in new[] { keyedOptions.ConnectionString, passwordOptions.ConnectionString })
        {
            connectionString.Should().NotContain(Aes256Key);
            connectionString.Should().NotContain(Password);
            connectionString.Should().NotContain("Password", "no password keyword may leak into the connection string");
            connectionString.Should().NotContain("Key");
        }

        var builder = new SqliteConnectionStringBuilder(keyedOptions.ConnectionString);
        builder.DataSource.Should().Be("owned/data.db");
        builder.LocalProvider.Should().Be(AhtolaLocalProvider.Managed);
    }

    [Test]
    public void OptionsWithoutEncryptionStayUnencrypted()
    {
        using var options = new AhtolaBrowserOptions("owned/data.db", "owned");

        options.IsEncrypted.Should().BeFalse();
        options.Encryption.Should().BeNull();
    }

    [Test]
    public void OptionsCopyTheCallerSuppliedKeySoCallerDisposalIsSafe()
    {
        var caller = AhtolaBrowserEncryptionOptions.FromHex(AhtolaEncryptionCipher.Aes256Gcm, Aes256Key);
        using var options = new AhtolaBrowserOptions("owned/data.db", "owned", encryption: caller);

        caller.Dispose();

        options.Encryption.Should().NotBeNull();
        options.Encryption!.Cipher.Should().Be(AhtolaEncryptionCipher.Aes256Gcm);
        var stillUsable = () => options.Encryption!.CreateOwnedCopy();
        stillUsable.Should().NotThrow();
    }

    [Test]
    public void DisposingOptionsReleasesTheEncryptionCopy()
    {
        using var caller = AhtolaBrowserEncryptionOptions.FromHex(AhtolaEncryptionCipher.Aes256Gcm, Aes256Key);
        var options = new AhtolaBrowserOptions("owned/data.db", "owned", encryption: caller);
        var copy = options.Encryption!;

        options.Dispose();

        options.Encryption.Should().BeNull();
        var released = () => copy.CreateOwnedCopy();
        released.Should().Throw<ObjectDisposedException>();
    }

    [Test]
    public void DisposedEncryptionOptionsRejectFurtherUse()
    {
        var options = AhtolaBrowserEncryptionOptions.FromPassword(Password);
        options.Dispose();
        options.Dispose();

        var action = () => options.CreateOwnedCopy();
        action.Should().Throw<ObjectDisposedException>();
    }

    [Test]
    public void EmptyPasswordsAndKeysAreRejected()
    {
        var emptyPassword = () => AhtolaBrowserEncryptionOptions.FromPassword(string.Empty);
        var emptyHex = () => AhtolaBrowserEncryptionOptions.FromHex(AhtolaEncryptionCipher.Aes256Gcm, string.Empty);

        emptyPassword.Should().Throw<ArgumentException>();
        emptyHex.Should().Throw<ArgumentException>();
    }

    [Test]
    public void BrowserPasswordDerivationMatchesTheDesktopScheme()
    {
        // The browser derives the same 32 bytes through Web Crypto PBKDF2; this
        // asserts the shared parameters that make those two derivations agree.
        using var desktop = AhtolaPasswordEncryption.FromPassword(Password);

        desktop.Cipher.Should().Be(Core.Storage.AhtolaEncryptionCipher.Aes256Gcm);
        AhtolaBrowserCryptoParameters.PasswordKeySize.Should().Be(32);
        Encoding.UTF8.GetBytes(AhtolaBrowserCryptoParameters.PasswordSalt).Should().Equal(
            Encoding.UTF8.GetBytes(AhtolaPasswordEncryption.DomainSaltV1));
    }
}
