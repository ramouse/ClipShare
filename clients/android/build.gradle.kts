plugins {
    alias(libs.plugins.android.application) apply false
    alias(libs.plugins.android.library) apply false
    alias(libs.plugins.detekt) apply false
    alias(libs.plugins.kotlin.compose) apply false
    alias(libs.plugins.kotlin.jvm) apply false
    alias(libs.plugins.kotlin.serialization) apply false
    alias(libs.plugins.ksp) apply false
    alias(libs.plugins.room) apply false
}

allprojects {
    dependencyLocking {
        lockAllConfigurations()
    }
}

tasks.register("a1JvmCheck") {
    group = "verification"
    description = "Runs the host-independent v0.3-A1 unit and static-analysis gates."
    dependsOn(
        ":core:model:test",
        ":core:network:test",
        ":feature:receive:test",
        ":feature:send:test",
        ":feature:settings:test",
        ":core:model:detekt",
        ":core:network:detekt",
        ":feature:receive:detekt",
        ":feature:send:detekt",
        ":feature:settings:detekt",
    )
}

tasks.register("a1AndroidCheck") {
    group = "verification"
    description = "Builds and checks the Android v0.3-A1 debug application."
    dependsOn(
        "a1JvmCheck",
        ":app:assembleDebug",
        ":app:bundleDebug",
        ":app:detekt",
        ":app:lintDebug",
        ":app:testDebugUnitTest",
        ":platform:android:detekt",
        ":platform:android:lintDebug",
        ":platform:android:testDebugUnitTest",
    )
}

tasks.register("c2JvmCheck") {
    group = "verification"
    description = "Runs the host-independent v0.3-C2 Vault unit and static-analysis gates."
    dependsOn(
        ":core:vault:test",
        ":core:crypto:test",
        ":core:sync:test",
        ":core:vault:detekt",
        ":core:crypto:detekt",
        ":core:sync:detekt",
    )
}

tasks.register("c2AndroidCheck") {
    group = "verification"
    description = "Builds and checks the Android v0.3-C2 Room/Keystore adapter test APK."
    dependsOn(
        "c2JvmCheck",
        ":platform:android:assembleDebugAndroidTest",
        ":platform:android:detekt",
        ":platform:android:lintDebug",
        ":platform:android:testDebugUnitTest",
    )
}
