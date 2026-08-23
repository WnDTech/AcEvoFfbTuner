$log = "C:\Users\paul_\AppData\Local\Temp\kilo\umdf-diag.log"
"=== umdf diag $(Get-Date) ===" | Set-Content $log

wevtutil sl "Microsoft-Windows-DriverFrameworks-UserMode/Operational" /e:true 2>&1 | Add-Content $log
wevtutil sl "Microsoft-Windows-DriverFrameworks-UserMode/Diagnostic" /e:true 2>&1 | Add-Content $log

$devcon = "C:\Program Files (x86)\Windows Kits\10\Tools\10.0.26100.0\x64\devcon.exe"
& $devcon restart "root\FakeRs50" 2>&1 | Add-Content $log
Start-Sleep -Seconds 6

"--- operational ---" | Add-Content $log
Get-WinEvent -LogName "Microsoft-Windows-DriverFrameworks-UserMode/Operational" -MaxEvents 10 -ErrorAction SilentlyContinue |
    ForEach-Object { "[$($_.TimeCreated)] $($_.Id): $($_.Message.Substring(0, [Math]::Min(400, $_.Message.Length)))" } | Add-Content $log

"--- diagnostic ---" | Add-Content $log
Get-WinEvent -LogName "Microsoft-Windows-DriverFrameworks-UserMode/Diagnostic" -MaxEvents 10 -ErrorAction SilentlyContinue |
    ForEach-Object { "[$($_.TimeCreated)] $($_.Id): $($_.Message.Substring(0, [Math]::Min(400, $_.Message.Length)))" } | Add-Content $log

"--- setupapi tail ---" | Add-Content $log
Get-Content "C:\Windows\INF\setupapi.dev.log" -Tail 40 | Select-String -Pattern "!!!|error|Error|FakeRs50|WUDF|driver" | ForEach-Object { $_.Line } | Select-Object -Last 12 | Add-Content $log

"=== done ===" | Add-Content $log