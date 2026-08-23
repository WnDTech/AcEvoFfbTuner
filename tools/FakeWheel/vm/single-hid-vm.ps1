param(
    [string]$VmIp = "172.26.169.198",
    [string]$VmUser = "FakeWheel",
    [string]$VmPass = "FakeWheel!2026"
)
$log = "C:\Users\paul_\AppData\Local\Temp\kilo\single-hid-vm.log"
"=== single-hid $(Get-Date) ===" | Set-Content $log
$cred = New-Object System.Management.Automation.PSCredential($VmUser, (ConvertTo-SecureString $VmPass -AsPlainText -Force))
$s = New-PSSession -ComputerName $VmIp -Credential $cred -ErrorAction Stop

Invoke-Command -Session $s -ScriptBlock {
    # Remove all test HID parent nodes; this leaves only the new fake node.
    & C:\FakeWheel\devcon.exe remove "root\vhidmini" 2>&1
    & C:\FakeWheel\devcon.exe remove "root\FakeRs50" 2>&1
    & C:\FakeWheel\devcon.exe remove "root\FakeRs50HidKm" 2>&1

    # Remove the reference/sample package if staged.
    $pkgs = pnputil /enum-drivers 2>&1 | Out-String
    foreach ($name in @('vhidmini.inf','fake_rs50.inf')) {
        foreach ($block in ($pkgs -split "(\r?\n){2,}" | Where-Object { $_ -match "Original Name:\s+$name" })) {
            if ($block -match "Published Name:\s+(\S+\.inf)") {
                pnputil /delete-driver $Matches[1] /uninstall /force 2>&1
            }
        }
    }

    # Stage and install our package as the only mshidkmdf client.
    pnputil /add-driver C:\FakeWheel\fake_rs50hidkm.inf /install 2>&1
    & C:\FakeWheel\devcon.exe install C:\FakeWheel\fake_rs50hidkm.inf "root\FakeRs50HidKm" 2>&1
} | Add-Content $log
Start-Sleep 10

Invoke-Command -Session $s -ScriptBlock {
    Get-PnpDevice -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -match 'ROOT\\SAMPLE' } |
        Select-Object Status, FriendlyName, InstanceId, @{n='Prob';e={$_.Problem}} | Format-Table -AutoSize | Out-String
    Get-PnpDevice -Class HIDClass -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -match 'VHF|ROOT|HID_DEVICE_SYSTEM_VHF' } |
        Select-Object Status, FriendlyName, InstanceId | Format-Table -AutoSize | Out-String
} | Add-Content $log

$s.Dispose()
"=== done ===" | Add-Content $log