$log = "C:\Users\paul_\AppData\Local\Temp\kilo\mshi2copy.log"
"=== mshidumdf2 $(Get-Date) ===" | Set-Content $log

# 1. Byte-identical copy under a fresh image name (keeps the embedded signature)
Copy-Item "C:\Windows\System32\drivers\mshidumdf.sys" "C:\Windows\System32\drivers\mshidumdf2.sys" -Force
Get-FileHash "C:\Windows\System32\drivers\mshidumdf.sys" -Algorithm SHA256 | Add-Content $log
Get-FileHash "C:\Windows\System32\drivers\mshidumdf2.sys" -Algorithm SHA256 | Add-Content $log

# 2. Point the mshidumdf service at the copy
reg add "HKLM\SYSTEM\CurrentControlSet\Services\mshidumdf" /v ImagePath /t REG_EXPAND_SZ /d "\SystemRoot\System32\drivers\mshidumdf2.sys" /f 2>&1 | Add-Content $log

# 3. Load it
sc.exe start mshidumdf 2>&1 | Add-Content $log
Start-Sleep -Seconds 2
sc.exe query mshidumdf 2>&1 | Add-Content $log

"=== done ===" | Add-Content $log