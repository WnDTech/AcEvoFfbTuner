# inject-unattend.ps1 — run ELEVATED. Stops the VM, mounts the boot VHD,
# injects the answer file into the image, restarts the VM.

$ErrorActionPreference = "Continue"
$out = "C:\Users\paul_\AppData\Local\Temp\kilo\inject.log"
"=== inject $(Get-Date) ===" | Set-Content $out

$bootVhd = "C:\Users\Public\Documents\Hyper-V\Virtual hard disks\FakeWheelBoot.vhdx"
$answerFile = "C:\Users\paul_\OneDrive\Documents\APP\ACEVO - Telemetry FFB\tools\FakeWheel\vm\autounattend.xml"

# 1. Stop the VM hard
Stop-VM -Name FakeWheelVM -Force
"VM stopped" | Add-Content $out

# 2. Mount the boot VHD on the host
$mounts = Mount-VHD -Path $bootVhd -PassThru
$diskNum = (Get-Disk | Where-Object { $_.FriendlyName -match "Virtual" } | Sort-Object Number | Select-Object -First 1).Number
"Disk: $diskNum" | Add-Content $out

# 3. Find the Windows partition (NTFS, has \Windows)
$parts = Get-Partition -DiskNumber $diskNum
$winPart = $parts | Where-Object { $_.DriveLetter } | ForEach-Object {
    $l = $_.DriveLetter + ":"
    if (Test-Path "$l\Windows") { $_ }
} | Select-Object -First 1
$winLetter = $winPart.DriveLetter + ":"
"Windows partition: $winLetter" | Add-Content $out

# 4. Inject into every location setup checks
$panther = "$winLetter\Windows\Panther"
New-Item -ItemType Directory -Force -Path $panther | Out-Null
Copy-Item $answerFile "$panther\autounattend.xml" -Force
Copy-Item $answerFile "$panther\unattend.xml" -Force
Copy-Item $answerFile "$winLetter\autounattend.xml" -Force
Get-ChildItem "$panther\*.xml", "$winLetter\autounattend.xml" | Select-Object Name, Length, LastWriteTime |
    Format-Table -AutoSize | Out-String | Add-Content $out

# 5. Also pre-create the admin account + WinRM regs via the offline registry (belt and braces)
"Offline registry tweaks..." | Add-Content $out
$winDir = "$winLetter\Windows\System32\config"
reg load HKLM\FAKEWHEEL_OFFLINE "$winDir\SOFTWARE" 2>&1 | Add-Content $out
reg add "HKLM\FAKEWHEEL_OFFLINE\Microsoft\Windows\CurrentVersion\OOBE" /v SkipMachineOOBE /t REG_DWORD /d 1 /f 2>&1 | Add-Content $out
reg add "HKLM\FAKEWHEEL_OFFLINE\Microsoft\Windows\CurrentVersion\OOBE" /v SkipUserOOBE /t REG_DWORD /d 1 /f 2>&1 | Add-Content $out
reg unload HKLM\FAKEWHEEL_OFFLINE 2>&1 | Add-Content $out

# 6. Unmount + restart
Dismount-VHD -Path $bootVhd
"VHD unmounted" | Add-Content $out
Start-VM -Name FakeWheelVM
"VM restarted" | Add-Content $out
"=== done ===" | Add-Content $out