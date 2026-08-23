param(
    [string]$VmIp = "172.26.169.198",
    [string]$VmUser = "FakeWheel",
    [string]$VmPass = "FakeWheel!2026"
)
$log = "C:\Users\paul_\AppData\Local\Temp\kilo\check-vm.log"
"=== check-vm $(Get-Date) ===" | Set-Content $log
$cred = New-Object System.Management.Automation.PSCredential($VmUser, (ConvertTo-SecureString $VmPass -AsPlainText -Force))
$s = New-PSSession -ComputerName $VmIp -Credential $cred -ErrorAction Stop

"--- device + VHF ---" | Add-Content $log
Invoke-Command -Session $s -ScriptBlock {
    Get-PnpDevice -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -match 'ROOT\\SAMPLE|VHF|HID_DEVICE_SYSTEM_VHF' } |
        Select-Object Status, Class, FriendlyName, InstanceId, @{n='Prob';e={$_.Problem}} | Format-Table -AutoSize | Out-String
} | Add-Content $log

"--- service ---" | Add-Content $log
Invoke-Command -Session $s -ScriptBlock { sc.exe query FakeRs50Kmdf 2>&1 | Select-String "STATE|WIN32" } | Add-Content $log

"--- capture log ---" | Add-Content $log
Invoke-Command -Session $s -ScriptBlock {
    if (Test-Path C:\Windows\Temp\FakeRs50.log) {
        "SIZE: $((Get-Item C:\Windows\Temp\FakeRs50.log).Length)" | Out-String
        Get-Content C:\Windows\Temp\FakeRs50.log -TotalCount 20 | Out-String
    } else { "no capture log yet" }
} | Add-Content $log

$s.Dispose()
"=== done ===" | Add-Content $log