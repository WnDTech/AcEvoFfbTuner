# drive-vm.ps1 — run ELEVATED. Connects to the VM and completes:
# cert trust -> driver install -> HID verification -> G HUB.

param(
    [string]$VmIp = "172.26.169.198",
    [string]$VmUser = "FakeWheel",
    [string]$VmPass = "FakeWheel!2026"
)
$ErrorActionPreference = "Continue"
$log = "C:\Users\paul_\AppData\Local\Temp\kilo\drive-vm.log"
"=== drive-vm $(Get-Date) ===" | Set-Content $log

$cred = New-Object System.Management.Automation.PSCredential($VmUser, (ConvertTo-SecureString $VmPass -AsPlainText -Force))
try {
    $s = New-PSSession -ComputerName $VmIp -Credential $cred -ErrorAction Stop
} catch {
    "FAIL session: $($_.Exception.Message)" | Add-Content $log
    exit 1
}
"Session OK" | Add-Content $log

# 0. Verify test signing is ON
$ts = Invoke-Command -Session $s -ScriptBlock { bcdedit /enum {current} 2>&1 | Select-String "testsigning" | ForEach-Object { $_.Line.Trim() } }
"Test signing: $ts" | Add-Content $log

# 1. Copy devcon into the VM
$devconB64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes("C:\Program Files (x86)\Windows Kits\10\Tools\10.0.26100.0\x64\devcon.exe"))
Invoke-Command -Session $s -ScriptBlock { param($b64) [IO.File]::WriteAllBytes("C:\FakeWheel\devcon.exe", [Convert]::FromBase64String($b64)) } -ArgumentList $devconB64 | Out-Null
"devcon copied" | Add-Content $log

# 2. Trust the WDK test cert
Invoke-Command -Session $s -ScriptBlock {
    certutil -f -addstore Root C:\FakeWheel\FakeRs50.cer 2>&1 | Out-Null
    certutil -f -addstore TrustedPublisher C:\FakeWheel\FakeRs50.cer 2>&1 | Out-Null
    "cert trusted"
} | Add-Content $log

# 3. Stage the driver package
Invoke-Command -Session $s -ScriptBlock { pnputil /add-driver C:\FakeWheel\fake_rs50kmdf.inf /install 2>&1 } | Add-Content $log

# 4. Create the root device
Invoke-Command -Session $s -ScriptBlock { & C:\FakeWheel\devcon.exe install C:\FakeWheel\fake_rs50kmdf.inf "root\FakeRs50Kmdf" 2>&1 } | Add-Content $log

Start-Sleep 8

# 5. Verify the VHF HID devices
$hid = Invoke-Command -Session $s -ScriptBlock {
    Get-PnpDevice -Class HIDClass -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -match 'VHF' } |
        Select-Object Status, FriendlyName, InstanceId | Format-Table -AutoSize | Out-String
}
"--- VHF HID devices ---" | Add-Content $log
$hid | Add-Content $log

# 6. G HUB: download + silent install + launch
Invoke-Command -Session $s -ScriptBlock {
    $ghub = "C:\FakeWheel\lghub_installer.exe"
    try {
        Invoke-WebRequest -Uri "https://download01.logi.com/web/ftp/pub/techsupport/gaming/lghub_installer.exe" -OutFile $ghub -TimeoutSec 900
        Start-Process -FilePath $ghub -ArgumentList "/silent" -Wait -NoNewWindow
        "G HUB install done" | Out-String
    } catch {
        "G HUB failed: $($_.Exception.Message)" | Out-String
    }
} | Add-Content $log

Invoke-Command -Session $s -ScriptBlock {
    Start-Process "C:\Program Files\LGHUB\lghub.exe" -ErrorAction SilentlyContinue
    Start-Process "C:\Program Files (x86)\LGHUB\lghub.exe" -ErrorAction SilentlyContinue
} | Out-Null
"G HUB launched - waiting 60s for discovery" | Add-Content $log
Start-Sleep 60

# 7. Pull the capture log
$cap = Invoke-Command -Session $s -ScriptBlock {
    if (Test-Path C:\Windows\Temp\FakeRs50.log) {
        Get-Content C:\Windows\Temp\FakeRs50.log -TotalCount 40 | Out-String
    } else { "no capture log yet" }
}
"--- capture log ---" | Add-Content $log
$cap | Add-Content $log

$s.Dispose()
"=== done ===" | Add-Content $log