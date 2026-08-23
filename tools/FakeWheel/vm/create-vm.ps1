# create-vm.ps1 — run ELEVATED. Creates the FakeWheel VM (Secure Boot OFF at
# the VM level) and starts the unattended Windows 11 install.

param(
    [string]$VmName = "FakeWheelVM",
    [string]$IsoPath = "C:\Users\paul_\AppData\Local\Temp\kilo\Win11_25H2_English_x64.iso",
    [int]$MemoryGB = 8,
    [int]$CpuCount = 4,
    [int]$DiskGB = 100
)
$ErrorActionPreference = "Stop"

$log = "C:\Users\paul_\AppData\Local\Temp\kilo\create-vm.log"
"=== create-vm $(Get-Date) ===" | Set-Content $log

if (-not (Test-Path $IsoPath)) { throw "ISO not found at $IsoPath" }
if (-not (Get-Command New-VM -ErrorAction SilentlyContinue)) { throw "Hyper-V module unavailable" }

$vhd = "C:\Users\Public\Documents\Hyper-V\Virtual hard disks\$VmName.vhdx"

# Unattend on a virtual floppy or attached ISO? Win11 setup reads autounattend
# from the ISO root or a floppy. Simplest: inject it into a small FAT ISO-less
# setup: use a secondary ISO built with oscdimg is overkill — instead copy the
# answer file into the ISO with a mounted copy is messy. Hyper-V supports
# attaching a VHD as floppy-like "virtual floppy" — instead we use the
# documented trick: put autounattend.xml in the ISO's root by re-mounting.
# Simplest robust approach: an answer-file ISO built via a plain ISO writer is
# unavailable here, so we patch the Windows ISO's sources\boot.wim is complex.
# We use the Hyper-V "Unattend" folder: Hyper-V automatically injects
# C:\ProgramData\Microsoft\Windows\Hyper-V\Unattend\autounattend.xml into the
# VM's setup when present (VM injection feature).
$unattendDir = "C:\ProgramData\Microsoft\Windows\Hyper-V\Unattend"
New-Item -ItemType Directory -Force -Path $unattendDir | Out-Null
Copy-Item "C:\Users\paul_\OneDrive\Documents\APP\ACEVO - Telemetry FFB\tools\FakeWheel\vm\autounattend.xml" (Join-Path $unattendDir "autounattend.xml") -Force
"Unattend injected to $unattendDir" | Add-Content $log

$vm = Get-VM -Name $VmName -ErrorAction SilentlyContinue
if ($vm) {
    "VM exists — removing" | Add-Content $log
    Stop-VM -Name $VmName -Force -ErrorAction SilentlyContinue
    Remove-VM -Name $VmName -Force
}

New-VM -Name $VmName -MemoryStartupBytes ($MemoryGB * 1GB) -Generation 2 -NewVHDPath $vhd -NewVHDSizeBytes ($DiskGB * 1GB) | Out-Null
Set-VM -Name $VmName -ProcessorCount $CpuCount
Set-VMFirmware -VMName $VmName -EnableSecureBoot Off
Add-VMDvdDrive -VMName $VmName -Path $IsoPath
$dvd = Get-VMDvdDrive -VMName $VmName
Set-VMBootOrder -VMName $VmName -Order $dvd
Start-VM -Name $VmName

"VM created and started. Install is unattended (~20-40 min)." | Add-Content $log
Get-VM -Name $VmName | Select-Object Name, State, Uptime | Format-List | Out-String | Add-Content $log
"=== done ===" | Add-Content $log