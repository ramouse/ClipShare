package com.clipshare.core.network

import okhttp3.HttpUrl
import okhttp3.HttpUrl.Companion.toHttpUrlOrNull

enum class BuildChannel {
    DEBUG_OR_TEST,
    RELEASE,
}

sealed interface EndpointValidation {
    data class Accepted(val baseUrl: HttpUrl) : EndpointValidation
    data class Rejected(val message: String) : EndpointValidation
}

object EndpointPolicy {
    private val localHosts = setOf("localhost", "127.0.0.1", "10.0.2.2")

    fun validate(raw: String, channel: BuildChannel): EndpointValidation {
        val parsed = raw.trim().toHttpUrlOrNull()
        return when {
            parsed == null -> EndpointValidation.Rejected("服务器地址必须是完整的 HTTP(S) URL")
            parsed.username.isNotEmpty() || parsed.password.isNotEmpty() ->
                EndpointValidation.Rejected("服务器地址不得包含用户名或密码")
            parsed.query != null || parsed.fragment != null ->
                EndpointValidation.Rejected("服务器地址不得包含查询参数或片段")
            channel == BuildChannel.RELEASE && !parsed.isHttps ->
                EndpointValidation.Rejected("Release 版本只允许 HTTPS 服务器")
            channel == BuildChannel.DEBUG_OR_TEST && parsed.host !in localHosts ->
                EndpointValidation.Rejected("Debug/Test 版本只允许回环地址或模拟器宿主 10.0.2.2")
            else -> EndpointValidation.Accepted(parsed.newBuilder().apply {
                if (!parsed.encodedPath.endsWith('/')) {
                    addPathSegment("")
                }
            }.build())
        }
    }
}
