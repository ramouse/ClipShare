package com.clipshare.feature.settings

import com.clipshare.core.model.ClipboardSubscription
import com.clipshare.core.model.ClipboardTextSource
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class ForegroundClipboardCoordinatorTest {
    @Test
    fun subscribesOnlyWhileVisibleFocusedAndEnabled() {
        val source = FakeClipboardSource()
        val accepted = mutableListOf<String>()
        val coordinator = ForegroundClipboardCoordinator(source, accepted::add)

        coordinator.setMonitoringEnabled(true)
        coordinator.setVisible(true)
        assertEquals(0, source.subscribeCount)
        coordinator.setFocused(true)
        assertEquals(1, source.subscribeCount)
        source.emit("hello")
        coordinator.setFocused(false)
        source.emit("ignored")

        assertEquals(listOf("hello"), accepted)
        assertEquals(1, source.cancelCount)
    }

    @Test
    fun rejectsDuplicateWithoutAnySendCapability() {
        val source = FakeClipboardSource()
        val accepted = mutableListOf<String>()
        val rejected = mutableListOf<String>()
        val coordinator = ForegroundClipboardCoordinator(source, accepted::add, rejected::add)
        coordinator.setMonitoringEnabled(true)
        coordinator.setVisible(true)
        coordinator.setFocused(true)

        source.emit("same")
        source.emit("same")

        assertEquals(listOf("same"), accepted)
        assertEquals(listOf("已忽略重复剪贴板内容"), rejected)
    }

    @Test
    fun rejectsNullBlankAndOversizeClipboardValues() {
        val source = FakeClipboardSource()
        val rejected = mutableListOf<String>()
        val coordinator = ForegroundClipboardCoordinator(source, {}, rejected::add)
        coordinator.setVisible(true)
        coordinator.setFocused(true)
        coordinator.setMonitoringEnabled(true)

        source.emit(null)
        source.emit(" ")
        source.emit("x".repeat(100_001))

        assertEquals(3, rejected.size)
        assertTrue(rejected[0].contains("不是纯文本"))
        assertTrue(rejected[1].contains("不能为空"))
        assertTrue(rejected[2].contains("100000"))
    }

    @Test
    fun disablingMonitoringAndCloseCancelOnlyActiveSubscription() {
        val source = FakeClipboardSource()
        val accepted = mutableListOf<String>()
        val coordinator = ForegroundClipboardCoordinator(source, accepted::add)
        coordinator.setVisible(true)
        coordinator.setFocused(true)
        coordinator.setMonitoringEnabled(true)
        coordinator.setMonitoringEnabled(false)
        source.emit("ignored")
        coordinator.close()
        coordinator.close()

        assertEquals(1, source.subscribeCount)
        assertEquals(1, source.cancelCount)
        assertTrue(accepted.isEmpty())
    }

    @Test
    fun repeatedStateAndActiveCloseDoNotLeakSubscriptions() {
        val source = FakeClipboardSource()
        val coordinator = ForegroundClipboardCoordinator(source, {})
        coordinator.setVisible(true)
        coordinator.setVisible(true)
        coordinator.setFocused(true)
        coordinator.setFocused(true)
        coordinator.setMonitoringEnabled(true)
        coordinator.setMonitoringEnabled(true)

        assertEquals(1, source.subscribeCount)
        coordinator.close()

        assertEquals(1, source.cancelCount)
    }

    private class FakeClipboardSource : ClipboardTextSource {
        var subscribeCount = 0
        var cancelCount = 0
        private var listener: ((String?) -> Unit)? = null

        override fun subscribe(listener: (String?) -> Unit): ClipboardSubscription {
            subscribeCount += 1
            this.listener = listener
            return ClipboardSubscription {
                cancelCount += 1
                if (this.listener === listener) this.listener = null
            }
        }

        fun emit(value: String?) {
            listener?.invoke(value)
        }
    }
}
