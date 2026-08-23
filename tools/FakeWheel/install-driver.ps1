# install-driver.ps1 — run ELEVATED.
#   .\install-driver.ps1
# Idempotent: removes any previously staged FakeRs50 package, stages the new
# one, and creates/updates the root-enumerated FakeWheel device.

$ErrorActionPreference = "Continue"
$log = "C:\Users\paul_\AppData\Local\Temp\kilo\install-driver.log"
"=== driver install $(Get-Date) ===" | Set-Content $log

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Run this script elevated (admin)."
}

$rel = Join-Path $PSScriptRoot "driver\fake_rs50\x64\Release\fake_rs50"
$inf = Join-Path $rel "fake_rs50.inf"
$devcon = "C:\Program Files (x86)\Windows Kits\10\Tools\10.0.26100.0\x64\devcon.exe"

if (-not (Test-Path $inf)) { throw "Missing $inf — build the driver first" }

# Remove any previously staged package for fake_rs50.inf
$enum = pnputil /enum-drivers 2>&1 | Out-String
$blocks = $enum -split "(\r?\n){2,}" | Where-Object { $_ -match "Original Name:\s+fake_rs50\.inf" }
foreach ($b in $blocks) {
    if ($b -match "Published Name:\s+(\S+\.inf)") {
        $pub = $Matches[1]
        "Removing previously staged package $pub" | Add-Content $log
        pnputil /delete-driver $pub /uninstall 2>&1 | Add-Content $log
    }
}

Write-Host "== Cleaning stale nodes =="
& $devcon remove "root\FakeRs50" 2>&1 | Add-Content $log

Write-Host "== Staging driver package =="
pnputil /add-driver $inf /install 2>&1 | Add-Content $log

Write-Host "== Creating root device (root\FakeRs50) =="
& $devcon install $inf "root\FakeRs50" 2>&1 | Add-Content $log

"=== done ===" | Add-Content $log