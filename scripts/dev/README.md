# scripts/dev — 调试辅助脚本

保持纯 ASCII 内容（PowerShell 5.1 读无 BOM 的 UTF-8 中文会按 GBK 解析导致语法错误）；
需要中文参数时从命令行传入，不要写进脚本正文。

| 脚本 | 用途 |
| --- | --- |
| `ui_nav.ps1 -Sidebar <名称> -Action <名称>` | UIA 导航：选择侧栏 / 点击按钮（按名称） |
| `click_button.ps1 -Name <名称>` | 按 UIA 名称找 Button 并坐标点击（规避 InvokePattern 缺失） |
| `click_by_name_ancestor.ps1 -Name <名称>` | 按名称找元素，向上找可 Invoke 的祖先按钮（页头按钮无自动化名时用） |
| `shot_screen.ps1` | 截全屏到 `%TEMP%\toolDock-screen.png` 并输出路径 |
| `memload.ps1` | 输出当前内存占用百分比 |
| `cursortint/` | 光标染色小工具（`dotnet run --project scripts/dev/cursortint`） |

前置：ToolDock 界面进程已在运行；脚本通过进程名 `ToolDock` 定位主窗。
