import org.gradle.api.tasks.JavaExec
import java.io.File

plugins {
    alias(libs.plugins.kotlin.multiplatform)
    alias(libs.plugins.android.kotlin.multiplatform.library)
    alias(libs.plugins.wire)
    `maven-publish`
}

group = "com.psycraft.robotcommand"
version = "0.1.0-alpha.1"

val ktlintConfiguration = configurations.create("ktlint")

dependencies {
    add(ktlintConfiguration.name, "com.pinterest.ktlint:ktlint-cli:${libs.versions.ktlint.get()}")
}

val canonicalProtoDirectory = rootProject.layout.projectDirectory.dir("../../src/sdk/RobotCommand.Sdk/Proto")
val canonicalProtoFile = canonicalProtoDirectory.file("team/v1/team.proto")

tasks.register("verifyCanonicalProto") {
    inputs.file(canonicalProtoFile)
    doLast {
        val proto = inputs.files.singleFile
        require(proto.isFile) {
            "Canonical Robot Command schema was not found at ${proto.absolutePath}"
        }
        val source = proto.readText()
        val requiredDeclarations = listOf(
            "service ServerInfoService",
            "service AccessService",
            "service ObserverService",
            "rpc GetServerInfo",
            "rpc RequestAccess",
            "rpc GetSnapshot",
            "rpc WatchSnapshots",
            "message RobotCommandSnapshot",
            "message UnitSnapshot",
            "message OperatorLocation",
        )
        requiredDeclarations.forEach { declaration ->
            check(declaration in source) {
                "Canonical schema is missing required declaration: $declaration"
            }
        }
    }
}

val kotlinSourceFiles = fileTree(projectDir.resolve("src")) {
    include("**/*.kt")
}

tasks.register<JavaExec>("ktlintCheck") {
    group = "verification"
    description = "Checks Kotlin source formatting with the pinned KtLint CLI."
    classpath = ktlintConfiguration
    mainClass.set("com.pinterest.ktlint.Main")
    args("--relative", *kotlinSourceFiles.files.map(File::getPath).toTypedArray())
}

tasks.register<JavaExec>("ktlintFormat") {
    group = "formatting"
    description = "Formats Kotlin source with the pinned KtLint CLI."
    classpath = ktlintConfiguration
    mainClass.set("com.pinterest.ktlint.Main")
    args("-F", "--relative", *kotlinSourceFiles.files.map(File::getPath).toTypedArray())
}

tasks.register("verifyPublicApiBoundary") {
    group = "verification"
    description = "Ensures commonMain does not import platform or transport types."
    doLast {
        val forbiddenImports = listOf(
            "import android.",
            "import java.",
            "import javax.",
            "import com.squareup.wire.",
            "import io.grpc.",
        )
        fileTree(projectDir.resolve("src/commonMain")) {
            include("**/*.kt")
        }.forEach { sourceFile ->
            val violations = sourceFile.readLines().filter { line ->
                forbiddenImports.any(line::contains)
            }
            check(violations.isEmpty()) {
                "${sourceFile.relativeTo(projectDir)} exposes platform or transport imports: $violations"
            }
        }
    }
}

val generatedWireDirectory = layout.buildDirectory.dir("generated/source/wire")

tasks.register("verifyGeneratedProtocol") {
    group = "verification"
    description = "Verifies that Wire generated the required Team API services and RPCs."
    dependsOn("verifyCanonicalProto", "generateCommonMainProtos")
    doLast {
        val generatedRoot = generatedWireDirectory.get().asFile
        check(generatedRoot.isDirectory) {
            "Wire generated output was not found at ${generatedRoot.absolutePath}"
        }
        val generatedSources = generatedRoot.walkTopDown()
            .filter { it.isFile && it.extension == "kt" }
            .toList()
        listOf(
            "ServerInfoServiceClient.kt",
            "AccessServiceClient.kt",
            "ObserverServiceClient.kt",
            "GrpcServerInfoServiceClient.kt",
            "GrpcAccessServiceClient.kt",
            "GrpcObserverServiceClient.kt",
        ).forEach { fileName ->
            check(generatedSources.any { it.name == fileName }) {
                "Wire did not generate expected service binding $fileName"
            }
        }
        val generatedText = generatedSources.joinToString("\n", transform = File::readText)
        listOf("GetServerInfo", "RequestAccess", "GetSnapshot", "WatchSnapshots").forEach { rpc ->
            check(rpc in generatedText) {
                "Wire output is missing expected RPC $rpc"
            }
        }
    }
}

wire {
    sourcePath {
        srcDir(canonicalProtoDirectory.asFile)
        include("team/v1/team.proto")
    }
    kotlin {
        rpcRole = "client"
        rpcCallStyle = "suspending"
        mutableTypes = false
        makeImmutableCopies = true
        enumMode = "enum_class"
        singleMethodServices = false
    }
}

kotlin {
    androidLibrary {
        namespace = "com.psycraft.robotcommand.sdk"
        compileSdk = 36
        minSdk = 26
    }

    jvm()
    iosArm64()
    iosSimulatorArm64()
    iosX64()

    targets.withType<org.jetbrains.kotlin.gradle.plugin.mpp.KotlinNativeTarget>().configureEach {
        binaries.framework {
            baseName = "RobotCommandSdk"
            isStatic = true
        }
    }

    sourceSets {
        commonMain.dependencies {
            implementation(libs.wire.runtime)
            implementation(libs.wire.grpc.client)
            api(libs.kotlinx.coroutines.core)
        }
        commonTest.dependencies {
            implementation(kotlin("test"))
            implementation(libs.kotlinx.coroutines.test)
        }
        androidMain.dependencies {
            implementation(libs.okhttp)
        }
        jvmTest.dependencies {
            implementation(libs.wire.grpc.mockwebserver)
        }
    }
}

tasks.matching { it.name.startsWith("compile") || it.name.startsWith("link") }.configureEach {
    dependsOn("verifyCanonicalProto")
}

tasks.named("check") {
    dependsOn("ktlintCheck", "verifyPublicApiBoundary", "verifyGeneratedProtocol")
}
