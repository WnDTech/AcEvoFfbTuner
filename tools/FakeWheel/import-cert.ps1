$log = "C:\Users\paul_\AppData\Local\Temp\kilo\certimport.log"
"=== cert import $(Get-Date) ===" | Set-Content $log

$cer = "C:\Users\paul_\OneDrive\Documents\APP\ACEVO - Telemetry FFB\tools\FakeWheel\driver\fake_rs50\x64\Release\FakeRs50.cer"

certutil -f -addstore Root $cer 2>&1 | Add-Content $log
certutil -f -addstore TrustedPublisher $cer 2>&1 | Add-Content $log

bcdedit /enum {current} 2>&1 | Select-String "testsigning" | Add-Content $log

"=== done ===" | Add-Content $log