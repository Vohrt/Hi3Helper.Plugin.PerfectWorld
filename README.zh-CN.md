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
├── Hi3Helper.PerfectWorld.Core/  # 可复用的“完美世界”发行商核心（程序集：PerfectWorld.Core）
│   ├── Utils/PatcherXml0.cs      #   PatcherXML0 清单解码器（AES-128-CBC + zlib）
│   ├── PerfectWorldGameConfig.cs #   每游戏配置 + CDN URL 构造
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
其余逻辑由 `PerfectWorld.Core` 负责。

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
CompileAOTAndShip.bat 1 2
```

不带参数运行会进入交互式选择：**先选择要编译的游戏**（`1` NTE、`2` P5X），再选择优化档位
（`1` 体积、`2` 速度、`3` 调试、`4-6` = 对应档位 + 实验性的“无反射”模式）。也可按位置直接传参
`CompileAOTAndShip.bat <游戏> <优化档位>`（例如 `1 2` = NTE + 速度）。
产物（含已生成的索引清单）位于 `Hi3Helper.Plugin.<NTE|P5X>\publish\<配置名>`。

等价的手动发布命令：

```bash
dotnet publish Hi3Helper.Plugin.NTE/Hi3Helper.Plugin.NTE.csproj -c Release -r win-x64 -p:PublishProfile=ReleasePublish-O2
```

它会产出 Collapse 加载的原生 COM 动态库 **`NTE.dll`**（导出 `TryGetApiExport`）。

## 生成发布包

Collapse 以一个由 **`Indexer.exe`** 生成的小体积发布包来分发插件：它读取发布好的插件，写出
`manifest.json`（插件名、作者、版本、图标以及逐资源的 MD5 表），把每个文件单独用 **Brotli** 压缩为
`.br`，再存进一个统一命名的 zip：

```
NTE_<版本>_API-<标准版本>_<yyyyMMdd>.zip   例如 NTE_1.0.0.0_API-0.1.5.0_20260724.zip
```

### 本地

`CompileAOTAndShip.bat` 在发布后已经会自动运行 Indexer，因此发布包会出现在已发布 DLL 旁边，即
`Hi3Helper.Plugin.NTE\publish\Release\`。要针对任意发布目录手动（重新）生成：

```bat
Indexer.exe Hi3Helper.Plugin.NTE\publish\Release
```

### 在 GitHub 上（Actions）

**Release Plugin** 工作流（`.github/workflows/release.yml`）会在 `windows-latest` 运行器上完成整套流程，
并把 zip 发布到 GitHub Release。在 *Actions* 选项卡 → *Release Plugin* → *Run workflow* 触发，并填写：

* **version**——例如 `1.0.0`（同时会写入程序集与清单）
* **publish_profile**——默认 `ReleasePublish-O2`（速度优先）；带 `…NoReflection…` 的档位会构建无反射变体

该运行会把 `NTE_<版本>_API-<标准版本>_<日期>.zip` 发布到一个新的、标签为 `NTE@v<版本>` 的 Release。
无需任何额外密钥——它使用内置的 `GITHUB_TOKEN`。

## 安装到 Collapse

把发布产物（已建立索引的 `publish\Release` 文件夹，内含 `NTE.dll` 及生成的清单）复制到 Collapse
的插件目录，然后重启 Collapse。插件会注册游戏、图标/海报、安装/更新流程以及启动动作。

## 原理（异环）

完美世界 PatcherSDK 的文件清单以 `PatcherXML0` 加密分发：

* 主体 = `AES-128-CBC( zlib.deflate(xml) + PKCS7 )`，前面带 12 字节魔数和解压后大小。
* **密钥** = `"<appId>@Patcher"` 用 `'0'` 右补齐到 16 字节（异环 → `1289@Patcher0000`）。
* **IV**  = `"PatcherSDK"` 用 `'0'` 右补齐到 16 字节（`PatcherSDK000000`）。

`PerfectWorld.Core` 会拉取 `config.xml`，下载并解密带版本号的 `ResList.bin.zip`，再从
`.../publish_PC/Res/<md5[0]>/<md5>.<filesize>` 下载每个内容寻址文件并校验 MD5。

**增量更新（HDiffPatch）。** 更新时，`PerfectWorld.Core` 还会拉取并解密带版本号的 `lastdiff.bin` 补丁清单。
对每个发生变化的文件，它会查找一个二进制差分补丁——其源 MD5 与本地文件一致、目标 MD5 与目标文件一致；
若存在这样的补丁（且体积小于整文件下载），就只下载这个很小的 `HDIFF13` 补丁块，用托管的
[`SharpHDiffPatch.Core`](https://github.com/CollapseLauncher/SharpHDiffPatch.Core) 打补丁器在本地应用，
并按 MD5 校验结果。若某文件没有可用补丁，或补丁应用/校验失败，则自动回退为整文件内容寻址下载；
若整个 `lastdiff` 清单不可用，则回退到经典的全文件对账。这样即便异环的 UE5 IoStore 把所有内容打包进
几个数 GB 的 `.pak`/`.ucas` 大文件，增量更新的下载量依然很小。

**更新体积的显示**也会据此计算：在估算某次更新的剩余下载量时，`PerfectWorld.Core` 会为每个存在可用补丁的变化文件
扣减相应的补丁体积，因此开始更新前显示的数值就是真实的、很小的增量传输量，而不是每个变化的数 GB 大文件的完整体积。

**启动器内容（背景、资讯、轮播、社媒）。** 插件还会直接用完美世界的线上网页数据填充 Collapse 的主页——
`pw_sdk` 没有公开的 launcher-media JSON 接口，因此每一项都取自官方启动器自身所用的来源：

* **背景图与背景视频**——取自启动器自己的 `bgimgs` 资源（`Version.ini` → `AllFiles.xml` → `config.json`）。
  静态图片与视频片段会被下载、解压并按内容寻址缓存，再以本地文件形式提供给 Collapse（默认显示图片，
  视频作为可切换的第二背景）。
* **资讯**——从公开的启动器页面解析出三栏（新闻 / 公告 / 活动）。
* **轮播 Banner**——从站点的 `gameSwiper` 数据解析（Banner 图片 + 跳转链接）。
* **社交媒体**——侧边栏条目（官方微博、官方 QQ 群、官方微信、TapTap、好游快爆、塔吉多、官方客服），
  配自带的内嵌 SVG 图标，并在可用时附上二维码图片。

以上全部只依赖 AOT 安全的基础设施（`[GeneratedRegex]`、`JsonDocument`、`System.IO.Compression`），
不引入任何第三方依赖。每一步都是尽力而为：若某个来源不可达，插件就跳过该元素，Collapse 会回退到内嵌海报。

**启动与登录（依赖官方启动器）。** 异环的账号登录并不由游戏本体负责：登录界面、反作弊初始化，以及把
登录 token 交接给游戏的整套流程，都在完美世界自己的 `NTELauncher` 里。因此直接启动 `HTGame.exe` 只会打开
游戏却不弹登录框。为了让游戏真正可玩，插件会在**安装/修复时一并下载官方启动器**——读取启动器自更新清单
（`.../publish_ob/launcher/Version.ini` → `AllFiles.xml`），再把其中约 670 个各自 zip 压缩的文件逐个下载、
校验并解压到 `NTELauncher\`（约 237 MB；清单里长度为 0 的条目带有错误的占位校验和，改用「是否为空」来校验，
而非 MD5）。之后点击**启动**会以安装根目录作为工作目录运行 `NTELauncher\NTELauncher.exe`——与官方「异环」
快捷方式完全一致——由启动器弹出登录界面并正常拉起游戏。现在「安装完成」需要同时具备游戏运行库
（`HTGameBase.dll`）和启动器入口（`NTELauncher.exe`）；此前那种没有启动器的安装会被判为*未安装*，
直到一次修复把启动器补齐。

**静默启动。** 为了不让官方启动器干扰体验，插件会在每次启动前修改启动器自己那份用户可写的设置文件
（`NTELauncher\UserData\Config\Config.ini`）——开启 `autoLogin`、`autoRun`（无需手动点「开始游戏」即自动拉起游戏）、
`quitWithGame`（游戏退出时启动器一并退出）与 `showAfterGameQuit=0`（游戏退出后不再弹回启动器）——随后**跟踪游戏进程**
而非那个瞬间退出的启动器外壳（外壳在拉起游戏后约 1 秒即退出）。由于 HTGame.exe 要经过一次 UAC 提权和 30–120 秒的自动登录才会出现，
插件会在整个启动过程中都上报**游戏正在运行**（以填补「外壳已退出、游戏尚未出现」的空档），从而让 Collapse 保持最小化、按钮状态与其他插件一致，
而不会中途跳回「开始游戏」。仅此一步即可省去多余的点击、并消除退出后启动器重新弹出的问题。
若想在约 1 分钟的自动登录启动过程中**彻底隐藏启动器窗口**，还需要**以管理员身份运行 Collapse**：`NTEGame.exe` 被强制要求管理员权限，
未提权的宿主进程会被 Windows UIPI 拦截、无法操作其窗口。提权后，启动过程中窗口会被隐藏，仅当日志显示需要交互式登录
（首次运行 / 令牌过期）或超过安全超时时才会显示出来。

**已知问题——退出游戏后按钮可能会在一段时间内仍显示「游戏正在运行」。** 异环的官方启动器掌管着游戏进程的
生命周期：当你关闭游戏时，它**并不会**立即结束 `HTGame.exe`，而是让该进程无窗口地空转，过一段时间后才回收。
这看起来是官方启动器自身的特性 / bug——它关闭游戏进程非常慢。由于插件跟踪的是真正的游戏进程（这是判断游戏是否
真正运行最可靠的信号），Collapse 会一直上报**游戏正在运行**，直到该进程最终退出，因此按钮可能需要过一会儿才会
恢复为「开始游戏」。这个状态**会自动恢复**，无需任何操作。若希望它立刻恢复，可在 Windows 系统托盘右键点击**异环**
图标并选择退出，即可立即强制结束这个残留进程。（插件刻意**不**主动提前杀掉该进程，以免误杀一个其实还在加载中的游戏。）

## 致谢与许可

* 基于 [Collapse Launcher 插件 SDK](https://github.com/CollapseLauncher/Hi3Helper.Plugin.Core) 构建。
* 以 [MIT 许可证](LICENSE) 发布。

> 本项目为非官方、粉丝制作的兼容性插件。所有游戏素材与商标归各自权利人所有，
> 与游戏的开发商 / 发行商无任何隶属或背书关系。
