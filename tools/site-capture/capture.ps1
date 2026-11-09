<#
  Website footage recorder for the FFB Tuner.

  Captures the FFB Tuner window while you drive and files the frames under whichever
  page is showing. With -AutoPages it also cycles the app's pages on a timer using
  UI Automation (no clicks, no focus change - safe while driving).

  - Uses PrintWindow, so the app can be behind the game or on a second screen
    (just not minimised).
  - Stops after -Minutes, or when the app closes, or Ctrl+C.

  Usage:
    powershell -ExecutionPolicy Bypass -File capture.ps1 -Minutes 5 `
      -AutoPages "Home,Telemetry,Live Map,FFB Coach,Equalizer,Devices,FFB Tuning" -PageSeconds 40
#>
param(
  [int]$Minutes = 15,
  [int]$Fps = 10,
  [string]$Out = (Join-Path $PSScriptRoot 'raw'),
  [string]$AutoPages = '',
  [int]$PageSeconds = 90
)

Add-Type -AssemblyName System.Drawing, UIAutomationClient, UIAutomationTypes
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public class W {
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint f);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out R r);
  [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  public struct R { public int L, T, Rt, B; }
}
"@
[W]::SetProcessDPIAware() | Out-Null

$p = Get-Process AcEvoFfbTuner -EA SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $p) { throw "FFB Tuner is not running - start it first." }
$h = $p.MainWindowHandle
$AE = [Windows.Automation.AutomationElement]
$root = $AE::FromHandle($h)

$jpg = [Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() | Where-Object { $_.MimeType -eq 'image/jpeg' }
$ep = New-Object Drawing.Imaging.EncoderParameters 1
$ep.Param[0] = [Drawing.Imaging.EncoderParameter]::new([Drawing.Imaging.Encoder]::Quality, [long]92)

$navNames = 'Home','FFB Tuning','Equalizer','FFB Coach','Devices','Profiles','Hub','Test Drive','Telemetry','Pipeline','Overlays','Live Map'
$radio = New-Object Windows.Automation.PropertyCondition ($AE::ControlTypeProperty), ([Windows.Automation.ControlType]::RadioButton)
function Find-Nav { @($root.FindAll([Windows.Automation.TreeScope]::Descendants, $radio) | Where-Object { $navNames -contains $_.Current.Name }) }
$script:navEls = Find-Nav

function Current-Page {
  if ($script:navEls.Count -lt 5) { $script:navEls = Find-Nav }
  foreach ($e in $script:navEls) {
    try { if ($e.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected) { return $e.Current.Name } } catch {}
  }
  return 'unknown'
}
function Select-Page([string]$name) {
  if ($script:navEls.Count -lt 5) { $script:navEls = Find-Nav }
  $e = $script:navEls | Where-Object { $_.Current.Name -eq $name } | Select-Object -First 1
  Write-Host ("[{0}] switch -> {1}" -f (Get-Date -Format 'HH:mm:ss'), $name)
  if ($e) { try { $e.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select() } catch { Write-Host "select failed: $_" } }
  else { Write-Host "  page '$name' not found" }
}

$auto = @($AutoPages -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
$ai = 0; $nextSwitch = Get-Date
$session = Get-Date -Format 'yyyyMMdd-HHmmss'
$iv = 1000.0 / $Fps; $end = (Get-Date).AddMinutes($Minutes)
$page = ''; $dir = ''; $i = 0; $total = 0; $tick = 0
$sw = [Diagnostics.Stopwatch]::StartNew()
Write-Host "Recording at $Fps fps for up to $Minutes min. Auto pages: $($auto -join ' > ')"

while ((Get-Date) -lt $end) {
  if ($p.HasExited) { Write-Host "App closed."; break }
  if ($auto.Count -gt 0 -and (Get-Date) -ge $nextSwitch) {
    Select-Page ($auto[$ai % $auto.Count]); $ai++
    $nextSwitch = (Get-Date).AddSeconds($PageSeconds)
  }
  if (($tick % [Math]::Max(1, [int]($Fps / 2))) -eq 0) {
    $pg = Current-Page
    if ($pg -ne $page) {
      $page = $pg; $i = 0
      $dir = Join-Path $Out ("{0}_{1}_{2}" -f $session, ($page -replace ' ', '-').ToLower(), (Get-Date -Format 'HHmmss'))
      New-Item -ItemType Directory -Force $dir | Out-Null
      Write-Host ("[{0}] page: {1}" -f (Get-Date -Format 'HH:mm:ss'), $page)
    }
  }
  $tick++
  if ([W]::IsIconic($h)) { Start-Sleep -Milliseconds 500; continue }
  $rc = New-Object W+R; [W]::GetWindowRect($h, [ref]$rc) | Out-Null
  $w = $rc.Rt - $rc.L; $ht = $rc.B - $rc.T
  if ($w -gt 100 -and $ht -gt 100) {
    $bmp = New-Object Drawing.Bitmap $w, $ht; $g = [Drawing.Graphics]::FromImage($bmp)
    $dc = $g.GetHdc(); [W]::PrintWindow($h, $dc, 2) | Out-Null; $g.ReleaseHdc($dc)
    $bmp.Save((Join-Path $dir ('f{0:D6}.jpg' -f $i)), $jpg, $ep); $g.Dispose(); $bmp.Dispose()
    $i++; $total++
  }
  $wait = [int]($total * $iv - $sw.ElapsedMilliseconds); if ($wait -gt 0) { Start-Sleep -Milliseconds $wait }
}
Write-Host "Done: $total frames in $Out"
