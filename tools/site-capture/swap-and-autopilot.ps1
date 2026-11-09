<#
  Waits until the car is stationary (FFB Tuner Home page speed = 0 for 6 s while the game
  is connected), then restarts the FFB Tuner from the capture build (whose sidebar responds
  to UI Automation), with AutoStart on so FFB comes straight back, and runs the recorder
  with automatic page cycling.
#>
param([string]$Exe="$env:LOCALAPPDATA\FfbTunerCaptureBuild\AcEvoFfbTuner.exe",
      [int]$Minutes=30,[int]$PageSeconds=90,
      [string]$Pages="Home,Telemetry,Live Map,FFB Coach,Equalizer,Devices,FFB Tuning")
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
$A=[Windows.Automation.AutomationElement]
function Get-Win { Get-Process AcEvoFfbTuner -EA SilentlyContinue | ?{$_.MainWindowHandle -ne 0} | select -First 1 }
function Read-State {
  $p=Get-Win; if(-not $p){ return $null }
  $root=$A::FromHandle($p.MainWindowHandle)
  $c=New-Object Windows.Automation.PropertyCondition ($A::ControlTypeProperty),([Windows.Automation.ControlType]::Text)
  $names=@($root.FindAll([Windows.Automation.TreeScope]::Descendants,$c) | %{ $_.Current.Name })
  $k=[Array]::IndexOf($names,'km/h')
  [pscustomobject]@{ Speed= if($k -gt 0 -and $names[$k-1] -match '^\d+$'){ [int]$names[$k-1] } else { $null };
                     Connected= [bool]($names -match '^Game: Connected') }
}
function Log($m){ Write-Host ("[{0}] {1}" -f (Get-Date -Format 'HH:mm:ss'),$m) }

if(-not (Test-Path $Exe)){ throw "Capture build not found: $Exe" }
Log "Waiting for the car to stop (speed 0 for 6 s)..."
$still=0
while($true){
  $s=Read-State
  if($s -and $s.Connected -and $s.Speed -eq 0){ $still++ } else { $still=0 }
  if($still -ge 6){ break }
  Start-Sleep -Seconds 1
}
Log "Car stopped - swapping to the capture build."

# AutoStart on so FFB resumes without pressing START (original settings backed up).
$set="$env:APPDATA\AcEvoFfbTuner\settings.json"
Copy-Item $set "$set.capture-backup" -Force
(Get-Content $set -Raw) -replace '"autoStart"\s*:\s*false','"autoStart": true' | Set-Content $set -NoNewline -Encoding UTF8

Stop-Process -Name AcEvoFfbTuner -Force
Start-Sleep -Seconds 2
Start-Process -FilePath $Exe -WorkingDirectory (Split-Path $Exe)
$t=0; while(-not (Get-Win) -and $t -lt 60){ Start-Sleep 1; $t++ }
Start-Sleep -Seconds 15
$p=Get-Win
if(-not $p){ throw "Capture build did not open." }
$root=$A::FromHandle($p.MainWindowHandle)
$id=New-Object Windows.Automation.PropertyCondition ($A::AutomationIdProperty),'StartStopToggle'
$tg=$root.FindFirst([Windows.Automation.TreeScope]::Descendants,$id)
Log ("Capture build running - START/STOP shows: " + $(if($tg){$tg.Current.Name}else{'?'}))

& "$PSScriptRoot\capture.ps1" -Minutes $Minutes -AutoPages $Pages -PageSeconds $PageSeconds
