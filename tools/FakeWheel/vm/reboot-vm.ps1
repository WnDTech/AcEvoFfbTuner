param(
    [string]$VmIp = "172.26.169.198",
    [string]$VmUser = "FakeWheel",
    [string]$VmPass = "FakeWheel!2026"
)
$log = "C:\Users\paul_\AppData\Local\Temp\kilo\reboot-vm.log"
"=== reboot-vm $(Get-Date) ===" | Set-Content $log
$cred = New-Object System.Management.Automation.PSCredential($VmUser, (ConvertTo-SecureString $VmPass -AsPlainText -Force))
$s = New-PSSession -ComputerName $VmIp -Credential $cred -ErrorAction Stop

# Restore the vhf lower filter first (needed for VHF children)
Invoke-Command -Session $s -ScriptBlock {
    reg add "HKLM\SYSTEM\CurrentControlSet\Enum\ROOT\SAMPLE\0000" /v LowerFilters /t REG_MULTI_SZ /d vhf /f 2>&1
    Restart-Computer -Force
} | Add-Content $log
$s.Dispose()
"reboot issued" | Add-Content $log
"=== done ===" | Add-Content $log