$out = "C:\Users\paul_\AppData\Local\Temp\kilo\vm-progress.txt"
$vhd = "C:\Users\Public\Documents\Hyper-V\Virtual hard disks\FakeWheelVM.vhdx"
$size = (Get-Item $vhd -ErrorAction SilentlyContinue).Length
"VHDX: $([math]::Round($size/1GB,2)) GB" | Set-Content $out

$vm = Get-VM -Name FakeWheelVM
"State: $($vm.State) | Uptime: $([math]::Round($vm.Uptime.TotalMinutes,1)) min" | Add-Content $out

$vm | Get-VMIntegrationService | Select-Object Name, Enabled, @{n='Status';e={$_.PrimaryStatusDescription}} | Format-Table -AutoSize | Out-String | Add-Content $out

$ips = Get-VMNetworkAdapter -VMName FakeWheelVM | Select-Object -ExpandProperty IPAddresses
"IPs: $($ips -join ',')" | Add-Content $out