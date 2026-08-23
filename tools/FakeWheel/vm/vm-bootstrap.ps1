# vm-bootstrap.ps1 — run from the HOST after the VM finished installing.
# Enables test signing in the VM, installs the FakeRs50Kmdf driver, verifies
# the virtual HID devices, then installs and launches G HUB.

param(
    [string]$VmName = "FakeWheelVM",
    [string]$VmUser = "FakeWheel",
    [string]$VmPass = "FakeWheel!2026",
    [int]$WaitMinutes = 60
)
$ErrorActionPreference = "Continue"
$log = "C:\Users\paul_\AppData\Local\Temp\kilo\vm-bootstrap.log"
"=== vm-bootstrap $(Get-Date) ===" | Set-Content $log

$cred = New-Object System.Management.Automation.PSCredential($VmUser, (ConvertTo-SecureString $VmPass -AsPlainText -Force))

function Get-VmIp {
    $vm = Get-VM -Name $VmName -ErrorAction SilentlyContinue
    if (-not $vm) { return $null }
    if ($vm.State -ne 'Running') { Start-VM -Name $VmName | Out-Null; Start-Sleep 10 }
    $ip = (Get-VMNetworkAdapter -VMName $VmName | Select-Object -ExpandProperty IPAddresses) | Where-Object { $_ -match '^172\.|^10\.|^192\.168\.' } | Select-Object -First 1
    return $ip
}

# --- wait for the VM to boot + WinRM to answer ---
$deadline = (Get-Date).AddMinutes($WaitMinutes)
$ip = $null
while ((Get-Date) -lt $deadline) {
    $ip = Get-VmIp
    if ($ip) {
        try {
            $null = Test-WSMan -ComputerName $ip -ErrorAction Stop
            break
        } catch { }
    }
    Start-Sleep 20
}
if (-not $ip) { "FAIL: VM unreachable within $WaitMinutes min" | Add-Content $log; exit 1 }
"VM at $ip — WinRM answering" | Add-Content $log

# --- 1. enable test signing ---
try {
    $s = New-PSSession -ComputerName $ip -Credential $cred -ErrorAction Stop
} catch {
    "FAIL: WinRM session: $($_.Exception.Message)" | Add-Content $log
    exit 1
}
Invoke-Command -Session $s -ScriptBlock {
    bcdedit /set testsigning on
    New-Item -ItemType Directory -Force -Path C:\FakeWheel | Out-Null
} | Add-Content $log

# --- 2. copy driver package + test cert into the VM ---
$src = "C:\Users\paul_\OneDrive\Documents\APP\ACEVO - Telemetry FFB\tools\FakeWheel\driver\fake_rs50kmdf\x64\Release\fake_rs50kmdf"
Copy-Item "$src\fake_rs50kmdf.inf", "$src\fake_rs50kmdf.cat", "$src\FakeRs50Kmdf.sys" -Destination C:\FakeWheel -ToSession $s
Copy-Item "C:\Users\paul_\OneDrive\Documents\APP\ACEVO - Telemetry FFB\tools\FakeWheel\driver\fake_rs50\x64\Release\FakeRs50.cer" -Destination C:\FakeWheel -ToSession $s
"Driver package copied" | Add-Content $log

# --- 3. reboot the VM (test signing takes effect) ---
Invoke-Command -Session $s -ScriptBlock { Restart-Computer -Force } | Out-Null
$s.Dispose()
"VM rebooting for test signing..." | Add-Content $log
Start-Sleep 60

$deadline = (Get-Date).AddMinutes(10)
while ((Get-Date) -lt $deadline) {
    $ip = Get-VmIp
    if ($ip) {
        try {
            $s = New-PSSession -ComputerName $ip -Credential $cred -ErrorAction Stop
            break
        } catch { $s = $null }
    }
    Start-Sleep 20
}
if (-not $s) { "FAIL: VM unreachable after reboot" | Add-Content $log; exit 1 }

Invoke-Command -Session $s -ScriptBlock {
    # trust the WDK test cert
    certutil -f -addstore Root C:\FakeWheel\FakeRs50.cer | Out-Null
    certutil -f -addstore TrustedPublisher C:\FakeWheel\FakeRs50.cer | Out-Null
    # stage the driver package
    pnputil /add-driver C:\FakeWheel\fake_rs50kmdf.inf /install
} | Add-Content $log

# --- 4. create the root device (devcon copied in-band via base64) ---
$devconB64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes("C:\Program Files (x86)\Windows Kits\10\Tools\10.0.26100.0\x64\devcon.exe"))
Invoke-Command -Session $s -ScriptBlock {
    param($b64)
    [IO.File]::WriteAllBytes("C:\FakeWheel\devcon.exe", [Convert]::FromBase64String($b64))
    & C:\FakeWheel\devcon.exe install C:\FakeWheel\fake_rs50kmdf.inf "root\FakeRs50Kmdf"
} -ArgumentList $devconB64 | Add-Content $log

Start-Sleep 8

# --- 5. verify HID devices ---
$hid = Invoke-Command -Session $s -ScriptBlock {
    Get-PnpDevice -Class HIDClass | Where-Object { $_.InstanceId -match 'VHF' } |
        Select-Object Status, FriendlyName, InstanceId | Format-Table -AutoSize | Out-String
} 
"--- VHF HID devices ---" | Add-Content $log
$hid | Add-Content $log

# --- 6. G HUB: download + silent install + launch ---
Invoke-Command -Session $s -ScriptBlock {
    $ghub = "C:\FakeWheel\lghub_installer.exe"
    try {
        Invoke-WebRequest -Uri "https://download01.logi.com/web/ftp/pub/techsupport/gaming/lghub_installer.exe" -OutFile $ghub -TimeoutSec 600
        Start-Process -FilePath $ghub -ArgumentList "/silent" -Wait -NoNewWindow
        "G HUB installed" | Out-String
    } catch {
        "G HUB download/install failed: $($_.Exception.Message)" | Out-String
    }
} | Add-Content $log

# --- 7. launch G HUB and give it time to discover devices ---
Invoke-Command -Session $s -ScriptBlock {
    Start-Process "C:\Program Files\LGHUB\lghub.exe" -ErrorAction SilentlyContinue
    Start-Process "C:\Program Files (x86)\LGHUB\lghub.exe" -ErrorAction SilentlyContinue
} | Out-Null
"G HUB launched — wait 60s for discovery" | Add-Content $log
Start-Sleep 60

# --- 8. pull the capture log ---
$logContent = Invoke-Command -Session $s -ScriptBlock {
    if (Test-Path C:\Windows\Temp\FakeRs50.log) {
        Get-Content C:\Windows\Temp\FakeRs50.log -TotalCount 60 | Out-String
    } else { "no capture log yet" }
}
"--- capture log (first 60 lines) ---" | Add-Content $log
$logContent | Add-Content $log

$s.Dispose()
"=== done ===" | Add-Content $log