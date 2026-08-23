param(
    [string]$VmIp = "172.26.169.198",
    [string]$VmUser = "FakeWheel",
    [string]$VmPass = "FakeWheel!2026"
)
$log = "C:\Users\paul_\AppData\Local\Temp\kilo\nic-vm.log"
"=== nic-vm $(Get-Date) ===" | Set-Content $log
$cred = New-Object System.Management.Automation.PSCredential($VmUser, (ConvertTo-SecureString $VmPass -AsPlainText -Force))
$s = New-PSSession -ComputerName $VmIp -Credential $cred -ErrorAction Stop

# Check Smart App Control / V&R policy state
"--- V&R policy state ---" | Add-Content $log
Invoke-Command -Session $s -ScriptBlock {
    Get-ItemProperty "HKLM\SYSTEM\CurrentControlSet\Control\CI\Policy" -ErrorAction SilentlyContinue |
        Select-Object VerifiedAndReputablePolicyState, VerifiedAndReputableAggressivenessPolicyState | Format-List | Out-String
} | Add-Content $log

# Enable nointegritychecks
Invoke-Command -Session $s -ScriptBlock {
    bcdedit /set nointegritychecks on 2>&1
    Restart-Computer -Force
} | Add-Content $log
$s.Dispose()
"reboot issued" | Add-Content $log
"=== done ===" | Add-Content $log