# enable-testing.ps1 — run ELEVATED (once).
#   .\enable-testing.ps1
# Enables Windows test signing (needs a reboot) and trusts the WDK test cert.

$ErrorActionPreference = "Stop"

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Run this script elevated (admin)."
}

Write-Host "== Enabling test signing =="
bcdedit /set testsigning on
if ($LASTEXITCODE -ne 0) { throw "bcdedit failed" }

$cer = Join-Path $PSScriptRoot "driver\fake_rs50\x64\Release\FakeRs50.cer"
if (-not (Test-Path $cer)) { throw "Missing $cer — build the driver first" }

Write-Host "== Trusting test certificate =="
Import-Certificate -FilePath $cer -CertStoreLocation Cert:\LocalMachine\Root | Out-Null
Import-Certificate -FilePath $cer -CertStoreLocation Cert:\LocalMachine\TrustedPublisher | Out-Null

Write-Host ""
Write-Host "Test signing is ON and the FakeRs50 test cert is trusted."
Write-Host "REBOOT the machine, then run install-driver.ps1 (elevated)."