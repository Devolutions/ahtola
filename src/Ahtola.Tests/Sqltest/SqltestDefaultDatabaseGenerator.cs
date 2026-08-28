using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text;
using Ahtola.Core;

namespace Ahtola.Tests.Sqltest;

internal sealed record SqltestGeneratedUser(
    string FirstName,
    string LastName,
    string Email,
    string PhoneNumber,
    string Address,
    string City,
    string State,
    string Zipcode,
    long Age);

internal sealed record SqltestGeneratedProduct(string Name, double Price);

internal sealed record SqltestGeneratedData(
    IReadOnlyList<SqltestGeneratedUser> Users,
    IReadOnlyList<SqltestGeneratedProduct> Products);

/// <summary>
/// Managed port of Turso's deterministic default sqltest database generator.
/// The seed, schema, generation order, fake-data providers, and ChaCha8 stream
/// mirror <c>testing/sqltest/src/generator/mod.rs</c>.
/// </summary>
internal static class SqltestDefaultDatabaseGenerator
{
    internal const ulong DefaultSeed = 42;
    internal const int DefaultUserCount = 10_000;

    private static readonly string[] ProductNames =
    [
        "hat", "cap", "shirt", "sweater", "sweatshirt", "shorts",
        "jeans", "sneakers", "boots", "coat", "accessories",
    ];

    private static readonly Lazy<string> DefaultPath =
        new(() => GenerateCached(noRowidAlias: false), LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Lazy<string> NoRowidAliasPath =
        new(() => GenerateCached(noRowidAlias: true), LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly string FixtureDirectory = Path.Combine(
        TestContext.CurrentContext.WorkDirectory,
        ".sqltest-default-fixtures",
        Environment.ProcessId.ToString(CultureInfo.InvariantCulture));

    static SqltestDefaultDatabaseGenerator()
        => AppDomain.CurrentDomain.ProcessExit += static (_, _) => TryDeleteFixtureDirectory();

    public static string GetDefaultPath(bool noRowidAlias)
        => noRowidAlias ? NoRowidAliasPath.Value : DefaultPath.Value;

    internal static SqltestGeneratedData GenerateData(int userCount, ulong seed = DefaultSeed)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(userCount);
        var random = new ChaCha8Random(seed);
        var users = new SqltestGeneratedUser[userCount];
        for (var index = 0; index < users.Length; index++)
            users[index] = GenerateUser(random);

        var products = ProductNames
            .Select(name => new SqltestGeneratedProduct(name, random.NextDoubleInclusive(1.0, 100.0)))
            .ToArray();
        return new SqltestGeneratedData(users, products);
    }

    internal static void GenerateDatabase(
        string path,
        bool noRowidAlias,
        int userCount = DefaultUserCount,
        ulong seed = DefaultSeed)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        DeleteDatabase(path);
        var data = GenerateData(userCount, seed);

        using var database = EmbeddedDatabase.OpenFile(path);
        using var connection = database.Connect();
        Execute(connection, "BEGIN");
        try
        {
            var primaryKey = noRowidAlias ? "INT PRIMARY KEY" : "INTEGER PRIMARY KEY";
            Execute(
                connection,
                $"""
                 CREATE TABLE users (
                     id {primaryKey},
                     first_name TEXT,
                     last_name TEXT,
                     email TEXT,
                     phone_number TEXT,
                     address TEXT,
                     city TEXT,
                     state TEXT,
                     zipcode TEXT,
                     age INTEGER
                 );
                 CREATE TABLE products (
                     id {primaryKey},
                     name TEXT,
                     price REAL
                 );
                 """);
            if (!noRowidAlias)
                Execute(connection, "CREATE INDEX age_idx ON users (age);");

            var userSql = noRowidAlias
                ? """
                  INSERT INTO users
                    (id, first_name, last_name, email, phone_number, address, city, state, zipcode, age)
                  VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10)
                  """
                : """
                  INSERT INTO users
                    (first_name, last_name, email, phone_number, address, city, state, zipcode, age)
                  VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9)
                  """;
            using (var insertUser = connection.Prepare(userSql))
            {
                for (var index = 0; index < data.Users.Count; index++)
                {
                    var user = data.Users[index];
                    var offset = noRowidAlias ? 1 : 0;
                    if (noRowidAlias)
                        insertUser.Bind(1, SqlValue.Integer(index + 1));
                    insertUser.Bind(offset + 1, SqlValue.Text(user.FirstName));
                    insertUser.Bind(offset + 2, SqlValue.Text(user.LastName));
                    insertUser.Bind(offset + 3, SqlValue.Text(user.Email));
                    insertUser.Bind(offset + 4, SqlValue.Text(user.PhoneNumber));
                    insertUser.Bind(offset + 5, SqlValue.Text(user.Address));
                    insertUser.Bind(offset + 6, SqlValue.Text(user.City));
                    insertUser.Bind(offset + 7, SqlValue.Text(user.State));
                    insertUser.Bind(offset + 8, SqlValue.Text(user.Zipcode));
                    insertUser.Bind(offset + 9, SqlValue.Integer(user.Age));
                    _ = insertUser.Step();
                    insertUser.Reset();
                }
            }

            var productSql = noRowidAlias
                ? "INSERT INTO products (id, name, price) VALUES (?1, ?2, ?3)"
                : "INSERT INTO products (name, price) VALUES (?1, ?2)";
            using (var insertProduct = connection.Prepare(productSql))
            {
                for (var index = 0; index < data.Products.Count; index++)
                {
                    var product = data.Products[index];
                    var offset = noRowidAlias ? 1 : 0;
                    if (noRowidAlias)
                        insertProduct.Bind(1, SqlValue.Integer(index + 1));
                    insertProduct.Bind(offset + 1, SqlValue.Text(product.Name));
                    insertProduct.Bind(offset + 2, SqlValue.Real(product.Price));
                    _ = insertProduct.Step();
                    insertProduct.Reset();
                }
            }

            Execute(connection, "COMMIT");
        }
        catch
        {
            try
            {
                Execute(connection, "ROLLBACK");
            }
            catch (Exception)
            {
                // Preserve the generation failure.
            }

            throw;
        }
    }

