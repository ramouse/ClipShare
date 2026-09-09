package com.clipshare.platform.android.vault

import com.clipshare.core.vault.InitializationPhase
import com.clipshare.core.vault.VaultId
import com.clipshare.core.vault.WrapperObservation
import java.io.File
import java.io.FileOutputStream
import java.nio.file.FileAlreadyExistsException
import java.nio.file.Files
import java.nio.file.StandardCopyOption
import java.security.MessageDigest
import kotlinx.serialization.Serializable
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json

@Serializable
data class AndroidPlatformWrapper(
    val schemaVersion: Int = 1,
    val platform: String = "ANDROID",
    val algorithm: String = "ANDROID-KEYSTORE-A256GCM",
    val vaultId: String,
    val initializationId: String,
    val keyEpoch: Long,
    val state: String,
    val keyReference: String,
    val nonce: String,
    val protectedSecret: String,
)

@Serializable
private data class AndroidNonceReservation(
    val keyAlias: String,
    val nonce: String,
    val owner: String,
)

class AndroidPlatformWrapperStore(private val directory: File) {
    private val json = Json { encodeDefaults = true; ignoreUnknownKeys = false }

    fun read(vaultId: String, epoch: Long): AndroidPlatformWrapper? {
        val file = wrapperFile(vaultId, epoch)
        if (!file.exists()) return null
        return json.decodeFromString<AndroidPlatformWrapper>(file.readText(Charsets.UTF_8)).also(::validate)
    }

    fun observe(vaultId: VaultId, epoch: Long): WrapperObservation? = read(vaultId.value, epoch)?.let { wrapper ->
        WrapperObservation(
            vaultId = VaultId.parse(wrapper.vaultId),
            initializationId = wrapper.initializationId,
            keyEpoch = wrapper.keyEpoch,
            digest = digest(wrapper),
            phase = InitializationPhase.valueOf(wrapper.state),
        )
    }

    fun writeAtomically(wrapper: AndroidPlatformWrapper) {
        validate(wrapper)
        directory.mkdirs()
        val destination = wrapperFile(wrapper.vaultId, wrapper.keyEpoch)
        val existing = read(wrapper.vaultId, wrapper.keyEpoch)
        if (existing != null) {
            check(existing.copy(state = wrapper.state) == wrapper) {
                "An existing epoch wrapper cannot be replaced with different key material."
            }
            check(existing.state != InitializationPhase.READY.name || wrapper.state == InitializationPhase.READY.name) {
                "A READY epoch wrapper cannot be downgraded."
            }
        }
        val temporary = File(directory, ".${destination.name}.${wrapper.initializationId}.tmp")
        val staleTemporaries = directory.listFiles { file ->
            file.name.startsWith(".${destination.name}.") && file.name.endsWith(".tmp")
        }.orEmpty()
        check(staleTemporaries.isEmpty()) { "A stale wrapper temporary file requires recovery." }
        reserveNonce(wrapper.keyReference, wrapper.nonce, destination.name)
        var committed = false
        try {
            val encoded = json.encodeToString(wrapper).toByteArray(Charsets.UTF_8)
            FileOutputStream(temporary).use { output ->
                output.write(encoded)
                output.fd.sync()
            }
            Files.move(
                temporary.toPath(),
                destination.toPath(),
                StandardCopyOption.ATOMIC_MOVE,
                StandardCopyOption.REPLACE_EXISTING,
            )
            committed = true
        } finally {
            if (!committed) temporary.delete()
        }
    }

    fun usedNonces(keyAlias: String): Set<String> {
        require(KEY_ALIAS_PATTERN.matches(keyAlias)) { "Invalid Vault Keystore alias." }
        if (!directory.exists()) return emptySet()
        val wrappers = directory.listFiles { file -> file.name.endsWith(".wrapper.json") || file.name.endsWith(".tmp") }
            .orEmpty()
            .map { file ->
                json.decodeFromString<AndroidPlatformWrapper>(file.readText(Charsets.UTF_8)).also(::validate)
            }
            .filter { it.keyReference == keyAlias }
            .mapTo(mutableSetOf()) { it.nonce }
        directory.listFiles { file -> file.name.endsWith(NONCE_RESERVATION_SUFFIX) }
            .orEmpty()
            .map(::readReservation)
            .filter { it.keyAlias == keyAlias }
            .mapTo(wrappers) { it.nonce }
        return wrappers
    }

