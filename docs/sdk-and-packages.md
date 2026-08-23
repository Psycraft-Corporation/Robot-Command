# SDK and packages

Robot Command consumes `Psycraft.Logos.Api.Sdk` as an external NuGet package. The current preview is `0.1.0-beta.8`. Package generation is driven from the Logos protobuf contract and the public .NET SDK repository; generated SDK output is not hand-edited in Robot Command.

Robot Command consumes the public `Psycraft.Logos.Api.Sdk` package from NuGet.org. Restore with `NuGet.config.template`; the application does not require a private package feed or package credentials.

Do not commit `NuGet.config`, credentials, or package tokens.

The .NET SDK is Apache-2.0. Generated protobuf contracts and other SDKs use the same approved license policy. See the dependency inventory and package metadata for third-party notices.
