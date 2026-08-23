$log = "C:\Users\paul_\AppData\Local\Temp\kilo\clean-mini2.log"
"=== clean-mini2 $(Get-Date) ===" | Set-Content $log

$devcon = "C:\Program Files (x86)\Windows Kits\10\Tools\10.0.26100.0\x64\devcon.exe"
$inf = "C:\Users\paul_\OneDrive\Documents\APP\ACEVO - Telemetry FFB\tools\FakeWheel\driver\fake_rs50mini\x64\Release\fake_rs50mini\fake_rs50mini.inf"

# Remove the churn copies + restore pristine service config
Remove-Item "C:\Windows\System32\drivers\mshidumdf2.sys", "C:\Windows\System32\drivers\mshidumdf3.sys" -Force -ErrorAction SilentlyContinue
reg add "HKLM\SYSTEM\CurrentControlSet\Services\mshidumdf" /v ImagePath /t REG_EXPAND_SZ /d "\SystemRoot\System32\drivers\mshidumdf.sys" /f 2>&1 | Add-Content $log

& $devcon remove "root\FakeRs50Mini" 2>&1 | Add-Content $log
Start-Sleep -Seconds 2

$enum = pnputil /enum-drivers 2>&1 | Out-String
$blocks = $enum -split "(\r?\n){2,}" | Where-Object { $_ -match "Original Name:\s+fake_rs50mini\.inf" }
foreach ($b in $blocks) {
    if ($b -match "Published Name:\s+(\S+\.inf)") {
        pnputil /delete-driver $Matches[1] /uninstall /force 2>&1 | Add-Content $log
    }
}

pnputil /add-driver $inf /install 2>&1 | Add-Content $log
& $devcon install $inf "root\FakeRs50Mini" 2>&1 | Add-Content $log
Start-Sleep -Seconds 10

sc.exe query mshidumdf 2>&1 | Select-String "STATE|WIN32" | Add-Content $log
Get-PnpDevice -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -match 'ROOT\\SAMPLE' } |
    Select-Object Status, @{n='Prob';e={$_.Problem}}, InstanceId | Format-Table -AutoSize | Out-String | Add-Content $log
Get-PnpDevice -Class HIDClass -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -match 'ROOT' } |
    Select-Object Status, FriendlyName, InstanceId | Format-Table -AutoSize | Out-String | Add-Content $log

"=== done ===" | Add-Content $log