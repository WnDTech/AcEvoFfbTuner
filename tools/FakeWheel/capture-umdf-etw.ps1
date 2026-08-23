$log = "C:\Users\paul_\AppData\Local\Temp\kilo\etw-diag.log"
"=== etw diag $(Get-Date) ===" | Set-Content $log

logman start wudftrace -p "Microsoft-Windows-DriverFrameworks-UserMode" 0xffffffffffffffff 0xff -o "C:\Windows\Temp\wudf.etl" -ets 2>&1 | Add-Content $log

$devcon = "C:\Program Files (x86)\Windows Kits\10\Tools\10.0.26100.0\x64\devcon.exe"
& $devcon restart "root\FakeRs50" 2>&1 | Add-Content $log
Start-Sleep -Seconds 8

logman stop wudftrace -ets 2>&1 | Add-Content $log

Start-Sleep -Seconds 3
tracerpt "C:\Windows\Temp\wudf.etl" -o "C:\Windows\Temp\wudf.txt" -y 2>&1 | Add-Content $log

"--- trace summary (errors/load) ---" | Add-Content $log
Get-Content "C:\Windows\Temp\wudf.txt" -ErrorAction SilentlyContinue | Select-String -Pattern "DriverEntry|load|failed|error|0x90040001|VHF|Level 0|failure" -CaseSensitive:$false | Select-Object -First 40 | ForEach-Object { $_.Line.Substring(0, [Math]::Min(220, $_.Line.Length)) } | Add-Content $log

"=== done ===" | Add-Content $log