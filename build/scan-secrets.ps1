[CmdletBinding()]
param(
    [string]$Path = (Get-Location).Path,
    [string]$ReportDirectory = '',
    [switch]$SourceOnly,
    [switch]$History
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($Path)
$gitleaks = Get-Command gitleaks -ErrorAction SilentlyContinue
if (-not $gitleaks) {
    throw 'gitleaks is required. Install it separately; do not add it to the repository.'
}

if ([string]::IsNullOrWhiteSpace($ReportDirectory)) {
    $ReportDirectory = Join-Path ([IO.Path]::GetTempPath()) 'robot-command-gitleaks'
}
New-Item -ItemType Directory -Path $ReportDirectory -Force | Out-Null

function Invoke-GitleaksScan {
    param(
        [string]$Mode,
        [string]$ReportName,
        [string[]]$Arguments
    )

    $reportPath = Join-Path $ReportDirectory $ReportName
    $common = @('--redact', '--no-banner', '--exit-code', '1', '--report-format', 'json', '--report-path', $reportPath)
    & $gitleaks.Source $Mode @Arguments @common
    $exitCode = $LASTEXITCODE
    if (($exitCode -ne 0) -and ($exitCode -ne 1)) {
        throw "gitleaks failed for $Mode with exit code $exitCode."
    }
    if ($exitCode -eq 1) {
        Write-Error "Secret findings were detected by the $Mode scan. Redacted report: $reportPath"
        return $false
    }
    Write-Host "gitleaks $Mode scan passed. Redacted report: $reportPath"
    return $true
}

$scanPath = $root
if ($SourceOnly) {
    $scanPath = Join-Path ([IO.Path]::GetTempPath()) ('robot-command-source-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $scanPath -Force | Out-Null
    $forbiddenNames = @(
        '.env', 'NuGet.config', 'appsettings.local.json', 'AGENTS.md',
        'bin', 'obj', 'artifacts', 'out', 'tmp', 'TestResults', '.dart_tool',
        'data', 'logs', 'screenshots', 'sitl', '.git'
    )
    $forbiddenExtensions = @('.nupkg', '.snupkg', '.zip', '.tar.gz', '.dmg', '.msix', '.deb', '.rpm')
    Get-ChildItem -LiteralPath $root -Recurse -Force -File | ForEach-Object {
        $relativePath = $_.FullName.Substring($root.Length).TrimStart([char[]]@([char]92, [char]47))
        $parts = $relativePath -split '[\\/]'
        $leaf = [IO.Path]::GetFileName($relativePath)
        $isForbidden = ($parts | Where-Object { $forbiddenNames -contains $_ }) -or
            ($leaf -like '.env.*') -or
            ($forbiddenExtensions | Where-Object { $leaf.EndsWith($_, [StringComparison]::OrdinalIgnoreCase) })
        if (-not $isForbidden) {
            $destination = Join-Path $scanPath $relativePath
            $destinationDirectory = Split-Path -Parent $destination
            New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null
            Copy-Item -LiteralPath $_.FullName -Destination $destination -Force
        }
    }
}

$passed = Invoke-GitleaksScan -Mode 'dir' -ReportName 'directory.json' -Arguments @($scanPath)
if ($History) {
    $historyPassed = Invoke-GitleaksScan -Mode 'git' -ReportName 'history.json' -Arguments @($root)
    $passed = $passed -and $historyPassed
}

if (-not $passed) {
    exit 1
}
