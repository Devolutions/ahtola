using AwesomeAssertions;
using Ahtola.Core.Storage;
using Ahtola.Data.Sqlite;

namespace Ahtola.Tests;

public class ManagedEncryptedFileOpenContractTests
{
    private const string Aes256Key = "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F";
    private const string WrongAes256Key = "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF";

    [Test]
    public void AhtolaManagedFileEncryptionCreatesReopensAndRejectsInvalidAccess()
    {
        var encryptedPath = CreateDatabasePath("provider");
        var plaintextPath = CreateDatabasePath("plaintext");
        try
        {
            using (var create = new global::Ahtola.AhtolaConnection(ManagedEncryptionConnectionString(encryptedPath, Aes256Key)))
            {
                create.Open();
                create.ExecuteNonQuery("CREATE TABLE records(id INTEGER PRIMARY KEY, value TEXT);");
                create.ExecuteNonQuery("INSERT INTO records VALUES (7, 'encrypted');");
            }

            File.ReadAllBytes(encryptedPath).AsSpan(0, 5).ToArray().Should().Equal("AHTLA"u8.ToArray());

            using (var reopen = new global::Ahtola.AhtolaConnection(ManagedEncryptionConnectionString(encryptedPath, Aes256Key)))
            {
                reopen.Open();
                using var query = reopen.CreateCommand();
                query.CommandText = "SELECT value FROM records WHERE id = 7;";
                query.ExecuteScalar().Should().Be("encrypted");
            }

            Assert.Throws<InvalidDataException>(() =>
            {
                using var missingKey = new global::Ahtola.AhtolaConnection(
                    $"Data Source={encryptedPath};Local Provider=Managed");
                missingKey.Open();
            })!.Message.Should().Contain("encrypted");

            Assert.Throws<InvalidDataException>(() =>
            {
                using var wrongKey = new global::Ahtola.AhtolaConnection(
                    ManagedEncryptionConnectionString(encryptedPath, WrongAes256Key));
                wrongKey.Open();
            })!.Message.Should().Contain("failed authentication");

            var encryptedBytes = File.ReadAllBytes(encryptedPath);
            encryptedBytes[100] ^= 0x80;
            File.WriteAllBytes(encryptedPath, encryptedBytes);
            Assert.Throws<InvalidDataException>(() =>
            {
                using var tampered = new global::Ahtola.AhtolaConnection(
                    ManagedEncryptionConnectionString(encryptedPath, Aes256Key));
                tampered.Open();
            })!.Message.Should().Contain("failed authentication");

            using (var plaintext = new global::Ahtola.AhtolaConnection(
                       $"Data Source={plaintextPath};Local Provider=Managed"))
            {
                plaintext.Open();
                plaintext.ExecuteNonQuery("CREATE TABLE records(id INTEGER PRIMARY KEY);");
            }

            Assert.Throws<InvalidDataException>(() =>
            {
                using var encryptedOpen = new global::Ahtola.AhtolaConnection(
                    ManagedEncryptionConnectionString(plaintextPath, Aes256Key));
                encryptedOpen.Open();
            })!.Message.Should().Contain("Plaintext fallback");
        }
        finally
        {
            DeleteDatabase(encryptedPath);
            DeleteDatabase(plaintextPath);
        }
    }

    [Test]
    public void SqliteFacadeManagedFileEncryptionUsesExplicitCipherAndKeyOptions()
    {
        var path = CreateDatabasePath("facade");
        try
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                LocalProvider = AhtolaLocalProvider.Managed,
                EncryptionCipher = "aes256gcm",
                EncryptionKey = Aes256Key,
            };

            using (var create = new SqliteConnection(builder.ConnectionString))
            {
                create.Open();
                create.ExecuteNonQuery("CREATE TABLE records(id INTEGER PRIMARY KEY, value TEXT);");
                create.ExecuteNonQuery("INSERT INTO records VALUES (1, 'facade');");
            }

