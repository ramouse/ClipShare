package com.clipshare.android

import android.content.ClipData
import android.content.ClipboardManager
import android.net.Uri
import androidx.test.core.app.ApplicationProvider
import androidx.test.platform.app.InstrumentationRegistry
import com.clipshare.core.model.ClipboardSubscription
import com.clipshare.platform.android.AndroidClipboardTextSource
import com.clipshare.platform.android.AndroidDocumentResolver
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicInteger
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

private const val TEST_DOCUMENT_AUTHORITY = "com.clipshare.android.test.documents"
private const val READ_CLIPBOARD_IN_BACKGROUND = "android.permission.READ_CLIPBOARD_IN_BACKGROUND"
private const val INPUT_PATH = "input"
private const val OUTPUT_PATH = "output"
private const val DOCUMENT_CONTENT = "streamed through content resolver"

class AndroidPlatformAdaptersTest {
    @Test
    fun documentResolverStreamsContentProviderInputAndOutput() {
        val targetContext = ApplicationProvider.getApplicationContext<android.content.Context>()
        val inputUri = documentUri(INPUT_PATH)
        val outputUri = documentUri(OUTPUT_PATH)
        val expected = DOCUMENT_CONTENT.encodeToByteArray()

        try {
            val resolver = AndroidDocumentResolver(targetContext.contentResolver)
            val readable = resolver.readable(inputUri)
            assertEquals("fixture.bin", readable.displayName)
            assertEquals("application/octet-stream", readable.contentType)
            assertEquals(expected.size.toLong(), readable.sizeBytes)
            assertTrue(expected.contentEquals(readable.inputStreamFactory.open().use { it.readBytes() }))

            resolver.writable(outputUri).outputStreamFactory.open().use {
                it.write(expected)
            }
            val written = resolver.readable(outputUri).inputStreamFactory.open().use { it.readBytes() }
            assertTrue(expected.contentEquals(written))
        } finally {
            targetContext.contentResolver.delete(inputUri, null, null)
            targetContext.contentResolver.delete(outputUri, null, null)
        }
    }

    @Test
    fun clipboardSubscriptionReadsPlainTextAndStopsAfterCancel() {
        val instrumentation = InstrumentationRegistry.getInstrumentation()
        val uiAutomation = instrumentation.uiAutomation
        uiAutomation.adoptShellPermissionIdentity(READ_CLIPBOARD_IN_BACKGROUND)
        val context = ApplicationProvider.getApplicationContext<android.content.Context>()
        val manager = context.getSystemService(ClipboardManager::class.java)
        val source = AndroidClipboardTextSource(manager)
        val callbacks = AtomicInteger(0)
        val firstCallback = CountDownLatch(1)
        var received: String? = null
        var subscription: ClipboardSubscription? = null

        try {
            instrumentation.runOnMainSync {
                subscription = source.subscribe {
                    received = it
                    callbacks.incrementAndGet()
                    firstCallback.countDown()
                }
            }
            instrumentation.runOnMainSync {
                manager.setPrimaryClip(ClipData.newPlainText("A1", "first-plain-text"))
            }
            assertTrue(firstCallback.await(15, TimeUnit.SECONDS))
            assertEquals("first-plain-text", received)

            instrumentation.runOnMainSync { subscription?.cancel() }
            val countAfterCancel = callbacks.get()
            instrumentation.runOnMainSync {
                manager.setPrimaryClip(ClipData.newPlainText("A1", "must-not-be-observed"))
            }
            Thread.sleep(500)
            assertEquals(countAfterCancel, callbacks.get())
        } finally {
            instrumentation.runOnMainSync {
                subscription?.cancel()
                manager.clearPrimaryClip()
            }
            uiAutomation.dropShellPermissionIdentity()
        }
    }

    private fun documentUri(path: String): Uri = Uri.Builder()
        .scheme("content")
        .authority(TEST_DOCUMENT_AUTHORITY)
        .appendPath(path)
        .build()
}
