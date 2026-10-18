# Supermarket More Players - LEAVE/REJOIN scenario test (solo host, fabricated actors)
# Runs NetworkManager.OnPlayerEnteredRoom / OnPlayerLeftRoom with fabricated players whose
# deterministic IDs = 50000 + actor. A rejoin is a NEW actor number, so the invariant under
# test is: no stale duplicate ViewID after leave + rejoin. Covers the two scenarios:
#   S1: P5 enters, P6 enters, P5 leaves, P5 rejoins
#   S2: P5 enters, P6 enters, P5 leaves, P6 leaves, P6 enters, P5 enters
#
# Usage: powershell -ExecutionPolicy Bypass -File RunRejoinTest.ps1 [-Scenario 1|2|0(all)] [-Loop N]
param([int]$Scenario = 0, [int]$Loop = 1)
$ErrorActionPreference = 'Continue'
$Game = 'C:\Program Files (x86)\Steam\steamapps\common\Supermarket Simulator'
$exe = Join-Path $Game 'Supermarket Simulator.exe'
$log = Join-Path $Game 'BepInEx\LogOutput.log'
$timeoutSec = 150
if (-not (Test-Path $exe)) { Write-Output "ERR: game exe not found at $exe"; exit 2 }

# Keep self-test off; the rejoin driver drives enter/leave directly.
$cfg = Join-Path $Game 'BepInEx\config\dev.codex.supermarket.moreplayers.cfg'
if (Test-Path $cfg) {
    $c = Get-Content -LiteralPath $cfg -Raw
    $c = $c -replace 'SelfTestPhotonRealloc\s*=\s*\w+', 'SelfTestPhotonRealloc = false'
    Set-Content -LiteralPath $cfg -Value $c -Encoding UTF8
}

for ($r = 1; $r -le $Loop; $r++) {
    Write-Output "`n=== REJOIN run $r/$Loop scenario=$Scenario ==="
    $preLen = if (Test-Path $log) { (Get-Item -LiteralPath $log).Length } else { 0 }
    Get-Process -Name 'Supermarket*' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
    Remove-Item -LiteralPath $log -Force -ErrorAction SilentlyContinue; $preLen = 0

    $p = Start-Process -FilePath $exe -ArgumentList @('-autotest', '-test-role=rejoin', "-test-scenario=$Scenario") -WorkingDirectory $Game -PassThru

    $done=$false; $verdict=''; $newText=''; $lastSize=$preLen
    $watch=[System.Diagnostics.Stopwatch]::StartNew()
    while ($watch.Elapsed.TotalSeconds -lt $timeoutSec) {
        Start-Sleep -Milliseconds 700
        try {
            if (Test-Path $log) {
                $fs=[System.IO.File]::Open($log,'Open','Read','ReadWrite')
                try {
                    if ($fs.Length -gt $lastSize) { $fs.Position=$lastSize }
                    $rem=$fs.Length-$fs.Position; $b=New-Object byte[] $rem; $fs.Read($b,0,$b.Length)|Out-Null
                    $newText=[System.Text.Encoding]::UTF8.GetString($b)
                } finally { $fs.Close() }
                if ($newText -match 'AUTOTEST-R: REJOIN\s+(?<chk>.*?ALL=(?:PASS|FAIL))') {
                    $done=$true; $verdict=$Matches.chk
                } elseif ($newText -match 'REJOIN\s+INVALID_SCENARIO') { $done=$true; $verdict='INVALID_SCENARIO' }
            }
        } catch { }
        if ($p.HasExited) { break }
    }
    $watch.Stop()
    if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }

    Write-Output "REJOIN RESULT: $verdict"
    if (-not $verdict) {
        Write-Output '--- log tail ---'
        if ($newText) { Write-Output $newText.Substring([Math]::Max(0,$newText.Length-700)) }
        Write-Output 'INCONCLUSIVE: no REJOIN verdict within timeout.'
        exit 1
    }
}
exit 0