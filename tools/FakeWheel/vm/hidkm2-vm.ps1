param(
    [string]$VmIp = "172.26.169.198",
    [string]$VmUser = "FakeWheel",
    [string]$VmPass = "FakeWheel!2026"
)
$log = "C:\Users\paul_\AppData\Local\Temp\kilo\hidkm2-vm.log"
"=== hidkm2 $(Get-Date) ===" | Set-Content $log
$cred = New-Object System.Management.Automation.PSCredential($VmUser, (ConvertTo-SecureString $VmPass -AsPlainText -Force))
$s = New-PSSession -ComputerName $VmIp -Credential $cred -ErrorAction Stop

"--- problem status 0002 ---" | Add-Content $log
Invoke-Command -Session $s -ScriptBlock {
    Get-PnpDeviceProperty -InstanceId 'ROOT\SAMPLE\0002' -KeyName 'DEVPKEY_Device_ProblemStatus' -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Data | Out-String
} | Add-Content $log

"--- Kernel-PnP 411 ---" | Add-Content $log
Invoke-Command -Session $s -ScriptBlock {
    Get-WinEvent -LogName "Microsoft-Windows-Kernel-PnP/Configuration" -MaxEvents 15 -ErrorAction SilentlyContinue |
        Where-Object { $_.Id -eq 411 } |
        Select-Object -First 1 TimeCreated, @{n='Msg';e={$_.Message.Substring(0, [Math]::Min(450, $_.Message.Length))}} |
        Format-List | Out-String
} | Add-Content $log

"--- setupapi tail ---" | Add-Content $log
Invoke-Command -Session $s -ScriptBlock {
    Get-Content C:\Windows\INF\setupapi.dev.log -Tail 150 | Select-String -Pattern "!!!|FakeRs50HidKm|problem status|not started" | Select-Object -Last 8 |
        ForEach-Object { $_.Line.Substring(0, [Math]::Min(190, $_.Line.Length)) }
} | Add-Content $log

"--- services ---" | Add-Content $log
Invoke-Command -Session $s -ScriptBlock { sc.exe query FakeRs50HidKm 2>&1 | Select-String "STATE|WIN32"; sc.exe query mshidkmdf 2>&1 | Select-String "STATE|WIN32" } | Add-Content $log

$s.Dispose()
"=== done ===" | Add-Content $log