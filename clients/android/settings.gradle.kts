pluginManagement {
    repositories {
        google()
        mavenCentral()
        gradlePluginPortal()
    }
}

dependencyResolutionManagement {
    repositoriesMode.set(RepositoriesMode.FAIL_ON_PROJECT_REPOS)
    repositories {
        google()
        mavenCentral()
    }
}

rootProject.name = "clipshare-android"

include(
    ":app",
    ":core:model",
    ":core:network",
    ":core:crypto",
    ":core:sync",
    ":core:vault",
    ":feature:receive",
    ":feature:send",
    ":feature:settings",
    ":platform:android",
)

// crypto-contract remains the standalone C1 executable and is intentionally not
// folded into the A1 application graph. C2 owns product E2E integration.
