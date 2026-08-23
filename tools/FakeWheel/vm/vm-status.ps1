$out = "C:\Users\paul_\AppData\Local\Temp\kilo\vm-status.txt"
$vm = Get-VM -Name FakeWheelVM -ErrorAction SilentlyContinue
if (-not $vm) { "NO VM" | Set-Content $out; exit 0 }

"State: $($vm.State) | Uptime: $([math]::Round($vm.Uptime.TotalMinutes,1)) min" | Set-Content $out
$ips = Get-VMNetworkAdapter -VMName FakeWheelVM | Select-Object -ExpandProperty IPAddresses
"IPs: $($ips -join ',')" | Add-Content $out

$ip = $ips | Where-Object { $_ -match '^172\.|^10\.|^192\.168\.' } | Select-Object -First 1
if ($ip) {
    try {
        Test-WSMan -ComputerName $ip -ErrorAction Stop | Out-Null
        "WinRM: YES ($ip)" | Add-Content $out
    } catch {
        "WinRM: no ($ip)" | Add-Content $out
    }
}