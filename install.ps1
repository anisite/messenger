# Installe MessengerWrapper pour l'utilisateur courant (sans droits admin).
#   .\install.ps1              -> copie + raccourci menu Démarrer
#   .\install.ps1 -Autostart   -> idem + lancement au démarrage de Windows (réduit dans la zone de notification)
#   .\install.ps1 -Uninstall   -> désinstalle (ajouter -KeepData pour garder la session)
param([switch]$Autostart, [switch]$Uninstall, [switch]$KeepData)
$ErrorActionPreference = 'Stop'

$name     = 'MessengerWrapper'
$target   = Join-Path $env:LOCALAPPDATA "Programs\$name"
$exe      = Join-Path $target "$name.exe"
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'Messenger++.lnk'
$runKey   = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'

Get-Process $name -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

if ($Uninstall) {
    Remove-Item $target -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item $shortcut -Force -ErrorAction SilentlyContinue
    Remove-ItemProperty $runKey -Name $name -ErrorAction SilentlyContinue
    if (-not $KeepData) { Remove-Item (Join-Path $env:LOCALAPPDATA $name) -Recurse -Force -ErrorAction SilentlyContinue }
    Write-Host 'Désinstallé.'
    return
}

# Le runtime WebView2 est livré avec Windows 11 ; on l'installe seulement s'il manque.
$wv2 = 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}'
if (-not (Test-Path $wv2) -and -not (Test-Path ($wv2 -replace 'HKLM:', 'HKCU:'))) {
    Write-Host 'Installation du runtime WebView2...'
    winget install --id Microsoft.EdgeWebView2Runtime -e --silent --accept-package-agreements --accept-source-agreements
}

New-Item -ItemType Directory -Force $target | Out-Null
Copy-Item (Join-Path $PSScriptRoot "$name.exe") $exe -Force

$lnk = (New-Object -ComObject WScript.Shell).CreateShortcut($shortcut)
$lnk.TargetPath = $exe
$lnk.WorkingDirectory = $target
$lnk.Save()

if ($Autostart) {
    Set-ItemProperty $runKey -Name $name -Value "`"$exe`" --hidden"
} else {
    Remove-ItemProperty $runKey -Name $name -ErrorAction SilentlyContinue
}

Write-Host "Installé dans $target - lance « Messenger++ » depuis le menu Démarrer."
