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
| 实用工具 | Uninstall Tool 3.8.2 / AltSnap 1.68（随仓库内置） | 可用 |
| 贴片 | 内存占用 / 设备电量 | 可用 |

## 功能

### 内存清理（对齐 Mem Reduct）
- 区域可选：工作集、系统文件缓存、已修改列表、备用列表、备用优先级 0、已修改文件缓存、注册表缓存、合并内存列表（默认掩码 `0xE7`）
- 自动清理：按阈值（50–99%）与间隔（5–720 分钟）触发，阈值与定时任一满足即执行
- 免 UAC：以管理员身份再跑一遍本进程创建两个最高权限计划任务（`ToolDock_Clean` / `ToolDock_CleanAuto`），之后静默清理；掩码运行时从插件设置读取，改配置无需重建任务
- 手动入口：托盘菜单、界面按钮、全局热键、命令行 `ToolDock.Agent.exe /clean [mask]`
- 结果气泡 / 日志开关、清理前确认、恢复 Mem Reduct 默认值
- 任务栏内存贴片：显示占用百分比（≥85% 红 / ≥70% 琥珀 / 其余绿），点击贴片立即清理，右键菜单

### 外设电量显示（基于 VigilWatt.Core）
- 设备：BLE GATT 电池服务、经典蓝牙、2.4G HID 多品牌插件（罗技、雷柏等）
- 设备外观自定义（重命名、图标）、忽略/屏蔽设备、低电量提醒
- 统计（24 小时 / 7 天 / 30 天折线）、历史记录与清理、采样/阈值/日志级别设置
- 诊断采证：按物理设备分组选择 HID 接口，探测能力后按步骤采集，导出离线包（只读句柄，不向设备写报文）
- 任务栏电量贴片：按设备/最低电量显示，可分屏，双击打开主界面

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

# 1) 常驻 Agent（AOT）
dotnet publish src/ToolDock.Agent/ToolDock.Agent.csproj -c Release -r win-x64 -o publish

# 2) 界面进程
dotnet publish src/ToolDock.App/ToolDock.App.csproj -c Release -o publish/app

# 3) 运行
publish/ToolDock.Agent.exe
```

发布目录结构：

```
publish/
  ToolDock.Agent.exe
  app/
    ToolDock.exe
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
| `%APPDATA%\ToolDock\battery-history.db` | 电量历史（SQLite） |
| `%APPDATA%\ToolDock\logs\` | Agent / 界面 / 贴片日志 |
| `%LOCALAPPDATA%\ToolDock\captures\` | 诊断采证导出包 |

## 第三方

- [VigilWatt.Core](https://github.com/evren-oct/vigil-watt)（MIT）：外设电量数据层，`Tools/vigil-watt`
- Mem Reduct（GPL-3.0）：仅作行为参考，未链接其代码
 - Uninstall Tool 3.8.2：完整目录随仓库内置于 `assets/tools/uninstalltool/`（含 `unins000.*`），仅限授权使用
- AltSnap 1.68（GPL-3.0，https://github.com/RamonUnch/AltSnap）：MSVC x64 构建，内置于 `assets/tools/altsnap/`

## 许可

GPL-3.0（见 [LICENSE](LICENSE)）。
