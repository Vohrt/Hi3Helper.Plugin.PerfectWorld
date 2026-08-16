# Hi3Helper.Plugin.PerfectWorld

Plugins for [Collapse Launcher](https://github.com/CollapseLauncher/Collapse) that add support for
games published/updated through **Perfect World's `pw_sdk` / PatcherSDK** update mechanism.

English | [简体中文](README.zh-CN.md)

Currently included:

| Plugin | Game | App ID |
| ------ | ---- | ------ |
| `Hi3Helper.Plugin.NTE` | **Neverness To Everness** (异环) | `1289` |
| `Hi3Helper.Plugin.P5X` | **Persona 5: The Phantom X** | `1264` |

---

## Repository layout

```
Hi3Helper.Plugin.PerfectWorld/
├── Hi3Helper.Plugin.Core/        # Collapse plugin SDK (git submodule, upstream)
├── SharpHDiffPatch.Core/         # HDiffPatch patcher (git submodule, in-repo fork with the >2 GB fix)
├── Hi3Helper.PerfectWorld.Core/  # Shared "Perfect World" publisher core (assembly: PerfectWorld.Core)
│   ├── Utils/PatcherXml0.cs      #   PatcherXML0 manifest decoder (AES-128-CBC + zlib)
│   ├── PerfectWorldGameConfig.cs #   Per-game config + CDN URL builders
│   └── Management/               #   Version manager + content-addressed download & HDiffPatch delta engine
├── Hi3Helper.Plugin.NTE/         # Thin plugin for NTE (assembly: NTE)
│   ├── Management/PresetConfig/  #   NTE-specific data (app id, CDN, exe path, launch args)
│   ├── SelfUpdate.cs             #   In-launcher self-update endpoints (release branch)
│   └── Properties/PublishProfiles/
├── Hi3Helper.Plugin.P5X/         # Thin plugin for P5X (assembly: P5X)
│   ├── Management/PresetConfig/  #   P5X-specific data (app id, CDN, exe path, launch args)
│   ├── SelfUpdate.cs             #   In-launcher self-update endpoints (release branch)
│   └── Properties/PublishProfiles/
├── Hi3Helper.Plugin.PerfectWorld.slnx
├── CompileAOTAndShip.bat         # One-click NativeAOT build + index (pick NTE or P5X)
└── Indexer.exe                   # Generates the plugin manifest Collapse consumes
```

The design mirrors the official plugin repos: a **thin plugin** (game-specific data only) on top of a
**reusable publisher core**. To support another `pw_sdk` game you generally only need a new thin plugin
project that supplies its App ID, CDN host and launch arguments — `PerfectWorld.Core` handles the rest.
The **NTE** and **P5X** plugins are built exactly this way, sharing the same core, download/patch engine and
launch driver.

## Prerequisites

* [.NET SDK **10.0**](https://dotnet.microsoft.com/download/dotnet/10.0) or newer
* For a NativeAOT publish: **Visual Studio 2022/2026** with the *Desktop development with C++* workload
  (provides the native linker `link.exe` and the Windows SDK)

## Getting the source

This repo uses git submodules (the Collapse plugin SDK and a patched `SharpHDiffPatch.Core`), so clone
**recursively**:

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
CompileAOTAndShip.bat 1 2
```

Run with no arguments to be prompted interactively — it asks **which game** to build first
(`1` NTE, `2` P5X), then the **optimization** profile (`1` Size, `2` Speed, `3` Debug, `4-6` = the same
with the experimental *Reflection-Free* mode). Both can also be passed positionally as
`CompileAOTAndShip.bat <game> <optimization>` (e.g. `1 2` = NTE + Speed). The output — including the
indexed manifest — lands in `Hi3Helper.Plugin.<NTE|P5X>\publish\<Configuration>`.

Equivalent manual publish (swap the project for the game you want):

```bash
# NTE
dotnet publish Hi3Helper.Plugin.NTE/Hi3Helper.Plugin.NTE.csproj -c Release -r win-x64 -p:PublishProfile=ReleasePublish-O2
# P5X
dotnet publish Hi3Helper.Plugin.P5X/Hi3Helper.Plugin.P5X.csproj -c Release -r win-x64 -p:PublishProfile=ReleasePublish-O2
```

This produces the native COM DLL Collapse loads — **`NTE.dll`** or **`P5X.dll`** (each exports `TryGetApiExport`).

## Creating a release bundle

Collapse distributes a plugin as a small bundle produced by **`Indexer.exe`**: it reads the published
plugin, writes a `manifest.json` (plugin name, author, version, icon and a per-asset MD5 table),
individually **Brotli-compresses** every file to `.br`, and stores them in a single zip named
`<SHORT>_<version>_API-<standardVersion>_<yyyyMMdd>.zip`, where `<SHORT>` is the plugin short name
(`NTE` or `P5X`):

```
NTE_1.0.0.0_API-0.1.5.0_20260724.zip
P5X_1.0.0.0_API-0.1.5.0_20260724.zip
```

### Locally

`CompileAOTAndShip.bat` already runs the Indexer after publishing, so the bundle appears next to the
published DLL at `Hi3Helper.Plugin.NTE\publish\Release\`. To (re)generate it by hand against any
publish folder:

```bat
Indexer.exe Hi3Helper.Plugin.NTE\publish\Release
```

## Installing into Collapse

Copy the published output (the indexed `publish\Release` folder, containing the plugin DLL — `NTE.dll` or
`P5X.dll` — and the generated manifest) into Collapse's plugin directory, then (re)start Collapse. The plugin
registers the game, its icon/poster, install/update flow and launch action.

## Automatic updates (self-update)

Once a plugin is installed, Collapse keeps it up to date automatically — no manual re-copy on every release.
On start-up Collapse hands the plugin's self-updater (`SelfUpdate.cs`, a `PluginSelfUpdateBase`) a small
`manifest.json`, which it compares against the local files and, when a newer build is published, downloads the
changed plugin DLL (`NTE.dll` / `P5X.dll`) and verifies it by MD5 before swapping it in.

The endpoints are the per-plugin folders on this repository's **`release` branch**, read through two mirrors
of the same content (a `raw.githubusercontent.com` primary + a `github.com/.../raw/...` fallback):

```
https://raw.githubusercontent.com/<owner>/Hi3Helper.Plugin.PerfectWorld/release/<SHORT>/manifest.json
https://raw.githubusercontent.com/<owner>/Hi3Helper.Plugin.PerfectWorld/release/<SHORT>/<SHORT>.dll
```

where `<SHORT>` is `NTE` or `P5X`. Those folders are populated automatically by the **Release Plugin**
workflow, which pushes the freshly-built `manifest.json` + plugin DLL onto the `release` branch on every
release. The endpoints are hard-coded in each plugin's `SelfUpdate.cs`, so a fork should repoint them at its
own `release` branch.

## How it works

The Perfect World PatcherSDK ships its file manifests encrypted as `PatcherXML0`:

* Body = `AES-128-CBC( zlib.deflate(xml) + PKCS7 )`, prefixed by a 12-byte magic and the inflated size.
* **Key** = `"<appId>@Patcher"` right-padded with `'0'` to 16 bytes (NTE → `1289@Patcher0000`, P5X → `1264@Patcher0000`).
* **IV**  = `"PatcherSDK"` right-padded with `'0'` to 16 bytes (`PatcherSDK000000`).

`PerfectWorld.Core` fetches `config.xml`, downloads and decrypts the versioned `ResList.bin.zip`, then
downloads each content-addressed file from `.../<branch>/Res/<md5[0]>/<md5>.<filesize>`, verifying MD5. Only
the App ID, CDN host and resource branch differ per game (NTE → `publish_PC` on `yhcdn*.wmupd.com`; P5X →
`CN_OB_OFFICIAL` on `nsywl-client-dev*.wmupd.com`).

**Incremental updates (HDiffPatch).** On update, `PerfectWorld.Core` also fetches and decrypts the versioned
`lastdiff.bin` patch manifest. For every changed file it looks for a binary delta whose source MD5 matches
the local file and whose target MD5 matches the wanted file; when one exists (and is smaller than a full
download) it downloads just that small `HDIFF13` patch blob, applies it locally with the in-repo
[`SharpHDiffPatch.Core`](https://github.com/Vohrt/SharpHDiffPatch.Core) fork (a git submodule), and verifies
the result by MD5. That fork is what keeps large updates small: the upstream patcher buffers each patch's
output in a single in-memory stream and throws on target files larger than ~2 GiB, so before the fix every
changed file above that size (NTE's `.pak`/`.ucas` chunks run 4–7 GB) silently fell back to a full
re-download — turning a ~160 MB incremental update into ~17 GB. The fork windows that output cache so those
multi-GB files patch correctly with bounded (~256 MiB) memory. Files with no usable patch — or whose patch
fails to apply/verify — transparently fall back to a full content-addressed download, and if the whole
`lastdiff` manifest is unavailable the classic full-file reconciliation is used. This keeps updates small even
though NTE's UE5 IoStore packs everything into a few multi-GB `.pak`/`.ucas` files.

The **reported update size** reflects this too: when computing the remaining download for an update,
`PerfectWorld.Core` credits each available delta (it subtracts the patch size from every changed file that has a
usable patch), so the figure shown before you start matches the true, small incremental transfer instead of
the full size of every changed multi-GB pak.

**Launcher content (background, news, banners, social).** The plugin also fills Collapse's home screen
straight from Perfect World's live web sources (NTE from `yh.wanmei.com`, P5X from `p5x.wanmei.com`) —
`pw_sdk` has no public launcher-media JSON API, so each element is derived from what the official launcher
itself uses:

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

**Game bring-up (vendor launcher & silent launch).** For **both** games, account login is *not* handled by
the game client itself — the sign-in UI, anti-cheat bring-up and the token hand-off all live in Perfect
World's own launcher app, so launching the bare game executable opens it with no login prompt. The plugin
therefore **installs the vendor launcher as part of install/repair and launches through it**; a finished
install consequently requires *both* the game runtime and the launcher entry point, and an earlier
launcher-less install is reported as *not installed* until a repair fetches it. A shared, config-driven
**silent launch** then keeps the launcher out of the way: right before every launch the plugin patches the
launcher's own user-writable settings (`…\UserData\Config\Config.ini`) to auto-login, auto-start the game and
quit together with it (never reappearing afterwards), and tracks the real **game** process rather than the thin
launcher shim (which exits within a second of spawning the game). The launcher runs in its **normal window** during
its brief auto-login start-up; on the default auto-click path it minimises itself to the tray the moment its
"开始游戏" button is pressed — exactly like the official launcher after a manual click — and it is terminated together
with the game.

The per-game specifics differ:

* **NTE** (UE5, `HTGame.exe`). The launcher is fetched from its self-update manifest
  (`.../publish_ob/launcher/Version.ini` → `AllFiles.xml`) and inflated into `NTELauncher\` (~670 individually
  zip-compressed files, ≈237 MB; zero-length entries carry a bogus placeholder checksum and are validated by
  emptiness instead of MD5); launch runs `NTELauncher\NTEGame.exe` **directly** — not the thin
  `NTELauncher.exe` shim, which merely relocates the patcher then spawns `NTEGame.exe /launcher /directly`, so
  the plugin reproduces that byte-for-byte and drops the redundant intermediary — with the working directory set
  to the install root. A finished install needs both `HTGameBase.dll` and `NTEGame.exe`. By default the plugin
  presses the launcher's "开始游戏" button via DLL-injection **auto-click** (see *Launcher auto-click* below); if
  that is unavailable it falls back to passing `/autoplay`, which auto-starts the game without a manual click but
  makes `NTEGame.exe` skip its in-process resource updater. Because the plugin must stay correct on the
  `/autoplay` fallback too, it still downloads **all** of NTE's voice languages up front at install/update time
  (see *Known issues* below).
* **P5X** (Unity IL2CPP, `client\pc\P5X.exe`). The plugin launches `P5XLaunch\P5XGame.exe` directly. By default
  it presses "开始游戏" via DLL-injection **auto-click** (see *Launcher auto-click* below); the fallback is
  `/launcher /directly /autoplay`. Either way the click is what matters: P5X's launcher gates auto-play off
  *server-side* (`canAutoPlay=false`), so patching the INI alone is not enough and it would otherwise stop at the
  "开始游戏" button — a real button press (auto-click) or the `/autoplay` override both get past it. A finished
  install needs both `GameAssembly.dll` and `P5XGame.exe`.

**Known issue (NTE) — every voice language is downloaded, not just one.** The official launcher installs the
base game plus a single default (Chinese) voice and lets the in-game updater fetch the other languages
(Japanese / English / Korean) on demand, so a fresh official install lists those three with a download size.
The plugin instead ships **all four** voice packs at install/update time, so an NTE install is ~5 GB larger
than the official one and the in-game menu shows every language as already installed. This is deliberate and
tied to the `/autoplay` **fallback** (above): that flag auto-starts the game but makes `NTEGame.exe` skip the
in-process `GameResUpdaterAgent` — the very component that reconciles/downloads voices on demand and seeds the
in-game updater's hand-off state. (The default *auto-click* path does let that updater run, but because every
voice is already bundled it finds nothing to fetch — so the game behaves the same either way.) An earlier
version tried to mirror the official launcher — defer the non-default
voices, drop `/autoplay`, and forge the native `PatcherSDK` state files (`config.xml` / `ResList.xml` /
`tmp\client.xml`) so the game would believe one voice was installed and fetch the rest itself. It was
**abandoned**: in practice the game looped on "更新失败" (update failed) after returning to the login screen and
listed every voice language *without* a size (i.e. it thought they were all already installed), because the
skipped/under-seeded updater never established a valid local state. Bundling all voices up front sidesteps that
entirely, at the cost of extra disk and bandwidth.

**Launcher auto-click (DLL injection) — the default launch path.** Passing `/autoplay` auto-starts the game but
has a side effect: on NTE it makes `NTEGame.exe` skip its in-process resource updater (the voice pitfall above).
To avoid it, the plugin's **default** launch path presses the launcher's real "开始游戏" button programmatically:

* **What is injected.** A tiny native helper, **`PwAutoClick.dll`** (x64, links the static CRT, depends only on
  `KERNEL32.dll`). Its source is in `Hi3Helper.PerfectWorld.Core/Native/PwAutoClick/`; the compiled DLL is
  embedded into `PerfectWorld.Core.dll` as a resource, extracted to `%TEMP%\CollapsePwPlugin\` at launch and
  loaded into the vendor launcher (`NTEGame.exe` / `P5XGame.exe`) via `CreateRemoteThread` + `LoadLibraryW`.
* **How the click is made.** Both launchers are Qt 5.15.17 apps whose play button runs the QML slot
  `BackgroudStageScheduler.gameActionBtnClicked()` (vendor spelling — the second *n* really is missing).
  `BackgroudStageScheduler` is a C++ `QObject` exposed to QML via
  `QQmlContext::setContextProperty(const QString&, QObject*)`. The DLL IAT-hooks that Qt export to capture the
  object pointer; then, once the launcher logs `all ready, wait for start game` (the plugin tails the launcher
  log and sets a named `Event` the DLL waits on), it calls
  `QMetaObject::invokeMethod(obj, "gameActionBtnClicked", Qt::QueuedConnection)` to fire the click on the
  launcher's GUI thread. This runs the launcher's **normal** flow — resource check included — exactly as a
  manual click would, with no `/autoplay` shortcut.
* **Requires administrator.** The vendor launcher is force-elevated, so injecting into it needs Collapse to run
  elevated too; when it doesn't, auto-click is skipped and the `/autoplay` fallback is used.
* **Fallback ladder (you are never left stuck).** If auto-click can't activate (Collapse not elevated, or the
  embedded DLL can't be prepared) the plugin uses the old `/autoplay` path. If injection is *attempted* but
  fails, the launcher is started **without** `/autoplay` and stays visible so you can click "开始游戏" yourself.
  If injection succeeds but the click never fires, the launcher simply stays on screen at the button — press it
  manually.
* **Diagnostics.** The DLL writes `%TEMP%\CollapsePwPlugin\PwAutoClick.log` (symbol resolution, hook count, object
  capture, the `invokeMethod` result); the plugin logs `[PWAutoClick]` lines for injection status. Both are safe to
  delete.
* **To disable / revert.** Set `LauncherAutoClickEnabled = false` in the game's preset (`NteCnPresetConfig.cs` /
  `P5xCnPresetConfig.cs`) and rebuild to force the proven `/autoplay` behaviour. A user-supplied custom launch
  argument also disables auto-click automatically (your argument is honoured verbatim).
* **Building the native DLL.** A normal plugin build needs **no** C++ toolchain — the prebuilt
  `Native/PwAutoClick.dll` is committed and embedded as-is. If you change the C++ source, regenerate it from a
  shell with the *x64 Native Tools* environment by running
  `Hi3Helper.PerfectWorld.Core/Native/PwAutoClick/build.ps1` before rebuilding the plugin.

## Credits & license

* Built on the [Collapse Launcher plugin SDK](https://github.com/CollapseLauncher/Hi3Helper.Plugin.Core).
* Released under the [MIT License](LICENSE).

> This is an unofficial, fan-made compatibility plugin. All game assets and trademarks belong to their
> respective owners. It is not affiliated with or endorsed by the game's developers or publishers.
