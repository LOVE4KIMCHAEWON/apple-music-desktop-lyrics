#requires -Version 5.1
<#
.SYNOPSIS
Summarizes observed Phase 1 GSMTC evidence without assigning a reliability PASS.
.DESCRIPTION
Reads the probe's type/utc/monotonicMs/data JSONL records. OutputPath writes
Markdown; JsonOutputPath optionally writes structured evidence. Neither is required.
Seek jumps are candidates only, even when a playback command was accepted.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$LogPath,
    [string]$OutputPath,
    [string]$JsonOutputPath
)

$ErrorActionPreference = 'Stop'
$inputPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($LogPath)
if (-not (Test-Path -LiteralPath $inputPath -PathType Leaf)) { throw "Probe log does not exist: $inputPath" }

function Get-TrackKey {
    param($Snapshot)
    return ConvertTo-Json -InputObject @([string]$Snapshot.title, [string]$Snapshot.artist, [string]$Snapshot.album) -Compress
}

function Test-HasTrackMetadata {
    param($Snapshot)
    return -not [string]::IsNullOrWhiteSpace([string]$Snapshot.title) -and -not [string]::IsNullOrWhiteSpace([string]$Snapshot.artist)
}

function Get-DateValue {
    param($Value)
    $parsed = [DateTimeOffset]::MinValue
    if ($null -ne $Value -and [DateTimeOffset]::TryParse([string]$Value, [ref]$parsed)) { return $parsed }
    return $null
}

function Get-NumericRange {
    param([object[]]$Values)
    $present = @($Values | Where-Object { $null -ne $_ } | ForEach-Object { [double]$_ })
    if ($present.Count -eq 0) { return $null }
    $range = $present | Measure-Object -Minimum -Maximum
    return [pscustomobject]@{ minimum = $range.Minimum; maximum = $range.Maximum }
}

function Get-EventCounts {
    param([object[]]$Entries)
    $counts = [ordered]@{}
    foreach ($group in @($Entries | Where-Object { $_.record.type -eq 'event' } | Group-Object { [string]$_.record.data.reason } | Sort-Object Name)) {
        $counts[$group.Name] = $group.Count
    }
    return $counts
}

function Add-PauseSegment {
    param($Segment, $Segments)
    if ($null -ne $Segment) {
        $Segments.Add([pscustomobject]@{
            sessionId = $Segment.sessionId
            durationSeconds = ($Segment.endMs - $Segment.startMs) / 1000.0
            snapshots = $Segment.samples
            minimumPositionSeconds = $Segment.minimum
            maximumPositionSeconds = $Segment.maximum
            spreadSeconds = $Segment.maximum - $Segment.minimum
            stable = ($Segment.samples -ge 2 -and ($Segment.endMs - $Segment.startMs) -ge 2000 -and ($Segment.maximum - $Segment.minimum) -le 0.25)
        })
    }
}

function Format-Number {
    param($Value)
    if ($null -eq $Value) { return 'unavailable' }
    return ([double]$Value).ToString('0.###', [Globalization.CultureInfo]::InvariantCulture)
}

function Format-Range {
    param($Range)
    if ($null -eq $Range) { return 'unavailable' }
    return (Format-Number $Range.minimum) + ' to ' + (Format-Number $Range.maximum) + ' s'
}

function Escape-Markdown {
    param($Value)
    if ([string]::IsNullOrWhiteSpace([string]$Value)) { return '(empty)' }
    return ([string]$Value).Replace('|', '\|').Replace('`', '\`') -replace '[\r\n]+', ' '
}

function Write-ReportFile {
    param([string]$Path, [string]$Content)
    $fullPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Path)
    if ([string]::Equals($inputPath, $fullPath, [StringComparison]::OrdinalIgnoreCase)) { throw 'Output must not overwrite the input log.' }
    $directory = Split-Path -Parent $fullPath
    if (-not (Test-Path -LiteralPath $directory -PathType Container)) { $null = New-Item -ItemType Directory -Path $directory -Force }
    [IO.File]::WriteAllText($fullPath, $Content + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
}

