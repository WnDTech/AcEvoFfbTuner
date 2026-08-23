$log = "C:\Users\paul_\AppData\Local\Temp\kilo\attach4.log"
"=== attach4 $(Get-Date) ===" | Set-Content $log

$devcon = "C:\Program Files (x86)\Windows Kits\10\Tools\10.0.26100.0\x64\devcon.exe"

sc.exe query mshidumdf 2>&1 | Select-String "STATE" | Add-Content $log

# Start the existing device node WITHOUT stopping the running service
& $devcon start "root\FakeRs50Mini" 2>&1 | Add-Content $log
Start-Sleep -Seconds 10

sc.exe query mshidumdf 2>&1 | Select-String "STATE|WIN32" | Add-Content $log
Get-PnpDevice -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -match 'ROOT\\SAMPLE' } |
    Select-Object Status, @{n='Prob';e={$_.Problem}}, InstanceId | Format-Table -AutoSize | Out-String | Add-Content $log
Get-PnpDevice -Class HIDClass -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -match 'ROOT' } |
    Select-Object Status, FriendlyName, InstanceId | Format-Table -AutoSize | Out-String | Add-Content $log

"=== done ===" | Add-Content $log