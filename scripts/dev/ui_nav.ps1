param(
    [string]$Sidebar = "",
    [string]$Action = ""
)

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -Namespace TDNav -Name N -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool ShowWindow(System.IntPtr h, int cmd);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool SetForegroundWindow(System.IntPtr h);
'@

$p = Get-Process ToolDock | Select-Object -First 1
$root = [System.Windows.Automation.AutomationElement]::RootElement
$win = $null
$children = $root.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)
for ($wi = 0; $wi -lt $children.Count; $wi++) {
    $cand = $children.Item($wi)
    if ($cand.Current.ProcessId -eq $p.Id -and $cand.Current.Name -eq '工具坞 ToolDock') { $win = $cand; break }
}
if ($win -eq $null) { 'no window'; exit 1 }

$hwnd = [IntPtr]$win.Current.NativeWindowHandle
[TDNav.N]::ShowWindow($hwnd, 9) | Out-Null
Start-Sleep -Milliseconds 300
[TDNav.N]::SetForegroundWindow($hwnd) | Out-Null
Start-Sleep -Milliseconds 250

function FindByName($name) {
    $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)
    return $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
}

if ($Sidebar -ne "") {
    $el = FindByName $Sidebar
    if ($el -ne $null) {
        $el.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        Start-Sleep -Milliseconds 700
        "selected $Sidebar"
    } else { "missing sidebar $Sidebar" }
}

if ($Action -ne "") {
    Start-Sleep -Milliseconds 300
    $el = FindByName $Action
    if ($el -ne $null) {
        $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Start-Sleep -Milliseconds 1200
        "clicked $Action"
    } else { "missing action $Action" }
}
