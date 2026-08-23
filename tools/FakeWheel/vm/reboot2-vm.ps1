param(
    [string]$VmIp = "172.26.169.198",
    [string]$VmUser = "FakeWheel",
    [string]$VmPass = "FakeWheel!2026"
)
$log = "C:\Users\paul_\AppData\Local\Temp\kilo\reboot2-vm.log"
"=== reboot2 $(Get-Date) ===" | Set-Content $log
$cred = New-Object System.Management.Automation.PSCredential($VmUser, (ConvertTo-SecureString $VmPass -AsPlainText -Force))
$s = New-PSSession -ComputerName $VmIp -Credential $cred -ErrorAction Stop
Invoke-Command -Session $s -ScriptBlock { Restart-Computer -Force } | Add-Content $log
$s.Dispose()
"reboot issued" | Add-Content $log
"=== done ===" | Add-Content $log