    private static string GenerateCached(bool noRowidAlias)
    {
        var path = Path.Combine(
            FixtureDirectory,
            noRowidAlias ? "database-no-rowidalias.db" : "database.db");
        GenerateDatabase(path, noRowidAlias);
        return path;
    }

    private static void TryDeleteFixtureDirectory()
    {
        try
        {
            if (Directory.Exists(FixtureDirectory))
                Directory.Delete(FixtureDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static SqltestGeneratedUser GenerateUser(ChaCha8Random random)
    {
        var firstName = random.Choose(SqltestFakeEnglishData.FirstNames);
        var lastName = random.Choose(SqltestFakeEnglishData.LastNames);
        var emailName = random.Choose(SqltestFakeEnglishData.FirstNames).ToLowerInvariant();
        var email = $"{emailName}@example.{random.Choose(SqltestFakeEnglishData.SafeEmailDomains)}";
        var phone = Numerify(random.Choose(SqltestFakeEnglishData.PhoneNumberFormats), random);

        var streetName = random.NextBoolean()
            ? random.Choose(SqltestFakeEnglishData.FirstNames)
            : random.Choose(SqltestFakeEnglishData.LastNames);
        var address = $"{streetName} {random.Choose(SqltestFakeEnglishData.StreetSuffixes)}";

        string city;
        switch (random.NextUInt32Inclusive(0, 4))
        {
            case 0:
                city =
                    $"{random.Choose(SqltestFakeEnglishData.CityPrefixes)} " +
                    $"{random.Choose(SqltestFakeEnglishData.FirstNames)} " +
                    $"{random.Choose(SqltestFakeEnglishData.LastNames)} " +
                    $"{random.Choose(SqltestFakeEnglishData.CitySuffixes)}";
                break;
            case 1:
                city =
                    $"{random.Choose(SqltestFakeEnglishData.FirstNames)} " +
                    $"{random.Choose(SqltestFakeEnglishData.CitySuffixes)}";
                break;
            default:
                city =
                    $"{random.Choose(SqltestFakeEnglishData.LastNames)} " +
                    $"{random.Choose(SqltestFakeEnglishData.CitySuffixes)}";
                break;
        }

        return new SqltestGeneratedUser(
            firstName,
            lastName,
            email,
            phone,
            address,
            city,
            random.Choose(SqltestFakeEnglishData.StateAbbreviations),
            Numerify(random.Choose(SqltestFakeEnglishData.ZipFormats), random),
            checked((long)random.NextUInt64Inclusive(1, 100)));
    }

    private static string Numerify(string format, ChaCha8Random random)
    {
        var result = new StringBuilder(format.Length);
        foreach (var character in format)
        {
            result.Append(character switch
            {
                '^' => (char)('0' + random.NextUInt32Inclusive(1, 9)),
                '#' => (char)('0' + random.NextUInt32Inclusive(0, 9)),
                _ => character,
            });
        }

        return result.ToString();
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        foreach (var statement in connection.PrepareScript(sql))
        {
            using (statement)
            {
                while (statement.Step() == StatementStepResult.Row)
                {
                }
            }
        }
    }

    private static void DeleteDatabase(string path)
    {
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
            File.Delete(path + suffix);
    }

    private sealed class ChaCha8Random
    {
        private readonly uint[] _key = new uint[8];
        private readonly uint[] _buffer = new uint[64];
        private ulong _counter;
        private int _index = 64;

        public ChaCha8Random(ulong seed)
        {
            Span<byte> key = stackalloc byte[32];
            var state = seed;
            for (var offset = 0; offset < key.Length; offset += sizeof(uint))
            {
                state = unchecked(state * 6_364_136_223_846_793_005UL + 11_634_580_027_462_260_723UL);
                var xorshifted = (uint)(((state >> 18) ^ state) >> 27);
                var rotation = (int)(state >> 59);
                BinaryPrimitives.WriteUInt32LittleEndian(
                    key[offset..],
                    BitOperations.RotateRight(xorshifted, rotation));
            }

            for (var index = 0; index < _key.Length; index++)
                _key[index] = BinaryPrimitives.ReadUInt32LittleEndian(key[(index * sizeof(uint))..]);
        }

        public T Choose<T>(IReadOnlyList<T> values)
            => values[checked((int)NextUInt32Inclusive(0, checked((uint)values.Count - 1)))];

        public bool NextBoolean() => unchecked((int)NextUInt32()) < 0;

        public uint NextUInt32Inclusive(uint low, uint high)
        {
            var range = unchecked(high - low + 1);
            if (range == 0)
                return NextUInt32();
            var product = (ulong)NextUInt32() * range;
            var result = (uint)(product >> 32);
            var lowOrder = (uint)product;
            if (lowOrder > unchecked(0U - range))
            {
                var newProduct = (ulong)NextUInt32() * range;
                if (unchecked(lowOrder + (uint)(newProduct >> 32)) < lowOrder)
                    result++;
            }

            return unchecked(low + result);
        }

        public ulong NextUInt64Inclusive(ulong low, ulong high)
        {
            var range = unchecked(high - low + 1);
            if (range == 0)
                return NextUInt64();
            var product = (UInt128)NextUInt64() * range;
            var result = (ulong)(product >> 64);
            var lowOrder = (ulong)product;
            if (lowOrder > unchecked(0UL - range))
            {
                var newProduct = (UInt128)NextUInt64() * range;
                if (unchecked(lowOrder + (ulong)(newProduct >> 64)) < lowOrder)
                    result++;
            }

            return unchecked(low + result);
        }

        public double NextDoubleInclusive(double low, double high)
        {
            var bits = (NextUInt64() >> 12) | 0x3FF0_0000_0000_0000UL;
            var zeroToOne = BitConverter.UInt64BitsToDouble(bits) - 1.0;
            return zeroToOne * (high - low) + low;
        }

        private uint NextUInt32()
        {
            if (_index == _buffer.Length)
                Refill();
            return _buffer[_index++];
        }

        private ulong NextUInt64()
        {
            var low = NextUInt32();
            var high = NextUInt32();
            return ((ulong)high << 32) | low;
        }

        private void Refill()
        {
            Span<uint> state = stackalloc uint[16];
            Span<uint> working = stackalloc uint[16];
            for (var block = 0; block < 4; block++)
            {
                state[0] = 0x61707865;
                state[1] = 0x3320646E;
                state[2] = 0x79622D32;
                state[3] = 0x6B206574;
                _key.CopyTo(state[4..12]);
                state[12] = (uint)(_counter + (ulong)block);
                state[13] = (uint)((_counter + (ulong)block) >> 32);
                state[14] = 0;
                state[15] = 0;
                state.CopyTo(working);
                for (var round = 0; round < 4; round++)
                {
                    QuarterRound(working, 0, 4, 8, 12);
                    QuarterRound(working, 1, 5, 9, 13);
                    QuarterRound(working, 2, 6, 10, 14);
                    QuarterRound(working, 3, 7, 11, 15);
                    QuarterRound(working, 0, 5, 10, 15);
                    QuarterRound(working, 1, 6, 11, 12);
                    QuarterRound(working, 2, 7, 8, 13);
                    QuarterRound(working, 3, 4, 9, 14);
                }

                for (var word = 0; word < 16; word++)
                    _buffer[(block * 16) + word] = unchecked(working[word] + state[word]);
            }

            _counter += 4;
            _index = 0;
        }

        private static void QuarterRound(Span<uint> state, int a, int b, int c, int d)
        {
            state[a] = unchecked(state[a] + state[b]);
            state[d] = BitOperations.RotateLeft(state[d] ^ state[a], 16);
            state[c] = unchecked(state[c] + state[d]);
            state[b] = BitOperations.RotateLeft(state[b] ^ state[c], 12);
            state[a] = unchecked(state[a] + state[b]);
            state[d] = BitOperations.RotateLeft(state[d] ^ state[a], 8);
            state[c] = unchecked(state[c] + state[d]);
            state[b] = BitOperations.RotateLeft(state[b] ^ state[c], 7);
        }
    }
}
