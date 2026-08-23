param(
    [string]$VmIp = "172.26.169.198",
    [string]$VmUser = "FakeWheel",
    [string]$VmPass = "FakeWheel!2026"
)
$ErrorActionPreference = "Continue"
$log = "C:\Users\paul_\AppData\Local\Temp\kilo\diag-vm.log"
"=== diag-vm $(Get-Date) ===" | Set-Content $log

$cred = New-Object System.Management.Automation.PSCredential($VmUser, (ConvertTo-SecureString $VmPass -AsPlainText -Force))
$s = New-PSSession -ComputerName $VmIp -Credential $cred -ErrorAction Stop

"--- bcdedit testsigning ---" | Add-Content $log
Invoke-Command -Session $s -ScriptBlock { bcdedit /enum {current} 2>&1 } | Add-Content $log

"--- device state ---" | Add-Content $log
Invoke-Command -Session $s -ScriptBlock {
    Get-PnpDevice -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -match 'FakeRs50Kmdf|VHF' } |
        Select-Object Status, Class, FriendlyName, InstanceId, @{n='Problem';e={$_.Problem}} | Format-Table -AutoSize | Out-String
} | Add-Content $log

"--- setupapi tail (load errors) ---" | Add-Content $log
Invoke-Command -Session $s -ScriptBlock {
    Get-Content C:\Windows\INF\setupapi.dev.log -Tail 120 | Select-String -Pattern "!!!|FakeRs50|oem2" | Select-Object -Last 12 |
        ForEach-Object { $_.Line.Substring(0, [Math]::Min(200, $_.Line.Length)) }
} | Add-Content $log

"--- driver service ---" | Add-Content $log
Invoke-Command -Session $s -ScriptBlock { sc.exe query FakeRs50Kmdf 2>&1; sc.exe qc FakeRs50Kmdf 2>&1 } | Add-Content $log

"--- G HUB install location ---" | Add-Content $log
Invoke-Command -Session $s -ScriptBlock { Get-ChildItem "C:\Program Files" -Directory -Filter "*LGHUB*" -ErrorAction SilentlyContinue | Select-Object -ExpandProperty FullName; Get-ChildItem "C:\Program Files (x86)" -Directory -Filter "*LGHUB*" -ErrorAction SilentlyContinue | Select-Object -ExpandProperty FullName } | Add-Content $log

$s.Dispose()
"=== done ===" | Add-Content $log