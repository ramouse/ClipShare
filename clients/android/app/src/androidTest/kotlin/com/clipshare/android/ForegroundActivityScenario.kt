package com.clipshare.android

import android.app.KeyguardManager
import android.view.WindowManager
import androidx.test.core.app.ActivityScenario

internal fun ActivityScenario<MainActivity>.prepareForForeground() {
    onActivity { activity ->
        activity.setShowWhenLocked(true)
        activity.setTurnScreenOn(true)
        activity.window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        activity.getSystemService(KeyguardManager::class.java)
            .requestDismissKeyguard(activity, null)
    }
}
