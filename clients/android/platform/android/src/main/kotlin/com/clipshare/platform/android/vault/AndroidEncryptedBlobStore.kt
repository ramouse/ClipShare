package com.clipshare.platform.android.vault

import java.io.File
import java.io.FileOutputStream
import java.nio.file.Files
import java.nio.file.StandardCopyOption
import java.security.MessageDigest
import java.util.UUID

data class AndroidStoredEncryptedChunk(
    val fileId: String,
    val generationId: String,
    val chunkIndex: Long,
    val ciphertextBytes: Long,
    val ciphertextSha256: String,
    val relativePath: String,
)

class AndroidEncryptedBlobStore(root: File) {
    private val root = root.canonicalFile

    fun writeChunkAtomically(
        fileId: String,
        generationId: String,
        chunkIndex: Long,
        ciphertextAndTag: ByteArray,
    ): AndroidStoredEncryptedChunk {
        validateUuid(fileId)
        validateUuid(generationId)
        require(chunkIndex >= 0) { "Chunk indexes are non-negative 63-bit counters." }
        require(ciphertextAndTag.size in GCM_TAG_BYTES..MAXIMUM_CIPHERTEXT_CHUNK_BYTES) {
            "Encrypted chunks must contain a tag and be bounded to one MiB plaintext."
        }

        val relative = relativePath(fileId, generationId, chunkIndex)
        val destination = resolve(relative)
        destination.parentFile?.mkdirs() ?: error("Encrypted chunk has no parent directory.")
        val temporary = File(destination.parentFile, ".${destination.name}.${UUID.randomUUID()}.tmp")
        val commitLock = File(destination.parentFile, ".${destination.name}.commit-lock")
        check(commitLock.createNewFile()) { "Encrypted chunk commit is already reserved." }
        var committed = false
        try {
            check(!destination.exists()) { "Encrypted chunks are immutable within a generation." }
            FileOutputStream(temporary).use { output ->
                output.write(ciphertextAndTag)
                output.fd.sync()
            }
            check(!destination.exists()) { "Encrypted chunks are immutable within a generation." }
            Files.move(temporary.toPath(), destination.toPath(), StandardCopyOption.ATOMIC_MOVE)
            committed = true
            return AndroidStoredEncryptedChunk(
                fileId,
                generationId,
                chunkIndex,
                ciphertextAndTag.size.toLong(),
                sha256(ciphertextAndTag),
                relative,
            )
        } finally {
            if (!committed) temporary.delete()
            commitLock.delete()
        }
    }

    fun readChunk(chunk: AndroidStoredEncryptedChunk): ByteArray {
        validateUuid(chunk.fileId)
        validateUuid(chunk.generationId)
        require(chunk.chunkIndex >= 0) { "Chunk indexes are non-negative 63-bit counters." }
        require(chunk.ciphertextBytes in GCM_TAG_BYTES.toLong()..MAXIMUM_CIPHERTEXT_CHUNK_BYTES.toLong()) {
            "Encrypted chunk length is outside the allowed bounds."
        }
        require(chunk.relativePath == relativePath(chunk.fileId, chunk.generationId, chunk.chunkIndex)) {
            "Encrypted chunk path does not match its manifest identity."
        }
        val file = resolve(chunk.relativePath)
        require(file.length() == chunk.ciphertextBytes) { "Encrypted chunk length does not match its manifest." }
        val result = file.readBytes()
        val digest = sha256(result)
        if (digest != chunk.ciphertextSha256) {
            result.fill(0)
            error("Encrypted chunk digest does not match its manifest.")
        }
        return result
    }

    private fun resolve(relativePath: String): File {
        require(!File(relativePath).isAbsolute) { "Encrypted chunk path must be relative." }
        val candidate = File(root, relativePath).canonicalFile
        require(candidate.path.startsWith(root.path + File.separator)) {
            "Encrypted chunk path escapes the Vault blob root."
        }
        return candidate
    }

    private fun relativePath(fileId: String, generationId: String, chunkIndex: Long): String =
        "$fileId/$generationId/chunk-${chunkIndex.toString().padStart(CHUNK_INDEX_WIDTH, '0')}.bin"

    private fun validateUuid(value: String) {
        require(LOWERCASE_UUID.matches(value)) { "Blob identifiers must be lowercase UUIDs." }
    }

    private fun sha256(value: ByteArray): String =
        MessageDigest.getInstance("SHA-256").digest(value).toLowerHex()

    private companion object {
        const val MAXIMUM_CIPHERTEXT_CHUNK_BYTES = 1_048_576 + 16
        const val GCM_TAG_BYTES = 16
        const val CHUNK_INDEX_WIDTH = 19
        val LOWERCASE_UUID = Regex("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$")
    }
}