            using var reopen = new SqliteConnection(builder.ConnectionString);
            reopen.Open();
            reopen.ExecuteScalar<string>("SELECT value FROM records WHERE id = 1;").Should().Be("facade");
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [Test]
    public void ManagedProviderRejectsEncryptionCipherWithoutKey()
    {
        using var connection = new global::Ahtola.AhtolaConnection(
            "Data Source=:memory:;Local Provider=Managed;Encryption Cipher=Aes256Gcm");

        Assert.Throws<InvalidOperationException>(() => connection.Open())!.Message
            .Should().Be("Encryption Key is required when Encryption Cipher is specified.");
    }

    [Test]
    public void ManagedProviderRejectsEncryptionKeyWithoutCipher()
    {
        using var connection = new global::Ahtola.AhtolaConnection(
            "Data Source=:memory:;Local Provider=Managed;Encryption Key=0011");

            Assert.Throws<InvalidOperationException>(() => connection.Open())!.Message
                .Should().Be("Encryption Cipher is required when Encryption Key is specified.");
    }

        [Test]
        public void SqliteFacadePasswordCreatesReopensAndRejectsWrongOrEmptyPassword()
        {
            var path = CreateDatabasePath("password");
            const string password = "rdm-workspace-secret";
            try
            {
                var createCs = new SqliteConnectionStringBuilder
                {
                    DataSource = path,
                    LocalProvider = AhtolaLocalProvider.Managed,
                    Password = password,
                    Pooling = false,
                }.ConnectionString;

                using (var create = new SqliteConnection(createCs))
                {
                    create.Open();
                    create.ExecuteNonQuery("CREATE TABLE records(id INTEGER PRIMARY KEY, value TEXT);");
                    create.ExecuteNonQuery("INSERT INTO records VALUES (1, 'secret-row');");
                }

                File.ReadAllBytes(path).AsSpan(0, 5).ToArray().Should().Equal("AHTLA"u8.ToArray());

                using (var reopen = new SqliteConnection(createCs))
                {
                    reopen.Open();
                    reopen.ExecuteScalar<string>("SELECT value FROM records WHERE id = 1;")
                        .Should().Be("secret-row");
                }

                var emptyPassword = Assert.Catch(() =>
                {
                    using var open = new SqliteConnection(
                        $"Data Source={path};Local Provider=Managed;Pooling=False");
                    open.Open();
                });
                emptyPassword.Should().BeOfType<InvalidDataException>();
                emptyPassword!.Message.Should().StartWith(
                    AhtolaPasswordEncryption.EncryptedOrNotDatabaseMessage);

                var wrongPassword = Assert.Catch(() =>
                {
                    using var open = new SqliteConnection(
                        $"Data Source={path};Local Provider=Managed;Password=wrong;Pooling=False");
                    open.Open();
                });
                wrongPassword.Should().BeOfType<InvalidDataException>();
                wrongPassword!.Message.Should().StartWith(
                    AhtolaPasswordEncryption.EncryptedOrNotDatabaseMessage);
                wrongPassword.InnerException.Should().NotBeNull();
            }
            finally
            {
                DeleteDatabase(path);
            }
        }

        [Test]
        public void SqliteFacadeRejectsPasswordCombinedWithEncryptionKey()
        {
            var path = CreateDatabasePath("password-key-conflict");
            try
            {
                using var connection = new SqliteConnection(
                    $"Data Source={path};Local Provider=Managed;Password=secret;Encryption Cipher=Aes256Gcm;Encryption Key={Aes256Key}");
                Assert.Throws<InvalidOperationException>(() => connection.Open())!.Message
                    .Should().Contain("Password and Encryption Key cannot be combined");
            }
            finally
            {
                DeleteDatabase(path);
            }
        }

