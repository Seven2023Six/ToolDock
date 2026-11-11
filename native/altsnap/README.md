# 内置 AltSnap 引擎（Windows x64）

工具箱内置窗口拖拽引擎版本 **1.0**（基于 AltSnap 1.68 内核）。窗口拖拽功能不是重新实现的简化版：工具箱把 **AltSnap 1.68 的官方引擎 `hooks.dll`** 直接加载进
`ToolDock.Agent.exe`，再配一个去掉托盘和配置窗口的宿主 DLL，由 Agent 的专用 STA 线程跑它自己的
消息循环。因此 AltSnap 的全部鼠标/键盘动作、Aero、SmartAero、StickyResize、区域（Zones）、黑名单、
性能参数都保持上游行为；工具箱只负责托盘、配置页和 ini 读写。

- 上游：https://github.com/RamonUnch/AltSnap （GPL-3.0）
- `hooks.dll`：`hooks.c` 原样编译，无修改
- `altsnap_host.dll`：由 `altsnap.c` 改造而来，源码见 `altsnap_host.c`

## 对 altsnap.c 的改动

1. 新增导出：
   - `ToolDockAltSnapStart(iniPath, hooksDir)`：阻塞运行宿主窗口 + 消息循环
   - `ToolDockAltSnapStop()`：向隐藏窗口发 `WM_CLOSE`
   - `ToolDockAltSnapReady()`：窗口是否已创建
2. 宿主模式（`-DTOOLDOCK_HOST`）：
   - 不创建托盘图标（`InitTray/UpdateTray` 跳过，`WM_ADDTRAY` 忽略）
   - 跳过第二实例检测与 `AlwaysElevate` 自提权
   - 配置文件路径由调用方传入，而不是 `AltSnap.exe` 同目录 / `%APPDATA%\AltSnap`
   - `hooks.dll` 从调用方传入的目录加载
   - `languages.c` 改为从 DLL 所在目录加载 `Lang\*.ini`（而不是 Agent exe 目录）
   - 随包附带精简的 `Lang/zh_CN.ini`（动作菜单中文）；工具箱启动时把 `Language=Auto` 修正为 `zh-CN`
   - 移除 `unfuckWinMain` / `ExitProcess` 入口，改为 `DllMain` + 上述导出

## 构建

要求 Visual Studio 2019/2022 Build Tools（MSVC x64）。在仓库根目录执行：

```powershell
powershell -ExecutionPolicy Bypass -File native/altsnap/build.ps1 -AltSnapSource Tools/AltSnap
```

脚本会：

1. 把 `Tools/AltSnap`（上游 1.68 源码）复制到 `native/altsnap/build/`
2. 用 `altsnap_host.c` 覆盖 `altsnap.c`，用 `languages_host.c` 覆盖 `languages.c`
3. `rc altsnap.rc` / `cl /MT /DTOOLDOCK_HOST` / `link /DLL`
4. 用上游 `hooks.c` 编译 `hooks.dll`
5. 输出到 `assets/tools/altsnap/`，并附带 `Lang/zh_CN.ini`

`/MT` 静态链接 CRT，运行时不依赖 VC++ Redistributable。产物只依赖
`KERNEL32/USER32/SHELL32/ADVAPI32/GDI32/COMCTL32/OLE32/...` 等系统 DLL。

## 运行时布局

```
publish/
  ToolDock.Agent.exe
  tools/altsnap/altsnap_host.dll
  tools/altsnap/hooks.dll
  tools/altsnap/AltSnap.dni      # 默认配置模板
```

Agent 把 `AltSnap.ini` 放在 `%APPDATA%\ToolDock\altsnap\AltSnap.ini`。

## 许可

AltSnap 为 GPL-3.0；本目录的修改同样以 GPL-3.0 发布。许可证全文见
`assets/tools/altsnap/License.txt`。
