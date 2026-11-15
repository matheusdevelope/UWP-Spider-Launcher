# SSD in-place: play from an external drive on Xbox

This document describes how the combined launcher (from **0.6.0.58**) keeps the heavy game files on an SSD/USB at `E:` and still lets the native host open the menu and start a session. It is not a directory junction and it is not a copy onto internal storage.

Validated on Xbox Developer Mode with a Steam **4.0630** dump at `E:\dumps\SpiderMan`: Prepare finished, the menu opened, New Game loaded, and the game was playable. That records the path that worked. It does not claim full-title stability.

## Goal

The native host (`SpiderManNativeStartup.exe`) always looks for files under:

`D:\DevelopmentFiles\LooseApps\SpiderManRuntime-20261008\StartupContent\asset_archive\`

The large files (`dag`, `g00s000`…`g00s033`, voice packs, and so on) are tens of gigabytes. Copying them onto `D:` would make the SSD pointless: internal storage would hold the same copy the external drive already has.

The split is:

- **SSD (`E:`)** — the user's original dump (`Spider-Man.exe`, complete `asset_archive`).
- **Internal (`D:` LooseApps)** — prepared host, derived module, and small overlays (patched toc, map travel, shaders).
- **Package (`S:\Program Files\WindowsApps\...`)** — launcher plus bundled runtime. The game process also requests files through this path.

## Three locations, one game

| Location | Example | What lives there |
| --- | --- | --- |
| SSD | `E:\dumps\SpiderMan\asset_archive\` | Original heavy archives. They are not copied. |
| LooseApps | `D:\DevelopmentFiles\LooseApps\SpiderManRuntime-20261008\` | Writable runtime. `archive-link.txt` points at the SSD. `StartupContent\asset_archive\` holds overlays only. |
| Package | `S:\Program Files\WindowsApps\Local.UwpSpiderCombinedPrototype_*\StartupContent\` | Combined-package binaries. It does **not** contain `dag` / `g00s*`. |

Marker written during Prepare:

```
D:\DevelopmentFiles\LooseApps\SpiderManRuntime-20261008\archive-link.txt
```

contents (one line):

```
E:\dumps\SpiderMan\asset_archive
```

## Why ordinary file APIs fail on Xbox

The process runs in an **AppContainer** (UWP / packaged). On a removable volume that changes what works:

| Attempt | Result on Xbox |
| --- | --- |
| `System.IO` (`File.OpenRead`, `Directory.Exists`, `File.WriteAllText`) on `E:` | Access denied. |
| `GetDiskFreeSpaceExW` on `E:` | Fails / error 5. |
| `CreateFile2FromAppW` on `.exe` files and some other files on `E:` | `ERROR_ACCESS_DENIED` (5). |
| `CreateSymbolicLinkFromAppW` targeting `E:` | Error 5 (it opens the removable target). |
| Junction / reparse from `D:` to `E:` created inside the AppContainer | Xbox blocks it. |
| Copy `asset_archive` onto `D:` | Works, but occupies internal storage. Rejected for that reason. |

What **does** work in the **launcher** (C#, StorageFolder broker):

- `KnownFolders.RemovableDevices`
- `StorageApplicationPermissions.FutureAccessList` (token `game-storage`)
- `GetFileAsync` / `CreateFileAsync` / `CreateFolderAsync`
- `System.FreeSpace`
- The folder chosen in the picker (**Use selected folder**)

That is enough for **Find files / Prepare**. It is not enough for **Play**: the native host does not use StorageFolder. It calls `CreateFile2` / `CreateFileW` on `D:` or `S:` paths.

The gow3 Xbox pattern opens `eboot.bin` through FromApp plus a file-type association. This dump is a Steam `.exe` with `g00s*` files and no FTA. The launcher therefore uses the broker, and the host needs a native hook.

## Two layers

### 1. Launcher (`GameStorage`, `UsbFolderSource`, `PreparationEngine`)

All Xbox I/O on a removable volume goes through the broker. `NeedsBroker` must **not** remap `D:` paths onto the SSD: overlays (`toc`, generated files) have to be created on internal storage.

`PlaceGameArchives`:

1. Tries a junction `D:\...\asset_archive` → SSD. That fails in the AppContainer.
2. Does not copy the heavy files.
3. Creates the overlay directory on `D:` (empty, or overlays only).
4. Writes `archive-link.txt`.

Prepare also:

- Treats `E:` as a volume root (`E:\`), not as `GetFullPath("E:")` relative to the cwd (otherwise `E:` became `asset_archive` and the next file looked like an unexposed folder).
- If `import-receipt.txt` exists and sizes still match, it does **not** SHA-256 the 37 large archives again (`import-archive-check.txt`).

Code: `source/GameStorage.cs`, `source/UsbFolderSource.cs`, `source/PreparationEngine.cs`.

### 2. Native host (wrappers `SpArgs40.dll` and `SpShell.dll`)

The original host does not know an SSD exists. The wrappers intercept file opens **before** the required-asset check and **during** gameplay.

In the combined package:

| Path in the package / LooseApps | Source |
| --- | --- |
| `StartupContent\SpArgs40.dll` | Our wrapper (`native/SpShellBridge.cpp`) |
| `StartupContent\SpArgs40Core.dll` | Original `SpArgs40` (Xbox kernel32 shim) |
| `StartupContent\SpShell.dll` | Our wrapper |
| `StartupContent\SpShellCore.dll` | Original `SpShell` (Documents adapter) |

`CombinedRuntime.VerifyPayload` checks the hash of the **Core** (original inventory) and only requires the wrapper to exist. On Play, `DeployStorageAdapters` copies the four DLLs into LooseApps, because the game cwd is `D:\...\StartupContent` and `LoadLibrary("SpArgs40.dll")` can pick up a local copy.

Code: `native/SpShellBridge.cpp`, `native/SpArgs40Bridge.def`, `native/SpArgs40Forwards.cpp`, `source/CombinedRuntime.cs`, `source/SpiderManInstaller.csproj`.

## Why the hook must live in SpArgs40, not only SpShell

Actual host order (`native-startup.log`, runtime 0.4.1.6):

1. Preload `vcruntime140`, `msvcp140`, `UxTheme`, **`SpArgs40.dll`**.
2. `cwd = D:\...\StartupContent`.
3. Opens `asset_archive\toc`, `dag`, `g00s000`, `g00s019` (16-byte header probe only).
4. If any of those is missing: `game_startup_blocked=missing_required_assets` and the process exits.
5. Only then `InitializeStorageAdapter` from SpShell.

Hooking `CreateFile2` from SpShell Init is too late: it runs after step 3. Logs from that attempt showed a 16-byte `toc` overlay on `D:` and `asset_open_error=2` on `dag` / `g00s000` / `g00s019`. Error 2 means the file was not found on a `CreateFile2` call **without** the hook. If the hook had run and the SSD open had failed, the error would have been 5.

`SpArgs40` exports about 288 names (many KERNEL32 forwards, plus its own `CreateFileW`). The host imports `CreateFile2` from kernel32. The wrapper:

- In `DllMain`, resolves the real kernel32 pointers and **patches the exe IAT** (`CreateFile2`, `CreateFileW`, attribute APIs, `GetProcAddress`, `LoadLibraryExW`, `LoadPackagedLibrary`).
- Re-exports the other 283 names to `SpArgs40Core` (`/export:...=SpArgs40Core....` pragmas).
- Replaces `CreateFileA` / `CreateFileW` / `GetFileAttributes*` with the hooks.

`.def` forwarders of the form `= SpArgs40Core.X` did not link for every symbol in an AppContainer DLL; the generated table in `SpArgs40Forwards.cpp` is what the linker accepted.

SpShell stays in the package: `InitializeStorageAdapter` still needs the original Core (GameDocuments, preference seed). The extra hook in Init covers DLLs loaded later.

## Path resolution

For a request under `\StartupContent\` (except `.dll` / `.exe`):

1. If the requested path **is already a file** (overlay on `D:`), do not remap.
2. Otherwise try the same relative path under LooseApps:  
   `D:\...\StartupContent\<rest>`  
   (patched toc, `local_map_travel_v1.dsar`, `first-launch-recipes-v1`, and so on).
3. If it is `asset_archive\...` and no overlay exists, read `archive-link.txt` and open on the SSD.
4. Writes **never** go to the SSD: new overlays stay on `D:`.
5. SSD opens use `CreateFile2FromAppW`, then `IStorageItemHandleAccess` on `KnownFolders.RemovableDevices` (GUID `5CA296B2-2C25-4D22-B785-B885C8201E6A`) if FromApp fails.
6. Overlays on `D:` / `C:` / `Q:` / `S:` use ordinary `CreateFile2`, not FromApp.

`archive-link.txt` is searched at the runtime root derived from the requested path, at the cwd, and at the known LooseApps location. The combined host requests files under `S:\WindowsApps\...\StartupContent\asset_archive\`; that location **does not** contain the marker. Without the LooseApps fallback, the menu could open (`g00s*` on the SSD) and New Game would then exit (overlays exist only on `D:`).

Pseudocode:

```
request = S:\WindowsApps\...\StartupContent\asset_archive\g00s000
  → missing on S:
  → no overlay on D:
  → E:\dumps\SpiderMan\asset_archive\g00s000   (FromApp/broker)

