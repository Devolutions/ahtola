namespace Ahtola;

public enum AhtolaValueType
{
    Empty,
    Null,
    Integer,
    Real,
    Text,
    Blob,
}

public struct AhtolaValue
{
    public AhtolaValueType ValueType;
    public long IntValue;
    public double RealValue;
    public string StringValue;
    public byte[] BlobValue;

    public static AhtolaValue Empty() => new() { ValueType = AhtolaValueType.Empty };
    public static AhtolaValue Null() => new() { ValueType = AhtolaValueType.Null };
    public static AhtolaValue Int(long value) => new() { ValueType = AhtolaValueType.Integer, IntValue = value };
    public static AhtolaValue Real(double value) => new() { ValueType = AhtolaValueType.Real, RealValue = value };
    public static AhtolaValue String(string value) => new() { ValueType = AhtolaValueType.Text, StringValue = value };
    public static AhtolaValue Blob(byte[] value) => new() { ValueType = AhtolaValueType.Blob, BlobValue = value };
}

/// <summary>
/// Supported encryption ciphers for local database encryption.
/// </summary>
public enum AhtolaEncryptionCipher
{
    /// <summary>AES-128-GCM cipher.</summary>
    Aes128Gcm,
    /// <summary>AES-256-GCM cipher.</summary>
    Aes256Gcm,
    /// <summary>AEGIS-256 cipher.</summary>
    Aegis256,
    /// <summary>AEGIS-256X2 cipher.</summary>
    Aegis256x2,
    /// <summary>AEGIS-128L cipher.</summary>
    Aegis128l,
    /// <summary>AEGIS-128X2 cipher.</summary>
    Aegis128x2,
    /// <summary>AEGIS-128X4 cipher.</summary>
    Aegis128x4,
    /// <summary>
    /// AEGIS-256X4 cipher. Appended last so the existing members keep their
    /// numeric values; ordinals here are not the on-disk cipher ids.
    /// </summary>
    Aegis256x4,
}
