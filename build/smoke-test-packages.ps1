[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ApplicationPath,
    [Parameter(Mandatory = $true)]
    [string]$CliPath,
    [int]$StartupSeconds = 5
)

$ErrorActionPreference = "Stop"

foreach ($path in @($ApplicationPath, $CliPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Smoke-test executable was not found: $path"
    }
}

Write-Host "Checking CLI help..."
$help = & $CliPath --help 2>&1
if ($LASTEXITCODE -ne 0) {
    throw "CLI --help failed with exit code $LASTEXITCODE.`n$($help -join [Environment]::NewLine)"
}

Write-Host "Starting application..."
$process = Start-Process -FilePath $ApplicationPath -WorkingDirectory (Split-Path -Parent $ApplicationPath) -PassThru
try {
    Start-Sleep -Seconds $StartupSeconds
    if ($process.HasExited) {
        throw "Application exited during startup with code $($process.ExitCode)."
    }
}
finally {
    if (-not $process.HasExited) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        $process.WaitForExit(10000) | Out-Null
    }
    if (-not $process.HasExited) {
        throw "Application process '$($process.Id)' did not exit after the smoke test."
    }
    $process.Dispose()
}

Write-Host "Package smoke test passed." -ForegroundColor Green
