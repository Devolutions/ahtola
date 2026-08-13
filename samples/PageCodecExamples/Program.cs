/**
 * Ahtola ports of Turso sample-style encryption / page-codec programs.
 *
 * 1) Built-in page encryption — adapted from turso-src/examples/dotnet/Encryption.cs
 *    (Turso uses AEGIS256; managed Ahtola uses AES-256-GCM as the shipped cipher).
 *
 * 2) External IPageCodec — XOR sample matching Turso core's XorPageCodec tests
 *    from the page-codec PR (#8095 / #8183). There is no separate Turso .NET
 *    external-codec sample yet; bindings only expose a C ABI IntPtr hook.
 */

using System.Text;
using Ahtola.Core.Storage;
using Ahtola.Data.Sqlite;

// --- 1. Built-in encryption (Turso Encryption.cs equivalent) ---
Console.WriteLine("=== Ahtola Local Encryption Example ===\n");

var encryptionDbPath = Path.Combine(Path.GetTempPath(), $"ahtola-encrypted-{Guid.NewGuid():N}.db");
// 32-byte hex key (AES-256-GCM). Turso sample used AEGIS256 with a different key string.
const string encryptionKey =
    "b1bbfda4f589dc9daaf004fe21111e00dc00c98237102f5c7002a5669fc76327";

try
{
    Console.WriteLine("1. Creating encrypted database...");
    using (var connection = new SqliteConnection(
               $"Data Source={encryptionDbPath};Local Provider=Managed;Pooling=False;" +
               $"Encryption Cipher=aes256gcm;Encryption Key={encryptionKey}"))
    {
        connection.Open();

        Console.WriteLine("2. Creating table and inserting data...");
        using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE users (id INTEGER PRIMARY KEY, name TEXT, ssn TEXT)";
            create.ExecuteNonQuery();
        }

        using (var insert = connection.CreateCommand())
        {
            insert.CommandText = "INSERT INTO users (name, ssn) VALUES ('Alice', '123-45-6789')";
            insert.ExecuteNonQuery();
        }

        using (var insert = connection.CreateCommand())
        {
            insert.CommandText = "INSERT INTO users (name, ssn) VALUES ('Bob', '987-65-4321')";
            insert.ExecuteNonQuery();
        }

        using (var checkpoint = connection.CreateCommand())
        {
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
            checkpoint.ExecuteNonQuery();
        }

        Console.WriteLine("3. Querying data...");
        using (var select = connection.CreateCommand())
        {
            select.CommandText = "SELECT * FROM users";
            using var reader = select.ExecuteReader();
            while (reader.Read())
            {
                Console.WriteLine(
                    $"   User: id={reader.GetInt64(0)}, name={reader.GetString(1)}, ssn={reader.GetString(2)}");
            }
        }
    }

    Console.WriteLine("\n4. Verifying encryption...");
    var rawContent = File.ReadAllBytes(encryptionDbPath);
    var contentStr = Encoding.UTF8.GetString(rawContent);
    var containsPlaintext = contentStr.Contains("Alice", StringComparison.Ordinal)
                            || contentStr.Contains("123-45-6789", StringComparison.Ordinal);

    if (containsPlaintext)
        throw new InvalidOperationException("Data appears to be unencrypted on disk!");
    Console.WriteLine("   Data is encrypted on disk (plaintext not found)");

    Console.WriteLine("\n5. Reopening database with correct key...");
    using (var connection2 = new SqliteConnection(
               $"Data Source={encryptionDbPath};Local Provider=Managed;Pooling=False;" +
               $"Encryption Cipher=aes256gcm;Encryption Key={encryptionKey}"))
    {
        connection2.Open();
        using var select = connection2.CreateCommand();
        select.CommandText = "SELECT name FROM users";
        using var reader = select.ExecuteReader();
        var names = new List<string>();
        while (reader.Read())
            names.Add(reader.GetString(0));
        Console.WriteLine($"   Successfully read users: {string.Join(", ", names)}");
        if (names is not ["Alice", "Bob"])
            throw new InvalidOperationException("Unexpected user names after reopen.");
    }

    Console.WriteLine("\n6. Attempting to open with wrong key (should fail)...");
    try
    {
        using var connection3 = new SqliteConnection(
            $"Data Source={encryptionDbPath};Local Provider=Managed;Pooling=False;" +
            "Encryption Cipher=aes256gcm;" +
            "Encryption Key=aaaaaaa4f589dc9daaf004fe21111e00dc00c98237102f5c7002a5669fc76327");
        connection3.Open();
        using var select = connection3.CreateCommand();
        select.CommandText = "SELECT * FROM users";
        using var reader = select.ExecuteReader();
        if (reader.Read())
            throw new InvalidOperationException("Should have failed with wrong key!");
    }
    catch (Exception e) when (e is not InvalidOperationException { Message: "Should have failed with wrong key!" })
    {
        Console.WriteLine($"   Correctly failed: {e.Message}");
    }

    Console.WriteLine("\n=== Encryption example completed successfully ===\n");
}
finally
{
    DeleteDatabase(encryptionDbPath);
}

// --- 2. External XOR page codec (Turso XorPageCodec sample) ---
Console.WriteLine("=== Ahtola External Page Codec (XOR) Example ===\n");

