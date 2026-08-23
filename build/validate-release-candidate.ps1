[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Version,
    [string]$Commit = "",
    [switch]$SkipPackaging,
    [switch]$RunSITL
)

$ErrorActionPreference = "Stop"
$rootDir = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $rootDir

if ([string]::IsNullOrWhiteSpace($Commit)) {
    $Commit = (git rev-parse HEAD).Trim()
}

$reportDir = Join-Path $rootDir "artifacts/release-candidate"
$reportPath = Join-Path $reportDir "$Version-checklist.json"
New-Item -ItemType Directory -Force -Path $reportDir | Out-Null
$results = [System.Collections.Generic.List[object]]::new()

function Invoke-ValidationStep([string]$Name, [scriptblock]$Action) {
    $started = [DateTimeOffset]::UtcNow
    try {
        & $Action
        if ($null -ne $LASTEXITCODE -and $LASTEXITCODE -ne 0) {
            throw "Process exited with code $LASTEXITCODE."
        }
        $results.Add([ordered]@{
            name = $Name
            status = "passed"
            started = $started
            completed = [DateTimeOffset]::UtcNow
        })
        Write-Host "PASS  $Name" -ForegroundColor Green
    }
    catch {
        $results.Add([ordered]@{
            name = $Name
            status = "failed"
            started = $started
            completed = [DateTimeOffset]::UtcNow
            error = $_.Exception.Message
        })
        Write-Error "FAIL  ${Name}: $($_.Exception.Message)"
        throw
    }
}

Invoke-ValidationStep "Restore from public NuGet" {
    dotnet restore RobotCommand.sln --configfile NuGet.config.template
}
Invoke-ValidationStep "Release build" {
    dotnet build RobotCommand.sln -c Release --no-restore
}
Invoke-ValidationStep "Deterministic tests" {
    dotnet test RobotCommand.sln -c Release --no-restore --no-build --filter "Category!=SITL&Category!=Hardware"
}
Invoke-ValidationStep "Whitespace formatting" {
    dotnet format RobotCommand.sln whitespace --verify-no-changes --no-restore
}
Invoke-ValidationStep "Style formatting" {
    dotnet format RobotCommand.sln style --verify-no-changes --no-restore
}
Invoke-ValidationStep "Source-only tree" {
    & (Join-Path $rootDir "build/validate-source-tree.ps1") -SourceOnly
}
Invoke-ValidationStep "Dependency inventory" {
    & (Join-Path $rootDir "build/validate-dependency-inventory.ps1")
}
Invoke-ValidationStep "Documentation links" {
    & (Join-Path $rootDir "tools/validate-docs.ps1")
}
Invoke-ValidationStep "CLI help" {
    dotnet run --project src/cli/RobotCommand.Cli/RobotCommand.Cli.csproj -c Release --no-restore -- --help
}
Invoke-ValidationStep "CLI JSON diagnostic" {
    dotnet run --project src/cli/RobotCommand.Cli/RobotCommand.Cli.csproj -c Release --no-restore -- 3d status --json
}
Invoke-ValidationStep "MeshProbe restore" {
    dotnet restore tools/RobotCommand.MeshProbe/RobotCommand.MeshProbe.csproj --configfile NuGet.config.template
}
Invoke-ValidationStep "MeshProbe validation" {
    dotnet run --project tools/RobotCommand.MeshProbe/RobotCommand.MeshProbe.csproj -c Release --no-restore -- validate tools/RobotCommand.MeshProbe/fixtures/triangle.obj
}

if ($RunSITL) {
    if ($env:ROBOT_COMMAND_PX4_SITL -eq "1") {
        Invoke-ValidationStep "PX4 SITL tests" {
            dotnet test RobotCommand.sln -c Release --no-restore --filter "Category=SITL&FullyQualifiedName~Px4"
        }
    }
    if ($env:ROBOT_COMMAND_ARDUPILOT_SITL -eq "1") {
        Invoke-ValidationStep "ArduPilot SITL tests" {
            dotnet test RobotCommand.sln -c Release --no-restore --filter "Category=SITL&FullyQualifiedName~ArduPilot"
        }
    }
}

if (-not $SkipPackaging) {
    $packageOutputDir = Join-Path $rootDir "artifacts/packages"
    if (Test-Path -LiteralPath $packageOutputDir) {
        Get-ChildItem -LiteralPath $packageOutputDir -File | Remove-Item -Force
    }
    else {
        New-Item -ItemType Directory -Path $packageOutputDir -Force | Out-Null
    }

    Invoke-ValidationStep "Windows application package" {
        & (Join-Path $rootDir "build/package.ps1") -Runtime win-x64 -Version $Version -SourceRevisionId $Commit
    }
    Invoke-ValidationStep "Windows CLI package" {
        & (Join-Path $rootDir "build/package-cli.ps1") -Runtime win-x64 -Version $Version -SourceRevisionId $Commit
    }
    Invoke-ValidationStep "Release manifest and checksums" {
        & (Join-Path $rootDir "build/write-release-manifest.ps1") -Version $Version -Commit $Commit
    }
    Invoke-ValidationStep "Release archive contents" {
        & (Join-Path $rootDir "build/validate-release-artifacts.ps1") -Version $Version -Commit $Commit
    }
    Invoke-ValidationStep "Application and CLI startup" {
        & (Join-Path $rootDir "build/smoke-test-packages.ps1") `
            -ApplicationPath "artifacts/publish/win-x64/RobotCommand.exe" `
            -CliPath "artifacts/publish-cli/win-x64/RobotCommand.Cli.exe"
    }
}

$report = [ordered]@{
    schema = "robot-command.release-candidate.v1"
    product = "Robot Command"
    version = $Version
    commit = $Commit
    runtime = "win-x64"
    sitlRequested = [bool]$RunSITL
    hardwareValidation = "operator-gated"
    results = @($results)
}
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $reportPath -Encoding UTF8
Write-Host "Release-candidate checklist: $reportPath" -ForegroundColor Green
