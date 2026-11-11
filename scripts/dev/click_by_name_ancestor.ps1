param([string]$Name = "")
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$p = Get-Process ToolDock | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $p) { Write-Output 'no window'; exit 1 }
$win = [System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)

$c1 = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $Name)
$el = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c1)
if (-not $el) { Write-Output ("element not found: " + $Name); exit 1 }

# climb to nearest ancestor that supports InvokePattern
$walker = [System.Windows.Automation.TreeWalker]::RawViewWalker
$cur = $el
for ($i = 0; $i -lt 8; $i++) {
    try { $inv = $cur.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern); break } catch { }
    if ($cur -eq [System.Windows.Automation.AutomationElement]::RootElement) { break }
    $cur = $walker.GetParent($cur)
}
if ($inv) {
    $inv.Invoke()
    Write-Output ("invoked ancestor: " + $cur.Current.ControlType.ProgrammaticName)
} else {
    Write-Output 'no invokable ancestor'
}
