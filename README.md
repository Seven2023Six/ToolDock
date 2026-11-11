# ToolDock 工具坞

[![build](https://github.com/Seven2023Six/ToolDock/actions/workflows/build.yml/badge.svg)](https://github.com/Seven2023Six/ToolDock/actions/workflows/build.yml)

Windows 托盘**工具坞**：常驻 Agent + 按需启动的 WPF 界面，把系统工具和第三方程序插件式收进同一个入口。

- 常驻进程为 **NativeAOT** 编译的 Agent（托盘 / 命名管道 / 设置唯一写者），空闲内存约几 MB
- 界面进程是 **.NET 8 WPF**，按需冷启动，退出后不占资源
- 工具分三类：**内置工具**（随工具箱实现）、**实用工具**（随包分发的第三方程序）、**任务栏贴片**（常驻读数）
- 新工具按同一套结构接入：一侧写插件/页面，另一侧在 `MainWindow.ShowPage` 与导航里登记，不需要改动现有工具

当前收录：

| 分类 | 工具 | 状态 |
| --- | --- | --- |
| 内置工具 | 内存清理（对齐 Mem Reduct） | 可用 |
| 内置工具 | 外设电量显示（VigilWatt 全功能） | 可用 |
| 内置工具 | 窗口拖拽（内置引擎 1.0，基于 AltSnap 1.68 内核） | 可用 |
| 内置工具 | 输入法状态提示（内置引擎 1.0，基于 InputTip 3.6.12） | 可用 |
| 内置工具 | MAS 激活工具（内置官方 MAS_AIO.cmd 3.12，离线即开即用） | 可用 |
| 实用工具 | Uninstall Tool 3.8.2（随仓库内置） | 可用 |
| 贴片 | 内存占用 / 设备电量 | 可用 |

## 功能

### 内存清理（对齐 Mem Reduct）
- 区域可选：工作集、系统文件缓存、已修改列表、备用列表、备用优先级 0、已修改文件缓存、注册表缓存、合并内存列表（默认掩码 `0xE7`）
- 自动清理：按阈值（50–99%）与间隔（5–720 分钟）触发，阈值与定时任一满足即执行
- 免 UAC：以管理员身份再跑一遍本进程作为**常驻提权助手**（`--clean-daemon`，命名管道通信），首次启用授权一次，之后自动/手动清理全程静默；助手不写计划任务，避免被安全软件当可疑启动项删除
- 降级策略：提权助手不可用时，自动清理改为逐进程回收工作集（无需管理员权限），不会静默失败
- 手动入口：托盘菜单、界面按钮、全局热键、命令行 `ToolDock.Agent.exe /clean [mask]`
- 结果气泡 / 日志开关、清理前确认、恢复 Mem Reduct 默认值
- 任务栏内存贴片：显示占用百分比（≥85% 红 / ≥70% 琥珀 / 其余绿），点击贴片立即清理，右键菜单

### 外设电量显示（基于 VigilWatt.Core）
- 设备：BLE GATT 电池服务、经典蓝牙、2.4G HID 多品牌插件（罗技、雷柏等）
- 设备外观自定义（重命名、图标）、忽略/屏蔽设备、低电量提醒
- 统计（24 小时 / 7 天 / 30 天折线）、历史记录与清理、采样/阈值/日志级别设置
- 诊断采证：按物理设备分组选择 HID 接口，探测能力后按步骤采集，导出离线包（只读句柄，不向设备写报文）
- 任务栏电量贴片：按设备/最低电量显示，可分屏，双击打开主界面

### 窗口拖拽（内置引擎 1.0，基于 AltSnap 1.68 内核）
- 官方 `hooks.dll` 引擎 + 去托盘宿主 `altsnap_host.dll` 直接加载进 Agent，进程内运行；没有 AltSnap.exe、没有第二个托盘图标
- 设置页按原版分类：常规 / 鼠标 / 键盘 / 黑名单 / 高级（含性能、布局区域、原始 ini）
- 覆盖上游全部配置键：鼠标动作矩阵（Alt/Ctrl/Shift/Win 组合）、滚轮动作、Aero/SmartAero、StickyResize、MMMaximize、AutoSnap、区域吸附、黑名单、阈值、性能参数等
- “原始配置文件”可直接编辑完整 `AltSnap.ini`（含自定义 `[Zone*]` 布局、每动作黑名单等）；应用后引擎自动重启读入
- 引擎自带的动作菜单（Ctrl+Alt+左键）已加载中文语言包，菜单项全部为中文

### 输入法状态提示（内置引擎 1.0，基于 InputTip 3.6.12）
- 原生 C# 重写并编译进 Agent：没有 AutoHotkey.exe、没有独立进程与第二个托盘
- 6 种状态：中文 `CN` · 英文 `EN` · 大写锁定 `Caps` · 英文键盘 `US` · 日文键盘 `JP` · 韩文键盘 `KR`
- 5 种提示方案：鼠标样式（整套系统光标）· 悬浮小窗（窗口/屏幕 21 个位置）· 光标符号 · 鼠标符号 · 窗口边框（普通窗口走 DWM，最大化/全屏走四边帧）
- 符号形态：图片（PNG）/ 方块 / 文字；支持字号、字重、颜色、透明度、圆角、边缘样式、每状态偏移、每屏幕偏移
- 规则调度：窗口规则（进程/类名/标题正则，文本监控、热键序列监控、空闲计时器）、热键规则（AutoHotkey 写法）、悬浮小窗/边框/符号的显示隐藏名单、光标捕获模式与窗口偏移规则
- 行为：状态切换（IME / LShift / RShift / Ctrl+Space / CapsLock）、键盘布局切换（CN/US/JP/KR 及日文/韩文子模式）、暂停/恢复/重启/停用、窗口置顶、状态码与捕获模式提示
- 其他：导出当前状态文件、今日按键统计、自定义托盘提示、恢复默认配置

### MAS 激活工具（内置官方脚本 3.12）
- 内置官方 `MAS_AIO.cmd`（Microsoft Activation Scripts 3.12，SHA-256 已按官方发布清单校验），随包分发、离线即开即用
- 「打开激活菜单」复刻官方引导流程：写临时文件（`@::: <guid>` 头）→ `cmd /c "...cmd" -el` 管理员运行 → 只有一次 UAC，与 `irm https://get.activated.win | iex` 行为一致
- 主菜单已汉化：启动时对内存副本应用中文补丁（仅显示文案，激活逻辑与官方一致），汉化版持久存放在安装目录 `tools/mas/MAS_AIO_CN.cmd` 并复用——放在安装目录是因为杀软会删除 %TEMP% 里的修改版变体（实测卡巴秒删，官方原版不受影响）
- 「检查更新」从官方引导脚本解析最新发布源与哈希，多源随机尝试拉取并校验后替换本地副本（本地副本在 `%APPDATA%\ToolDock\mas\`，汉化版随之自动重生成）
- 菜单功能与官方完全一致：HWID / Ohook / TSforge / KMS38 / 在线 KMS / 激活疑难解答
- 注意：部分杀毒软件会将激活脚本误报为风险程序（官方仓库多次被误报），被拦截时请将 ToolDock 加入信任区；工具检测到控制台秒退时会给出明确的信任区指引

## 界面

> 截图占位：首页 / 内置工具 / 内存清理 / 外设电量 / 实用工具

## 架构

```
ToolDock.Agent (NativeAOT, 常驻)
  ├─ 托盘图标 / 菜单 / 状态行 / 全局热键
  ├─ 命名管道 \\.\pipe\tooldock-agent（定义在 ToolDock.Core）
  ├─ 插件宿主（内存清理；设置由 Agent 独占写盘）
  └─ 提权模式：--install-task / --uninstall-task / --taskclean-* / --clean

ToolDock.App (WPF, 按需启动)
  ├─ 主窗口（首页 / 内置工具 / 实用工具 / 自定义 / 设置 / 关于）
  ├─ 外设电量页（设备 / 统计 / 历史 / 诊断 / 电量设置）
  ├─ 任务栏贴片（原生分层窗口 SetParent 到 Shell_TrayWnd）
  └─ 与 Agent 的管道客户端（AgentLink）

ToolDock.Core            共享契约、设置、内存清理核心、原子写文件
ToolDock.InputTip        输入法状态提示引擎（原生 C#，Win32 分层窗口 + GDI，无 UI 依赖）
VigilWatt.Core (MIT)     外设电量数据层（BLE/经典蓝牙/2.4G HID 插件、采集引擎）
```

## 环境要求

- Windows 10 1809+ / Windows 11，x64
- 构建：.NET SDK 8.0+（Agent/Helper 为 NativeAOT，需要 C++ 生成工具与 Windows SDK）
- 运行：界面进程需要 **.NET 8 Desktop Runtime**；Agent 为原生可执行文件，无需运行时

## 构建

```bash
# 依赖：外设电量数据层（MIT）以子模块引入
git clone --recursive https://github.com/Seven2023Six/ToolDock.git
# 已 clone 过但没有子模块时：
git submodule update --init --recursive

# 一键发布（停进程 → 发布 Agent/App → 校验产物并打印 SHA256；加 -Start 发布后拉起）
powershell -ExecutionPolicy Bypass -File scripts/build-release.ps1 -Start

# 单元测试
dotnet test tests/ToolDock.Tests/ToolDock.Tests.csproj -c Release

# 冒烟（握手 + getSettings/getAltSnap/getInputTip）
powershell -ExecutionPolicy Bypass -File scripts/smoke.ps1
```

手工发布（等价于脚本前两步）：

```bash
# 1) 常驻 Agent（AOT）
dotnet publish src/ToolDock.Agent/ToolDock.Agent.csproj -c Release -r win-x64 -o publish

# 2) 界面进程
dotnet publish src/ToolDock.App/ToolDock.App.csproj -c Release -o publish/app

# 3) 运行
publish/ToolDock.Agent.exe
```

开发流程、提权模式、编码/杀软环境契约与调试脚本见 [DEV.md](DEV.md)。

发布目录结构：

```
publish/
  ToolDock.Agent.exe
  app/
    ToolDock.exe
    tools/mas/MAS_AIO.cmd     # MAS 激活工具内置脚本（assets/tools/mas 随包分发）
    tools/uninstalltool/   # 可选：第三方实用工具，不随仓库分发
```

### 打包 zip

```bash
python scripts/package-win-x64.py        # 产物 dist/ToolDock-<版本>-win-x64.zip
```

## 数据与配置

| 路径 | 内容 |
| --- | --- |
| `%APPDATA%\ToolDock\settings.json` | 主题、材质、开机自启等（Agent 写） |
| `%APPDATA%\ToolDock\plugindata\memclean.json` | 内存清理设置（掩码/自动策略/热键/免 UAC） |
| `%APPDATA%\ToolDock\battery-settings.json` | 外设电量设置 |
| `%APPDATA%\ToolDock\ui-settings.json` | 工具启用状态、贴片开关 |
| `%APPDATA%\ToolDock\altsnap\AltSnap.ini` | 窗口拖拽完整配置（AltSnap 原生 ini，UTF-16） |
| `%APPDATA%\ToolDock\inputtip\settings.json` | 输入法状态提示全部设置与规则（Agent 重载后生效） |
| `%APPDATA%\ToolDock\inputtip\assets\` | 自定义光标（cursor）与符号图片（symbol）；首次启动从 `tools/inputtip/assets` 复制默认资源 |
| `%APPDATA%\ToolDock\mas\MAS_AIO.cmd` | MAS 激活工具运行时脚本（首启从 `tools/mas` 复制；「检查更新」替换此文件） |
| `%APPDATA%\ToolDock\battery-history.db` | 电量历史（SQLite） |
| `%APPDATA%\ToolDock\logs\` | Agent / 界面 / 贴片日志 |
| `%LOCALAPPDATA%\ToolDock\captures\` | 诊断采证导出包 |

## 第三方

- [VigilWatt.Core](https://github.com/evren-oct/vigil-watt)（MIT）：外设电量数据层，`Tools/vigil-watt`
- Mem Reduct（GPL-3.0）：仅作行为参考，未链接其代码
- Uninstall Tool 3.8.2：完整目录随仓库内置于 `assets/tools/uninstalltool/`（含 `unins000.*`），仅限授权使用
- [Microsoft Activation Scripts](https://github.com/massgravel/Microsoft-Activation-Scripts) 3.12（`MAS_AIO.cmd`）：MAS 激活工具的功能本体，随仓库内置于 `assets/tools/mas/`，启动流程复刻官方 `irm https://get.activated.win | iex` 引导（哈希校验 + 临时文件 + `-el` 自提权）
- AltSnap 1.68（GPL-3.0，https://github.com/RamonUnch/AltSnap）：窗口拖拽引擎内核；工具箱内置引擎版本 1.0。`hooks.dll` 由上游 `hooks.c` 原样编译；`altsnap_host.dll` 为去托盘/去配置窗口的宿主改造，源码与构建说明见 [`native/altsnap`](native/altsnap/README.md)。GPL-3.0 全文见 `assets/tools/altsnap/License.txt`
- InputTip 3.6.12（AGPL-3.0，https://github.com/abgox/InputTip）：输入法状态提示的行为与默认资源来源；工具箱内为原生 C# 重写（`src/ToolDock.InputTip`），默认光标/符号资源随包分发，未包含 AutoHotkey 运行时

## 许可

GPL-3.0（见 [LICENSE](LICENSE)）。
