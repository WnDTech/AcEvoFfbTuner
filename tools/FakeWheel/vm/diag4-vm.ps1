param(
    [string]$VmIp = "172.26.169.198",
    [string]$VmUser = "FakeWheel",
    [string]$VmPass = "FakeWheel!2026"
)
$log = "C:\Users\paul_\AppData\Local\Temp\kilo\diag4-vm.log"
"=== diag4 $(Get-Date) ===" | Set-Content $log
$cred = New-Object System.Management.Automation.PSCredential($VmUser, (ConvertTo-SecureString $VmPass -AsPlainText -Force))
$s = New-PSSession -ComputerName $VmIp -Credential $cred -ErrorAction Stop

"--- sc start FakeRs50Kmdf ---" | Add-Content $log
Invoke-Command -Session $s -ScriptBlock { sc.exe start FakeRs50Kmdf 2>&1; sc.exe query FakeRs50Kmdf 2>&1 | Select-String "STATE|WIN32" } | Add-Content $log

"--- setupapi full context around the error ---" | Add-Content $log
Invoke-Command -Session $s -ScriptBlock {
    $c = Get-Content C:\Windows\INF\setupapi.dev.log -Tail 400
    $idx = ($c | Select-String "not started" | Select-Object -Last 1).LineNumber
    if ($idx) { $c[($idx-12)..($idx+2)] | ForEach-Object { $_.Substring(0, [Math]::Min(200, $_.Length)) } }
} | Add-Content $log

$s.Dispose()
"=== done ===" | Add-Content $log