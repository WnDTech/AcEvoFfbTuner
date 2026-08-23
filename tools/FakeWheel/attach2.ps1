$log = "C:\Users\paul_\AppData\Local\Temp\kilo\attach2.log"
"=== attach2 $(Get-Date) ===" | Set-Content $log

$devcon = "C:\Program Files (x86)\Windows Kits\10\Tools\10.0.26100.0\x64\devcon.exe"
$inf = "C:\Users\paul_\OneDrive\Documents\APP\ACEVO - Telemetry FFB\tools\FakeWheel\driver\fake_rs50mini\x64\Release\fake_rs50mini\fake_rs50mini.inf"

& $devcon remove "root\FakeRs50Mini" 2>&1 | Add-Content $log
Start-Sleep -Seconds 2

sc.exe query mshidumdf 2>&1 | Select-String "STATE" | Add-Content $log

& $devcon install $inf "root\FakeRs50Mini" 2>&1 | Add-Content $log

Start-Sleep -Seconds 10

"--- after ---" | Add-Content $log
sc.exe query mshidumdf 2>&1 | Select-String "STATE|WIN32" | Add-Content $log
reg query "HKLM\SYSTEM\CurrentControlSet\Services\mshidumdf" /v ImagePath 2>&1 | Add-Content $log
Get-PnpDevice -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -match 'ROOT\\SAMPLE' } |
    Select-Object Status, @{n='Prob';e={$_.Problem}}, InstanceId | Format-Table -AutoSize | Out-String | Add-Content $log
Get-PnpDevice -Class HIDClass -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -match 'ROOT' } |
    Select-Object Status, FriendlyName, InstanceId | Format-Table -AutoSize | Out-String | Add-Content $log

"=== done ===" | Add-Content $log