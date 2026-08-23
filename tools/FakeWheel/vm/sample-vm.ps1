param(
    [string]$VmIp = "172.26.169.198",
    [string]$VmUser = "FakeWheel",
    [string]$VmPass = "FakeWheel!2026"
)
$log = "C:\Users\paul_\AppData\Local\Temp\kilo\sample-vm.log"
"=== sample-vm $(Get-Date) ===" | Set-Content $log
$cred = New-Object System.Management.Automation.PSCredential($VmUser, (ConvertTo-SecureString $VmPass -AsPlainText -Force))
$s = New-PSSession -ComputerName $VmIp -Credential $cred -ErrorAction Stop

$src = "C:\Users\paul_\OneDrive\Documents\APP\ACEVO - Telemetry FFB\tools\FakeWheel\driver\_upstream\hid\vhidmini2\driver\kmdf\x64\Release\vhidmini"
New-Item -ItemType Directory -Force -Path "C:\Users\paul_\_local_fakewheel_stage" -ErrorAction SilentlyContinue | Out-Null
Copy-Item "$src\vhidmini.inf", "$src\vhidmini.sys", "$src\wudf.cat" -Destination "C:\Users\paul_\_local_fakewheel_stage" -Force

$infBytes = [IO.File]::ReadAllBytes("C:\Users\paul_\_local_fakewheel_stage\vhidmini.inf")
$sysBytes = [IO.File]::ReadAllBytes("C:\Users\paul_\_local_fakewheel_stage\vhidmini.sys")
$catBytes = [IO.File]::ReadAllBytes("C:\Users\paul_\_local_fakewheel_stage\wudf.cat")

Invoke-Command -Session $s -ScriptBlock {
    param($infb, $sysb, $catb)
    New-Item -ItemType Directory -Force -Path C:\FakeWheel\sample | Out-Null
    [IO.File]::WriteAllBytes("C:\FakeWheel\sample\vhidmini.inf", [Convert]::FromBase64String($infb))
    [IO.File]::WriteAllBytes("C:\FakeWheel\sample\vhidmini.sys", [Convert]::FromBase64String($sysb))
    [IO.File]::WriteAllBytes("C:\FakeWheel\sample\wudf.cat", [Convert]::FromBase64String($catb))
} -ArgumentList ([Convert]::ToBase64String($infBytes)), ([Convert]::ToBase64String($sysBytes)), ([Convert]::ToBase64String($catBytes)) | Out-Null

Invoke-Command -Session $s -ScriptBlock {
    pnputil /add-driver C:\FakeWheel\sample\vhidmini.inf /install 2>&1
    & C:\FakeWheel\devcon.exe install C:\FakeWheel\sample\vhidmini.inf "root\vhidmini" 2>&1
} | Add-Content $log
Start-Sleep 8

"--- sample device state ---" | Add-Content $log
Invoke-Command -Session $s -ScriptBlock {
    Get-PnpDevice -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -match 'VHIDMINI|vhidmini' } |
        Select-Object Status, Class, FriendlyName, InstanceId, @{n='Prob';e={$_.Problem}} | Format-Table -AutoSize | Out-String
} | Add-Content $log

$s.Dispose()
"=== done ===" | Add-Content $log