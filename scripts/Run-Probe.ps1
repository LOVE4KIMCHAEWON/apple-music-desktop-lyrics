#requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateRange(0, 86400)][int]$Seconds = 60,
    [ValidateRange(250, 60000)][int]$SampleMs = 1000,
    [string]$OutputDirectory,
    [string]$AppleAumid,
    [switch]$Exercise
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$portableExe = Join-Path $projectRoot 'AppleMusicLyrics.Probe.exe'
$exe = if (Test-Path -LiteralPath $portableExe) { $portableExe } else {
    Join-Path $projectRoot 'src\AppleMusicLyrics.Probe\bin\Release\net8.0-windows10.0.19041.0\win-x64\AppleMusicLyrics.Probe.exe'
}
if (-not (Test-Path -LiteralPath $exe)) { throw 'Build the probe first with scripts\Build-Probe.ps1, or use the published portable directory.' }
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { $OutputDirectory = Join-Path $projectRoot 'diagnostics' }
$null = New-Item -ItemType Directory -Path $OutputDirectory -Force
$outputRoot = (Resolve-Path -LiteralPath $OutputDirectory).Path
$stamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff')
$discovery = Join-Path $outputRoot "apple-music-discovery-$stamp.json"
& (Join-Path $PSScriptRoot 'Discover-AppleMusic.ps1') -OutputPath $discovery
if (-not (Test-Path -LiteralPath $discovery)) { throw 'Installed-app discovery did not produce its report.' }
$log = Join-Path $outputRoot "probe-$stamp.jsonl"
$arguments = @('--seconds', "$Seconds", '--sample-ms', "$SampleMs", '--discovery', $discovery, '--output', $log)
if (-not [string]::IsNullOrWhiteSpace($AppleAumid)) { $arguments += @('--apple-aumid', $AppleAumid) }
if ($Exercise) { $arguments += '--exercise' }
& $exe @arguments
$probeExitCode = $LASTEXITCODE
$summarizer = Join-Path $PSScriptRoot 'Summarize-Probe.ps1'
if (Test-Path -LiteralPath $summarizer) { & $summarizer -LogPath $log -OutputPath (Join-Path $outputRoot "findings-$stamp.md") }
exit $probeExitCode
