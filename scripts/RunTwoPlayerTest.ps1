# Supermarket More Players - TWO-INSTANCE harness (host + real client on one machine)
# Requires a second full game copy with a RENAMED exe (to bypass the game's single-instance
# guard) and renamed _Data folder. Defaults match the local test setup.
#
#   HOST  = original install  -> "Supermarket Simulator.exe", role=host, creates room token
#   CLIENT = copied install  -> renamed exe, role=client, joins room token
#
# The mod drives: host waits for the real client, then fabricates players #5-N and asserts
# the host-side pipeline (POOL/DOUBLE/UNIQUE/DRIVE). The client reports what IT acquired
# (JOINED/LOCAL_BODY/FRESH_VIEW), which is the cross-client check for the fresh view.
#
# Usage: powershell -ExecutionPolicy Bypass -File RunTwoPlayerTest.ps1 [-Players 6] [-Loop N]
param(
    [int]$Players = 6,
    [int]$Loop = 1
)
$ErrorActionPreference = 'Continue'

$hostGame = 'C:\Program Files (x86)\Steam\steamapps\common\Supermarket Simulator'
$clientDir = 'C:\Users\danil\AppData\Local\Temp\opencode\SMP-Client'
$hostExe = Join-Path $hostGame 'Supermarket Simulator.exe'
$clientExe = Join-Path $clientDir 'SupermarketSimulatorClient.exe'
$hostLog = Join-Path $hostGame 'BepInEx\LogOutput.log'
$clientLog = Join-Path $clientDir 'BepInEx\LogOutput.log'
$timeoutSec = 200

if (-not (Test-Path $hostExe)) { Write-Output "ERR: host exe not found at $hostExe"; exit 2 }
if (-not (Test-Path $clientExe)) { Write-Output "ERR: client exe not found at $clientExe"; exit 2 }
if (-not (Test-Path (Join-Path $clientDir 'SupermarketSimulatorClient_Data'))) {
    Write-Output 'ERR: client _Data folder must be renamed to SupermarketSimulatorClient_Data'; exit 2
}

function Reset-Cfg($dir) {
    $cfg = Join-Path $dir 'BepInEx\config\dev.codex.supermarket.moreplayers.cfg'
    if (Test-Path $cfg) {
        $c = Get-Content -LiteralPath $cfg -Raw
        $c = $c -replace 'SelfTestPhotonRealloc\s*=\s*\w+', 'SelfTestPhotonRealloc = false'
        $c = $c -replace 'ForceMinPlayerInstances\s*=\s*\d+', "ForceMinPlayerInstances = $Players"
        Set-Content -LiteralPath $cfg -Value $c -Encoding UTF8
    }
}

for ($r = 1; $r -le $Loop; $r++) {
    Write-Output "`n=== TWO-PLAYER run $r/$Loop (players=$Players) ==="
    $token = 'smp2_' + -join ((48..57)+(97..102) | Get-Random -Count 10 | % {[char]$_})

    Get-Process -Name 'Supermarket*' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2

    Reset-Cfg $hostGame
    Reset-Cfg $clientDir
    Remove-Item -LiteralPath $hostLog, $clientLog -Force -ErrorAction SilentlyContinue

    $hst = Start-Process -FilePath $hostExe -ArgumentList @('-autotest', "-test-role=host", "-test-room=$token", "-test-players=$Players") -WorkingDirectory $hostGame -PassThru
    Write-Output "host pid=$($hst.Id) room=$token"
    Start-Sleep -Seconds 20   # let host connect + open the room

    $cli = Start-Process -FilePath $clientExe -ArgumentList @('-autotest', '-test-role=client', "-test-room=$token") -WorkingDirectory $clientDir -PassThru
    Write-Output "client pid=$($cli.Id) joining $token"

    # Wait up to timeout for BOTH a MATRIX verdict (host) and a CLIENT verdict (client).
    $hVerdict=$null; $cVerdict=$null
    $hLast=0; $cLast=0
    $watch=[System.Diagnostics.Stopwatch]::StartNew()
    while ($watch.Elapsed.TotalSeconds -lt $timeoutSec -and (-not $hVerdict -or -not $cVerdict)) {
        Start-Sleep -Milliseconds 800
        if (-not $hVerdict -and (Test-Path $hostLog)) {
            $n = (Get-Item $hostLog).Length
            if ($n -gt $hLast) {
                $fs=[System.IO.File]::Open($hostLog,'Open','Read','ReadWrite'); try { $fs.Position=$hLast; $b=New-Object byte[] ($fs.Length-$fs.Position); $fs.Read($b,0,$b.Length)|Out-Null; $t=[System.Text.Encoding]::UTF8.GetString($b) } finally { $fs.Close() }
                $hLast = (Get-Item $hostLog).Length
                if ($t -match 'AUTOTEST-M: MATRIX\s+(?<chk>POOL=\S+ DOUBLE=\S+ UNIQUE=\S+(?: \S+)*)') { $hVerdict = $Matches.chk }
            }
        }
        if (-not $cVerdict -and (Test-Path $clientLog)) {
            $n = (Get-Item $clientLog).Length
            if ($n -gt $cLast) {
                $fs=[System.IO.File]::Open($clientLog,'Open','Read','ReadWrite'); try { $fs.Position=$cLast; $b=New-Object byte[] ($fs.Length-$fs.Position); $fs.Read($b,0,$b.Length)|Out-Null; $t=[System.Text.Encoding]::UTF8.GetString($b) } finally { $fs.Close() }
                $cLast = (Get-Item $clientLog).Length
                if ($t -match 'AUTOTEST-C: CLIENT\s+(?<chk>JOINED=\S+ LOCAL_BODY=\S+(?:[^\s].*?)? FRESH_VIEW=\S+)') { $cVerdict = $Matches.chk }
            }
        }
        if ($hst.HasExited -and $cli.HasExited) { break }
    }
    $watch.Stop()
    if (-not $hst.HasExited) { Stop-Process -Id $hst.Id -Force -ErrorAction SilentlyContinue }
    if (-not $cli.HasExited) { Stop-Process -Id $cli.Id -Force -ErrorAction SilentlyContinue }

    Write-Output "HOST  : $hVerdict"
    Write-Output "CLIENT: $cVerdict"

    if (-not $hVerdict) { Write-Output '  HOST INCONCLUSIVE (no MATRIX verdict)' }
    if (-not $cVerdict) { Write-Output '  CLIENT INCONCLUSIVE (no CLIENT verdict)' }

    # Cross-check: host UNIQUE=PASS plus client FRESH_VIEW=PASS is the end-to-end signal.
    if ($hVerdict -match 'UNIQUE=PASS' -and $hVerdict -match 'DRIVE=PASS' -and $cVerdict -match 'JOINED=PASS' -and $cVerdict -match 'LOCAL_BODY=PASS' -and $cVerdict -match 'FRESH_VIEW=PASS') {        Write-Output "TWO-PLAYER RESULT: PASS"
    } else {
        Write-Output "TWO-PLAYER RESULT: FAIL (one or more checks not all-PASS)"
    }
}
exit 0