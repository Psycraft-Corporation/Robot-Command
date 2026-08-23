[CmdletBinding()]
param(
    [string]$Path = (Get-Location).Path,
    [switch]$SourceOnly,
    [switch]$TrackedOnly
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($Path)
$forbiddenNames = @(
    '.env', 'NuGet.config', 'appsettings.local.json', 'AGENTS.md', 'INSTRUCTIONS.md',
    'bin', 'obj', 'artifacts', 'out', 'tmp', 'TestResults', '.dart_tool',
    'data', 'logs', 'screenshots', 'sitl', '.git', '.codex', '.agents'
)
$forbiddenExtensions = @('.dll', '.exe', '.pdb', '.nupkg', '.snupkg', '.zip', '.tar.gz', '.dmg', '.msix', '.deb', '.rpm', '.dmp', '.etl')
$privateEndpointFragments = @('pkgs' + '.dev.azure.com', 'dracula' + '.local')
$credentialPatterns = @(
    '(?i)ClearTextPassword"\s+value="(?!YOUR_|<|placeholder|example)[^"]+"',
    '(?i)AZURE_ARTIFACTS_PAT\s*[:=]\s*["\x27]?(?!YOUR_|<|placeholder|example)[^\s"\x27]+',
    '(?i)NUGET_ORG_API_KEY\s*[:=]\s*["\x27]?(?!YOUR_|<|placeholder|example)[^\s"\x27]+',
    '(?i)(github_pat_|ghp_)[A-Za-z0-9_]+'
)

if ($SourceOnly) {
    $sourceRoot = Join-Path ([IO.Path]::GetTempPath()) ('robot-command-source-validation-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $sourceRoot -Force | Out-Null
    Get-ChildItem -LiteralPath $root -Recurse -Force -File | ForEach-Object {
        $relativePath = $_.FullName.Substring($root.Length).TrimStart([char[]]@([char]92, [char]47))
        $parts = $relativePath -split '[\\/]'
        $leaf = [IO.Path]::GetFileName($relativePath)
        $isForbidden = ($parts | Where-Object { $forbiddenNames -contains $_ }) -or
            ($leaf -like '.env.*') -or
            ($forbiddenExtensions | Where-Object { $leaf.EndsWith($_, [StringComparison]::OrdinalIgnoreCase) })
        if (-not $isForbidden) {
            $destination = Join-Path $sourceRoot $relativePath
            New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
            Copy-Item -LiteralPath $_.FullName -Destination $destination -Force
        }
    }
    $scanRoot = $sourceRoot
    $paths = Get-ChildItem -LiteralPath $scanRoot -Recurse -Force -File | ForEach-Object {
        $_.FullName.Substring($scanRoot.Length).TrimStart([char[]]@([char]92, [char]47))
    }
} elseif ($TrackedOnly) {
    $scanRoot = $root
    $paths = git -C $root ls-files | Where-Object { Test-Path -LiteralPath (Join-Path $root $_) }
    if ($LASTEXITCODE -ne 0) { throw 'Unable to read the tracked-file manifest.' }
} else {
    $scanRoot = $root
    $paths = Get-ChildItem -LiteralPath $root -Recurse -Force -File | ForEach-Object {
        $_.FullName.Substring($root.Length).TrimStart([char]92, [char]47)
    }
}

$violations = foreach ($relativePath in $paths) {
    $parts = $relativePath -split '[\\/]'
    $leaf = [IO.Path]::GetFileName($relativePath)
    $fullPath = Join-Path $scanRoot $relativePath
    if (($parts | Where-Object { $forbiddenNames -contains $_ }) -or
        ($leaf -like '.env.*') -or
        ($forbiddenNames -contains $leaf) -or
        ($forbiddenExtensions | Where-Object { $leaf.EndsWith($_, [StringComparison]::OrdinalIgnoreCase) })) {
        $relativePath
    } elseif ((Get-Item -LiteralPath $fullPath).Length -gt 25MB) {
        "${relativePath}: file exceeds 25 MB source limit"
    }
}

if ($violations) {
    Write-Error ('Forbidden release-tree entries found:' + [Environment]::NewLine + (($violations | Sort-Object -Unique) -join [Environment]::NewLine))
    exit 1
}

$contentViolations = foreach ($relativePath in $paths) {
    $fullPath = Join-Path $scanRoot $relativePath
    try { $content = Get-Content -LiteralPath $fullPath -Raw -ErrorAction Stop } catch { continue }

    foreach ($fragment in $privateEndpointFragments) {
        if ($content.IndexOf($fragment, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            "${relativePath}: private endpoint marker"
        }
    }
    foreach ($pattern in $credentialPatterns) {
        if ([regex]::IsMatch($content, $pattern)) {
            "${relativePath}: credential-like assignment"
        }
    }
    if ($content -match '(?i)https?://(dev\.azure\.com|[A-Za-z0-9.-]+\.pkgs\.dev\.azure\.com)/') {
        "${relativePath}: private Azure endpoint"
    }
}

if ($contentViolations) {
    Write-Error ('Forbidden source content found:' + [Environment]::NewLine + (($contentViolations | Sort-Object -Unique) -join [Environment]::NewLine))
    exit 1
}

Write-Host "Source tree validation passed: $($paths.Count) files checked."
