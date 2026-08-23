# deploy-vhd2.ps1 — step by step, full logging
$ErrorActionPreference = "Continue"
$log = "C:\Users\paul_\AppData\Local\Temp\kilo\deploy-vhd2.log"
"=== deploy-vhd2 $(Get-Date) ===" | Set-Content $log

$isoPath = "C:\Users\paul_\AppData\Local\Temp\kilo\Win11_25H2_English_x64.iso"
$vhdDir = "C:\Users\Public\Documents\Hyper-V\Virtual hard disks"
$bootVhd = Join-Path $vhdDir "FakeWheelBoot.vhdx"

# Clean up any previous state
Stop-VM -Name FakeWheelVM -Force -ErrorAction SilentlyContinue
Dismount-VHD -Path $bootVhd -ErrorAction SilentlyContinue
Dismount-DiskImage -ImagePath $isoPath -ErrorAction SilentlyContinue
Remove-Item $bootVhd -Force -ErrorAction SilentlyContinue

# 1. Mount ISO
"Step 1: Mount ISO" | Add-Content $log
$iso = Mount-DiskImage -ImagePath $isoPath -PassThru
$isoLetter = ($iso | Get-Volume).DriveLetter
$wim = "${isoLetter}:\sources\install.wim"
if (-not (Test-Path $wim)) { "WIM not found at $wim" | Add-Content $log; exit 1 }
"ISO mounted: $isoLetter | WIM: $wim" | Add-Content $log

# 2. Get Windows 11 Pro index
"Step 2: Get WIM index" | Add-Content $log
$idx = dism /Get-ImageInfo /ImageFile:$wim 2>&1 | Select-String "Windows 11 Pro" | ForEach-Object { $_ -replace '.*Index\s+(\d+).*','$1' } | Select-Object -First 1
if (-not $idx) { $idx = 6 }
"Windows 11 Pro at index: $idx" | Add-Content $log

# 3. Create boot VHDX
"Step 3: Create boot VHDX" | Add-Content $log
try {
    New-VHD -Path $bootVhd -SizeBytes 80GB -Dynamic -ErrorAction Stop | Out-Null
    "VHDX created: $((Get-Item $bootVhd).Length) bytes" | Add-Content $log
} catch { "FAIL New-VHD: $($_.Exception.Message)" | Add-Content $log; exit 1 }

# 4. Mount + initialize + partition
"Step 4: Mount and partition VHDX" | Add-Content $log
$disk = Mount-VHD -Path $bootVhd -PassThru -GetDisk -ErrorAction Stop
"VHD disk: $($disk.Number)" | Add-Content $log
Initialize-Disk -Number $disk.Number -PartitionStyle GPT -ErrorAction Stop | Out-Null

$esp = New-Partition -DiskNumber $disk.Number -Size 260MB -IsSystem -UseMaximumSize:$false -AssignDriveLetter -ErrorAction Stop
Format-Volume -FileSystem FAT32 -InputObject $esp -Force -ErrorAction Stop | Out-Null
$espLetter = $esp.DriveLetter + ":"
"ESP: $espLetter" | Add-Content $log

$winPart = New-Partition -DiskNumber $disk.Number -UseMaximumSize -AssignDriveLetter -ErrorAction Stop
Format-Volume -FileSystem NTFS -InputObject $winPart -Force -ErrorAction Stop | Out-Null
$winLetter = $winPart.DriveLetter + ":"
"Windows: $winLetter" | Add-Content $log

# 5. DISM apply
"Step 5: DISM apply (this takes several minutes)" | Add-Content $log
$dismCmd = "dism /Apply-Image /ImageFile:${isoLetter}:\sources\install.wim /Index:$idx /ApplyDir:${winLetter}\"
"CMD: $dismCmd" | Add-Content $log
$dismOut = & dism /Apply-Image /ImageFile:"${isoLetter}:\sources\install.wim" /Index:$idx /ApplyDir:"${winLetter}\" 2>&1
$dismOut | Add-Content $log
"DISM exit: $LASTEXITCODE" | Add-Content $log
if ($LASTEXITCODE -ne 0) { "FAIL DISM" | Add-Content $log; exit 1 }

# 6. bcdboot
"Step 6: bcdboot" | Add-Content $log
bcdboot "${winLetter}\Windows" /s $espLetter /f UEFI /l en-US 2>&1 | Add-Content $log

# 7. Inject autounattend
"Step 7: Inject autounattend" | Add-Content $log
$panther = "${winLetter}\Windows\Panther"
New-Item -ItemType Directory -Force -Path $panther | Out-Null
Copy-Item "C:\Users\paul_\OneDrive\Documents\APP\ACEVO - Telemetry FFB\tools\FakeWheel\vm\autounattend.xml" (Join-Path $panther "autounattend.xml") -Force
"Autounattend to $panther" | Add-Content $log

# 8. Clean up
"Step 8: Dismount + attach to VM" | Add-Content $log
Dismount-VHD -Path $bootVhd
Dismount-DiskImage -ImagePath $isoPath | Out-Null

Stop-VM -Name FakeWheelVM -Force
Add-VMHardDiskDrive -VMName FakeWheelVM -Path $bootVhd -ControllerType SCSI -ControllerNumber 0 -ControllerLocation 0
Start-VM -Name FakeWheelVM
"VM started from boot VHD" | Add-Content $log
"=== done ===" | Add-Content $log