# deploy-visible.ps1 — visible elevated window so you can watch every step
$ErrorActionPreference = "Continue"
$log = "C:\Users\paul_\AppData\Local\Temp\kilo\deploy-live.log"
Write-Host "=== FakeWheel VM Deploy ===" -ForegroundColor Cyan

$isoPath = "C:\Users\paul_\AppData\Local\Temp\kilo\Win11_25H2_English_x64.iso"
$vhdDir = "C:\Users\Public\Documents\Hyper-V\Virtual hard disks"
$bootVhd = Join-Path $vhdDir "FakeWheelBoot.vhdx"

Write-Host "Cleaning up..." -ForegroundColor Yellow
Stop-VM -Name FakeWheelVM -Force -ErrorAction SilentlyContinue
Dismount-VHD -Path $bootVhd -ErrorAction SilentlyContinue
Dismount-DiskImage -ImagePath $isoPath -ErrorAction SilentlyContinue
Remove-Item $bootVhd -Force -ErrorAction SilentlyContinue
Write-Host "Mounting ISO..." -ForegroundColor Yellow
$iso = Mount-DiskImage -ImagePath $isoPath -PassThru
$isoLetter = ($iso | Get-Volume).DriveLetter
Write-Host "ISO mounted at $isoLetter`:" -ForegroundColor Green

Write-Host "Finding Windows 11 Pro image..." -ForegroundColor Yellow
$idx = dism /Get-ImageInfo /ImageFile:"${isoLetter}:\sources\install.wim" 2>&1 | Select-String -Pattern "Index\s+(\d+)" | ForEach-Object { $_.Matches[0].Groups[1].Value } | Select-Object -First 1
if (-not $idx) { $idx = 6 }
Write-Host "Image index: $idx" -ForegroundColor Green

Write-Host "Creating 80GB boot VHDX..." -ForegroundColor Yellow
New-VHD -Path $bootVhd -SizeBytes 80GB -Dynamic | Out-Null

Write-Host "Initializing disk + partitions..." -ForegroundColor Yellow
Mount-VHD -Path $bootVhd
$diskNum = (Get-VHD -Path $bootVhd).Number
Initialize-Disk -Number $diskNum -PartitionStyle GPT | Out-Null

$esp = New-Partition -DiskNumber $diskNum -Size 260MB -IsSystem -UseMaximumSize:$false -AssignDriveLetter
Format-Volume -FileSystem FAT32 -InputObject $esp -Force | Out-Null
$espLetter = $esp.DriveLetter + ":"

$win = New-Partition -DiskNumber $diskNum -UseMaximumSize -AssignDriveLetter
Format-Volume -FileSystem NTFS -InputObject $win -Force | Out-Null
$winLetter = $win.DriveLetter + ":"

Write-Host "Partitions ready — ESP=$espLetter Windows=$winLetter" -ForegroundColor Green

Write-Host ""
Write-Host "=== DISM applying Windows 11 Pro (7GB image, ~3-5 min) ===" -ForegroundColor Cyan
Write-Host ""
& dism /Apply-Image /ImageFile:"${isoLetter}:\sources\install.wim" /Index:$idx /ApplyDir:"$winLetter\"

Write-Host "=== Installing boot files ===" -ForegroundColor Cyan
bcdboot "$winLetter\Windows" /s $espLetter /f UEFI /l en-US

Write-Host "Injecting autounattend.xml..." -ForegroundColor Yellow
$panther = "$winLetter\Windows\Panther"
New-Item -ItemType Directory -Force -Path $panther | Out-Null
Copy-Item "C:\Users\paul_\OneDrive\Documents\APP\ACEVO - Telemetry FFB\tools\FakeWheel\vm\autounattend.xml" (Join-Path $panther "autounattend.xml") -Force

Write-Host "Cleaning up mounts..." -ForegroundColor Yellow
Dismount-VHD -Path $bootVhd
Dismount-DiskImage -ImagePath $isoPath | Out-Null

Write-Host "Attaching boot VHD to VM and starting..." -ForegroundColor Yellow
Stop-VM -Name FakeWheelVM -Force
# Remove the old empty disk so the boot VHD takes slot 0
Get-VMHardDiskDrive -VMName FakeWheelVM | Remove-VMHardDiskDrive -Force
Add-VMHardDiskDrive -VMName FakeWheelVM -Path $bootVhd -ControllerType SCSI -ControllerNumber 0 -ControllerLocation 0
# Boot from the boot VHD (DVD no longer needed)
Start-VM -Name FakeWheelVM

Write-Host ""
Write-Host "=== DONE — VM is booting Windows! ===" -ForegroundColor Green
Write-Host "Watch vmconnect for the Windows setup (OOBE). When it reaches the desktop, let me know." -ForegroundColor Green