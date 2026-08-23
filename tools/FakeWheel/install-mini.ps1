# install-mini.ps1 — run ELEVATED. Installs the FakeRs50Mini HID minidriver.

$ErrorActionPreference = "Continue"
$log = "C:\Users\paul_\AppData\Local\Temp\kilo\install-mini.log"
"=== mini install $(Get-Date) ===" | Set-Content $log

$devcon = "C:\Program Files (x86)\Windows Kits\10\Tools\10.0.26100.0\x64\devcon.exe"
$rel = Join-Path $PSScriptRoot "driver\fake_rs50mini\x64\Release\fake_rs50mini"
$inf = Join-Path $rel "fake_rs50mini.inf"

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

Start-Sleep -Seconds 8

"--- HID devices under the fake root node ---" | Add-Content $log
Get-PnpDevice -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -match 'FakeRs50Mini|ROOT\\SAMPLE' } |
    Select-Object Status, Class, FriendlyName, InstanceId | Format-Table -AutoSize | Out-String | Add-Content $log

"=== done ===" | Add-Content $log