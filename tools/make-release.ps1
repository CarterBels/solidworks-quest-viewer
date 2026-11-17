# Packages the two downloads for a GitHub release (into release\, which git ignores):
#   SolidWorksQuestViewer.apk    the Quest app
#   SolidWorksQuestSender.zip    the SolidWorks add-in: the prebuilt DLL, install.ps1, INSTALL.txt, and the three
#                                SolidWorks API assemblies it was built against (from SolidWorks' api\redist folder, which
#                                SolidWorks provides for shipping with add-ins), so it loads with any SolidWorks version.
# The names stay the same for every release, so .../releases/latest/download/<name> always gets the newest.
#   powershell -ExecutionPolicy Bypass -File tools\make-release.ps1             build both
#   powershell -ExecutionPolicy Bypass -File tools\make-release.ps1 -SkipViewer reuse the last viewer build
param([switch]$SkipViewer)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$out = "$root\release"
New-Item -ItemType Directory -Force $out | Out-Null

# The add-in.
& dotnet build "$root\SolidWorksSender\SolidWorksSender.csproj" -c Release -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Add-in build failed.' }
$stage = "$out\addin"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory $stage | Out-Null
Copy-Item "$root\SolidWorksSender\bin\Release\net48\SolidWorksQuestSender.dll" $stage
Copy-Item "$root\SolidWorksSender\install.ps1" $stage
foreach ($n in 'sldworks', 'swconst', 'swpublished') {
    Copy-Item "$root\SolidWorksSender\bin\Release\net48\SolidWorks.Interop.$n.dll" $stage
}
@"
SolidWorks Quest Sender (add-in for SolidWorks 2025 and other recent versions)

1. Unzip this folder (don't run it from inside the zip), and close SolidWorks.
2. Right-click install.ps1 > Run with PowerShell (or in PowerShell, in this folder:
     powershell -ExecutionPolicy Bypass -File install.ps1 )
   Click Yes when Windows asks for administrator rights (SolidWorks reads its add-in list from there).
   The window says whether it worked, and why if it didn't.
3. Start SolidWorks: a "Quest Viewer" tab appears. If not: Tools > Add-ins > tick Quest Viewer.

Remove it with: powershell -ExecutionPolicy Bypass -File install.ps1 -Uninstall
Full instructions: see the project's web page.
"@ | Out-File "$stage\INSTALL.txt" -Encoding ascii
$zip = "$out\SolidWorksQuestSender.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive "$stage\*" $zip
Remove-Item $stage -Recurse -Force
Write-Host "Add-in: $zip"

# The viewer.
if (-not $SkipViewer) { & "$root\Viewer\build.ps1" -Release }
$apk = "$root\Viewer\Builds\Android\SolidWorksQuestViewer.apk"
if (-not (Test-Path $apk)) { throw "No viewer APK ($apk)." }
Copy-Item $apk "$out\SolidWorksQuestViewer.apk" -Force
Write-Host "Viewer: $out\SolidWorksQuestViewer.apk"
