#requires -Version 5.1
[CmdletBinding()]
param([switch]$Publish, [switch]$SkipTests, [switch]$Offline)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$localDotnet = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
$dotnet = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { (Get-Command dotnet -ErrorAction Stop).Source }
$savedEnvironment = @{}
foreach ($key in @('DOTNET_CLI_HOME','APPDATA','LOCALAPPDATA','DOTNET_CLI_TELEMETRY_OPTOUT','DOTNET_GENERATE_ASPNET_CERTIFICATE')) {
    $savedEnvironment[$key] = [Environment]::GetEnvironmentVariable($key, 'Process')
}
Push-Location -LiteralPath $projectRoot
try {
    $env:DOTNET_CLI_HOME = Join-Path $projectRoot '.tools\cli'
    $env:APPDATA = Join-Path $projectRoot '.tools\appdata'
    $env:LOCALAPPDATA = Join-Path $projectRoot '.tools\localappdata'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
    $config = Join-Path $projectRoot 'NuGet.Config'
    $app = Join-Path $projectRoot 'src\AppleMusicLyrics.App\AppleMusicLyrics.App.csproj'
    [xml]$appProject = Get-Content -Raw -LiteralPath $app
    $version = [string]$appProject.Project.PropertyGroup.Version
    if ($version -notmatch '^\d+\.\d+\.\d+$' -or $version -eq '0.1.0') { throw 'Refusing an invalid or preserved release publish target.' }
    $probe = Join-Path $projectRoot 'src\AppleMusicLyrics.Probe\AppleMusicLyrics.Probe.csproj'
    $tests = Join-Path $projectRoot 'tests\AppleMusicLyrics.Core.Tests\AppleMusicLyrics.Core.Tests.csproj'
    # Offline mode uses already cached packages; a network vulnerability audit is not possible.
    $restoreOptions = @()
    if ($Offline) { $restoreOptions = @('--ignore-failed-sources','-p:NuGetAudit=false') }
    if (-not $SkipTests) {
        & $dotnet restore $tests --configfile $config @restoreOptions --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Test dependency restore failed.' }
        & $dotnet test $tests -c Release --no-restore --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Core tests failed.' }
    }
    $selfContained = if ($Publish) { 'true' } else { 'false' }
    foreach ($project in @($app, $probe)) {
        & $dotnet restore $project --configfile $config -r win-x64 "-p:SelfContained=$selfContained" @restoreOptions --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Windows project restore failed.' }
        if ($Publish) {
            $output = Join-Path $projectRoot ("artifacts\apple-music-desktop-lyrics-$version-win-x64")
            # Both apps share the same official .NET runtime files in one portable directory.
            & $dotnet publish $project -c Release -r win-x64 --no-restore --self-contained true -o $output -p:PublishTrimmed=false -p:PublishReadyToRun=false --nologo
            if ($LASTEXITCODE -ne 0) { throw 'Self-contained publish failed.' }
        }
        else {
            & $dotnet build $project -c Release --no-restore --nologo
            if ($LASTEXITCODE -ne 0) { throw 'Windows project build failed.' }
        }
    }
    if ($Publish) {
        foreach ($name in @('README.md','README.zh-CN.md','Run-Portable.cmd','Run-Portable-zh-CN.cmd','Run-Probe.cmd')) { Copy-Item -LiteralPath (Join-Path $projectRoot $name) -Destination $output -Force }
        $scriptOutput = Join-Path $output 'scripts'
        $docsOutput = Join-Path $output 'docs'
        $null = New-Item -ItemType Directory -Path $scriptOutput,$docsOutput -Force
        foreach ($name in @('Discover-AppleMusic.ps1','Run-Probe.ps1','Summarize-Probe.ps1','Test-Probe.ps1')) { Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $scriptOutput -Force }
        Get-ChildItem -LiteralPath (Join-Path $projectRoot 'docs') -File -Filter '*.md' | Copy-Item -Destination $docsOutput -Force
        # Both language editions use identical binaries. Copy only distributable
        # files so a developer's local portable profile never enters the other edition.
        $chineseOutput = Join-Path $projectRoot ("artifacts\apple-music-desktop-lyrics-$version-win-x64-zh-CN")
        $null = New-Item -ItemType Directory -Path $chineseOutput -Force
        foreach ($file in Get-ChildItem -LiteralPath $output -File -Recurse -Force) {
            $relative = $file.FullName.Substring($output.Length + 1)
            if ($relative -match '^(data|diagnostics)[\\/]' -or $relative -match '\.(log|tmp|lnk)$' -or
                $relative -eq 'ui-language.txt' -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { continue }
            $destination = Join-Path $chineseOutput $relative
            $null = New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force
            Copy-Item -LiteralPath $file.FullName -Destination $destination -Force
        }
        Copy-Item -LiteralPath (Join-Path $projectRoot 'Run-Portable-zh-CN.cmd') -Destination (Join-Path $chineseOutput 'Run-Portable.cmd') -Force
        # A non-personal default marker also makes a direct EXE launch Chinese.
        'zh-CN' | Set-Content -LiteralPath (Join-Path $chineseOutput 'ui-language.txt') -Encoding ASCII
        Write-Host "English portable application + diagnostic: $output"
        Write-Host "Chinese portable application + diagnostic: $chineseOutput"
    }
}
finally {
    Pop-Location
    foreach ($key in $savedEnvironment.Keys) { [Environment]::SetEnvironmentVariable($key, $savedEnvironment[$key], 'Process') }
}
