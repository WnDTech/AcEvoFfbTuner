# deploy-fresh.ps1 - run ELEVATED. Full clean redeploy of the FakeWheel VM.
# 1) deploy Windows to a fresh boot VHD (diskpart + DISM + bcdboot + Apply-Unattend)
# 2) create the VM around it (Gen 2, Secure Boot OFF)
# 3) boot

$ErrorActionPreference = "Continue"
$log = "C:\Users\paul_\AppData\Local\Temp\kilo\deploy-fresh.log"
"=== deploy-fresh $(Get-Date) ===" | Set-Content $log
function Step($m) { Write-Host $m -ForegroundColor Cyan; Add-Content $log $m }
function Ok($m) { Write-Host $m -ForegroundColor Green; Add-Content $log $m }

$isoPath = "C:\Users\paul_\AppData\Local\Temp\kilo\Win11_25H2_English_x64.iso"
$vhdDir = "C:\Users\Public\Documents\Hyper-V\Virtual hard disks"
$bootVhd = Join-Path $vhdDir "FakeWheelBoot.vhdx"
$answerFile = "C:\Users\paul_\OneDrive\Documents\APP\ACEVO - Telemetry FFB\tools\FakeWheel\vm\autounattend.xml"

Step "Step 0: Remove the old VM + disk"
Stop-VM -Name FakeWheelVM -Force -ErrorAction SilentlyContinue
Remove-VM -Name FakeWheelVM -Force -ErrorAction SilentlyContinue
Dismount-VHD -Path $bootVhd -ErrorAction SilentlyContinue
Dismount-DiskImage -ImagePath $isoPath -ErrorAction SilentlyContinue
Remove-Item $bootVhd -Force -ErrorAction SilentlyContinue
Ok "clean"

Step "Step 1: Mount ISO"
$iso = Mount-DiskImage -ImagePath $isoPath -PassThru
$isoLetter = ($iso | Get-Volume).DriveLetter
Ok "ISO at ${isoLetter}:"

Step "Step 2: Find Windows 11 Pro index"
$idx = dism /Get-ImageInfo /ImageFile:"${isoLetter}:\sources\install.wim" 2>&1 | Select-String -Pattern "Index\s+(\d+)" | ForEach-Object { $_.Matches[0].Groups[1].Value } | Select-Object -First 1
if (-not $idx) { $idx = 6 }
Ok "Index: $idx"

Step "Step 3: Create 80GB boot VHDX"
New-VHD -Path $bootVhd -SizeBytes 80GB -Dynamic | Out-Null
Ok "created"

