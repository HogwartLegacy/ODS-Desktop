<#
.SYNOPSIS
    Build, test and package the native Windows ODS desktop app.

.DESCRIPTION
    Runs the unit tests, publishes ODS.exe and ods-cli.exe self-contained for
    one Windows runtime (no .NET install needed on the user's PC) and, with
    -Pack, builds the Velopack installer, full and delta update packages and
    the release feed in artifacts\releases\<runtime>.

.EXAMPLE
    .\build.ps1                         # test and publish win-x64
    .\build.ps1 -Pack                   # also build Setup.exe and update packages
    .\build.ps1 -Pack -Runtime win-arm64
    .\build.ps1 -Pack -AzureTrustedSignFile .\signing.json   # signed release
#>
[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64',
    [string]$Configuration = 'Release',
    [switch]$SkipTests,
    [switch]$Pack,
    # signtool.exe parameters, e.g. '/a /fd sha256 /tr http://timestamp.digicert.com /td sha256'
    [string]$SignParams = '',
    # Azure Trusted Signing metadata.json (preferred for CI)
    [string]$AzureTrustedSignFile = '',
    # Also build a machine-wide .msi bootstrapper for enterprise deployment
    [switch]$Msi,
    # Override the prerelease label (e.g. preview.2); empty for a final release
    [string]$Prerelease
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

function Invoke-Step([string]$Title, [scriptblock]$Command) {
    Write-Host "==> $Title" -ForegroundColor Cyan
    & $Command
    if ($LASTEXITCODE -ne 0) { throw "$Title failed (exit $LASTEXITCODE)." }
}

$versionProperties = @()
if ($PSBoundParameters.ContainsKey('Prerelease')) {
    $odsVersion = (dotnet msbuild src\Ods.Desktop\Ods.Desktop.csproj -getProperty:OdsVersion).Trim()
    $versionProperties = @("-p:Version=$(if ($Prerelease) { "$odsVersion-$Prerelease" } else { $odsVersion })")
}
$version = (dotnet msbuild src\Ods.Desktop\Ods.Desktop.csproj -getProperty:Version @versionProperties).Trim()
$publish = Join-Path $PSScriptRoot "artifacts\publish\$Runtime"
$releases = Join-Path $PSScriptRoot "artifacts\releases\$Runtime"
Write-Host "ODS Desktop $version ($Runtime, $Configuration)"

Invoke-Step 'Restore tools' { dotnet tool restore }

if (-not $SkipTests) {
    Invoke-Step 'Unit tests' { dotnet test --project tests\Ods.Core.Tests\Ods.Core.Tests.csproj -c $Configuration }
}

if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
foreach ($project in 'src\Ods.Desktop\Ods.Desktop.csproj', 'src\Ods.Cli\Ods.Cli.csproj') {
    Invoke-Step "Publish $project" {
        dotnet publish $project -c $Configuration -r $Runtime --self-contained true -o $publish -p:DebugType=None -p:DebugSymbols=false @versionProperties
    }
}
foreach ($required in 'ODS.exe', 'ods-cli.exe', 'data\model-library.json', 'data\runtime-pins.json') {
    if (-not (Test-Path (Join-Path $publish $required))) { throw "The published app is missing $required." }
}
Write-Host ("Published {0:N0} MB to {1}" -f ((Get-ChildItem $publish -Recurse | Measure-Object Length -Sum).Sum / 1MB), $publish)

if (-not $Pack) { return }

$channel = if ($Runtime -eq 'win-arm64') { 'win-arm64' } else { 'win' }
$icon = Resolve-Path 'upstream\extensions\services\dashboard\public\ods.ico'
$arguments = @(
    'vpk', 'pack',
    '--packId', 'ODSDesktop',
    '--packVersion', $version,
    '--packTitle', 'ODS',
    '--packAuthors', 'Osmantic',
    '--packDir', $publish,
    '--mainExe', 'ODS.exe',
    '--icon', $icon,
    '--channel', $channel,
    '--runtime', $Runtime,
    '--shortcuts', 'Desktop,StartMenuRoot',
    '--releaseNotes', 'RELEASE_NOTES.md',
    '--outputDir', $releases
)
if ($SignParams) { $arguments += @('--signParams', $SignParams) }
if ($AzureTrustedSignFile) { $arguments += @('--azureTrustedSignFile', $AzureTrustedSignFile) }
if ($Msi) { $arguments += '--msi' }
if (-not $SignParams -and -not $AzureTrustedSignFile) {
    Write-Warning 'Building an unsigned installer. Windows SmartScreen will warn users; sign releases with -AzureTrustedSignFile or -SignParams.'
}
Invoke-Step 'Package with Velopack' { dotnet @arguments }
Get-ChildItem $releases | Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } } | Format-Table -AutoSize