$entries = [Collections.Generic.List[object]]::new()
$errors = [Collections.Generic.List[object]]::new()
$lineNumber = 0
foreach ($line in [IO.File]::ReadLines($inputPath)) {
    $lineNumber++
    if ([string]::IsNullOrWhiteSpace($line)) { continue }
    try {
        $record = ConvertFrom-Json -InputObject $line -ErrorAction Stop
        if ($null -eq $record.type -or $null -eq $record.data) { throw 'Missing type or data.' }
        $entries.Add([pscustomobject]@{ line = $lineNumber; record = $record })
    }
    catch { $errors.Add([pscustomobject]@{ line = $lineNumber; type = 'invalidJsonRecord'; detail = $_.Exception.Message }) }
}

$environmentEntry = @($entries | Where-Object { $_.record.type -eq 'environment' } | Select-Object -First 1)
$environment = $null
if ($environmentEntry.Count -gt 0) { $environment = $environmentEntry[0].record.data }
$freshnessSeconds = 5.0
if ($null -ne $environment.sampleMs) { $freshnessSeconds = [Math]::Max(5.0, [double]$environment.sampleMs / 1000.0 * 3.0) }
$sourceIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$droppedEvents = 0L
foreach ($entry in $entries) {
    $record = $entry.record
    $data = $record.data
    if (-not [string]::IsNullOrWhiteSpace([string]$data.sourceAppUserModelId)) { $null = $sourceIds.Add([string]$data.sourceAppUserModelId) }
    if ($record.type -eq 'summary' -and $null -ne $data.droppedEvents) { $droppedEvents = [Math]::Max($droppedEvents, [long]$data.droppedEvents) }
    if ($record.type -match '(?i)(error|fatal)$') {
        $errors.Add([pscustomobject]@{ line = $entry.line; type = $record.type; detail = (ConvertTo-Json -InputObject $data -Compress -Depth 10) })
    }
    foreach ($errorItem in @($data.errors)) {
        if ($null -ne $errorItem) { $errors.Add([pscustomobject]@{ line = $entry.line; type = $record.type; detail = (ConvertTo-Json -InputObject $errorItem -Compress -Depth 10) }) }
    }
    if ($record.type -eq 'event' -and $data.reason -eq 'exercise' -and [string]$data.payload.step -match '(?i)error$') {
        $errors.Add([pscustomobject]@{ line = $entry.line; type = 'exercise'; detail = (ConvertTo-Json -InputObject $data.payload -Compress -Depth 10) })
    }
}

