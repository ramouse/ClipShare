import org.jetbrains.kotlin.gradle.dsl.JvmTarget

plugins {
    alias(libs.plugins.kotlin.jvm)
    alias(libs.plugins.kotlin.serialization)
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
    implementation(libs.kotlinx.serialization.json)
    testImplementation(libs.junit4)
}

tasks.test {
    systemProperty(
        "clipshare.vault.vectors",
        rootProject.file("../../contracts/vault/test-vectors/positive-vectors.json").absolutePath,
    )
}

detekt {
    buildUponDefaultConfig = true
    allRules = false
}

tasks.withType<JavaCompile>().configureEach {
    options.release.set(17)
    options.compilerArgs.add("-Werror")
}
