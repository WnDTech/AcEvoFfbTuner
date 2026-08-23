param(
    [string]$VmIp = "172.26.169.198",
    [string]$VmUser = "FakeWheel",
    [string]$VmPass = "FakeWheel!2026"
)
$log = "C:\Users\paul_\AppData\Local\Temp\kilo\diag10-vm.log"
"=== diag10 $(Get-Date) ===" | Set-Content $log
$cred = New-Object System.Management.Automation.PSCredential($VmUser, (ConvertTo-SecureString $VmPass -AsPlainText -Force))
$s = New-PSSession -ComputerName $VmIp -Credential $cred -ErrorAction Stop

Invoke-Command -Session $s -ScriptBlock {
    # Remove the vhf lower filter to isolate
    reg delete "HKLM\SYSTEM\CurrentControlSet\Enum\ROOT\SAMPLE\0000" /v LowerFilters /f 2>&1
    & C:\FakeWheel\devcon.exe restart "root\FakeRs50Kmdf" 2>&1
} | Add-Content $log
Start-Sleep 10

Invoke-Command -Session $s -ScriptBlock {
    Get-PnpDevice -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -match 'ROOT\\SAMPLE' } |
        Select-Object Status, @{n='Prob';e={$_.Problem}}, InstanceId | Format-Table -AutoSize | Out-String
    sc.exe query FakeRs50Kmdf 2>&1 | Select-String "STATE|WIN32"
} | Add-Content $log

$s.Dispose()
"=== done ===" | Add-Content $log