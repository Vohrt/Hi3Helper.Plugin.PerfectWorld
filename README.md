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

### On GitHub (Actions)

The **Release Plugin** workflow (`.github/workflows/release.yml`) does the whole thing on a
`windows-latest` runner and publishes the zip to a GitHub Release. Trigger it from the *Actions* tab →
*Release Plugin* → *Run workflow*, and provide:

* **version** — e.g. `1.0.0` (also stamped into the assembly and the manifest)
* **publish_profile** — defaults to `ReleasePublish-O2` (Speed); the `…NoReflection…` profiles build the
  reflection-free variant

The run publishes `NTE_<version>_API-<standardVersion>_<date>.zip` to a new release tagged
`NTE@v<version>`, and also pushes the freshly-built `manifest.json` + `NTE.dll` onto the repo's `release`
branch (under `NTE/`) so the in-launcher self-updater can pick them up (see
[Automatic updates](#automatic-updates-self-update) below). No extra secrets are required — it uses the
built-in `GITHUB_TOKEN`. The workflow currently builds the **NTE** plugin; **P5X** ships the same way (same
publish profiles and Indexer) through an equivalent run.

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
quit together with it (never reappearing afterwards), tracks the real **game** process rather than the thin
launcher shim (which exits within a second of spawning the game), and — when Collapse itself runs **as
administrator** — hides the launcher window during the ~1-minute auto-login start-up, revealing it only if an
interactive login is actually needed (first run / expired token) or after a safety timeout. Elevation is
required for the window-hiding because the vendor game process is force-elevated and Windows UIPI blocks a
non-elevated host from touching its window.

The per-game specifics differ:

* **NTE** (UE5, `HTGame.exe`). The launcher is fetched from its self-update manifest
  (`.../publish_ob/launcher/Version.ini` → `AllFiles.xml`) and inflated into `NTELauncher\` (~670 individually
  zip-compressed files, ≈237 MB; zero-length entries carry a bogus placeholder checksum and are validated by
  emptiness instead of MD5); launch runs `NTELauncher\NTELauncher.exe` with the working directory set to the
  install root, exactly like the official 异环 shortcut. A finished install needs both `HTGameBase.dll` and
  `NTELauncher.exe`. Launch passes `/autoplay`, which `NTELauncher.exe` forwards verbatim to `NTEGame.exe` so
  the game auto-starts without a manual "开始游戏" click. Because `/autoplay` makes `NTEGame.exe` skip its
  in-process resource updater — the step that would otherwise fetch a voice language on demand — the plugin
  downloads **all** of NTE's voice languages up front at install/update time (see *Known issues* below).
* **P5X** (Unity IL2CPP, `client\pc\P5X.exe`). The plugin launches `P5XLaunch\P5XGame.exe` directly with
  `/launcher /directly /autoplay`. The `/autoplay` flag is essential — P5X's launcher gates auto-play off
  *server-side* (`canAutoPlay=false`), so patching the INI alone is not enough and it would otherwise stop at
  the "开始游戏" button. A finished install needs both `GameAssembly.dll` and `P5XGame.exe`.

**Known issue (NTE) — every voice language is downloaded, not just one.** The official launcher installs the
base game plus a single default (Chinese) voice and lets the in-game updater fetch the other languages
(Japanese / English / Korean) on demand, so a fresh official install lists those three with a download size.
The plugin instead ships **all four** voice packs at install/update time, so an NTE install is ~5 GB larger
than the official one and the in-game menu shows every language as already installed. This is deliberate and
tied to `/autoplay` (above): that flag auto-starts the game but makes `NTEGame.exe` skip the in-process
`GameResUpdaterAgent` — the very component that reconciles/downloads voices on demand and seeds the in-game
updater's hand-off state. An earlier version tried to mirror the official launcher — defer the non-default
voices, drop `/autoplay`, and forge the native `PatcherSDK` state files (`config.xml` / `ResList.xml` /
`tmp\client.xml`) so the game would believe one voice was installed and fetch the rest itself. It was
**abandoned**: in practice the game looped on "更新失败" (update failed) after returning to the login screen and
listed every voice language *without* a size (i.e. it thought they were all already installed), because the
skipped/under-seeded updater never established a valid local state. Bundling all voices up front sidesteps that
entirely, at the cost of extra disk and bandwidth.

**Known issue (NTE) — the button can stay on *game running* for a while after you quit.** NTE's vendor launcher
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
