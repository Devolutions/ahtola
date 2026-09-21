using AwesomeAssertions;
using Ahtola.Core.Storage;
using Ahtola.Data.Sqlite.Browser;

#pragma warning disable CA1416

namespace Ahtola.Tests;

public sealed class AhtolaBrowserEncryptionOptionsTests
{
    private const string Aes256Key = "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F";
    private const string Aes128Key = "000102030405060708090A0B0C0D0E0F";

    [TestCase(Aes128Key, AhtolaEncryptionCipher.Aes128Gcm)]
    [TestCase(Aes256Key, AhtolaEncryptionCipher.Aes256Gcm)]
    public void HexAndRawKeysProduceEquivalentOptions(string hexKey, AhtolaEncryptionCipher cipher)
    {
        using var fromHex = AhtolaBrowserEncryptionOptions.FromHex(cipher, hexKey);
        using var fromKey = AhtolaBrowserEncryptionOptions.FromKey(cipher, Convert.FromHexString(hexKey));

        fromHex.Cipher.Should().Be(cipher);
        fromKey.Cipher.Should().Be(cipher);
    }

    [TestCase(AhtolaEncryptionCipher.Aes128Gcm, 15)]
    [TestCase(AhtolaEncryptionCipher.Aes128Gcm, 32)]
    [TestCase(AhtolaEncryptionCipher.Aes256Gcm, 16)]
    public void ExactKeyLengthIsRequired(AhtolaEncryptionCipher cipher, int keyLength)
    {
        var action = () => AhtolaBrowserEncryptionOptions.FromKey(cipher, new byte[keyLength]);

        action.Should().Throw<ArgumentException>();
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
    public void OptionsCopyTheCallerSuppliedKeySoCallerDisposalIsSafe()
    {
        var caller = AhtolaBrowserEncryptionOptions.FromHex(AhtolaEncryptionCipher.Aes256Gcm, Aes256Key);
        using var copy = caller.CreateOwnedCopy();
        caller.Dispose();

        copy.Cipher.Should().Be(AhtolaEncryptionCipher.Aes256Gcm);
        var stillUsable = () => copy.CreateOwnedCopy();
        stillUsable.Should().NotThrow();
    }
}