    fun digest(wrapper: AndroidPlatformWrapper): String {
        validate(wrapper)
        return MessageDigest.getInstance("SHA-256")
            .digest(decodeB64url(wrapper.protectedSecret))
            .toLowerHex()
    }

    private fun wrapperFile(vaultId: String, epoch: Long): File {
        require(LOWERCASE_UUID.matches(vaultId)) { "Invalid wrapper Vault ID." }
        require(epoch > 0) { "Invalid wrapper epoch." }
        return File(directory, "$vaultId.$epoch.wrapper.json")
    }

    private fun reserveNonce(keyAlias: String, nonce: String, owner: String) {
        val reservation = AndroidNonceReservation(keyAlias, nonce, owner).also(::validateReservation)
        val aliasDigest = MessageDigest.getInstance("SHA-256")
            .digest(keyAlias.toByteArray(Charsets.US_ASCII))
            .toLowerHex()
        val file = File(directory, ".nonce.$aliasDigest.$nonce$NONCE_RESERVATION_SUFFIX")
        try {
            Files.createFile(file.toPath())
            FileOutputStream(file).use { output ->
                output.write(json.encodeToString(reservation).toByteArray(Charsets.UTF_8))
                output.fd.sync()
            }
        } catch (_: FileAlreadyExistsException) {
            check(readReservation(file) == reservation) { "A wrapper nonce is already reserved by another epoch." }
        }
    }

    private fun readReservation(file: File): AndroidNonceReservation =
        json.decodeFromString<AndroidNonceReservation>(file.readText(Charsets.UTF_8)).also(::validateReservation)

    private fun validateReservation(reservation: AndroidNonceReservation) {
        require(KEY_ALIAS_PATTERN.matches(reservation.keyAlias)) { "Invalid nonce reservation alias." }
        require(decodeB64url(reservation.nonce).size == GCM_NONCE_BYTES) { "Invalid reserved wrapper nonce." }
        require(WRAPPER_FILE_PATTERN.matches(reservation.owner)) { "Invalid nonce reservation owner." }
    }

    private fun validate(wrapper: AndroidPlatformWrapper) {
        require(wrapper.schemaVersion == 1 && wrapper.platform == "ANDROID") { "Unsupported platform wrapper." }
        require(wrapper.algorithm == "ANDROID-KEYSTORE-A256GCM") { "Unsupported platform wrapper algorithm." }
        require(wrapper.keyEpoch > 0) { "Invalid wrapper epoch." }
        require(runCatching { InitializationPhase.valueOf(wrapper.state) }.isSuccess) { "Invalid wrapper state." }
        require(LOWERCASE_UUID.matches(wrapper.vaultId)) { "Invalid wrapper Vault ID." }
        require(LOWERCASE_UUID.matches(wrapper.initializationId)) { "Invalid wrapper initialization ID." }
        require(KEY_ALIAS_PATTERN.matches(wrapper.keyReference)) { "Invalid Vault Keystore alias." }
        require(decodeB64url(wrapper.nonce).size == GCM_NONCE_BYTES) { "Invalid wrapper nonce." }
        require(decodeB64url(wrapper.protectedSecret).size == PROTECTED_SECRET_BYTES) {
            "Invalid wrapped epoch secret."
        }
    }

    private fun decodeB64url(value: String): ByteArray {
        require(value.matches(BASE64_PATTERN) && value.length % BASE64_QUANTUM != 1) {
            "Invalid wrapper Base64URL."
        }
        return java.util.Base64.getUrlDecoder().decode(value).also {
            require(java.util.Base64.getUrlEncoder().withoutPadding().encodeToString(it) == value) {
                "Non-canonical wrapper Base64URL."
            }
        }
    }

    private companion object {
        val BASE64_PATTERN = Regex("^[A-Za-z0-9_-]+$")
        val LOWERCASE_UUID = Regex("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$")
        val KEY_ALIAS_PATTERN = Regex("^clipshare\\.vault\\.wrap\\.v1\\.[0-9a-f]{32}$")
        val WRAPPER_FILE_PATTERN = Regex(
            "^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[89ab][0-9a-f]{3}-[0-9a-f]{12}\\.[1-9][0-9]*\\.wrapper\\.json$",
        )
        const val BASE64_QUANTUM = 4
        const val GCM_NONCE_BYTES = 12
        const val PROTECTED_SECRET_BYTES = 48
        const val NONCE_RESERVATION_SUFFIX = ".reservation.json"
    }
}
