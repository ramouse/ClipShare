namespace ClipShare.Windows.Infrastructure;

using System.Security.Cryptography;
using ClipShare.Windows.Vault;
using Microsoft.Data.Sqlite;

public sealed record EncryptedFolderRecord(
    string FolderId,
    string VaultId,
    string? ParentId,
    string CurrentVersionId,
    long KeyEpoch,
    bool Tombstone,
    string EncryptedMetadata);

public sealed record EncryptedItemRecord(
    string ItemId,
    string VaultId,
    string FolderId,
    string ContentType,
    string CurrentVersionId,
    long KeyEpoch,
    bool Tombstone,
    string EncryptedMetadata,
    string? EncryptedPayload);

public sealed record EncryptedVersionRecord(
    string VersionId,
    string EntityId,
    string EntityKind,
    string? BaseVersionId,
    long KeyEpoch,
    string EncryptedSnapshot);

public sealed record EncryptedEventRecord(
    string EventId,
    string SourceDeviceId,
    long SourceSequence,
    long LogicalTime,
    string EntityId,
    string EntityKind,
    string Operation,
    string? BaseVersionId,
    string NewVersionId,
    long KeyEpoch,
    string EncryptedBody,
    string BodyDigest,
    string Disposition);

public sealed record EncryptedFileManifestRecord(
    string ManifestId,
    string FileId,
    string ItemId,
    string GenerationId,
    long KeyEpoch,
    long ChunkSize,
    long ChunkCount,
    string EncryptedManifest,
    string WrappedFileKey,
    bool Committed);

public sealed record EncryptedFileChunkRecord(
    string FileId,
    string GenerationId,
    long ChunkIndex,
    string Nonce,
    long CiphertextBytes,
    string CiphertextSha256,
    string RelativePath);

public enum EventAppendResult
{
    Inserted,
    Duplicate,
}

public sealed class WindowsVaultSqliteStore
{
    private const long MaximumNonceCounter = long.MaxValue;
    private const long MaximumPlaintextChunkBytes = 1_048_576;
    private const long MaximumCiphertextChunkBytes = MaximumPlaintextChunkBytes + 16;
    private const int SchemaVersion = 1;
    private readonly string databasePath;
    private readonly string connectionString;

