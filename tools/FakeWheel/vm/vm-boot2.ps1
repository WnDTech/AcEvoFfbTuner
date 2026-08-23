$out = "C:\Users\paul_\AppData\Local\Temp\kilo\vm-boot2.txt"
"=== boot attempt 2 $(Get-Date) ===" | Set-Content $out

# 1. Remove the unattend injection (can hang the boot if it's the problem)
$unattendDir = "C:\ProgramData\Microsoft\Windows\Hyper-V\Unattend"
Remove-Item (Join-Path $unattendDir "autounattend.xml") -Force -ErrorAction SilentlyContinue
"Unattend removed" | Add-Content $out

# 2. Stop the VM hard, ensure the DVD is attached, restart
Stop-VM -Name FakeWheelVM -Force
$dvd = Get-VMDvdDrive -VMName FakeWheelVM
if (-not $dvd) {
    Add-VMDvdDrive -VMName FakeWheelVM -Path "C:\Users\paul_\AppData\Local\Temp\kilo\Win11_25H2_English_x64.iso" | Out-Null
    $dvd = Get-VMDvdDrive -VMName FakeWheelVM
}
"DVD: $($dvd.Path)" | Add-Content $out
Set-VMFirmware -VMName FakeWheelVM -BootOrder $dvd | Out-Null
Start-VM -Name FakeWheelVM
"VM restarted" | Add-Content $out

# 3. Watch for heartbeat contact + raw VHDX growth for ~2 minutes
$vhd = "C:\Users\Public\Documents\Hyper-V\Virtual hard disks\FakeWheelVM.vhdx"
for ($i = 0; $i -lt 8; $i++) {
    Start-Sleep 15
    $vm = Get-VM -Name FakeWheelVM
    $hb = ($vm | Get-VMIntegrationService -Name Heartbeat).PrimaryStatusDescription
    $size = (Get-Item $vhd).Length
    "[$([datetime]::Now.ToString('HH:mm:ss'))] state=$($vm.State) hb=$hb vhdx=$size" | Add-Content $out
}
"=== done ===" | Add-Content $out