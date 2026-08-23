param(
    [string]$Runtime = "win-x64",
    [string]$Version = "0.1.0-dev",
    [string]$SourceRevisionId = "",
    [string]$GStreamerVersion = "1.26.11",
    [string]$GStreamerRoot = "",
    [switch]$SkipGStreamer
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
$RootDir = Split-Path -Parent $PSScriptRoot
$OutputDir = Join-Path $RootDir "artifacts/publish/$Runtime"
$PackageDir = Join-Path $RootDir "artifacts/packages"
$CacheDir = Join-Path $RootDir "artifacts/cache/gstreamer/$GStreamerVersion/$Runtime"
$Project = Join-Path $RootDir "src/app/RobotCommand/RobotCommand.csproj"

function Write-Section([string]$Message) {
    Write-Host "== $Message ==" -ForegroundColor Cyan
}

function Find-GStreamerRoot([string]$Candidate) {
    if ([string]::IsNullOrWhiteSpace($Candidate)) {
        return $null
    }

    $full = [IO.Path]::GetFullPath($Candidate)
    if (-not (Test-Path -LiteralPath $full -PathType Container)) {
        return $null
    }
    $launch = Join-Path $full "bin/gst-launch-1.0.exe"
    $inspect = Join-Path $full "bin/gst-inspect-1.0.exe"
    if ((Test-Path -LiteralPath $launch) -and (Test-Path -LiteralPath $inspect)) {
        return $full
    }

    $launchFile = Get-ChildItem -LiteralPath $full -Recurse -File -Filter "gst-launch-1.0.exe" | Select-Object -First 1
    if ($null -eq $launchFile) {
        return $null
    }

    $bin = $launchFile.Directory.FullName
    $root = Split-Path -Parent $bin
    if ((Test-Path -LiteralPath (Join-Path $bin "gst-inspect-1.0.exe"))) {
        return $root
    }

    return $null
}

function Get-GStreamerRoot {
    $existing = Find-GStreamerRoot $GStreamerRoot
    if ($null -ne $existing) {
        return $existing
    }

    if ($SkipGStreamer) {
        Write-Host "GStreamer bundling skipped by request."
        return $null
    }

    if ($Runtime -ne "win-x64") {
        throw "The automatic GStreamer downloader currently supports only win-x64. Supply -GStreamerRoot or use -SkipGStreamer for '$Runtime'."
    }

    $architecture = "x86_64"
    $fileName = "gstreamer-1.0-msvc-$architecture-$GStreamerVersion.msi"
    $baseUrl = "https://gstreamer.freedesktop.org/data/pkg/windows/$GStreamerVersion/msvc"
    $downloadUrl = "$baseUrl/$fileName"
    $checksumUrl = "$downloadUrl.sha256sum"
    $msiPath = Join-Path $CacheDir $fileName
    $checksumPath = "$msiPath.sha256sum"
    $extractedDir = Join-Path $CacheDir "extracted"

    New-Item -ItemType Directory -Force -Path $CacheDir | Out-Null
    if (-not (Test-Path -LiteralPath $msiPath)) {
        Write-Section "Downloading GStreamer $GStreamerVersion runtime"
        Invoke-WebRequest -Uri $downloadUrl -OutFile $msiPath
    }
    if (-not (Test-Path -LiteralPath $checksumPath)) {
        Invoke-WebRequest -Uri $checksumUrl -OutFile $checksumPath
    }

    $expected = (Get-Content -Raw -LiteralPath $checksumPath).Trim() -split '\s+' | Select-Object -First 1
    $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $msiPath).Hash.ToLowerInvariant()
    if ($actual -ne $expected.ToLowerInvariant()) {
        throw "GStreamer runtime checksum mismatch. Expected $expected, received $actual."
    }

    $extractedRoot = Find-GStreamerRoot $extractedDir
    if ($null -eq $extractedRoot) {
        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue $extractedDir
        New-Item -ItemType Directory -Force -Path $extractedDir | Out-Null
        Write-Section "Extracting GStreamer runtime"
        $process = Start-Process -FilePath "msiexec.exe" -ArgumentList @(
            "/a", $msiPath, "/qn", "/norestart", "TARGETDIR=$extractedDir"
        ) -Wait -PassThru
        if ($process.ExitCode -ne 0) {
            throw "GStreamer runtime extraction failed with msiexec exit code $($process.ExitCode)."
        }
        $extractedRoot = Find-GStreamerRoot $extractedDir
    }

    if ($null -eq $extractedRoot) {
        throw "The downloaded GStreamer runtime does not contain gst-launch-1.0.exe and gst-inspect-1.0.exe."
    }
    return $extractedRoot
}

Remove-Item -Recurse -Force $OutputDir -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $OutputDir, $PackageDir | Out-Null

$gStreamerRoot = Get-GStreamerRoot
$publishProperties = @(
    "PublishSingleFile=true",
    "PublishTrimmed=false",
    "Version=$Version",
    "InformationalVersion=$Version"
)
if (-not [string]::IsNullOrWhiteSpace($gStreamerRoot)) {
    $publishProperties += "RobotCommandGStreamerRoot=$gStreamerRoot"
}
if (-not [string]::IsNullOrWhiteSpace($SourceRevisionId)) {
    $publishProperties += "SourceRevisionId=$SourceRevisionId"
    $publishProperties = $publishProperties | ForEach-Object {
        if ($_ -eq "InformationalVersion=$Version") {
            "InformationalVersion=$Version+$SourceRevisionId"
        } else {
            $_
        }
    }
}

Write-Section "Publishing Robot Command"
dotnet publish $Project `
    -c Release `
    -r $Runtime `
    --self-contained true `
    $(foreach ($property in $publishProperties) { "-p:$property" }) `
    -o $OutputDir

if (-not [string]::IsNullOrWhiteSpace($gStreamerRoot)) {
    $bundle = Join-Path $OutputDir "native/gstreamer"
    $publishedRoot = Find-GStreamerRoot $bundle
    if ($null -eq $publishedRoot) {
        throw "Publish completed without a usable app-local GStreamer runtime under '$bundle'."
    }

    $noticeDir = Join-Path $OutputDir "THIRD-PARTY-NOTICES"
    New-Item -ItemType Directory -Force -Path $noticeDir | Out-Null
    @"
Robot Command includes the GStreamer $GStreamerVersion Windows MSVC runtime.

GStreamer is distributed under the GNU Lesser General Public License, version 2.1 or later,
with additional notices and licenses applying to individual plugins and bundled dependencies.
The complete runtime files and their accompanying notices are included under native/gstreamer
and native/gstreamer/share where provided by the upstream runtime package.

Project: https://gstreamer.freedesktop.org/
Windows packages: https://gstreamer.freedesktop.org/download/
"@ | Set-Content -LiteralPath (Join-Path $noticeDir "GStreamer.txt") -Encoding UTF8
    Write-Host "Bundled GStreamer runtime: $publishedRoot"
}

$Archive = Join-Path $PackageDir "RobotCommand-$Version-$Runtime.zip"
Remove-Item -Force $Archive -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $OutputDir "*") -DestinationPath $Archive
Write-Host "Package: $Archive" -ForegroundColor Green
