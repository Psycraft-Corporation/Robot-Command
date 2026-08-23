# Dependency and asset inventory

This inventory covers the direct dependencies and redistributable assets in
the Robot Command source tree. Versions are taken from
`Directory.Packages.props` or the owning project file. Transitive dependency
licenses are supplied by the package manager and must be included in a final
binary distribution review.

## Direct .NET dependencies

| Dependency | Version | License | Source | Use/status |
| --- | --- | --- | --- | --- |
| Avalonia | 12.0.2 | MIT | https://github.com/AvaloniaUI/Avalonia | GUI |
| Avalonia.Desktop | 12.0.2 | MIT | https://github.com/AvaloniaUI/Avalonia | GUI |
| Avalonia.Themes.Fluent | 12.0.2 | MIT | https://github.com/AvaloniaUI/Avalonia | GUI |
| BruTile.MbTiles | 6.0.0 | MIT | https://github.com/BruTile/BruTile | Offline maps |
| DSoft.Makaretu.Dns.Multicast | 1.0.2412.112 | MIT | https://github.com/DSoft-Systems/Makaretu.Dns.Multicast | LAN discovery |
| Google.Protobuf | 3.29.3 | BSD-3-Clause | https://github.com/protocolbuffers/protobuf | SDK/protocol generation |
| Grpc.AspNetCore | 2.67.0 | Apache-2.0 | https://github.com/grpc/grpc-dotnet | Observer server |
| Grpc.Core.Api | 2.67.0 | Apache-2.0 | https://github.com/grpc/grpc-dotnet | SDK API |
| Grpc.Net.Client | 2.67.0 | Apache-2.0 | https://github.com/grpc/grpc-dotnet | SDK client |
| Grpc.Tools | 2.67.0 | Apache-2.0 | https://github.com/grpc/grpc-dotnet | Build-time code generation |
| Mapsui | 5.1.0 | MIT | https://github.com/Mapsui/Mapsui | Map rendering |
| Mapsui.Avalonia12 | 5.1.0-beta.7 | MIT | https://github.com/Mapsui/Mapsui | GUI map integration |
| Mapsui.Nts | 5.1.0 | MIT | https://github.com/Mapsui/Mapsui | Geometry/map support |
| MavLinkSharp | 1.8.0 | MIT | https://github.com/RotorHazard/MavLinkSharp | MAVLink transport |
| Microsoft.Extensions.Hosting | 10.0.0 | MIT | https://github.com/dotnet/runtime | Hosting/lifecycle |
| Microsoft.NET.Test.Sdk | 18.0.0 | MIT | https://github.com/microsoft/vstest | Test infrastructure |
| Microsoft.Windows.SDK.NET.Ref | 10.0.26100.1 | Microsoft license terms | https://github.com/microsoft/windows-sdk-for-net | Windows integration |
| QRCoder | 1.6.0 | MIT | https://github.com/codebude/QRCoder | Pairing QR codes |
| System.IO.Ports | 10.0.0 | MIT | https://github.com/dotnet/runtime | Serial/SIK transport |
| System.ServiceProcess.ServiceController | 10.0.0 | MIT | https://github.com/dotnet/runtime | Windows services |
| Veldrid | 4.9.0 | MIT | https://github.com/mellinoe/veldrid | Optional renderer backend |
| xunit.v3 | 3.0.1 | Apache-2.0 | https://github.com/xunit/xunit | Tests |
| xunit.runner.visualstudio | 3.1.5 | Apache-2.0 | https://github.com/xunit/xunit | Test discovery |
| YamlDotNet | 18.1.0 | MIT | https://github.com/aaubry/YamlDotNet | Configuration/assets |
| Psycraft.Logos.Api.Sdk | 0.1.0-beta.8 | Apache-2.0 preview package | NuGet.org | Public preview package |

Package licenses and source URLs must be rechecked against the exact package
metadata before binary publication. Private packages must not be included in a
public source-only import.

## Native runtimes and data

| Asset | Version/source | License/status |
| --- | --- | --- |
| GStreamer Windows MSVC runtime | 1.26.11, https://gstreamer.freedesktop.org/ | LGPL-2.1-or-later with plugin-specific notices; bundled notices are in `THIRD-PARTY-NOTICES/GStreamer.txt` during packaging |
| OpenStreetMap tiles/data | Runtime-selected provider | Provider terms and attribution apply; no map package is included in the source tree |
| Copernicus terrain service | Runtime-selected endpoint | Service terms apply; no terrain dataset is included in the source tree |
| MeshProbe triangle OBJ fixture | Repository test fixture | Psycraft-created test geometry, Apache-2.0 |

Generated protobuf sources are produced from the checked-in SDK `.proto`
contract and are not independently licensed third-party assets.
