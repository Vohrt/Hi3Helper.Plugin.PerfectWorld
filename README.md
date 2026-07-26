# Hi3Helper.Plugin.PerfectWorld

Plugins for [Collapse Launcher](https://github.com/CollapseLauncher/Collapse) that add support for
games published/updated through **Perfect World's `pw_sdk` / PatcherSDK** update mechanism.

English | [简体中文](README.zh-CN.md)

Currently included:

| Plugin | Game | App ID |
| ------ | ---- | ------ |
| `Hi3Helper.Plugin.NTE` | **Neverness To Everness** (异环) | `1289` |

---

## Repository layout

```
Hi3Helper.Plugin.PerfectWorld/
├── Hi3Helper.Plugin.Core/        # Collapse plugin SDK (git submodule, upstream)
├── Hi3Helper.PerfectWorld.Core/  # Shared "Perfect World" publisher core (assembly: PerfectWorld.Core)
│   ├── Utils/PatcherXml0.cs      #   PatcherXML0 manifest decoder (AES-128-CBC + zlib)
│   ├── PerfectWorldGameConfig.cs #   Per-game config + CDN URL builders
│   └── Management/               #   Version manager + content-addressed download & HDiffPatch delta engine
├── Hi3Helper.Plugin.NTE/         # Thin plugin for NTE (assembly: NTE)
│   ├── Management/PresetConfig/  #   NTE-specific data (app id, CDN, exe path, launch args)
│   └── Properties/PublishProfiles/
├── Hi3Helper.Plugin.PerfectWorld.slnx
├── CompileAOTAndShip.bat         # One-click NativeAOT build + index
└── Indexer.exe                   # Generates the plugin manifest Collapse consumes
```

The design mirrors the official plugin repos: a **thin plugin** (game-specific data only) on top of a
**reusable publisher core**. To support another `pw_sdk` game you generally only need a new thin plugin
project that supplies its App ID, CDN host and launch arguments — `PerfectWorld.Core` handles the rest.

## Prerequisites

* [.NET SDK **10.0**](https://dotnet.microsoft.com/download/dotnet/10.0) or newer
* For a NativeAOT publish: **Visual Studio 2022/2026** with the *Desktop development with C++* workload
  (provides the native linker `link.exe` and the Windows SDK)

## Getting the source

This repo uses a git submodule for the Collapse plugin SDK, so clone **recursively**:

```bash
git clone --recursive https://github.com/<you>/Hi3Helper.Plugin.PerfectWorld.git
```

Already cloned without `--recursive`? Run:

```bash
git submodule update --init --recursive
```

## Building

### Quick compile check (managed, no AOT)

```bash
dotnet build Hi3Helper.Plugin.PerfectWorld.slnx -c Debug
```

### Ship it (NativeAOT, from a *Developer Command Prompt / x64 Native Tools* shell)

```bat
CompileAOTAndShip.bat 2
```

The argument selects the optimization profile (`1` Size, `2` Speed, `3` Debug, `4-6` = the same with the
experimental *Reflection-Free* mode). The output — including the indexed manifest — lands in
`Hi3Helper.Plugin.NTE\publish\<Configuration>`.

Equivalent manual publish:

```bash
dotnet publish Hi3Helper.Plugin.NTE/Hi3Helper.Plugin.NTE.csproj -c Release -r win-x64 -p:PublishProfile=ReleasePublish-O2
```

This produces the native COM DLL **`NTE.dll`** that Collapse loads (it exports `TryGetApiExport`).

## Creating a release bundle

Collapse distributes a plugin as a small bundle produced by **`Indexer.exe`**: it reads the published
plugin, writes a `manifest.json` (plugin name, author, version, icon and a per-asset MD5 table),
individually **Brotli-compresses** every file to `.br`, and stores them in a single zip named

```
NTE_<version>_API-<standardVersion>_<yyyyMMdd>.zip   e.g. NTE_1.0.0.0_API-0.1.5.0_20260724.zip
```

### Locally

`CompileAOTAndShip.bat` already runs the Indexer after publishing, so the bundle appears next to the
published DLL at `Hi3Helper.Plugin.NTE\publish\Release\`. To (re)generate it by hand against any
publish folder:

```bat
Indexer.exe Hi3Helper.Plugin.NTE\publish\Release
```

### On GitHub (Actions)

The **Release Plugin** workflow (`.github/workflows/release.yml`) does the whole thing on a
`windows-latest` runner and publishes the zip to a GitHub Release. Trigger it from the *Actions* tab →
*Release Plugin* → *Run workflow*, and provide:

* **version** — e.g. `1.0.0` (also stamped into the assembly and the manifest)
* **publish_profile** — defaults to `ReleasePublish-O2` (Speed); the `…NoReflection…` profiles build the
  reflection-free variant

The run publishes `NTE_<version>_API-<standardVersion>_<date>.zip` to a new release tagged
`NTE@v<version>`. No extra secrets are required — it uses the built-in `GITHUB_TOKEN`.

## Installing into Collapse

Copy the published output (the indexed `publish\Release` folder, containing `NTE.dll` and the generated
manifest) into Collapse's plugin directory, then (re)start Collapse. The plugin registers the game,
its icon/poster, install/update flow and launch action.

## How it works (NTE)

The Perfect World PatcherSDK ships its file manifests encrypted as `PatcherXML0`:

* Body = `AES-128-CBC( zlib.deflate(xml) + PKCS7 )`, prefixed by a 12-byte magic and the inflated size.
* **Key** = `"<appId>@Patcher"` right-padded with `'0'` to 16 bytes (NTE → `1289@Patcher0000`).
* **IV**  = `"PatcherSDK"` right-padded with `'0'` to 16 bytes (`PatcherSDK000000`).

`PerfectWorld.Core` fetches `config.xml`, downloads and decrypts the versioned `ResList.bin.zip`, then
downloads each content-addressed file from `.../publish_PC/Res/<md5[0]>/<md5>.<filesize>`, verifying MD5.

**Incremental updates (HDiffPatch).** On update, `PerfectWorld.Core` also fetches and decrypts the versioned
`lastdiff.bin` patch manifest. For every changed file it looks for a binary delta whose source MD5 matches
the local file and whose target MD5 matches the wanted file; when one exists (and is smaller than a full
download) it downloads just that small `HDIFF13` patch blob, applies it locally with the managed
[`SharpHDiffPatch.Core`](https://github.com/CollapseLauncher/SharpHDiffPatch.Core) patcher, and verifies the
result by MD5. Files with no usable patch — or whose patch fails to apply/verify — transparently fall back to
a full content-addressed download, and if the whole `lastdiff` manifest is unavailable the classic full-file
reconciliation is used. This keeps NTE updates small even though its UE5 IoStore packs everything into a few
multi-GB `.pak`/`.ucas` files.

The **reported update size** reflects this too: when computing the remaining download for an update,
`PerfectWorld.Core` credits each available delta (it subtracts the patch size from every changed file that has a
usable patch), so the figure shown before you start matches the true, small incremental transfer instead of
the full size of every changed multi-GB pak.

**Launcher content (background, news, banners, social).** The plugin also fills Collapse's home screen
straight from Perfect World's live web sources — `pw_sdk` has no public launcher-media JSON API, so each
element is derived from what the official launcher itself uses:

* **Background image & video** — taken from the launcher's own `bgimgs` set (`Version.ini` →
  `AllFiles.xml` → `config.json`). The static image and the video clip are downloaded, unzipped and cached
  content-addressed, then served to Collapse as local files (image is the default; the video is a
  switchable second background).
* **News** — the three columns (Info / Notice / Event) are parsed from the public launcher page.
* **Carousel banners** — parsed from the site's `gameSwiper` data (banner image + click-through link).
* **Social media** — the sidebar entries (官方微博, 官方 QQ 群, 官方微信, TapTap, 好游快爆, 塔吉多,
  官方客服) with self-contained inline-SVG icons and, where available, QR images.

All of this relies only on AOT-safe primitives (`[GeneratedRegex]`, `JsonDocument`,
`System.IO.Compression`) and pulls in no third-party dependency. Every step is best-effort: if a source is
unreachable the plugin just omits that element and Collapse falls back to its embedded poster.

**Launching & login (vendor launcher).** NTE's account login is *not* handled by the game client itself: the
sign-in UI, anti-cheat bring-up and the token hand-off to the game all live in Perfect World's own
`NTELauncher`. Launching `HTGame.exe` directly therefore opens the game with no login prompt. To make the game
actually playable, the plugin **downloads the official launcher as part of install/repair** — it reads the
launcher self-update manifest (`.../publish_ob/launcher/Version.ini` → `AllFiles.xml`), then fetches, verifies
and inflates each of its ~670 individually zip-compressed files into `NTELauncher\` (≈237 MB; zero-length
entries carry a bogus placeholder checksum in the manifest and are validated by emptiness instead of MD5).
Clicking **Launch** then runs `NTELauncher\NTELauncher.exe` with the working directory set to the install root
— exactly like the official `异环` shortcut — so the launcher shows the login UI and starts the game normally.
A finished install now requires both the game runtime (`HTGameBase.dll`) and the launcher entry point
(`NTELauncher.exe`); an earlier launcher-less install is reported as *not installed* until a repair fetches it.

**Silent launch.** So the vendor launcher does not get in the way, the plugin patches the launcher's own
user-writable settings (`NTELauncher\UserData\Config\Config.ini`) right before every launch — enabling
`autoLogin`, `autoRun` (start the game with no manual *Start* click), `quitWithGame` and `showAfterGameQuit=0`
(do not reappear after the game exits) — and then tracks the **game** process rather than the thin launcher
shim (which exits within a second of spawning the game). Because HTGame.exe only appears after a UAC prompt
and a 30–120 s auto-login, the plugin reports **game running** for that whole start-up window (bridging the
gap where the shim has exited but the game has not yet spawned), so Collapse stays minimized and the button
matches the other plugins instead of flipping back to *Start*. This alone removes the extra click and the post-exit
re-popup. Fully hiding the launcher window during the ~1-minute auto-login start-up additionally requires
Collapse to run **as administrator**: `NTEGame.exe` is force-elevated, so a non-elevated host is blocked by
Windows UIPI from touching its window. When elevated, the window is hidden during start-up and revealed only
if the log reports that an interactive login is needed (first run / expired token) or after a safety timeout.

**Known issue — the button can stay on *game running* for a while after you quit.** NTE's vendor launcher
owns the game process's lifetime: when you close the game it does **not** terminate `HTGame.exe` right away —
it leaves the process running idle (with no window) and only reaps it some time later. This appears to be a
quirk/bug of the official launcher itself (it is very slow to close the game process). Because the plugin
tracks the real game process — the reliable signal that the game is actually up — Collapse keeps reporting
**game running** until that process finally exits, so the button can take a while to return to *Start*. It
**does** reset on its own once the process goes away; no action is required. If you want it back immediately,
right-click the **异环** icon in the Windows system tray and choose exit — that force-closes the lingering
game process at once. (The plugin deliberately does *not* kill the process early on its own, to avoid the risk
of terminating a game that is merely still loading.)

## Credits & license

* Built on the [Collapse Launcher plugin SDK](https://github.com/CollapseLauncher/Hi3Helper.Plugin.Core).
* Released under the [MIT License](LICENSE).

> This is an unofficial, fan-made compatibility plugin. All game assets and trademarks belong to their
> respective owners. It is not affiliated with or endorsed by the game's developers or publishers.
