param(
    [string]$VmIp = "172.26.169.198",
    [string]$VmUser = "FakeWheel",
    [string]$VmPass = "FakeWheel!2026"
)
$log = "C:\Users\paul_\AppData\Local\Temp\kilo\diag9-vm.log"
"=== diag9 $(Get-Date) ===" | Set-Content $log
$cred = New-Object System.Management.Automation.PSCredential($VmUser, (ConvertTo-SecureString $VmPass -AsPlainText -Force))
$s = New-PSSession -ComputerName $VmIp -Credential $cred -ErrorAction Stop

Invoke-Command -Session $s -ScriptBlock {
    Get-WinEvent -LogName "Microsoft-Windows-Kernel-PnP/Configuration" -MaxEvents 20 -ErrorAction SilentlyContinue |
        Where-Object { $_.Id -eq 411 -or $_.Id -eq 410 } |
        Select-Object -First 3 TimeCreated, Id, @{n='Msg';e={$_.Message.Substring(0, [Math]::Min(500, $_.Message.Length))}} |
        Format-List | Out-String
} | Add-Content $log

$s.Dispose()
"=== done ===" | Add-Content $log