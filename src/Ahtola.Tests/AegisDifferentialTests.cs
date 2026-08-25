using System.Security.Cryptography;
using AwesomeAssertions;
using Ahtola.Core.Storage;
using Ahtola.Core.Storage.Crypto;
using StorageCipher = Ahtola.Core.Storage.AhtolaEncryptionCipher;

namespace Ahtola.Tests;

/// <summary>
/// Randomized differential tests: the production AEGIS core must agree with
/// <see cref="AegisReferenceImplementation"/> -- a separately written, textbook
/// transcription of the CFRG pseudocode -- on random inputs of every shape.
/// </summary>
/// <remarks>
/// The specification vectors in <see cref="AegisKnownAnswerTests"/> pin a handful
/// of exact inputs. These tests cover the input shapes those vectors do not:
/// associated data and messages that are empty, shorter than the rate, exactly
/// one rate, a rate boundary plus one byte, and several rates plus a partial
/// tail -- the partial-block path being where an AEAD implementation is most
/// likely to diverge.
/// </remarks>
public sealed class AegisDifferentialTests
{
    private static readonly (StorageCipher Cipher, bool Is128, int Degree)[] Variants =
    [
        (StorageCipher.Aegis128L, true, 1),
        (StorageCipher.Aegis128X2, true, 2),
        (StorageCipher.Aegis128X4, true, 4),
        (StorageCipher.Aegis256, false, 1),
        (StorageCipher.Aegis256X2, false, 2),
        (StorageCipher.Aegis256X4, false, 4),
    ];

    public static IEnumerable<TestCaseData> Variations()
    {
        foreach (var (cipher, is128, degree) in Variants)
        {
            foreach (var software in new[] { false, true })
                yield return new TestCaseData(cipher, is128, degree, software).SetName($"{{m}}({cipher},software={software})");
        }
    }

    [TestCaseSource(nameof(Variations))]
    public void ProductionAgreesWithReferenceOnRandomInputs(
        StorageCipher cipher,
        bool is128,
        int degree,
        bool forceSoftwareAesRound)
    {
        var parameters = AhtolaEncryptedPageFormat.GetParameters(cipher);
        var rate = is128 ? 32 * degree : 16 * degree;
        var random = new Random(HashCode.Combine(cipher, forceSoftwareAesRound));

        // Deliberately include zero, sub-rate, exact-rate, rate+1 and multi-rate
        // lengths so the zero-padded partial-block path is always exercised.
        int[] lengths = [0, 1, rate - 1, rate, rate + 1, (rate * 3) + 5, (rate * 7) - 1];

        foreach (var messageLength in lengths)
        {
            foreach (var associatedDataLength in lengths)
            {
                var key = RandomBytes(random, parameters.KeySize);
                var nonce = RandomBytes(random, parameters.NonceSize);
                var associatedData = RandomBytes(random, associatedDataLength);
                var message = RandomBytes(random, messageLength);

                var (expectedCiphertext, expectedTag) = is128
                    ? AegisReferenceImplementation.Encrypt128(degree, key, nonce, associatedData, message)
                    : AegisReferenceImplementation.Encrypt256(degree, key, nonce, associatedData, message);

                using var aead = AhtolaAeadFactory.Create(cipher, key, forceSoftwareAesRound);
                var ciphertext = new byte[message.Length];
                var tag = new byte[16];
                aead.Encrypt(nonce, message, ciphertext, tag, associatedData);

                ciphertext.Should().Equal(
                    expectedCiphertext,
                    $"{cipher} ciphertext for |ad|={associatedDataLength}, |msg|={messageLength}");
                tag.Should().Equal(
                    expectedTag,
                    $"{cipher} tag for |ad|={associatedDataLength}, |msg|={messageLength}");

                var plaintext = new byte[ciphertext.Length];
                aead.TryDecrypt(nonce, ciphertext, tag, plaintext, associatedData).Should().BeTrue();
                plaintext.Should().Equal(message);

                var (referencePlaintext, referenceTag) = is128
                    ? AegisReferenceImplementation.Decrypt128(degree, key, nonce, associatedData, ciphertext)
                    : AegisReferenceImplementation.Decrypt256(degree, key, nonce, associatedData, ciphertext);
                referencePlaintext.Should().Equal(message);
                referenceTag.Should().Equal(tag);
            }
        }
    }

