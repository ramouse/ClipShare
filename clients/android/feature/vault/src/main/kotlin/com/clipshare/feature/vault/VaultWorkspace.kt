package com.clipshare.feature.vault

@Suppress("TooManyFunctions")
interface VaultWorkspace : AutoCloseable {
    suspend fun initialize()

    suspend fun load(section: VaultSection, query: String?): VaultSnapshot

    suspend fun createFolder(command: CreateVaultFolderCommand)

    suspend fun renameFolder(command: RenameVaultFolderCommand)

    suspend fun saveText(command: SaveVaultTextCommand)

    suspend fun importFile(command: ImportVaultFileCommand)

    suspend fun exportFile(command: ExportVaultFileCommand)

    suspend fun updateText(command: UpdateVaultTextCommand)

    suspend fun move(selection: Set<VaultEntitySelection>, targetFolderId: String)

    suspend fun moveToTrash(selection: Set<VaultEntitySelection>, includeFolderContents: Boolean)

    suspend fun restore(selection: Set<VaultEntitySelection>)

    suspend fun purge(selection: Set<VaultEntitySelection>)

    suspend fun setPolicy(selection: Set<VaultEntitySelection>, command: VaultPolicyCommand)

    fun clearSensitiveState()
}
