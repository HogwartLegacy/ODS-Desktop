<#
.SYNOPSIS
    Acceptance test for a clean Windows machine: install ODS, prove local AI
    works with the installed binaries, start the app, then uninstall.

.DESCRIPTION
    Runs in CI on a fresh GitHub-hosted Windows VM and equally by hand on a new
    laptop or VM. It needs no Docker, WSL, Python or .NET install, which is the
    point: it checks the release the way a customer receives it.

.EXAMPLE
    .\clean-machine.ps1 -Setup .\ODSDesktop-win-Setup.exe
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Setup,
    [string]$DataRoot = (Join-Path $env:TEMP 'ods-clean-machine'),
    [string]$Model = 'qwen3.5-2b-q4',
    [string]$ResultPath = (Join-Path $env:TEMP 'ods-clean-machine-result.json')
)

$ErrorActionPreference = 'Stop'
$app = Join-Path $env:LOCALAPPDATA 'ODSDesktop'
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\ODSDesktop'
$env:ODS_DESKTOP_HOME = $DataRoot

function Assert-That([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "FAIL: $Message" }
    Write-Host "PASS: $Message" -ForegroundColor Green
}

# True once no llama-server from this ODS data folder is running (Windows needs
# a moment to tear down a terminated process). Other llama-servers are ignored.
function Test-NoOdsModelServer([int]$TimeoutSeconds = 15) {
    $runtimes = Join-Path $DataRoot 'runtimes'
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        $ours = @(Get-CimInstance Win32_Process -Filter "Name='llama-server.exe'" |
            Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($runtimes, [StringComparison]::OrdinalIgnoreCase) })
        if ($ours.Count -eq 0) { return $true }
        Start-Sleep -Milliseconds 500
    } while ((Get-Date) -lt $deadline)
    return $false
}

Write-Host "== Install (per user, silent)"
$process = Start-Process -FilePath $Setup -ArgumentList '--silent' -Wait -PassThru
Assert-That ($process.ExitCode -eq 0) 'Setup exits with code 0'
Assert-That (Test-Path (Join-Path $app 'current\ODS.exe')) 'ODS.exe is installed under %LOCALAPPDATA%\ODSDesktop'
Assert-That (Test-Path (Join-Path $app 'current\ods-cli.exe')) 'ods-cli.exe is installed beside it'
Assert-That (Test-Path $uninstallKey) 'ODS is listed in Settings > Apps'
Assert-That (Test-Path (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\ODS.lnk')) 'A Start menu shortcut exists'

Write-Host "== Local AI with the installed binaries"
& (Join-Path $app 'current\ods-cli.exe') smoke $Model --json-out $ResultPath
Assert-That ($LASTEXITCODE -eq 0) 'ods-cli smoke passes: llama.cpp runtime, verified model, server, generation proof, streaming chat'
Assert-That (Test-NoOdsModelServer) 'The model server stops when the smoke test ends'

Write-Host "== Desktop app starts and quits cleanly"
$gui = Start-Process -FilePath (Join-Path $app 'current\ODS.exe') -PassThru
Start-Sleep -Seconds 15
Assert-That (-not $gui.HasExited) 'ODS.exe starts and keeps running'
$quit = Start-Process -FilePath (Join-Path $app 'current\ODS.exe') -ArgumentList '--quit' -Wait -PassThru
Assert-That ($quit.ExitCode -eq 0) 'ODS.exe --quit hands off to the running instance'
Assert-That ($gui.WaitForExit(30000)) 'The running instance exits after --quit'

Write-Host "== Uninstall"
$process = Start-Process -FilePath (Join-Path $app 'Update.exe') -ArgumentList 'uninstall', '--silent' -Wait -PassThru
Assert-That ($process.ExitCode -eq 0) 'Uninstall exits with code 0'
Assert-That (-not (Test-Path (Join-Path $app 'current'))) 'Program files are removed'
Assert-That (-not (Test-Path $uninstallKey)) 'The Settings > Apps entry is removed'
Assert-That (-not (Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -ErrorAction SilentlyContinue).ODS) 'No sign-in entry remains'
Assert-That (Test-NoOdsModelServer) 'No ODS model server is left running'
Assert-That (Test-Path (Join-Path $DataRoot 'models')) 'User data (models, chats, settings) is kept, as ODS uninstallers do'

Write-Host "All clean-machine checks passed." -ForegroundColor Green
