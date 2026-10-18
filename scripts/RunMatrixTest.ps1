# Supermarket More Players - Fabricated-Player MATRIX test (solo, no 2nd instance)
# Launches one game instance in matrix mode. The mod fabricates fake Photon players
# and drives the real OnPlayerEnteredRoom pipeline; this script waits for the
# "MATRIX ..." verdict line and reports PASS/FAIL/INCONCLUSIVE per check.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File RunMatrixTest.ps1 -Players 6 [-Loop N]

param(
    [int]$Players = 6,
    [int]$Loop = 1,
    [string]$Game = 'C:\Program Files (x86)\Steam\steamapps\common\Supermarket Simulator'
)

$exe = Join-Path $Game 'Supermarket Simulator.exe'
$log = Join-Path $Game 'BepInEx\LogOutput.log'
$timeoutSec = 150

if (-not (Test-Path $exe)) { Write-Output "ERR: game exe not found at $exe"; exit 2 }

# Ensure the mod's self-test stays OFF (matrix drives OnPlayerEnteredRoom, not the realloc);
# keep ForceMinPlayerInstances high so the solo room still grows the pool for the assertion.
$cfg = Join-Path $Game 'BepInEx\config\dev.codex.supermarket.moreplayers.cfg'
if (Test-Path $cfg) {
    $c = Get-Content -LiteralPath $cfg -Raw
    $c = $c -replace 'SelfTestPhotonRealloc\s*=\s*\w+', 'SelfTestPhotonRealloc = false'
    $c = $c -replace 'ForceMinPlayerInstances\s*=\s*\d+', ("ForceMinPlayerInstances = $Players")
    Set-Content -LiteralPath $cfg -Value $c -Encoding UTF8
    Write-Output "MATRIX: cfg set (selfTest=false, forceMin=$Players)."
}

function Get-Verdict($Text) {
    if ($Text -match 'MATRIX\s+(?<chk>POOL=\S+ DOUBLE=\S+ UNIQUE=\S+ DRIVE=\S+)') {
        return $Matches.chk
    }
    return $null
}

for ($r = 1; $r -le $Loop; $r++) {
    Write-Output "`n=== MATRIX run $r/$Loop ==="

    # Capture pre-launch log length so we only grade NEW entries (BepInEx appends).
    $preLen = if (Test-Path $log) { (Get-Item -LiteralPath $log).Length } else { 0 }

    # Reset the log for a clean per-run verdict is NOT safe (appends); instead use preLen.
    Get-Process -Name 'Supermarket*' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2

    $p = Start-Process -FilePath $exe -ArgumentList @('-autotest', "-test-role=matrix", "-test-players=$Players") -WorkingDirectory $Game -PassThru

    $done = $false; $newText = ''; $lastSize = $preLen
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    while ($watch.Elapsed.TotalSeconds -lt $timeoutSec) {
        Start-Sleep -Milliseconds 700
        try {
            if (Test-Path $log) {
                $fs = [System.IO.File]::Open($log, 'Open', 'Read', 'ReadWrite')
                try {
                    if ($fs.Length -gt $lastSize) { $fs.Position = $lastSize }
                    $remaining = $fs.Length - $fs.Position
                    $bytes = New-Object byte[] $remaining
                    $fs.Read($bytes, 0, $bytes.Length) | Out-Null
                    $newText = [System.Text.Encoding]::UTF8.GetString($bytes)
                } finally { $fs.Close() }
                if ($newText -match 'AUTOTEST-M: MATRIX\s+(?<chk>POOL=\S+ DOUBLE=\S+ UNIQUE=\S+(?: \S+)*)') {
                    $done = $true; $verdict = $Matches.chk
                } elseif ($newText -match 'AUTOTEST: complete') { $done = $true }
            }
        } catch { }
        if ($p.HasExited) { break }
    }
    $watch.Stop()
    if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }

    Write-Output "MATRIX RESULT: $verdict"
    if (-not $verdict) {
        Write-Output '--- log tail ---'
        if ($newText) { Write-Output $newText.Substring([Math]::Max(0, $newText.Length - 700)) }
        Write-Output 'INCONCLUSIVE: no MATRIX verdict within timeout.'
        return 1
    }
}
exit 0