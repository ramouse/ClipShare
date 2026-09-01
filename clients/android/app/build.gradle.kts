import org.jetbrains.kotlin.gradle.dsl.JvmTarget

plugins {
    alias(libs.plugins.android.application)
    alias(libs.plugins.kotlin.compose)
    alias(libs.plugins.detekt)
}

android {
    namespace = "com.clipshare.android"
    compileSdk = 36

    defaultConfig {
        applicationId = "com.clipshare.android"
        minSdk = 29
        targetSdk = 36
        versionCode = 3
        versionName = "0.3.0-a1"
        testInstrumentationRunner = "androidx.test.runner.AndroidJUnitRunner"
    }

    buildTypes {
        debug {
            buildConfigField("String", "DEFAULT_BASE_URL", "\"http://10.0.2.2:8000/\"")
            buildConfigField("boolean", "ALLOW_LOCAL_ENDPOINTS", "true")
        }
        release {
            isMinifyEnabled = false
            buildConfigField("String", "DEFAULT_BASE_URL", "\"\"")
            buildConfigField("boolean", "ALLOW_LOCAL_ENDPOINTS", "false")
            proguardFiles(
                getDefaultProguardFile("proguard-android-optimize.txt"),
                "proguard-rules.pro",
            )
        }
    }

    buildFeatures {
        buildConfig = true
        compose = true
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    packaging {
        resources.excludes += "/META-INF/{AL2.0,LGPL2.1}"
    }

    testOptions {
        unitTests.isIncludeAndroidResources = true
        managedDevices.localDevices {
            for (api in listOf(29, 31, 33, 34, 35, 36)) {
                create("pixel2Api$api") {
                    device = "Pixel 2"
                    apiLevel = api
                    systemImageSource = "aosp"
                }
            }
        }
    }

    lint {
        abortOnError = true
        warningsAsErrors = true
        checkDependencies = true
        checkReleaseBuilds = true
        // A1 freezes API 36 and the verified dependency catalog. Version drift
        // is handled by a later explicitly authorized upgrade, not by this gate.
        disable += setOf("OldTargetApi", "GradleDependency", "NewerVersionAvailable")
    }
}

kotlin {
    compilerOptions {
        jvmTarget.set(JvmTarget.JVM_17)
        allWarningsAsErrors.set(true)
        progressiveMode.set(true)
    }
}

dependencies {
    implementation(project(":core:model"))
    implementation(project(":core:network"))
    implementation(project(":feature:receive"))
    implementation(project(":feature:send"))
    implementation(project(":feature:settings"))
    implementation(project(":platform:android"))

    implementation(platform(libs.compose.bom))
    implementation(libs.androidx.activity.compose)
    implementation(libs.androidx.core.ktx)
    implementation(libs.androidx.lifecycle.runtime.compose)
    implementation(libs.androidx.lifecycle.runtime.ktx)
    implementation(libs.compose.foundation)
    implementation(libs.compose.material3)
    implementation(libs.compose.ui)
    implementation(libs.compose.ui.tooling.preview)
    implementation(libs.kotlinx.coroutines.android)

    testImplementation(libs.junit4)
    androidTestImplementation(platform(libs.compose.bom))
    androidTestImplementation(libs.androidx.test.core)
    androidTestImplementation(libs.androidx.test.ext.junit)
    androidTestImplementation(libs.androidx.test.runner)
    androidTestImplementation(libs.compose.ui.test.junit4)
    androidTestImplementation(libs.espresso.core)
    debugImplementation(libs.compose.ui.test.manifest)
    debugImplementation(libs.compose.ui.tooling)
}

detekt {
    buildUponDefaultConfig = true
    allRules = false
}
