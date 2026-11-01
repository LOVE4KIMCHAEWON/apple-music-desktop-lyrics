#requires -Version 5.1
[CmdletBinding()]
param([string]$ExePath)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($ExePath)) {
    $ExePath = Join-Path $projectRoot 'AppleMusicLyrics.Probe.exe'
    if (-not (Test-Path -LiteralPath $ExePath)) {
        $ExePath = Join-Path $projectRoot 'src\AppleMusicLyrics.Probe\bin\Release\net8.0-windows10.0.19041.0\win-x64\AppleMusicLyrics.Probe.exe'
    }
}
$ExePath = (Resolve-Path -LiteralPath $ExePath).Path
$testDirectory = Join-Path $projectRoot ('diagnostics\smoke-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $testDirectory -Force
$script:checks = 0
function Assert-True([bool]$Condition, [string]$Description) {
    if (-not $Condition) { throw "FAIL: $Description" }
    $script:checks++
    Write-Host "PASS: $Description"
}
foreach ($script in Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.ps1') {
    $tokens = $null; $parseErrors = $null
    $null = [System.Management.Automation.Language.Parser]::ParseFile($script.FullName, [ref]$tokens, [ref]$parseErrors)
    Assert-True ($parseErrors.Count -eq 0) ("PowerShell syntax: " + $script.Name)
}
$helpText = & $ExePath --help 2>&1
Assert-True ($LASTEXITCODE -eq 0 -and ($helpText -join "`n") -match 'sample-ms') 'CLI help'
# Native stderr is expected for invalid input. Capture it without PowerShell 5.1
# turning it into a terminating NativeCommandError.
$savedPreference = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
$null = & $ExePath --sample-ms 1 2>&1
$invalidSampleExit = $LASTEXITCODE
$null = & $ExePath --seconds -1 2>&1
$negativeDurationExit = $LASTEXITCODE
$null = & $ExePath --seconds 1 --exercise 2>&1
$shortExerciseExit = $LASTEXITCODE
$null = & $ExePath --unknown 2>&1
$unknownExit = $LASTEXITCODE
$ErrorActionPreference = $savedPreference
Assert-True ($invalidSampleExit -eq 1) 'Reject excessive diagnostic sampling'
Assert-True ($negativeDurationExit -eq 1) 'Reject invalid duration'
Assert-True ($shortExerciseExit -eq 1) 'Reject truncated playback exercise'
Assert-True ($unknownExit -eq 1) 'Reject unknown options'
$log = Join-Path $testDirectory 'capture.jsonl'
$null = & $ExePath --seconds 2 --output $log
$captureExit = $LASTEXITCODE
Assert-True ($captureExit -eq 0 -or $captureExit -eq 3) 'Read-only live capture completes, with or without Apple Music'
$rows = @(Get-Content -LiteralPath $log | ForEach-Object { ConvertFrom-Json -InputObject $_ })
Assert-True ($rows[0].type -eq 'environment' -and $rows[0].data.readOnly) 'Environment and read-only policy logged'
Assert-True ($rows[-1].type -eq 'summary' -and -not $rows[-1].data.reliabilityVerified) 'Summary does not claim untested reliability'
$previous = -1.0
foreach ($row in $rows) {
    Assert-True ($row.monotonicMs -ge $previous) ('Monotonic log order: ' + $row.type)
    $previous = $row.monotonicMs
}
$original = [IO.File]::ReadAllText($log)
$ErrorActionPreference = 'Continue'
$null = & $ExePath --seconds 1 --output $log 2>&1
$overwriteExit = $LASTEXITCODE
$ErrorActionPreference = $savedPreference
Assert-True ($overwriteExit -eq 1 -and [IO.File]::ReadAllText($log) -eq $original) 'Existing diagnostic evidence is never overwritten'
Write-Host ("Completed {0} smoke checks. Evidence: {1}" -f $script:checks, $testDirectory)
