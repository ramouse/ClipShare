package com.clipshare.platform.android

import android.content.ClipDescription
import android.content.ClipboardManager
import com.clipshare.core.model.ClipboardSubscription
import com.clipshare.core.model.ClipboardTextSource

class AndroidClipboardTextSource(
    private val clipboardManager: ClipboardManager,
) : ClipboardTextSource {
    override fun subscribe(listener: (String?) -> Unit): ClipboardSubscription {
        val platformListener = ClipboardManager.OnPrimaryClipChangedListener {
            val plainText = try {
                val description = clipboardManager.primaryClipDescription
                val clip = clipboardManager.primaryClip
                val isPlainTextOnly = description?.let {
                    it.hasMimeType(ClipDescription.MIMETYPE_TEXT_PLAIN) &&
                        !it.hasMimeType(ClipDescription.MIMETYPE_TEXT_HTML)
                } == true
                val firstItem = clip?.takeIf { it.itemCount > 0 }?.getItemAt(0)
                if (isPlainTextOnly) {
                    firstItem?.text?.toString()
                } else {
                    null
                }
            } catch (ignored: SecurityException) {
                null
            }
            listener(plainText)
        }
        clipboardManager.addPrimaryClipChangedListener(platformListener)
        return ClipboardSubscription {
            clipboardManager.removePrimaryClipChangedListener(platformListener)
        }
    }
}
