# deploy-v4.ps1 - visible elevated window, diskpart-based partitioning
$ErrorActionPreference = "Continue"
Write-Host "=== FakeWheel VM Deploy v4 ===" -ForegroundColor Cyan

$isoPath = "C:\Users\paul_\AppData\Local\Temp\kilo\Win11_25H2_English_x64.iso"
$vhdDir = "C:\Users\Public\Documents\Hyper-V\Virtual hard disks"
$bootVhd = Join-Path $vhdDir "FakeWheelBoot.vhdx"

Write-Host "Step 1: Clean up previous state..." -ForegroundColor Yellow
Stop-VM -Name FakeWheelVM -Force -ErrorAction SilentlyContinue
Get-VMHardDiskDrive -VMName FakeWheelVM -ErrorAction SilentlyContinue | Remove-VMHardDiskDrive -ErrorAction SilentlyContinue
Dismount-VHD -Path $bootVhd -ErrorAction SilentlyContinue
Dismount-DiskImage -ImagePath $isoPath -ErrorAction SilentlyContinue
Remove-Item $bootVhd -Force -ErrorAction SilentlyContinue
Write-Host "OK" -ForegroundColor Green

Write-Host "Step 2: Mount ISO..." -ForegroundColor Yellow
$iso = Mount-DiskImage -ImagePath $isoPath -PassThru
$isoLetter = ($iso | Get-Volume).DriveLetter
Write-Host "ISO at ${isoLetter}:" -ForegroundColor Green

Write-Host "Step 3: Find Windows 11 Pro index..." -ForegroundColor Yellow
$idx = dism /Get-ImageInfo /ImageFile:"${isoLetter}:\sources\install.wim" 2>&1 | Select-String -Pattern "Index\s+(\d+)" | ForEach-Object { $_.Matches[0].Groups[1].Value } | Select-Object -First 1
if (-not $idx) { $idx = 6 }
Write-Host "Index: $idx" -ForegroundColor Green

Write-Host "Step 4: Create 80GB boot VHDX..." -ForegroundColor Yellow
New-VHD -Path $bootVhd -SizeBytes 80GB -Dynamic | Out-Null
Write-Host "OK" -ForegroundColor Green

Write-Host "Step 5: Mount + partition via diskpart..." -ForegroundColor Yellow
Mount-VHD -Path $bootVhd
Start-Sleep 2
$diskNum = (Get-Disk | Where-Object { $_.FriendlyName -match "Virtual" } | Sort-Object Number | Select-Object -First 1).Number
if (-not $diskNum) {
    $diskNum = (Get-CimInstance Win32_DiskDrive | Where-Object { $_.Model -match "Virtual" } | Select-Object -First 1).Index
}
Write-Host "VHD disk number: $diskNum" -ForegroundColor Green
if (-not $diskNum) { Write-Host "FATAL: could not find the VHD disk" -ForegroundColor Red; exit 1 }

$dpScript = @"
select vdisk file="$bootVhd"
select disk $diskNum
clean
convert gpt
create partition efi size=260
format quick fs=fat32 label="EFI"
assign letter=S
create partition primary
format quick fs=ntfs label="Windows"
assign letter=W
exit
"@
$dpFile = "C:\Users\paul_\AppData\Local\Temp\kilo\deploy-diskpart.txt"
Set-Content -Path $dpFile -Value $dpScript -Encoding ASCII
diskpart /s $dpFile
Start-Sleep 2
Write-Host "Partitioning done" -ForegroundColor Green

if (-not (Test-Path "S:\") -or -not (Test-Path "W:\")) {
    Write-Host "FATAL: partitions S:/W: not found" -ForegroundColor Red
    Get-Volume | Where-Object { $_.DriveLetter -match 'S|W' } | Format-Table -AutoSize
    exit 1
}
$winLetter = "W:"
$espLetter = "S:"
Write-Host "ESP=$espLetter Windows=$winLetter" -ForegroundColor Green

Write-Host ""
Write-Host "=== Step 6: DISM applying Windows 11 Pro (7GB, ~3-5 min) ===" -ForegroundColor Cyan
Write-Host ""
& dism /Apply-Image /ImageFile:"${isoLetter}:\sources\install.wim" /Index:$idx /ApplyDir:"$winLetter\"
Write-Host "DISM exit code: $LASTEXITCODE" -ForegroundColor Yellow

Write-Host "Step 7: Install boot files..." -ForegroundColor Yellow
bcdboot "$winLetter\Windows" /s $espLetter /f UEFI /l en-US

Write-Host "Step 8: Inject autounattend.xml..." -ForegroundColor Yellow
$panther = "$winLetter\Windows\Panther"
New-Item -ItemType Directory -Force -Path $panther | Out-Null
Copy-Item "C:\Users\paul_\OneDrive\Documents\APP\ACEVO - Telemetry FFB\tools\FakeWheel\vm\autounattend.xml" (Join-Path $panther "autounattend.xml") -Force
Write-Host "OK" -ForegroundColor Green

Write-Host "Step 9: Detach VHD, attach to VM, start..." -ForegroundColor Yellow
Dismount-VHD -Path $bootVhd
Dismount-DiskImage -ImagePath $isoPath | Out-Null

Stop-VM -Name FakeWheelVM -Force -ErrorAction SilentlyContinue
Get-VMHardDiskDrive -VMName FakeWheelVM -ErrorAction SilentlyContinue | Remove-VMHardDiskDrive -ErrorAction SilentlyContinue
Add-VMHardDiskDrive -VMName FakeWheelVM -Path $bootVhd -ControllerType SCSI -ControllerNumber 0 -ControllerLocation 0
Start-VM -Name FakeWheelVM

Write-Host ""
Write-Host "=== DONE - VM booting Windows ===" -ForegroundColor Green
Write-Host "First boot ~2-3 min (autounattend does the setup automatically)." -ForegroundColor Green