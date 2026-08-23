$log = "C:\Users\paul_\AppData\Local\Temp\kilo\attach3.log"
"=== attach3 $(Get-Date) ===" | Set-Content $log

$devcon = "C:\Program Files (x86)\Windows Kits\10\Tools\10.0.26100.0\x64\devcon.exe"

reg add "HKLM\SYSTEM\CurrentControlSet\Services\mshidumdf" /v ImagePath /t REG_EXPAND_SZ /d "\SystemRoot\System32\drivers\mshidumdf2.sys" /f 2>&1 | Add-Content $log

& $devcon restart "root\FakeRs50Mini" 2>&1 | Add-Content $log
Start-Sleep -Seconds 10

sc.exe query mshidumdf 2>&1 | Select-String "STATE|WIN32" | Add-Content $log
Get-PnpDevice -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -match 'ROOT\\SAMPLE' } |
    Select-Object Status, @{n='Prob';e={$_.Problem}}, InstanceId | Format-Table -AutoSize | Out-String | Add-Content $log
Get-PnpDevice -Class HIDClass -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -match 'ROOT' } |
    Select-Object Status, FriendlyName, InstanceId | Format-Table -AutoSize | Out-String | Add-Content $log

"=== done ===" | Add-Content $log