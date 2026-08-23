$ip = '172.17.27.106'
$cred = New-Object System.Management.Automation.PSCredential('FakeWheel', (ConvertTo-SecureString 'FakeWheel!2026' -AsPlainText -Force))
$s = New-PSSession -ComputerName $ip -Credential (New-Object System.Management.Automation.PSCredential('FakeWheel',(ConvertTo-SecureString 'FakeWheel!2026' -AsPlainText -Force))) -ErrorAction Stop
Invoke-Command -Session $s -ScriptBlock {
    Get-PnpDevice -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -match 'ROOT\\SAMPLE' } | Select-Object Status, FriendlyName, InstanceId, Problem | Format-Table -AutoSize | Out-String
}
Remove-PSSession $s