request = S:\WindowsApps\...\StartupContent\asset_archive\toc
  → overlay D:\...\StartupContent\asset_archive\toc   (generated toc)

request = D:\...\StartupContent\asset_archive\toc
  → already a local file, do not remap
```

## Problems and rejected approaches

### Prepare (launcher)

| Symptom | Cause | Workaround |
| --- | --- | --- |
| SSD folder not found | `Directory.Exists("E:\\")` in the AppContainer | Broker `RemovableDevices` + picker + FAL |
| Access denied on `Spider-Man.exe` | `CreateFile2FromAppW` / `System.IO` on an `.exe` on `E:` | StorageFolder only for the dump |
| “Cannot create game storage at e:\\” | `CreateDirectory` on the volume root | Do not create `E:\`; use the dump folder / `dumps\SpiderMan` |
| Out of space with error 5 | `GetDiskFreeSpaceExW` on removable media | `System.FreeSpace` on the StorageFolder |
| “Linking the SSD folder failed” | Junction `D:`→`E:` blocked | Overlay + `archive-link.txt`, no archive copy |
| After the 37 hashes, folder “not exposed” | `Path.GetFullPath("E:")` became `...\asset_archive` | `VolumeRoot` = `E:\`; keep the picked StorageFolder |
| Overlay writes attempted on `E:` | `NeedsBroker` went through `Physical()` and sent `D:\...\asset_archive` to the broker | `NeedsBroker` does not remap `D:` |

### Play (native host)

| Symptom | Cause | Workaround |
| --- | --- | --- |
| Immediate exit, `asset_open_error=2` on dag/g00s* | SpShell Init is **after** the asset check | `SpArgs40` wrapper on preload, IAT patch in `DllMain` |
| Menu opens, New Game exits | `S:\WindowsApps\...` requests were remapped only onto the SSD | Prefer LooseApps overlay, then SSD |
| `preference seed copy failed error=2` | `seed-userprefs.save` is not in the payload | SpShell Core continues; the menu crash was the remap, not the seed |
| Languages `a00s035.fr` and similar with `err=5` | Voice packs not present in the dump | Expected; English (`a00s034.us`) opened |

Rejected approaches:

- Copying heavy archives onto `D:`.
- Relying on AppContainer symlink/junction.
- Hooking only SpShell.
- Remapping overlay writes onto the SSD.

## Diagnostics

Useful logs (copy off the Xbox after a run):

| File | Where | What it shows |
| --- | --- | --- |
| `storage-bridge.log` | LooseApps (`D:\DevelopmentFiles\LooseApps\SpiderManRuntime-20261008\`) and, when possible, LocalState | Each remap (`request -> destination`, handle, `GetLastError`) |
| `native-startup.log` | Package LocalState | Preload, toc/dag/g00s check, CRT, exception |
| `launcher-debug.log` | LocalState | Prepare/Play flow; Settings → Copy debug log |
| `combined-launcher.log` | LocalState | Combined: verify, map travel, `storage_adapters_deployed` |
| `archive-link.txt` | LooseApps | Should be `E:\dumps\SpiderMan\asset_archive` (or the picked folder) |
| `allocation-trace.log` | LocalState | Native crash (`0xC0000005`, module RVA) if the host got past the asset check |

Native open error 2 with no `-> E:\...` line in the bridge log: the hook did not run. Error 5 **with** a remap to a language that is not in the dump: missing SSD file, not a bridge failure.

## Limits

- The host still believes files live under `StartupContent\asset_archive`. The bridge is what translates those paths.
- Voice languages that were not imported do not open (`err=5`).
- Profile 4.0630 is experimental; 3.0618 is the stable supported dump. The playable SSD run described here used 4.0630.
- This does not claim memory, shader, cutscene, or full-game stability — only that SSD in-place reached gameplay in 0.6.0.58.
- Keep the SSD connected. Without the drive, `archive-link.txt` points at a volume the AppContainer cannot see.

## Reference code

- `source/GameStorage.cs` — broker, `PlaceGameArchives`, `NeedsBroker`, `archive-link.txt`
- `source/PreparationEngine.cs` — overlays vs import files, SHA skip
- `source/CombinedRuntime.cs` — wrappers in the inventory, copy into LooseApps on Play
- `native/SpShellBridge.cpp` — IAT, `MapPath`, FromApp, broker `IStorageItemHandleAccess`
- `native/SpArgs40Bridge.vcxproj` + `SpArgs40Forwards.cpp` — proxy of the 288 exports
- `source/SpiderManInstaller.csproj` — original `SpShell` / `SpArgs40` become `*Core.dll`
- `release/STATUS.md` — version notes
