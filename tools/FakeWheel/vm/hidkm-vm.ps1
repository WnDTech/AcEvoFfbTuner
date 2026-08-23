param(
    [string]$VmIp = "172.26.169.198",
    [string]$VmUser = "FakeWheel",
    [string]$VmPass = "FakeWheel!2026"
)
$log = "C:\Users\paul_\AppData\Local\Temp\kilo\hidkm-vm.log"
"=== hidkm-vm $(Get-Date) ===" | Set-Content $log
$cred = New-Object System.Management.Automation.PSCredential($VmUser, (ConvertTo-SecureString $VmPass -AsPlainText -Force))
$s = New-PSSession -ComputerName $VmIp -Credential $cred -ErrorAction Stop

$src = "C:\Users\paul_\OneDrive\Documents\APP\ACEVO - Telemetry FFB\tools\FakeWheel\driver\fake_rs50hidkm\x64\Release\fake_rs50hidkm"
Copy-Item "$src\fake_rs50hidkm.inf", "$src\fake_rs50hidkm.cat", "$src\FakeRs50HidKm.sys" -Destination C:\FakeWheel -ToSession $s -Force

Invoke-Command -Session $s -ScriptBlock {
    pnputil /add-driver C:\FakeWheel\fake_rs50hidkm.inf /install 2>&1
    & C:\FakeWheel\devcon.exe install C:\FakeWheel\fake_rs50hidkm.inf "root\FakeRs50HidKm" 2>&1
} | Add-Content $log
Start-Sleep 12

"--- FakeRs50HidKm node ---" | Add-Content $log
Invoke-Command -Session $s -ScriptBlock {
    Get-PnpDevice -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -match 'ROOT\\SAMPLE' } |
        Select-Object Status, FriendlyName, InstanceId, @{n='Prob';e={$_.Problem}} | Format-Table -AutoSize | Out-String
} | Add-Content $log

"--- HID devices under the fake node ---" | Add-Content $log
Invoke-Command -Session $s -ScriptBlock {
    Get-PnpDevice -Class HIDClass -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -match 'VHID|ROOT|mshidkmdf|Fake' } |
        Select-Object Status, FriendlyName, InstanceId | Format-Table -AutoSize | Out-String
} | Add-Content $log

"--- capture log ---" | Add-Content $log
Invoke-Command -Session $s -ScriptBlock {
    if (Test-Path C:\Windows\Temp\FakeRs50.log) { "SIZE: $((Get-Item C:\Windows\Temp\FakeRs50.log).Length)" | Out-String } else { "no capture log yet" }
} | Add-Content $log

$s.Dispose()
"=== done ===" | Add-Content $log