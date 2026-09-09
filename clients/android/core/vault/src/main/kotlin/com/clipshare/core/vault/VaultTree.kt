package com.clipshare.core.vault

const val MAX_FOLDER_DEPTH = 8

class VaultTree(folders: Collection<VaultFolder>) {
    private val byId = folders.associateBy { it.id }

    init {
        require(byId.size == folders.size) { "Duplicate folder ID." }
        validateAll()
    }

    fun depth(folderId: FolderId): Int = depth(folderId, mutableSetOf())

    fun effectivePolicy(folderId: FolderId): SyncPolicySetting {
        var current: VaultFolder? = requireFolder(folderId)
        val visited = mutableSetOf<FolderId>()
        while (current != null) {
            require(visited.add(current.id)) { "Folder cycle detected." }
            if (current.syncPolicy.override != null) return current.syncPolicy
            current = current.parentId?.let(::requireFolder)
        }
        return SyncPolicySetting(SyncPolicy.LOCAL_ONLY)
    }

    fun effectivePolicy(item: VaultItem): SyncPolicySetting =
        if (item.syncPolicy.override == null) effectivePolicy(item.folderId) else item.syncPolicy

    fun validateMove(movingIds: Set<FolderId>, targetParentId: FolderId?) {
        require(movingIds.isNotEmpty()) { "At least one folder must move." }
        movingIds.forEach(::requireFolder)
        targetParentId?.let(::requireFolder)
        require(targetParentId !in movingIds) { "A folder cannot move into itself." }

        val descendants = movingIds.flatMapTo(mutableSetOf()) { descendantsOf(it) }
        require(targetParentId !in descendants) { "A folder cannot move below its descendant." }

        val targetDepth = targetParentId?.let(::depth) ?: 0
        for (movingId in movingIds) {
            if (byId.getValue(movingId).parentId in movingIds) continue
            val subtreeHeight = subtreeHeight(movingId)
            require(targetDepth + subtreeHeight <= MAX_FOLDER_DEPTH) {
                "The move would exceed the maximum folder depth."
            }
        }
    }

    private fun validateAll() {
        for (folder in byId.values) {
            folder.parentId?.let { requireFolder(it) }
            require(depth(folder.id) <= MAX_FOLDER_DEPTH) { "Folder depth exceeds $MAX_FOLDER_DEPTH." }
        }
    }

    private fun depth(folderId: FolderId, visited: MutableSet<FolderId>): Int {
        require(visited.add(folderId)) { "Folder cycle detected." }
        val folder = requireFolder(folderId)
        return 1 + (folder.parentId?.let { depth(it, visited) } ?: 0)
    }

    private fun descendantsOf(folderId: FolderId): Set<FolderId> {
        val result = mutableSetOf<FolderId>()
        val pending = ArrayDeque<FolderId>()
        pending.add(folderId)
        while (pending.isNotEmpty()) {
            val parent = pending.removeFirst()
            for (child in byId.values.filter { it.parentId == parent }) {
                if (result.add(child.id)) pending.add(child.id)
            }
        }
        return result
    }

    private fun subtreeHeight(folderId: FolderId): Int {
        val childHeights = byId.values
            .filter { it.parentId == folderId }
            .map { subtreeHeight(it.id) }
        return 1 + (childHeights.maxOrNull() ?: 0)
    }

    private fun requireFolder(folderId: FolderId): VaultFolder =
        requireNotNull(byId[folderId]) { "Unknown folder ID." }
}

object DefaultFolderInitializer {
    val names = listOf("收件箱", "密码", "工作", "私人", "其他")

    fun create(
        alreadyInitialized: Boolean,
        existingFolders: Collection<VaultFolder>,
        nextFolderId: () -> FolderId,
        nextVersionId: () -> VersionId,
    ): List<VaultFolder> {
        if (alreadyInitialized) return emptyList()
        require(existingFolders.isEmpty()) { "Incomplete initialization must be resumed, not rebuilt." }
        return names.mapIndexed { index, name ->
            VaultFolder(
                id = nextFolderId(),
                parentId = null,
                name = name,
                description = null,
                sortOrder = index.toLong(),
                syncPolicy = SyncPolicySetting(null),
                currentVersionId = nextVersionId(),
                deletedAt = null,
            )
        }
    }
}
