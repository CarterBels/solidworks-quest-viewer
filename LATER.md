# Later: planned viewer changes

Planned, not built yet. Each item says what it does and where the change goes.

## 1. "Marvin, tablet": bring the keypad back (built)

**What it does:** you say *"Marvin, tablet"* and the keypad comes back to where it first appears: low and a little
to the left of where you're looking, tilted toward you. You don't have to go and find it.

**How:**
- **Voice:** `Viewer/Assets/QuestViewer/Scripts/VoiceCommands.cs` gets a new command `Tablet`. Its phrases are
  "marvin tablet", "marvin keypad", "marvin bring the tablet" and "marvin where's the tablet". Saying just "tablet"
  right after his name also works, like the other commands.
- **Moving the keypad:**
  - Handling the command calls `Respawn()` on the keypad's `SpawnInFront` (`SpawnInFront.cs:69`), after letting go
    of any grab, so it glides back with the same easing a RESET uses for the model.
  - The keypad has `recallable = false`, so RESET and controller A/X keep leaving it alone. Only this command moves
    it.
- **Where it lands:** the build script (`Viewer/Assets/QuestViewer/Editor/QuestViewerSetup.cs:341`) sets the spot:
  0.33 m out, 0.30 m below the eyes, 0.2 m to the side, tilted 40°. Check which way `sideways = -0.2` goes in the
  headset. "Off to the left and slightly down" may need the sign flipped, or a little less drop.
- **Help and docs:** add a card to the CONTROLS screen (`ControlsPanel.cs`), a line to the GESTURES and voice
  lists, and a line in the README.

## 2. Start it like a normal app (on hold)

**Status (2026-11-16):** ready but not used yet. The app is registered on the developer dashboard; the release key
and `build.ps1 -Upload` are set up (see the README). Still to do when you want it: App ID and secret into
`%USERPROFILE%.questviewerPload.json`, a tester on the channel, the first upload, then reinstall from the library.

**The problem:** sideloaded apps only show under *App Library → (dropdown) Unknown Sources*, and the headset doesn't
offer to pin them.

**Options, quickest first:**

1. **Drag it to the dock.** Some users report you can drag the app's icon from the Unknown Sources list onto the
   Universal Menu (the bar at the bottom). You may have to unpin one of the apps already there to make room. Meta
   has also been testing keeping sideloaded apps pinned there. Try this first; it needs no code.
2. **Install it from the Meta Horizon Store on a private release channel.** This is the proper way to get a normal
   library app: a regular icon, pinnable, and it updates itself.
   - **Setup:** register the app in the Meta Horizon developer dashboard, under the developer organisation already
     used for developer mode. Then upload the APK to a private release channel and invite your own account (and
     anyone you want to share it with).
   - **Code:**
     - The build already supports a release keystore: `QuestViewerSetup.cs` reads `QV_KEYSTORE`, `QV_KEYSTORE_PASS`,
       `QV_KEY_ALIAS` and `QV_KEY_PASS`.
     - `build.ps1` would get an `-Upload` option that runs Meta's `ovr-platform-util` to push the signed APK to the
       channel.
     - The version code has to go up with every upload.
   - **Trade-off:** it takes an account step on Meta's website, and the store's automatic checks may ask for small
     manifest changes.
3. **Not possible:** an app can't add itself to the dock. The headset doesn't let apps do that.

## 3. Credit the original project in the headset (built)

**What it does:** whenever no model is loaded, a small holo-panel under the starting cube says:

> Based on **Onshape Quest Viewer** by almostinventor
> github.com/almostinventor/onshape-quest-viewer · MIT License

**How:**
- **The panel:** a new `CreditsPanel` in `Viewer/Assets/QuestViewer/Scripts/`, using the same `HoloPanel` shader as
  the specs screen. It sits under the starting cube (the cube that `ModelHost.placeholder` hides when a model
  arrives), so it is visible at start-up and while waiting for SolidWorks.
- **Also on the CONTROLS screen:** the same line at the bottom of its general page, so the credit can be found
  later too.
- **No link button:** the headset can't open a link from the app without leaving it, so the address is shown as
  text.
- **In the build script:** `QuestViewerSetup.cs` adds the panel next to the starting cube.
