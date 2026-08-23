$ErrorActionPreference = "Stop"
$log = "C:\Users\paul_\AppData\Local\Temp\kilo\create-vm2.log"
"=== create-vm2 $(Get-Date) ===" | Set-Content $log

$vhdDir = "C:\Users\Public\Documents\Hyper-V\Virtual hard disks"
New-Item -ItemType Directory -Force -Path $vhdDir | Out-Null
$vhd = Join-Path $vhdDir "FakeWheelVM.vhdx"
$iso = "C:\Users\paul_\AppData\Local\Temp\kilo\Win11_25H2_English_x64.iso"

$vm = Get-VM -Name FakeWheelVM -ErrorAction SilentlyContinue
if ($vm) { Stop-VM -Name FakeWheelVM -Force -ErrorAction SilentlyContinue; Remove-VM -Name FakeWheelVM -Force }
Remove-Item $vhd -Force -ErrorAction SilentlyContinue

try {
    New-VM -Name FakeWheelVM -MemoryStartupBytes 8GB -Generation 2 -NewVHDPath $vhd -NewVHDSizeBytes 100GB | Out-Null
    Set-VM -Name FakeWheelVM -ProcessorCount 4 | Out-Null
    Set-VMFirmware -VMName FakeWheelVM -EnableSecureBoot Off | Out-Null
    Add-VMDvdDrive -VMName FakeWheelVM -Path $iso | Out-Null
    Start-VM -Name FakeWheelVM | Out-Null
    "VM created + started" | Add-Content $log
} catch {
    "ERROR: $($_.Exception.Message)" | Add-Content $log
    exit 1
}

Get-VM -Name FakeWheelVM | Select-Object Name, State, Generation | Format-List | Out-String | Add-Content $log
"=== done ===" | Add-Content $log