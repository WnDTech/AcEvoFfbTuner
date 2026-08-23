# read-setup-logs.ps1 — run ELEVATED. Mounts the boot VHD and dumps the
# Windows Setup logs that explain why the unattend did not apply.

$ErrorActionPreference = "Continue"
$out = "C:\Users\paul_\AppData\Local\Temp\kilo\setup-logs.txt"
"=== setup logs $(Get-Date) ===" | Set-Content $out

$bootVhd = "C:\Users\Public\Documents\Hyper-V\Virtual hard disks\FakeWheelBoot.vhdx"

Mount-VHD -Path $bootVhd
Start-Sleep 2
$diskNum = (Get-Disk | Where-Object { $_.FriendlyName -match "Virtual" } | Sort-Object Number | Select-Object -First 1).Number
$winLetter = ((Get-Partition -DiskNumber $diskNum | Where-Object { $_.DriveLetter }) | ForEach-Object {
    $l = $_.DriveLetter + ":"
    if (Test-Path "$l\Windows") { $_.DriveLetter + ":" }
} | Select-Object -First 1)

"Windows partition: $winLetter" | Add-Content $out

"--- Panther files ---" | Add-Content $out
Get-ChildItem "$winLetter\Windows\Panther" -ErrorAction SilentlyContinue | Select-Object Name, Length, LastWriteTime | Format-Table -AutoSize | Out-String | Add-Content $out

"--- setupact.log: errors/warnings (tail 60) ---" | Add-Content $out
$logs = @("$winLetter\Windows\Panther\setupact.log", "$winLetter\Windows\Panther\UnattendGC\setupact.log")
foreach ($l in $logs) {
    if (Test-Path $l) {
        "### $l" | Add-Content $out
        Get-Content $l -Tail 2000 -ErrorAction SilentlyContinue |
            Select-String -Pattern "Error|Fail|Warn|unattend|Unattend|Skip|oobe" -CaseSensitive:$false |
            Select-Object -Last 25 | ForEach-Object { $_.Line.Substring(0, [Math]::Min(220, $_.Line.Length)) } | Add-Content $out
    }
}

"--- diagerr.xml ---" | Add-Content $out
Get-Content "$winLetter\Windows\Panther\diagerr.xml" -ErrorAction SilentlyContinue | Select-Object -First 30 | Add-Content $out

Dismount-VHD -Path $bootVhd
"=== done ===" | Add-Content $out