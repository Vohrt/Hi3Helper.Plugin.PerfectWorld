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
├── Hi3Helper.Wanmei.Core/        # Shared "Perfect World" publisher core (assembly: Wanmei.Core)
│   ├── Utils/PatcherXml0.cs      #   PatcherXML0 manifest decoder (AES-128-CBC + zlib)
│   ├── WanmeiGameConfig.cs       #   Per-game config + CDN URL builders
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
project that supplies its App ID, CDN host and launch arguments — `Wanmei.Core` handles the rest.

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

## Installing into Collapse

Copy the published output (the indexed `publish\Release` folder, containing `NTE.dll` and the generated
manifest) into Collapse's plugin directory, then (re)start Collapse. The plugin registers the game,
its icon/poster, install/update flow and launch action.

## How it works (NTE)

The Perfect World PatcherSDK ships its file manifests encrypted as `PatcherXML0`:

* Body = `AES-128-CBC( zlib.deflate(xml) + PKCS7 )`, prefixed by a 12-byte magic and the inflated size.
* **Key** = `"<appId>@Patcher"` right-padded with `'0'` to 16 bytes (NTE → `1289@Patcher0000`).
* **IV**  = `"PatcherSDK"` right-padded with `'0'` to 16 bytes (`PatcherSDK000000`).

`Wanmei.Core` fetches `config.xml`, downloads and decrypts the versioned `ResList.bin.zip`, then
downloads each content-addressed file from `.../publish_PC/Res/<md5[0]>/<md5>.<filesize>`, verifying MD5.

**Incremental updates (HDiffPatch).** On update, `Wanmei.Core` also fetches and decrypts the versioned
`lastdiff.bin` patch manifest. For every changed file it looks for a binary delta whose source MD5 matches
the local file and whose target MD5 matches the wanted file; when one exists (and is smaller than a full
download) it downloads just that small `HDIFF13` patch blob, applies it locally with the managed
[`SharpHDiffPatch.Core`](https://github.com/CollapseLauncher/SharpHDiffPatch.Core) patcher, and verifies the
result by MD5. Files with no usable patch — or whose patch fails to apply/verify — transparently fall back to
a full content-addressed download, and if the whole `lastdiff` manifest is unavailable the classic full-file
reconciliation is used. This keeps NTE updates small even though its UE5 IoStore packs everything into a few
multi-GB `.pak`/`.ucas` files.

## Credits & license

* Built on the [Collapse Launcher plugin SDK](https://github.com/CollapseLauncher/Hi3Helper.Plugin.Core).
* Released under the [MIT License](LICENSE).

> This is an unofficial, fan-made compatibility plugin. All game assets and trademarks belong to their
> respective owners. It is not affiliated with or endorsed by the game's developers or publishers.
