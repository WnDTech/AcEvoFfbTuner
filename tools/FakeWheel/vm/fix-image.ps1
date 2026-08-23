# fix-image.ps1 — run ELEVATED. Clears the MediaBootInstall flag and the
# stale skip/autologon keys so the OOBE runs exactly once, normally.

$ErrorActionPreference = "Continue"
$out = "C:\Users\paul_\AppData\Local\Temp\kilo\fix-image.log"
"=== fix-image $(Get-Date) ===" | Set-Content $out

$bootVhd = "C:\Users\Public\Documents\Hyper-V\Virtual hard disks\FakeWheelBoot.vhdx"

Stop-VM -Name FakeWheelVM -Force -ErrorAction SilentlyContinue
Mount-VHD -Path $bootVhd
Start-Sleep 2
$diskNum = (Get-Disk | Where-Object { $_.FriendlyName -match "Virtual" } | Sort-Object Number | Select-Object -First 1).Number
$winLetter = ((Get-Partition -DiskNumber $diskNum | Where-Object { $_.DriveLetter }) | ForEach-Object {
    $l = $_.DriveLetter + ":"
    if (Test-Path "$l\Windows") { $_.DriveLetter + ":" }
} | Select-Object -First 1)
"Windows: $winLetter" | Add-Content $out

# Load the offline SOFTWARE hive
reg load HKLM\FW_SOFT "$winLetter\Windows\System32\config\SOFTWARE" 2>&1 | Add-Content $out

# 1. Clear MediaBootInstall (the OOBE-reruns-every-boot flag)
reg delete "HKLM\FW_SOFT\Microsoft\Windows\CurrentVersion\Setup\OOBE" /v MediaBootInstall /f 2>&1 | Add-Content $out

# 2. Remove the stale skip keys (they bypass OOBE to a user that doesn't exist)
reg delete "HKLM\FW_SOFT\Microsoft\Windows\CurrentVersion\OOBE" /v SkipMachineOOBE /f 2>&1 | Add-Content $out
reg delete "HKLM\FW_SOFT\Microsoft\Windows\CurrentVersion\OOBE" /v SkipUserOOBE /f 2>&1 | Add-Content $out

# 3. Remove the autologon to the nonexistent user
reg delete "HKLM\FW_SOFT\Microsoft\Windows NT\CurrentVersion\Winlogon" /v AutoAdminLogon /f 2>&1 | Add-Content $out
reg delete "HKLM\FW_SOFT\Microsoft\Windows NT\CurrentVersion\Winlogon" /v DefaultUserName /f 2>&1 | Add-Content $out
reg delete "HKLM\FW_SOFT\Microsoft\Windows NT\CurrentVersion\Winlogon" /v DefaultPassword /f 2>&1 | Add-Content $out

reg unload HKLM\FW_SOFT 2>&1 | Add-Content $out

Dismount-VHD -Path $bootVhd
Start-VM -Name FakeWheelVM
"VM started — OOBE will run once, normally" | Add-Content $out
"=== done ===" | Add-Content $out