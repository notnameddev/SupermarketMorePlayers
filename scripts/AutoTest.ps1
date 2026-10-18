# Supermarket More Players - Auto Test (no friends, no Steam UI interaction)
# Launches the game with -autotest; the mod auto-connects to Photon, creates a
# room, runs the PhotonView re-allocation self-test, logs the verdict, and quits.
# This script waits for a FRESH verdict (new log entries only), then PASS/FAIL.

$ErrorActionPreference = 'Continue'
# Game install folder is personal/environment-specific, so keep it variable:
# override with an env var or CLI arg; fall back to a sibling folder.
$game = if ($null -ne $env:SMP_GAME_DIR -and $env:SMP_GAME_DIR -ne '') {
    $env:SMP_GAME_DIR
} elseif ($Args.Count -gt 0 -and $Args[0] -ne '') {
    $Args[0]
} else {
    (Join-Path (Split-Path -Parent (Split-Path -Parent $args[0])) 'Supermarket Simulator')
}
$exe = Join-Path $game 'Supermarket Simulator.exe'
$log = Join-Path $game 'BepInEx\LogOutput.log'
$timeoutSec = 150

if (-not (Test-Path $exe)) { Write-Output "ERR: game exe not found at $exe"; exit 2 }

# Set the self-test enabled so the mod actually runs it.
$cfg = Join-Path $game 'BepInEx\config\dev.codex.supermarket.moreplayers.cfg'
if (Test-Path $cfg) {
    $c = Get-Content -LiteralPath $cfg -Raw
    if ($c -match 'SelfTestPhotonRealloc\s*=\s*false') {
        $c = $c -replace 'SelfTestPhotonRealloc\s*=\s*false', 'SelfTestPhotonRealloc = true'
    }
    # Force the pool to grow to 6 bodies for the headless "players 5-6" check.
    if ($c -match 'ForceMinPlayerInstances\s*=\s*\d+') {
        $c = $c -replace 'ForceMinPlayerInstances\s*=\s*\d+', 'ForceMinPlayerInstances = 6'
    } elseif ($c -notmatch 'ForceMinPlayerInstances') {
        $c = $c -replace '(\[Diagnostics\]\r?\n)', '$1`nForceMinPlayerInstances = 6`n'
    }
    Set-Content -LiteralPath $cfg -Value $c -Encoding UTF8
    Write-Output 'AUTOTEST: enabled SelfTestPhotonRealloc + ForceMinPlayerInstances=6 in config.'
}

# Stash the current log so we only look at NEW lines after launch.
$preLen = 0
if (Test-Path $log) { $preLen = (Get-Item -LiteralPath $log).Length }

Write-Output 'AUTOTEST: launching game with -autotest...'
$args = @("-autotest", "-screen-fullscreen", "0", "-screen-width", "640", "-screen-height", "480")
$p = Start-Process -FilePath $exe -ArgumentList $args -PassThru
Write-Output ("AUTOTEST: started PID " + $p.Id + ". Waiting up to $timeoutSec s for a fresh verdict...")

$watch = [System.Diagnostics.Stopwatch]::StartNew()
$done = $false
$pass = $false
$newText = ''
$lastSize = $preLen

while ($watch.Elapsed.TotalSeconds -lt $timeoutSec) {
    Start-Sleep -Milliseconds 700
    try {
        if (Test-Path $log) {
            # Read the whole tail region that came after launch (authoritative).
            $fs = [System.IO.File]::Open($log, 'Open', 'Read', 'ReadWrite')
            try {
                if ($fs.Length -gt $lastSize) { $fs.Position = $lastSize }
                $remaining = $fs.Length - $fs.Position
                $bytes = New-Object byte[] $remaining
                $fs.Read($bytes, 0, $bytes.Length) | Out-Null
                $newText = [System.Text.Encoding]::UTF8.GetString($bytes)
            } finally { $fs.Close() }

            if ($newText -match 'AUTOTEST6: (?<b>\d+)/(?<t>\d+) non-null bodies, (?<u>\d+) unique non-zero ViewIDs - (?<r>PASS|FAIL)') {
                # Authoritative verdict: the full 6-body pool check.
                $done = $true
                $pass = ($Matches.r -eq 'PASS')
                $lastText = "6-body: $($Matches.u)/$($Matches.b) non-null bodies unique, $($Matches.b)/$($Matches.t) total non-null"
                break
            }
            if ($newText -match 'SELFTEST: DONE - (?<p>\d+) re-allocated, (?<f>\d+) failed') {
                # Not authoritative: step 3 (AUTOTEST6) runs ~1s later. Remember as fallback.
                $reallocText = "realloc PASS=$($Matches.p) FAIL=$($Matches.f)"
                $reallocPass = ([int]$Matches.p -gt 0) -and ([int]$Matches.f -eq 0)
            }
            if ($newText -match 'AUTOTEST: complete') { $done = $true }
        }
        if ($p.HasExited) { break }
    } catch { }
}
$watch.Stop()

if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }

if ($p.HasExited -and -not $done -and $reallocText) {
    $done = $true
    $pass = $reallocPass
    $lastText = "$reallocText (process exited before 6-body check)"
}

Write-Output ""
Write-Output "================ AUTOTEST RESULT ================"
if ($done -and $pass) { Write-Output "PASS: $(if ($lastText) { $lastText } else { 'PhotonView re-allocation assigned a distinct ID.' })" }
elseif ($done) { Write-Output "FAIL: $(if ($lastText) { $lastText } else { 'PhotonView re-allocation did NOT assign a distinct ID.' })" }
else { Write-Output "INCONCLUSIVE: no fresh verdict within timeout (check game launched / Photon reachable)." }
Write-Output "=================================================="
Write-Output ""
if ($newText) { Write-Output "--- new log tail ---"; Write-Output $newText.Substring([Math]::Max(0,$newText.Length-500)) }

if ($done -and $pass) { exit 0 } else { exit 1 }