$sources = [Collections.Generic.List[object]]::new()
foreach ($sourceId in @($sourceIds | Sort-Object)) {
    $sourceEntries = @($entries | Where-Object { [string]::Equals([string]$_.record.data.sourceAppUserModelId, $sourceId, [StringComparison]::OrdinalIgnoreCase) })
    $snapshots = @($sourceEntries | Where-Object { $_.record.type -eq 'snapshot' } | ForEach-Object { $_.record.data } | Sort-Object { [double]$_.readMonotonicMs })
    $identityReasons = @($sourceEntries | ForEach-Object { $_.record.data.identityReason } | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) } | Sort-Object -Unique)
    $isApple = @($sourceEntries | Where-Object { $_.record.data.isApple -eq $true }).Count -gt 0
    $tracks = [Collections.Generic.List[object]]::new()
    foreach ($group in @($snapshots | Group-Object { Get-TrackKey $_ })) {
        $first = $group.Group[0]
        $tracks.Add([pscustomobject]@{
            title = $first.title
            artist = $first.artist
            album = $first.album
            durationSeconds = @($group.Group | ForEach-Object { $_.durationSeconds } | Where-Object { $null -ne $_ } | Sort-Object -Unique)
            rawPositionRange = Get-NumericRange @($group.Group | ForEach-Object { $_.timeline.positionSeconds })
            snapshots = $group.Count
        })
    }

    $pauseSegments = [Collections.Generic.List[object]]::new()
    $seekCandidates = [Collections.Generic.List[object]]::new()
    $timelineAdvances = 0
    $pauseTransitions = 0
    $resumeTransitions = 0
    $trackChanges = 0
    foreach ($sessionGroup in @($snapshots | Group-Object sessionId)) {
        $previous = $null
        $lastFresh = $null
        $pause = $null
        $lastTransitionMs = $null
        foreach ($snapshot in $sessionGroup.Group) {
            $key = Get-TrackKey $snapshot
            $sameTrack = $null -ne $previous -and (Get-TrackKey $previous) -ceq $key
            if ($null -eq $previous -or -not $sameTrack -or $previous.playbackStatus -ne $snapshot.playbackStatus) { $lastTransitionMs = [double]$snapshot.readMonotonicMs }
            if ($null -ne $previous -and -not $sameTrack -and (Test-HasTrackMetadata $previous) -and (Test-HasTrackMetadata $snapshot)) { $trackChanges++ }
            if ($sameTrack -and $previous.playbackStatus -eq 'Playing' -and $snapshot.playbackStatus -eq 'Paused') { $pauseTransitions++ }
            if ($sameTrack -and $previous.playbackStatus -eq 'Paused' -and $snapshot.playbackStatus -eq 'Playing') { $resumeTransitions++ }

            $validPause = $snapshot.playbackStatus -eq 'Paused' -and $snapshot.timeline.usable -eq $true -and $null -ne $snapshot.timeline.positionSeconds
            if ($validPause) {
                $position = [double]$snapshot.timeline.positionSeconds
                # A final timeline correction can arrive just after the pause event.
                # Preserve each constant-position interval instead of letting that
                # initial correction hide a later, directly observed stable pause.
                if ($null -ne $pause -and ($pause.trackKey -cne $key -or [Math]::Max($pause.maximum, $position) - [Math]::Min($pause.minimum, $position) -gt 0.25)) { Add-PauseSegment $pause $pauseSegments; $pause = $null }
                if ($null -eq $pause) {
                    $pause = [pscustomobject]@{ sessionId = $snapshot.sessionId; trackKey = $key; startMs = [double]$snapshot.readMonotonicMs; endMs = [double]$snapshot.readMonotonicMs; minimum = $position; maximum = $position; samples = 1 }
                }
                else {
                    $pause.endMs = [double]$snapshot.readMonotonicMs
                    $pause.minimum = [Math]::Min($pause.minimum, $position)
                    $pause.maximum = [Math]::Max($pause.maximum, $position)
                    $pause.samples++
                }
            }
            elseif ($null -ne $pause) { Add-PauseSegment $pause $pauseSegments; $pause = $null }

            $updated = Get-DateValue $snapshot.timeline.lastUpdatedTime
            $age = $snapshot.timeline.updateAgeSeconds
            $fresh = $snapshot.timeline.usable -eq $true -and $null -ne $updated -and $updated -gt [DateTimeOffset]'1970-01-01T00:00:00Z' -and $null -ne $age -and [double]$age -ge -1 -and [double]$age -le $freshnessSeconds
            if ($fresh) {
                $stable = $null -ne $lastFresh -and $sameTrack -and $lastFresh.trackKey -ceq $key -and $lastFresh.snapshot.playbackStatus -eq $snapshot.playbackStatus -and $snapshot.playbackStatus -in @('Playing', 'Paused')
                if ($stable -and $updated -gt $lastFresh.updated) {
                    $old = $lastFresh.snapshot
                    $rawDelta = [double]$snapshot.timeline.positionSeconds - [double]$old.timeline.positionSeconds
                    $elapsed = ([double]$snapshot.readMonotonicMs - [double]$old.readMonotonicMs) / 1000.0
                    $providerElapsed = $elapsed - [double]$age + [double]$old.timeline.updateAgeSeconds
                    $rate = 1.0
                    if ($null -ne $old.playbackRate) { $rate = [double]$old.playbackRate }
                    $expected = 0.0
                    if ($snapshot.playbackStatus -eq 'Playing') { $expected = [Math]::Max(0.0, $providerElapsed) * $rate }
                    $residual = $rawDelta - $expected
                    $sameRate = $null -eq $snapshot.playbackRate -or $null -eq $old.playbackRate -or [Math]::Abs([double]$snapshot.playbackRate - [double]$old.playbackRate) -lt 0.000001
                    if ($elapsed -gt 0 -and $sameRate) {
                        $settled = ([double]$snapshot.readMonotonicMs - $lastTransitionMs) -ge 2000 -and ([double]$old.readMonotonicMs - $lastTransitionMs) -ge 2000
                        if ([Math]::Abs($residual) -ge 2.0 -and $settled) {
                            $seekCandidates.Add([pscustomobject]@{ sessionId = $snapshot.sessionId; readMonotonicMs = $snapshot.readMonotonicMs; title = $snapshot.title; playbackStatus = $snapshot.playbackStatus; fromSeconds = $old.timeline.positionSeconds; toSeconds = $snapshot.timeline.positionSeconds; residualSeconds = $residual })
                        }
                        elseif ([Math]::Abs($residual) -lt 2.0 -and $snapshot.playbackStatus -eq 'Playing' -and $rawDelta -gt 0.1) { $timelineAdvances++ }
                    }
                }
                if (-not $stable -or $updated -ne $lastFresh.updated) { $lastFresh = [pscustomobject]@{ snapshot = $snapshot; updated = $updated; trackKey = $key } }
            }
            else { $lastFresh = $null }
            $previous = $snapshot
        }
        Add-PauseSegment $pause $pauseSegments
    }

    $sources.Add([pscustomobject]@{
        sourceAppUserModelId = $sourceId
        isApple = $isApple
        identityReasons = $identityReasons
        sessionIds = @($sourceEntries | ForEach-Object { $_.record.data.sessionId } | Where-Object { $null -ne $_ } | Sort-Object -Unique)
        snapshots = $snapshots.Count
        usableMetadataAndTimelineSnapshots = @($snapshots | Where-Object { $_.timeline.usable -eq $true -and (Test-HasTrackMetadata $_) }).Count
        playbackStates = @($snapshots | ForEach-Object { $_.playbackStatus } | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) } | Sort-Object -Unique)
        tracks = @($tracks.ToArray())
        rawPositionRange = Get-NumericRange @($snapshots | ForEach-Object { $_.timeline.positionSeconds })
        rawStartRange = Get-NumericRange @($snapshots | ForEach-Object { $_.timeline.startSeconds })
        rawEndRange = Get-NumericRange @($snapshots | ForEach-Object { $_.timeline.endSeconds })
        events = Get-EventCounts $sourceEntries
        timelineAdvances = $timelineAdvances
        pauseTransitions = $pauseTransitions
        resumeTransitions = $resumeTransitions
        pauseSegments = @($pauseSegments.ToArray())
        possibleSeekJumps = @($seekCandidates.ToArray())
        trackMetadataChanges = $trackChanges
        sessionAddedCount = @($sourceEntries | Where-Object { $_.record.type -eq 'sessionAdded' }).Count
        sessionRemovedCount = @($sourceEntries | Where-Object { $_.record.type -eq 'sessionRemoved' }).Count
        exerciseCommands = @($sourceEntries | Where-Object { $_.record.type -eq 'event' -and $_.record.data.reason -eq 'exercise' } | ForEach-Object { $_.record.data.payload })
    })
}

