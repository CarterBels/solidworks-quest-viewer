# Builds the SolidWorks Quest Viewer APK with Unity in batch mode and (optionally) installs it on a Quest over USB.
#   powershell -ExecutionPolicy Bypass -File Viewer\build.ps1            build only
#   powershell -ExecutionPolicy Bypass -File Viewer\build.ps1 -Install   build, then adb install
#   powershell -ExecutionPolicy Bypass -File Viewer\build.ps1 -Release   build signed with the release key (for a
#                                                                          GitHub release: tools\make-release.ps1)
#   powershell -ExecutionPolicy Bypass -File Viewer\build.ps1 -Upload    build, then upload to a Meta Horizon Store
#                                                                          release channel (-Notes "what changed")
# Needs Unity 6000.6.5f1 with Android Build Support (installed and signed in through Unity Hub). Close the project in
# the Unity editor first: batch mode can't open a project that is already open.
#
# Release signing and store upload (SolidWorks fork) use files in %USERPROFILE%\.questviewer, outside the repo:
#   release.keystore, signing.json  the release key and its password. Back them up: a store app can only be updated
#                                   with the same key. Used by -Upload only; other builds stay debug-signed.
#   version-code.txt                the next version code; every upload uses it and then counts it up.
#   upload.json                     appId and appSecret from the developer dashboard (API page), the channel (ALPHA
#                                   by default) and, if the store asks for one, ageGroup (e.g. TEENS_AND_ADULTS).
#   ovr-platform-util.exe           Meta's upload tool, downloaded from Meta on the first -Upload.
param([switch]$Install, [switch]$SkipSetup, [switch]$Upload, [switch]$Release, [string]$Notes = "")
$ErrorActionPreference = 'Stop'
$project = $PSScriptRoot
$version = ((Get-Content "$project\ProjectSettings\ProjectVersion.txt" | Select-String 'm_EditorVersion:') -split ':\s*')[1].Trim()

$candidates = @(
    "$env:ProgramFiles\Unity\Hub\Editor\$version\Editor\Unity.exe",
    "${env:ProgramFiles(x86)}\Unity\Hub\Editor\$version\Editor\Unity.exe"
)
$hubSecondary = "$env:APPDATA\UnityHub\secondaryInstallPath.json"
if (Test-Path $hubSecondary) {
    $dir = (Get-Content $hubSecondary -Raw).Trim('"', ' ', "`r", "`n")
    if ($dir) { $candidates += "$dir\$version\Editor\Unity.exe" }
}
$unity = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $unity) { throw "Unity $version not found. Install it with Android Build Support through Unity Hub." }

function Invoke-Unity($method, $log) {
    Write-Host "Unity: $method (log: $log)"
    $p = Start-Process $unity -ArgumentList @('-batchmode', '-quit', '-projectPath', "`"$project`"", '-buildTarget', 'Android',
        '-executeMethod', $method, '-logFile', "`"$log`"") -PassThru -NoNewWindow
    # Wait for Unity itself, not everything it started (its licensing helper keeps running).
    $null = $p.Handle  # keep the handle, or ExitCode reads as empty in Windows PowerShell
    $p.WaitForExit()
    if ($p.ExitCode -ne 0) { Get-Content $log -Tail 40; throw "Unity failed ($method), exit code $($p.ExitCode). See $log" }
}

$local = "$env:USERPROFILE\.questviewer"
$signing = "$local\signing.json"
if (($Upload -or $Release) -and (Test-Path $signing)) { # -Install keeps the debug key, matching the copy adb installed
    $s = Get-Content $signing -Raw | ConvertFrom-Json
    $env:QV_KEYSTORE = $s.keystore; $env:QV_KEY_ALIAS = $s.alias; $env:QV_KEYSTORE_PASS = $s.password
    Write-Host "Signing with the release key ($($s.keystore))"
} elseif ($Upload) { throw "No release key ($signing). The store doesn't accept debug-signed builds." }
$codeFile = "$local\version-code.txt"
if ($Upload -and (Test-Path $codeFile)) { $env:QV_VERSION_CODE = (Get-Content $codeFile -Raw).Trim(); Write-Host "Version code $env:QV_VERSION_CODE" }

New-Item -ItemType Directory -Force "$project\Logs" | Out-Null
$apk = "$project\Builds\Android\SolidWorksQuestViewer.apk"
if (Test-Path $apk) { Remove-Item $apk } # the build only logs failures, so a missing APK is how we notice one
if (-not $SkipSetup) { Invoke-Unity 'QuestViewerSetup.Run' "$project\Logs\setup.log" }
Invoke-Unity 'QuestViewerSetup.BuildQuest' "$project\Logs\build.log"

if (-not (Test-Path $apk)) { Get-Content "$project\Logs\build.log" -Tail 60; throw "No APK produced. See $project\Logs\build.log" }
Write-Host "Built $apk ($([math]::Round((Get-Item $apk).Length / 1MB, 1)) MB)"

if ($Install) {
    $adb = (Get-Command adb -ErrorAction SilentlyContinue).Source
    if (-not $adb) { throw "adb not found on PATH (Android platform-tools)." }
    & $adb install -r $apk
    if ($LASTEXITCODE -ne 0) { throw "adb install failed. Is the Quest connected and USB debugging allowed?" }
    Write-Host "Installed. On the Quest: Library > Unknown Sources > SolidWorks Quest Viewer."
}

if ($Upload) {
    $cfgFile = "$local\upload.json"
    $cfg = if (Test-Path $cfgFile) { Get-Content $cfgFile -Raw | ConvertFrom-Json } else { $null }
    if (-not $cfg -or -not $cfg.appId -or -not $cfg.appSecret) {
        throw "Put your App ID and App Secret (developer dashboard, API page) into $cfgFile first."
    }
    $util = "$local\ovr-platform-util.exe"
    if (-not (Test-Path $util)) {
        Write-Host "Downloading Meta's ovr-platform-util..."
        Invoke-WebRequest "https://www.oculus.com/download_app/?id=1076686279105243" -OutFile $util -UseBasicParsing
    }
    $channel = if ($cfg.channel) { $cfg.channel } else { "ALPHA" }
    $uploadArgs = @('upload-quest-build', '--app_id', $cfg.appId, '--app_secret', $cfg.appSecret, '--apk', $apk, '--channel', $channel)
    if ($cfg.ageGroup) { $uploadArgs += @('--age-group', $cfg.ageGroup) }
    if ($Notes) { $uploadArgs += @('--notes', $Notes) }
    Write-Host "Uploading version code $env:QV_VERSION_CODE to the $channel channel..."
    & $util @uploadArgs
    if ($LASTEXITCODE -ne 0) { throw "Upload failed (see the messages above)." }
    # The next upload needs a higher version code.
    ([int]$env:QV_VERSION_CODE + 1).ToString() | Out-File $codeFile -Encoding ascii
    Write-Host "Uploaded. Testers on the $channel channel get it in their Quest library."
}
