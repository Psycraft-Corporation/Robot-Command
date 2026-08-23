param(
    [string]$Runtime = "win-x64",
    [string]$Version = "0.1.0-dev",
    [string]$SourceRevisionId = "",
    [string]$GStreamerVersion = "1.26.11",
    [string]$GStreamerRoot = "",
    [string]$GStreamerBundlePath = "",
    [string]$GStreamerBaseUrl = "",
    [switch]$SkipGStreamer
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
$RootDir = Split-Path -Parent $PSScriptRoot
$OutputDir = Join-Path $RootDir "artifacts/publish/$Runtime"
$PackageDir = Join-Path $RootDir "artifacts/packages"
$CacheDir = Join-Path $RootDir "artifacts/cache/gstreamer/$GStreamerVersion/$Runtime"
$PinnedGStreamerDir = if ([string]::IsNullOrWhiteSpace($GStreamerBundlePath)) {
    Join-Path $RootDir "build/third-party/gstreamer/$GStreamerVersion/$Runtime"
} else {
    [IO.Path]::GetFullPath($GStreamerBundlePath)
}
$Project = Join-Path $RootDir "src/app/RobotCommand/RobotCommand.csproj"

function Write-Section([string]$Message) {
    Write-Host "== $Message ==" -ForegroundColor Cyan
}

function Get-RemoteFileLength([string]$Uri, [object]$CurlCommand) {
    $headers = & $CurlCommand.Source `
        "--fail" `
        "--location" `
        "--silent" `
        "--show-error" `
        "--http1.1" `
        "--head" `
        "--retry" "5" `
        "--retry-all-errors" `
        "--retry-delay" "2" `
        "--retry-max-time" "120" `
        $Uri 2>&1
    if ($LASTEXITCODE -ne 0) {
        return 0
    }

    $length = 0L
    foreach ($header in @($headers)) {
        if ($header -match '(?i)^Content-Length:\s*(\d+)') {
            $length = [int64]$Matches[1]
        }
    }
    return $length
}

function Download-WithCurlRanges([string]$Uri, [string]$Destination, [object]$CurlCommand, [int64]$RemoteLength) {
    $chunkPath = "$Destination.chunk"
    $headerPath = "$Destination.headers"
    # Some public CDNs and CI egress proxies truncate large responses. Keep
    # ranges modest and advance by whatever valid portion arrived instead of
    # retrying the same missing tail over and over.
    $chunkSize = 4MB
    Remove-Item -LiteralPath $Destination -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $chunkPath -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $headerPath -Force -ErrorAction SilentlyContinue
    New-Item -ItemType File -Force -Path $Destination | Out-Null

    try {
        $offset = 0L
        while ($offset -lt $RemoteLength) {
            $end = $offset + $chunkSize - 1
            if ($end -ge $RemoteLength) {
                $end = $RemoteLength - 1
            }
            $expectedChunkLength = $end - $offset + 1
            $chunkDownloaded = $false

            for ($attempt = 1; $attempt -le 3; $attempt++) {
                Remove-Item -LiteralPath $chunkPath -Force -ErrorAction SilentlyContinue
                Remove-Item -LiteralPath $headerPath -Force -ErrorAction SilentlyContinue
                & $CurlCommand.Source `
                    "--fail" `
                    "--location" `
                    "--silent" `
                    "--show-error" `
                    "--http1.1" `
                    "--range" "$offset-$end" `
                    "--connect-timeout" "30" `
                    "--max-time" "600" `
                    "--dump-header" $headerPath `
                    "--output" $chunkPath `
                    $Uri
                $curlExitCode = $LASTEXITCODE

                if (Test-Path -LiteralPath $chunkPath -PathType Leaf) {
                    $chunkLength = (Get-Item -LiteralPath $chunkPath).Length
                    $headerText = if (Test-Path -LiteralPath $headerPath -PathType Leaf) {
                        Get-Content -Raw -LiteralPath $headerPath
                    } else {
                        ""
                    }
                    $rangeMatches = [regex]::Matches(
                        $headerText,
                        '(?im)^Content-Range:\s*bytes\s+(\d+)-(\d+)/\d+'
                    )
                    $rangeStart = $null
                    if ($rangeMatches.Count -gt 0) {
                        $rangeStart = [int64]$rangeMatches[$rangeMatches.Count - 1].Groups[1].Value
                    }

                    # A valid partial response is useful progress even when
                    # curl exits 18 (CURLE_PARTIAL_FILE). The final SHA-256
                    # check remains the authority for the completed download.
                    $validRange = $chunkLength -gt 0 -and
                        $chunkLength -le $expectedChunkLength -and
                        ($null -eq $rangeStart -and $offset -eq 0 -or $rangeStart -eq $offset)
                    if ($validRange -and ($curlExitCode -eq 0 -or $null -ne $rangeStart)) {
                        $sourceStream = [IO.File]::OpenRead($chunkPath)
                        $destinationStream = [IO.File]::Open(
                            $Destination,
                            [IO.FileMode]::Append,
                            [IO.FileAccess]::Write,
                            [IO.FileShare]::None
                        )
                        try {
                            $sourceStream.CopyTo($destinationStream)
                        }
                        finally {
                            $destinationStream.Dispose()
                            $sourceStream.Dispose()
                        }
                        $chunkDownloaded = $true
                        $offset += $chunkLength
                        break
                    }
                }

                $delaySeconds = [Math]::Min(30, 2 * $attempt)
                Write-Warning "GStreamer range $offset-$end made no valid progress; retrying in $delaySeconds seconds."
                Start-Sleep -Seconds $delaySeconds
            }

            if (-not $chunkDownloaded) {
                throw "Failed to download byte range $offset-$end from '$Uri'."
            }
        }

        $downloadedLength = (Get-Item -LiteralPath $Destination).Length
        if ($downloadedLength -ne $RemoteLength) {
            throw "Range download length mismatch. Expected $RemoteLength bytes, received $downloadedLength."
        }
    }
    finally {
        Remove-Item -LiteralPath $chunkPath -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $headerPath -Force -ErrorAction SilentlyContinue
    }
}

