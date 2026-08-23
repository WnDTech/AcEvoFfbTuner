$out = "C:\Users\paul_\AppData\Local\Temp\kilo\netfix.txt"
"=== netfix $(Get-Date) ===" | Set-Content $out

Connect-VMNetworkAdapter -VMName FakeWheelVM -SwitchName "Default Switch"
"Adapter connected to Default Switch" | Add-Content $out

Stop-VM -Name FakeWheelVM -Force
Start-VM -Name FakeWheelVM
"VM restarted" | Add-Content $out
Start-Sleep 30

$vm = Get-VM -Name FakeWheelVM
"State: $($vm.State)" | Add-Content $out
Get-VMNetworkAdapter -VMName FakeWheelVM | Select-Object SwitchName, Connected | Format-List | Out-String | Add-Content $out
"=== done ===" | Add-Content $out