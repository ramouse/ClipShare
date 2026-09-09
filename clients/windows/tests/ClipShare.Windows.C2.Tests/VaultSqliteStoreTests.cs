namespace ClipShare.Windows.C2.Tests;

using System.Text;
using System.Text.Json;
using ClipShare.Windows.Infrastructure;
using ClipShare.Windows.Platform;
using ClipShare.Windows.Vault;
using Microsoft.Data.Sqlite;

public sealed class VaultSqliteStoreTests
{
    private const string VaultIdValue = "00112233-4455-4677-8899-aabbccddeeff";
    private const string DeviceIdValue = "11111111-2222-4333-8444-555555555555";
    private static readonly string[] DefaultFolderNames = ["收件箱", "密码", "工作", "私人", "其他"];

    [Fact]
    public async Task InitializationAndNonceAllocationSurviveReopenWithoutReuse()
    {
        string directory = CaseDirectory("sqlite-nonce");
        try
        {
            string path = Path.Combine(directory, "vault.db");
            var store = new WindowsVaultSqliteStore(path);
            Assert.Null(await store.ObserveAsync(TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.AllocateNonceAsync(
                VaultId.Parse(VaultIdValue),
                0,
                CryptoPurpose.ItemPayload,
                DeviceId.Parse(DeviceIdValue),
                TestContext.Current.CancellationToken));
            await store.CreateEmptyStagedAsync(
                VaultId.Parse(VaultIdValue),
                "90000000-0000-4000-8000-000000000001",
                1,
                new string('0', 64),
                TestContext.Current.CancellationToken);
            DatabaseObservation staged = Assert.IsType<DatabaseObservation>(
                await store.ObserveAsync(TestContext.Current.CancellationToken));
            Assert.Equal(InitializationPhase.Staged, staged.Phase);
            Assert.False(staged.ContainsCiphertext);

            NonceAllocation first = await store.AllocateNonceForTestAsync(
                VaultId.Parse(VaultIdValue),
                1,
                CryptoPurpose.ItemPayload,
                DeviceId.Parse(DeviceIdValue),
                new byte[] { 1, 2, 3, 4 },
                TestContext.Current.CancellationToken);
            Assert.Equal(1, first.Counter);

            await store.PopulateDefaultFoldersAsync(Defaults(), TestContext.Current.CancellationToken);
            Assert.True(Assert.IsType<DatabaseObservation>(
                await store.ObserveAsync(TestContext.Current.CancellationToken)).ContainsCiphertext);

            var reopened = new WindowsVaultSqliteStore(path);
            NonceAllocation second = await reopened.AllocateNonceForTestAsync(
                VaultId.Parse(VaultIdValue),
                1,
                CryptoPurpose.ItemPayload,
                DeviceId.Parse(DeviceIdValue),
                new byte[] { 9, 9, 9, 9 },
                TestContext.Current.CancellationToken);
            Assert.Equal(2, second.Counter);
            Assert.Equal(first.Prefix, second.Prefix);

            await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Pooling = false,
            }.ToString()))
            {
                await connection.OpenAsync(TestContext.Current.CancellationToken);
                await using SqliteCommand command = connection.CreateCommand();
                command.CommandText = "UPDATE nonce_state SET last_counter = $maximum;";
                command.Parameters.AddWithValue("$maximum", long.MaxValue);
                _ = await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }
            await Assert.ThrowsAsync<InvalidOperationException>(() => reopened.AllocateNonceAsync(
                VaultId.Parse(VaultIdValue),
                1,
                CryptoPurpose.ItemPayload,
                DeviceId.Parse(DeviceIdValue),
                TestContext.Current.CancellationToken));

