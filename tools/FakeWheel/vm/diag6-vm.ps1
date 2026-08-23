param(
    [string]$VmIp = "172.26.169.198",
    [string]$VmUser = "FakeWheel",
    [string]$VmPass = "FakeWheel!2026"
)
$log = "C:\Users\paul_\AppData\Local\Temp\kilo\diag6-vm.log"
"=== diag6 $(Get-Date) ===" | Set-Content $log
$cred = New-Object System.Management.Automation.PSCredential($VmUser, (ConvertTo-SecureString $VmPass -AsPlainText -Force))
$s = New-PSSession -ComputerName $VmIp -Credential $cred -ErrorAction Stop

"--- system log: device start errors ---" | Add-Content $log
Invoke-Command -Session $s -ScriptBlock {
    Get-WinEvent -LogName System -MaxEvents 300 -ErrorAction SilentlyContinue |
        Where-Object { $_.TimeCreated -gt (Get-Date).AddMinutes(-15) -and ($_.Message -match 'FakeRs50|SAMPLE|vhf|entry point|0xC00000B9') } |
        Select-Object -First 5 TimeCreated, Id, ProviderName, @{n='Msg';e={$_.Message.Substring(0, [Math]::Min(350, $_.Message.Length))}} |
        Format-List | Out-String
} | Add-Content $log

"--- vhf.sys export check (file size as proxy) ---" | Add-Content $log
Invoke-Command -Session $s -ScriptBlock { Get-Item C:\Windows\System32\drivers\vhf.sys | Select-Object Length, LastWriteTime | Format-List | Out-String } | Add-Content $log

"--- sc query vhf ---" | Add-Content $log
Invoke-Command -Session $s -ScriptBlock { sc.exe query vhf 2>&1 | Select-String "STATE|WIN32" } | Add-Content $log

$s.Dispose()
"=== done ===" | Add-Content $log