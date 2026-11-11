# 开发与验证指南（DEV）

本文件记录工具箱的开发流程、运行契约与"踩过的坑"。改动涉及 Agent/App 协作、提权或第三方脚本时，先读一遍对应章节。

## 1. 目录与职责

| 路径 | 职责 |
| --- | --- |
| `src/ToolDock.Agent` | 常驻 Agent（NativeAOT，WinExe + 托盘 + 命名管道服务端）。后台插件编译进本工程：`Plugins/*.cs` |
| `src/ToolDock.App` | 按需 WPF 界面（关窗即退），管道客户端；`Services/Mas/` 为 MAS 工具实现 |
| `src/ToolDock.Core` | 设置模型、清理核心、管道帧编解码、日志轮转 |
| `src/ToolDock.InputTip` | 输入法提示引擎（原生 C#，无 UI 依赖） |
| `src/ToolDock.Abstractions` | 插件接口（`IBackgroundPlugin` / `IAgentContext`） |
| `tests/ToolDock.Tests` | xunit 单测（Core / InputTip / MAS 纯逻辑） |
| `scripts/` | `build-release.ps1`、`smoke.ps1`、`package-win-x64.py` |
| `scripts/dev/` | 日常调试辅助脚本（UI 自动化、截图、内存读数） |

## 2. 构建、测试与发布

```powershell
# 单元测试
dotnet test tests/ToolDock.Tests/ToolDock.Tests.csproj -c Release

# 一键发布（会先停掉正在运行的 Agent/App，发布后校验关键文件并打印 SHA256）
powershell -ExecutionPolicy Bypass -File scripts/build-release.ps1

# 发布并打包 zip
powershell -ExecutionPolicy Bypass -File scripts/build-release.ps1 -Start

# 只发布不打包、发布后拉起
powershell -ExecutionPolicy Bypass -File scripts/build-release.ps1 -SkipPackage -Start

# 冒烟：握手 + getSettings/getAltSnap/getInputTip（需先关掉已连接的界面窗口）
powershell -ExecutionPolicy Bypass -File scripts/smoke.ps1
```

发布产物：`publish/ToolDock.Agent.exe` + `publish/app/ToolDock.exe` + `publish/app/tools/*`。

版本号唯一来源是根目录 `Directory.Build.props`（当前 0.3.0）；`ToolDock.InputTip` 在自身 csproj 显式覆盖为引擎版本 1.0.0。

## 3. Agent 的提权副本模式（同一 exe 带参数再跑一遍）

| 参数 | 用途 | 何时使用 |
| --- | --- | --- |
| `--clean-daemon` | 常驻提权清理助手（命名管道客户端，向 Agent 的管道服务端反向接入） | 「免 UAC 提权」开启时启动一次，之后静默清理 |
| `--clean <mask> -out <file>` | 一次性提权清理，结果写临时文件 | 未开免 UAC 时的手动清理 |
| `--taskclean-manual` / `--taskclean-auto` | 旧版计划任务兼容入口 | 仅历史任务残留会调用 |
| `/clean [mask]` | 命令行清理（需管理员） | 脚本/手工排障 |

这些模式必须在单实例检查**之前**处理（常驻 Agent 占着互斥量，提权副本不能因"已有实例"退出）。

## 4. 管道协议

- 帧格式：4 字节小端长度 + UTF-8 JSON；编解码唯一出口 `ToolDock.Core/Pipe/PipeFrame.cs`。
- 协议版本：`ToolDock.Core/Pipe/PipeProtocol.cs`，握手时 `welcome.ver` 回传；新增字段必须保持**未知成员可忽略**（JSON 反序列化默认行为，单测已覆盖）。
- 心跳：App 每 10 秒发 `{"t":"ping"}`，Agent 回 `pong`；35 秒无任何回包视为断线，App 每 10 秒尝试重连一次。
- 写纪律：UI 线程禁止同步等待管道 IO；所有发送走后台线程 + `_writeGate` 串行。

## 5. 配置归属（单一事实源）

| 文件 | 写者 | 内容 |
| --- | --- | --- |
| `settings.json` | **仅 Agent** | 主题、材质、自启动 |
| `plugindata/memclean.json` | Agent 插件 | 内存清理掩码/自动策略/免 UAC/热键 |
| `ui-settings.json` | App | 工具启用状态、贴片开关 |
| `battery-settings.json` | App/电量服务 | 电量工具设置 |
| `altsnap/AltSnap.ini` | App（保存时） | 窗口拖拽完整配置 |
| `inputtip/settings.json` | App（保存时） | 输入法提示设置与规则 |
| `mas/MAS_AIO.cmd` | App（检查更新时） | MAS 官方脚本运行时副本 |

