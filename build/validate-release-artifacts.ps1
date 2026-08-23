[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Version,
    [Parameter(Mandatory = $true)]
    [string]$Commit,
    [string]$Runtime = "win-x64",
    [string]$PackageDirectory = "artifacts/packages"
)

$ErrorActionPreference = "Stop"
$rootDir = Split-Path -Parent $PSScriptRoot
$directory = [IO.Path]::GetFullPath((Join-Path $rootDir $PackageDirectory))
$appArchive = Join-Path $directory "RobotCommand-$Version-$Runtime.zip"
$cliArchive = Join-Path $directory "RobotCommand.Cli-$Version-$Runtime.zip"
$manifestPath = Join-Path $directory "RobotCommand-$Version-release-manifest.json"
$checksumsPath = Join-Path $directory "SHA256SUMS"

foreach ($path in @($appArchive, $cliArchive, $manifestPath, $checksumsPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required release artifact is missing: $path"
    }
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.schema -ne "robot-command.release.v1" -or
    $manifest.product -ne "Robot Command" -or
    $manifest.version -ne $Version -or
    $manifest.commit -ne $Commit -or
    $manifest.runtime -ne $Runtime -or
    $manifest.windowsBinariesSigned -ne $false) {
    throw "Release manifest metadata does not match the requested release."
}

foreach ($artifact in $manifest.artifacts) {
    $path = Join-Path $directory $artifact.name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Manifest artifact is missing: $($artifact.name)" }
    $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash.ToLowerInvariant()
    if ($actual -ne $artifact.sha256) { throw "Manifest checksum mismatch: $($artifact.name)" }
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
foreach ($archive in @($appArchive, $cliArchive)) {
    $zip = [IO.Compression.ZipFile]::OpenRead($archive)
    try {
        $forbidden = $zip.Entries | Where-Object {
            $entryName = $_.FullName.Replace('\', '/')
            $entryName -match '^(NuGet\.config|appsettings\.local\.json|\.env(?:\.|$)|bin/|obj/|artifacts/|out/|data/|logs/|screenshots/|sitl/|\.git/)'
        }
        if ($forbidden) { throw "Forbidden local or generated content is present in $($archive.Name)." }
    } finally {
        $zip.Dispose()
    }
}

$appZip = [IO.Compression.ZipFile]::OpenRead($appArchive)
try {
    $appEntries = @($appZip.Entries | ForEach-Object { $_.FullName.Replace('\', '/') })
    $hasGStreamer = $appEntries | Where-Object { $_ -eq "native/gstreamer/bin/gst-launch-1.0.exe" }
    $hasGStreamerNotice = $appEntries | Where-Object { $_ -eq "THIRD-PARTY-NOTICES/GStreamer.txt" }
    if ($hasGStreamer -and -not $hasGStreamerNotice) {
        throw "The Windows application archive contains GStreamer but no GStreamer notice."
    }
} finally {
    $appZip.Dispose()
}

Write-Host "Release artifact validation passed." -ForegroundColor Green
