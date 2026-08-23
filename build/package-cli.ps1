[CmdletBinding()]
param(
    [string]$Runtime = "win-x64",
    [string]$Version = "0.1.0-dev",
    [string]$SourceRevisionId = ""
)

$ErrorActionPreference = "Stop"
$rootDir = Split-Path -Parent $PSScriptRoot
$outputDir = Join-Path $rootDir "artifacts/publish-cli/$Runtime"
$packageDir = Join-Path $rootDir "artifacts/packages"
$project = Join-Path $rootDir "src/cli/RobotCommand.Cli/RobotCommand.Cli.csproj"
$informationalVersion = $Version
if (-not [string]::IsNullOrWhiteSpace($SourceRevisionId)) {
    $informationalVersion = "$Version+$SourceRevisionId"
}

Remove-Item -Recurse -Force $outputDir -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $outputDir, $packageDir | Out-Null

dotnet publish $project `
    -c Release `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:PublishTrimmed=false `
    -p:Version=$Version `
    -p:InformationalVersion=$informationalVersion `
    -p:SourceRevisionId=$SourceRevisionId `
    -o $outputDir

$archive = Join-Path $packageDir "RobotCommand.Cli-$Version-$Runtime.zip"
Remove-Item -Force $archive -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $outputDir "*") -DestinationPath $archive
Write-Host "Package: $archive" -ForegroundColor Green
