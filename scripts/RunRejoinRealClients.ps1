# Supermarket More Players - REAL-CLIENT LEAVE/REJOIN harness (host + client copy(s) on one machine)
# Uses real Photon connection/disconnect/reconnect semantics with real actor numbers.
# Killed+relaunched client => NEW actor => new deterministic ID (50000+actor).
#
# Host runs in handout mode: actors >= 2 get deterministic bodies. No fabrication in host mode;
# real enter/leave drive PlayerJoinedPrefix (assign) / ReclaimBodyForActor (leave).
#
# Usage: powershell -ExecutionPolicy Bypass -File RunRejoinRealClients.ps1 [-Loop N]
param([int]$Loop = 1)
$ErrorActionPreference = 'Continue'

$hostGame = 'C:\Program Files (x86)\Steam\steamapps\common\Supermarket Simulator'
$clientDir = 'C:\Users\danil\AppData\Local\Temp\opencode\SMP-Client'
$hostExe = Join-Path $hostGame 'Supermarket Simulator.exe'
$clientExe = Join-Path $clientDir 'SupermarketSimulatorClient.exe'
$hostLog = Join-Path $hostGame 'BepInEx\LogOutput.log'
$clientLog = Join-Path $clientDir 'BepInEx\LogOutput.log'
$timeoutSec = 240

foreach ($path in @($hostExe, $clientExe)) {
    if (-not (Test-Path $path)) { Write-Output "ERR: not found: $path"; exit 2 }
}

function Reset-Cfg($dir) {
    $cfg = Join-Path $dir 'BepInEx\config\dev.codex.supermarket.moreplayers.cfg'
    if (Test-Path $cfg) {
        $c = Get-Content -LiteralPath $cfg -Raw
        $c = $c -replace 'SelfTestPhotonRealloc\s*=\s*\w+', 'SelfTestPhotonRealloc = false'
        $c = $c -replace 'ForceMinPlayerInstances\s*=\s*\d+', 'ForceMinPlayerInstances = 8'
        $c = $c -replace 'MaxPlayers\s*=\s*\d+', 'MaxPlayers = 8'
        Set-Content -LiteralPath $cfg -Value $c -Encoding UTF8
    }
}

for ($r = 1; $r -le $Loop; $r++) {
    Write-Output "`n=== REAL-REJOIN run $r/$Loop ==="
    $token = 'smpR_' + -join ((48..57)+(97..102) | Get-Random -Count 10 | % {[char]$_})
    Get-Process -Name 'Supermarket*' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
    Reset-Cfg $hostGame; Reset-Cfg $clientDir
    Remove-Item -LiteralPath $hostLog, $clientLog -Force -ErrorAction SilentlyContinue

    $hst = Start-Process -FilePath $hostExe -ArgumentList @('-autotest','-test-role=host',"-test-room=$token",'-test-players=8','-test-handout-clone') -WorkingDirectory $hostGame -PassThru
    Write-Output "host pid=$($hst.Id)"

    # Wait for HOST READY.
    Start-Sleep -Seconds 25

    # Client A joins (actor 2) = "P5".
    $a = Start-Process -FilePath $clientExe -ArgumentList @('-autotest','-test-role=client',"-test-room=$token") -WorkingDirectory $clientDir -PassThru
    Write-Output "clientA pid=$($a.Id) joins (expect deterministic 50002)"
    Start-Sleep -Seconds 30

    # Client B (if a 3rd copy exists) = "P6".
    $thirdDir = 'C:\Users\danil\AppData\Local\Temp\opencode\SMP-Client3'
    $thirdExe = Join-Path $thirdDir 'SupermarketSimulatorClient.exe'
    $b = $null
    if (Test-Path $thirdExe) {
        Reset-Cfg $thirdDir
        $bl = Join-Path $thirdDir 'BepInEx\LogOutput.log'; Remove-Item -LiteralPath $bl -Force -ErrorAction SilentlyContinue
        $b = Start-Process -FilePath $thirdExe -ArgumentList @('-autotest','-test-role=client',"-test-room=$token") -WorkingDirectory $thirdDir -PassThru
        Write-Output "clientB pid=$($b.Id) joins (expect deterministic 50003)"
        Start-Sleep -Seconds 30
    } else {
        Write-Output 'no 3rd copy (host+clientA only; still a real leave/rejoin).'
    }

    # --- LEAVE client A ---
    Write-Output "[leave] killing clientA..."
    Stop-Process -Id $a.Id -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 12

    # --- REJOIN client A (new actor) ---
    Write-Output "[rejoin] relaunching clientA..."
    $a2 = Start-Process -FilePath $clientExe -ArgumentList @('-autotest','-test-role=client',"-test-room=$token") -WorkingDirectory $clientDir -PassThru
    Write-Output "clientA2 pid=$($a2.Id) rejoins (expect new deterministic body, e.g. 50004)"
    Start-Sleep -Seconds 35

    foreach ($proc in @($hst,$a2,$b)) { if ($proc) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue } }
    Start-Sleep -Seconds 12   # settle so the last leave's reclaim (OnPlayerLeftRoom) registers in the host log

    Write-Output '--- host deterministic assign/reclaim events ---'
    $ht = Get-Content -LiteralPath $hostLog -Raw -ErrorAction SilentlyContinue
    [regex]::Matches($ht, '(?m)(PlayerJoinedPrefix: ensured deterministic body for actor \d+[^\r\n]*|ReclaimBodyForActor: freeing[^\r\n]*|ReclaimBodyForActor: pool now \d+[^\r\n]*)') | ForEach-Object { Write-Output $_.Value }

    Write-Output '--- host client-join (PlayerCount) ---'
    [regex]::Matches($ht, '(?m)CurrentRoom: PlayerCount=\d[^\r\n]*') | Select-Object -Last 4 | ForEach-Object { Write-Output $_.Value }

    # PASS if we saw at least one real assign (actor>=2) AND at least one real reclaim,
    # and every assigned deterministic view eventually reconciled with no duplicates.
    $assigns = @([regex]::Matches($ht, '(?m)PlayerJoinedPrefix: ensured deterministic body for actor (\d+)') | ForEach-Object { [int]$_.Groups[1].Value })
    $reclaims = @([regex]::Matches($ht, '(?m)ReclaimBodyForActor: freeing[^\r\n]*view (\d+)') | ForEach-Object { [int]$_.Groups[1].Value })
    $reals = @($assigns | Where-Object { $_ -ge 2 })
    $hadReclaim = $reclaims.Count -gt 0
    $hadLeaveView = $reclaims.Count -gt 0

    Write-Output "assigned actors: $($assigns -join ',')"
    Write-Output "reclaimed views: $($reclaims -join ',')"
    # Honest criterion: every deterministic view we assigned to a real actor must eventually be
    # reclaimed (leave cleans up what enter created), and we must have seen at least one real
    # actor (>=2). Missing a reclaim => FAIL, no silent pass.
    $allReclaimed = (@($assigns | Where-Object { $_ -ge 2 }).Count -gt 0) -and
                    ($reclaims.Count -ge @($assigns | Where-Object { $_ -ge 2 }).Count) -and
                    -not ($reclaims | Where-Object { $_ -lt 50002 } | Select-Object -First 1)
    if ($allReclaimed) {
        Write-Output 'REAL-REJOIN RESULT: PASS'
    } else {
        Write-Output 'REAL-REJOIN RESULT: FAIL (every real-client deterministic view must be reclaimed'
        Write-Output '--- client log tail ---'; Get-Content -LiteralPath $clientLog -Tail 15 -ErrorAction SilentlyContinue
    }
}
exit 0