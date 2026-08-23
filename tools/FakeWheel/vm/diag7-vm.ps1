param(
    [string]$VmIp = "172.26.169.198",
    [string]$VmUser = "FakeWheel",
    [string]$VmPass = "FakeWheel!2026"
)
$log = "C:\Users\paul_\AppData\Local\Temp\kilo\diag7-vm.log"
"=== diag7 $(Get-Date) ===" | Set-Content $log
$cred = New-Object System.Management.Automation.PSCredential($VmUser, (ConvertTo-SecureString $VmPass -AsPlainText -Force))
$s = New-PSSession -ComputerName $VmIp -Credential $cred -ErrorAction Stop

"--- sc start vhf ---" | Add-Content $log
Invoke-Command -Session $s -ScriptBlock { sc.exe start vhf 2>&1 } | Add-Content $log

"--- vhf state ---" | Add-Content $log
Invoke-Command -Session $s -ScriptBlock { sc.exe query vhf 2>&1 | Select-String "STATE|WIN32" } | Add-Content $log

$s.Dispose()
"=== done ===" | Add-Content $log