$appleSources = @($sources | Where-Object isApple)
$usableApple = 0
foreach ($source in $appleSources) { $usableApple += $source.usableMetadataAndTimelineSnapshots }
$summary = [ordered]@{
    logPath = $inputPath
    validRecords = $entries.Count
    environment = $environment
    feasibilityData = $(if ($usableApple -gt 0) { 'Observed Apple Music metadata and usable timeline.' } else { 'No Apple Music metadata with usable timeline observed.' })
    reliabilityVerdict = 'Not automatically assigned. Commands and snapshots alone do not verify reliability.'
    freshTimelineMaximumAgeSeconds = $freshnessSeconds
    globalEvents = Get-EventCounts @($entries.ToArray())
    droppedEvents = $droppedEvents
    sources = @($sources.ToArray())
    errors = @($errors.ToArray())
}

$lines = [Collections.Generic.List[string]]::new()
$lines.Add('# Phase 1 probe findings')
$lines.Add('')
$lines.Add('Log: ' + (Escape-Markdown $inputPath))
if ($null -ne $environment) { $lines.Add(('Environment: {0}; .NET {1}; {2}; read-only: {3}.' -f $environment.os, $environment.runtime, $environment.architecture, $environment.readOnly)) }
$lines.Add($summary.feasibilityData)
$lines.Add('Reliability has no automatic PASS verdict. Accepted commands are recorded separately from observed state changes.')
$lines.Add('')
$lines.Add(('Valid records: {0}; errors: {1}; dropped events: {2}.' -f $entries.Count, $errors.Count, $droppedEvents))
$globalCounts = @($summary.globalEvents.GetEnumerator() | ForEach-Object { $_.Key + '=' + $_.Value })
$lines.Add('All event counts: ' + $(if ($globalCounts.Count -gt 0) { $globalCounts -join ', ' } else { 'none' }) + '.')

