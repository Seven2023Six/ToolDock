# 工具坞 ToolDock · 总体架构设计

> 版本 **v0.2（双进程分体架构）** ｜ 2026-11-01 ｜ 配套文档：[REQUIREMENTS.md](REQUIREMENTS.md)
> 状态：M0.2 已实现并实测。本版取代 v0.1 的"单进程 WPF + 托盘"方案。

---

## 0. 设计公理（v0.2 修订）

1. **单托盘入口是信仰**：托盘图标由 Agent 独占，插件只允许"贡献"菜单项和 tooltip 状态行。
2. **常驻内存最小化**：常驻的只有 Agent（实测 ~13MB，NativeAOT）；WPF 界面按需启动、关窗即退。
3. **插件是一等公民**：后台插件编译进 Agent（源码集成）；界面插件为 DLL 由 App 动态加载。
4. **外壳要命比插件要紧**：任何插件故障只降级自身；App 崩溃不影响托盘（它本来就是短命进程）。
5. **预留而不实现**：提权帮手、快捷面板、轮盘唤出——接口留缝，等决策到位。

## 1. 实测数据（B2 决策依据）

| 进程 | 生命周期 | 实测常驻 |
|---|---|---|
| ToolDock.Agent（NativeAOT，瘦身配置 + 工作集回收） | 常驻 | **~4 MB**（2026-11-04，优化前 ~14.8 MB） |
| ToolDock.exe（WPF，R2R，非并发 GC） | 按需，关窗即退 | ~145 MB（仅使用期间，WPF 地板） |
| v0.1 单进程方案（对照） | 常驻 | ~124 MB |

结论：托盘常驻占用为传统单进程方案的 ~3%；代价是"打开界面"有约 1 秒冷启动。

## 2. 总览图

```
┌─ ToolDock.Agent（常驻 · NativeAOT）──────────┐       ┌─ ToolDock.App（按需 · WPF）────┐
│ 托盘图标（Shell_NotifyIconW + 消息窗口）        │       │ 主窗：侧边导航 + 插件页面        │
│ 右键菜单 / 气泡通知 / tooltip 聚合             │◄─────►│ 主题（浅色默认/可切换）+ Mica    │
│ settings.json 唯一写者                        │ 命名管道 │ ALC 动态加载界面插件 DLL（M1+）  │
│ 后台插件宿主（源码编译集成）                     │JSON 帧 │ 关窗 = 进程退出                 │
│ 日志、自启（M3）、热键（M 后期）                 │       │ 连接断开 → 独立模式兜底          │
└───────────────────────────────────────────┘       └──────────────────────────────┘
        管道：\\.\pipe\tooldock-agent，4字节长度 + UTF-8 JSON，消息见 Core/Pipe
        二次启动 App：Agent 让旧实例置前（activate），新实例自行退出（duplicate）
```

## 3. 解决方案结构

```
ToolDock/
├── ToolDock.slnx
├── src/
│   ├── ToolDock.Abstractions/   # ★ 契约底层（net8.0，无 UI）：
│   │                            #   IBackgroundPlugin、IAgentContext、6 个服务接口、PluginMetadata
│   ├── ToolDock.Contracts/      # 契约 UI 层（net8.0-windows, WPF）：IUiPlugin、IUiContext
│   ├── ToolDock.Core/           # 共享：AppSettings、SingleInstanceGuard、管道协议（消息/帧/JSON源生成）
│   ├── ToolDock.Agent/          # 常驻（PublishAot=true）：Win32 托盘、像素图标、管道服务端、
│   │                            #   插件宿主、文件日志、SamplePlugin
│   └── ToolDock.App/            # 按需 GUI（WPF）：主窗、页面、主题、AgentLink 客户端
└── publish/                     # 发布布局：ToolDock.Agent.exe + app/ToolDock.exe
```

引用规则：Agent → Abstractions + Core；App → Core（界面插件落地后加 Contracts）；
Contracts → Abstractions；**Agent 永不引用 WPF；App 不再使用 WinForms**。

