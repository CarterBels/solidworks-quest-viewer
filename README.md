# SolidWorks Quest Viewer

View a SolidWorks part or assembly at 1:1 scale in mixed reality on a Meta Quest 3, and handle it with your hands.

This project builds on [almostinventor/onshape-quest-viewer](https://github.com/almostinventor/onshape-quest-viewer).
That project's Quest viewer app is reused (as is, or as a lightly modified build in `Viewer/`, see below): it receives
a binary glTF model over the local network. The
upstream project sends from Onshape with a Chrome extension; here a **SolidWorks add-in** takes its place and speaks the
same protocol. Hands, keypad, exploded view, section cut, voice commands and so on all work as described in the
[upstream README](https://github.com/almostinventor/onshape-quest-viewer#use).

> Experimental. Developed and tried with SolidWorks 2025 (Student Edition) on Windows 11 and a **Meta Quest 3S**
> running the upstream v1.1.0 APK. Confirmed working: sending the Sprinkler sample assembly (29 parts) from the
> ribbon button; it appears at 1:1 scale with its colours, about 12 s after clicking over Wi-Fi. Also a 1,700-part FTC
> drive train (simplified to fit), and sending by voice command.

## What you need
- SolidWorks 2025 (other recent versions should work; build with `-p:SolidWorksDir=...` if it is installed elsewhere).
- The [.NET 8 SDK](https://dotnet.microsoft.com/download) to build the add-in (it targets .NET Framework 4.8, which
  Windows already has).
- A Meta Quest 3 with developer mode and hand tracking, on the same network as the PC.
- [Android platform-tools](https://developer.android.com/tools/releases/platform-tools) (`adb`) to install the viewer.
  Nothing else from Meta (no SDK, no Unity) is needed for the upstream prebuilt APK; the modified viewer needs Unity.

## Setup

### 1. Viewer on the Quest
Download `OnshapeQuestViewer-1.1.0.apk` from the
[upstream releases](https://github.com/almostinventor/onshape-quest-viewer/releases), connect the Quest by USB, accept
the USB debugging prompt in the headset, then:
```
adb install -r OnshapeQuestViewer-1.1.0.apk
```
It appears in the Quest library under *Unknown Sources* (named Onshape Quest Viewer).

**Or build the modified viewer** in `Viewer/` (a copy of the upstream Unity project; the changes are in their own
commits). It installs as a separate app, *SolidWorks Quest Viewer*, with its own pairing code, and adds:
- Grabbable parts for big assemblies: every part gets a collider, cooked from at most ~600 points (or its bounding
  box beyond 600 parts), so loading never freezes and there's no 96-part limit.
- A **BOX** keypad button (or *"Marvin, box"*): the model's bounding box is outlined and you can pinch anywhere inside
  it to grab the whole model, or swat it like a solid block. Each part's bounding box is outlined too (they follow
  the parts in the exploded view).
- A **LOCK** keypad button (or *"Marvin, lock"* / *"Marvin, unlock"*): the model stays where it is, so it can't be
  grabbed, pushed or flicked away. Exploded parts can still be pulled out, and RESET still brings it back in front of
  you.
- The model appears with its near side at least 60 cm (2 ft) from your eyes, and only moves back in front of you
  when you ask (RESET, *"Marvin, reset"*, controller A/X), not on an air double-pinch or a resend.
- Exploding a model that isn't locked no longer stutters or drifts off.
- A holo-screen above the keypad with the specs of the part you touch in INSPECT (or last isolated): name,
  sub-assembly, file and configuration, material, size (mm and in), volume, surface area, mass, density, how many
  are in the model, triangles. The add-in sends these with the model (glTF node `extras`); mass and volume come
  through this way even for big assemblies, whose part info doesn't fit the upstream header.
- **ISOLATE** (or *"Marvin, isolate"*; turns on EXPLODE too): a part you pull out of the exploded view stays where
  you let go (in the room, even if you then move the model), or settles onto a table or the floor if you let go
  within 15 cm above it. It can be picked up again; turning ISOLATE off sends them all home.
  **LOCK PART** (*"Marvin, lock part"*) fixes the isolated part on the holo-screen in place; press again to release.
- **MOVE** (*"Marvin, move"*): pinch a part to turn or slide it as its SolidWorks mates allow; everything mated to it
  follows. The add-in works out how the whole assembly can move from its top-level mates. It traces linkages (like a
  four-bar) step by step, so their joints stay together, and stops them where they lock up. Limit mates (distance
  and angle) stop the motion where SolidWorks would. The holo-screen shows the position, or the limit mate's own
  value (*LimitAngle2 15.6° (limits 0 to 25°)*). Parts riding on something that moves (a roller on a swinging hood)
  spin on their own when grabbed and ride along with it. **FIX PART** then holds parts still (pinch to hold or free);
  held parts glow orange in MOVE. If held parts stop a move they flash, and pinching the part again frees them.
  Double-tap FIX PART (or *"Marvin, free all"*) frees everything. Mates to suppressed parts are skipped.
- The keypad: the gear, RESET, INSPECT / CUT, ROTATE, LOCK / EXPLODE / MOVE. Buttons that depend on another one only
  appear once it is on, next to it: ISOLATE after EXPLODE, LOCK PART after ISOLATE, FIX PART after MOVE; ROTATE hides
  while LOCK is on. The display shows the model's name; the address and pairing code are on the settings page's
  CONNECTION screen. **CONTROLS** (*"Marvin, controls"* / *"Marvin, help"*) opens a help screen beside the keypad: while it
  is on, touching a button shows how to use it (what it does, what to say, the hand gesture, the controller button)
  instead of pressing it. With no button touched it has a GESTURES dropdown listing every hand gesture.
- Clapping (EXPLODE) shows a shockwave: flat rings burst out from your hands when it turns on, and rush back in to them
  when it turns off.
- ROTATE by finger: point your index finger straight up (other fingers curled) and draw a flat circle, like
  stirring. Where you start is the top of a flat ring; it fills to wherever your finger is round its centre (within
  15 cm / 6 in), with an arrow with an arrow the way you are going and turns green when ROTATE starts; while it spins the ring is red, and
  a full circle strikes it through and stops it. The gear (*"Marvin,
  settings"*) opens the options: HOLO, STYLE (solid / X-ray), BOX, FIXED, the spawn distance (1-5 ft), the CONNECTION
  screen and a brightness slider (lighting is always shaded, a little darker than upstream).
- *"Marvin, tablet"* (or *"keypad"*) brings the keypad back to where it first appears; RESET still leaves it alone.
- While no model is loaded, a small panel under the starting cube credits the upstream project (also at the bottom of
  the CONTROLS screen).
- Full render resolution (the template rendered at 80%) and bigger button text, for the Quest 3S's coarser screen.
- The section cut frame shows from both sides and no longer pushes the model.
- Marvin shows what he hears with a glow around the edges of your view: blue, bouncing with your voice, once he
  hears his name; a green flash and the command at the top of your view (*Marvin: explode*) when he understood;
  orange when he didn't catch it, right after you stop speaking; red when the command couldn't be done. After his
  name (or a miss) he keeps listening a few seconds, so you can just say the command (*"explode"*). "Marvel" and
  "Martin" count as his name.

It needs Unity 6000.6.5f1 with Android Build Support (installed through Unity Hub). With the Quest connected by USB:
```
powershell -ExecutionPolicy Bypass -File Viewer\build.ps1 -Install
```
**Through the Meta Horizon Store** (a normal library app that updates itself): register the app on the Meta developer
dashboard, add testers to a release channel, put the App ID and App Secret into
`%USERPROFILE%.questviewerPload.json`, then `Vieweruild.ps1 -Upload -Notes "what changed"`. Builds are signed with
the release key in that folder (see the top of `build.ps1`); keep a backup of it. Uninstall an adb-installed copy
before installing from the store (different signature).

The add-in notices this viewer (its `/status` says `manyParts`) and then sends big assemblies part by part.

### 2. SolidWorks add-in
Close SolidWorks, then run:
```
powershell -ExecutionPolicy Bypass -File SolidWorksSender\install.ps1
```
It builds the add-in, copies it to `%LOCALAPPDATA%\SolidWorksQuestSender` and registers it (one UAC prompt: SolidWorks
reads its add-in list from HKEY_LOCAL_MACHINE). Start SolidWorks: a **Quest Viewer** tab appears for parts and
assemblies. If not, enable *Quest Viewer* under **Tools → Add-ins**.

Remove it with `install.ps1 -Uninstall`.

### 3. Pair
Start the viewer on the Quest. Its keypad shows an **address** and a 6-character **pairing code** (with the modified viewer: on the gear's
settings page, CONNECTION). In
SolidWorks click **Quest Viewer → Quest settings**, enter both, **Save**, **Test connection**.

The settings live in `%APPDATA%\SolidWorksQuestSender\settings.json` and can also be edited by hand; changes apply
immediately, without restarting SolidWorks:
```json
{
  "viewer": "192.168.1.50:8723",
  "pairCode": "ABC123",
  "listenForVoice": true
}
```
The Quest gets its IP address from your router, so it can change (the keypad always shows the current one). To keep
it fixed, reserve an address for the Quest in your router's DHCP settings. The pairing code only changes if the viewer
is reinstalled. For the PC version of the viewer (Quest Link) use `127.0.0.1:8723`.

## Use
Open a part or assembly and click **Send to Quest**. The model appears in front of you; send again after an edit and
the new version replaces the old one where you left it. If nothing changed, the viewer just says *Up to date*
(**Send again** forces a resend). Or say *"Marvin, send this to the Quest"* in the headset.

What is sent:
- Visible solid bodies of the active part, or of every visible, resolved component of the assembly, in their
  assembled positions. Hidden, suppressed and envelope components are skipped. Lightweight components are resolved
  first: the Send button asks (answer No to send without them), a voice command resolves them without asking.
- Colours: assembly component appearance → face → feature → body → part.
- Each body becomes one part in the viewer (for INSPECT and the exploded view), named after the component. Volume is
  always sent; mass too when the part has a material.

Big models are simplified to fit the headset: repeated parts (screws, wheels, brackets) are sent once and reused,
and if the triangles drawn (counting every copy) exceed `maxTriangles` in `settings.json` (default 1,500,000), meshes
are simplified with a growing tolerance (0.05 mm, 0.1 mm, …) until they fit. A 1,700-part FTC robot went from
19.4 M triangles / 140 MB to 1.05 M triangles / 11 MB at 0.2 mm, in about 5 s. The log shows the tolerance used.

Status shows on the **Q** icon in the notification area (grey idle, yellow sending, green sent, red failed; hover
for details), and in `%APPDATA%\SolidWorksQuestSender\log.txt`.

## How it works
| Piece | Role |
|---|---|
| `SolidWorksSender/SolidWorksExporter.cs` | Tessellates visible bodies via the SolidWorks API (`Face2.GetTessTriangles` / `GetTessNorms`), applies component transforms, gathers colours and volume/mass |
| `SolidWorksSender/Decimator.cs` | Quadric-error mesh simplification to fit the triangle budget |
| `SolidWorksSender/Grouping.cs` | Merges big assemblies into at most 96 sub-assembly parts, so each gets a grabbable collider |
| `SolidWorksSender/GlbWriter.cs` | Writes glTF 2.0 binary; nodes carry the `PTC_onshape_metadata` id extension the viewer uses to match parts with mass info |
| `SolidWorksSender/ViewerClient.cs` | The viewer protocol: `GET /status`, `POST /notify`, `POST /model`, `GET /commands` (voice long poll) |
| `SolidWorksSender/QuestAddin.cs` | Add-in: ribbon tab, tray icon, voice polling, COM registration |
| `tools/mock-viewer.js` | Stand-in viewer for testing without a headset (`node tools/mock-viewer.js TEST42`) |
| `tools/AddinHarness` | Drives the add-in from outside SolidWorks for testing (`export`, `send`, `dev`, `local`) |

Exporting the 29-part, 173k-triangle Sprinkler sample takes about 0.3 s from the ribbon button. (Driven from another
process through COM, as the harness does, the same export takes ~45 s: SolidWorks handles API calls far more slowly
while it is servicing an external call. Use the harness for correctness, not timing.)

## Web page and releases
`docs/index.html` is the project's GitHub Pages site (install guide, features, how to use). Its download buttons
point at the latest GitHub release, which needs two files with fixed names; `toolsmake-release.ps1` makes them in
`release`: `SolidWorksQuestViewer.apk` (signed with the release key) and `SolidWorksQuestSender.zip` (the prebuilt
add-in with `install.ps1`; SolidWorks' own API assemblies are taken from the user's SolidWorks, not shipped).

## Keeping up with upstream
`upstream/` is a clone of the upstream repository (ignored by this repository). `git -C upstream pull` gets new
viewer versions; check its releases for a newer APK.

## Known limitations
- With the upstream viewer (not the modified one above): it gives each part its own collider (so hands can grab it)
  only up to 96 parts. Bigger assemblies are sent
  as up to 96 groups: the add-in starts from the top-level components and keeps opening the biggest sub-assemblies
  while the count fits, merging the bodies of each group into one part. INSPECT and the exploded view then work per
  group, and the file is larger (merged groups can't reuse meshes). But the viewer builds those colliders on its main
  thread, in time that grows with the square of a part's vertex count (20k vertices 0.3 s, 200k 39 s on a Quest 3S),
  so grouping is skipped when it would take over ~8 s (the log says so). The 1,700-part FTC robot is too detailed for
  that and still gets the single box.
- MOVE uses the top-level mates only (a sub-assembly moves as one piece), and supports concentric, coincident,
  distance, parallel, perpendicular, angle, tangent, width, hinge, slider and lock mates plus distance/angle limits.
  Gear, belt, cam and other mechanical mates are ignored, so parts they would drive don't follow. Parts that can move
  in several independent ways at once (a ball joint, a part free in a plane) can't be moved by hand yet.
- A limit angle or distance has no sign in SolidWorks. The add-in learns which way is positive from a send where the
  mate isn't at 0 (it remembers that per mate in `%APPDATA%\SolidWorksQuestSender\limit-signs.txt`). Until then, a
  part sitting exactly at 0 can be moved past it.
- Textures and decals aren't sent; only colours and transparency.
- The model goes over your local network unencrypted (as upstream).

## License
MIT, as upstream. The add-in was written by an AI coding assistant (Claude); use at your own risk.
