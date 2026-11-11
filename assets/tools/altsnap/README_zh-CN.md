# AltSnap 引擎（工具箱内置）

本目录只包含窗口拖拽功能的**引擎文件**（工具箱内置引擎版本 1.0，基于 AltSnap 1.68 内核），不是可执行程序：

- `altsnap_host.dll`：去掉托盘/配置窗口的 AltSnap 宿主，导出 `ToolDockAltSnapStart/Stop/Ready`
- `hooks.dll`：AltSnap 1.68 官方鼠标/键盘引擎（上游 `hooks.c` 原样编译）
- `AltSnap.dni`：默认配置模板，首次运行复制为 `%APPDATA%\ToolDock\altsnap\AltSnap.ini`
- `License.txt`：AltSnap 的 GPL-3.0 许可证

由 `ToolDock.Agent.exe` 用 `LoadLibrary` 加载，在 Agent 进程内运行；没有 `AltSnap.exe`，
也没有第二个托盘图标。设置页位于：内置工具 → 窗口拖拽。

上游项目：https://github.com/RamonUnch/AltSnap
修改与构建说明：仓库根目录 `native/altsnap/README.md`。
