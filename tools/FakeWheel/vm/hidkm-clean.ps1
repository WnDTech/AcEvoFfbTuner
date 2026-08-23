param(
    [string]$VmIp = "172.26.169.198",
    [string]$VmUser = "FakeWheel",
    [string]$VmPass = "FakeWheel!2026"
)
$ErrorActionPreference = "Stop"
$log = "C:\Users\paul_\AppData\Local\Temp\kilo\hidkm-clean.log"
"=== hidkm-clean $(Get-Date) ===" | Set-Content $log

$cred = New-Object System.Management.Automation.PSCredential($VmUser, (ConvertTo-SecureString $VmPass -AsPlainText -Force))
$s = New-PSSession -ComputerName $VmIp -Credential $cred -ErrorAction Stop

# Remove all previous test nodes and the old fake package.
Invoke-Command -Session $s -ScriptBlock {
    foreach ($id in @('root\FakeRs50HidKm','root\FakeRs50','root\vhidmini')) {
        & C:\FakeWheel\devcon.exe remove $id 2>&1
    }
    $blocks = (pnputil /enum-drivers 2>&1 | Out-String) -split "(\r?\n){2,}"
    foreach ($name in @('fake_rs50hidkm.inf','fake_rs50kmdf.inf','fake_rs50mini.inf','fake_rs50.inf','vhidmini.inf')) {
        foreach ($block in ($blocks | Where-Object { $_ -match "Original Name:\s+$name" })) {
            if ($block -match "Published Name:\s+(\S+\.inf)") {
                pnputil /delete-driver $Matches[1] /uninstall /force 2>&1
            }
        }
    }
} | Add-Content $log

# Copy rebuilt package.
$src = "C:\Users\paul_\OneDrive\Documents\APP\ACEVO - Telemetry FFB\tools\FakeWheel\driver\fake_rs50hidkm\x64\Release\fake_rs50hidkm"
Copy-Item "$src\fake_rs50hidkm.inf", "$src\fake_rs50hidkm.cat", "$src\FakeRs50HidKm.sys" -Destination C:\FakeWheel -ToSession $s -Force
"new package copied" | Add-Content $log

# Fresh boot clears mshidkmdf's loaded/unloaded state.
Invoke-Command -Session $s -ScriptBlock { Restart-Computer -Force } | Out-Null
$s.Dispose()
"VM reboot issued" | Add-Content $log

Start-Sleep 100
for ($i = 0; $i -lt 12; $i++) {
    try { $s = New-PSSession -ComputerName $VmIp -Credential $cred -ErrorAction Stop; break } catch { Start-Sleep 20 }
}
if (-not $s) { "FAIL: VM did not return" | Add-Content $log; exit 1 }

# Install exactly once after the fresh boot.
Invoke-Command -Session $s -ScriptBlock {
    pnputil /add-driver C:\FakeWheel\fake_rs50hidkm.inf /install 2>&1
    & C:\FakeWheel\devcon.exe install C:\FakeWheel\fake_rs50hidkm.inf "root\FakeRs50HidKm" 2>&1
} | Add-Content $log
Start-Sleep 10

Invoke-Command -Session $s -ScriptBlock {
    Get-PnpDevice -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -match 'ROOT\\SAMPLE|VHF|HID_DEVICE_SYSTEM_VHF' } |
        Select-Object Status, Class, FriendlyName, InstanceId, @{n='Prob';e={$_.Problem}} | Format-Table -AutoSize | Out-String
} | Add-Content $log

$s.Dispose()
"=== done ===" | Add-Content $log