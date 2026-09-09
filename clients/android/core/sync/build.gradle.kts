import org.jetbrains.kotlin.gradle.dsl.JvmTarget

plugins {
    alias(libs.plugins.kotlin.jvm)
    alias(libs.plugins.detekt)
}

kotlin {
    compilerOptions {
        jvmTarget.set(JvmTarget.JVM_17)
        allWarningsAsErrors.set(true)
        progressiveMode.set(true)
    }
}

dependencies {
    implementation(project(":core:vault"))
    testImplementation(libs.junit4)
}

detekt {
    buildUponDefaultConfig = true
    allRules = false
}

tasks.withType<JavaCompile>().configureEach {
    options.release.set(17)
    options.compilerArgs.add("-Werror")
}
