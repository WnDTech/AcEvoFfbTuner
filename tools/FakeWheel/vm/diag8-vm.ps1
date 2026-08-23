param(
    [string]$VmIp = "172.26.169.198",
    [string]$VmUser = "FakeWheel",
    [string]$VmPass = "FakeWheel!2026"
)
$log = "C:\Users\paul_\AppData\Local\Temp\kilo\diag8-vm.log"
"=== diag8 $(Get-Date) ===" | Set-Content $log
$cred = New-Object System.Management.Automation.PSCredential($VmUser, (ConvertTo-SecureString $VmPass -AsPlainText -Force))
$s = New-PSSession -ComputerName $VmIp -Credential $cred -ErrorAction Stop

Invoke-Command -Session $s -ScriptBlock {
    & C:\FakeWheel\devcon.exe restart "root\FakeRs50Kmdf" 2>&1
} | Add-Content $log
Start-Sleep 10

Invoke-Command -Session $s -ScriptBlock {
    Get-PnpDevice -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -match 'ROOT\\SAMPLE|VHF' } |
        Select-Object Status, Class, FriendlyName, InstanceId, @{n='Prob';e={$_.Problem}} | Format-Table -AutoSize | Out-String
} | Add-Content $log

$s.Dispose()
"=== done ===" | Add-Content $log