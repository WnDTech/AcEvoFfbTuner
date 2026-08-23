$ip = '172.26.169.198'
$cred = New-Object System.Management.Automation.PSCredential(
    'FakeWheel',
    (ConvertTo-SecureString 'FakeWheel!2026' -AsPlainText -Force))
$session = New-PSSession -ComputerName $ip -Credential $cred -ErrorAction Stop

Invoke-Command -Session $session -ScriptBlock {
    Get-PnpDevice -ErrorAction SilentlyContinue |
        Where-Object { $_.InstanceId -match 'ROOT\\SAMPLE|VHF|HID_DEVICE_SYSTEM_VHF' } |
        Select-Object Status, Class, FriendlyName, InstanceId, Problem |
        Format-Table -AutoSize | Out-String

    sc.exe query mshidkmdf 2>&1 | Select-String 'STATE|WIN32'
    sc.exe query FakeRs50HidKm 2>&1 | Select-String 'STATE|WIN32'
}

Remove-PSSession $session
