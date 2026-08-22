using System.Security.Cryptography;

namespace Ahtola.Data.Sqlite.Browser;

internal static class AhtolaBrowserCryptoKnownAnswers
{
    internal const string Password = "ahtola-browser-test";

    internal static byte[] GetPasswordKey()
        => Convert.FromHexString("F320F26F76939AF6E9D2F8997A4B40B13A78BDC088F0178DA97C66748CD562F5");

    internal static AhtolaBrowserAesGcmKnownAnswer GetAes128()
        => new(
            key: Convert.FromHexString("FEFFE9928665731C6D6A8F9467308308"),
            nonce: Convert.FromHexString("CAFEBABEFACEDBADDECAF888"),
            plaintext: Convert.FromHexString(
                "D9313225F88406E5A55909C5AFF5269A"
                + "86A7A9531534F7DA2E4C303D8A318A72"
                + "1C3C0C95956809532FCF0E2449A6B525"
                + "B16AEDF5AA0DE657BA637B39"),
            associatedData: Convert.FromHexString("FEEDFACEDEADBEEFFEEDFACEDEADBEEFABADDAD2"),
            ciphertext: Convert.FromHexString(
                "42831EC2217774244B7221B784D0D49C"
                + "E3AA212F2C02A4E035C17E2329ACA12E"
                + "21D514B25466931C7D8F6A5AAC84AA05"
                + "1BA30B396A0AAC973D58E091"),
            tag: Convert.FromHexString("5BC94FBC3221A5DB94FAE95AE7121A47"));

    internal static AhtolaBrowserAesGcmKnownAnswer GetAes256()
        => new(
            key: new byte[32],
            nonce: new byte[AhtolaBrowserCryptoParameters.AesGcmNonceSize],
            plaintext: new byte[16],
            associatedData: [],
            ciphertext: Convert.FromHexString("CEA7403D4D606B6E074EC5D3BAF39D18"),
            tag: Convert.FromHexString("D0D1C8A799996BF0265B98B5D48AB919"));
}

internal sealed class AhtolaBrowserAesGcmKnownAnswer : IDisposable
{
    internal AhtolaBrowserAesGcmKnownAnswer(
        byte[] key,
        byte[] nonce,
        byte[] plaintext,
        byte[] associatedData,
        byte[] ciphertext,
        byte[] tag)
    {
        Key = key;
        Nonce = nonce;
        Plaintext = plaintext;
        AssociatedData = associatedData;
        Ciphertext = ciphertext;
        Tag = tag;
    }

    internal byte[] Key { get; }

    internal byte[] Nonce { get; }

    internal byte[] Plaintext { get; }

    internal byte[] AssociatedData { get; }

    internal byte[] Ciphertext { get; }

    internal byte[] Tag { get; }

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(Key);
        CryptographicOperations.ZeroMemory(Nonce);
        CryptographicOperations.ZeroMemory(Plaintext);
        CryptographicOperations.ZeroMemory(AssociatedData);
        CryptographicOperations.ZeroMemory(Ciphertext);
        CryptographicOperations.ZeroMemory(Tag);
    }
}
