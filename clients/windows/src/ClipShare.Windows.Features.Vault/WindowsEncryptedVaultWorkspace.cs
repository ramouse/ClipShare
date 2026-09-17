namespace ClipShare.Windows.Features.Vault;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClipShare.Windows.Application;
using ClipShare.Windows.Infrastructure;
using ClipShare.Windows.Platform;
using ClipShare.Windows.Vault;

public sealed class WindowsEncryptedVaultWorkspace : IVaultWorkspace
{
    private const long InitialEpoch = 1;
    private const int SearchNameLimit = 50_000;
    private const int SearchBodyItemLimit = 10_000;
    private const long SearchBodyBytesLimit = 64L * 1024 * 1024;
    private const int SearchSingleBodyBytesLimit = 256 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = false,
    };

    private readonly WindowsVaultSqliteStore database;
    private readonly WindowsPlatformWrapperStore wrapperStore;
    private readonly WindowsEncryptedBlobStore blobStore;
    private readonly WindowsLocalDeviceIdentityStore identityStore;
    private readonly IWindowsVaultSecretProtector protector;
    private readonly ILocalFilePort localFiles;
    private readonly TimeProvider timeProvider;
    private readonly SemaphoreSlim mutationLock = new(1, 1);
    private readonly Dictionary<long, byte[]> epochSecrets = [];
    private VaultId? vaultId;
    private DeviceId? deviceId;
    private long sourceSequence;
    private bool initialized;
    private bool disposed;

    public WindowsEncryptedVaultWorkspace(
        string rootDirectory,
        ILocalFilePort localFiles,
        IWindowsVaultSecretProtector? protector = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        string root = Path.GetFullPath(rootDirectory);
        Directory.CreateDirectory(root);
        database = new WindowsVaultSqliteStore(Path.Combine(root, "vault.db"));
        wrapperStore = new WindowsPlatformWrapperStore(Path.Combine(root, "wrappers"));
        blobStore = new WindowsEncryptedBlobStore(Path.Combine(root, "blobs"));
        identityStore = new WindowsLocalDeviceIdentityStore(Path.Combine(root, "identity"));
        this.localFiles = localFiles ?? throw new ArgumentNullException(nameof(localFiles));
        this.protector = protector ?? new DpapiWindowsVaultSecretProtector();
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await mutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (initialized)
            {
                return;
            }

            deviceId = await identityStore.ReadOrCreateAsync(cancellationToken).ConfigureAwait(false);
            DatabaseObservation? databaseObservation = await database.ObserveAsync(cancellationToken).ConfigureAwait(false);
            if (databaseObservation is null)
            {
                await InitializeWithoutDatabaseAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await ResumeDatabaseAsync(databaseObservation, cancellationToken).ConfigureAwait(false);
            }

            sourceSequence = await database.HighestSourceSequenceAsync(RequireDeviceId(), cancellationToken)
                .ConfigureAwait(false);
            initialized = true;
        }
        catch
        {
            ClearSecrets();
            throw;
        }
        finally
        {
            _ = mutationLock.Release();
        }
    }

    public async Task<VaultSnapshot> LoadAsync(
        VaultSection section,
        string? query,
        CancellationToken cancellationToken)
    {
        EnsureInitialized();
        IReadOnlyList<FolderState> folders = await ReadFolderStatesAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<ItemState> items = await ReadItemStatesAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyDictionary<string, SyncPolicy> folderPolicies = EffectiveFolderPolicies(folders);

        IEnumerable<FolderState> visibleFolders = section == VaultSection.Trash
            ? folders.Where(folder => folder.Record.Tombstone)
            : folders.Where(folder => !folder.Record.Tombstone);
        IEnumerable<ItemState> visibleItems = section == VaultSection.Trash
            ? items.Where(item => item.Record.Tombstone)
            : items.Where(item => !item.Record.Tombstone);

        if (section == VaultSection.Inbox)
        {
            string? inboxId = folders
                .Where(folder => !folder.Record.Tombstone && folder.Metadata.TemplateRole == "inbox")
                .Select(folder => folder.Record.FolderId)
                .SingleOrDefault();
            visibleFolders = visibleFolders.Where(folder => folder.Record.FolderId == inboxId);
            visibleItems = visibleItems.Where(item => item.Record.FolderId == inboxId);
        }
        else if (section == VaultSection.Folders)
        {
            visibleItems = [];
        }
        else if (section == VaultSection.Synced)
        {
            visibleFolders = visibleFolders.Where(folder => EffectivePolicy(folder, folderPolicies) != SyncPolicy.LocalOnly);
            visibleItems = visibleItems.Where(item => EffectivePolicy(item, folderPolicies) != SyncPolicy.LocalOnly);
        }
        else if (section == VaultSection.LocalOnly)
        {
            visibleFolders = visibleFolders.Where(folder => EffectivePolicy(folder, folderPolicies) == SyncPolicy.LocalOnly);
            visibleItems = visibleItems.Where(item => EffectivePolicy(item, folderPolicies) == SyncPolicy.LocalOnly);
        }
        else if (section == VaultSection.Recent)
        {
            visibleFolders = [];
            visibleItems = visibleItems.OrderByDescending(item => item.Metadata.UpdatedAt).Take(100);
        }
        else if (section == VaultSection.Devices)
        {
            return new VaultSnapshot([], [], [], false, "设备配对与真实同步属于 P1；当前不会发起网络连接。 ");
        }

        bool slowSearch = false;
        if (!string.IsNullOrWhiteSpace(query))
        {
            (visibleFolders, visibleItems, slowSearch) = ApplySearch(visibleFolders, visibleItems, query);
        }

        IReadOnlyList<VaultFolderView> folderViews = visibleFolders
            .OrderBy(folder => folder.Metadata.SortOrder)
            .ThenBy(folder => folder.Metadata.Name, StringComparer.CurrentCulture)
            .Select(folder => new VaultFolderView(
                folder.Record.FolderId,
                folder.Record.ParentId,
                folder.Metadata.Name,
                folder.Metadata.Description,
                folder.Metadata.SortOrder,
                ToPolicySetting(folder.Metadata.Policy, folder.Metadata.SelectedDevices),
                EffectivePolicy(folder, folderPolicies),
                folder.Record.Tombstone))
            .ToArray();
        IReadOnlyList<VaultItemView> itemViews = visibleItems
            .OrderByDescending(item => item.Metadata.UpdatedAt)
            .Select(item => new VaultItemView(
                item.Record.ItemId,
                item.Record.FolderId,
                ParseContentType(item.Record.ContentType),
                item.Metadata.Title,
                item.Text,
                item.Metadata.FileName,
                item.Metadata.SizeBytes,
                ToPolicySetting(item.Metadata.Policy, item.Metadata.SelectedDevices),
                EffectivePolicy(item, folderPolicies),
                item.Metadata.CreatedAt,
                item.Metadata.UpdatedAt,
                item.Record.Tombstone))
            .ToArray();
        return new VaultSnapshot(
            folderViews,
            itemViews,
            [],
            slowSearch,
            slowSearch ? "搜索结果完整；已超过内存快速索引预算，使用较慢扫描。" : null);
    }

    public async Task CreateFolderAsync(CreateVaultFolderCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await MutateAsync(async token =>
        {
            IReadOnlyList<FolderState> existing = await ReadFolderStatesAsync(token).ConfigureAwait(false);
            FolderId? parent = command.ParentId is null ? null : FolderId.Parse(command.ParentId);
            if (parent is FolderId parentId && !existing.Any(folder => folder.Record.FolderId == parentId.Value && !folder.Record.Tombstone))
            {
                throw new InvalidOperationException("目标父文件夹不存在或已删除。 ");
            }

            FolderId id = NewFolderId();
            var candidate = existing.Select(ToDomainFolder).Append(new VaultFolder(
                id,
                parent,
                command.Name,
                command.Description,
                existing.Count,
                new SyncPolicySetting(null),
                NewVersionId(),
                null));
            _ = new VaultTree(candidate).Depth(id);

            FolderMetadata metadata = new(
                command.Name,
                command.Description,
                existing.Count,
                null,
                [],
                null,
                timeProvider.GetUtcNow());
            EncryptedVaultMutation mutation = await CreateFolderMutationAsync(
                id.Value,
                parent?.Value,
                null,
                metadata,
                "CREATE",
                false,
                token).ConfigureAwait(false);
            await database.ApplyMutationBatchAsync([mutation], token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task RenameFolderAsync(RenameVaultFolderCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await MutateAsync(async token =>
        {
            FolderState folder = (await ReadFolderStatesAsync(token).ConfigureAwait(false))
                .SingleOrDefault(candidate =>
                    candidate.Record.FolderId == command.FolderId && !candidate.Record.Tombstone)
                ?? throw new InvalidOperationException("文件夹不存在或已删除。 ");
            EncryptedVaultMutation mutation = await CreateFolderMutationAsync(
                folder.Record.FolderId,
                folder.Record.ParentId,
                folder.Record.CurrentVersionId,
                folder.Metadata with
                {
                    Name = command.Name,
                    Description = command.Description,
                    UpdatedAt = timeProvider.GetUtcNow(),
                },
                "UPDATE",
                false,
                token).ConfigureAwait(false);
            await database.ApplyMutationBatchAsync([mutation], token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveTextAsync(SaveVaultTextCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await MutateAsync(async token =>
        {
            _ = await RequireActiveFolderAsync(command.FolderId, token).ConfigureAwait(false);
            ItemId itemId = NewItemId();
            DateTimeOffset now = timeProvider.GetUtcNow();
            ItemMetadata metadata = new(command.Title, null, Encoding.UTF8.GetByteCount(command.Text), null, [], now, now, null);
            EncryptedVaultMutation mutation = await CreateItemMutationAsync(
                itemId.Value,
                command.FolderId,
                WireContentType(command.ContentType),
                null,
                metadata,
                command.Text,
                "CREATE",
                false,
                token).ConfigureAwait(false);
            await database.ApplyMutationBatchAsync([mutation], token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task ImportFileAsync(ImportVaultFileCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await MutateAsync(async token =>
        {
            _ = await RequireActiveFolderAsync(command.FolderId, token).ConfigureAwait(false);
            IUploadFile source = localFiles.OpenVaultImport(command.UserSelectedPath);
            ItemId itemId = NewItemId();
            string fileId = NewVaultId().Value;
            string generationId = NewVaultId().Value;
            string manifestId = NewVaultId().Value;
            long epoch = await CurrentEpochAsync(token).ConfigureAwait(false);
            DateTimeOffset now = timeProvider.GetUtcNow();
            var storedChunks = new List<StoredEncryptedChunk>();
            var chunkRecords = new List<EncryptedFileChunkRecord>();
            using FileEncryptionMaterial material = FileEncryptionMaterial.Create();
            byte[] fileDek = material.CopyFileDek();
            try
            {
                await using Stream input = await source.OpenReadAsync(token).ConfigureAwait(false);
                byte[] buffer = new byte[FileEncryptionMaterial.MaximumPlaintextChunkBytes];
                long totalBytes = 0;
                long chunkIndex = 0;
                try
                {
                    while (true)
                    {
                        int count = await ReadChunkAsync(input, buffer, token).ConfigureAwait(false);
                        if (count == 0)
                        {
                            break;
                        }

                        totalBytes = checked(totalBytes + count);
                        VaultCipherEnvelope encrypted = VaultCryptography.EncryptFileChunk(
                            fileDek,
                            new VaultCryptoContext(
                                CryptoPurpose.FileChunk,
                                RequireVaultId(),
                                RequireDeviceId(),
                                fileId,
                                "chunk",
                                epoch),
                            material.AllocateChunk(chunkIndex),
                            buffer.AsSpan(0, count));
                        byte[] ciphertext = DecodeBase64Url(encrypted.CipherAndTag);
                        try
                        {
                            StoredEncryptedChunk stored = await blobStore.WriteChunkAtomicallyAsync(
                                fileId,
                                generationId,
                                chunkIndex,
                                ciphertext,
                                token).ConfigureAwait(false);
                            storedChunks.Add(stored);
                            chunkRecords.Add(new EncryptedFileChunkRecord(
                                fileId,
                                generationId,
                                chunkIndex,
                                encrypted.Nonce,
                                stored.CiphertextBytes,
                                stored.CiphertextSha256,
                                stored.RelativePath));
                        }
                        finally
                        {
                            CryptographicOperations.ZeroMemory(ciphertext);
                            CryptographicOperations.ZeroMemory(buffer.AsSpan(0, count));
                        }

                        chunkIndex = checked(chunkIndex + 1);
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(buffer);
                }

                if (totalBytes != source.Length)
                {
                    throw new InvalidDataException("Selected file length changed during Vault import.");
                }

                string wrappedFileKey = await EncryptBytesAsync(
                    CryptoPurpose.FileKeyWrap,
                    fileId,
                    "file-key",
                    fileDek.ToArray(),
                    epoch,
                    token).ConfigureAwait(false);
                string encryptedManifest = await EncryptJsonAsync(
                    CryptoPurpose.FileManifest,
                    fileId,
                    "manifest",
                    new FileManifestPayload(
                        fileId,
                        generationId,
                        RequireDeviceId().Value,
                        source.DisplayName,
                        source.ContentType,
                        totalBytes,
                        FileEncryptionMaterial.MaximumPlaintextChunkBytes,
                        chunkRecords.Count),
                    epoch,
                    token).ConfigureAwait(false);
                ItemMetadata metadata = new(
                    command.Title,
                    source.DisplayName,
                    totalBytes,
                    null,
                    [],
                    now,
                    now,
                    null);
                EncryptedVaultMutation itemMutation = await CreateItemMutationAsync(
                    itemId.Value,
                    command.FolderId,
                    "FILE",
                    null,
                    metadata,
                    null,
                    "CREATE",
                    false,
                    token).ConfigureAwait(false);
                var manifest = new EncryptedFileManifestRecord(
                    manifestId,
                    fileId,
                    itemId.Value,
                    generationId,
                    epoch,
                    FileEncryptionMaterial.MaximumPlaintextChunkBytes,
                    chunkRecords.Count,
                    encryptedManifest,
                    wrappedFileKey,
                    false);
                await database.ApplyFileMutationAsync(itemMutation, manifest, chunkRecords, token).ConfigureAwait(false);
            }
            catch
            {
                blobStore.DeleteCommittedChunks(storedChunks.Select(chunk => chunk.RelativePath));
                throw;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(fileDek);
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task ExportFileAsync(
        string itemId,
        string userSelectedPath,
        CancellationToken cancellationToken)
    {
        EnsureInitialized();
        ItemState item = await RequireItemAsync(itemId, includeDeleted: false, cancellationToken).ConfigureAwait(false);
        if (item.Record.ContentType != "FILE")
        {
            throw new InvalidOperationException("只有文件条目可以导出。 ");
        }

        EncryptedFileManifestRecord record = await database.ReadCommittedFileManifestAsync(itemId, cancellationToken)
            .ConfigureAwait(false);
        FileManifestPayload manifest = DecryptJson<FileManifestPayload>(record.EncryptedManifest);
        if (manifest.FileId != record.FileId || manifest.GenerationId != record.GenerationId ||
            manifest.SizeBytes != item.Metadata.SizeBytes || manifest.ChunkCount != record.ChunkCount)
        {
            throw new InvalidDataException("Encrypted file manifest does not match its item record.");
        }

        byte[] fileDek = DecryptBytes(record.WrappedFileKey);
        IDownloadTarget target = localFiles.CreateDownloadTarget(userSelectedPath);
        try
        {
            IReadOnlyList<EncryptedFileChunkRecord> chunks = await database.ReadFileChunksAsync(
                record.FileId,
                record.GenerationId,
                cancellationToken).ConfigureAwait(false);
            if (chunks.Count != record.ChunkCount)
            {
                throw new InvalidDataException("Encrypted file chunks are incomplete.");
            }

            long total = 0;
            await using (Stream output = await target.OpenWriteAsync(cancellationToken).ConfigureAwait(false))
            {
                foreach (EncryptedFileChunkRecord chunk in chunks)
                {
                    byte[] ciphertext = await blobStore.ReadChunkAsync(
                        new StoredEncryptedChunk(
                            chunk.FileId,
                            chunk.GenerationId,
                            chunk.ChunkIndex,
                            chunk.CiphertextBytes,
                            chunk.CiphertextSha256,
                            chunk.RelativePath),
                        cancellationToken).ConfigureAwait(false);
                    byte[] plaintext;
                    try
                    {
                        plaintext = VaultCryptography.DecryptFileChunk(
                            fileDek,
                            new VaultCipherEnvelope(
                                CryptoPurpose.FileChunk.ToWireValue(),
                                RequireVaultId().Value,
                                manifest.OriginDeviceId,
                                record.FileId,
                                "chunk",
                                record.KeyEpoch,
                                chunk.ChunkIndex,
                                chunk.Nonce,
                                chunk.CiphertextBytes - 16,
                                Base64Url(ciphertext)));
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(ciphertext);
                    }

                    try
                    {
                        await output.WriteAsync(plaintext, cancellationToken).ConfigureAwait(false);
                        total = checked(total + plaintext.Length);
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(plaintext);
                    }
                }

                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            if (total != manifest.SizeBytes)
            {
                throw new InvalidDataException("Decrypted file length does not match its manifest.");
            }

            await target.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await target.AbortAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(fileDek);
        }
    }

    public async Task UpdateTextAsync(UpdateVaultTextCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await MutateAsync(async token =>
        {
            ItemState item = await RequireItemAsync(command.ItemId, includeDeleted: false, token).ConfigureAwait(false);
            if (item.Record.ContentType == "FILE")
            {
                throw new InvalidOperationException("文件内容必须通过新 generation 替换。 ");
            }

            ItemMetadata metadata = item.Metadata with
            {
                Title = command.Title,
                SizeBytes = Encoding.UTF8.GetByteCount(command.Text),
                UpdatedAt = timeProvider.GetUtcNow(),
            };
            EncryptedVaultMutation mutation = await CreateItemMutationAsync(
                item.Record.ItemId,
                item.Record.FolderId,
                item.Record.ContentType,
                item.Record.CurrentVersionId,
                metadata,
                command.Text,
                "UPDATE",
                false,
                token).ConfigureAwait(false);
            await database.ApplyMutationBatchAsync([mutation], token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task MoveAsync(
        IReadOnlySet<VaultEntitySelection> selection,
        string targetFolderId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selection);
        await MutateAsync(async token =>
        {
            IReadOnlyList<FolderState> folders = await ReadFolderStatesAsync(token).ConfigureAwait(false);
            IReadOnlyList<ItemState> items = await ReadItemStatesAsync(token).ConfigureAwait(false);
            _ = folders.SingleOrDefault(folder => folder.Record.FolderId == targetFolderId && !folder.Record.Tombstone)
                ?? throw new InvalidOperationException("目标文件夹不存在或已删除。 ");
            HashSet<FolderId> movingFolders = selection
                .Where(entity => entity.Kind == VaultEntityKind.Folder)
                .Select(entity => FolderId.Parse(entity.Id))
                .ToHashSet();
            if (movingFolders.Count != 0)
            {
                new VaultTree(folders.Where(folder => !folder.Record.Tombstone).Select(ToDomainFolder))
                    .ValidateMove(movingFolders, FolderId.Parse(targetFolderId));
            }

            var mutations = new List<EncryptedVaultMutation>();
            foreach (VaultEntitySelection entity in selection)
            {
                if (entity.Kind == VaultEntityKind.Folder)
                {
                    FolderState folder = folders.Single(candidate => candidate.Record.FolderId == entity.Id && !candidate.Record.Tombstone);
                    mutations.Add(await CreateFolderMutationAsync(
                        entity.Id,
                        targetFolderId,
                        folder.Record.CurrentVersionId,
                        folder.Metadata with { UpdatedAt = timeProvider.GetUtcNow() },
                        "MOVE",
                        false,
                        token).ConfigureAwait(false));
                }
                else
                {
                    ItemState item = items.Single(candidate => candidate.Record.ItemId == entity.Id && !candidate.Record.Tombstone);
                    mutations.Add(await CreateItemMutationAsync(
                        entity.Id,
                        targetFolderId,
                        item.Record.ContentType,
                        item.Record.CurrentVersionId,
                        item.Metadata with { UpdatedAt = timeProvider.GetUtcNow() },
                        item.Text,
                        "MOVE",
                        false,
                        token).ConfigureAwait(false));
                }
            }

            await database.ApplyMutationBatchAsync(mutations, token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task MoveToTrashAsync(
        IReadOnlySet<VaultEntitySelection> selection,
        bool includeFolderContents,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selection);
        await MutateAsync(async token =>
        {
            IReadOnlyList<FolderState> folders = await ReadFolderStatesAsync(token).ConfigureAwait(false);
            IReadOnlyList<ItemState> items = await ReadItemStatesAsync(token).ConfigureAwait(false);
            HashSet<VaultEntitySelection> expanded = ExpandFolderSelection(selection, folders, items, includeFolderContents);
            DateTimeOffset deletedAt = timeProvider.GetUtcNow();
            var mutations = new List<EncryptedVaultMutation>();
            foreach (VaultEntitySelection entity in expanded)
            {
                if (entity.Kind == VaultEntityKind.Folder)
                {
                    FolderState folder = folders.Single(candidate => candidate.Record.FolderId == entity.Id);
                    mutations.Add(await CreateFolderMutationAsync(
                        entity.Id,
                        folder.Record.ParentId,
                        folder.Record.CurrentVersionId,
                        folder.Metadata with { DeletedAt = deletedAt, UpdatedAt = deletedAt },
                        "DELETE",
                        true,
                        token).ConfigureAwait(false));
                }
                else
                {
                    ItemState item = items.Single(candidate => candidate.Record.ItemId == entity.Id);
                    mutations.Add(await CreateItemMutationAsync(
                        entity.Id,
                        item.Record.FolderId,
                        item.Record.ContentType,
                        item.Record.CurrentVersionId,
                        item.Metadata with { DeletedAt = deletedAt, UpdatedAt = deletedAt },
                        item.Text,
                        "DELETE",
                        true,
                        token).ConfigureAwait(false));
                }
            }

            await database.ApplyMutationBatchAsync(mutations, token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task RestoreAsync(
        IReadOnlySet<VaultEntitySelection> selection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selection);
        await MutateAsync(async token =>
        {
            IReadOnlyList<FolderState> folders = await ReadFolderStatesAsync(token).ConfigureAwait(false);
            IReadOnlyList<ItemState> items = await ReadItemStatesAsync(token).ConfigureAwait(false);
            string fallbackFolder = folders
                .Where(folder => !folder.Record.Tombstone)
                .OrderBy(folder => folder.Metadata.SortOrder)
                .Select(folder => folder.Record.FolderId)
                .FirstOrDefault()
                ?? throw new InvalidOperationException("没有可用于恢复的目标文件夹。 ");
            var mutations = new List<EncryptedVaultMutation>();
            foreach (VaultEntitySelection entity in selection)
            {
                if (entity.Kind == VaultEntityKind.Folder)
                {
                    FolderState folder = folders.Single(candidate => candidate.Record.FolderId == entity.Id && candidate.Record.Tombstone);
                    string? parentId = folder.Record.ParentId;
                    if (parentId is not null && !folders.Any(candidate => candidate.Record.FolderId == parentId && !candidate.Record.Tombstone))
                    {
                        parentId = null;
                    }

                    mutations.Add(await CreateFolderMutationAsync(
                        entity.Id,
                        parentId,
                        folder.Record.CurrentVersionId,
                        folder.Metadata with { DeletedAt = null, UpdatedAt = timeProvider.GetUtcNow() },
                        "RESTORE",
                        false,
                        token,
                        removeTombstone: true).ConfigureAwait(false));
                }
                else
                {
                    ItemState item = items.Single(candidate => candidate.Record.ItemId == entity.Id && candidate.Record.Tombstone);
                    string folderId = folders.Any(folder => folder.Record.FolderId == item.Record.FolderId && !folder.Record.Tombstone)
                        ? item.Record.FolderId
                        : fallbackFolder;
                    mutations.Add(await CreateItemMutationAsync(
                        entity.Id,
                        folderId,
                        item.Record.ContentType,
                        item.Record.CurrentVersionId,
                        item.Metadata with { DeletedAt = null, UpdatedAt = timeProvider.GetUtcNow() },
                        item.Text,
                        "RESTORE",
                        false,
                        token,
                        removeTombstone: true).ConfigureAwait(false));
                }
            }

            await database.ApplyMutationBatchAsync(mutations, token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task PurgeAsync(
        IReadOnlySet<VaultEntitySelection> selection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selection);
        await MutateAsync(async token =>
        {
            IReadOnlyList<FolderState> folders = await ReadFolderStatesAsync(token).ConfigureAwait(false);
            IReadOnlyList<ItemState> items = await ReadItemStatesAsync(token).ConfigureAwait(false);
            foreach (VaultEntitySelection entity in selection)
            {
                bool isDeleted = entity.Kind == VaultEntityKind.Folder
                    ? folders.Any(folder => folder.Record.FolderId == entity.Id && folder.Record.Tombstone)
                    : items.Any(item => item.Record.ItemId == entity.Id && item.Record.Tombstone);
                if (!isDeleted)
                {
                    throw new InvalidOperationException("只有回收站中的内容可以永久删除。 ");
                }
            }

            IReadOnlyList<string> paths = await database.PurgeEntitiesAsync(
                selection.Select(entity => (entity.Kind == VaultEntityKind.Folder ? "FOLDER" : "ITEM", entity.Id)).ToArray(),
                token).ConfigureAwait(false);
            blobStore.DeleteCommittedChunks(paths);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task SetPolicyAsync(
        IReadOnlySet<VaultEntitySelection> selection,
        VaultPolicyCommand policy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(policy);
        await MutateAsync(async token =>
        {
            IReadOnlyList<FolderState> folders = await ReadFolderStatesAsync(token).ConfigureAwait(false);
            IReadOnlyList<ItemState> items = await ReadItemStatesAsync(token).ConfigureAwait(false);
            string[] selectedDevices = policy.SelectedDeviceIds.Order(StringComparer.Ordinal).ToArray();
            string policyValue = WirePolicy(policy.Policy);
            HashSet<VaultEntitySelection> expanded = policy.ClearDescendantOverrides
                ? ExpandFolderSelection(selection, folders, items, includeFolderContents: true)
                : selection.ToHashSet();
            var mutations = new List<EncryptedVaultMutation>();
            foreach (VaultEntitySelection entity in expanded)
            {
                bool inheritedDescendant = policy.ClearDescendantOverrides && !selection.Contains(entity);
                string? nextPolicy = inheritedDescendant ? null : policyValue;
                string[] nextDevices = inheritedDescendant ? [] : selectedDevices;
                if (entity.Kind == VaultEntityKind.Folder)
                {
                    FolderState folder = folders.Single(candidate => candidate.Record.FolderId == entity.Id && !candidate.Record.Tombstone);
                    mutations.Add(await CreateFolderMutationAsync(
                        entity.Id,
                        folder.Record.ParentId,
                        folder.Record.CurrentVersionId,
                        folder.Metadata with { Policy = nextPolicy, SelectedDevices = nextDevices, UpdatedAt = timeProvider.GetUtcNow() },
                        "UPDATE",
                        false,
                        token).ConfigureAwait(false));
                }
                else
                {
                    ItemState item = items.Single(candidate => candidate.Record.ItemId == entity.Id && !candidate.Record.Tombstone);
                    mutations.Add(await CreateItemMutationAsync(
                        entity.Id,
                        item.Record.FolderId,
                        item.Record.ContentType,
                        item.Record.CurrentVersionId,
                        item.Metadata with { Policy = nextPolicy, SelectedDevices = nextDevices, UpdatedAt = timeProvider.GetUtcNow() },
                        item.Text,
                        "UPDATE",
                        false,
                        token).ConfigureAwait(false));
                }
            }

            await database.ApplyMutationBatchAsync(mutations, token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public void ClearSensitiveState()
    {
        if (disposed)
        {
            return;
        }

        ClearSecrets();
        initialized = false;
    }

    public ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return ValueTask.CompletedTask;
        }

        disposed = true;
        ClearSecrets();
        mutationLock.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task InitializeWithoutDatabaseAsync(CancellationToken cancellationToken)
    {
        WindowsPlatformWrapper? existing = await wrapperStore.ReadSingleAsync(cancellationToken).ConfigureAwait(false);
        if (existing is not null && existing.State != "STAGED")
        {
            throw new InvalidDataException("A READY wrapper without its Vault database cannot be recovered automatically.");
        }

        WindowsPlatformWrapper wrapper;
        byte[] secret;
        if (existing is null)
        {
            VaultId createdVaultId = NewVaultId();
            string initializationId = NewVaultId().Value;
            secret = RandomNumberGenerator.GetBytes(EpochSecret.Size);
            WindowsProtectedSecret protectedSecret = protector.Protect(createdVaultId, initializationId, InitialEpoch, secret);
            wrapper = new WindowsPlatformWrapper(
                1,
                "WINDOWS",
                "DPAPI-CURRENT-USER",
                createdVaultId.Value,
                initializationId,
                InitialEpoch,
                "STAGED",
                null,
                null,
                protectedSecret.ProtectedSecret);
            await wrapperStore.WriteAtomicallyAsync(wrapper, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            wrapper = existing;
            secret = protector.Unprotect(
                VaultId.Parse(wrapper.VaultId),
                wrapper.InitializationId,
                wrapper.KeyEpoch,
                new WindowsProtectedSecret(wrapper.ProtectedSecret));
        }

        vaultId = VaultId.Parse(wrapper.VaultId);
        AddSecret(wrapper.KeyEpoch, secret);
        await database.CreateEmptyStagedAsync(
            RequireVaultId(),
            wrapper.InitializationId,
            wrapper.KeyEpoch,
            WindowsPlatformWrapperStore.Digest(wrapper),
            cancellationToken).ConfigureAwait(false);
        await PopulateDefaultsAsync(cancellationToken).ConfigureAwait(false);
        await database.PromoteReadyAsync(cancellationToken).ConfigureAwait(false);
        await wrapperStore.WriteAtomicallyAsync(wrapper with { State = "READY" }, cancellationToken).ConfigureAwait(false);
    }

    private async Task ResumeDatabaseAsync(
        DatabaseObservation observation,
        CancellationToken cancellationToken)
    {
        vaultId = observation.VaultId;
        WindowsPlatformWrapper wrapper = await wrapperStore.ReadAsync(
            observation.VaultId,
            observation.KeyEpoch,
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Vault database exists without its platform wrapper.");
        WrapperObservation wrapperObservation = new(
            VaultId.Parse(wrapper.VaultId),
            wrapper.InitializationId,
            wrapper.KeyEpoch,
            WindowsPlatformWrapperStore.Digest(wrapper),
            wrapper.State == "READY" ? InitializationPhase.Ready : InitializationPhase.Staged);
        InitializationAction action = VaultInitializationStateMachine.Decide(wrapperObservation, observation);
        if (action is InitializationAction.FailClosed failure)
        {
            throw new InvalidDataException(failure.Reason);
        }

        byte[] secret = protector.Unprotect(
            observation.VaultId,
            wrapper.InitializationId,
            wrapper.KeyEpoch,
            new WindowsProtectedSecret(wrapper.ProtectedSecret));
        AddSecret(wrapper.KeyEpoch, secret);
        if (action is InitializationAction.PopulateStagedDatabase)
        {
            await PopulateDefaultsAsync(cancellationToken).ConfigureAwait(false);
            await database.PromoteReadyAsync(cancellationToken).ConfigureAwait(false);
            await wrapperStore.WriteAtomicallyAsync(wrapper with { State = "READY" }, cancellationToken).ConfigureAwait(false);
        }
        else if (action is InitializationAction.PromoteDatabaseReady)
        {
            await database.PromoteReadyAsync(cancellationToken).ConfigureAwait(false);
            await wrapperStore.WriteAtomicallyAsync(wrapper with { State = "READY" }, cancellationToken).ConfigureAwait(false);
        }
        else if (action is InitializationAction.PromoteWrapperReady)
        {
            await wrapperStore.WriteAtomicallyAsync(wrapper with { State = "READY" }, cancellationToken).ConfigureAwait(false);
        }
        else if (action is not InitializationAction.OpenReady)
        {
            throw new InvalidDataException("Unsupported Vault recovery state.");
        }
    }

    private async Task PopulateDefaultsAsync(CancellationToken cancellationToken)
    {
        sourceSequence = 0;
        string[] roles = ["inbox", "password", "work", "private", "other"];
        var defaults = new List<(EncryptedFolderRecord, EncryptedVersionRecord, EncryptedEventRecord)>();
        for (int index = 0; index < DefaultFolderInitializer.Names.Count; index++)
        {
            FolderId folderId = NewFolderId();
            FolderMetadata metadata = new(
                DefaultFolderInitializer.Names[index],
                null,
                index,
                null,
                [],
                roles[index],
                timeProvider.GetUtcNow());
            EncryptedVaultMutation mutation = await CreateFolderMutationAsync(
                folderId.Value,
                null,
                null,
                metadata,
                "CREATE",
                false,
                cancellationToken).ConfigureAwait(false);
            defaults.Add((mutation.Folder!, mutation.Version, mutation.Event));
        }

        await database.PopulateDefaultFoldersAsync(defaults, cancellationToken).ConfigureAwait(false);
    }

    private async Task<EncryptedVaultMutation> CreateFolderMutationAsync(
        string folderId,
        string? parentId,
        string? baseVersionId,
        FolderMetadata metadata,
        string operation,
        bool tombstone,
        CancellationToken cancellationToken,
        bool removeTombstone = false)
    {
        VersionId versionId = NewVersionId();
        long epoch = await CurrentEpochAsync(cancellationToken).ConfigureAwait(false);
        string encryptedMetadata = await EncryptJsonAsync(
            CryptoPurpose.FolderMetadata,
            folderId,
            "metadata",
            metadata,
            epoch,
            cancellationToken).ConfigureAwait(false);
        string encryptedSnapshot = await EncryptJsonAsync(
            CryptoPurpose.FolderMetadata,
            folderId,
            "snapshot",
            metadata,
            epoch,
            cancellationToken).ConfigureAwait(false);
        EventPayload eventPayload = new(operation, "FOLDER", folderId, baseVersionId, versionId.Value);
        string encryptedEvent = await EncryptJsonAsync(
            CryptoPurpose.EventBody,
            folderId,
            "event",
            eventPayload,
            epoch,
            cancellationToken).ConfigureAwait(false);
        EncryptedTombstoneRecord? tombstoneRecord = tombstone
            ? await CreateTombstoneAsync(folderId, "FOLDER", versionId.Value, epoch, metadata.DeletedAt!.Value, cancellationToken)
                .ConfigureAwait(false)
            : null;
        return new EncryptedVaultMutation(
            new EncryptedFolderRecord(folderId, RequireVaultId().Value, parentId, versionId.Value, epoch, tombstone, encryptedMetadata),
            null,
            new EncryptedVersionRecord(versionId.Value, folderId, "FOLDER", baseVersionId, epoch, encryptedSnapshot),
            NewEvent(folderId, "FOLDER", operation, baseVersionId, versionId.Value, epoch, encryptedEvent),
            tombstoneRecord,
            removeTombstone);
    }

    private async Task<EncryptedVaultMutation> CreateItemMutationAsync(
        string itemId,
        string folderId,
        string contentType,
        string? baseVersionId,
        ItemMetadata metadata,
        string? text,
        string operation,
        bool tombstone,
        CancellationToken cancellationToken,
        bool removeTombstone = false)
    {
        VersionId versionId = NewVersionId();
        long epoch = await CurrentEpochAsync(cancellationToken).ConfigureAwait(false);
        string encryptedMetadata = await EncryptJsonAsync(
            CryptoPurpose.ItemMetadata,
            itemId,
            "metadata",
            metadata,
            epoch,
            cancellationToken).ConfigureAwait(false);
        string? encryptedPayload = text is null
            ? null
            : await EncryptBytesAsync(
                CryptoPurpose.ItemPayload,
                itemId,
                "payload",
                Encoding.UTF8.GetBytes(text),
                epoch,
                cancellationToken).ConfigureAwait(false);
        string encryptedSnapshot = await EncryptJsonAsync(
            CryptoPurpose.ItemMetadata,
            itemId,
            "snapshot",
            metadata,
            epoch,
            cancellationToken).ConfigureAwait(false);
        EventPayload eventPayload = new(operation, "ITEM", itemId, baseVersionId, versionId.Value);
        string encryptedEvent = await EncryptJsonAsync(
            CryptoPurpose.EventBody,
            itemId,
            "event",
            eventPayload,
            epoch,
            cancellationToken).ConfigureAwait(false);
        EncryptedTombstoneRecord? tombstoneRecord = tombstone
            ? await CreateTombstoneAsync(itemId, "ITEM", versionId.Value, epoch, metadata.DeletedAt!.Value, cancellationToken)
                .ConfigureAwait(false)
            : null;
        return new EncryptedVaultMutation(
            null,
            new EncryptedItemRecord(
                itemId,
                RequireVaultId().Value,
                folderId,
                contentType,
                versionId.Value,
                epoch,
                tombstone,
                encryptedMetadata,
                encryptedPayload),
            new EncryptedVersionRecord(versionId.Value, itemId, "ITEM", baseVersionId, epoch, encryptedSnapshot),
            NewEvent(itemId, "ITEM", operation, baseVersionId, versionId.Value, epoch, encryptedEvent),
            tombstoneRecord,
            removeTombstone);
    }

    private async Task<EncryptedTombstoneRecord> CreateTombstoneAsync(
        string entityId,
        string entityKind,
        string versionId,
        long epoch,
        DateTimeOffset deletedAt,
        CancellationToken cancellationToken)
    {
        string encryptedDeletedAt = await EncryptJsonAsync(
            CryptoPurpose.EventBody,
            entityId,
            "deleted-at",
            new DeletedAtPayload(deletedAt),
            epoch,
            cancellationToken).ConfigureAwait(false);
        long purgeBucket = (deletedAt + TrashRetention.Duration).ToUnixTimeSeconds() / 86_400;
        return new EncryptedTombstoneRecord(entityId, entityKind, versionId, epoch, encryptedDeletedAt, purgeBucket);
    }

    private EncryptedEventRecord NewEvent(
        string entityId,
        string entityKind,
        string operation,
        string? baseVersionId,
        string versionId,
        long epoch,
        string encryptedBody)
    {
        sourceSequence = checked(sourceSequence + 1);
        return new EncryptedEventRecord(
            NewEventId().Value,
            RequireDeviceId().Value,
            sourceSequence,
            timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
            entityId,
            entityKind,
            operation,
            baseVersionId,
            versionId,
            epoch,
            encryptedBody,
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(encryptedBody))),
            "APPLIED");
    }

    private async Task<string> EncryptJsonAsync<T>(
        CryptoPurpose purpose,
        string entityId,
        string field,
        T value,
        long epoch,
        CancellationToken cancellationToken) =>
        await EncryptBytesAsync(
            purpose,
            entityId,
            field,
            JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions),
            epoch,
            cancellationToken).ConfigureAwait(false);

    private async Task<string> EncryptBytesAsync(
        CryptoPurpose purpose,
        string entityId,
        string field,
        byte[] value,
        long epoch,
        CancellationToken cancellationToken)
    {
        try
        {
            NonceAllocation allocation = await database.AllocateNonceAsync(
                RequireVaultId(),
                epoch,
                purpose,
                RequireDeviceId(),
                cancellationToken).ConfigureAwait(false);
            VaultCipherEnvelope encrypted = VaultCryptography.EncryptRecord(
                RequireSecret(epoch),
                new VaultCryptoContext(purpose, RequireVaultId(), RequireDeviceId(), entityId, field, epoch),
                allocation,
                value);
            return SerializeEnvelope(encrypted);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(value);
        }
    }

    private T DecryptJson<T>(string value)
    {
        VaultCipherEnvelope envelope = ParseEnvelope(value);
        byte[] plaintext = VaultCryptography.DecryptRecord(RequireSecret(envelope.KeyEpoch), envelope);
        try
        {
            return JsonSerializer.Deserialize<T>(plaintext, JsonOptions)
                ?? throw new InvalidDataException("Encrypted Vault metadata was empty.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private string DecryptText(string value)
    {
        VaultCipherEnvelope envelope = ParseEnvelope(value);
        byte[] plaintext = VaultCryptography.DecryptRecord(RequireSecret(envelope.KeyEpoch), envelope);
        try
        {
            return new UTF8Encoding(false, true).GetString(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private byte[] DecryptBytes(string value)
    {
        VaultCipherEnvelope envelope = ParseEnvelope(value);
        return VaultCryptography.DecryptRecord(RequireSecret(envelope.KeyEpoch), envelope);
    }

    private async Task<IReadOnlyList<FolderState>> ReadFolderStatesAsync(CancellationToken cancellationToken) =>
        (await database.ReadFoldersAsync(cancellationToken).ConfigureAwait(false))
        .Select(record => new FolderState(record, DecryptJson<FolderMetadata>(record.EncryptedMetadata)))
        .ToArray();

    private async Task<IReadOnlyList<ItemState>> ReadItemStatesAsync(CancellationToken cancellationToken) =>
        (await database.ReadItemsAsync(cancellationToken).ConfigureAwait(false))
        .Select(record => new ItemState(
            record,
            DecryptJson<ItemMetadata>(record.EncryptedMetadata),
            record.EncryptedPayload is null ? null : DecryptText(record.EncryptedPayload)))
        .ToArray();

    private async Task<FolderState> RequireActiveFolderAsync(string id, CancellationToken cancellationToken) =>
        (await ReadFolderStatesAsync(cancellationToken).ConfigureAwait(false))
        .SingleOrDefault(folder => folder.Record.FolderId == id && !folder.Record.Tombstone)
        ?? throw new InvalidOperationException("目标文件夹不存在或已删除。 ");

    private async Task<ItemState> RequireItemAsync(
        string id,
        bool includeDeleted,
        CancellationToken cancellationToken) =>
        (await ReadItemStatesAsync(cancellationToken).ConfigureAwait(false))
        .SingleOrDefault(item => item.Record.ItemId == id && (includeDeleted || !item.Record.Tombstone))
        ?? throw new InvalidOperationException("条目不存在或当前状态不允许该操作。 ");

    private static HashSet<VaultEntitySelection> ExpandFolderSelection(
        IReadOnlySet<VaultEntitySelection> selection,
        IReadOnlyList<FolderState> folders,
        IReadOnlyList<ItemState> items,
        bool includeFolderContents)
    {
        var result = selection.ToHashSet();
        foreach (VaultEntitySelection selected in selection.Where(entity => entity.Kind == VaultEntityKind.Folder))
        {
            HashSet<string> descendants = DescendantIds(selected.Id, folders);
            bool hasContents = descendants.Count != 0 || items.Any(item =>
                item.Record.FolderId == selected.Id || descendants.Contains(item.Record.FolderId));
            if (hasContents && !includeFolderContents)
            {
                throw new InvalidOperationException("非空文件夹删除前必须明确包含其内容。 ");
            }

            if (includeFolderContents)
            {
                result.UnionWith(descendants.Select(id => new VaultEntitySelection(VaultEntityKind.Folder, id)));
                result.UnionWith(items
                    .Where(item => item.Record.FolderId == selected.Id || descendants.Contains(item.Record.FolderId))
                    .Select(item => new VaultEntitySelection(VaultEntityKind.Item, item.Record.ItemId)));
            }
        }

        return result;
    }

    private static HashSet<string> DescendantIds(string folderId, IReadOnlyList<FolderState> folders)
    {
        HashSet<string> result = [];
        Queue<string> pending = new();
        pending.Enqueue(folderId);
        while (pending.TryDequeue(out string? parent))
        {
            foreach (string child in folders.Where(folder => folder.Record.ParentId == parent).Select(folder => folder.Record.FolderId))
            {
                if (result.Add(child))
                {
                    pending.Enqueue(child);
                }
            }
        }

        return result;
    }

    private static Dictionary<string, SyncPolicy> EffectiveFolderPolicies(IReadOnlyList<FolderState> folders)
    {
        var domain = folders.Select(ToDomainFolder).ToArray();
        var tree = new VaultTree(domain);
        return domain.ToDictionary(folder => folder.Id.Value, folder => tree.EffectivePolicy(folder.Id).Override ?? SyncPolicy.LocalOnly);
    }

    private static SyncPolicy EffectivePolicy(
        FolderState folder,
        IReadOnlyDictionary<string, SyncPolicy> folderPolicies) =>
        folderPolicies[folder.Record.FolderId];

    private static SyncPolicy EffectivePolicy(
        ItemState item,
        IReadOnlyDictionary<string, SyncPolicy> folderPolicies) =>
        item.Metadata.Policy is null ? folderPolicies[item.Record.FolderId] : ParsePolicy(item.Metadata.Policy);

    private static (IEnumerable<FolderState>, IEnumerable<ItemState>, bool) ApplySearch(
        IEnumerable<FolderState> folders,
        IEnumerable<ItemState> items,
        string query)
    {
        FolderState[] folderArray = folders.ToArray();
        ItemState[] itemArray = items.ToArray();
        long bodyBytes = itemArray.Sum(item => item.Text is null ? 0 : Encoding.UTF8.GetByteCount(item.Text));
        bool slow = folderArray.Length + itemArray.Length > SearchNameLimit ||
            itemArray.Count(item => item.Text is not null) > SearchBodyItemLimit ||
            bodyBytes > SearchBodyBytesLimit ||
            itemArray.Any(item => item.Text is not null && Encoding.UTF8.GetByteCount(item.Text) > SearchSingleBodyBytesLimit);
        return (
            folderArray.Where(folder => Contains(folder.Metadata.Name, query) || Contains(folder.Metadata.Description, query)),
            itemArray.Where(item =>
                Contains(item.Metadata.Title, query) ||
                Contains(item.Metadata.FileName, query) ||
                Contains(item.Text, query)),
            slow);
    }

    private static bool Contains(string? value, string query) =>
        value?.Contains(query, StringComparison.CurrentCultureIgnoreCase) == true;

    private async Task MutateAsync(Func<CancellationToken, Task> mutation, CancellationToken cancellationToken)
    {
        EnsureInitialized();
        await mutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        long priorSequence = sourceSequence;
        try
        {
            await mutation(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            sourceSequence = priorSequence;
            throw;
        }
        finally
        {
            _ = mutationLock.Release();
        }
    }

    private static async Task<int> ReadChunkAsync(
        Stream input,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await input.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    private static byte[] DecodeBase64Url(string value)
    {
        string padded = value.Replace('-', '+').Replace('_', '/')
            .PadRight((value.Length + 3) / 4 * 4, '=');
        return Convert.FromBase64String(padded);
    }

    private static string Base64Url(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private async Task<long> CurrentEpochAsync(CancellationToken cancellationToken) =>
        await database.CurrentWriteEpochAsync(cancellationToken).ConfigureAwait(false);

    private static string SerializeEnvelope(VaultCipherEnvelope envelope) => JsonSerializer.Serialize(
        new VaultEnvelopeContract
        {
            SchemaVersion = 1,
            Algorithm = "A256GCM",
            Purpose = envelope.Purpose,
            VaultId = envelope.VaultId,
            OriginDeviceId = envelope.OriginDeviceId,
            EntityId = envelope.EntityId,
            Field = envelope.Field,
            KeyEpoch = envelope.KeyEpoch,
            NonceCounter = envelope.NonceCounter,
            Nonce = envelope.Nonce,
            PaddedPlaintextBytes = envelope.PaddedPlaintextBytes,
            CipherAndTag = envelope.CipherAndTag,
        },
        JsonOptions);

    private static VaultCipherEnvelope ParseEnvelope(string value)
    {
        VaultEnvelopeContract envelope = VaultContractCodec.DecodeEnvelope(value);
        return new VaultCipherEnvelope(
            envelope.Purpose,
            envelope.VaultId,
            envelope.OriginDeviceId,
            envelope.EntityId,
            envelope.Field,
            envelope.KeyEpoch,
            envelope.NonceCounter,
            envelope.Nonce,
            envelope.PaddedPlaintextBytes,
            envelope.CipherAndTag);
    }

    private void AddSecret(long epoch, byte[] secret)
    {
        if (secret.Length != EpochSecret.Size || epochSecrets.ContainsKey(epoch))
        {
            CryptographicOperations.ZeroMemory(secret);
            throw new InvalidDataException("Vault epoch secret state is invalid.");
        }

        epochSecrets.Add(epoch, secret);
    }

    private byte[] RequireSecret(long epoch) =>
        epochSecrets.TryGetValue(epoch, out byte[]? secret)
            ? secret
            : throw new InvalidOperationException("The required Vault epoch secret is not loaded.");

    private void ClearSecrets()
    {
        foreach (byte[] secret in epochSecrets.Values)
        {
            CryptographicOperations.ZeroMemory(secret);
        }

        epochSecrets.Clear();
        vaultId = null;
        deviceId = null;
        sourceSequence = 0;
    }

    private VaultId RequireVaultId() => vaultId ?? throw new InvalidOperationException("Vault is not initialized.");

    private DeviceId RequireDeviceId() => deviceId ?? throw new InvalidOperationException("Device identity is not initialized.");

    private void EnsureInitialized()
    {
        ThrowIfDisposed();
        if (!initialized)
        {
            throw new InvalidOperationException("Vault must be initialized before use.");
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);

    private static VaultId NewVaultId() => VaultId.Parse(Guid.CreateVersion7().ToString("D"));

    private static FolderId NewFolderId() => FolderId.Parse(Guid.CreateVersion7().ToString("D"));

    private static ItemId NewItemId() => ItemId.Parse(Guid.CreateVersion7().ToString("D"));

    private static VersionId NewVersionId() => VersionId.Parse(Guid.CreateVersion7().ToString("D"));

    private static EventId NewEventId() => EventId.Parse(Guid.CreateVersion7().ToString("D"));

    private static string WireContentType(VaultContentType contentType) => contentType switch
    {
        VaultContentType.Text => "TEXT",
        VaultContentType.Url => "URL",
        VaultContentType.File => "FILE",
        _ => throw new ArgumentOutOfRangeException(nameof(contentType)),
    };

    private static VaultContentType ParseContentType(string value) => value switch
    {
        "TEXT" => VaultContentType.Text,
        "URL" => VaultContentType.Url,
        "FILE" => VaultContentType.File,
        _ => throw new InvalidDataException("Unknown Vault content type."),
    };

    private static string WirePolicy(SyncPolicy policy) => policy switch
    {
        SyncPolicy.LocalOnly => "LOCAL_ONLY",
        SyncPolicy.SelectedDevices => "SELECTED_DEVICES",
        SyncPolicy.AllPairedDevices => "ALL_PAIRED_DEVICES",
        _ => throw new ArgumentOutOfRangeException(nameof(policy)),
    };

    private static SyncPolicy ParsePolicy(string value) => value switch
    {
        "LOCAL_ONLY" => SyncPolicy.LocalOnly,
        "SELECTED_DEVICES" => SyncPolicy.SelectedDevices,
        "ALL_PAIRED_DEVICES" => SyncPolicy.AllPairedDevices,
        _ => throw new InvalidDataException("Unknown Vault sync policy."),
    };

    private static SyncPolicySetting ToPolicySetting(string? policy, IReadOnlyList<string> selectedDevices) =>
        new(
            policy is null ? null : ParsePolicy(policy),
            selectedDevices.Select(DeviceId.Parse).ToHashSet());

    private static VaultFolder ToDomainFolder(FolderState folder) => new(
        FolderId.Parse(folder.Record.FolderId),
        folder.Record.ParentId is null ? null : FolderId.Parse(folder.Record.ParentId),
        folder.Metadata.Name,
        folder.Metadata.Description,
        folder.Metadata.SortOrder,
        ToPolicySetting(folder.Metadata.Policy, folder.Metadata.SelectedDevices),
        VersionId.Parse(folder.Record.CurrentVersionId),
        folder.Metadata.DeletedAt);

    private sealed record FolderState(EncryptedFolderRecord Record, FolderMetadata Metadata);

    private sealed record ItemState(EncryptedItemRecord Record, ItemMetadata Metadata, string? Text);

    private sealed record FolderMetadata(
        string Name,
        string? Description,
        long SortOrder,
        string? Policy,
        string[] SelectedDevices,
        string? TemplateRole,
        DateTimeOffset UpdatedAt,
        DateTimeOffset? DeletedAt = null);

    private sealed record ItemMetadata(
        string Title,
        string? FileName,
        long SizeBytes,
        string? Policy,
        string[] SelectedDevices,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt,
        DateTimeOffset? DeletedAt);

    private sealed record EventPayload(
        string Operation,
        string EntityKind,
        string EntityId,
        string? BaseVersionId,
        string NewVersionId);

    private sealed record FileManifestPayload(
        string FileId,
        string GenerationId,
        string OriginDeviceId,
        string FileName,
        string ContentType,
        long SizeBytes,
        int ChunkSize,
        int ChunkCount);

    private sealed record DeletedAtPayload(DateTimeOffset DeletedAt);
}
