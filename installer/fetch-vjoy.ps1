<#
.SYNOPSIS
  Downloads the pinned vJoy installer for bundling (hash-verified).
.DESCRIPTION
  Reads the pin (tag / asset URL / SHA256) from
  installer/third-party/vjoy/VERSION.txt, downloads vJoySetup.exe next to it
  and verifies the SHA256. The binary is git-ignored and consumed by
  installer/setup.iss ([Components] vjoy → {tmp} → silent install).
  Without the binary present, setup.iss builds fine and simply omits the
  vJoy component (skipifsourcedoesntexist).
.PARAMETER FailOnError
  Exit non-zero when the download or hash check fails (CI release builds).
#>
param([switch]$FailOnError)

$ErrorActionPreference = 'Stop'
$dir = Join-Path $PSScriptRoot 'third-party' 'vjoy'
$versionFile = Join-Path $dir 'VERSION.txt'
$outFile = Join-Path $dir 'vJoySetup.exe'

function Fail($msg) {
    Write-Error $msg
    if ($FailOnError) { exit 1 }
}

try {
    $text = Get-Content $versionFile -Raw
    $url = ([regex]::Match($text, '^\s*URL:\s*(\S+)\s*$', 'Multiline').Groups[1].Value)
    $sha = ([regex]::Match($text, '^\s*SHA256:\s*([0-9A-Fa-f]{64})\s*$', 'Multiline').Groups[1].Value).ToUpperInvariant()
    if (-not $url -or -not $sha) { Fail "VERSION.txt pin is incomplete (need URL + SHA256)"; return }

    if (Test-Path $outFile) {
        $existing = (Get-FileHash $outFile -Algorithm SHA256).Hash.ToUpperInvariant()
        if ($existing -eq $sha) { Write-Output "vJoySetup.exe already present, hash OK"; return }
        Write-Output "Existing vJoySetup.exe hash mismatch — re-downloading"
    }

    Write-Output "Downloading $url"
    Invoke-WebRequest -Uri $url -OutFile $outFile
    $actual = (Get-FileHash $outFile -Algorithm SHA256).Hash.ToUpperInvariant()
    if ($actual -ne $sha) {
        Remove-Item $outFile -Force -ErrorAction SilentlyContinue
        Fail "SHA256 mismatch: expected $sha, got $actual"
        return
    }
    Write-Output "vJoySetup.exe verified ($actual)"
}
catch {
    Fail "fetch-vjoy failed: $($_.Exception.Message)"
}