Step "Step 4: Mount + partition (diskpart)"
# Purge stale drive-letter caches from previous runs (MountedDevices)
foreach ($stale in @("T:", "U:", "W:", "S:", "A:")) {
    reg delete "HKLM\SYSTEM\MountedDevices" /v "\DosDevices\$stale" /f 2>&1 | Out-Null
}
Mount-VHD -Path $bootVhd
Start-Sleep 2
$diskNum = (Get-Disk | Where-Object { $_.FriendlyName -match "Virtual" } | Sort-Object Number | Select-Object -First 1).Number
if (-not $diskNum) { $diskNum = (Get-CimInstance Win32_DiskDrive | Where-Object { $_.Model -match "Virtual" } | Select-Object -First 1).Index }
Ok "VHD disk: $diskNum"
$dpScript = @"
select vdisk file="$bootVhd"
select disk $diskNum
clean
convert gpt
create partition efi size=260
format quick fs=fat32 label="EFI"
assign
create partition primary
format quick fs=ntfs label="Windows"
assign
exit
"@
Set-Content -Path "C:\Users\paul_\AppData\Local\Temp\kilo\deploy-diskpart.txt" -Value $dpScript -Encoding ASCII
diskpart /s "C:\Users\paul_\AppData\Local\Temp\kilo\deploy-diskpart.txt" | Add-Content $log
Start-Sleep 2
# Discover the partition letters; force-assign a free letter to the ESP if
# Windows did not auto-assign one (common for EFI partitions).
$espLetter = ""; $winLetter = ""
Get-Partition -DiskNumber $diskNum -ErrorAction SilentlyContinue | ForEach-Object {
    $v = $_ | Get-Volume
    if ($v.FileSystem -eq 'FAT32' -and $v.DriveLetter) { $espLetter = $v.DriveLetter }
    if ($v.FileSystem -eq 'NTFS' -and $v.DriveLetter) { $winLetter = $v.DriveLetter }
}
if (-not $espLetter) {
    $espPart = Get-Partition -DiskNumber $diskNum | Where-Object { ($_ | Get-Volume).FileSystem -eq 'FAT32' } | Select-Object -First 1
    if ($espPart) {
        $free = (65..90 | ForEach-Object { [char]$_ } | Where-Object { -not (Get-Volume -DriveLetter $_ -ErrorAction SilentlyContinue) } | Select-Object -First 1)
        if ($free) { Set-Partition -InputObject $espPart -NewDriveLetter $free -ErrorAction SilentlyContinue; Start-Sleep 1; $espLetter = (($espPart | Get-Volume).DriveLetter) }
    }
}
if (-not $winLetter) {
    $winPart = Get-Partition -DiskNumber $diskNum | Where-Object { ($_ | Get-Volume).FileSystem -eq 'NTFS' } | Select-Object -First 1
    if ($winPart) {
        $free = (65..90 | ForEach-Object { [char]$_ } | Where-Object { -not (Get-Volume -DriveLetter $_ -ErrorAction SilentlyContinue) } | Select-Object -First 1)
        if ($free) { Set-Partition -InputObject $winPart -NewDriveLetter $free -ErrorAction SilentlyContinue; Start-Sleep 1; $winLetter = (($winPart | Get-Volume).DriveLetter) }
    }
}
if (-not $espLetter -or -not $winLetter) { Step "FATAL: partitions missing (esp=$espLetter win=$winLetter)"; exit 1 }
$espLetter += ":"; $winLetter += ":"
Ok "ESP=$espLetter Windows=$winLetter"

Step "Step 5: DISM apply Windows 11 Pro (7GB, few minutes)"
& dism /Apply-Image /ImageFile:"${isoLetter}:\sources\install.wim" /Index:$idx /ApplyDir:"${winLetter}\" 2>&1 | Add-Content $log
Ok "DISM exit: $LASTEXITCODE"
if (-not (Test-Path "$winLetter\Windows\System32")) { Step "FATAL: image not applied"; exit 1 }

Step "Step 6: bcdboot"
bcdboot "$winLetter\Windows" /s $espLetter /f UEFI /l en-US 2>&1 | Add-Content $log
if (-not (Test-Path "$espLetter\EFI")) { Step "FATAL: boot files not created"; exit 1 }
Ok "boot files created"

Step "Step 7: Bake the answer file into the image (Apply-Unattend)"
dism /Image:"${winLetter}\" /Apply-Unattend:"$answerFile" 2>&1 | Add-Content $log
Ok "Apply-Unattend exit: $LASTEXITCODE"

Step "Step 8: Belt and braces - copy answer file to Panther + root"
$panther = "$winLetter\Windows\Panther"
New-Item -ItemType Directory -Force -Path $panther | Out-Null
Copy-Item $answerFile "$panther\autounattend.xml" -Force
Copy-Item $answerFile "$panther\unattend.xml" -Force
Copy-Item $answerFile "$winLetter\autounattend.xml" -Force
Ok "injected"

Step "Step 9: Detach the VHD"
Dismount-VHD -Path $bootVhd
Dismount-DiskImage -ImagePath $isoPath | Out-Null
Ok "detached"

Step "Step 10: Create the VM around the deployed disk (Gen 2, Secure Boot OFF)"
New-VM -Name FakeWheelVM -MemoryStartupBytes 8GB -Generation 2 | Out-Null
Set-VM -Name FakeWheelVM -ProcessorCount 4 | Out-Null
Set-VMFirmware -VMName FakeWheelVM -EnableSecureBoot Off | Out-Null
Add-VMHardDiskDrive -VMName FakeWheelVM -Path $bootVhd -ControllerType SCSI -ControllerNumber 0 -ControllerLocation 0
Ok "VM created"

Step "Step 11: Boot"
Start-VM -Name FakeWheelVM
Ok "VM started - OOBE should auto-complete to the desktop in ~2-3 min"
"=== done ===" | Add-Content $log