foreach ($source in $sources) {
    $lines.Add('')
    $lines.Add('## ' + (Escape-Markdown $source.sourceAppUserModelId))
    $lines.Add('')
    $lines.Add(('Apple Music: {0}; identity evidence: {1}.' -f $source.isApple, ($source.identityReasons -join '; ')))
    if ($source.identityReasons -contains 'explicit-user-override') { $lines.Add('An explicit user override is not independent installed-app verification.') }
    $lines.Add(('Sessions: {0}; snapshots: {1}; metadata + usable timeline: {2}; playback states: {3}.' -f ($source.sessionIds -join ', '), $source.snapshots, $source.usableMetadataAndTimelineSnapshots, ($source.playbackStates -join ', ')))
    $lines.Add(('Raw timeline: position {0}; start {1}; end {2}.' -f (Format-Range $source.rawPositionRange), (Format-Range $source.rawStartRange), (Format-Range $source.rawEndRange)))
    $counts = @($source.events.GetEnumerator() | ForEach-Object { $_.Key + '=' + $_.Value })
    $lines.Add('Source events: ' + $(if ($counts.Count -gt 0) { $counts -join ', ' } else { 'none' }) + '.')
    $lines.Add('')
    $lines.Add('| Track title | Reported artist | Reported album | Duration (s) | Raw position range |')
    $lines.Add('| --- | --- | --- | --- | --- |')
    foreach ($track in $source.tracks) {
        $durations = @($track.durationSeconds | ForEach-Object { Format-Number $_ })
        $durationText = $(if ($durations.Count -gt 0) { $durations -join ', ' } else { 'unavailable' })
        $lines.Add(('| {0} | {1} | {2} | {3} | {4} |' -f (Escape-Markdown $track.title), (Escape-Markdown $track.artist), (Escape-Markdown $track.album), $durationText, (Format-Range $track.rawPositionRange)))
    }
    $lines.Add('')
    $lines.Add(('Observed: {0} ordinary playing timeline advances; {1} Playing-to-Paused transitions; {2} Paused-to-Playing transitions; {3} track metadata changes.' -f $source.timelineAdvances, $source.pauseTransitions, $source.resumeTransitions, $source.trackMetadataChanges))
    $stablePauses = @($source.pauseSegments | Where-Object stable)
    if ($stablePauses.Count -gt 0) {
        $longest = $stablePauses | Sort-Object durationSeconds -Descending | Select-Object -First 1
        $lines.Add(('Paused reported position stability observed for {0} s; spread {1} s across {2} snapshots (longest qualifying interval).' -f (Format-Number $longest.durationSeconds), (Format-Number $longest.spreadSeconds), $longest.snapshots))
    }
    elseif ($source.pauseSegments.Count -gt 0) { $lines.Add('Usable paused readings were observed, but no stable reported-position interval met the 2 s / <= 0.25 s spread criterion.') }
    else { $lines.Add('Paused position stability is untested: no usable paused readings were recorded.') }
    if ($source.possibleSeekJumps.Count -gt 0) {
        $lines.Add(('Possible seek jumps: {0}. These are candidates requiring manual confirmation; provider corrections can also cause jumps.' -f $source.possibleSeekJumps.Count))
        foreach ($jump in $source.possibleSeekJumps) { $lines.Add(('- Session {0}, at {1} monotonic ms: {2} to {3} s; residual {4} s; {5}.' -f $jump.sessionId, (Format-Number $jump.readMonotonicMs), (Format-Number $jump.fromSeconds), (Format-Number $jump.toSeconds), (Format-Number $jump.residualSeconds), $jump.playbackStatus)) }
    }
    elseif ($source.isApple) { $lines.Add('Seek behavior is untested: no qualifying jump observed. Seek forward and backward in Apple Music while capturing another log.') }
    else { $lines.Add('No qualifying seek jump observed in this excluded source.') }
    if ($source.isApple -and ($source.pauseTransitions -eq 0 -or $source.resumeTransitions -eq 0)) { $lines.Add('Pause/resume detection remains incomplete; capture both transitions on the same track.') }
    if ($source.isApple -and $source.trackMetadataChanges -eq 0) { $lines.Add('Song-change detection is untested; capture next and previous track actions.') }
    if ($source.isApple -and ($source.sessionAddedCount -lt 2 -or $source.sessionRemovedCount -eq 0)) { $lines.Add('Session reconnection is untested; capture Apple Music closing and reopening.') }
    elseif ($source.isApple) { $lines.Add('Session removal and multiple additions were observed; confirm the reconnection action manually.') }
    if ($source.exerciseCommands.Count -gt 0) { $lines.Add('Exercise records (commands are not success evidence): ' + (ConvertTo-Json -InputObject @($source.exerciseCommands) -Compress -Depth 10)) }
}

