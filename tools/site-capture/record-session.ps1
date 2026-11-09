<#
  Records the GAME screen and the APP screen together, frame-synced, for website footage.

  - One ffmpeg process grabs both monitors with Desktop Duplication (works with fullscreen
    games) and encodes on the GPU (AMD AMF), so the two files share one clock.
  - Optionally cycles the FFB Tuner's pages via UI Automation (no clicks, no focus change)
    and logs when each page was shown (pages.json) so clips can be cut per page.

  Usage:
    powershell -ExecutionPolicy Bypass -File record-session.ps1 -Minutes 5 `
      -AutoPages "Home,Telemetry,Live Map,FFB Tuning" -PageSeconds 45
  Output: session\<stamp>\game.mp4, app.mp4, pages.json
#>
param(
  [double]$Minutes = 5,
  [int]$Fps = 30,
  [int]$GameOutput = 0,
  [int]$AppOutput = 1,
  [string]$AutoPages = '',
  [int]$PageSeconds = 45,
  [string]$Encoder = 'h264_amf'
)

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$dir = Join-Path $PSScriptRoot "session\$stamp"
New-Item -ItemType Directory -Force $dir | Out-Null

$fc = "[0]hwdownload,format=bgra,scale=1920:-2:flags=bicubic,format=nv12[g];" +
      "[1]hwdownload,format=bgra,format=nv12[a]"
$ffArgs = @('-hide_banner','-loglevel','warning','-y',
  '-f','lavfi','-i',"ddagrab=output_idx=${GameOutput}:framerate=${Fps}:draw_mouse=0",
  '-f','lavfi','-i',"ddagrab=output_idx=${AppOutput}:framerate=${Fps}:draw_mouse=0",
  '-filter_complex',$fc,
  '-map','[g]','-c:v',$Encoder,'-b:v','24M','-t',"$($Minutes*60)",(Join-Path $dir 'game.mp4'),
  '-map','[a]','-c:v',$Encoder,'-b:v','16M','-t',"$($Minutes*60)",(Join-Path $dir 'app.mp4'))

$psi = New-Object Diagnostics.ProcessStartInfo 'ffmpeg'
$psi.Arguments = ($ffArgs | ForEach-Object { if ($_ -match '[\s;\[\]]') { '"' + $_ + '"' } else { $_ } }) -join ' '
$psi.UseShellExecute = $false; $psi.RedirectStandardInput = $true; $psi.CreateNoWindow = $true
$ff = [Diagnostics.Process]::Start($psi)
$t0 = Get-Date
Write-Host "Recording game (output $GameOutput) + app (output $AppOutput) for $Minutes min -> $dir"

# ---- page cycling (same UIA approach as capture.ps1) ----
$auto = @($AutoPages -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
$log = New-Object System.Collections.ArrayList
if ($auto.Count) {
  Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
  $AE = [Windows.Automation.AutomationElement]
  $p = Get-Process AcEvoFfbTuner -EA SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
  if ($p) {
    $root = $AE::FromHandle($p.MainWindowHandle)
    $radio = New-Object Windows.Automation.PropertyCondition ($AE::ControlTypeProperty), ([Windows.Automation.ControlType]::RadioButton)
    $nav = @($root.FindAll([Windows.Automation.TreeScope]::Descendants, $radio))
  } else { Write-Host "FFB Tuner not running - recording without page cycling." }
}
$ai = 0; $next = Get-Date
while (-not $ff.HasExited) {
  if ($nav -and (Get-Date) -ge $next) {
    $name = $auto[$ai % $auto.Count]; $ai++
    $e = $nav | Where-Object { $_.Current.Name -eq $name } | Select-Object -First 1
    if ($e) {
      try { $e.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select() } catch {}
      $sec = [Math]::Round(((Get-Date) - $t0).TotalSeconds, 2)
      [void]$log.Add([pscustomobject]@{ page = $name; at = $sec })
      Write-Host ("[{0,6}s] page -> {1}" -f $sec, $name)
    }
    $next = (Get-Date).AddSeconds($PageSeconds)
  }
  Start-Sleep -Milliseconds 250
}
$log | ConvertTo-Json | Set-Content (Join-Path $dir 'pages.json') -Encoding UTF8
Write-Host "Done -> $dir"
