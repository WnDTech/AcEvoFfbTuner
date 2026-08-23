param(
    [ValidateSet("Release", "Debug")][string]$Configuration = "Release"
)
$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $PSScriptRoot "fake_rs50\fake_rs50.vcxproj"
$msbuild = "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe"

if (-not (Test-Path $msbuild)) {
    throw "MSBuild not found at $msbuild"
}

Write-Host "== Building FakeRs50 ($Configuration) x64 =="
& $msbuild $proj /p:Configuration=$Configuration /p:Platform=x64 /v:m /nologo

if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

$outDir = Join-Path $PSScriptRoot "fake_rs50\x64\$Configuration\"
Write-Host ""
Write-Host "== Build output =="
Get-ChildItem $outDir | ForEach-Object { Write-Host ("  {0,14}  {1}" -f $_.Length, $_.Name) }
Write-Host ""
Write-Host "Install (elevated, test signing must be ON + rebooted):"
Write-Host "  devcon install $(Join-Path $outDir 'fake_rs50.inf') root\FakeRs50"
Write-Host "  -- or --"
Write-Host "  pnputil /add-driver $(Join-Path $outDir 'fake_rs50.inf') /install"