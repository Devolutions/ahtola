using System.Security.Cryptography;
using System.Text;
using AwesomeAssertions;
using Ahtola.Core.Storage;
using Ahtola.Data.Sqlite.Browser;

namespace Ahtola.Tests;

public sealed class AhtolaBrowserCryptoKnownAnswerTests
{
    [Test]
    public void PasswordV1ParametersProduceTheExpectedPbkdf2Sha256Key()
    {
        AhtolaBrowserCryptoParameters.PasswordSchemeId
            .Should().Be(AhtolaPasswordEncryption.SchemeIdV1);
        AhtolaBrowserCryptoParameters.PasswordSalt
            .Should().Be(AhtolaPasswordEncryption.DomainSaltV1);
        AhtolaBrowserCryptoParameters.PasswordIterations
            .Should().Be(AhtolaPasswordEncryption.Pbkdf2IterationsV1);
        AhtolaBrowserCryptoParameters.PasswordKeySize.Should().Be(32);

        var password = Encoding.UTF8.GetBytes(AhtolaBrowserCryptoKnownAnswers.Password);
        var salt = Encoding.UTF8.GetBytes(AhtolaBrowserCryptoParameters.PasswordSalt);
        var expected = AhtolaBrowserCryptoKnownAnswers.GetPasswordKey();
        var actual = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            AhtolaBrowserCryptoParameters.PasswordIterations,
            HashAlgorithmName.SHA256,
            AhtolaBrowserCryptoParameters.PasswordKeySize);
        try
        {
            actual.Should().Equal(expected);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(password);
            CryptographicOperations.ZeroMemory(salt);
            CryptographicOperations.ZeroMemory(expected);
            CryptographicOperations.ZeroMemory(actual);
        }
    }

    [TestCase(AhtolaEncryptionCipher.Aes128Gcm)]
    [TestCase(AhtolaEncryptionCipher.Aes256Gcm)]
    public void AhtolaAesGcmParametersMatchKnownAnswerVectors(AhtolaEncryptionCipher cipher)
    {
        using var vector = cipher == AhtolaEncryptionCipher.Aes128Gcm
            ? AhtolaBrowserCryptoKnownAnswers.GetAes128()
            : AhtolaBrowserCryptoKnownAnswers.GetAes256();
        var ciphertext = new byte[vector.Plaintext.Length];
        var tag = new byte[AhtolaBrowserCryptoParameters.AesGcmTagSize];
        var decrypted = new byte[vector.Plaintext.Length];
        try
        {
            using var aes = new AesGcm(vector.Key, AhtolaBrowserCryptoParameters.AesGcmTagSize);
            aes.Encrypt(
                vector.Nonce,
                vector.Plaintext,
                ciphertext,
                tag,
                vector.AssociatedData);

            ciphertext.Should().Equal(vector.Ciphertext);
            tag.Should().Equal(vector.Tag);

            aes.Decrypt(
                vector.Nonce,
                ciphertext,
                tag,
                decrypted,
                vector.AssociatedData);
            decrypted.Should().Equal(vector.Plaintext);
            vector.Nonce.Should().HaveCount(AhtolaBrowserCryptoParameters.AesGcmNonceSize);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(ciphertext);
            CryptographicOperations.ZeroMemory(tag);
            CryptographicOperations.ZeroMemory(decrypted);
        }
    }
}
