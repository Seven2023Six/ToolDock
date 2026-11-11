param([string]$Name = "")
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -Namespace TDClick -Name N -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, System.UIntPtr extra);
'@

$p = Get-Process ToolDock | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $p) { Write-Output 'no ToolDock window'; exit 1 }
$win = [System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)

$c1 = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $Name)
$c2 = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
$btn = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.AndCondition($c1, $c2)))
if (-not $btn) { Write-Output ("button not found: " + $Name); exit 1 }

$pt = $btn.GetClickablePoint()
[TDClick.N]::SetCursorPos([int]$pt.X, [int]$pt.Y) | Out-Null
Start-Sleep -Milliseconds 120
[TDClick.N]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero)   # LEFTDOWN
[TDClick.N]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)   # LEFTUP
Write-Output ("clicked button at " + $pt.X + "," + $pt.Y)
