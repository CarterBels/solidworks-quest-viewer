# Builds (if needed; a downloaded release is prebuilt) and registers the SolidWorks Quest Sender add-in. Asks for administrator rights (UAC),
# because SolidWorks reads its add-in list from HKEY_LOCAL_MACHINE. Close SolidWorks first.
#   powershell -ExecutionPolicy Bypass -File install.ps1            install / update
#   powershell -ExecutionPolicy Bypass -File install.ps1 -Uninstall remove
param([switch]$Uninstall)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$dest = Join-Path $env:LOCALAPPDATA 'SolidWorksQuestSender'
$dll = Join-Path $dest 'SolidWorksQuestSender.dll'
$regasm = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe'

$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $admin) {
    if (Get-Process SLDWORKS -ErrorAction SilentlyContinue) {
        Write-Host 'Close SolidWorks first, then run this again.' -ForegroundColor Yellow
        exit 1
    }
    if (-not $Uninstall) {
        New-Item -ItemType Directory -Force $dest | Out-Null
        if (Test-Path "$here\SolidWorksQuestSender.dll") {
            # Downloaded release: the add-in is prebuilt; SolidWorks' own API assemblies come from your SolidWorks.
            $sw = $null
            foreach ($key in Get-ChildItem 'HKLM:\SOFTWARE\SolidWorks' -ErrorAction SilentlyContinue) {
                $folder = (Get-ItemProperty "$($key.PSPath)\Setup" -ErrorAction SilentlyContinue).'SolidWorks Folder'
                if ($folder -and (Test-Path "$folder\api\redist\SolidWorks.Interop.sldworks.dll")) { $sw = $folder }
            }
            if (-not $sw) { $sw = 'C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS' }
            if (-not (Test-Path "$sw\api\redist\SolidWorks.Interop.sldworks.dll")) { throw "SolidWorks not found (looked in $sw)." }
            Write-Host "Using SolidWorks in $sw"
            Copy-Item "$here\SolidWorksQuestSender.dll" $dest -Force
            foreach ($n in 'sldworks', 'swconst', 'swpublished') {
                Copy-Item "$sw\api\redist\SolidWorks.Interop.$n.dll" $dest -Force
            }
        } else {
            Write-Host 'Building...'
            & dotnet build "$here\SolidWorksSender.csproj" -c Release -nologo -v q
            if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
            Copy-Item "$here\bin\Release\net48\*.dll" $dest -Force
        }
    }
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$($MyInvocation.MyCommand.Path)`"")
    if ($Uninstall) { $argList += '-Uninstall' }
    $p = Start-Process powershell -Verb RunAs -ArgumentList $argList -Wait -PassThru
    if ($p.ExitCode -ne 0) { Write-Host "Registration failed (exit $($p.ExitCode)). See $dest\install.log" -ForegroundColor Red; exit $p.ExitCode }
    if ($Uninstall) { Write-Host 'Removed. Start SolidWorks as usual.' }
    else { Write-Host "Installed. Start SolidWorks: the 'Quest Viewer' tab appears for parts and assemblies." -ForegroundColor Green }
    exit 0
}

# Elevated part: register / unregister with RegAsm.
Start-Transcript -Path (Join-Path $dest 'install.log') -Force | Out-Null
try {
    if ($Uninstall) {
        if (Test-Path $dll) { & $regasm /u $dll }
    } else {
        & $regasm /codebase $dll
        if ($LASTEXITCODE -ne 0) { throw "RegAsm failed ($LASTEXITCODE)" }
    }
    exit 0
} catch {
    Write-Host $_
    exit 1
} finally {
    Stop-Transcript | Out-Null
}
