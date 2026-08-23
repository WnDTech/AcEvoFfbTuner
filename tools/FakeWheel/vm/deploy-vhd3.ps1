# deploy-vhd3.ps1 — DISM deploy with fixed parsing
$ErrorActionPreference = "Stop"
$log = "C:\Users\paul_\AppData\Local\Temp\kilo\deploy-vhd3.log"
"=== deploy-vhd3 $(Get-Date) ===" | Set-Content $log

$isoPath = "C:\Users\paul_\AppData\Local\Temp\kilo\Win11_25H2_English_x64.iso"
$vhdDir = "C:\Users\Public\Documents\Hyper-V\Virtual hard disks"
$bootVhd = Join-Path $vhdDir "FakeWheelBoot.vhdx"

Stop-VM -Name FakeWheelVM -Force -ErrorAction SilentlyContinue
Dismount-VHD -Path $bootVhd -ErrorAction SilentlyContinue
Dismount-DiskImage -ImagePath $isoPath -ErrorAction SilentlyContinue
Remove-Item $bootVhd -Force -ErrorAction SilentlyContinue

# 1. Mount ISO + find Pro index
$iso = Mount-DiskImage -ImagePath $isoPath -PassThru
$isoVol = $iso | Get-Volume
$isoLetter = $isoVol.DriveLetter + ":"
"ISO: $isoLetter" | Add-Content $log
$dismInfo = dism /Get-ImageInfo /ImageFile:"$isoLetter\sources\install.wim" 2>&1
# Parse: find "Index : N" line immediately followed by "Name : Windows 11 Pro"
$lines = $dismInfo -split "`r?`n"
$idx = 0
for ($i = 0; $i -lt $lines.Count - 2; $i++) {
    if ($lines[$i] -match 'Index\s*:\s*(\d+)' -and $lines[$i+1] -match 'Windows 11 Pro') {
        $idx = [int]$Matches[1]; break
    }
}
if ($idx -eq 0) { $idx = 6 }
"Pro image index: $idx" | Add-Content $log

# 2. Create VHDX
"Creating VHDX..." | Add-Content $log
New-VHD -Path $bootVhd -SizeBytes 80GB -Dynamic | Out-Null

# 3. Mount + partition
$disk = Mount-VHD -Path $bootVhd -PassThru -GetDisk
$diskNum = $disk.Number
"Disk number: $diskNum" | Add-Content $log
Initialize-Disk -Number $diskNum -PartitionStyle GPT | Out-Null

$esp = New-Partition -DiskNumber $diskNum -Size 260MB -IsSystem -UseMaximumSize:$false -AssignDriveLetter
Format-Volume -FileSystem FAT32 -InputObject $esp -Force | Out-Null
$espLetter = $esp.DriveLetter + ":"
"ESP: $espLetter" | Add-Content $log

$win = New-Partition -DiskNumber $diskNum -UseMaximumSize -AssignDriveLetter
Format-Volume -FileSystem NTFS -InputObject $win -Force | Out-Null
$winLetter = $win.DriveLetter + ":"
"Windows: $winLetter" | Add-Content $log

# 4. DISM
"Applying image..." | Add-Content $log
$dismOut = & dism /Apply-Image /ImageFile:"$isoLetter\sources\install.wim" /Index:$idx /ApplyDir:"$winLetter\" 2>&1
$dismOut | ForEach-Object { $_ } | Add-Content $log
"DISM exit: $LASTEXITCODE" | Add-Content $log
if ($LASTEXITCODE -ne 0) { exit 1 }

# 5. bcdboot
bcdboot "$winLetter\Windows" /s $espLetter /f UEFI /l en-US 2>&1 | Add-Content $log

# 6. autounattend
$panther = "$winLetter\Windows\Panther"
New-Item -ItemType Directory -Force -Path $panther | Out-Null
Copy-Item "C:\Users\paul_\OneDrive\Documents\APP\ACEVO - Telemetry FFB\tools\FakeWheel\vm\autounattend.xml" (Join-Path $panther "autounattend.xml") -Force

# 7. Dismount + attach
Dismount-VHD -Path $bootVhd
Dismount-DiskImage -ImagePath $isoPath | Out-Null

Stop-VM -Name FakeWheelVM -Force
Add-VMHardDiskDrive -VMName FakeWheelVM -Path $bootVhd -ControllerType SCSI -ControllerNumber 0 -ControllerLocation 0
Start-VM -Name FakeWheelVM
"VM started from boot VHD" | Add-Content $log
"=== done ===" | Add-Content $log