var codecDbPath = Path.Combine(Path.GetTempPath(), $"ahtola-xor-codec-{Guid.NewGuid():N}.db");
var codec = new XorPageCodec(mask: 0x5A);

try
{
    Console.WriteLine("1. Creating database with external XOR page codec...");
    using (var connection = new SqliteConnection(
               $"Data Source={codecDbPath};Local Provider=Managed;Pooling=False"))
    {
        connection.PageCodec = codec;
        connection.Open();

        Console.WriteLine("2. Creating table and inserting data...");
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText =
                "CREATE TABLE secrets (id INTEGER PRIMARY KEY, token TEXT);" +
                "INSERT INTO secrets (token) VALUES ('codec-token-alpha');" +
                "INSERT INTO secrets (token) VALUES ('codec-token-beta');";
            cmd.ExecuteNonQuery();
        }

        using (var checkpoint = connection.CreateCommand())
        {
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
            checkpoint.ExecuteNonQuery();
        }

        Console.WriteLine("3. Querying through the codec...");
        using var select = connection.CreateCommand();
        select.CommandText = "SELECT token FROM secrets ORDER BY id";
        using var reader = select.ExecuteReader();
        while (reader.Read())
            Console.WriteLine($"   token={reader.GetString(0)}");
    }

    Console.WriteLine("\n4. Verifying on-disk bytes are transformed...");
    var encodedBytes = File.ReadAllBytes(codecDbPath);
    var encodedText = Encoding.UTF8.GetString(encodedBytes);
    if (encodedText.Contains("codec-token-alpha", StringComparison.Ordinal)
        || encodedText.Contains("SQLite format 3", StringComparison.Ordinal))
    {
        throw new InvalidOperationException("XOR page codec left plaintext / SQLite magic visible on disk.");
    }

    Console.WriteLine("   On-disk image hides SQLite magic and plaintext tokens");

    Console.WriteLine("\n5. Reopening with the same codec...");
    using (var connection2 = new SqliteConnection(
               $"Data Source={codecDbPath};Local Provider=Managed;Pooling=False"))
    {
        connection2.PageCodec = codec;
        connection2.Open();
        using var select = connection2.CreateCommand();
        select.CommandText = "SELECT token FROM secrets ORDER BY id";
        using var reader = select.ExecuteReader();
        var tokens = new List<string>();
        while (reader.Read())
            tokens.Add(reader.GetString(0));
        Console.WriteLine($"   Successfully read tokens: {string.Join(", ", tokens)}");
        if (tokens is not ["codec-token-alpha", "codec-token-beta"])
            throw new InvalidOperationException("Unexpected tokens after codec reopen.");
    }

    Console.WriteLine("\n6. Opening without codec must fail...");
    try
    {
        using var bare = new SqliteConnection(
            $"Data Source={codecDbPath};Local Provider=Managed;Pooling=False");
        bare.Open();
        using var select = bare.CreateCommand();
        select.CommandText = "SELECT token FROM secrets";
        using var reader = select.ExecuteReader();
        if (reader.Read())
            throw new InvalidOperationException("Bare open should not read codec-transformed pages.");
        throw new InvalidOperationException("Bare open unexpectedly succeeded without a codec.");
    }
    catch (Exception e) when (e is not InvalidOperationException
                              {
                                  Message: "Bare open should not read codec-transformed pages."
                                  or "Bare open unexpectedly succeeded without a codec."
                              })
    {
        Console.WriteLine($"   Correctly failed without codec: {e.GetType().Name}: {e.Message}");
    }

    Console.WriteLine("\n=== Page codec example completed successfully ===");
}
finally
{
    DeleteDatabase(codecDbPath);
}

static void DeleteDatabase(string path)
{
    foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
    {
        var candidate = path + suffix;
        if (File.Exists(candidate))
            File.Delete(candidate);
    }
}

/// <summary>
/// Sample external page codec matching Turso's test XorPageCodec: full-page XOR
/// with bootstrap decode so open can recover page size / reserved space.
/// </summary>
sealed class XorPageCodec : IPageCodec
{
    private readonly byte _mask;

    public XorPageCodec(byte mask)
    {
        _mask = mask;
        Span<byte> id = stackalloc byte[16];
        "ahtola-xor-samp-"u8.CopyTo(id);
        id[15] = mask;
        var codecId = new PageCodecId(id);
        PageCodecId.ValidateNonZero(codecId);
        CodecId = codecId;
    }

    public PageCodecId CodecId { get; }

    public byte RequiredReservedBytes => 0;

    public PageCodecHeaderInfo BootstrapPageInfo(ReadOnlySpan<byte> rawPage1Prefix)
    {
        Span<byte> decoded = stackalloc byte[rawPage1Prefix.Length];
        Xor(rawPage1Prefix, decoded);
        return PageCodecHeaderInfo.FromVisibleSqliteHeader(decoded);
    }

    public void EncodePage(PageCodecContext context, ReadOnlySpan<byte> input, Span<byte> output)
        => Xor(input, output);

    public void DecodePage(PageCodecContext context, ReadOnlySpan<byte> input, Span<byte> output)
        => Xor(input, output);

    private void Xor(ReadOnlySpan<byte> input, Span<byte> output)
    {
        if (input.Length != output.Length)
            throw new ArgumentException("XOR codec requires equal input and output lengths.");
        for (var i = 0; i < input.Length; i++)
            output[i] = (byte)(input[i] ^ _mask);
    }
}
