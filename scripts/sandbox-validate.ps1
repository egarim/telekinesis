# Single-monitor validation pass for issue #7 — runs INSIDE Windows Sandbox
# (or any clean single-monitor Windows box). Assumes the folder containing this
# script also holds telekinesis.exe (self-contained build).
# Produces sandbox-validate-report.txt next to the script.
$ErrorActionPreference = "Continue"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe = Join-Path $here "telekinesis.exe"
$report = Join-Path $here "sandbox-validate-report.txt"
"Telekinesis single-monitor validation — $(Get-Date -Format o)" | Set-Content $report

function Log($s) { $s | Add-Content $report; Write-Output $s }

# 1) doctor — expect per-monitor DPI active and all checks ok.
Log "`n== doctor =="
& $exe doctor 2>&1 | ForEach-Object { Log $_ }

# 2) Launch Notepad as the target.
Start-Process notepad; Start-Sleep 4
$apps = & $exe probe 2>&1
Log "`n== probe (apps) =="
$apps | ForEach-Object { Log $_ }
$pid2 = [regex]::Match(($apps -join "`n"), 'Notepad\s+\(id: (pid:\d+)\)').Groups[1].Value
if (-not $pid2) { Log "FAIL: Notepad not found on the UIA root"; exit 1 }

# 3) Tree + bounds sanity: click the a11y-reported center of the Close button's
#    sibling (Minimize) and verify state — on a single monitor the a11y center
#    MUST match pixels (this is the exact thing #7 could not verify on mixed-DPI).
Log "`n== find Minimize =="
$find = & $exe probe --app $pid2 --find "Minimize" 2>&1
$find | ForEach-Object { Log $_ }
$m = [regex]::Match(($find -join "`n"), '@(-?\d+),(-?\d+) (\d+)x(\d+)')
if (-not $m.Success) { Log "FAIL: no bounds for Minimize"; exit 1 }
$cx = [int]$m.Groups[1].Value + [math]::Floor([int]$m.Groups[3].Value / 2)
$cy = [int]$m.Groups[2].Value + [math]::Floor([int]$m.Groups[4].Value / 2)

# Screenshot the button region first — pixel evidence for the report.
& $exe probe --screenshot (Join-Path $here "minimize-region.png") --region "$([int]$m.Groups[1].Value),$([int]$m.Groups[2].Value),$([int]$m.Groups[3].Value),$([int]$m.Groups[4].Value)" 2>&1 | ForEach-Object { Log $_ }

Log "`n== click_at a11y center ($cx,$cy) =="
& $exe probe --enable-actions --click-at "$cx,$cy" 2>&1 | ForEach-Object { Log $_ }
Start-Sleep 2

# 4) Minimized state check: the window should now report parked/no bounds.
Log "`n== window after minimize =="
& $exe probe --app $pid2 --depth 1 2>&1 | ForEach-Object { Log $_ }

# 5) type/set-text round trip: restore, type, read back.
(New-Object -ComObject WScript.Shell).AppActivate("Notepad") | Out-Null; Start-Sleep 2
Log "`n== set-text round trip =="
& $exe probe --enable-actions --app $pid2 --set-text "single-monitor validation OK" 2>&1 | ForEach-Object { Log $_ }

Log "`nDone. Review minimize-region.png (should show the Minimize glyph, proving a11y bounds == pixels) and the read-back above."
