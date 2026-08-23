[CmdletBinding()]
param(
    [string] $Path = (Get-Location).Path
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path -LiteralPath $Path).Path
$docs = @(
    (Join-Path $root "README.md"),
    (Join-Path $root "CONTRIBUTING.md")
)
$docs += Get-ChildItem -LiteralPath (Join-Path $root "docs") -Filter "*.md" -File -Recurse | Select-Object -ExpandProperty FullName
$missing = [System.Collections.Generic.List[string]]::new()

foreach ($document in $docs) {
    $text = Get-Content -LiteralPath $document -Raw
    $matches = [regex]::Matches($text, "\]\(([^)#]+)(?:#[^)]+)?\)")
    foreach ($match in $matches) {
        $target = $match.Groups[1].Value.Trim()
        if ($target -match "^(https?|mailto):") { continue }
        $candidate = Join-Path (Split-Path -Parent $document) $target
        if (-not (Test-Path -LiteralPath $candidate)) {
            $missing.Add("$([System.IO.Path]::GetRelativePath($root, $document)): $target")
        }
    }
}

if ($missing.Count -gt 0) {
    $missing | Sort-Object -Unique | ForEach-Object { Write-Error "Missing documentation link: $_" }
    exit 1
}

Write-Output "Documentation link validation passed."
