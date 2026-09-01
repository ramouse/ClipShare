package com.clipshare.core.network

import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable

@Serializable
internal data class ShareCreateRequestDto(
    val content: String,
    val expiry: String,
    @SerialName("max_views") val maxViews: Int?,
)

@Serializable
internal data class ShareCreatedResponseDto(
    val code: String,
    val url: String,
    @SerialName("expires_at") val expiresAt: String?,
    @SerialName("max_views") val maxViews: Int?,
    @SerialName("created_at") val createdAt: String,
)

@Serializable
internal data class ShareReadResponseDto(
    val code: String,
    val content: String,
    @SerialName("expires_at") val expiresAt: String?,
    @SerialName("remaining_views") val remainingViews: Int?,
    @SerialName("created_at") val createdAt: String,
)

@Serializable
internal data class FileCreatedResponseDto(
    val code: String,
    val url: String,
    @SerialName("original_name") val originalName: String,
    @SerialName("size_bytes") val sizeBytes: Long,
    val encrypted: Boolean,
    @SerialName("expires_at") val expiresAt: String?,
    @SerialName("max_views") val maxViews: Int?,
    @SerialName("created_at") val createdAt: String,
)

@Serializable
internal data class FileReadResponseDto(
    val code: String,
    @SerialName("original_name") val originalName: String,
    @SerialName("size_bytes") val sizeBytes: Long,
    val encrypted: Boolean,
    @SerialName("content_type") val contentType: String,
    @SerialName("preview_available") val previewAvailable: Boolean,
    @SerialName("expires_at") val expiresAt: String?,
    @SerialName("remaining_views") val remainingViews: Int?,
    @SerialName("created_at") val createdAt: String,
    val kind: String = "file",
)

@Serializable
internal data class ProblemDetailDto(
    val type: String,
    val title: String,
    val status: Int,
    val detail: String,
)
