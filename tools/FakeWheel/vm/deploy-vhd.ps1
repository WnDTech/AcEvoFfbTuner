# deploy-vhd.ps1 — run ELEVATED.
# Bypasses the ISO boot entirely: creates a boot VHD, applies Win11 from the
# ISO's install.wim using DISM, runs bcdboot, attaches it to the VM.

$ErrorActionPreference = "Stop"
$log = "C:\Users\paul_\AppData\Local\Temp\kilo\deploy-vhd.log"
"=== deploy-vhd $(Get-Date) ===" | Set-Content $log

$isoPath = "C:\Users\paul_\AppData\Local\Temp\kilo\Win11_25H2_English_x64.iso"
$vhdDir = "C:\Users\Public\Documents\Hyper-V\Virtual hard disks"
$bootVhd = Join-Path $vhdDir "FakeWheelBoot.vhdx"

# --- Mount the ISO ---
$iso = Mount-DiskImage -ImagePath $isoPath -PassThru
$isoLetter = ($iso | Get-Volume).DriveLetter + ":"
$wim = "$isoLetter\sources\install.wim"
"ISO mounted at $isoLetter | WIM: $wim" | Add-Content $log

# Find the Windows 11 Pro index
$indices = dism /Get-ImageInfo /ImageFile:$wim 2>&1
$idx = ($indices | Select-String "Index\s+(\d+).*Windows 11 Pro" | ForEach-Object { $_.Matches[0].Groups[1].Value } | Select-Object -First 1)
if (-not $idx) { $idx = 6 } # fallback
"WIM index: $idx" | Add-Content $log

# --- Create + partition the boot VHDX ---
New-VHD -Path $bootVhd -SizeBytes 80GB -Dynamic | Out-Null
$vhdDisk = Mount-VHD -Path $bootVhd -PassThru -GetDisk
Initialize-Disk -Number $vhdDisk.Number -PartitionStyle GPT | Out-Null
$esp = New-Partition -DiskNumber $vhdDisk.Number -Size 260MB -IsSystem -AssignDriveLetter
Format-Volume -FileSystem FAT32 -InputObject $esp -Force | Out-Null
$espLetter = $esp.DriveLetter + ":"
$winPart = New-Partition -DiskNumber $vhdDisk.Number -UseMaximumSize -AssignDriveLetter
Format-Volume -FileSystem NTFS -InputObject $winPart -Force | Out-Null
$winLetter = $winPart.DriveLetter + ":"
"ESP: $espLetter | Windows: $winLetter" | Add-Content $log

# --- Apply the image ---
"Applying Windows 11 Pro (index $idx)..." | Add-Content $log
dism /Apply-Image /ImageFile:$wim /Index:$idx /ApplyDir:$winLetter\ 2>&1 | Add-Content $log

# --- Install boot files ---
bcdboot "$winLetter\Windows" /s $espLetter /f UEFI /l en-US 2>&1 | Add-Content $log

# --- Create autounattend.xml inside the image ---
$panther = "$winLetter\Windows\Panther"
New-Item -ItemType Directory -Force -Path $panther | Out-Null
Copy-Item "C:\Users\paul_\OneDrive\Documents\APP\ACEVO - Telemetry FFB\tools\FakeWheel\vm\autounattend.xml" (Join-Path $panther "autounattend.xml") -Force
"Autounattend injected to $panther" | Add-Content $log

# --- Dismount everything ---
Dismount-VHD -Path $bootVhd
Dismount-DiskImage -ImagePath $isoPath | Out-Null

# --- Attach the boot VHDX to the VM ---
Stop-VM -Name FakeWheelVM -Force
# Remove the DVD (no longer needed) — or keep as fallback
$dvd = Get-VMDvdDrive -VMName FakeWheelVM
# Add the boot VHD as the primary drive
Remove-VMHardDiskDrive -VMName FakeWheelVM -ControllerType SCSI -ControllerNumber 0 -ControllerLocation 0 -ErrorAction SilentlyContinue
Add-VMHardDiskDrive -VMName FakeWheelVM -Path $bootVhd -ControllerType SCSI -ControllerNumber 0
# Keep DVD as second device (for driver tools later)
if (-not $dvd) { Add-VMDvdDrive -VMName FakeWheelVM -Path $isoPath | Out-Null }
"Boot VHD attached, DVD kept as fallback" | Add-Content $log

Start-VM -Name FakeWheelVM
"VM started from VHD" | Add-Content $log
"=== done ===" | Add-Content $log