# Robot Command Kotlin Multiplatform SDK

This is the multiplatform observer SDK for Robot Command Mobile and other
read-only Team API clients. It is developed alongside the desktop .NET and
Dart SDKs, using the same canonical protobuf contract.

## Source of truth and generated bindings

The only protocol source is:

```text
../../src/sdk/RobotCommand.Sdk/Proto/team/v1/team.proto
```

Wire reads that file directly during the Gradle build and generates immutable
Kotlin protobuf models plus client RPC interfaces for `ServerInfoService`,
`AccessService`, and `ObserverService`. Generated sources are build output and
must not be edited or committed. The generated package is intentionally
protocol-facing; application code should use the neutral SDK models instead.

## Local development

Use Android Studio's bundled JDK or another JDK supported by the pinned Gradle
wrapper. Android builds require Android SDK 36. iOS framework tasks require a
macOS host with Xcode installed.

```powershell
$env:JAVA_HOME = "C:\Program Files\Android\Android Studio\jbr"
$env:ANDROID_HOME = "$env:LOCALAPPDATA\Android\Sdk"
$env:ANDROID_SDK_ROOT = $env:ANDROID_HOME
.\gradlew.bat :sdk:verifyCanonicalProto
.\gradlew.bat :sdk:verifyGeneratedProtocol
.\gradlew.bat :sdk:jvmTest
.\gradlew.bat :sdk:compileAndroidMain
.\gradlew.bat :sdk:check
```

Use `:sdk:ktlintFormat` to apply the pinned formatter locally. The `check`
task also verifies that `commonMain` does not leak Android/JVM or Wire/gRPC
implementation imports through the shared boundary.

On macOS, compile the static iOS framework with:

```bash
./gradlew :sdk:linkDebugFrameworkIosSimulatorArm64
```

The SDK currently implements the live certificate-pinned observer transport
for Android. The iOS target and public API are present, while its native
transport is deliberately left for the macOS/Xcode milestone.

## API boundary

`RobotCommandLanClient` provides HTTPS probing and host-approved observer
access. `RobotCommandObserverSession` exposes access status, lifecycle events,
and immutable snapshot models. Bearer tokens and generated Wire/gRPC types
remain internal to the SDK and are never persisted.

The client never starts a server, discovers peers, reconnects automatically, or
sends commands.

## FieldApp local composite build

While both repositories are local, FieldApp can consume this SDK without a
copied source tree or absolute path dependency:

```kotlin
includeBuild("../Robot-Command/sdks/kmp")
```

```kotlin
implementation("com.psycraft.robotcommand:robot-command-sdk")
```

This local composite dependency will later be replaced by a pinned Maven
artifact. GitHub Packages is suitable while the repositories are private;
Maven Central is a possible public distribution path.

## Compatibility

The .NET SDK continues to use Google `protoc`/`Grpc.Tools`, and the Dart SDK
continues to use its checked-in generated bindings. All three SDKs must remain
wire-compatible with the canonical schema and the desktop Team server.
