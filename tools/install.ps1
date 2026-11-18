<#
  Licht & Lüfter installieren oder aktualisieren.

  Aufruf (aus dem Repo, z. B. nach "git pull"):   powershell -ExecutionPolicy Bypass -File tools\install.ps1
  Optionen:  -NoStart   App nach der Installation nicht starten

  Ablauf: baut die App (lädt bei Bedarf das .NET-10-SDK nach .tools\dotnet, keine Systeminstallation),
  beendet eine laufende Instanz sauber (Lüfter zurück aufs BIOS), kopiert nach "C:\Program Files\LichtLuefter",
  legt die Startmenü-Verknüpfung und den Eintrag unter "Apps & Features" an und startet die App im Tray.
  Einstellungen (%LOCALAPPDATA%\LichtLuefter) bleiben erhalten. Deinstallieren: über "Apps & Features".
#>
param([switch]$NoStart)
$ErrorActionPreference = 'Stop'

# Adminrechte (Programme-Ordner, HKLM, Aufgabenplanung)
$id = [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"")
    if ($NoStart) { $argList += '-NoStart' }
    Start-Process -FilePath (Get-Process -Id $PID).Path -Verb RunAs -ArgumentList $argList
    return
}

$repo    = Split-Path $PSScriptRoot -Parent
$target  = Join-Path $env:ProgramFiles 'LichtLuefter'
$exe     = Join-Path $target 'LichtLuefter.exe'
$dotnet  = Join-Path $repo '.tools\dotnet\dotnet.exe'
$publish = Join-Path $env:TEMP 'LichtLuefter-publish'

Write-Host '1/5  .NET-SDK prüfen'
if (-not (Test-Path $dotnet)) {
    $installer = Join-Path $env:TEMP 'LichtLuefter-dotnet-install.ps1'
    Invoke-WebRequest 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installer
    & $installer -Channel 10.0 -InstallDir (Join-Path $repo '.tools\dotnet') -NoPath
}

Write-Host '2/5  Bauen'
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'; $env:DOTNET_NOLOGO = '1'
& $dotnet publish (Join-Path $repo 'src\LichtLuefter') -c Release --self-contained true -o $publish -v q
if ($LASTEXITCODE -ne 0 -or -not (Test-Path (Join-Path $publish 'LichtLuefter.exe'))) { throw 'Bauen fehlgeschlagen.' }

Write-Host '3/5  Laufende Instanz beenden (Lüfter zurück auf BIOS)'
try { [System.Threading.EventWaitHandle]::OpenExisting('Local\LichtLuefter.Exit').Set() | Out-Null } catch { }
$until = (Get-Date).AddSeconds(20)
while ((Get-Process LichtLuefter -ErrorAction SilentlyContinue) -and (Get-Date) -lt $until) { Start-Sleep -Milliseconds 300 }
if (Get-Process LichtLuefter -ErrorAction SilentlyContinue) { throw 'Licht & Lüfter läuft noch. Bitte über das Tray-Symbol beenden und erneut ausführen.' }

Write-Host "4/5  Kopieren nach $target"
robocopy $publish $target /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "Kopieren fehlgeschlagen (robocopy $LASTEXITCODE)." }
Remove-Item $publish -Recurse -Force -ErrorAction SilentlyContinue

Write-Host '5/5  Startmenü und "Apps & Features"'
$shell = New-Object -ComObject WScript.Shell
$common = Join-Path ([Environment]::GetFolderPath('CommonPrograms')) 'Licht & Lüfter.lnk'
$lnk = $shell.CreateShortcut($common)
$lnk.TargetPath = $exe; $lnk.WorkingDirectory = $target; $lnk.IconLocation = "$exe,0"; $lnk.Description = 'Beleuchtung und Lüfter'
$lnk.Save()
# Alte Verknüpfung des Benutzers (zeigte auf den Entwicklungsordner) entfernen
$userLnk = Join-Path ([Environment]::GetFolderPath('Programs')) 'Licht & Lüfter.lnk'
if (Test-Path $userLnk) { Remove-Item $userLnk -Force }

$key = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\LichtLuefter'
New-Item -Path $key -Force | Out-Null
$version = (Get-Item $exe).VersionInfo.ProductVersion -replace '\+.*$', ''
$sizeKb = [int]((Get-ChildItem $target -Recurse | Measure-Object Length -Sum).Sum / 1KB)
$values = @{
    DisplayName = 'Licht & Lüfter'; DisplayVersion = $version; Publisher = 'juliuskum'
    InstallLocation = $target; DisplayIcon = "$exe,0"; URLInfoAbout = 'https://github.com/juliuskum/rgb-and-fan'
    UninstallString = "`"$exe`" --uninstall"; QuietUninstallString = "`"$exe`" --uninstall --quiet"
}
foreach ($k in $values.Keys) { Set-ItemProperty -Path $key -Name $k -Value $values[$k] }
Set-ItemProperty -Path $key -Name NoModify -Value 1 -Type DWord
Set-ItemProperty -Path $key -Name NoRepair -Value 1 -Type DWord
Set-ItemProperty -Path $key -Name EstimatedSize -Value $sizeKb -Type DWord

Write-Host "Fertig: Licht & Lüfter $version in $target"
if (-not $NoStart) { Start-Process $exe -ArgumentList '--tray' }
