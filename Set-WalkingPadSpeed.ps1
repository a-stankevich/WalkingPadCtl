<#
.SYNOPSIS
  Connects to a Kingsmith WalkingPad over BLE and sets its speed (0 stops the belt),
  or, without -Speed, reads and prints its current state and settings.
.PARAMETER Speed
  Speed in 0.1 km/h units, e.g. 35 = 3.5 km/h. 0 stops the belt. Omit to only read the state.
.PARAMETER Json
  Read mode only: return the state as an object (parsed from the app's JSON output).
.EXAMPLE
  ./Set-WalkingPadSpeed.ps1 -Speed 35
.EXAMPLE
  ./Set-WalkingPadSpeed.ps1 -Speed 0
.EXAMPLE
  ./Set-WalkingPadSpeed.ps1
.EXAMPLE
  (./Set-WalkingPadSpeed.ps1 -Json).speed
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateRange(0, 120)]
    [Nullable[int]]$Speed,
    [switch]$Json,
    [string]$Address,
    [switch]$Scan
)

$ErrorActionPreference = 'Stop'
$exe = Join-Path $PSScriptRoot 'bin\walkingpad.exe'
if (-not (Test-Path $exe)) {
    Write-Host 'Building walkingpad.exe (first run)...'
    dotnet publish (Join-Path $PSScriptRoot 'WalkingPadCtl.csproj') -c Release -o (Join-Path $PSScriptRoot 'bin') --nologo -v q | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
}

$argList = @()
if ($null -ne $Speed) { $argList += @('-Speed', $Speed) }
elseif ($Json) { $argList += '-Json' }
if ($Address) { $argList += @('-Address', $Address) }
if ($Scan) { $argList += '-Scan' }
if ($VerbosePreference -eq 'Continue') { $argList += '-Verbose' }

if ($null -eq $Speed -and $Json) {
    $out = & $exe @argList
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    $out | Out-String | ConvertFrom-Json
    return
}

& $exe @argList
exit $LASTEXITCODE