规则：**Agent 连接时，App 不得写 `settings.json` 的内存清理字段**（否则与插件配置分叉）；只有独立模式（Agent 不在）才写本地兜底。

## 6. 环境契约（重要，踩过的坑）

### 杀毒软件（卡巴斯基实测）

- 会**秒删 `%TEMP%` 里任何修改过的 MAS 脚本**（官方原版哈希在白名单里不碰）。因此汉化脚本持久化在安装目录 `tools/mas/MAS_AIO_CN.cmd`，写入后 300ms 内校验是否被删，被删则弹信任区指引。
- 网页防护会拦截 `get.activated.win`（返回 HTML 拦截页）。「检查更新」失败时提示加信任。
- 发布时可能长时间占用 `publish/ToolDock.Agent.exe`。`build-release.ps1` 会先停进程；仍被锁时可临时用别名构建（`ToolDockAgentAssemblyName`）或稍后重试。

### 编码

- **PowerShell 5.1 读无 BOM 的 UTF-8 中文脚本会按 GBK 解析**，可能吞掉引号/换行导致语法错误。写 `.ps1` 时若含中文，务必加 UTF-8 BOM（或用纯 ASCII）。
- 控制台批处理（MAS 汉化）用 **GBK(cp936)** 写入，与默认控制台代码页一致；**不要**用 `chcp 65001` + UTF-8——cmd 按块解析会错位吞行（实测 `[6]`/`[E]` 两行被执行成命令）。

### MAS 启动契约

- 外层 `cmd /c "...MAS_AIO_CN.cmd" -el` 以 runas 启动后，**常在 1-3 秒内退出（退出码 0）**：这是 MAS 自身的 QuickEdit 重置重启设计，不是失败。
- 真正的菜单在重启后的控制台里出现（约 1-5 秒后），标题为 `管理员: MAS 激活工具 3.12`。
- 启动器监视 30 秒；若外层秒退且菜单窗口始终未出现，才判定为杀软拦截并提示。

### UAC

- 本机策略 `ConsentPromptBehaviorAdmin=0`（自动同意），`runas` 不会弹框；其他机器上会正常弹 UAC，属预期。

## 7. 日志

- 位置：`%APPDATA%\ToolDock\logs\`，按天分文件；单文件超过 4MB 自动轮转为 `.log.1`（`ToolDock.Core.LogRotation`）。
- 标签：`[agent]`、`[pipe]`、`[memclean]`、`[cleaner]`、`[altsnap]`、`[inputtip]`、`[elevated]`、`[mas]`。
- 界面「关于」页提供「打开日志目录」按钮。

## 8. 性能基线（本机参考）

采样条件：Agent 空闲（无用户操作）、输入法提示停用、AltSnap 引擎运行、清理助手未启动，12 逻辑核。

| 指标 | 数值 |
| --- | --- |
| Agent 空闲 CPU | ≈0%（10 秒采样低于 0.01%） |
| Agent 工作集 | 5.1 MB |
| 界面进程 | 按需启动，关窗即退 |

输入法提示启用后会额外运行 20ms 轮询与键盘钩子，CPU 随输入活跃度变化；如需评估，可在启用后重复上述采样并对比。

## 9. 单测覆盖范围

- 管道帧往返、超长/截断帧、未知 JSON 字段容忍
- 清理核心掩码契约、非提权工作集回收路径
- MAS 汉化补丁命中/版本解析/GBK 往返、引导脚本解析、SHA-256 向量
- 输入法设置归一化、规则引擎正则与屏幕偏移解析

新增纯逻辑（尤其是协议、配置解析、补丁表）时，同步补一条用例。

## 10. 调试辅助（scripts/dev）

| 脚本 | 用途 |
| --- | --- |
| `ui_nav.ps1` | UIA 导航：按名称选择侧栏/点击按钮 |
| `click_button.ps1` | 按 UIA 名称点击按钮（坐标点击，规避 InvokePattern 缺失） |
| `click_by_name_ancestor.ps1` | 按名称找元素并向上找可 Invoke 的祖先（页头按钮无自动化名时用） |
| `shot_screen.ps1` | 截全屏到 `scratch/screen.png` |
| `memload.ps1` | 读取当前内存占用百分比 |

注意：这些脚本保持**纯 ASCII**，避免 PowerShell 5.1 的编码问题；中文参数从命令行传入。