    /// <summary>
    /// Any single flipped bit in the ciphertext, tag, nonce or associated data
    /// must be rejected, and the plaintext buffer must be left zeroed.
    /// </summary>
    [TestCaseSource(nameof(Variations))]
    public void RandomSingleBitForgeriesAreAlwaysRejected(
        StorageCipher cipher,
        bool is128,
        int degree,
        bool forceSoftwareAesRound)
    {
        _ = is128;
        _ = degree;
        var parameters = AhtolaEncryptedPageFormat.GetParameters(cipher);
        var random = new Random(HashCode.Combine(cipher, forceSoftwareAesRound, 0x5EED));

        var key = RandomBytes(random, parameters.KeySize);
        var nonce = RandomBytes(random, parameters.NonceSize);
        var associatedData = RandomBytes(random, 37);
        var message = RandomBytes(random, 211);

        using var aead = AhtolaAeadFactory.Create(cipher, key, forceSoftwareAesRound);
        var ciphertext = new byte[message.Length];
        var tag = new byte[16];
        aead.Encrypt(nonce, message, ciphertext, tag, associatedData);

        for (var iteration = 0; iteration < 48; iteration++)
        {
            var forgedCiphertext = (byte[])ciphertext.Clone();
            var forgedTag = (byte[])tag.Clone();
            var forgedNonce = (byte[])nonce.Clone();
            var forgedAssociatedData = (byte[])associatedData.Clone();

            var target = iteration % 4;
            var buffer = target switch
            {
                0 => forgedCiphertext,
                1 => forgedTag,
                2 => forgedNonce,
                _ => forgedAssociatedData,
            };
            buffer[random.Next(buffer.Length)] ^= (byte)(1 << random.Next(8));

            var plaintext = new byte[ciphertext.Length];
            plaintext.AsSpan().Fill(0xEE);
            aead.TryDecrypt(forgedNonce, forgedCiphertext, forgedTag, plaintext, forgedAssociatedData)
                .Should().BeFalse($"{cipher} must reject a flipped bit in region {target}");
            plaintext.Should().OnlyContain(value => value == 0, "unverified plaintext must be zeroed");
        }
    }

    /// <summary>
    /// The accelerated and software AES rounds must produce identical AEAD output
    /// for every variant. This is the guard that keeps the browser-wasm and
    /// no-AES-instruction paths byte-compatible with everything else.
    /// </summary>
    [Test]
    public void SoftwareAndAcceleratedRoundsProduceIdenticalOutput()
    {
        foreach (var (cipher, _, _) in Variants)
        {
            var parameters = AhtolaEncryptedPageFormat.GetParameters(cipher);
            var key = new byte[parameters.KeySize];
            var nonce = new byte[parameters.NonceSize];
            RandomNumberGenerator.Fill(key);
            RandomNumberGenerator.Fill(nonce);
            var message = new byte[1000];
            RandomNumberGenerator.Fill(message);
            var associatedData = new byte[100];
            RandomNumberGenerator.Fill(associatedData);

            using var accelerated = AhtolaAeadFactory.Create(cipher, key);
            using var software = AhtolaAeadFactory.Create(cipher, key, forceSoftwareAesRound: true);

            var acceleratedCiphertext = new byte[message.Length];
            var acceleratedTag = new byte[16];
            accelerated.Encrypt(nonce, message, acceleratedCiphertext, acceleratedTag, associatedData);

            var softwareCiphertext = new byte[message.Length];
            var softwareTag = new byte[16];
            software.Encrypt(nonce, message, softwareCiphertext, softwareTag, associatedData);

            softwareCiphertext.Should().Equal(acceleratedCiphertext, $"{cipher} ciphertext");
            softwareTag.Should().Equal(acceleratedTag, $"{cipher} tag");

            // Cross-decrypt: each implementation must authenticate the other's output.
            var plaintext = new byte[message.Length];
            software.TryDecrypt(nonce, acceleratedCiphertext, acceleratedTag, plaintext, associatedData)
                .Should().BeTrue();
            plaintext.Should().Equal(message);
        }
    }

