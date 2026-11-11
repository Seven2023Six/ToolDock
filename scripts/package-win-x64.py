# -*- coding: utf-8 -*-
"""把 publish/ 打成交付 zip。

用法：先 dotnet publish（Agent AOT → publish/，App → publish/app），再运行本脚本。
产物：dist/ToolDock-<VERSION>-win-x64.zip
"""
import os
import sys
import zipfile

VERSION = os.environ.get("TOOLDOCK_VERSION") or "0.2.0"
PACKAGE_NAME = "ToolDock"
SRC = os.environ.get("TOOLDOCK_PUBLISH") or "publish"
# 可选：Agent 本体从另一个路径读取（某些杀软会长时间占用标准文件名，构建时用别名产物）
AGENT_SOURCE = os.environ.get("TOOLDOCK_AGENT_SOURCE")
DIST = "dist"

README = """工具坞 ToolDock {ver}
========================================

运行
  双击 ToolDock.Agent.exe（常驻托盘，首次运行会写入开机自启）。
  右键托盘图标 → 打开工具坞；单击/双击托盘图标也可打开。
  界面关窗只是隐藏到托盘；托盘右键「退出」才真正退出（并通知界面一起退出）。

目录
  ToolDock.Agent.exe      常驻后台（NativeAOT，含托盘/管道/插件）
  tools/altsnap/*.dll     内置窗口拖拽引擎（AltSnap 1.68，进程内加载，无独立 exe/托盘）
  tools/inputtip/assets   输入法状态提示的默认光标/符号（首次启动复制到 %APPDATA%）
  app/ToolDock.exe        界面进程（按需启动，关窗即退）
  app/tools/uninstalltool 内置实用工具：Uninstall Tool 3.8.2

功能
  内置工具：内存清理（对齐 Mem Reduct）、外设电量显示（VigilWatt）、窗口拖拽（AltSnap 全功能）、
            输入法状态提示（InputTip 全功能，原生引擎，无 AutoHotkey 进程）
  实用工具：Uninstall Tool（需要管理员权限，启动时请求 UAC）
  任务栏贴片：内存占用（点击清理）＋设备电量，竖线分隔
  自定义：占位，后续注入清单

卸载
  1) 托盘右键 → 退出
  2) 删除本目录
  3) 删除 %APPDATA%\\ToolDock
  4) 打开 regedit，删除 HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run 下的 ToolDock
  5) 若曾开启「免 UAC 提权」，删除计划任务 ToolDock_Clean / ToolDock_CleanAuto

环境
  Windows 10 1809+ / Windows 11，x64
  界面进程需要 .NET 8 Desktop Runtime（Agent 为原生 AOT，不需要）
"""


def keep(rel: str) -> bool:
    """rel 相对 SRC 的 posix 路径。"""
    if rel.endswith(".pdb"):
        return False
    if rel.startswith("helper/"):
        return False
    if rel.startswith("app/runtimes/"):
        # 只保留 x64 Windows 的原生依赖，其余 RID（linux/wasm/arm…）不进包
        return rel[len("app/runtimes/"):].startswith("win-x64/")
    if "/" not in rel and rel != "ToolDock.Agent.exe":
        # publish 根目录只保留 Agent 本体，其余是历史发布残留
        return False
    return True


def main() -> int:
    agent_source = AGENT_SOURCE or os.path.join(SRC, "ToolDock.Agent.exe")
    if not os.path.isdir(SRC) or not os.path.isfile(agent_source):
        print("找不到 ToolDock.Agent.exe，请先 dotnet publish（或设置 TOOLDOCK_AGENT_SOURCE）")
        return 1
    if not os.path.isfile(os.path.join(SRC, "app", "ToolDock.exe")):
        print("找不到 publish/app/ToolDock.exe，请先 dotnet publish App")
        return 1

    os.makedirs(DIST, exist_ok=True)
    out = os.path.join(DIST, f"{PACKAGE_NAME}-{VERSION}-win-x64.zip")
    if os.path.exists(out):
        os.remove(out)

    count = 0
    with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as z:
        z.write(agent_source, f"{PACKAGE_NAME}/ToolDock.Agent.exe")
        count += 1
        for base, dirs, files in os.walk(SRC):
            for name in files:
                full = os.path.join(base, name)
                rel = os.path.relpath(full, SRC).replace(os.sep, "/")
                if rel == "ToolDock.Agent.exe":
                    continue
                if not keep(rel):
                    continue
                try:
                    z.write(full, f"{PACKAGE_NAME}/{rel}")
                    count += 1
                except PermissionError:
                    print(f"跳过被占用的文件：{rel}")
        z.writestr(f"{PACKAGE_NAME}/使用说明.txt", README.format(ver=VERSION))

    size = os.path.getsize(out)
    print(f"created {out}")
    print(f"files={count + 1} size={size / 1048576.0:.1f} MB")
    return 0


if __name__ == "__main__":
    sys.exit(main())