## 4. 插件模型（B2 定稿）

| | 后台插件 | 界面插件 |
|---|---|---|
| 契约 | `IBackgroundPlugin`（Abstractions） | `IUiPlugin`（Contracts） |
| 宿主 | Agent，**源码编译集成**（AOT 不能运行时加载程序集） | App，AssemblyLoadContext 动态加载 DLL |
| 加新工具 | 写类 + 在 PluginHost 登记一行，重编译（约 5 秒） | 丢 DLL 进 plugins/ 目录即生效 |
| 能力 | 托盘菜单/状态行、气泡、隔离设置、热键、提权 | 同左 + ShowPage、WPF 页面 |
| 示例 | SamplePlugin（托盘菜单→气泡，链路已验证） | M1 起落地 |

设计取舍记录：B2 放弃了"后台插件 DLL 热加载"的动态性（单人项目价值低），换 Agent 常驻 35MB → 13MB；
界面侧保留完整动态加载，两者按 plugin 的 kind 自由组合。

## 5. 关键实现要点（踩坑记录）

1. **托盘 = 纯 Win32**：`Shell_NotifyIconW` + 隐藏消息窗口（HWND_MESSAGE），零 UI 框架依赖，AOT 友好。
   WndProc 委托必须静态保活；Explorer 重启后监听 `TaskbarCreated` 补挂图标。
2. **图标像素级绘制**：`CreateDIBSection`(32bpp ARGB) + `CreateIconIndirect`。
   ⚠️ `CreateIcon` 的参数是字节指针不是 HBITMAP 句柄——传句柄 = AccessViolation（已踩）。
3. **AOT 约束**：全部 JSON 走源生成（`ToolDockJsonContext`）；插件设置用 JsonNode 具体分支（禁泛型 Create/GetValue）；
   `InvariantGlobalization=true`。
4. **管道帧**：4 字节小端长度 + JSON；`PipeFrame` 所有 await **必须 ConfigureAwait(false)**
   ——App 在 UI 线程同步等待 IO，续延回 UI 线程 = 死锁（已踩，症状是进程活着但窗口永远不出现）。
5. **App 双保险**：连不上 Agent（开发场景）→ 独立模式，主题直接读写本地文件；
   settings.json 唯一写者是 Agent。
6. **Mica/主题**：照旧（浅色默认、DynamicResource、ThemeService 换字典），主题变更经管道回传 Agent 落盘。

## 6. 杀毒软件注意（环境事实）

本机杀毒为 **Kaspersky**（Defender 已被其接管）。未签名的 AOT 原生 exe 会被查杀，
需在卡巴斯基"威胁和排除项"中排除项目目录（或对发布产物做代码签名）。

## 7. 里程碑状态与后续

| 里程碑 | 状态 |
|---|---|
| M0.1 壳 + UI（单进程版） | 已被 v0.2 取代 |
| M0.2 双进程分体 + 主题 + 管道 | ✅ 本次完成并实测 |
| M1 电量插件（复用 VigilWatt.Core 可行部分，后台轮询在 Agent + 页面在 App） | 待开工 |
| M2 内存清理插件 | ✅ 已完成：CleanupCore 进 Core、ElevatedHelper 提权助手（requireAdministrator+AOT）、免 UAC 计划任务、内存页真控件（区域勾选/阈值/定时/立即清理），端到端实测通过 |
| M3 自启（Agent 写 HKCU Run）、设置页接线、日志完善 | 待开工 |

## 8. 遗留待决（联动 REQUIREMENTS.md）

1. UAC 方案（等用户提供 Mem Reduct 分析）——现在 `IElevationService` 已有最小实现
2. 主窗口形态（图吧式 / uTools 式 / 两者）
3. `Tools/` 目录用途；两个现成工具的具体不满点
4. 卡巴斯基排除项长期化（建议加排除而不是长期关保护）
