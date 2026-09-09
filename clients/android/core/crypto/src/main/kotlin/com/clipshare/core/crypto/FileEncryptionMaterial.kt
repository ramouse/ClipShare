package com.clipshare.core.crypto

import java.security.SecureRandom

class FileEncryptionMaterial private constructor(
    private val fileDek: ByteArray,
    private val noncePrefix: ByteArray,
) : AutoCloseable {
    private var closed = false

    fun copyFileDek(): ByteArray {
        check(!closed) { "File encryption material is no longer available." }
        return fileDek.copyOf()
    }

    fun allocationFor(chunkIndex: Long): NonceAllocation {
        check(!closed) { "File encryption material is no longer available." }
        require(chunkIndex >= 0) { "Chunk indexes are non-negative 63-bit counters." }
        return NonceAllocation(noncePrefix.copyOf(), chunkIndex)
    }

    override fun close() {
        fileDek.fill(0)
        noncePrefix.fill(0)
        closed = true
    }

    companion object {
        const val MAXIMUM_PLAINTEXT_CHUNK_BYTES = 1_048_576

        fun create(): FileEncryptionMaterial {
            val random = SecureRandom()
            return FileEncryptionMaterial(
                ByteArray(FILE_DEK_BYTES).also(random::nextBytes),
                ByteArray(NONCE_PREFIX_BYTES).also(random::nextBytes),
            )
        }
    }
}
