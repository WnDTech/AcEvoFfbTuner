param(
    [string]$VmIp = "172.26.169.198",
    [string]$VmUser = "FakeWheel",
    [string]$VmPass = "FakeWheel!2026"
)
$log = "C:\Users\paul_\AppData\Local\Temp\kilo\diag3-vm.log"
"=== diag3 $(Get-Date) ===" | Set-Content $log
$cred = New-Object System.Management.Automation.PSCredential($VmUser, (ConvertTo-SecureString $VmPass -AsPlainText -Force))
$s = New-PSSession -ComputerName $VmIp -Credential $cred -ErrorAction Stop

"--- sys in VM ---" | Add-Content $log
Invoke-Command -Session $s -ScriptBlock {
    Get-Item C:\Windows\System32\drivers\FakeRs50Kmdf.sys | Select-Object Length, LastWriteTime | Format-List | Out-String
    Get-Item C:\FakeWheel\FakeRs50Kmdf.sys | Select-Object Length, LastWriteTime | Format-List | Out-String
} | Add-Content $log

"--- problem status ---" | Add-Content $log
Invoke-Command -Session $s -ScriptBlock {
    $d = Get-PnpDevice -InstanceId 'ROOT\SAMPLE\0000' -ErrorAction SilentlyContinue
    Get-PnpDeviceProperty -InstanceId 'ROOT\SAMPLE\0000' -KeyName 'DEVPKEY_Device_ProblemStatus' -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Data | Out-String
} | Add-Content $log

"--- setupapi tail ---" | Add-Content $log
Invoke-Command -Session $s -ScriptBlock {
    Get-Content C:\Windows\INF\setupapi.dev.log -Tail 120 | Select-String -Pattern "!!!|0xc00000b9|problem status" | Select-Object -Last 6 | ForEach-Object { $_.Line.Substring(0, [Math]::Min(200, $_.Line.Length)) }
} | Add-Content $log

$s.Dispose()
"=== done ===" | Add-Content $log