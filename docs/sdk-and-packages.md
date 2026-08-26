# SDK and packages

Robot Command consumes `Psycraft.Logos.Api.Sdk` as an external NuGet package. The current preview is `0.1.0-beta.8`. Package generation is driven from the Logos protobuf contract and the public .NET SDK repository; generated SDK output is not hand-edited in Robot Command.

Robot Command consumes the public `Psycraft.Logos.Api.Sdk` package from NuGet.org. Restore with `NuGet.config.template`; the application does not require a private package feed or package credentials.

Do not commit `NuGet.config`, credentials, or package tokens.

The .NET SDK is Apache-2.0. Generated protobuf contracts and other SDKs use the same approved license policy. See the dependency inventory and package metadata for third-party notices.

## Kotlin Multiplatform SDK

The mobile SDK lives in `sdks/kmp` and is built from the canonical schema at
`src/sdk/RobotCommand.Sdk/Proto/team/v1/team.proto`. Square Wire generates the
Kotlin protobuf and gRPC client bindings during every build; generated sources
are build output and must not be committed. The public SDK API maps those
protocol types into immutable, protocol-neutral models for Android and future
iOS consumers.

From Windows, run the Android/JVM checks with:

```powershell
cd sdks/kmp
.\gradlew.bat :sdk:jvmTest
.\gradlew.bat :sdk:allTests
.\gradlew.bat :sdk:compileAndroidMain
```

The iOS framework is compiled on macOS, for example:

```bash
./gradlew :sdk:linkDebugFrameworkIosSimulatorArm64
```

FieldApp currently consumes the SDK through a local Gradle composite build only.
The future hosted dependency will be versioned as
`com.psycraft.robotcommand:robot-command-sdk`, likely through GitHub Packages
while the repositories are private and Maven Central after public release.
The .NET and Dart SDKs remain separate consumers of the same wire contract.
