param(
    [string]$VmIp = "172.26.169.198",
    [string]$VmUser = "FakeWheel",
    [string]$VmPass = "FakeWheel!2026"
)
$log = "C:\Users\paul_\AppData\Local\Temp\kilo\ci-vm.log"
"=== ci-vm $(Get-Date) ===" | Set-Content $log
$cred = New-Object System.Management.Automation.PSCredential($VmUser, (ConvertTo-SecureString $VmPass -AsPlainText -Force))
$s = New-PSSession -ComputerName $VmIp -Credential $cred -ErrorAction Stop

# 1. Trigger a fresh load attempt
Invoke-Command -Session $s -ScriptBlock {
    & C:\FakeWheel\devcon.exe restart "root\FakeRs50Kmdf" 2>&1
    sc.exe start FakeRs50Kmdf 2>&1
} | Add-Content $log
Start-Sleep 8

# 2. CodeIntegrity operational log
"--- CodeIntegrity/Operational ---" | Add-Content $log
Invoke-Command -Session $s -ScriptBlock {
    Get-WinEvent -LogName "Microsoft-Windows-CodeIntegrity/Operational" -MaxEvents 60 -ErrorAction SilentlyContinue |
        Where-Object { $_.TimeCreated -gt (Get-Date).AddMinutes(-10) } |
        Select-Object -First 12 TimeCreated, Id, LevelDisplayName, @{n='Msg';e={$_.Message.Substring(0, [Math]::Min(500, $_.Message.Length))}} |
        Format-List | Out-String
} | Add-Content $log

# 3. Kernel-PnP latest failure
"--- Kernel-PnP 411 ---" | Add-Content $log
Invoke-Command -Session $s -ScriptBlock {
    Get-WinEvent -LogName "Microsoft-Windows-Kernel-PnP/Configuration" -MaxEvents 10 -ErrorAction SilentlyContinue |
        Where-Object { $_.Id -eq 411 } |
        Select-Object -First 2 TimeCreated, @{n='Msg';e={$_.Message.Substring(0, [Math]::Min(450, $_.Message.Length))}} |
        Format-List | Out-String
} | Add-Content $log

# 4. Testsigning flags + boot flags
"--- bcdedit flags ---" | Add-Content $log
Invoke-Command -Session $s -ScriptBlock { bcdedit /enum '{current}' 2>&1 } | Add-Content $log

$s.Dispose()
"=== done ===" | Add-Content $log