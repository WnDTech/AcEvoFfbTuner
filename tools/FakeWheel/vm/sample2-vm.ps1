param(
    [string]$VmIp = "172.26.169.198",
    [string]$VmUser = "FakeWheel",
    [string]$VmPass = "FakeWheel!2026"
)
$log = "C:\Users\paul_\AppData\Local\Temp\kilo\sample2-vm.log"
"=== sample2 $(Get-Date) ===" | Set-Content $log
$cred = New-Object System.Management.Automation.PSCredential($VmUser, (ConvertTo-SecureString $VmPass -AsPlainText -Force))
$s = New-PSSession -ComputerName $VmIp -Credential $cred -ErrorAction Stop

Invoke-Command -Session $s -ScriptBlock {
    Get-PnpDevice -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -match 'ROOT\\SAMPLE' } |
        Select-Object Status, FriendlyName, InstanceId, @{n='Prob';e={$_.Problem}} | Format-Table -AutoSize | Out-String
    Get-PnpDevice -Class HIDClass -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -match 'ROOT|VHID' } |
        Select-Object Status, FriendlyName, InstanceId | Format-Table -AutoSize | Out-String
} | Add-Content $log

$s.Dispose()
"=== done ===" | Add-Content $log