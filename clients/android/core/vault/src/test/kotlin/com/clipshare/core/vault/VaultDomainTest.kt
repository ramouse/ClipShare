package com.clipshare.core.vault

import java.time.Instant
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class VaultDomainTest {
    @Test
    fun `folder identifiers reject uppercase and malformed values`() {
        assertFails { FolderId.parse("AAAAAAAA-bbbb-4ccc-8ddd-eeeeeeeeeeee") }
        assertFails { FolderId.parse("not-a-uuid") }
        assertEquals(
            "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee",
            VaultId.parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee").value,
        )
        assertFails { ItemId.parse("AAAAAAAA-bbbb-4ccc-8ddd-eeeeeeeeeeee") }
        assertFails { VersionId.parse("aaaaaaaa-bbbb-4ccc-7ddd-eeeeeeeeeeee") }
        assertFails { EventId.parse("aaaaaaaa-bbbb-4ccc-cddd-eeeeeeeeeeee") }
        assertFails { DeviceId.parse("aaaaaaaa-bbbb-4ccc-8ddd-EEEEEEEEEEEE") }
    }

    @Test
    fun `vault state rejects unsupported formats and nonpositive epochs`() {
        val id = VaultId.parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee")
        assertEquals(1, VaultState(id, 1, 1, initialized = false).formatVersion)
        assertFails { VaultState(id, 2, 1, initialized = false) }
        assertFails { VaultState(id, 1, 0, initialized = false) }
    }

    @Test
    fun `tree permits duplicate names and exactly eight levels`() {
        val folders = chain(8, "密码")
        val tree = VaultTree(folders)
        assertEquals(8, tree.depth(folders.last().id))
        assertEquals(8, folders.count { it.name == "密码" })
        assertFails { VaultTree(chain(9, "same")) }
    }

    @Test
    fun `tree rejects cycles and moves below descendants`() {
        val one = folder(1, parent = id(2))
        val two = folder(2, parent = id(1))
        assertFails { VaultTree(listOf(one, two)) }

        val folders = chain(3, "ordinary")
        val tree = VaultTree(folders)
        assertFails { tree.validateMove(setOf(folders.first().id), folders.last().id) }
        assertFails { VaultTree(listOf(folder(1), folder(1))) }
        assertFails { VaultTree(listOf(folder(1, parent = id(99)))) }
        assertFails { tree.validateMove(emptySet(), null) }
        assertFails { tree.validateMove(setOf(id(99)), null) }
        assertFails { tree.validateMove(setOf(folders.first().id), id(99)) }
        assertFails { tree.validateMove(setOf(folders.first().id), folders.first().id) }
    }

    @Test
    fun `move validates complete subtree depth before mutation`() {
        val left = chain(4, "left")
        val right = (5..9).mapIndexed { index, value ->
            folder(value, if (index == 0) null else id(value - 1), "right")
        }
        val tree = VaultTree(left + right)
        assertFails { tree.validateMove(setOf(left.first().id), right.last().id) }
    }

    @Test
    fun `sync policy inherits and selected devices require an explicit target`() {
        val root = folder(
            1,
            policy = SyncPolicySetting(SyncPolicy.ALL_PAIRED_DEVICES),
        )
        val child = folder(2, parent = root.id, policy = SyncPolicySetting(null))
        val tree = VaultTree(listOf(root, child))
        assertEquals(SyncPolicy.ALL_PAIRED_DEVICES, tree.effectivePolicy(child.id).override)
        assertEquals(SyncPolicy.LOCAL_ONLY, VaultTree(listOf(folder(3))).effectivePolicy(id(3)).override)
        val selectedDevice = DeviceId.parse("11111111-2222-4333-8444-555555555555")
        val selected = SyncPolicySetting(SyncPolicy.SELECTED_DEVICES, setOf(selectedDevice))
        assertEquals(selected, selected)
        assertFails { SyncPolicySetting(SyncPolicy.SELECTED_DEVICES) }
        assertFails {
            SyncPolicySetting(
                SyncPolicy.LOCAL_ONLY,
                setOf(selectedDevice),
            )
        }

        val inheritedItem = item(folderId = child.id, policy = SyncPolicySetting(null))
        assertEquals(SyncPolicy.ALL_PAIRED_DEVICES, tree.effectivePolicy(inheritedItem).override)
        assertEquals(selected, tree.effectivePolicy(inheritedItem.copy(syncPolicy = selected)))
    }

    @Test
    fun `password is an ordinary folder and defaults are only created once`() {
        var next = 20
        val defaults = DefaultFolderInitializer.create(
            alreadyInitialized = false,
            existingFolders = emptyList(),
            nextFolderId = { id(next++) },
            nextVersionId = { version(next++) },
        )
        assertEquals(DefaultFolderInitializer.names, defaults.map { it.name })
        assertEquals(
            emptyList<VaultFolder>(),
            DefaultFolderInitializer.create(true, defaults, { id(90) }, { version(90) }),
        )
        assertFails {
            DefaultFolderInitializer.create(false, defaults, { id(91) }, { version(91) })
        }

        val password = defaults.single { it.name == "密码" }
        val renamed = password.copy(name = "renamed")
        assertEquals(password.syncPolicy, renamed.syncPolicy)
        assertEquals(password.parentId, renamed.parentId)
    }

    @Test
    fun `items compare payload bytes rather than array identity`() {
        val now = Instant.parse("2026-09-02T00:00:00Z")
        val first = item(now = now)
        assertEquals(first, first.copy(payload = byteArrayOf(1, 2, 3)))
        assertTrue(first.hashCode() == first.copy(payload = byteArrayOf(1, 2, 3)).hashCode())
        assertFalse(first.equals("not-an-item"))
        assertTrue(first != first.copy(id = ItemId.parse("88888888-7777-4666-8555-444444444444")))
        assertTrue(first != first.copy(folderId = id(2)))
        assertTrue(first != first.copy(contentType = VaultContentType.URL))
        assertTrue(first != first.copy(title = "other"))
        assertTrue(first != first.copy(payload = byteArrayOf(9)))
        assertTrue(first != first.copy(syncPolicy = SyncPolicySetting(SyncPolicy.LOCAL_ONLY)))
        assertTrue(first != first.copy(currentVersionId = version(2)))
        assertTrue(first != first.copy(createdAt = now.plusSeconds(1)))
        assertTrue(first != first.copy(updatedAt = now.plusSeconds(1)))
        assertTrue(first != first.copy(deletedAt = now))
        assertTrue(first.copy(deletedAt = now).hashCode() != first.hashCode())
    }

    private fun chain(count: Int, name: String): List<VaultFolder> =
        (1..count).map { value -> folder(value, if (value == 1) null else id(value - 1), name) }

    private fun folder(
        value: Int,
        parent: FolderId? = null,
        name: String = "folder-$value",
        policy: SyncPolicySetting = SyncPolicySetting(null),
    ) = VaultFolder(
        id = id(value),
        parentId = parent,
        name = name,
        description = null,
        sortOrder = value.toLong(),
        syncPolicy = policy,
        currentVersionId = version(value),
        deletedAt = null,
    )

    private fun id(value: Int) = FolderId.parse("00000000-0000-4000-8000-${value.toString().padStart(12, '0')}")

    private fun version(value: Int) = VersionId.parse("10000000-0000-4000-8000-${value.toString().padStart(12, '0')}")

    private fun item(
        folderId: FolderId = id(1),
        policy: SyncPolicySetting = SyncPolicySetting(null),
        now: Instant = Instant.parse("2026-09-02T00:00:00Z"),
    ) = VaultItem(
        id = ItemId.parse("99999999-8888-4777-8666-555555555555"),
        folderId = folderId,
        contentType = VaultContentType.TEXT,
        title = "title",
        payload = byteArrayOf(1, 2, 3),
        syncPolicy = policy,
        currentVersionId = version(1),
        createdAt = now,
        updatedAt = now,
        deletedAt = null,
    )

    private fun assertFails(operation: () -> Unit) {
        var failed = false
        try {
            operation()
        } catch (_: IllegalArgumentException) {
            failed = true
        }
        assertTrue("Expected IllegalArgumentException", failed)
    }
}
