package com.clipshare.platform.android.vault

internal fun ByteArray.toLowerHex(): String = joinToString("") { byte ->
    val value = byte.toInt() and BYTE_MASK
    "${HEX[value ushr NIBBLE_BITS]}${HEX[value and LOW_NIBBLE_MASK]}"
}

private const val HEX = "0123456789abcdef"
private const val BYTE_MASK = 0xff
private const val NIBBLE_BITS = 4
private const val LOW_NIBBLE_MASK = 0x0f