    /// <summary>
    /// Whole encrypted pages produced with the software round must be identical to
    /// the ones the accelerated round produces, at every page size the engine uses.
    /// </summary>
    [Test]
    public void EncryptedPagesAreIdenticalOnHardwareAndScalarPaths()
    {
        foreach (var (cipher, _, _) in Variants)
        {
            var parameters = AhtolaEncryptedPageFormat.GetParameters(cipher);
            var key = new byte[parameters.KeySize];
            RandomNumberGenerator.Fill(key);
            var nonce = new byte[parameters.NonceSize];
            RandomNumberGenerator.Fill(nonce);

            foreach (var pageSize in new[] { 512, 4096, 65536 })
            {
                foreach (var pageNumber in new uint[] { 1, 12 })
                {
                    var page = pageNumber == 1
                        ? AhtolaPageCipherParityTests.CreatePlaintextPageOne(pageSize, parameters.MetadataSize)
                        : AhtolaPageCipherParityTests.CreatePlaintextPage(pageSize, parameters.MetadataSize, 0x6D);

                    var accelerated = AhtolaPageCipherParityTests.EncryptWithFixedNonce(
                        cipher, key, nonce, page, pageNumber);
                    var software = AhtolaPageCipherParityTests.EncryptWithFixedNonce(
                        cipher, key, nonce, page, pageNumber, forceSoftwareAesRound: true);

                    software.Should().Equal(accelerated, $"{cipher} page {pageNumber} at {pageSize} bytes");
                }
            }
        }
    }

    /// <summary>
    /// The AEGIS core must not allocate: its whole state, keystream and scratch
    /// live in <c>stackalloc</c> buffers. Only the caller's output buffers are
    /// heap memory, so a page encrypt/decrypt adds no GC pressure beyond the page
    /// itself.
    /// </summary>
    [Test]
    public void AegisEncryptAndDecryptDoNotAllocate()
    {
        foreach (var (cipher, _, _) in Variants)
        {
            var parameters = AhtolaEncryptedPageFormat.GetParameters(cipher);
            var key = new byte[parameters.KeySize];
            var nonce = new byte[parameters.NonceSize];
            RandomNumberGenerator.Fill(key);
            RandomNumberGenerator.Fill(nonce);

            var message = new byte[4048];
            RandomNumberGenerator.Fill(message);
            var associatedData = new byte[100];
            var ciphertext = new byte[message.Length];
            var tag = new byte[16];
            var plaintext = new byte[message.Length];

            using var aead = AhtolaAeadFactory.Create(cipher, key);

            // Warm up so JIT compilation is not counted.
            aead.Encrypt(nonce, message, ciphertext, tag, associatedData);
            aead.TryDecrypt(nonce, ciphertext, tag, plaintext, associatedData).Should().BeTrue();

            var before = GC.GetAllocatedBytesForCurrentThread();
            var authenticated = true;
            for (var iteration = 0; iteration < 16; iteration++)
            {
                aead.Encrypt(nonce, message, ciphertext, tag, associatedData);
                authenticated &= aead.TryDecrypt(nonce, ciphertext, tag, plaintext, associatedData);
            }

            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            authenticated.Should().BeTrue();
            allocated.Should().Be(0, $"{cipher} must encrypt and decrypt without allocating");
        }
    }

    private static byte[] RandomBytes(Random random, int length)
    {
        var bytes = new byte[length];
        random.NextBytes(bytes);
        return bytes;
    }
}
