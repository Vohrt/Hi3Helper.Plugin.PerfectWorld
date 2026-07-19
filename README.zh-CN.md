# Hi3Helper.Plugin.PerfectWorld

为 [Collapse Launcher](https://github.com/CollapseLauncher/Collapse) 开发的插件集合，用于支持
采用 **完美世界 `pw_sdk` / PatcherSDK** 更新机制的游戏。

[English](README.md) | 简体中文

当前包含：

| 插件 | 游戏 | App ID |
| ---- | ---- | ------ |
| `Hi3Helper.Plugin.NTE` | **异环**（Neverness To Everness） | `1289` |

---

## 目录结构

```
Hi3Helper.Plugin.PerfectWorld/
├── Hi3Helper.Plugin.Core/        # Collapse 插件 SDK（git 子模块，指向上游官方仓库）
├── Hi3Helper.Wanmei.Core/        # 可复用的“完美世界”发行商核心（程序集：Wanmei.Core）
│   ├── Utils/PatcherXml0.cs      #   PatcherXML0 清单解码器（AES-128-CBC + zlib）
│   ├── WanmeiGameConfig.cs       #   每游戏配置 + CDN URL 构造
│   └── Management/               #   版本管理器 + 内容寻址下载与 HDiffPatch 增量引擎
├── Hi3Helper.Plugin.NTE/         # 异环的薄插件（程序集：NTE）
│   ├── Management/PresetConfig/  #   异环专属数据（App ID、CDN、可执行文件路径、启动参数）
│   └── Properties/PublishProfiles/
├── Hi3Helper.Plugin.PerfectWorld.slnx
├── CompileAOTAndShip.bat         # 一键 NativeAOT 编译 + 生成索引
└── Indexer.exe                   # 生成 Collapse 识别所需的插件清单
```

设计沿用官方插件仓库的思路：在**可复用的发行商核心**之上放一个只含游戏专属数据的**薄插件**。
要支持另一款 `pw_sdk` 游戏，通常只需新建一个薄插件工程，提供它的 App ID、CDN 域名和启动参数即可，
其余逻辑由 `Wanmei.Core` 负责。

## 环境要求

* [.NET SDK **10.0**](https://dotnet.microsoft.com/download/dotnet/10.0) 或更新版本
* 若要进行 NativeAOT 发布：安装带有 **“使用 C++ 的桌面开发”** 工作负载的 **Visual Studio 2022/2026**
  （提供本机链接器 `link.exe` 与 Windows SDK）

## 获取源码

本仓库通过 git 子模块引用 Collapse 插件 SDK，请**递归克隆**：

```bash
git clone --recursive https://github.com/<你>/Hi3Helper.Plugin.PerfectWorld.git
```

如果已经克隆但没加 `--recursive`：

```bash
git submodule update --init --recursive
```

## 编译

### 快速编译检查（托管，非 AOT）

```bash
dotnet build Hi3Helper.Plugin.PerfectWorld.slnx -c Debug
```

### 正式发布（NativeAOT，需在 *x64 Native Tools 命令提示符* 中运行）

```bat
CompileAOTAndShip.bat 2
```

参数用于选择优化档位（`1` 体积、`2` 速度、`3` 调试、`4-6` = 对应档位 + 实验性的“无反射”模式）。
产物（含已生成的索引清单）位于 `Hi3Helper.Plugin.NTE\publish\<配置名>`。

等价的手动发布命令：

```bash
dotnet publish Hi3Helper.Plugin.NTE/Hi3Helper.Plugin.NTE.csproj -c Release -r win-x64 -p:PublishProfile=ReleasePublish-O2
```

它会产出 Collapse 加载的原生 COM 动态库 **`NTE.dll`**（导出 `TryGetApiExport`）。

## 安装到 Collapse

把发布产物（已建立索引的 `publish\Release` 文件夹，内含 `NTE.dll` 及生成的清单）复制到 Collapse
的插件目录，然后重启 Collapse。插件会注册游戏、图标/海报、安装/更新流程以及启动动作。

## 原理（异环）

完美世界 PatcherSDK 的文件清单以 `PatcherXML0` 加密分发：

* 主体 = `AES-128-CBC( zlib.deflate(xml) + PKCS7 )`，前面带 12 字节魔数和解压后大小。
* **密钥** = `"<appId>@Patcher"` 用 `'0'` 右补齐到 16 字节（异环 → `1289@Patcher0000`）。
* **IV**  = `"PatcherSDK"` 用 `'0'` 右补齐到 16 字节（`PatcherSDK000000`）。

`Wanmei.Core` 会拉取 `config.xml`，下载并解密带版本号的 `ResList.bin.zip`，再从
`.../publish_PC/Res/<md5[0]>/<md5>.<filesize>` 下载每个内容寻址文件并校验 MD5。

**增量更新（HDiffPatch）。** 更新时，`Wanmei.Core` 还会拉取并解密带版本号的 `lastdiff.bin` 补丁清单。
对每个发生变化的文件，它会查找一个二进制差分补丁——其源 MD5 与本地文件一致、目标 MD5 与目标文件一致；
若存在这样的补丁（且体积小于整文件下载），就只下载这个很小的 `HDIFF13` 补丁块，用托管的
[`SharpHDiffPatch.Core`](https://github.com/CollapseLauncher/SharpHDiffPatch.Core) 打补丁器在本地应用，
并按 MD5 校验结果。若某文件没有可用补丁，或补丁应用/校验失败，则自动回退为整文件内容寻址下载；
若整个 `lastdiff` 清单不可用，则回退到经典的全文件对账。这样即便异环的 UE5 IoStore 把所有内容打包进
几个数 GB 的 `.pak`/`.ucas` 大文件，增量更新的下载量依然很小。

## 致谢与许可

* 基于 [Collapse Launcher 插件 SDK](https://github.com/CollapseLauncher/Hi3Helper.Plugin.Core) 构建。
* 以 [MIT 许可证](LICENSE) 发布。

> 本项目为非官方、粉丝制作的兼容性插件。所有游戏素材与商标归各自权利人所有，
> 与游戏的开发商 / 发行商无任何隶属或背书关系。
