# Convenience build wrapper. Assumes a .NET 6 SDK on PATH (or set DOTNET6 below
# to a specific SDK, e.g. the fresh SDK used during development).
$ErrorActionPreference = 'Stop'

$DOTNET6 = if ($null -ne $env:DOTNET6) { $env:DOTNET6 } else { 'dotnet' }

Push-Location (Split-Path -Parent $MyInvocation.MyInvocation.InvocationPath)
try {
	& $DOTNET6 build -c Release
	if ($LASTEXITCODE -ne 0) { throw "build failed with exit code $LASTEXITCODE" }
	$out = Join-Path (Get-Location) 'bin\Release\net6.0\SupermarketMorePlayers.dll'
	Write-Output "OK: $out"
} finally {
	Pop-Location
}