function Download-FileWithRetry([string]$Uri, [string]$Destination) {
    $temporaryPath = "$Destination.download"
    $curl = Get-Command curl.exe -ErrorAction SilentlyContinue

    if ($null -ne $curl) {
        $remoteLength = Get-RemoteFileLength $Uri $curl
        if ($remoteLength -gt 8MB) {
            Download-WithCurlRanges $Uri $temporaryPath $curl $remoteLength
        }
        else {
            Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue
            $maxAttempts = 3
            for ($attempt = 1; $attempt -le $maxAttempts; $attempt++) {
                $curlArguments = @(
                    "--fail",
                    "--location",
                    "--silent",
                    "--show-error",
                    "--http1.1",
                    "--retry", "0",
                    "--connect-timeout", "30",
                    "--max-time", "1800",
                    "--continue-at", "-",
                    "--output", $temporaryPath,
                    $Uri
                )
                & $curl.Source @curlArguments
                $curlExitCode = $LASTEXITCODE
                if ($curlExitCode -eq 0 -and (Test-Path -LiteralPath $temporaryPath -PathType Leaf)) {
                    $downloadLength = (Get-Item -LiteralPath $temporaryPath).Length
                    if ($downloadLength -gt 0) {
                        break
                    }
                }

                if ($attempt -eq $maxAttempts) {
                    Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue
                    throw "curl failed to download '$Uri' after $maxAttempts attempts (last exit code $curlExitCode)."
                }

                $delaySeconds = [Math]::Min(12, 2 * $attempt)
                Write-Warning "Download attempt $attempt made no complete progress; retrying in $delaySeconds seconds."
                Start-Sleep -Seconds $delaySeconds
            }
        }

        if (-not (Test-Path -LiteralPath $temporaryPath -PathType Leaf)) {
            throw "curl did not create a download file for '$Uri'."
        }

        $curlLength = (Get-Item -LiteralPath $temporaryPath).Length
        if ($curlLength -le 0) {
            Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue
            throw "curl downloaded an empty file for '$Uri'."
        }

        Move-Item -LiteralPath $temporaryPath -Destination $Destination -Force
        return
    }

    $client = [System.Net.Http.HttpClient]::new()
    $client.Timeout = [TimeSpan]::FromMinutes(15)
    $client.DefaultRequestVersion = [Version]::new(1, 1)
    $client.DefaultVersionPolicy = [System.Net.Http.HttpVersionPolicy]::RequestVersionExact
    $client.DefaultRequestHeaders.UserAgent.ParseAdd("RobotCommand.Release/1.0")

    try {
        $maxAttempts = 4
        for ($attempt = 1; $attempt -le $maxAttempts; $attempt++) {
            Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue

            try {
                $response = $client.GetAsync(
                    $Uri,
                    [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead
                ).GetAwaiter().GetResult()

                try {
                    if (-not $response.IsSuccessStatusCode) {
                        throw "HTTP $([int]$response.StatusCode) $($response.ReasonPhrase)"
                    }

                    $responseStream = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
                    try {
                        $fileStream = [IO.File]::Open(
                            $temporaryPath,
                            [IO.FileMode]::CreateNew,
                            [IO.FileAccess]::Write,
                            [IO.FileShare]::None
                        )
                        try {
                            $responseStream.CopyToAsync($fileStream).GetAwaiter().GetResult()
                        }
                        finally {
                            $fileStream.Dispose()
                        }
                    }
                    finally {
                        $responseStream.Dispose()
                    }
                }
                finally {
                    $response.Dispose()
                }

                if (-not (Test-Path -LiteralPath $temporaryPath -PathType Leaf)) {
                    throw "The download did not create a file."
                }

                $length = (Get-Item -LiteralPath $temporaryPath).Length
                if ($length -le 0) {
                    throw "The download was empty."
                }

                Move-Item -LiteralPath $temporaryPath -Destination $Destination -Force
                return
            }
            catch {
                Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue
                if ($attempt -eq $maxAttempts) {
                    throw "Failed to download '$Uri' after $maxAttempts attempts. Last error: $($_.Exception.Message)"
                }

                $delaySeconds = [Math]::Min(30, 2 * $attempt)
                Write-Warning "Download attempt $attempt failed: $($_.Exception.Message). Retrying in $delaySeconds seconds."
                Start-Sleep -Seconds $delaySeconds
            }
        }
    }
    finally {
        Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue
        $client.Dispose()
    }
}

function Get-GStreamerBaseUrls {
    $urls = [System.Collections.Generic.List[string]]::new()
    if (-not [string]::IsNullOrWhiteSpace($GStreamerBaseUrl)) {
        $urls.Add($GStreamerBaseUrl.TrimEnd('/'))
    }

    # /pkg is the current official download hierarchy. Keep the legacy path as
    # a fallback for mirrors or older provider deployments that still expose it.
    $urls.Add("https://gstreamer.freedesktop.org/pkg/windows/$GStreamerVersion/msvc")
    $urls.Add("https://gstreamer.freedesktop.org/data/pkg/windows/$GStreamerVersion/msvc")
    return @($urls | Select-Object -Unique)
}

function Download-GStreamerFile([string]$FileName, [string]$Destination) {
    $errors = [System.Collections.Generic.List[string]]::new()
    foreach ($baseUrl in @(Get-GStreamerBaseUrls)) {
        $uri = "$baseUrl/$FileName"
        Remove-Item -LiteralPath $Destination -Force -ErrorAction SilentlyContinue
        try {
            Download-FileWithRetry $uri $Destination
            return
        }
        catch {
            $errors.Add("${uri}: $($_.Exception.Message)")
            Write-Warning "GStreamer download failed from $uri. Trying the next source."
        }
    }

    throw "Unable to download GStreamer '$FileName' from the configured sources. $($errors -join ' | ')"
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
    $msiPath = Join-Path $CacheDir $fileName
    $checksumPath = "$msiPath.sha256sum"
    $extractedDir = Join-Path $CacheDir "extracted"
    $pinnedMsiPath = Join-Path $PinnedGStreamerDir $fileName
    $pinnedChecksumPath = "$pinnedMsiPath.sha256sum"
    $hasPinnedMsi = Test-Path -LiteralPath $pinnedMsiPath -PathType Leaf
    $hasPinnedChecksum = Test-Path -LiteralPath $pinnedChecksumPath -PathType Leaf

    if ($hasPinnedMsi -xor $hasPinnedChecksum) {
        throw "The pinned GStreamer bundle is incomplete. Add both '$fileName' and '$fileName.sha256sum' to '$PinnedGStreamerDir'."
    }

    New-Item -ItemType Directory -Force -Path $CacheDir | Out-Null
    $expected = $null
    if ($hasPinnedMsi) {
        Write-Section "Validating pinned GStreamer $GStreamerVersion runtime"
        $expected = (Get-Content -Raw -LiteralPath $pinnedChecksumPath).Trim() -split '\s+' | Select-Object -First 1
        if ($expected -notmatch '^[0-9a-fA-F]{64}$') {
            throw "The pinned GStreamer checksum file '$pinnedChecksumPath' does not contain a SHA-256 checksum."
        }

        $pinnedHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $pinnedMsiPath).Hash
        if ($pinnedHash -ine $expected) {
            throw "Pinned GStreamer runtime checksum mismatch. Expected $expected, received $pinnedHash."
        }

        # Seed the normal cache from the reviewed source-tree dependency. The
        # package build then follows the same extraction and validation path.
        Copy-Item -LiteralPath $pinnedMsiPath -Destination $msiPath -Force
        Copy-Item -LiteralPath $pinnedChecksumPath -Destination $checksumPath -Force
    } elseif (-not (Test-Path -LiteralPath $checksumPath)) {
        Write-Section "Downloading GStreamer $GStreamerVersion checksum"
        Download-GStreamerFile "$fileName.sha256sum" $checksumPath
    }

    if ([string]::IsNullOrWhiteSpace($expected)) {
        $expected = (Get-Content -Raw -LiteralPath $checksumPath).Trim() -split '\s+' | Select-Object -First 1
    }
    if ($expected -notmatch '^[0-9a-fA-F]{64}$') {
        if ($hasPinnedMsi) {
            throw "The pinned GStreamer checksum for '$pinnedMsiPath' is invalid."
        }

        Remove-Item -LiteralPath $checksumPath -Force
        Write-Section "Refreshing GStreamer $GStreamerVersion checksum"
        Download-GStreamerFile "$fileName.sha256sum" $checksumPath
        $expected = (Get-Content -Raw -LiteralPath $checksumPath).Trim() -split '\s+' | Select-Object -First 1
    }

    $downloadRequired = -not (Test-Path -LiteralPath $msiPath -PathType Leaf)
    if (-not $downloadRequired) {
        $cachedHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $msiPath).Hash
        $downloadRequired = $cachedHash -ine $expected
        if ($downloadRequired) {
            Write-Warning "Cached GStreamer runtime failed checksum validation; downloading it again."
            Remove-Item -LiteralPath $msiPath -Force
        }
    }

    if ($downloadRequired) {
        Write-Section "Downloading GStreamer $GStreamerVersion runtime"
        Download-GStreamerFile $fileName $msiPath
    }

    $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $msiPath).Hash.ToLowerInvariant()
    if ($actual -ne $expected.ToLowerInvariant()) {
        Remove-Item -LiteralPath $msiPath -Force
        throw "GStreamer runtime checksum mismatch after download. Expected $expected, received $actual."
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
    --no-restore `
    --self-contained true `
    $(foreach ($property in $publishProperties) { "-p:$property" }) `
    -o $OutputDir
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

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