if ($sources.Count -eq 0) { $lines.Add('No media sources were recorded. Launch Apple Music, play a track, and capture again.') }
$lines.Add('')
$lines.Add(('Jump analysis uses the same session, track, stable playback status/rate, usable timeline, changed provider timestamp, and update age <= {0} s. Both compared readings must be at least 2 s after a track/status transition; reconnect boundaries are excluded.' -f (Format-Number $freshnessSeconds)))
$lines.Add('These logs do not establish repeated-run reliability, audible lyric synchronization, or overlay behavior.')
if ($errors.Count -gt 0) {
    $lines.Add('')
    $lines.Add('## Errors')
    $lines.Add('')
    foreach ($item in $errors) { $lines.Add(('- Line {0}, {1}: {2}' -f $item.line, $item.type, (Escape-Markdown $item.detail))) }
}
$markdown = $lines -join [Environment]::NewLine
if (-not [string]::IsNullOrWhiteSpace($OutputPath)) { Write-ReportFile $OutputPath $markdown }
if (-not [string]::IsNullOrWhiteSpace($JsonOutputPath)) {
    if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
        $markdownPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputPath)
        $jsonPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($JsonOutputPath)
        if ([string]::Equals($markdownPath, $jsonPath, [StringComparison]::OrdinalIgnoreCase)) { throw 'Markdown and JSON outputs must use different paths.' }
    }
    Write-ReportFile $JsonOutputPath (ConvertTo-Json -InputObject $summary -Depth 20)
}
Write-Output $markdown
