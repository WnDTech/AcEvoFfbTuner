$out = "C:\Users\paul_\AppData\Local\Temp\kilo\vm-bootfix.txt"
"=== boot fix $(Get-Date) ===" | Set-Content $out

$fw = Get-VMFirmware -VMName FakeWheelVM
"Current boot order:" | Add-Content $out
$fw.BootOrder | ForEach-Object { "  $($_.DeviceType) $($_.Path)" } | Add-Content $out

$dvd = Get-VMDvdDrive -VMName FakeWheelVM
if ($dvd) {
    Set-VMFirmware -VMName FakeWheelVM -BootOrder $dvd | Out-Null
    "Boot order set to DVD ($($dvd.Path))" | Add-Content $out
}

Stop-VM -Name FakeWheelVM -Force
Start-VM -Name FakeWheelVM
"VM restarted" | Add-Content $out
"=== done ===" | Add-Content $out