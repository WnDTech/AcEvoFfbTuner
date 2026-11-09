<# Records a smooth scroll through the current page's largest scrollable area (no clicks, no setting changes). #>
param([int]$Seconds=10,[int]$Fps=20,[string]$Out=(Join-Path $PSScriptRoot 'raw\tour'))
Add-Type -AssemblyName System.Drawing,UIAutomationClient,UIAutomationTypes
Add-Type -TypeDefinition @"
using System; using System.Runtime.InteropServices;
public class W2 {
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint f);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out R r);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  public struct R { public int L, T, Rt, B; }
}
"@
[W2]::SetProcessDPIAware() | Out-Null
$A=[Windows.Automation.AutomationElement]
$p=Get-Process AcEvoFfbTuner | ?{$_.MainWindowHandle -ne 0} | select -First 1; $h=$p.MainWindowHandle
$root=$A::FromHandle($h)
$c=New-Object Windows.Automation.PropertyCondition ($A::IsScrollPatternAvailableProperty),$true
$sv=$root.FindAll([Windows.Automation.TreeScope]::Descendants,$c) | ?{ $_.GetCurrentPattern([Windows.Automation.ScrollPattern]::Pattern).Current.VerticallyScrollable } |
    sort { $r=$_.Current.BoundingRectangle; -($r.Width*$r.Height) } | select -First 1
$sp=$sv.GetCurrentPattern([Windows.Automation.ScrollPattern]::Pattern)
$jpg=[Drawing.Imaging.ImageCodecInfo]::GetImageEncoders()|?{$_.MimeType -eq 'image/jpeg'}
$ep=New-Object Drawing.Imaging.EncoderParameters 1; $ep.Param[0]=[Drawing.Imaging.EncoderParameter]::new([Drawing.Imaging.Encoder]::Quality,[long]92)
Remove-Item -Recurse -Force $Out -EA SilentlyContinue; New-Item -ItemType Directory -Force $Out | Out-Null
$n=$Seconds*$Fps
for($i=0;$i -lt $n;$i++){
  $t=$i/($n-1); $e=(1-[Math]::Cos([Math]::PI*$t))/2   # ease in/out
  $sp.SetScrollPercent(-1, [Math]::Min(100,[Math]::Max(0,100*$e)))
  Start-Sleep -Milliseconds 30
  $rc=New-Object W2+R; [W2]::GetWindowRect($h,[ref]$rc)|Out-Null
  $bmp=New-Object Drawing.Bitmap ($rc.Rt-$rc.L),($rc.B-$rc.T); $g=[Drawing.Graphics]::FromImage($bmp)
  $dc=$g.GetHdc(); [W2]::PrintWindow($h,$dc,2)|Out-Null; $g.ReleaseHdc($dc)
  $bmp.Save((Join-Path $Out ('f{0:D6}.jpg' -f $i)),$jpg,$ep); $g.Dispose(); $bmp.Dispose()
}
$sp.SetScrollPercent(-1,0)
"$n frames -> $Out"
