param(
    [string]$VmIp = "172.26.169.198",
    [string]$VmUser = "FakeWheel",
    [string]$VmPass = "FakeWheel!2026"
)
$ErrorActionPreference = "Continue"
$log = "C:\Users\paul_\AppData\Local\Temp\kilo\diag2-vm.log"
"=== diag2-vm $(Get-Date) ===" | Set-Content $log

$cred = New-Object System.Management.Automation.PSCredential($VmUser, (ConvertTo-SecureString $VmPass -AsPlainText -Force))
$s = New-PSSession -ComputerName $VmIp -Credential $cred -ErrorAction Stop

"--- testsigning ---" | Add-Content $log
Invoke-Command -Session $s -ScriptBlock { bcdedit /enum '{current}' 2>&1 | Select-String testsigning } | Add-Content $log

"--- root device state ---" | Add-Content $log
Invoke-Command -Session $s -ScriptBlock {
    Get-PnpDevice -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -match 'ROOT\\SAMPLE' } |
        Select-Object Status, FriendlyName, InstanceId, @{n='Prob';e={$_.Problem}}, @{n='ProbDesc';e={$_.ProblemDescription}} | Format-List | Out-String
} | Add-Content $log

"--- setupapi start error ---" | Add-Content $log
Invoke-Command -Session $s -ScriptBlock {
    Get-Content C:\Windows\INF\setupapi.dev.log -Tail 250 | Select-String -Pattern "!!!|FakeRs50Kmdf.sys|error 577|0x" | Select-Object -Last 10 |
        ForEach-Object { $_.Line.Substring(0, [Math]::Min(200, $_.Line.Length)) }
} | Add-Content $log

"--- sys exists ---" | Add-Content $log
Invoke-Command -Session $s -ScriptBlock { Get-Item C:\Windows\System32\drivers\FakeRs50Kmdf.sys | Select-Object Length, LastWriteTime | Format-List | Out-String } | Add-Content $log

$s.Dispose()
"=== done ===" | Add-Content $log