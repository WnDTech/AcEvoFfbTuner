param(
    [string]$VmIp = "172.26.169.198",
    [string]$VmUser = "FakeWheel",
    [string]$VmPass = "FakeWheel!2026"
)
$log = "C:\Users\paul_\AppData\Local\Temp\kilo\deploy2-vm.log"
"=== deploy2 $(Get-Date) ===" | Set-Content $log
$cred = New-Object System.Management.Automation.PSCredential($VmUser, (ConvertTo-SecureString $VmPass -AsPlainText -Force))
$s = New-PSSession -ComputerName $VmIp -Credential $cred -ErrorAction Stop

# Remove old device + package
Invoke-Command -Session $s -ScriptBlock {
    & C:\FakeWheel\devcon.exe remove "root\FakeRs50Kmdf" 2>&1
    pnputil /enum-drivers 2>&1 | Out-String | ForEach-Object {
        if ($_ -match "Original Name:\s+fake_rs50kmdf\.inf") { }
    }
    pnputil /delete-driver oem2.inf /uninstall /force 2>&1
} | Add-Content $log

# Copy the rebuilt package
$src = "C:\Users\paul_\OneDrive\Documents\APP\ACEVO - Telemetry FFB\tools\FakeWheel\driver\fake_rs50kmdf\x64\Release\fake_rs50kmdf"
Copy-Item "$src\fake_rs50kmdf.inf", "$src\fake_rs50kmdf.cat", "$src\FakeRs50Kmdf.sys" -Destination C:\FakeWheel -ToSession $s -Force

# Install
Invoke-Command -Session $s -ScriptBlock {
    pnputil /add-driver C:\FakeWheel\fake_rs50kmdf.inf /install 2>&1
    & C:\FakeWheel\devcon.exe install C:\FakeWheel\fake_rs50kmdf.inf "root\FakeRs50Kmdf" 2>&1
} | Add-Content $log
Start-Sleep 10

"--- device state ---" | Add-Content $log
Invoke-Command -Session $s -ScriptBlock {
    Get-PnpDevice -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -match 'ROOT\\SAMPLE' } |
        Select-Object Status, FriendlyName, InstanceId, @{n='Prob';e={$_.Problem}} | Format-Table -AutoSize | Out-String
    Get-PnpDevice -Class HIDClass -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -match 'VHF|ROOT' } |
        Select-Object Status, FriendlyName, InstanceId | Format-Table -AutoSize | Out-String
} | Add-Content $log

"--- capture log ---" | Add-Content $log
Invoke-Command -Session $s -ScriptBlock {
    if (Test-Path C:\Windows\Temp\FakeRs50.log) { "SIZE: $((Get-Item C:\Windows\Temp\FakeRs50.log).Length)" | Out-String } else { "no capture log" }
} | Add-Content $log

$s.Dispose()
"=== done ===" | Add-Content $log