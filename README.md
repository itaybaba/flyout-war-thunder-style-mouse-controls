# War Thunder Style Mouse Controls v1

Mouse-following flight controls for **Flyout**, with an aim circle, a nose marker, free look, and an Instructor that steers your aircraft toward the mouse target.

[Download v1.0.0](https://github.com/itaybaba/flyout-war-thunder-style-mouse-controls/releases/tag/v1.0.0) · [Report a problem](https://github.com/itaybaba/flyout-war-thunder-style-mouse-controls/issues)

## Features

- Mouse aiming with a smooth sensitivity curve for small corrections and large turns.
- Aircraft-specific AoA estimates from wing geometry, adjusted when separation is observed.
- Normal pitch and roll controls retain Flyout aircraft differences: a heavy or underpowered design still responds differently from a maneuverable one.
- Mild artificial **yaw-only** stabilization reduces sideways wobble. Enabled at 65% strength; adjustable or removable.
- Manual pitch, roll, and yaw inputs override their respective axes.
- A controls guide appears for six seconds at the start of each flight. F12 reopens it at any time or closes it early.

## Requirements

- Windows x64, Flyout. Tested game build: **0.2409**, Unity 2022.3.62f3.
- **MelonLoader 0.7.3**, installed for Flyout. Get the loader from [LavaGang's official releases](https://github.com/LavaGang/MelonLoader/releases/tag/v0.7.3); follow its [official installation guide](https://github.com/LavaGang/MelonWiki/blob/master/docs/gettingstarted.md), including its .NET runtime requirements.
- Single-player fixed-wing aircraft. Other game builds, helicopters, and multiplayer have not been validated.

No separate robotics mod or MCP bridge is required to use these controls.

## Optional automatic installer

Download **(optional)-War-Thunder-Style-Mouse-Controls-v1-Installer.exe** from the Releases page if you prefer automatic folder detection. This is **optional**; the manual ZIP works too.

Close Flyout, run the installer, and click **Install mod**. It finds Flyout through Steam's registry entry and library folders, including libraries on other drives. If several installations are found, choose the one you use. If detection fails, use **Browse** to select the folder containing `Flyout.exe`.

The installer contains the mod DLL; it does not need to download it. It verifies the selected game folder and requires an existing MelonLoader installation. It does not install Steam, Flyout, or MelonLoader. Install MelonLoader 0.7.3 first using the official guide above. Existing settings are preserved, and an old mod DLL is backed up under `<Flyout>/UserData/FlyoutMouseAim-Backups/`. It refuses installation while Flyout is running. An administrator launch may be needed if your game folder is protected.

The executable is unsigned and uses Windows' .NET Framework 4.x. You can always use the ZIP instead.

## Manual installation

1. Close Flyout.
2. In Steam, open Flyout → Manage → Browse local files. This is the folder containing `Flyout.exe`.
3. Install MelonLoader if you do not already use it. Launch the game once to finish its setup, then close it.
4. Download **War-Thunder-Style-Mouse-Controls-v1.zip** from this repository's Releases page. Extract its `Mods` folder into the Flyout folder, so the file is `<Flyout>/Mods/FlyoutMouseAim.dll`.
5. If upgrading, replace the existing `FlyoutMouseAim.dll`. Keep only one copy: do not rename an old DLL and leave it inside `Mods`.
6. Start Flyout, launch your aircraft, and get airborne. Press **F2** to enter mouse flight.

The DLL filename stays `FlyoutMouseAim.dll` for compatibility with earlier development versions. MelonLoader displays the new mod name and version 1.0.0. Existing settings are preserved.

## Controls

| Key / input | Action |
|---|---|
| F2 | Enter or leave Flyout mouse flight |
| Mouse | Move the desired flight direction / aim circle |
| Left Alt, held | Free look without moving the flight target |
| Mouse wheel | Zoom |
| Normal pitch / roll / yaw keys | Manually override the corresponding axis |
| F7 | Enable/disable this mod's mouse-flight assistance |
| F6 | Show/hide the status HUD |
| F9 | Release this mod immediately; F2 also exits native mouse flight |
| F12 | Show/hide the controls guide |

The circle is where you want to fly; the cross is where the nose currently points. Release manual inputs to blend back into assisted flight. Help opened manually stays visible until closed. F2/F7 toggles do not restart the six-second startup popup.

## Settings

After the first launch, edit `<Flyout>/UserData/FlyoutMouseAim.json` **with the game closed**. Back it up first.

| Setting | Default | Purpose |
|---|---:|---|
| MouseSensitivity | 0.09 | Full-response mouse sensitivity |
| PrecisionSensitivity | 0.4 | Slow-movement sensitivity multiplier |
| FullSensitivitySpeed | 1000 | Mouse counts/second for full response |
| MouseSmoothing | 0.025 | Mouse filter time, seconds |
| ArtificialYawStability | true | Enable the artificial yaw damper |
| YawStabilityStrength | 0.65 | Yaw assist strength, 0 to 1 |
| YawStabilityMaxAcceleration | 0.5 | Yaw acceleration cap before strength/fade, rad/s² |
| ShowHud | true | Show the status panel |
| AdaptiveAoA | true | Estimate positive and negative AoA limits |
| AoAMarginDeg | 2 | Margin below the estimated lift peak |

Mouse and yaw settings are top-level fields. AoA settings are inside `Instructor`. For finer aim, reduce `PrecisionSensitivity`. To disable artificial yaw, set `ArtificialYawStability` to `false`.

The optional Python diagnostic client is in the source repository (`py tools/mouseaim.py status`). It is not needed to play.

## Flight behavior and compatibility

This is an independent implementation inspired by War Thunder-style mouse aiming. It does not contain War Thunder code or promise identical handling. It is not affiliated with Gaijin or Stonext Games.

Pitch and roll are commanded through existing aircraft controls. The yaw assistance directly adds a bounded angular-rate correction along the aircraft's body yaw axis; natural aerodynamic coupling can still affect other axes afterward. Stall, energy loss, structural behavior, and differences between aircraft remain relevant. AoA and load limits are estimates, not guarantees against stalls, crashes, or damage.

During mouse flight the mod temporarily bypasses Flyout's native FBW processing to avoid competing controllers, then restores the saved FBW flag after each control update. `BypassNativeFbw: false` opts out. Native autopilot and explicit MCP control leases take priority. Artificial yaw yields to manual rudder and fades at low speed or high AoA/sideslip. Pause, loss of focus, and input locks suspend normal assistance.

Other mods that replace the same camera, mouse-flight controller, or angular velocity may conflict. Compatibility with every aircraft or control mod is not established. The mod does not edit saved craft files.

## Troubleshooting and uninstall

- No assistance: check that F2 mouse flight is active, then F7. Check MelonLoader's log for `War Thunder Style Mouse Controls` version `1.0.0`.
- Too twitchy: lower `PrecisionSensitivity` or `MouseSensitivity` in the settings.
- Unexpected behavior: press F9 to release the mod, then F2 to leave native mouse flight.
- Missing loader / failed startup: check the loader installation and runtime requirements using the official guide linked above.
- To report a bug, include your game version, aircraft description, reproduction steps, and relevant excerpts from `<Flyout>/MelonLoader/Latest.log`. Review logs for personal information before sharing.
- To uninstall, close the game and remove `Mods/FlyoutMouseAim.dll`. Optionally remove `UserData/FlyoutMouseAim.json` to reset preferences.

## Validation

Release build: zero warnings/errors. All **99 offline assertions** pass. Earlier live baseline checks covered F-42, M4, Gryphon, and Radial-MK2. Paired yaw-assist tests on t7 improved fine aiming and recovery after a rudder pulse. These are a limited sample of aircraft, not universal compatibility testing. The six-second help feature has been build-checked; live timer verification is pending. See [validation details](docs/VALIDATION.md).

## Building from source

Install the .NET SDK with net6.0 support. Install and launch Flyout with MelonLoader once to generate its local interop assemblies. Then, from the repository root:

```powershell
dotnet build FlyoutMouseAim.csproj -c Release -p:FlyoutDir="C:/path/to/Flyout"
dotnet run -c Release --project tests/SimTests.csproj
```

The build uses your locally installed game/loader assemblies; those dependencies are not redistributed. Output: `bin/Release/net6.0/FlyoutMouseAim.dll`.

## Building the optional installer

On Windows, first build the mod in Release mode, then run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File installer/build.ps1
```

Output: `bin/installer/(optional)-War-Thunder-Style-Mouse-Controls-v1-Installer.exe`. The build embeds your compiled mod DLL and uses the Windows .NET Framework compiler. Run the executable with `--self-test` for isolated fixture checks, or `--detect-only` to write detected game folders without installing anything. These diagnostic modes write their report beside the executable.

## Credits and license

Created by **itaybaba**. Developed with AI assistance, including OpenAI Codex (GPT-6 family); the earlier prototype credited Claude Code. No generated art or audio is included.

Thanks to LavaGang for MelonLoader, the Harmony project for patching support, and the Il2CppInterop contributors. These dependencies retain their own licenses and are installed separately.

Original mod code: [MIT license](LICENSE.txt). Flyout and War Thunder names belong to their respective owners.
