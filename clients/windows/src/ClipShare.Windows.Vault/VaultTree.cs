namespace ClipShare.Windows.Vault;

public sealed class VaultTree
{
    public const int MaximumDepth = 8;
    private readonly IReadOnlyDictionary<FolderId, VaultFolder> folders;

    public VaultTree(IEnumerable<VaultFolder> folders)
    {
        ArgumentNullException.ThrowIfNull(folders);
        var list = folders.ToList();
        if (list.Select(folder => folder.Id).Distinct().Count() != list.Count)
        {
            throw new ArgumentException("Duplicate folder ID.", nameof(folders));
        }

        this.folders = list.ToDictionary(folder => folder.Id);

        ValidateAll();
    }

    public int Depth(FolderId folderId) => Depth(folderId, []);

    public SyncPolicySetting EffectivePolicy(FolderId folderId)
    {
        VaultFolder? current = RequireFolder(folderId);
        HashSet<FolderId> visited = [];
        while (current is not null)
        {
            if (!visited.Add(current.Id))
            {
                throw new InvalidOperationException("Folder cycle detected.");
            }

            if (current.SyncPolicy.Override is not null)
            {
                return current.SyncPolicy;
            }

            current = current.ParentId is FolderId parent ? RequireFolder(parent) : null;
        }

        return new SyncPolicySetting(SyncPolicy.LocalOnly);
    }

    public SyncPolicySetting EffectivePolicy(VaultItem item) =>
        item.SyncPolicy.Override is null ? EffectivePolicy(item.FolderId) : item.SyncPolicy;

    public void ValidateMove(IReadOnlySet<FolderId> movingIds, FolderId? targetParentId)
    {
        ArgumentNullException.ThrowIfNull(movingIds);
        if (movingIds.Count == 0)
        {
            throw new ArgumentException("At least one folder must move.", nameof(movingIds));
        }

        foreach (var movingId in movingIds)
        {
            _ = RequireFolder(movingId);
        }

        if (targetParentId is FolderId target)
        {
            _ = RequireFolder(target);
            if (movingIds.Contains(target))
            {
                throw new InvalidOperationException("A folder cannot move into itself.");
            }

            var descendants = movingIds.SelectMany(DescendantsOf).ToHashSet();
            if (descendants.Contains(target))
            {
                throw new InvalidOperationException("A folder cannot move below its descendant.");
            }
        }

        var targetDepth = targetParentId is FolderId parent ? Depth(parent) : 0;
        foreach (var movingId in movingIds)
        {
            if (folders[movingId].ParentId is FolderId oldParent && movingIds.Contains(oldParent))
            {
                continue;
            }

            if (targetDepth + SubtreeHeight(movingId) > MaximumDepth)
            {
                throw new InvalidOperationException("The move would exceed the maximum folder depth.");
            }
        }
    }

    private void ValidateAll()
    {
        foreach (var folder in folders.Values)
        {
            if (folder.ParentId is FolderId parent)
            {
                _ = RequireFolder(parent);
            }

            if (Depth(folder.Id) > MaximumDepth)
            {
                throw new InvalidOperationException($"Folder depth exceeds {MaximumDepth}.");
            }
        }
    }

    private int Depth(FolderId folderId, HashSet<FolderId> visited)
    {
        if (!visited.Add(folderId))
        {
            throw new InvalidOperationException("Folder cycle detected.");
        }

        var folder = RequireFolder(folderId);
        return 1 + (folder.ParentId is FolderId parent ? Depth(parent, visited) : 0);
    }

    private IEnumerable<FolderId> DescendantsOf(FolderId folderId)
    {
        HashSet<FolderId> result = [];
        Queue<FolderId> pending = new();
        pending.Enqueue(folderId);
        while (pending.TryDequeue(out var parent))
        {
            foreach (var child in folders.Values.Where(candidate => candidate.ParentId == parent))
            {
                if (result.Add(child.Id))
                {
                    pending.Enqueue(child.Id);
                }
            }
        }

        return result;
    }

    private int SubtreeHeight(FolderId folderId)
    {
        var heights = folders.Values
            .Where(candidate => candidate.ParentId == folderId)
            .Select(candidate => SubtreeHeight(candidate.Id));
        return 1 + heights.DefaultIfEmpty(0).Max();
    }

    private VaultFolder RequireFolder(FolderId id) =>
        folders.TryGetValue(id, out var folder)
            ? folder
            : throw new InvalidOperationException("Unknown folder ID.");
}

public static class DefaultFolderInitializer
{
    public static IReadOnlyList<string> Names { get; } = ["收件箱", "密码", "工作", "私人", "其他"];

    public static IReadOnlyList<VaultFolder> Create(
        bool alreadyInitialized,
        IReadOnlyCollection<VaultFolder> existingFolders,
        Func<FolderId> nextFolderId,
        Func<VersionId> nextVersionId)
    {
        ArgumentNullException.ThrowIfNull(existingFolders);
        ArgumentNullException.ThrowIfNull(nextFolderId);
        ArgumentNullException.ThrowIfNull(nextVersionId);
        if (alreadyInitialized)
        {
            return [];
        }

        if (existingFolders.Count != 0)
        {
            throw new InvalidOperationException("Incomplete initialization must be resumed, not rebuilt.");
        }

        return Names.Select((name, index) => new VaultFolder(
            nextFolderId(),
            null,
            name,
            null,
            index,
            new SyncPolicySetting(null),
            nextVersionId(),
            null)).ToArray();
    }
}
