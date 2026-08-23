# post-reboot-verify.ps1 — run ELEVATED after a reboot.
# Installs the FakeRs50Mini HID minidriver (fresh mshidumdf load) and reports
# whether the HID device appeared with the fake RS50 identity.

$ErrorActionPreference = "Continue"
$log = "C:\Users\paul_\AppData\Local\Temp\kilo\post-reboot.log"
"=== post-reboot verify $(Get-Date) ===" | Set-Content $log

Remove-Item "C:\Windows\Temp\FakeRs50.log" -Force -ErrorAction SilentlyContinue

$devcon = "C:\Program Files (x86)\Windows Kits\10\Tools\10.0.26100.0\x64\devcon.exe"
$inf = "C:\Users\paul_\OneDrive\Documents\APP\ACEVO - Telemetry FFB\tools\FakeWheel\driver\fake_rs50mini\x64\Release\fake_rs50mini\fake_rs50mini.inf"

& $devcon remove "root\FakeRs50Mini" 2>&1 | Add-Content $log

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

"--- fake root node + HID devices ---" | Add-Content $log
Get-PnpDevice -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -match 'ROOT\\SAMPLE|FakeRs50Mini' } |
    Select-Object Status, Class, FriendlyName, InstanceId | Format-Table -AutoSize | Out-String | Add-Content $log

$hid = Get-PnpDevice -Class HIDClass -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -match 'ROOT' }
"--- HID devices under root ---" | Add-Content $log
$hid | Select-Object Status, FriendlyName, InstanceId | Format-Table -AutoSize | Out-String | Add-Content $log

if ($hid) {
    "RESULT: OK — HID device present" | Add-Content $log
} else {
    $root = Get-PnpDevice -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -eq 'ROOT\SAMPLE\0001' }
    "RESULT: FAIL — root status: $($root.Status) problem: $($root.Problem)" | Add-Content $log
}

"=== done ===" | Add-Content $log