        [Test]
        public void AhtolaManagedPasswordMatchesSqliteFacadeDerivation()
        {
            var path = CreateDatabasePath("password-ahtola");
            const string password = "shared-passphrase";
            try
            {
                using (var create = new global::Ahtola.AhtolaConnection(
                           $"Data Source={path};Local Provider=Managed;Password={password}"))
                {
                    create.Open();
                    create.ExecuteNonQuery("CREATE TABLE t(v TEXT);");
                    create.ExecuteNonQuery("INSERT INTO t VALUES ('ok');");
                }

                using var reopen = new SqliteConnection(
                    $"Data Source={path};Local Provider=Managed;Password={password};Pooling=False");
                reopen.Open();
                reopen.ExecuteScalar<string>("SELECT v FROM t;").Should().Be("ok");
            }
            finally
            {
                DeleteDatabase(path);
            }
        }

        [Test]
        public void SqliteFacadeChangePasswordAndClearPasswordRewriteRekey()
        {
            var path = CreateDatabasePath("rekey");
            const string original = "original-secret";
            const string rotated = "rotated-secret";
            try
            {
                using (var create = new SqliteConnection(
                           $"Data Source={path};Local Provider=Managed;Password={original};Pooling=False"))
                {
                    create.Open();
                    create.ExecuteNonQuery("CREATE TABLE records(id INTEGER PRIMARY KEY, value TEXT);");
                    create.ExecuteNonQuery("INSERT INTO records VALUES (1, 'before-rekey');");
                    create.ChangePassword(rotated);
                    create.ExecuteScalar<string>("SELECT value FROM records WHERE id = 1;")
                        .Should().Be("before-rekey");
                }

                Assert.Catch(() =>
                {
                    using var stale = new SqliteConnection(
                        $"Data Source={path};Local Provider=Managed;Password={original};Pooling=False");
                    stale.Open();
                })!.Message.Should().ContainEquivalentOf(
                    AhtolaPasswordEncryption.EncryptedOrNotDatabaseMessage);

                using (var reopen = new SqliteConnection(
                           $"Data Source={path};Local Provider=Managed;Password={rotated};Pooling=False"))
                {
                    reopen.Open();
                    reopen.ExecuteScalar<string>("SELECT value FROM records WHERE id = 1;")
                        .Should().Be("before-rekey");
                    reopen.ClearPassword();
                    reopen.ExecuteScalar<string>("SELECT value FROM records WHERE id = 1;")
                        .Should().Be("before-rekey");
                }

                File.ReadAllBytes(path).AsSpan(0, 15).ToArray()
                    .Should().Equal(System.Text.Encoding.ASCII.GetBytes("SQLite format 3"));

                using var plain = new SqliteConnection(
                    $"Data Source={path};Local Provider=Managed;Pooling=False");
                plain.Open();
                plain.ExecuteScalar<string>("SELECT value FROM records WHERE id = 1;")
                    .Should().Be("before-rekey");
            }
            finally
            {
                DeleteDatabase(path);
            }
        }

        [Test]
        public void SqliteFacadeSetPasswordEncryptsPlaintextDatabase()
        {
            var path = CreateDatabasePath("set-password");
            const string password = "after-create";
            try
            {
                using (var create = new SqliteConnection(
                           $"Data Source={path};Local Provider=Managed;Pooling=False"))
                {
                    create.Open();
                    create.ExecuteNonQuery("CREATE TABLE records(id INTEGER PRIMARY KEY, value TEXT);");
                    create.ExecuteNonQuery("INSERT INTO records VALUES (3, 'plain-then-encrypted');");
                    create.SetPassword(string.Empty);
                    create.SetPassword(password);
                }

                File.ReadAllBytes(path).AsSpan(0, 5).ToArray().Should().Equal("AHTLA"u8.ToArray());

                using var reopen = new SqliteConnection(
                    $"Data Source={path};Local Provider=Managed;Password={password};Pooling=False");
                reopen.Open();
                reopen.ExecuteScalar<string>("SELECT value FROM records WHERE id = 3;")
                    .Should().Be("plain-then-encrypted");
            }
            finally
            {
                DeleteDatabase(path);
            }
        }

