# Runs real-input Probes one after another. Only while the user is away: they move the mouse and type.
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File tools/run-real-probes.ps1 -Probes "d13b,d03,d04" -Out <folder>
# Each Probe writes into <folder>\<probe>\ (console.txt + its result files); one line per Probe goes to <folder>\summary.txt.
param([string[]]$Probes, [string]$Out = "artifacts/real-probes")
$Probes = $Probes | ForEach-Object { $_ -split "," } | Where-Object { $_ }
Set-Location (Split-Path $PSScriptRoot -Parent)
$probe = "tools/PdfNotes.Probe/bin/Release/net10.0-windows/win-x64/PdfNotes.Probe.exe"
$root = [System.IO.Path]::GetFullPath($Out)
New-Item -ItemType Directory -Force $root | Out-Null
$summary = Join-Path $root "summary.txt"
foreach ($p in $Probes) {
    $o = Join-Path $root $p
    if (Test-Path $o) { Remove-Item -Recurse -Force $o }
    New-Item -ItemType Directory -Force $o | Out-Null
    $start = Get-Date
    $out = & $probe $p $o 2>&1 | Out-String
    $code = $LASTEXITCODE
    $out | Out-File -Encoding utf8 (Join-Path $o "console.txt")
    $fails = ($out -split "`n" | Where-Object { $_ -match "^FAIL|Exception" } | Select-Object -First 3) -join " || "
    $pass = ([regex]::Matches($out, "(?m)^PASS")).Count
    $line = "{0,-10} exit={1} pass-lines={2} {3:0}s {4}" -f $p, $code, $pass, ((Get-Date) - $start).TotalSeconds, $fails
    Add-Content -Encoding utf8 $summary $line
    Start-Sleep -Seconds 2 # the next Probe needs the foreground back
}
Add-Content -Encoding utf8 $summary "done"
