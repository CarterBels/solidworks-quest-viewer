# Installs the SolidWorks Quest Sender add-in: copies it to %LOCALAPPDATA%\SolidWorksQuestSender (building it first
# when run from the source tree) and registers it. Asks for administrator rights (UAC), because SolidWorks reads its
# add-in list from HKEY_LOCAL_MACHINE. Close SolidWorks first.
#   powershell -ExecutionPolicy Bypass -File install.ps1            install / update
#   powershell -ExecutionPolicy Bypass -File install.ps1 -Uninstall remove
# The window stays open at the end so you can read what happened (-NoPause to skip that).
param([switch]$Uninstall, [switch]$NoPause, [switch]$Elevated)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$dest = Join-Path $env:LOCALAPPDATA 'SolidWorksQuestSender'
$dll = Join-Path $dest 'SolidWorksQuestSender.dll'
$log = Join-Path $dest 'install.log'
$regasm = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe'
$addinKey = 'HKLM:\SOFTWARE\SolidWorks\AddIns\{6c1b7a52-3f0e-4e4b-9d0a-8723a11ce5e1}'
$interop = 'sldworks', 'swconst', 'swpublished'

function Done($code) {
    if (-not $NoPause -and -not $Elevated) { Write-Host ''; Read-Host 'Press Enter to close' | Out-Null }
    exit $code
}

# Elevated part (started below through UAC): register / unregister with RegAsm, logged to install.log.
if ($Elevated) {
    Start-Transcript -Path $log -Force | Out-Null
    try {
        if ($Uninstall) { if (Test-Path $dll) { & $regasm /u $dll } }
        else {
            & $regasm /codebase $dll
            if ($LASTEXITCODE -ne 0) { throw "RegAsm failed ($LASTEXITCODE)" }
        }
        exit 0
    } catch { Write-Host $_; exit 1 }
    finally { Stop-Transcript | Out-Null }
}

try {
    if (Get-Process SLDWORKS -ErrorAction SilentlyContinue) {
        Write-Host 'Close SolidWorks first, then run this again.' -ForegroundColor Yellow
        Done 1
    }
    if (-not $Uninstall) {
        New-Item -ItemType Directory -Force $dest | Out-Null
        if (Test-Path "$here\SolidWorksQuestSender.dll") {
            # Downloaded release: prebuilt. Windows marks downloaded files, and .NET may refuse to load marked ones.
            Get-ChildItem $here -File | Unblock-File
            Copy-Item "$here\SolidWorksQuestSender.dll" $dest -Force
            if (Test-Path "$here\SolidWorks.Interop.sldworks.dll") {
                # The SolidWorks API assemblies it was built against (SolidWorks' redistributable files), so it
                # loads whatever SolidWorks version or service pack is installed.
                foreach ($n in $interop) { Copy-Item "$here\SolidWorks.Interop.$n.dll" $dest -Force }
            } else {
                $sw = $null
                foreach ($key in Get-ChildItem 'HKLM:\SOFTWARE\SolidWorks' -ErrorAction SilentlyContinue) {
                    $folder = (Get-ItemProperty "$($key.PSPath)\Setup" -ErrorAction SilentlyContinue).'SolidWorks Folder'
                    if ($folder -and (Test-Path "$folder\api\redist\SolidWorks.Interop.sldworks.dll")) { $sw = $folder }
                }
                if (-not $sw) { $sw = 'C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS' }
                if (-not (Test-Path "$sw\api\redist\SolidWorks.Interop.sldworks.dll")) { throw "SolidWorks not found (looked in $sw)." }
                foreach ($n in $interop) { Copy-Item "$sw\api\redist\SolidWorks.Interop.$n.dll" $dest -Force }
            }
        } else {
            Write-Host 'Building...'
            & dotnet build "$here\SolidWorksSender.csproj" -c Release -nologo -v q
            if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
            Copy-Item "$here\bin\Release\net48\*.dll" $dest -Force
        }
        Get-ChildItem $dest -File | Unblock-File
    }

    Write-Host 'Registering (click Yes on the Windows prompt)...'
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$($MyInvocation.MyCommand.Path)`"", '-Elevated')
    if ($Uninstall) { $argList += '-Uninstall' }
    $p = Start-Process powershell -Verb RunAs -ArgumentList $argList -Wait -PassThru -WindowStyle Hidden
    if ($p.ExitCode -ne 0) {
        Write-Host "Registration failed (exit $($p.ExitCode)). From $log :" -ForegroundColor Red
        if (Test-Path $log) { Get-Content $log | Select-String 'error|failed|Could not' | ForEach-Object { Write-Host "  $_" } }
        Done 1
    }
    if ($Uninstall) { Write-Host 'Removed. Start SolidWorks as usual.' -ForegroundColor Green; Done 0 }
    if (-not (Test-Path $addinKey)) {
        Write-Host "Registered, but SolidWorks' add-in list has no Quest Viewer entry. See $log" -ForegroundColor Red
        Done 1
    }
    Write-Host "Installed. Start SolidWorks: the 'Quest Viewer' tab appears for parts and assemblies." -ForegroundColor Green
    Write-Host "(If not: Tools > Add-ins > tick Quest Viewer.)"
    Done 0
} catch {
    Write-Host "Install failed: $_" -ForegroundColor Red
    Done 1
}