            await reopened.PromoteReadyAsync(TestContext.Current.CancellationToken);
            DatabaseObservation ready = Assert.IsType<DatabaseObservation>(
                await reopened.ObserveAsync(TestContext.Current.CancellationToken));
            Assert.Equal(InitializationPhase.Ready, ready.Phase);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                reopened.PromoteReadyAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task EventReplayIsIdempotentButSequenceSubstitutionFailsClosed()
    {
        string directory = CaseDirectory("sqlite-events");
        try
        {
            var store = new WindowsVaultSqliteStore(Path.Combine(directory, "vault.db"));
            await store.InitializeSchemaAsync(TestContext.Current.CancellationToken);
            EncryptedItemRecord item = Item("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee");
            EncryptedVersionRecord version = Version("bbbbbbbb-cccc-4ddd-8eee-ffffffffffff", item.ItemId);
            EncryptedEventRecord syncEvent = Event(
                "cccccccc-dddd-4eee-8fff-000000000001",
                101,
                item.ItemId,
                version.VersionId,
                new string('a', 64));
            Assert.Equal(
                EventAppendResult.Inserted,
                await store.AppendItemAtomicallyAsync(item, version, syncEvent, TestContext.Current.CancellationToken));
            Assert.Equal(
                EventAppendResult.Duplicate,
                await store.AppendItemAtomicallyAsync(item, version, syncEvent, TestContext.Current.CancellationToken));

            EncryptedEventRecord substituted = syncEvent with
            {
                EventId = "dddddddd-eeee-4fff-8000-000000000002",
                BodyDigest = new string('b', 64),
            };
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                store.AppendItemAtomicallyAsync(item, version, substituted, TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                store.AppendItemAtomicallyAsync(
                    item,
                    version,
                    syncEvent with { LogicalTime = 102 },
                    TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                store.AppendItemAtomicallyAsync(
                    item,
                    version,
                    syncEvent with
                    {
                        EventId = "87654321-4321-4321-8321-cba987654321",
                        SourceSequence = 100,
                    },
                    TestContext.Current.CancellationToken));

            var folder = new EncryptedFolderRecord(
                "eeeeeeee-ffff-4000-8000-000000000003",
                VaultIdValue,
                null,
                "ffffffff-0000-4000-8000-000000000004",
                1,
                false,
                "cipher-folder");
            EncryptedVersionRecord folderVersion = Version(folder.CurrentVersionId, folder.FolderId, "FOLDER");
            EncryptedEventRecord folderEvent = Event(
                "12345678-1234-4234-8234-123456789abc",
                102,
                folder.FolderId,
                folderVersion.VersionId,
                new string('c', 64)) with { EntityKind = "FOLDER" };
            Assert.Equal(
                EventAppendResult.Inserted,
                await store.AppendFolderAtomicallyAsync(
                    folder,
                    folderVersion,
                    folderEvent,
                    TestContext.Current.CancellationToken));
            Assert.Equal(
                EventAppendResult.Duplicate,
                await store.AppendFolderAtomicallyAsync(
                    folder,
                    folderVersion,
                    folderEvent,
                    TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task FileGenerationCommitIsAtomicImmutableAndIdentityChecked()
    {
        string directory = CaseDirectory("sqlite-file-generation");
        try
        {
            var store = new WindowsVaultSqliteStore(Path.Combine(directory, "vault.db"));
            await store.InitializeSchemaAsync(TestContext.Current.CancellationToken);
            var manifest = new EncryptedFileManifestRecord(
                "12345678-1234-4234-8234-123456789abc",
                "eeeeeeee-ffff-4000-8000-000000000003",
                "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee",
                "ffffffff-0000-4000-8000-000000000004",
                1,
                1_048_576,
                1,
                "cipher-manifest",
                "cipher-file-key",
                false);
            var chunk = new EncryptedFileChunkRecord(
                manifest.FileId,
                manifest.GenerationId,
                0,
                "AQIDBAAAAAAAAAAA",
                48,
                new string('a', 64),
                $"{manifest.FileId}/{manifest.GenerationId}/chunk-0000000000000000000.bin");

            await store.CommitFileGenerationAsync(manifest, [chunk], TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<SqliteException>(() =>
                store.CommitFileGenerationAsync(manifest, [chunk], TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<InvalidDataException>(() => store.CommitFileGenerationAsync(
                manifest with { ManifestId = "23456789-2345-4234-8234-23456789abcd" },
                [chunk with { GenerationId = "cccccccc-dddd-4eee-8fff-000000000001" }],
                TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<InvalidDataException>(() => store.CommitFileGenerationAsync(
                manifest with { ManifestId = "3456789a-3456-4234-8234-3456789abcde", Committed = true },
                [chunk],
                TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DatabaseWalShmAndBlobContainNoPlaintextSentinels()
    {
        string directory = CaseDirectory("sentinel");
        try
        {
            string databasePath = Path.Combine(directory, "vault.db");
            var store = new WindowsVaultSqliteStore(databasePath);
            byte[] secret = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
            IReadOnlyList<(EncryptedFolderRecord Folder, EncryptedVersionRecord Version, EncryptedEventRecord Event)> defaults =
                EncryptedDefaults(secret);
            await store.CreateStagedAsync(
                VaultId.Parse(VaultIdValue),
                "90000000-0000-4000-8000-000000000001",
                1,
                new string('0', 64),
                defaults,
                TestContext.Current.CancellationToken);

            string sentinel = "CLIPSHARE-C2-PLAINTEXT-秘密-20260902";
            VaultCryptoContext context = new(
                CryptoPurpose.FileChunk,
                VaultId.Parse(VaultIdValue),
                DeviceId.Parse(DeviceIdValue),
                "eeeeeeee-ffff-4000-8000-000000000003",
                "chunk",
                1);
            byte[] dek = Enumerable.Repeat((byte)7, 32).ToArray();
            VaultCipherEnvelope envelope = VaultCryptography.EncryptFileChunk(
                dek,
                context,
                new NonceAllocation(new byte[] { 4, 3, 2, 1 }, 0),
                Encoding.UTF8.GetBytes(sentinel));
            byte[] cipherAndTag = DecodeBase64Url(envelope.CipherAndTag);
            var blobStore = new WindowsEncryptedBlobStore(Path.Combine(directory, "blobs"));
            _ = await blobStore.WriteChunkAtomicallyAsync(
                context.EntityId,
                "ffffffff-0000-4000-8000-000000000004",
                0,
                cipherAndTag,
                TestContext.Current.CancellationToken);
            await store.CheckpointAsync(TestContext.Current.CancellationToken);

            byte[] needle = Encoding.UTF8.GetBytes(sentinel);
            foreach (string file in Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
            {
                byte[] content = await File.ReadAllBytesAsync(file, TestContext.Current.CancellationToken);
                Assert.Equal(-1, content.AsSpan().IndexOf(needle));
            }

            foreach (string defaultName in DefaultFolderNames)
            {
                byte[] needleName = Encoding.UTF8.GetBytes(defaultName);
                byte[] database = await File.ReadAllBytesAsync(databasePath, TestContext.Current.CancellationToken);
                Assert.Equal(-1, database.AsSpan().IndexOf(needleName));
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static (EncryptedFolderRecord Folder, EncryptedVersionRecord Version, EncryptedEventRecord Event)[] Defaults() =>
        Enumerable.Range(1, 5).Select(index =>
        {
            string folderId = $"{index:00000000}-0000-4000-8000-{index:000000000000}";
            string versionId = $"{index + 10:00000000}-0000-4000-8000-{index:000000000000}";
            return (
                new EncryptedFolderRecord(folderId, VaultIdValue, null, versionId, 1, false, $"cipher-{index}"),
                Version(versionId, folderId, "FOLDER"),
                Event(
                    $"{index + 20:00000000}-0000-4000-8000-{index:000000000000}",
                    index,
                    folderId,
                    versionId,
                    new string('a', 64)) with { EntityKind = "FOLDER" });
        }).ToArray();

    private static List<(EncryptedFolderRecord Folder, EncryptedVersionRecord Version, EncryptedEventRecord Event)>
        EncryptedDefaults(byte[] secret)
    {
        var result = new List<(EncryptedFolderRecord, EncryptedVersionRecord, EncryptedEventRecord)>();
        for (var index = 0; index < DefaultFolderNames.Length; index++)
        {
            string folderId = $"{index + 1:00000000}-0000-4000-8000-{index + 1:000000000000}";
            string versionId = $"{index + 11:00000000}-0000-4000-8000-{index + 1:000000000000}";
            VaultCryptoContext context = new(
                CryptoPurpose.FolderMetadata,
                VaultId.Parse(VaultIdValue),
                DeviceId.Parse(DeviceIdValue),
                folderId,
                "metadata",
                1);
            VaultCipherEnvelope envelope = VaultCryptography.EncryptRecord(
                secret,
                context,
                new NonceAllocation(new byte[] { 1, 2, 3, 4 }, index + 1),
                Encoding.UTF8.GetBytes(DefaultFolderNames[index]));
            string encrypted = JsonSerializer.Serialize(envelope);
            result.Add((
                new EncryptedFolderRecord(folderId, VaultIdValue, null, versionId, 1, false, encrypted),
                Version(versionId, folderId, "FOLDER") with { EncryptedSnapshot = encrypted },
                Event(
                    $"{index + 21:00000000}-0000-4000-8000-{index + 1:000000000000}",
                    index + 1,
                    folderId,
                    versionId,
                    new string('a', 64)) with { EntityKind = "FOLDER", EncryptedBody = encrypted }));
        }

        return result;
    }

    private static EncryptedItemRecord Item(string itemId) => new(
        itemId,
        VaultIdValue,
        "00000001-0000-4000-8000-000000000001",
        "TEXT",
        "bbbbbbbb-cccc-4ddd-8eee-ffffffffffff",
        1,
        false,
        "cipher-metadata",
        "cipher-payload");

    private static EncryptedVersionRecord Version(string versionId, string entityId, string entityKind = "ITEM") =>
        new(versionId, entityId, entityKind, null, 1, "cipher-snapshot");

    private static EncryptedEventRecord Event(
        string eventId,
        long sequence,
        string entityId,
        string versionId,
        string digest) => new(
            eventId,
            DeviceIdValue,
            sequence,
            sequence,
            entityId,
            "ITEM",
            "CREATE",
            null,
            versionId,
            1,
            "cipher-event",
            digest,
            "APPLIED");

    private static byte[] DecodeBase64Url(string value) =>
        Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/').PadRight((value.Length + 3) / 4 * 4, '='));

    private static string CaseDirectory(string name)
    {
        string? root = Environment.GetEnvironmentVariable("CLIPSHARE_TEST_OUTPUT");
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            throw new InvalidOperationException("CLIPSHARE_TEST_OUTPUT must point to an existing sandbox directory.");
        }

        string directory = Path.Combine(root, $"c2-{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }
}
