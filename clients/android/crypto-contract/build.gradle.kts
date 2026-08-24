import org.jetbrains.kotlin.gradle.dsl.JvmTarget
import org.gradle.api.tasks.wrapper.Wrapper

plugins {
    kotlin("jvm") version "2.4.10"
    application
}

group = "com.clipshare"
version = "0.3.0-c1"

dependencies {
    implementation("org.jetbrains.kotlinx:kotlinx-serialization-json:1.11.0")
}

kotlin {
    jvmToolchain(21)
    compilerOptions {
        jvmTarget.set(JvmTarget.JVM_21)
        allWarningsAsErrors.set(true)
        progressiveMode.set(true)
    }
}

application {
    mainClass.set("com.clipshare.crypto.ContractMainKt")
    applicationName = "clipshare-enc1-contract"
}

dependencyLocking {
    lockAllConfigurations()
}

tasks.withType<JavaCompile>().configureEach {
    options.release.set(21)
    options.compilerArgs.add("-Werror")
}

tasks.named<Wrapper>("wrapper") {
    gradleVersion = "9.7.1"
    distributionType = Wrapper.DistributionType.BIN
    distributionSha256Sum = "acd53f1edaf02f1a8ff99879f8a34b302661a057d9b063ae9e35b552f804d20a"
}
