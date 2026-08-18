# Hi3Helper.Plugin.PerfectWorld

为 [Collapse Launcher](https://github.com/CollapseLauncher/Collapse) 开发的插件集合，用于支持
采用 **完美世界 `pw_sdk` / PatcherSDK** 更新机制的游戏。

[English](README.md) | 简体中文

当前包含：

| 插件 | 游戏 | App ID |
| ---- | ---- | ------ |
| `Hi3Helper.Plugin.NTE` | **异环**（Neverness To Everness） | `1289` |
| `Hi3Helper.Plugin.P5X` | **Persona 5: The Phantom X** | `1264` |

---

## 目录结构

```
Hi3Helper.Plugin.PerfectWorld/
├── Hi3Helper.Plugin.Core/        # Collapse 插件 SDK（git 子模块，指向上游官方仓库）
├── SharpHDiffPatch.Core/         # HDiffPatch 打补丁器（git 子模块，内置分叉，含 >2 GB 修复）
├── Hi3Helper.PerfectWorld.Core/  # 可复用的“完美世界”发行商核心（程序集：PerfectWorld.Core）
│   ├── Utils/PatcherXml0.cs      #   PatcherXML0 清单解码器（AES-128-CBC + zlib）
│   ├── PerfectWorldGameConfig.cs #   每游戏配置 + CDN URL 构造
│   └── Management/               #   版本管理器 + 内容寻址下载与 HDiffPatch 增量引擎
├── Hi3Helper.Plugin.NTE/         # 异环的薄插件（程序集：NTE）
│   ├── Management/PresetConfig/  #   异环专属数据（App ID、CDN、可执行文件路径、启动参数）
│   ├── SelfUpdate.cs             #   插件内自更新端点（release 分支）
│   └── Properties/PublishProfiles/
├── Hi3Helper.Plugin.P5X/         # P5X 的薄插件（程序集：P5X）
│   ├── Management/PresetConfig/  #   P5X 专属数据（App ID、CDN、可执行文件路径、启动参数）
│   ├── SelfUpdate.cs             #   插件内自更新端点（release 分支）
│   └── Properties/PublishProfiles/
├── Hi3Helper.Plugin.PerfectWorld.slnx
├── CompileAOTAndShip.bat         # 一键 NativeAOT 编译 + 生成索引（可选 NTE 或 P5X）
└── Indexer.exe                   # 生成 Collapse 识别所需的插件清单
```

设计沿用官方插件仓库的思路：在**可复用的发行商核心**之上放一个只含游戏专属数据的**薄插件**。
要支持另一款 `pw_sdk` 游戏，通常只需新建一个薄插件工程，提供它的 App ID、CDN 域名和启动参数即可，
其余逻辑由 `PerfectWorld.Core` 负责。**NTE** 与 **P5X** 两个插件正是这样构建的，共享同一套核心、
下载/打补丁引擎与启动驱动。

## 环境要求

* [.NET SDK **10.0**](https://dotnet.microsoft.com/download/dotnet/10.0) 或更新版本
* 若要进行 NativeAOT 发布：安装带有 **“使用 C++ 的桌面开发”** 工作负载的 **Visual Studio 2022/2026**
  （提供本机链接器 `link.exe` 与 Windows SDK）

## 获取源码

本仓库通过 git 子模块引用 Collapse 插件 SDK 与一份打过补丁的 `SharpHDiffPatch.Core`，请**递归克隆**：

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

等价的手动发布命令（按需替换成对应游戏的工程）：

```bash
# NTE
dotnet publish Hi3Helper.Plugin.NTE/Hi3Helper.Plugin.NTE.csproj -c Release -r win-x64 -p:PublishProfile=ReleasePublish-O2
# P5X
dotnet publish Hi3Helper.Plugin.P5X/Hi3Helper.Plugin.P5X.csproj -c Release -r win-x64 -p:PublishProfile=ReleasePublish-O2
```

它会产出 Collapse 加载的原生 COM 动态库——**`NTE.dll`** 或 **`P5X.dll`**（各自导出 `TryGetApiExport`）。

## 生成发布包

Collapse 以一个由 **`Indexer.exe`** 生成的小体积发布包来分发插件：它读取发布好的插件，写出
`manifest.json`（插件名、作者、版本、图标以及逐资源的 MD5 表），把每个文件单独用 **Brotli** 压缩为
`.br`，再存进一个命名为 `<SHORT>_<版本>_API-<标准版本>_<yyyyMMdd>.zip` 的 zip，其中 `<SHORT>` 为插件短名
（`NTE` 或 `P5X`）：

```
NTE_1.0.0.0_API-0.1.5.0_20260724.zip
P5X_1.0.0.0_API-0.1.5.0_20260724.zip
```

### 本地

`CompileAOTAndShip.bat` 在发布后已经会自动运行 Indexer，因此发布包会出现在已发布 DLL 旁边，即
`Hi3Helper.Plugin.NTE\publish\Release\`。要针对任意发布目录手动（重新）生成：

```bat
Indexer.exe Hi3Helper.Plugin.NTE\publish\Release
```


## 安装到 Collapse

把发布产物（已建立索引的 `publish\Release` 文件夹，内含插件 DLL——`NTE.dll` 或 `P5X.dll`——及生成的清单）
复制到 Collapse 的插件目录，然后重启 Collapse。插件会注册游戏、图标/海报、安装/更新流程以及启动动作。

## 自动更新（插件自更新）

插件一旦安装，Collapse 就会自动保持其为最新——无需每次发版都手动重新复制。启动时，Collapse 会把一份很小的
`manifest.json` 交给插件的自更新器（`SelfUpdate.cs`，一个 `PluginSelfUpdateBase`）；它将其与本地文件比对，
当有更新的构建发布时，就下载发生变化的插件 DLL（`NTE.dll` / `P5X.dll`），并在替换前按 MD5 校验。

更新端点是本仓库 **`release` 分支**下的每插件目录，通过同一份内容的两个镜像读取（`raw.githubusercontent.com`
为主，`github.com/.../raw/...` 为兜底）：

```
https://raw.githubusercontent.com/<owner>/Hi3Helper.Plugin.PerfectWorld/release/<SHORT>/manifest.json
https://raw.githubusercontent.com/<owner>/Hi3Helper.Plugin.PerfectWorld/release/<SHORT>/<SHORT>.dll
```

其中 `<SHORT>` 为 `NTE` 或 `P5X`。这些目录由 **Release Plugin** 工作流自动填充——它在每次发版时把新构建的
`manifest.json` + 插件 DLL 推送到 `release` 分支。端点在每个插件的 `SelfUpdate.cs` 中硬编码，因此若你 fork
本仓库，应把它们改指向你自己的 `release` 分支。

## 原理

完美世界 PatcherSDK 的文件清单以 `PatcherXML0` 加密分发：

* 主体 = `AES-128-CBC( zlib.deflate(xml) + PKCS7 )`，前面带 12 字节魔数和解压后大小。
* **密钥** = `"<appId>@Patcher"` 用 `'0'` 右补齐到 16 字节（异环 → `1289@Patcher0000`，P5X → `1264@Patcher0000`）。
* **IV**  = `"PatcherSDK"` 用 `'0'` 右补齐到 16 字节（`PatcherSDK000000`）。

`PerfectWorld.Core` 会拉取 `config.xml`，下载并解密带版本号的 `ResList.bin.zip`，再从
`.../<branch>/Res/<md5[0]>/<md5>.<filesize>` 下载每个内容寻址文件并校验 MD5。每款游戏之间只有 App ID、CDN
域名和资源分支不同（异环 → `yhcdn*.wmupd.com` 上的 `publish_PC`；P5X → `nsywl-client-dev*.wmupd.com` 上的
`CN_OB_OFFICIAL`）。

**增量更新（HDiffPatch）。** 更新时，`PerfectWorld.Core` 还会拉取并解密带版本号的 `lastdiff.bin` 补丁清单。
对每个发生变化的文件，它会查找一个二进制差分补丁——其源 MD5 与本地文件一致、目标 MD5 与目标文件一致；
若存在这样的补丁（且体积小于整文件下载），就只下载这个很小的 `HDIFF13` 补丁块，用仓库内置的
[`SharpHDiffPatch.Core`](https://github.com/Vohrt/SharpHDiffPatch.Core) 分叉（一个 git 子模块）在本地应用，
并按 MD5 校验结果。正是这个分叉让大文件更新保持很小：上游打补丁器会把每个补丁的输出缓冲进单个内存流，
一旦目标文件大于约 2 GiB 就会抛异常——于是在修复之前，每个超过该大小的变化文件（异环的 `.pak`/`.ucas` 分块
往往 4–7 GB）都会悄悄回退为整包重下，把约 160 MB 的增量更新硬生生变成约 17 GB。该分叉对输出缓存做了滑窗处理，
使这些数 GB 的大文件能在有限内存（约 256 MiB）下正确打补丁。若某文件没有可用补丁，或补丁应用/校验失败，则自动
回退为整文件内容寻址下载；若整个 `lastdiff` 清单不可用，则回退到经典的全文件对账。这样即便异环的 UE5 IoStore
把所有内容打包进几个数 GB 的 `.pak`/`.ucas` 大文件，增量更新的下载量依然很小。

**更新体积的显示**也会据此计算：在估算某次更新的剩余下载量时，`PerfectWorld.Core` 会为每个存在可用补丁的变化文件
扣减相应的补丁体积，因此开始更新前显示的数值就是真实的、很小的增量传输量，而不是每个变化的数 GB 大文件的完整体积。

**启动器内容（背景、资讯、轮播、社媒）。** 插件还会直接用完美世界的线上网页数据填充 Collapse 的主页
（异环取自 `yh.wanmei.com`，P5X 取自 `p5x.wanmei.com`）——`pw_sdk` 没有公开的 launcher-media JSON 接口，
因此每一项都取自官方启动器自身所用的来源：

* **背景图与背景视频**——取自启动器自己的 `bgimgs` 资源（`Version.ini` → `AllFiles.xml` → `config.json`）。
  静态图片与视频片段会被下载、解压并按内容寻址缓存，再以本地文件形式提供给 Collapse（默认显示图片，
  视频作为可切换的第二背景）。
* **资讯**——从公开的启动器页面解析出三栏（新闻 / 公告 / 活动）。
* **轮播 Banner**——从站点的 `gameSwiper` 数据解析（Banner 图片 + 跳转链接）。
* **社交媒体**——侧边栏条目（官方微博、官方 QQ 群、官方微信、TapTap、好游快爆、塔吉多、官方客服），
  配自带的内嵌 SVG 图标，并在可用时附上二维码图片。

以上全部只依赖 AOT 安全的基础设施（`[GeneratedRegex]`、`JsonDocument`、`System.IO.Compression`），
不引入任何第三方依赖。每一步都是尽力而为：若某个来源不可达，插件就跳过该元素，Collapse 会回退到内嵌海报。

**游戏拉起（依赖官方启动器 + 静默启动）。** 对**两款游戏**而言，账号登录都不由游戏本体负责——登录界面、
反作弊初始化，以及把登录 token 交接给游戏的整套流程，都在完美世界自己的启动器程序里，因此直接启动裸游戏
可执行文件只会打开游戏却不弹登录框。为此插件会在**安装/修复时一并安装官方启动器，并通过它来启动**；
于是「安装完成」需要同时具备游戏运行库*和*启动器入口，此前那种没有启动器的安装会被判为*未安装*，直到一次修复
把启动器补齐。随后由一套共享的、配置驱动的**静默启动**让启动器不干扰体验：在每次启动前，插件会修改启动器自己
那份用户可写的设置文件（`…\UserData\Config\Config.ini`），令其自动登录、自动拉起游戏并随游戏一同退出
（退出后不再弹回）；同时**跟踪真正的游戏进程**而非那个瞬间退出的启动器外壳（外壳在拉起游戏后约 1 秒即退出）。
启动器会以**正常窗口**方式启动，在这段短暂的自动登录启动过程中保持可见；在默认的自动点击路径下，一旦「开始游戏」
按钮被按下，启动器便会自行最小化到托盘——与官方启动器在手动点击后的行为完全一致——并随游戏一同退出。

两款游戏的具体差异：

* **异环**（UE5，`HTGame.exe`）。启动器取自其自更新清单（`.../publish_ob/launcher/Version.ini` → `AllFiles.xml`），
  解压到 `NTELauncher\`（约 670 个各自 zip 压缩的文件，约 237 MB；清单里长度为 0 的条目带有错误的占位校验和，
  改用「是否为空」而非 MD5 来校验）；启动会以安装根目录作为工作目录**直接运行** `NTELauncher\NTEGame.exe`——
  而非那个薄壳 `NTELauncher.exe`（它只是定位补丁器后再拉起 `NTEGame.exe /launcher /directly`，插件把这一命令行
  逐字复刻并省去这个多余的中间进程）。「安装完成」需要同时具备 `HTGameBase.dll` 和 `NTEGame.exe`。默认情况下插件
  会通过 DLL 注入**自动点击**启动器的「开始游戏」按钮（见下文「启动器自动点击」）；若该方式不可用，则回退到传入
  `/autoplay`——它能免手动点击自动进入游戏，但会让 `NTEGame.exe` 跳过其进程内的资源更新器。由于默认的自动点击路径
  会运行该进程内更新器，插件只安装本体，并把所有语音包**延迟**到游戏内按需下载（详见下文「按需语音」）。
* **P5X**（Unity IL2CPP，`client\pc\P5X.exe`）。插件会直接运行 `P5XLaunch\P5XGame.exe`。默认情况下通过 DLL 注入
  **自动点击**「开始游戏」（见下文「启动器自动点击」）；回退方式为 `/launcher /directly /autoplay`。无论哪种方式，
  关键都在于那一次点击：P5X 的启动器是否自动进入游戏由*服务端*控制（`canAutoPlay=false`），仅改 INI 并不足够，
  否则会停在「开始游戏」按钮处——真实的按钮点击（自动点击）或 `/autoplay` 覆盖都能越过它。「安装完成」需要同时
  具备 `GameAssembly.dll` 和 `P5XGame.exe`。

**按需语音（异环）——不预装任何语音，游戏在初始化时自行下载所需语音。** 插件只安装本体，并把所有
按语言划分的语音包（`pakchunk101/102/103/104` = 中 / 日 / 英 / 韩，位于 `Content/TagPatchPaks/`，由
`DeferredContentPathMarkers` 匹配、`DeferredContentKeepMarkers` 留空）全部**延迟**下载，因此异环的安装体积比
一次性打包全部语言小若干 GB。随后由 `NTEGame.exe` 的初始化流程按当前系统语言选择并下载对应语音——这是最合适的
默认行为，而不是在所有系统上都强装中文。之所以可行，是因为**默认的自动点击启动路径**（见下文）会让 `NTEGame.exe`
运行其正常的进程内资源/语音对账——正是 `/autoplay` 会跳过的那一步——从而按需下载所选语言。为避免该对账陷入
「更新失败」死循环，插件会在每次安装/更新后伪造原生 `PatcherSDK` 状态文件（`config.xml` / `ResList.xml` /
`tmp\client.xml`，即 `WritePatcherState = true`），把本体记录为已安装、语音记录为尚未安装（即空的
`<BaseVerson>`）；否则补丁器会看到本地版本 0.0，认为整个版本缺失而陷入死循环。（早期版本曾把这种延迟与去掉
`/autoplay` 搭配，但由于 `/autoplay` 会跳过进程内更新器，语音从未被对账、游戏陷入「更新失败」死循环，因而被回退、
改为打包全部语音。当前基于 DLL 注入的自动点击路径会运行该更新器，按需语音功能因而得以恢复。）

**启动器自动点击（DLL 注入）——默认的启动方式。** 传入 `/autoplay` 虽能自动进入游戏，却有副作用：在异环上它会让
`NTEGame.exe` 跳过其进程内的资源更新器（即上文的语音隐患）。为避免它，插件的**默认**启动方式是以程序化方式点击
启动器真正的「开始游戏」按钮：

* **注入了什么。** 一个极小的原生辅助库 **`PwAutoClick.dll`**（x64，静态链接 CRT，仅依赖 `KERNEL32.dll`）。其源码位于
  `Hi3Helper.PerfectWorld.Core/Native/PwAutoClick/`；编译好的 DLL 作为资源嵌入 `PerfectWorld.Core.dll`，启动时释放到
  `%TEMP%\CollapsePwPlugin\`，再经 `CreateRemoteThread` + `LoadLibraryW` 载入官方启动器（`NTEGame.exe` / `P5XGame.exe`）。
* **如何完成点击。** 两个启动器都是 Qt 5.15.17 应用，其开始按钮运行 QML 槽
  `BackgroudStageScheduler.gameActionBtnClicked()`（厂商拼写——第二个 *n* 确实缺失）。`BackgroudStageScheduler` 是一个
  通过 `QQmlContext::setContextProperty(const QString&, QObject*)` 暴露给 QML 的 C++ `QObject`。该 DLL 通过 IAT 挂钩这个
  Qt 导出来捕获对象指针；随后，一旦启动器日志出现 `all ready, wait for start game`（插件会读取启动器日志并置位一个
  DLL 正在等待的具名 `Event`），它便调用 `QMetaObject::invokeMethod(obj, "gameActionBtnClicked", Qt::QueuedConnection)`
  在启动器的 GUI 线程上触发点击。这样启动器会走它**正常**的流程——包含资源检查——与手动点击完全一致，不带任何
  `/autoplay` 捷径。
* **需要管理员权限。** 官方启动器被强制以管理员运行，因此注入它也需要 Collapse 同样以管理员运行；否则会跳过自动点击，
  改用 `/autoplay` 回退。
* **回退阶梯（绝不会让你卡住）。** 若自动点击无法启用（Collapse 非管理员，或嵌入的 DLL 无法准备），插件改用旧的
  `/autoplay` 路径。若*尝试*注入但失败，则启动器以**不带** `/autoplay` 的方式启动并保持可见，好让你自己点击
  「开始游戏」。若注入成功但点击始终未触发，启动器会停留在按钮界面——直接手动点击即可。
* **诊断信息。** 该 DLL 会写入 `%TEMP%\CollapsePwPlugin\PwAutoClick.log`（符号解析、挂钩数量、对象捕获、`invokeMethod`
  结果）；插件则以 `[PWAutoClick]` 前缀记录注入状态。两者均可安全删除。
* **如何关闭 / 回退。** 在对应游戏的预设（`NteCnPresetConfig.cs` / `P5xCnPresetConfig.cs`）中设 `LauncherAutoClickEnabled =
  false` 并重新编译，即可强制使用已验证的 `/autoplay` 行为。用户自定义的启动参数也会自动关闭自动点击（你的参数会被
  原样使用）。
* **编译原生 DLL。** 普通的插件编译**无需** C++ 工具链——已提交的 `Native/PwAutoClick.dll` 会被原样嵌入。若你修改了
  C++ 源码，请在具备 *x64 Native Tools* 环境的命令行中运行 `Hi3Helper.PerfectWorld.Core/Native/PwAutoClick/build.ps1`
  重新生成它，然后再编译插件。


## 致谢与许可

* 基于 [Collapse Launcher 插件 SDK](https://github.com/CollapseLauncher/Hi3Helper.Plugin.Core) 构建。
* 以 [MIT 许可证](LICENSE) 发布。

> 本项目为非官方、粉丝制作的兼容性插件。所有游戏素材与商标归各自权利人所有，
> 与游戏的开发商 / 发行商无任何隶属或背书关系。
