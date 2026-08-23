# Shapez2Multiplayer

Multiplayer mod for [Shapez 2](https://store.steampowered.com/app/2162800)

This checkout is a local synchronization fork of Bknibb's v1.1.9 release. It
keeps research spending, operator levels/jobs, vortex delivery totals, and the
left-hand pinned-shape UI authoritative to the host. Both the host and every
connecting player must use this exact build; its network protocol is not
compatible with the original v1.1.9 DLL.

**Expect bugs, this has not been tested with a full playthrough yet**

Instructions:\
Host - in the pause menu press host and optionally invite your friends using steam\
Client (Steam) - Join your friend in the multiplayer menu, in the steam overlay or accept an invite\
Client (Direct Connect) - Enter the host's ip in the multiplayer menu and press direct connect

Supports Direct Connect and Steam Networking

While connected, open the pause menu and press `JUMP` beside a player to center
the map on their latest cursor position. The queued jump is retained while the
menu is open and applied again as gameplay resumes.

Currently does not sync belt, pipe and logic states, however all buildings and research systems are synced so the game should naturally stay mostly synced

Please report any issues you find

ENet libraries compiled from https://github.com/nxrighthere/ENet-CSharp

## Build and install (Windows / Steam)

1. Disable or unsubscribe from the Workshop version of Shapez2Multiplayer on
   both PCs. Do not leave both copies enabled: they share the same entry point
   and Harmony patch ID.
2. Keep the Workshop dependencies installed and enabled: HarmonyX, Steamworks
   Platform Resolver, and Shapez2UILib.
3. Add `--set-modding-env-vars` to the shapez 2 Steam launch options, start the
   game once, close it, then restart the terminal or IDE. This populates
   `SPZ2_PATH` (the game's `shapez 2_Data/Managed` directory) and
   `SPZ2_PERSISTENT` (the game's persistent-data directory).
4. Install a current .NET SDK and confirm `dotnet --info` works.
5. Locate `Shapez2UILib.dll`. It will either be under
   `%SPZ2_PERSISTENT%\mods\Shapez2UILib`, or in Steam's Workshop content folder
   for item `3735218203`.
6. From this repository, run the included PowerShell build script. Its default
   Workshop path matches a Steam library on `F:`:

   ```powershell
   .\Build.ps1
   ```

   For another Steam library, provide either the Workshop item directory or
   the DLL directly:

   ```powershell
   .\Build.ps1 -WorkshopPath "C:\Program Files (x86)\Steam\steamapps\workshop\content\2162800\3735218203"
   .\Build.ps1 -UiLibPath "C:\full\path\to\Shapez2UILib.dll"
   ```

   Add `-NoPause` when running the script from an existing terminal or an
   automated build.

   The project writes the complete local mod directly to
   `%SPZ2_PERSISTENT%\mods\Shapez2Multiplayer`.
7. Start shapez 2, open the Mods screen, enable the local
   Shapez2Multiplayer v1.2.0-local.7 entry and its dependencies, then restart
   when prompted. Repeat the same source/build steps on the other PC, or copy
   the completed `%SPZ2_PERSISTENT%\mods\Shapez2Multiplayer` folder to the same
   location on that PC.

The default persistent-data paths are:

- Windows: `%USERPROFILE%\AppData\LocalLow\tobspr Games\shapez 2`
- Linux: `~/.config/unity3d/tobspr Games/shapez 2`
- macOS: `~/Library/Application Support/tobspr Games/shapez 2`

Back up the save before the first multiplayer test. The mod declares that it
does not alter save data, but multiplayer code can still leave the host save in
an unintended gameplay state if a session is interrupted during an action.