    public WindowsVaultSqliteStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        this.databasePath = Path.GetFullPath(databasePath);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = this.databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            ForeignKeys = true,
            DefaultTimeout = 5,
        }.ToString();
    }

    public bool Exists => File.Exists(databasePath);

    public async Task InitializeSchemaAsync(CancellationToken cancellationToken = default)
    {
        string? parent = Path.GetDirectoryName(databasePath);
        if (parent is null)
        {
            throw new InvalidOperationException("Vault database path has no parent directory.");
        }

        Directory.CreateDirectory(parent);
        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, null, "PRAGMA journal_mode=WAL;", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, null, "PRAGMA synchronous=FULL;", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, null, SchemaSql, cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, null, $"PRAGMA user_version={SchemaVersion};", cancellationToken).ConfigureAwait(false);
    }

    public async Task<DatabaseObservation?> ObserveAsync(CancellationToken cancellationToken = default)
    {
        if (!Exists)
        {
            return null;
        }

        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT vault_id, initialization_id, current_write_epoch, wrapper_digest,
                   initialization_state,
                   (SELECT COUNT(*) FROM folders) +
                   (SELECT COUNT(*) FROM items) +
                   (SELECT COUNT(*) FROM item_versions) +
                   (SELECT COUNT(*) FROM encrypted_file_manifests) +
                   (SELECT COUNT(*) FROM sync_events) +
                   (SELECT COUNT(*) FROM tombstones)
            FROM vault_state
            WHERE singleton_id = 1;
            """;
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException("Vault database exists without initialization state.");
        }

        string phaseValue = reader.GetString(4);
        InitializationPhase phase = phaseValue switch
        {
            "STAGED" => InitializationPhase.Staged,
            "READY" => InitializationPhase.Ready,
            _ => throw new InvalidDataException("Vault database has an invalid initialization state."),
        };
        return new DatabaseObservation(
            VaultId.Parse(reader.GetString(0)),
            reader.GetString(1),
            reader.GetInt64(2),
            reader.GetString(3),
            phase,
            reader.GetInt64(5) > 0);
    }

    public async Task CreateStagedAsync(
        VaultId vaultId,
        string initializationId,
        long currentWriteEpoch,
        string wrapperDigest,
        IReadOnlyList<(EncryptedFolderRecord Folder, EncryptedVersionRecord Version, EncryptedEventRecord Event)> defaults,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        await CreateEmptyStagedAsync(
            vaultId,
            initializationId,
            currentWriteEpoch,
            wrapperDigest,
            cancellationToken).ConfigureAwait(false);
        await PopulateDefaultFoldersAsync(defaults, cancellationToken).ConfigureAwait(false);
    }

    public async Task CreateEmptyStagedAsync(
        VaultId vaultId,
        string initializationId,
        long currentWriteEpoch,
        string wrapperDigest,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(currentWriteEpoch);
        await InitializeSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand stateCommand = connection.CreateCommand();
        stateCommand.CommandText =
            """
            INSERT INTO vault_state (
                singleton_id, vault_id, initialization_id, format_version,
                current_write_epoch, initialization_state, wrapper_digest)
            VALUES (1, $vaultId, $initializationId, 1, $epoch, 'STAGED', $digest);
            """;
        stateCommand.Parameters.AddWithValue("$vaultId", vaultId.Value);
        stateCommand.Parameters.AddWithValue("$initializationId", initializationId);
        stateCommand.Parameters.AddWithValue("$epoch", currentWriteEpoch);
        stateCommand.Parameters.AddWithValue("$digest", wrapperDigest);
        _ = await stateCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task PopulateDefaultFoldersAsync(
        IReadOnlyList<(EncryptedFolderRecord Folder, EncryptedVersionRecord Version, EncryptedEventRecord Event)> defaults,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        if (defaults.Count != 5)
        {
            throw new ArgumentException("Vault initialization requires exactly five default folders.", nameof(defaults));
        }

        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction = connection.BeginTransaction();
        try
        {
            await EnsureDatabaseStagedAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            long existingCiphertext = await CountCiphertextAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            if (existingCiphertext != 0)
            {
                throw new InvalidOperationException("A staged Vault with existing ciphertext cannot be repopulated.");
            }
            foreach ((EncryptedFolderRecord folder, EncryptedVersionRecord version, EncryptedEventRecord syncEvent) in defaults)
            {
                ValidateFolderTuple(folder, version, syncEvent);
                await InsertFolderAsync(connection, transaction, folder, cancellationToken).ConfigureAwait(false);
                await InsertVersionAsync(connection, transaction, version, cancellationToken).ConfigureAwait(false);
                await InsertEventAsync(connection, transaction, syncEvent, cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task PromoteReadyAsync(CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "UPDATE vault_state SET initialization_state = 'READY' WHERE singleton_id = 1 AND initialization_state = 'STAGED';";
        int affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (affected != 1)
        {
            throw new InvalidOperationException("Vault database cannot be promoted from its current state.");
        }
    }

    public async Task<NonceAllocation> AllocateNonceAsync(
        VaultId vaultId,
        long keyEpoch,
        CryptoPurpose purpose,
        DeviceId originDeviceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(keyEpoch);
        byte[] candidatePrefix = RandomNumberGenerator.GetBytes(4);
        try
        {
            return await AllocateNonceForTestAsync(
                vaultId,
                keyEpoch,
                purpose,
                originDeviceId,
                candidatePrefix,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(candidatePrefix);
        }
    }

    internal async Task<NonceAllocation> AllocateNonceForTestAsync(
        VaultId vaultId,
        long keyEpoch,
        CryptoPurpose purpose,
        DeviceId originDeviceId,
        ReadOnlyMemory<byte> candidatePrefix,
        CancellationToken cancellationToken = default)
    {
        if (candidatePrefix.Length != 4)
        {
            throw new ArgumentException("Nonce prefixes are exactly four bytes.", nameof(candidatePrefix));
        }

        if (purpose == CryptoPurpose.FileChunk)
        {
            throw new ArgumentException("File chunks use generation-scoped counters.", nameof(purpose));
        }

        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction = connection.BeginTransaction();
        try
        {
            await using (SqliteCommand createCommand = connection.CreateCommand())
            {
                createCommand.Transaction = transaction;
                createCommand.CommandText =
                    """
                    INSERT OR IGNORE INTO nonce_state (
                        vault_id, key_epoch, purpose, origin_device_id, prefix, last_counter)
                    VALUES ($vaultId, $epoch, $purpose, $deviceId, $prefix, 0);
                    """;
                createCommand.Parameters.AddWithValue("$vaultId", vaultId.Value);
                createCommand.Parameters.AddWithValue("$epoch", keyEpoch);
                createCommand.Parameters.AddWithValue("$purpose", purpose.ToWireValue());
                createCommand.Parameters.AddWithValue("$deviceId", originDeviceId.Value);
                createCommand.Parameters.Add("$prefix", SqliteType.Blob).Value = candidatePrefix.ToArray();
                _ = await createCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            byte[] prefix;
            long counter;
            await using (SqliteCommand incrementCommand = connection.CreateCommand())
            {
                incrementCommand.Transaction = transaction;
                incrementCommand.CommandText =
                    """
                    UPDATE nonce_state
                    SET last_counter = last_counter + 1
                    WHERE vault_id = $vaultId AND key_epoch = $epoch AND purpose = $purpose
                      AND origin_device_id = $deviceId AND last_counter < $maximum
                    RETURNING prefix, last_counter;
                    """;
                incrementCommand.Parameters.AddWithValue("$vaultId", vaultId.Value);
                incrementCommand.Parameters.AddWithValue("$epoch", keyEpoch);
                incrementCommand.Parameters.AddWithValue("$purpose", purpose.ToWireValue());
                incrementCommand.Parameters.AddWithValue("$deviceId", originDeviceId.Value);
                incrementCommand.Parameters.AddWithValue("$maximum", MaximumNonceCounter);
                await using SqliteDataReader reader = await incrementCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    throw new InvalidOperationException("Nonce counter is exhausted or unavailable; rotate the epoch.");
                }

                prefix = (byte[])reader.GetValue(0);
                counter = reader.GetInt64(1);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new NonceAllocation(prefix, counter);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<EventAppendResult> AppendFolderAtomicallyAsync(
        EncryptedFolderRecord folder,
        EncryptedVersionRecord version,
        EncryptedEventRecord syncEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(syncEvent);
        ValidateFolderTuple(folder, version, syncEvent);
        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction = connection.BeginTransaction();
        try
        {
            EventAppendResult duplicate = await ExistingEventDispositionAsync(
                connection,
                transaction,
                syncEvent,
                cancellationToken).ConfigureAwait(false);
            if (duplicate == EventAppendResult.Duplicate)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return duplicate;
            }

            await InsertFolderAsync(connection, transaction, folder, cancellationToken).ConfigureAwait(false);
            await InsertVersionAsync(connection, transaction, version, cancellationToken).ConfigureAwait(false);
            await InsertEventAsync(connection, transaction, syncEvent, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return EventAppendResult.Inserted;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<EventAppendResult> AppendItemAtomicallyAsync(
        EncryptedItemRecord item,
        EncryptedVersionRecord version,
        EncryptedEventRecord syncEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(syncEvent);
        ValidateItemTuple(item, version, syncEvent);
        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction = connection.BeginTransaction();
        try
        {
            EventAppendResult duplicate = await ExistingEventDispositionAsync(
                connection,
                transaction,
                syncEvent,
                cancellationToken).ConfigureAwait(false);
            if (duplicate == EventAppendResult.Duplicate)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return duplicate;
            }

            await InsertItemAsync(connection, transaction, item, cancellationToken).ConfigureAwait(false);
            await InsertVersionAsync(connection, transaction, version, cancellationToken).ConfigureAwait(false);
            await InsertEventAsync(connection, transaction, syncEvent, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return EventAppendResult.Inserted;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task CommitFileGenerationAsync(
        EncryptedFileManifestRecord manifest,
        IReadOnlyList<EncryptedFileChunkRecord> chunks,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(chunks);
        ValidateFileGeneration(manifest, chunks);

        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction = connection.BeginTransaction();
        try
        {
            await using (SqliteCommand manifestCommand = connection.CreateCommand())
            {
                manifestCommand.Transaction = transaction;
                manifestCommand.CommandText =
                    """
                    INSERT INTO encrypted_file_manifests (
                        manifest_id, file_id, item_id, generation_id, key_epoch,
                        chunk_size, chunk_count, encrypted_manifest, wrapped_file_key, committed)
                    VALUES ($manifestId, $fileId, $itemId, $generationId, $epoch,
                            $chunkSize, $chunkCount, $manifest, $fileKey, 0);
                    """;
                manifestCommand.Parameters.AddWithValue("$manifestId", manifest.ManifestId);
                manifestCommand.Parameters.AddWithValue("$fileId", manifest.FileId);
                manifestCommand.Parameters.AddWithValue("$itemId", manifest.ItemId);
                manifestCommand.Parameters.AddWithValue("$generationId", manifest.GenerationId);
                manifestCommand.Parameters.AddWithValue("$epoch", manifest.KeyEpoch);
                manifestCommand.Parameters.AddWithValue("$chunkSize", manifest.ChunkSize);
                manifestCommand.Parameters.AddWithValue("$chunkCount", manifest.ChunkCount);
                manifestCommand.Parameters.AddWithValue("$manifest", manifest.EncryptedManifest);
                manifestCommand.Parameters.AddWithValue("$fileKey", manifest.WrappedFileKey);
                _ = await manifestCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            foreach (EncryptedFileChunkRecord chunk in chunks)
            {
                await InsertFileChunkAsync(connection, transaction, chunk, cancellationToken).ConfigureAwait(false);
            }

            await using (SqliteCommand commitCommand = connection.CreateCommand())
            {
                commitCommand.Transaction = transaction;
                commitCommand.CommandText =
                    "UPDATE encrypted_file_manifests SET committed = 1 WHERE manifest_id = $manifestId AND committed = 0;";
                commitCommand.Parameters.AddWithValue("$manifestId", manifest.ManifestId);
                if (await commitCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                {
                    throw new InvalidOperationException("File manifest could not be committed.");
                }
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task CheckpointAsync(CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, null, "PRAGMA wal_checkpoint(TRUNCATE);", cancellationToken).ConfigureAwait(false);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, null, "PRAGMA busy_timeout=5000;", cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, null, "PRAGMA foreign_keys=ON;", cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<EventAppendResult> ExistingEventDispositionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        EncryptedEventRecord syncEvent,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT event_id, source_device_id, source_sequence, logical_time, entity_id,
                   entity_kind, operation, base_version_id, new_version_id, key_epoch,
                   encrypted_body, body_digest, disposition
            FROM sync_events
            WHERE event_id = $eventId
               OR (source_device_id = $deviceId AND source_sequence = $sequence);
            """;
        command.Parameters.AddWithValue("$eventId", syncEvent.EventId);
        command.Parameters.AddWithValue("$deviceId", syncEvent.SourceDeviceId);
        command.Parameters.AddWithValue("$sequence", syncEvent.SourceSequence);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var existing = new EncryptedEventRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt64(2),
                reader.GetInt64(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.GetString(8),
                reader.GetInt64(9),
                reader.GetString(10),
                reader.GetString(11),
                reader.GetString(12));
            return existing == syncEvent
                ? EventAppendResult.Duplicate
                : throw new InvalidDataException("Event ID or source sequence was replayed with different content.");
        }

        await reader.DisposeAsync().ConfigureAwait(false);
        await using SqliteCommand sequenceCommand = connection.CreateCommand();
        sequenceCommand.Transaction = transaction;
        sequenceCommand.CommandText = "SELECT MAX(source_sequence) FROM sync_events WHERE source_device_id = $deviceId;";
        sequenceCommand.Parameters.AddWithValue("$deviceId", syncEvent.SourceDeviceId);
        object? highestValue = await sequenceCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (highestValue is not (null or DBNull) &&
            Convert.ToInt64(highestValue, System.Globalization.CultureInfo.InvariantCulture) >= syncEvent.SourceSequence)
        {
            throw new InvalidDataException("An older source sequence cannot be appended after a newer event.");
        }

        return EventAppendResult.Inserted;
    }

    private static async Task<long> CountCiphertextAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT
              (SELECT COUNT(*) FROM folders) +
              (SELECT COUNT(*) FROM items) +
              (SELECT COUNT(*) FROM item_versions) +
              (SELECT COUNT(*) FROM encrypted_file_manifests) +
              (SELECT COUNT(*) FROM sync_events) +
              (SELECT COUNT(*) FROM tombstones);
            """;
        object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task EnsureDatabaseStagedAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT initialization_state FROM vault_state WHERE singleton_id = 1;";
        object? state = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (!string.Equals(Convert.ToString(state, System.Globalization.CultureInfo.InvariantCulture), "STAGED", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Only a STAGED Vault database can be populated.");
        }
    }

    private static void ValidateFolderTuple(
        EncryptedFolderRecord folder,
        EncryptedVersionRecord version,
        EncryptedEventRecord syncEvent)
    {
        if (!string.Equals(folder.CurrentVersionId, version.VersionId, StringComparison.Ordinal) ||
            !string.Equals(folder.FolderId, version.EntityId, StringComparison.Ordinal) ||
            !string.Equals(version.EntityKind, "FOLDER", StringComparison.Ordinal) ||
            !string.Equals(syncEvent.EntityId, folder.FolderId, StringComparison.Ordinal) ||
            !string.Equals(syncEvent.EntityKind, "FOLDER", StringComparison.Ordinal) ||
            !string.Equals(syncEvent.NewVersionId, version.VersionId, StringComparison.Ordinal) ||
            folder.KeyEpoch != version.KeyEpoch || version.KeyEpoch != syncEvent.KeyEpoch)
        {
            throw new InvalidDataException("Folder, version, and event identities must form one atomic update.");
        }
    }

    private static void ValidateItemTuple(
        EncryptedItemRecord item,
        EncryptedVersionRecord version,
        EncryptedEventRecord syncEvent)
    {
        if (!string.Equals(item.CurrentVersionId, version.VersionId, StringComparison.Ordinal) ||
            !string.Equals(item.ItemId, version.EntityId, StringComparison.Ordinal) ||
            !string.Equals(version.EntityKind, "ITEM", StringComparison.Ordinal) ||
            !string.Equals(syncEvent.EntityId, item.ItemId, StringComparison.Ordinal) ||
            !string.Equals(syncEvent.EntityKind, "ITEM", StringComparison.Ordinal) ||
            !string.Equals(syncEvent.NewVersionId, version.VersionId, StringComparison.Ordinal) ||
            item.KeyEpoch != version.KeyEpoch || version.KeyEpoch != syncEvent.KeyEpoch)
        {
            throw new InvalidDataException("Item, version, and event identities must form one atomic update.");
        }
    }

    private static void ValidateFileGeneration(
        EncryptedFileManifestRecord manifest,
        IReadOnlyList<EncryptedFileChunkRecord> chunks)
    {
        _ = ItemId.Parse(manifest.ManifestId);
        _ = ItemId.Parse(manifest.FileId);
        _ = ItemId.Parse(manifest.ItemId);
        _ = ItemId.Parse(manifest.GenerationId);
        if (manifest.Committed || manifest.KeyEpoch <= 0 ||
            manifest.ChunkSize is < 1 or > MaximumPlaintextChunkBytes ||
            manifest.ChunkCount != chunks.Count ||
            string.IsNullOrEmpty(manifest.EncryptedManifest) ||
            string.IsNullOrEmpty(manifest.WrappedFileKey))
        {
            throw new InvalidDataException("Invalid encrypted file manifest.");
        }

        for (var index = 0; index < chunks.Count; index++)
        {
            EncryptedFileChunkRecord chunk = chunks[index];
            string expectedPath = $"{manifest.FileId}/{manifest.GenerationId}/chunk-{index:D19}.bin";
            if (!string.Equals(chunk.FileId, manifest.FileId, StringComparison.Ordinal) ||
                !string.Equals(chunk.GenerationId, manifest.GenerationId, StringComparison.Ordinal) ||
                chunk.ChunkIndex != index ||
                chunk.Nonce.Length != 16 ||
                chunk.Nonce.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')) ||
                chunk.CiphertextBytes is < 16 or > MaximumCiphertextChunkBytes ||
                chunk.CiphertextSha256.Length != 64 ||
                chunk.CiphertextSha256.Any(character => !Uri.IsHexDigit(character) || character is >= 'A' and <= 'F') ||
                !string.Equals(chunk.RelativePath, expectedPath, StringComparison.Ordinal))
            {
                throw new InvalidDataException("File chunks must match the manifest and be structurally valid.");
            }
        }
    }

    private static async Task InsertFolderAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        EncryptedFolderRecord folder,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO folders (
                folder_id, vault_id, parent_id, current_version_id, key_epoch, tombstone, encrypted_metadata)
            VALUES ($id, $vaultId, $parentId, $versionId, $epoch, $tombstone, $metadata);
            """;
        command.Parameters.AddWithValue("$id", folder.FolderId);
        command.Parameters.AddWithValue("$vaultId", folder.VaultId);
        command.Parameters.AddWithValue("$parentId", (object?)folder.ParentId ?? DBNull.Value);
        command.Parameters.AddWithValue("$versionId", folder.CurrentVersionId);
        command.Parameters.AddWithValue("$epoch", folder.KeyEpoch);
        command.Parameters.AddWithValue("$tombstone", folder.Tombstone ? 1 : 0);
        command.Parameters.AddWithValue("$metadata", folder.EncryptedMetadata);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertItemAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        EncryptedItemRecord item,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO items (
                item_id, vault_id, folder_id, content_type, current_version_id,
                key_epoch, tombstone, encrypted_metadata, encrypted_payload)
            VALUES ($id, $vaultId, $folderId, $contentType, $versionId,
                    $epoch, $tombstone, $metadata, $payload);
            """;
        command.Parameters.AddWithValue("$id", item.ItemId);
        command.Parameters.AddWithValue("$vaultId", item.VaultId);
        command.Parameters.AddWithValue("$folderId", item.FolderId);
        command.Parameters.AddWithValue("$contentType", item.ContentType);
        command.Parameters.AddWithValue("$versionId", item.CurrentVersionId);
        command.Parameters.AddWithValue("$epoch", item.KeyEpoch);
        command.Parameters.AddWithValue("$tombstone", item.Tombstone ? 1 : 0);
        command.Parameters.AddWithValue("$metadata", item.EncryptedMetadata);
        command.Parameters.AddWithValue("$payload", (object?)item.EncryptedPayload ?? DBNull.Value);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertVersionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        EncryptedVersionRecord version,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO item_versions (
                version_id, entity_id, entity_kind, base_version_id, key_epoch, encrypted_snapshot)
            VALUES ($id, $entityId, $kind, $baseId, $epoch, $snapshot);
            """;
        command.Parameters.AddWithValue("$id", version.VersionId);
        command.Parameters.AddWithValue("$entityId", version.EntityId);
        command.Parameters.AddWithValue("$kind", version.EntityKind);
        command.Parameters.AddWithValue("$baseId", (object?)version.BaseVersionId ?? DBNull.Value);
        command.Parameters.AddWithValue("$epoch", version.KeyEpoch);
        command.Parameters.AddWithValue("$snapshot", version.EncryptedSnapshot);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertFileChunkAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        EncryptedFileChunkRecord chunk,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO encrypted_file_chunks (
                file_id, generation_id, chunk_index, nonce,
                ciphertext_bytes, ciphertext_sha256, relative_path)
            VALUES ($fileId, $generationId, $chunkIndex, $nonce,
                    $ciphertextBytes, $digest, $relativePath);
            """;
        command.Parameters.AddWithValue("$fileId", chunk.FileId);
        command.Parameters.AddWithValue("$generationId", chunk.GenerationId);
        command.Parameters.AddWithValue("$chunkIndex", chunk.ChunkIndex);
        command.Parameters.AddWithValue("$nonce", chunk.Nonce);
        command.Parameters.AddWithValue("$ciphertextBytes", chunk.CiphertextBytes);
        command.Parameters.AddWithValue("$digest", chunk.CiphertextSha256);
        command.Parameters.AddWithValue("$relativePath", chunk.RelativePath);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertEventAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        EncryptedEventRecord syncEvent,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO sync_events (
                event_id, source_device_id, source_sequence, logical_time, entity_id,
                entity_kind, operation, base_version_id, new_version_id, key_epoch,
                encrypted_body, body_digest, disposition)
            VALUES ($id, $deviceId, $sequence, $logicalTime, $entityId,
                    $kind, $operation, $baseId, $newId, $epoch,
                    $body, $digest, $disposition);
            """;
        command.Parameters.AddWithValue("$id", syncEvent.EventId);
        command.Parameters.AddWithValue("$deviceId", syncEvent.SourceDeviceId);
        command.Parameters.AddWithValue("$sequence", syncEvent.SourceSequence);
        command.Parameters.AddWithValue("$logicalTime", syncEvent.LogicalTime);
        command.Parameters.AddWithValue("$entityId", syncEvent.EntityId);
        command.Parameters.AddWithValue("$kind", syncEvent.EntityKind);
        command.Parameters.AddWithValue("$operation", syncEvent.Operation);
        command.Parameters.AddWithValue("$baseId", (object?)syncEvent.BaseVersionId ?? DBNull.Value);
        command.Parameters.AddWithValue("$newId", syncEvent.NewVersionId);
        command.Parameters.AddWithValue("$epoch", syncEvent.KeyEpoch);
        command.Parameters.AddWithValue("$body", syncEvent.EncryptedBody);
        command.Parameters.AddWithValue("$digest", syncEvent.BodyDigest);
        command.Parameters.AddWithValue("$disposition", syncEvent.Disposition);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private const string SchemaSql =
        """
        CREATE TABLE IF NOT EXISTS vault_state (
            singleton_id INTEGER PRIMARY KEY CHECK(singleton_id = 1),
            vault_id TEXT NOT NULL,
            initialization_id TEXT NOT NULL,
            format_version INTEGER NOT NULL CHECK(format_version = 1),
            current_write_epoch INTEGER NOT NULL CHECK(current_write_epoch > 0),
            initialization_state TEXT NOT NULL CHECK(initialization_state IN ('STAGED', 'READY')),
            wrapper_digest TEXT NOT NULL CHECK(length(wrapper_digest) = 64)
        ) STRICT;
        CREATE TABLE IF NOT EXISTS folders (
            folder_id TEXT PRIMARY KEY,
            vault_id TEXT NOT NULL,
            parent_id TEXT NULL,
            current_version_id TEXT NOT NULL UNIQUE,
            key_epoch INTEGER NOT NULL CHECK(key_epoch > 0),
            tombstone INTEGER NOT NULL CHECK(tombstone IN (0, 1)),
            encrypted_metadata TEXT NOT NULL
        ) STRICT;
        CREATE INDEX IF NOT EXISTS idx_folders_parent ON folders(parent_id);
        CREATE TABLE IF NOT EXISTS items (
            item_id TEXT PRIMARY KEY,
            vault_id TEXT NOT NULL,
            folder_id TEXT NOT NULL,
            content_type TEXT NOT NULL CHECK(content_type IN ('TEXT', 'URL', 'FILE')),
            current_version_id TEXT NOT NULL UNIQUE,
            key_epoch INTEGER NOT NULL CHECK(key_epoch > 0),
            tombstone INTEGER NOT NULL CHECK(tombstone IN (0, 1)),
            encrypted_metadata TEXT NOT NULL,
            encrypted_payload TEXT NULL
        ) STRICT;
        CREATE INDEX IF NOT EXISTS idx_items_folder ON items(folder_id);
        CREATE TABLE IF NOT EXISTS item_versions (
            version_id TEXT PRIMARY KEY,
            entity_id TEXT NOT NULL,
            entity_kind TEXT NOT NULL CHECK(entity_kind IN ('FOLDER', 'ITEM')),
            base_version_id TEXT NULL,
            key_epoch INTEGER NOT NULL CHECK(key_epoch > 0),
            encrypted_snapshot TEXT NOT NULL
        ) STRICT;
        CREATE INDEX IF NOT EXISTS idx_versions_entity ON item_versions(entity_id);
        CREATE INDEX IF NOT EXISTS idx_versions_base ON item_versions(base_version_id);
        CREATE TABLE IF NOT EXISTS encrypted_file_manifests (
            manifest_id TEXT PRIMARY KEY,
            file_id TEXT NOT NULL,
            item_id TEXT NOT NULL,
            generation_id TEXT NOT NULL,
            key_epoch INTEGER NOT NULL CHECK(key_epoch > 0),
            chunk_size INTEGER NOT NULL CHECK(chunk_size > 0 AND chunk_size <= 1048576),
            chunk_count INTEGER NOT NULL CHECK(chunk_count >= 0),
            encrypted_manifest TEXT NOT NULL,
            wrapped_file_key TEXT NOT NULL,
            committed INTEGER NOT NULL CHECK(committed IN (0, 1)),
            UNIQUE(file_id, generation_id)
        ) STRICT;
        CREATE INDEX IF NOT EXISTS idx_manifests_item ON encrypted_file_manifests(item_id);
        CREATE TABLE IF NOT EXISTS encrypted_file_chunks (
            file_id TEXT NOT NULL,
            generation_id TEXT NOT NULL,
            chunk_index INTEGER NOT NULL CHECK(chunk_index >= 0),
            nonce TEXT NOT NULL,
            ciphertext_bytes INTEGER NOT NULL CHECK(ciphertext_bytes >= 16),
            ciphertext_sha256 TEXT NOT NULL CHECK(length(ciphertext_sha256) = 64),
            relative_path TEXT NOT NULL,
            PRIMARY KEY(file_id, generation_id, chunk_index),
            UNIQUE(file_id, generation_id, nonce)
        ) STRICT;
        CREATE TABLE IF NOT EXISTS sync_events (
            event_id TEXT PRIMARY KEY,
            source_device_id TEXT NOT NULL,
            source_sequence INTEGER NOT NULL CHECK(source_sequence > 0),
            logical_time INTEGER NOT NULL CHECK(logical_time > 0),
            entity_id TEXT NOT NULL,
            entity_kind TEXT NOT NULL CHECK(entity_kind IN ('FOLDER', 'ITEM')),
            operation TEXT NOT NULL,
            base_version_id TEXT NULL,
            new_version_id TEXT NOT NULL,
            key_epoch INTEGER NOT NULL CHECK(key_epoch > 0),
            encrypted_body TEXT NOT NULL,
            body_digest TEXT NOT NULL CHECK(length(body_digest) = 64),
            disposition TEXT NOT NULL,
            UNIQUE(source_device_id, source_sequence)
        ) STRICT;
        CREATE INDEX IF NOT EXISTS idx_events_entity ON sync_events(entity_id);
        CREATE TABLE IF NOT EXISTS device_key_wrappers (
            device_id TEXT NOT NULL,
            key_epoch INTEGER NOT NULL CHECK(key_epoch > 0),
            wrapper TEXT NOT NULL,
            revoked INTEGER NOT NULL CHECK(revoked IN (0, 1)),
            PRIMARY KEY(device_id, key_epoch)
        ) STRICT;
        CREATE TABLE IF NOT EXISTS tombstones (
            entity_id TEXT PRIMARY KEY,
            entity_kind TEXT NOT NULL CHECK(entity_kind IN ('FOLDER', 'ITEM')),
            version_id TEXT NOT NULL,
            key_epoch INTEGER NOT NULL CHECK(key_epoch > 0),
            encrypted_deleted_at TEXT NOT NULL,
            purge_after_bucket INTEGER NOT NULL CHECK(purge_after_bucket >= 0)
        ) STRICT;
        CREATE INDEX IF NOT EXISTS idx_tombstones_purge ON tombstones(purge_after_bucket);
        CREATE TABLE IF NOT EXISTS nonce_state (
            vault_id TEXT NOT NULL,
            key_epoch INTEGER NOT NULL CHECK(key_epoch > 0),
            purpose TEXT NOT NULL,
            origin_device_id TEXT NOT NULL,
            prefix BLOB NOT NULL CHECK(length(prefix) = 4),
            last_counter INTEGER NOT NULL CHECK(last_counter >= 0),
            PRIMARY KEY(vault_id, key_epoch, purpose, origin_device_id),
            UNIQUE(vault_id, key_epoch, purpose, origin_device_id, last_counter)
        ) STRICT;
        CREATE TABLE IF NOT EXISTS migration_state (
            migration_id TEXT PRIMARY KEY,
            from_version INTEGER NOT NULL,
            to_version INTEGER NOT NULL,
            state TEXT NOT NULL,
            encrypted_rollback_path TEXT NULL
        ) STRICT;
        """;
}
