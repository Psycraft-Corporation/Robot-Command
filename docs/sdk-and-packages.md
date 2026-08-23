# SDK and packages

Robot Command consumes `Psycraft.Logos.Api.Sdk` as an external NuGet package. The current preview is `0.1.0-beta.8`. Package generation is driven from the Logos protobuf contract and the public .NET SDK repository; generated SDK output is not hand-edited in Robot Command.

For local development, use the configured public or integration NuGet source outside the repository. Do not commit `NuGet.config`, credentials, Azure feed URLs, or package tokens.

The public target is `nuget.org` for `Psycraft.Logos.Api.Sdk`. This repository remains private until the complete release plan is finished.

The .NET SDK is Apache-2.0. Generated protobuf contracts and other SDKs use the same approved license policy. See the dependency inventory and package metadata for third-party notices.