    [Test]
        public void PassphraseSchemeCatalogExposesDefaultV1AndRejectsUnknownIds()
    {
            AhtolaPassphraseSchemes.Default.Id.Should().Be(AhtolaPasswordEncryption.SchemeIdV1);
            AhtolaPassphraseSchemes.IsRegistered(AhtolaPasswordEncryption.SchemeIdV1).Should().BeTrue();
            AhtolaPassphraseSchemes.RegisteredIds.Should().Contain(AhtolaPasswordEncryption.SchemeIdV1);

            var act = () => AhtolaPassphraseSchemes.Resolve("Ahtola.Password.does-not-exist");
            act.Should().Throw<NotSupportedException>()
                .WithMessage("*Unknown Password Scheme*")
                .WithMessage("*IPageCodec*");
        }

        [Test]
        public void SqliteFacadeExplicitPasswordSchemeV1MatchesOmittedScheme()
        {
            var path = CreateDatabasePath("password-scheme-v1");
            const string secret = "scheme-roundtrip";
            try
            {
                var createBuilder = new SqliteConnectionStringBuilder
                {
                    DataSource = path,
                    LocalProvider = AhtolaLocalProvider.Managed,
                    Password = secret,
                    PasswordScheme = AhtolaPasswordEncryption.SchemeIdV1,
                    Pooling = false,
                };

                using (var create = new SqliteConnection(createBuilder.ConnectionString))
                {
                    create.Open();
                    create.ExecuteNonQuery("CREATE TABLE records(id INTEGER PRIMARY KEY, value TEXT);");
                    create.ExecuteNonQuery("INSERT INTO records VALUES (1, 'scheme-ok');");
                }

                var defaultSchemeBuilder = new SqliteConnectionStringBuilder
                {
                    DataSource = path,
                    LocalProvider = AhtolaLocalProvider.Managed,
                    Password = secret,
                    Pooling = false,
                };
                using var reopen = new SqliteConnection(defaultSchemeBuilder.ConnectionString);
                reopen.Open();
                reopen.ExecuteScalar<string>("SELECT value FROM records WHERE id = 1;")
                    .Should().Be("scheme-ok");
            }
            finally
            {
                DeleteDatabase(path);
            }
        }

        [Test]
        public void SqliteFacadeRejectsUnknownPasswordSchemeAndSchemeWithoutPassword()
        {
            var path = CreateDatabasePath("password-scheme-errors");
            try
            {
                var unknown = new SqliteConnectionStringBuilder
                {
                    DataSource = path,
                    LocalProvider = AhtolaLocalProvider.Managed,
                    Password = "x",
                    PasswordScheme = "not.a.real.scheme",
                    Pooling = false,
                };
                Assert.Throws<NotSupportedException>(() =>
                {
                    using var connection = new SqliteConnection(unknown.ConnectionString);
                    connection.Open();
                })!.Message.Should().Contain("Unknown Password Scheme");

                var schemeOnly = new SqliteConnectionStringBuilder
                {
                    DataSource = path,
                    LocalProvider = AhtolaLocalProvider.Managed,
                    PasswordScheme = AhtolaPasswordEncryption.SchemeIdV1,
                    Pooling = false,
                };
                Assert.Throws<InvalidOperationException>(() =>
                {
                    using var connection = new SqliteConnection(schemeOnly.ConnectionString);
                    connection.Open();
                })!.Message.Should().Contain("Password Scheme requires Password");
            }
            finally
            {
                DeleteDatabase(path);
            }
        }

