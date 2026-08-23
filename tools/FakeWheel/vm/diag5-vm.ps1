param(
    [string]$VmIp = "172.26.169.198",
    [string]$VmUser = "FakeWheel",
    [string]$VmPass = "FakeWheel!2026"
)
$log = "C:\Users\paul_\AppData\Local\Temp\kilo\diag5-vm.log"
"=== diag5 $(Get-Date) ===" | Set-Content $log
$cred = New-Object System.Management.Automation.PSCredential($VmUser, (ConvertTo-SecureString $VmPass -AsPlainText -Force))
$s = New-PSSession -ComputerName $VmIp -Credential $cred -ErrorAction Stop

Invoke-Command -Session $s -ScriptBlock {
    "--- store copy ---" | Out-String
    Get-ChildItem C:\Windows\System32\DriverStore\FileRepository -Directory -Filter "fake_rs50kmdf*" | ForEach-Object {
        Get-Item (Join-Path $_.FullName "FakeRs50Kmdf.sys") | Select-Object FullName, Length, LastWriteTime | Format-List | Out-String
    }
    "--- drivers copy ---" | Out-String
    Get-Item C:\Windows\System32\drivers\FakeRs50Kmdf.sys | Select-Object Length, LastWriteTime | Format-List | Out-String
    "--- catalog in store ---" | Out-String
    Get-ChildItem C:\Windows\System32\DriverStore\FileRepository -Directory -Filter "fake_rs50kmdf*" | ForEach-Object { Get-ChildItem $_.FullName | Select-Object Name, Length | Format-Table -AutoSize | Out-String }
} | Add-Content $log

$s.Dispose()
"=== done ===" | Add-Content $log