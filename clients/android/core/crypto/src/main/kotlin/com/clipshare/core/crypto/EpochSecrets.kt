package com.clipshare.core.crypto

import java.security.SecureRandom

const val EPOCH_SECRET_BYTES = 32

fun interface SecretGenerator {
    fun generate(size: Int): ByteArray
}

object SecureRandomSecretGenerator : SecretGenerator {
    private val random = SecureRandom()

    override fun generate(size: Int): ByteArray = ByteArray(size).also(random::nextBytes)
}

class EpochSecret private constructor(private val value: ByteArray) : AutoCloseable {
    private var closed = false

    fun copyBytes(): ByteArray {
        check(!closed) { "Epoch secret is no longer available." }
        return value.copyOf()
    }

    override fun close() {
        value.fill(0)
        closed = true
    }

    companion object {
        fun fromBytes(value: ByteArray): EpochSecret {
            require(value.size == EPOCH_SECRET_BYTES) { "Epoch secrets are exactly 32 bytes." }
            return EpochSecret(value.copyOf())
        }
    }
}

class EpochKeyRing(
    initialEpoch: Long,
    initialSecret: EpochSecret,
) : AutoCloseable {
    private val secrets = mutableMapOf(initialEpoch to initialSecret)

    var currentWriteEpoch: Long = initialEpoch
        private set

    init {
        require(initialEpoch > 0) { "Epoch numbers are positive." }
    }

    fun secret(epoch: Long): EpochSecret =
        requireNotNull(secrets[epoch]) { "The requested epoch secret is unavailable." }

    fun rotate(generator: SecretGenerator = SecureRandomSecretGenerator): Long {
        check(currentWriteEpoch < Long.MAX_VALUE) { "Epoch number exhausted." }
        repeat(MAX_GENERATION_ATTEMPTS) {
            val candidate = generator.generate(EPOCH_SECRET_BYTES)
            try {
                check(candidate.size == EPOCH_SECRET_BYTES) { "Secret generator returned the wrong length." }
                if (!matchesExistingSecret(candidate)) {
                    currentWriteEpoch += 1
                    secrets[currentWriteEpoch] = EpochSecret.fromBytes(candidate)
                    return currentWriteEpoch
                }
            } finally {
                candidate.fill(0)
            }
        }
        error("Secret generator repeated a retained epoch secret.")
    }

    fun removeHistoricalEpoch(epoch: Long, referenceCount: Long) {
        require(referenceCount == 0L) { "Referenced epoch secrets cannot be removed." }
        require(epoch != currentWriteEpoch) { "The active write epoch cannot be removed." }
        requireNotNull(secrets.remove(epoch)) { "The epoch secret is unavailable." }.close()
    }

    override fun close() {
        secrets.values.forEach(EpochSecret::close)
        secrets.clear()
    }

    private fun matchesExistingSecret(candidate: ByteArray): Boolean = secrets.values.any { secret ->
        val existing = secret.copyBytes()
        try {
            candidate.contentEquals(existing)
        } finally {
            existing.fill(0)
        }
    }

    private companion object {
        const val MAX_GENERATION_ATTEMPTS = 8
    }
}
