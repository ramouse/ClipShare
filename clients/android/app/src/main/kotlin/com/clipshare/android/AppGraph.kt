package com.clipshare.android

import android.content.ClipboardManager
import android.content.Context
import com.clipshare.core.model.GatewayAccess
import com.clipshare.core.model.GatewayProvider
import com.clipshare.core.model.IdempotencyKeyFactory
import com.clipshare.core.network.BuildChannel
import com.clipshare.core.network.ClipShareHttpClient
import com.clipshare.core.network.EndpointPolicy
import com.clipshare.core.network.EndpointValidation
import com.clipshare.core.network.OkHttpClipShareGateway
import com.clipshare.feature.receive.ReceiveController
import com.clipshare.feature.send.SendController
import com.clipshare.feature.settings.ForegroundClipboardCoordinator
import com.clipshare.feature.settings.SettingsController
import com.clipshare.platform.android.AndroidClipboardTextSource
import com.clipshare.platform.android.AndroidDocumentResolver
import com.clipshare.platform.android.AndroidSettingsRepository
import java.util.UUID
import kotlinx.coroutines.flow.first

class AppGraph(context: Context) {
    val settingsRepository = AndroidSettingsRepository(context)
    val settingsController = SettingsController(settingsRepository)
    val documentResolver = AndroidDocumentResolver(context.contentResolver)

    private val httpClient = ClipShareHttpClient.create()
    private val gatewayProvider = GatewayProvider {
        val configured = settingsRepository.settings.first().serverBaseUrl
        val effective = configured.ifBlank { BuildConfig.DEFAULT_BASE_URL }
        if (effective.isBlank()) {
            GatewayAccess.InvalidEndpoint("请先在设置中填写 HTTPS 服务器地址")
        } else {
            val channel = if (BuildConfig.ALLOW_LOCAL_ENDPOINTS) {
                BuildChannel.DEBUG_OR_TEST
            } else {
                BuildChannel.RELEASE
            }
            when (val validation = EndpointPolicy.validate(effective, channel)) {
                is EndpointValidation.Accepted -> GatewayAccess.Available(
                    OkHttpClipShareGateway(validation.baseUrl, httpClient),
                )

                is EndpointValidation.Rejected -> GatewayAccess.InvalidEndpoint(validation.message)
            }
        }
    }

    val sendController = SendController(
        gatewayProvider,
        IdempotencyKeyFactory { "a1:${UUID.randomUUID()}" },
    )
    val receiveController = ReceiveController(gatewayProvider)

    private val clipboardSource = AndroidClipboardTextSource(
        context.getSystemService(ClipboardManager::class.java),
    )
    val clipboardCoordinator = ForegroundClipboardCoordinator(
        clipboardSource,
        onAccepted = { sendController.acceptExternalText(it, "前台剪贴板") },
        onRejected = sendController::reportMessage,
    )

    fun close() {
        clipboardCoordinator.close()
        httpClient.dispatcher.executorService.shutdown()
        httpClient.connectionPool.evictAll()
    }
}
