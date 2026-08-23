[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Version,
    [Parameter(Mandatory = $true)]
    [string]$Commit,
    [string]$Runtime = "win-x64",
    [string]$PackageDirectory = "artifacts/packages",
    [string]$GStreamerVersion = "1.26.11"
)

$ErrorActionPreference = "Stop"
$rootDir = Split-Path -Parent $PSScriptRoot
$directory = [IO.Path]::GetFullPath((Join-Path $rootDir $PackageDirectory))
if (-not (Test-Path -LiteralPath $directory -PathType Container)) {
    throw "Package directory '$directory' does not exist."
}

$files = Get-ChildItem -LiteralPath $directory -File | Where-Object {
    $_.Name -notin @("SHA256SUMS", "RobotCommand-$Version-release-manifest.json")
}
if ($files.Count -eq 0) {
    throw "No release artifacts were found in '$directory'."
}

$artifacts = @($files | Sort-Object Name | ForEach-Object {
    $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash.ToLowerInvariant()
    [ordered]@{
        name = $_.Name
        size = $_.Length
        sha256 = $hash
    }
})

$manifest = [ordered]@{
    schema = "robot-command.release.v1"
    product = "Robot Command"
    version = $Version
    commit = $Commit
    runtime = $Runtime
    windowsBinariesSigned = $false
    gstreamerVersion = $GStreamerVersion
    artifacts = $artifacts
}

$manifestPath = Join-Path $directory "RobotCommand-$Version-release-manifest.json"
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifestPath -Encoding UTF8

$checksumLines = @($files + (Get-Item -LiteralPath $manifestPath) | Sort-Object Name | ForEach-Object {
    $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash.ToLowerInvariant()
    "$hash *$($_.Name)"
})
$checksumLines | Set-Content -LiteralPath (Join-Path $directory "SHA256SUMS") -Encoding ASCII
Write-Host "Manifest and checksums written to $directory" -ForegroundColor Green
