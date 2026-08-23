$log = "C:\Users\paul_\AppData\Local\Temp\kilo\attach-mini.log"
"=== attach-mini $(Get-Date) ===" | Set-Content $log

$devcon = "C:\Program Files (x86)\Windows Kits\10\Tools\10.0.26100.0\x64\devcon.exe"
$inf = "C:\Users\paul_\OneDrive\Documents\APP\ACEVO - Telemetry FFB\tools\FakeWheel\driver\fake_rs50mini\x64\Release\fake_rs50mini\fake_rs50mini.inf"

# 1. Remove the failing node
& $devcon remove "root\FakeRs50Mini" 2>&1 | Add-Content $log

# 2. Ensure mshidumdf is LOADED (running) BEFORE the device start
sc.exe stop mshidumdf 2>&1 | Add-Content $log
Start-Sleep -Seconds 3
sc.exe start mshidumdf 2>&1 | Add-Content $log
Start-Sleep -Seconds 2
sc.exe query mshidumdf 2>&1 | Add-Content $log

# 3. Create the device node — its start should attach to the running service
& $devcon install $inf "root\FakeRs50Mini" 2>&1 | Add-Content $log

Start-Sleep -Seconds 8

"--- state ---" | Add-Content $log
sc.exe query mshidumdf 2>&1 | Add-Content $log
Get-PnpDevice -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -match 'ROOT\\SAMPLE' } |
    Select-Object Status, @{n='Prob';e={$_.Problem}}, InstanceId | Format-Table -AutoSize | Out-String | Add-Content $log
Get-PnpDevice -Class HIDClass -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -match 'ROOT' } |
    Select-Object Status, FriendlyName, InstanceId | Format-Table -AutoSize | Out-String | Add-Content $log

"=== done ===" | Add-Content $log