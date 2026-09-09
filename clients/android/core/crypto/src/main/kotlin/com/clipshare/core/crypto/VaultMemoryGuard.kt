package com.clipshare.core.crypto

import java.time.Duration
import java.time.Instant

class VaultMemoryGuard(
    private val clearSensitiveState: () -> Unit,
    private val backgroundLimit: Duration = Duration.ofMinutes(DEFAULT_BACKGROUND_MINUTES),
) {
    private var backgroundSince: Instant? = null

    init {
        require(!backgroundLimit.isNegative) { "The background limit cannot be negative." }
    }

    fun onBackground(now: Instant) {
        backgroundSince = now
    }

    fun onForeground(now: Instant) {
        val started = backgroundSince
        backgroundSince = null
        if (started != null && !now.isBefore(started.plus(backgroundLimit))) clearSensitiveState()
    }

    fun onSystemLocked() {
        backgroundSince = null
        clearSensitiveState()
    }

    fun onProcessExit() {
        backgroundSince = null
        clearSensitiveState()
    }

    private companion object {
        const val DEFAULT_BACKGROUND_MINUTES = 10L
    }
}
