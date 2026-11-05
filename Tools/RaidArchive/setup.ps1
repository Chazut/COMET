param(
    [string]$Game = 'C:\Games\SPT-4.1',
    [string]$Archive = "$env:USERPROFILE\Documents\COMET-RaidArchive",
    [int]$Port = 8791,
    [switch]$NoStartup
)
$ErrorActionPreference = 'Stop'
$python = (Get-Command python.exe).Source
$pythonWindowless = Join-Path (Split-Path $python) 'pythonw.exe'
if (-not (Test-Path -LiteralPath $pythonWindowless)) { throw 'pythonw.exe is required for the background collector.' }
$settings = Join-Path $PSScriptRoot 'settings.json'
if (-not (Test-Path -LiteralPath $settings)) {
    @{ game=$Game; archive=$Archive; port=$Port; intervalSeconds=30; settleSeconds=30;
       minimumFreeBytes=1073741824; rrUrl='http://127.0.0.1:7829' } |
        ConvertTo-Json | Set-Content -LiteralPath $settings -Encoding utf8
}
$app = Join-Path $PSScriptRoot 'app.py'
$shell = New-Object -ComObject WScript.Shell
$desktop = [Environment]::GetFolderPath('Desktop')
$link = $shell.CreateShortcut((Join-Path $desktop 'COMET Raid Observatory.lnk'))
$link.TargetPath=$pythonWindowless
$link.Arguments='"' + $app + '" --settings "' + $settings + '" --open'
$link.WorkingDirectory=$PSScriptRoot
$link.Description='Local raid archive and feature coverage'
$link.Save()
if (-not $NoStartup) {
    $startup = [Environment]::GetFolderPath('Startup')
    $link = $shell.CreateShortcut((Join-Path $startup 'COMET Raid Observatory.lnk'))
    $link.TargetPath=$pythonWindowless
    $link.Arguments='"' + $app + '" --settings "' + $settings + '"'
    $link.WorkingDirectory=$PSScriptRoot
    $link.Save()
}
Start-Process -FilePath $pythonWindowless -ArgumentList ('"' + $app + '" --settings "' + $settings + '"') -WindowStyle Hidden
Write-Output "Collector started. Dashboard: http://127.0.0.1:$Port"
Write-Output "Archive: $Archive"
