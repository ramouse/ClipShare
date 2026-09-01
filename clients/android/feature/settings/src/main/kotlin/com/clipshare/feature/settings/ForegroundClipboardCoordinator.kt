package com.clipshare.feature.settings

import com.clipshare.core.model.ClipboardSubscription
import com.clipshare.core.model.ClipboardTextSource
import com.clipshare.core.model.TextPolicy
import com.clipshare.core.model.TextValidation

class ForegroundClipboardCoordinator(
    private val source: ClipboardTextSource,
    private val onAccepted: (String) -> Unit,
    private val onRejected: (String) -> Unit = {},
) {
    private var visible = false
    private var focused = false
    private var monitoringEnabled = false
    private var subscription: ClipboardSubscription? = null
    private var lastAccepted: String? = null

    fun setVisible(value: Boolean) {
        visible = value
        updateSubscription()
    }

    fun setFocused(value: Boolean) {
        focused = value
        updateSubscription()
    }

    fun setMonitoringEnabled(value: Boolean) {
        monitoringEnabled = value
        updateSubscription()
    }

    fun close() {
        subscription?.cancel()
        subscription = null
        visible = false
        focused = false
    }

    private fun updateSubscription() {
        val shouldListen = visible && focused && monitoringEnabled
        if (shouldListen && subscription == null) {
            subscription = source.subscribe(::handleText)
        } else if (!shouldListen && subscription != null) {
            subscription?.cancel()
            subscription = null
        }
    }

    private fun handleText(raw: String?) {
        if (!(visible && focused && monitoringEnabled)) return
        if (raw == null) {
            onRejected("剪贴板不是纯文本或 HTTP(S) URL")
            return
        }
        when (val validation = TextPolicy.validate(raw)) {
            is TextValidation.Rejected -> onRejected(validation.message)
            is TextValidation.Accepted -> if (validation.text == lastAccepted) {
                onRejected("已忽略重复剪贴板内容")
            } else {
                lastAccepted = validation.text
                onAccepted(validation.text)
            }
        }
    }
}
