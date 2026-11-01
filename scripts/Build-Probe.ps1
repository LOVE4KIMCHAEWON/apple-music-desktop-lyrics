#requires -Version 5.1
[CmdletBinding()]
param([switch]$Publish)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$localDotnet = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
$dotnet = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { (Get-Command dotnet -ErrorAction Stop).Source }
$savedEnvironment = @{}
foreach ($key in @('DOTNET_CLI_HOME', 'APPDATA', 'LOCALAPPDATA', 'DOTNET_CLI_TELEMETRY_OPTOUT', 'DOTNET_GENERATE_ASPNET_CERTIFICATE')) {
    $savedEnvironment[$key] = [Environment]::GetEnvironmentVariable($key, 'Process')
}
Push-Location -LiteralPath $projectRoot
try {
    # Keep build caches in the workspace. Do not change global SDKs or user settings.
    $env:DOTNET_CLI_HOME = Join-Path $projectRoot '.tools\cli'
    $env:APPDATA = Join-Path $projectRoot '.tools\appdata'
    $env:LOCALAPPDATA = Join-Path $projectRoot '.tools\localappdata'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
    $project = Join-Path $projectRoot 'src\AppleMusicLyrics.Probe\AppleMusicLyrics.Probe.csproj'
    $selfContained = if ($Publish) { 'true' } else { 'false' }
    & $dotnet restore $project --configfile (Join-Path $projectRoot 'NuGet.Config') "-p:SelfContained=$selfContained" --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Probe restore failed.' }
    if ($Publish) {
        $output = Join-Path $projectRoot 'artifacts\probe-win-x64'
        & $dotnet publish $project -c Release --no-restore --self-contained true -o $output -p:PublishTrimmed=false -p:PublishReadyToRun=false --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Probe publish failed.' }
        $scriptOutput = Join-Path $output 'scripts'
        $null = New-Item -ItemType Directory -Path $scriptOutput -Force
        foreach ($name in @('Discover-AppleMusic.ps1', 'Run-Probe.ps1', 'Summarize-Probe.ps1', 'Test-Probe.ps1')) {
            $source = Join-Path $PSScriptRoot $name
            if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination $scriptOutput -Force }
        }
        Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination $output -Force
        Copy-Item -LiteralPath (Join-Path $projectRoot 'Run-Probe.cmd') -Destination $output -Force
        $findings = Join-Path $projectRoot 'docs\PHASE1-FINDINGS.md'
        if (Test-Path -LiteralPath $findings) {
            $docsOutput = Join-Path $output 'docs'
            $null = New-Item -ItemType Directory -Path $docsOutput -Force
            Copy-Item -LiteralPath $findings -Destination $docsOutput -Force
        }
        Write-Host "Portable x64 diagnostic: $output"
    }
    else {
        & $dotnet build $project -c Release --no-restore --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Probe build failed.' }
    }
}
finally {
    Pop-Location
    foreach ($key in $savedEnvironment.Keys) { [Environment]::SetEnvironmentVariable($key, $savedEnvironment[$key], 'Process') }
}
