# apply-unattend.ps1 — run ELEVATED. Mounts the boot VHD and bakes the answer
# file into the image with DISM /Apply-Unattend, then restarts the VM.

$ErrorActionPreference = "Continue"
$out = "C:\Users\paul_\AppData\Local\Temp\kilo\apply-unattend.log"
"=== apply-unattend $(Get-Date) ===" | Set-Content $out

$bootVhd = "C:\Users\Public\Documents\Hyper-V\Virtual hard disks\FakeWheelBoot.vhdx"
$answerFile = "C:\Users\paul_\OneDrive\Documents\APP\ACEVO - Telemetry FFB\tools\FakeWheel\vm\autounattend.xml"

Stop-VM -Name FakeWheelVM -Force -ErrorAction SilentlyContinue

Mount-VHD -Path $bootVhd
Start-Sleep 2
$diskNum = (Get-Disk | Where-Object { $_.FriendlyName -match "Virtual" } | Sort-Object Number | Select-Object -First 1).Number
$winLetter = ((Get-Partition -DiskNumber $diskNum | Where-Object { $_.DriveLetter }) | ForEach-Object {
    $l = $_.DriveLetter + ":"
    if (Test-Path "$l\Windows") { $_.DriveLetter + ":" }
} | Select-Object -First 1)

"Windows: $winLetter" | Add-Content $out

# Bake the answer file into the image (offline specialize + oobeSystem passes)
"Applying unattend..." | Add-Content $out
dism /Image:"$winLetter\" /Apply-Unattend:"$answerFile" 2>&1 | Add-Content $out
"DISM exit: $LASTEXITCODE" | Add-Content $out

# Belt and braces: also place it in Panther + root
$panther = "$winLetter\Windows\Panther"
New-Item -ItemType Directory -Force -Path $panther | Out-Null
Copy-Item $answerFile "$panther\autounattend.xml" -Force
Copy-Item $answerFile "$panther\unattend.xml" -Force
Copy-Item $answerFile "$winLetter\autounattend.xml" -Force

Dismount-VHD -Path $bootVhd
Start-VM -Name FakeWheelVM
"VM started" | Add-Content $out
"=== done ===" | Add-Content $out