package com.clipshare.platform.android.vault

import androidx.room.Database
import androidx.room.RoomDatabase

@Database(
    entities = [
        VaultStateEntity::class,
        FolderEntity::class,
        ItemEntity::class,
        ItemVersionEntity::class,
        FileManifestEntity::class,
        FileChunkEntity::class,
        SyncEventEntity::class,
        DeviceKeyWrapperEntity::class,
        TombstoneEntity::class,
        NonceStateEntity::class,
        MigrationStateEntity::class,
    ],
    version = 1,
    exportSchema = true,
)
abstract class VaultRoomDatabase : RoomDatabase() {
    abstract fun vaultDao(): VaultRoomDao
}
