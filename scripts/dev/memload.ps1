$m = Get-CimInstance Win32_OperatingSystem
$used = $m.TotalVisibleMemorySize - $m.FreePhysicalMemory
Write-Output ("load=" + [math]::Round($used * 100.0 / $m.TotalVisibleMemorySize, 1) + "% used=" + [math]::Round($used / 1MB, 2) + "GB total=" + [math]::Round($m.TotalVisibleMemorySize / 1MB, 2) + "GB")
