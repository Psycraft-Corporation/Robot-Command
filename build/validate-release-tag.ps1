[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Tag,
    [string]$ExpectedVersion
)

$ErrorActionPreference = 'Stop'

if ($Tag -notmatch '^v(?<version>\d+\.\d+\.\d+(?:-[0-9A-Za-z][0-9A-Za-z.-]*)?)$') {
    throw "Release tag '$Tag' must match vMAJOR.MINOR.PATCH or a prerelease form such as v0.1.0-beta.8."
}

$version = $Matches.version
if (-not [string]::IsNullOrWhiteSpace($ExpectedVersion) -and $ExpectedVersion -ne $version) {
    throw "Release tag version '$version' does not match expected version '$ExpectedVersion'."
}

Write-Output $version
