<#
.SYNOPSIS
    Refresh upstream\ from a checkout of Osmantic/ODS, then run the tests.

.EXAMPLE
    git clone https://github.com/Osmantic/ODS ..\ODS
    .\sync-upstream.ps1 -OdsPath ..\ODS\ods
#>
[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$OdsPath, [switch]$SkipTests)

$ErrorActionPreference = 'Stop'
$files = @(
    'config\model-library.json', 'config\extensions-catalog.json',
    'config\backends\amd.json', 'config\backends\nvidia.json',
    'tests\fixtures\model-selection-envelopes.json', 'tests\fixtures\model-selection-golden.json',
    'installers\windows\lib\constants.ps1',
    'extensions\services\dashboard\public\ods.ico', 'extensions\services\dashboard\public\ods-icon-192.png')
$source = (Resolve-Path $OdsPath).Path
foreach ($file in $files) {
    $from = Join-Path $source $file
    if (-not (Test-Path $from)) { throw "$from is missing; point -OdsPath at the ods folder of an ODS checkout." }
    $to = Join-Path $PSScriptRoot "upstream\$file"
    New-Item -ItemType Directory -Force (Split-Path $to) | Out-Null
    Copy-Item $from $to -Force
}
$commit = git -C $source rev-parse --short=12 HEAD
$readme = Join-Path $PSScriptRoot 'upstream\README.md'
(Get-Content $readme -Raw) -replace 'commit `[0-9a-f]+`', "commit ``$commit``" | Set-Content $readme -Encoding utf8
Write-Host "Vendored files updated to ODS commit $commit."
if (-not $SkipTests) {
    dotnet test --project (Join-Path $PSScriptRoot 'tests\Ods.Core.Tests\Ods.Core.Tests.csproj')
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed against the refreshed files; see the output for what drifted.' }
}
