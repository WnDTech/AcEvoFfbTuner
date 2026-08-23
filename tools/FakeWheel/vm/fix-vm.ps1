param(
    [string]$VmIp = "172.26.169.198",
    [string]$VmUser = "FakeWheel",
    [string]$VmPass = "FakeWheel!2026"
)
$ErrorActionPreference = "Continue"
$log = "C:\Users\paul_\AppData\Local\Temp\kilo\fix-vm.log"
"=== fix-vm $(Get-Date) ===" | Set-Content $log

$cred = New-Object System.Management.Automation.PSCredential($VmUser, (ConvertTo-SecureString $VmPass -AsPlainText -Force))
$s = New-PSSession -ComputerName $VmIp -Credential $cred -ErrorAction Stop
"Session OK" | Add-Content $log

# 1. Remove old device + package
Invoke-Command -Session $s -ScriptBlock {
    & C:\FakeWheel\devcon.exe remove "root\FakeRs50Kmdf" 2>&1
    pnputil /delete-driver oem2.inf /uninstall /force 2>&1
} | Add-Content $log

# 2. Copy the fixed driver files
$src = "C:\Users\paul_\OneDrive\Documents\APP\ACEVO - Telemetry FFB\tools\FakeWheel\driver\fake_rs50kmdf\x64\Release\fake_rs50kmdf"
Copy-Item "$src\fake_rs50kmdf.inf", "$src\fake_rs50kmdf.cat", "$src\FakeRs50Kmdf.sys" -Destination C:\FakeWheel -ToSession $s -Force
"driver copied" | Add-Content $log

# 3. Stage + install
Invoke-Command -Session $s -ScriptBlock {
    pnputil /add-driver C:\FakeWheel\fake_rs50kmdf.inf /install 2>&1
    & C:\FakeWheel\devcon.exe install C:\FakeWheel\fake_rs50kmdf.inf "root\FakeRs50Kmdf" 2>&1
} | Add-Content $log
Start-Sleep 8

# 4. Verify device + VHF children
$state = Invoke-Command -Session $s -ScriptBlock {
    Get-PnpDevice -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -match 'ROOT\\SAMPLE|VHF' } |
        Select-Object Status, Class, FriendlyName, InstanceId, @{n='Prob';e={$_.Problem}} | Format-Table -AutoSize | Out-String
}
"--- device state ---" | Add-Content $log
$state | Add-Content $log

# 5. G HUB download + install (verified)
$gh = Invoke-Command -Session $s -ScriptBlock {
    $u = "https://download01.logi.com/web/ftp/pub/techsupport/gaming/lghub_installer.exe"
    try {
        Invoke-WebRequest -Uri $u -OutFile C:\FakeWheel\lghub_installer.exe -TimeoutSec 900
        "downloaded: $((Get-Item C:\FakeWheel\lghub_installer.exe).Length) bytes" | Out-String
    } catch { "download failed: $($_.Exception.Message)" | Out-String }
}
$gh | Add-Content $log

$ghInst = Invoke-Command -Session $s -ScriptBlock {
    $p = Start-Process C:\FakeWheel\lghub_installer.exe -ArgumentList "/silent" -PassThru -Wait
    "installer exit: $($p.ExitCode)" | Out-String
    Get-ChildItem "C:\Program Files" -Directory -Filter "*LGHUB*" -ErrorAction SilentlyContinue | Select-Object -ExpandProperty FullName | Out-String
    Get-ChildItem "C:\Program Files (x86)" -Directory -Filter "*LGHUB*" -ErrorAction SilentlyContinue | Select-Object -ExpandProperty FullName | Out-String
}
$ghInst | Add-Content $log

# 6. Launch G HUB
Invoke-Command -Session $s -ScriptBlock {
    $paths = @("C:\Program Files\LGHUB\lghub.exe", "C:\Program Files (x86)\LGHUB\lghub.exe")
    foreach ($pth in $paths) { if (Test-Path $pth) { Start-Process $pth } }
} | Out-Null
"G HUB launched - waiting 90s" | Add-Content $log
Start-Sleep 90

# 7. Capture log
$cap = Invoke-Command -Session $s -ScriptBlock {
    if (Test-Path C:\Windows\Temp\FakeRs50.log) {
        "SIZE: $((Get-Item C:\Windows\Temp\FakeRs50.log).Length)" | Out-String
        Get-Content C:\Windows\Temp\FakeRs50.log -TotalCount 40 | Out-String
    } else { "no capture log yet" }
}
"--- capture log ---" | Add-Content $log
$cap | Add-Content $log

$s.Dispose()
"=== done ===" | Add-Content $log