$log = "C:\Users\paul_\AppData\Local\Temp\kilo\clean-reinstall.log"
"=== clean reinstall $(Get-Date) ===" | Set-Content $log

Stop-Process -Name wudfhost -Force -ErrorAction SilentlyContinue
Remove-Item "C:\Windows\Temp\FakeRs50.trace", "C:\Windows\Temp\FakeRs50.log" -Force -ErrorAction SilentlyContinue

$devcon = "C:\Program Files (x86)\Windows Kits\10\Tools\10.0.26100.0\x64\devcon.exe"
& $devcon remove "root\FakeRs50" 2>&1 | Add-Content $log

$enum = pnputil /enum-drivers 2>&1 | Out-String
$blocks = $enum -split "(\r?\n){2,}" | Where-Object { $_ -match "Original Name:\s+fake_rs50\.inf" }
foreach ($b in $blocks) {
    if ($b -match "Published Name:\s+(\S+\.inf)") {
        pnputil /delete-driver $Matches[1] /uninstall /force 2>&1 | Add-Content $log
    }
}

"--- fresh stage + install ---" | Add-Content $log
$inf = "C:\Users\paul_\OneDrive\Documents\APP\ACEVO - Telemetry FFB\tools\FakeWheel\driver\fake_rs50\x64\Release\fake_rs50\fake_rs50.inf"
pnputil /add-driver $inf /install 2>&1 | Add-Content $log
& $devcon install $inf "root\FakeRs50" 2>&1 | Add-Content $log

Start-Sleep -Seconds 12
"--- trace ---" | Add-Content $log
Get-Content "C:\Windows\Temp\FakeRs50.trace" -ErrorAction SilentlyContinue | Select-Object -First 24 | Add-Content $log

"=== done ===" | Add-Content $log