# Development and compatibility

## Scope

Windows x64 mod for a legitimately owned Steam installation of **4Wheel Challenge (Wheelchair Simulator)**, app **3504700**. The public package contains the mod and launcher, not the game, game assets, generated game assemblies, saves or logs. It does not bypass Steam ownership or authentication.

The launcher discovers the Steam install or asks for the game executable. It creates its own copy under `%LOCALAPPDATA%\4WheelLocalSplitScreenMod\Game`, installs the loader there and starts that copy with `-force-d3d11 -localcoop-profile`. It does not install an injection DLL into the Steam original. Normal Steam launches remain vanilla. Close one version before starting the other.

The launcher downloads the pinned official loader on first use and checks its SHA-256. The game and loader need additional disk space and first-time setup may take several minutes. The mod profile is separate at `Game\BepInEx\local-profile` under the cache directory.

## Compatibility baseline

| Component | Version |
| --- | --- |
| Mod | 0.3.1 |
| Steam game build used during development | 25192656 |
| Unity | 6000.0.80f1, IL2CPP |
| BepInEx | 6.0.0-be.791+b782da4, Unity IL2CPP win-x64 |
| C# target | net6.0 |
| SDK used to build | .NET SDK 8.0.426 |

Only the user's own installed game is copied. Compatibility with future game updates is not guaranteed. Public source refers to game APIs but includes no decompiled game implementation or game interop binaries.

## Building

Install a .NET SDK capable of targeting net6.0. Start the packaged mod once to generate BepInEx interop assemblies, then close the game. Run from the repository root:

```powershell
.\scripts\Build-Mod.ps1
```

Use `-GameDirectory` to select a different modded copy that already has `BepInEx\core` and `BepInEx\interop`. `-DotnetPath` selects a non-default SDK executable. The build emits `payload\WheelchairLocalCoop.dll`; it does not ship referenced game or loader assemblies. Release debug symbols are disabled so the binary does not embed the developer's local PDB path.

## Behavior

The host creates a private online room using the game's normal UI. The mod adds local actors after level load and assigns independent Xbox input, cameras and weapon HUDs. Four fixed viewports are used; three-player mode leaves the bottom-right view unused. Global menus remain shared. F7 switches three/four players, F8 toggles local multiplayer, and F9 toggles the status panel. Controller slots follow the devices detected by the game.

C4 keeps its native arc and proximity detonation. An owner must leave the trigger area before approaching it again. There is no separate remote-detonation command.

The 0.3.1 movement guard clears destroyed cached wheel contacts before native platform-velocity calculation. Valid contacts still use the original physics. The lobby fix selects Play when the host first switches from mouse to controller with no valid selection; existing selection and modal state remain intact.

## Validation limits

Earlier local play confirmed multiple controllers, four viewports, respawn and basic weapon use. Development checks for 0.3.1 reproduced destroyed wheel contacts, compared valid moving/rotating contact calculations, checked lobby focus and exercised four virtual controllers for six minutes. **Actual gameplay riding moving platforms was not tested.** Only Up and all maps have not been comprehensively tested. These results are limited checks, not a claim of bug-free gameplay.

This packaging pass checks build output, launch-script parsing, source selection and archive privacy. It does not add further gameplay tests. Diagnostic helpers are inactive unless explicitly launched with `-localcoop-test`; the normal launcher never sets it. Diagnostic files, if generated locally, can contain runtime paths or session details and should be reviewed before sharing.