        [Test]
        public void AppCanRegisterPrivatePassphraseSchemeWithoutReplacingBuiltIns()
        {
            var scheme = new FixedKeyPassphraseScheme(
                id: "Tests.FixedKey.v1",
                keyHex: Aes256Key);

            AhtolaPassphraseSchemes.Register(scheme).Should().BeTrue();
            try
            {
                AhtolaPassphraseSchemes.Register(scheme).Should().BeFalse();
                var replaceBuiltIn = () => AhtolaPassphraseSchemes.Register(
                    new FixedKeyPassphraseScheme(AhtolaPasswordEncryption.SchemeIdV1, Aes256Key));
                replaceBuiltIn.Should().Throw<InvalidOperationException>().WithMessage("*built-in*");

                var path = CreateDatabasePath("custom-scheme");
                try
                {
                    var builder = new SqliteConnectionStringBuilder
                    {
                        DataSource = path,
                        LocalProvider = AhtolaLocalProvider.Managed,
                        Password = "ignored-by-fixed-scheme",
                        PasswordScheme = scheme.Id,
                        Pooling = false,
                    };
                    using (var create = new SqliteConnection(builder.ConnectionString))
                    {
                        create.Open();
                        create.ExecuteNonQuery("CREATE TABLE t(v TEXT);");
                        create.ExecuteNonQuery("INSERT INTO t VALUES ('custom');");
                    }

                    // Same on-disk key as raw Encryption Key path.
                    using var raw = new SqliteConnection(
                        ManagedEncryptionConnectionString(path, Aes256Key) + ";Pooling=False");
                    raw.Open();
                    raw.ExecuteScalar<string>("SELECT v FROM t;").Should().Be("custom");
                }
                finally
                {
                    DeleteDatabase(path);
                }
            }
            finally
            {
                AhtolaPassphraseSchemes.Unregister(scheme.Id).Should().BeTrue();
                AhtolaPassphraseSchemes.IsRegistered(scheme.Id).Should().BeFalse();
            }
        }

        [Test]
        public void EncryptedPhysicalPagerFactoryEncryptsWalFramesAndReopensThem()
        {
            var databasePath = CreateDatabasePath("pager");
        var walPath = databasePath + "-wal";
        var page = new byte[SqlitePageSize.Default];
        page.AsSpan(0, page.Length - 28).Fill(0xA5);
        try
        {
            using var encryption = AhtolaEncryptionOptions.FromHex(AhtolaEncryptionCipher.Aes256Gcm, Aes256Key);
            var fileSystem = new AhtolaEncryptionFileSystem(PhysicalFileSystem.Instance, encryption);
            var header = SqliteWalHeader.Create(SqlitePageSize.Default, salt1: 1, salt2: 2);

            using (var pager = SqlitePager.Create(fileSystem, databasePath, walPath, header))
            {
                using var transaction = pager.BeginTransaction(targetDatabaseSizeInPages: 2);
                transaction.WritePage(2, page);
                transaction.Commit();
            }

            var walBytes = File.ReadAllBytes(walPath);
            walBytes.AsSpan(SqliteWalHeader.Size + SqliteWalFrameHeader.Size, page.Length)
                .SequenceEqual(page)
                .Should()
                .BeFalse();

            using var reopened = SqlitePager.Open(fileSystem, databasePath, walPath);
            reopened.ReadCommittedPage(2).Should().Equal(page);
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    private static string ManagedEncryptionConnectionString(string path, string key)
        => $"Data Source={path};Local Provider=Managed;Encryption Cipher=Aes256Gcm;Encryption Key={key}";

    private static string CreateDatabasePath(string name)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "managed-encrypted-file-open-contract-tests");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"{name}-{Guid.NewGuid():N}.db");
    }

    private static void DeleteDatabase(string path)
    {
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var candidate = path + suffix;
            if (File.Exists(candidate))
                File.Delete(candidate);
        }
    }

        /// <summary>
        /// Test-only scheme that ignores the passphrase and uses a fixed AES-256 key.
        /// Proves app registration without depending on a second built-in KDF.
        /// </summary>
        private sealed class FixedKeyPassphraseScheme(string id, string keyHex) : IAhtolaPassphraseScheme
        {
            public string Id { get; } = id;
            public string Description => "Test fixed-key scheme";
            public Ahtola.Core.Storage.AhtolaEncryptionCipher PageCipher =>
                Ahtola.Core.Storage.AhtolaEncryptionCipher.Aes256Gcm;

            public AhtolaEncryptionOptions DeriveEncryptionOptions(string password)
            {
                _ = password;
                return AhtolaEncryptionOptions.FromHex(PageCipher, keyHex);
            }
        }
    }
