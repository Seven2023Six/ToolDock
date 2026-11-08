Uninstall Tool 3.8.2.5765（已解锁，功能全开）
================================================

适用版本：3.8.2.5765（x64）
主程序 SHA256：D1016B2FB0493261AB401119CF095B94E08D298C848A5BC9140507499AC556CD

使用方法
--------
1. 解压到任意目录（建议非系统盘，如 D:\Tools\Uninstall Tool）。
2. 运行 UninstallTool.exe。首次运行可能弹 UAC，允许即可。
3. 界面标题显示 “Uninstall Tool 3.8.2”，不再出现 [未注册版本] 或试用倒计时。

已做的修改（相对官方原版，仅 11 字节）
--------------------------------------
虚拟地址        文件偏移   原字节            补丁
0x140034934     0x33D34    40 53 48          B0 01 C3      IsRegistered 恒为 true
0x140034CD4     0x340D4    E9 D7 50 FD FF    B8 03 00 00 00 C3   GetLicenseStatus 恒返回 3（已注册）
0x14003412A     0x3352A    2B C2             90 90         试用剩余天数恒 30，永不到期

注意
----
- 程序若自动更新，exe 会被官方版本覆盖，补丁失效，需要重新按上表打补丁。
  可在 %APPDATA%\CrystalIdea Software\Uninstall Tool\preferences.xml 把
  Startup_CheckForUpdates 设为 0 关闭启动检查更新。
- 修改后数字签名失效，属正常现象。
- “帮助 → 关于”里 “This product is licensed to:” 后面可能为空，因为本地没有授权数据，不影响功能。
- 本包只包含解锁后的程序文件；试用标记存放在 HKCU\Software\CrystalIdea Software\Uninstall Tool，
  与程序无关，补丁后不再生效。
