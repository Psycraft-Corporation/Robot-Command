[CmdletBinding()]
param(
    [string]$Path = (Get-Location).Path
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($Path)
$inventoryPath = Join-Path $root 'docs/dependency-inventory.md'
if (-not (Test-Path -LiteralPath $inventoryPath)) {
    throw 'docs/dependency-inventory.md is missing.'
}

$inventory = Get-Content -LiteralPath $inventoryPath -Raw
$packageNames = @(
    Get-ChildItem -LiteralPath $root -Recurse -Filter '*.csproj' -File |
        Get-Content |
        ForEach-Object {
            if ($_ -notmatch '^\s*<!--' -and $_ -match '<PackageReference\s+Include="([^"]+)"') { $Matches[1] }
        }
)

$missing = $packageNames |
    Sort-Object -Unique |
    Where-Object { $inventory -notmatch ('\|\s*' + [regex]::Escape($_) + '\s*\|') }
if ($missing) {
    throw ('Dependencies missing from inventory: ' + ($missing -join ', '))
}

foreach ($requiredPath in @('LICENCE', 'NOTICE', 'SECURITY.md', 'THIRD-PARTY-NOTICES/README.md')) {
    if (-not (Test-Path -LiteralPath (Join-Path $root $requiredPath))) {
        throw "Required release file is missing: $requiredPath"
    }
}

Write-Host "Dependency inventory validation passed: $(@($packageNames | Sort-Object -Unique).Count) direct packages checked."
