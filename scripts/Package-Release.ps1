#requires -Version 5.1
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
[xml]$project = Get-Content -Raw -LiteralPath (Join-Path $projectRoot 'src\AppleMusicLyrics.App\AppleMusicLyrics.App.csproj')
$version = [string]$project.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$' -or $version -eq '0.1.0') { throw 'Expected a development release newer than preserved v0.1.0.' }
$artifacts = Join-Path $projectRoot 'artifacts'
$portable = Join-Path $artifacts ("apple-music-desktop-lyrics-$version-win-x64")
$portableZip = Join-Path $artifacts ("AppleMusicDesktopLyrics-$version-win-x64.zip")
$chinesePortable = Join-Path $artifacts ("apple-music-desktop-lyrics-$version-win-x64-zh-CN")
$chineseZip = Join-Path $artifacts ("AppleMusicDesktopLyrics-$version-win-x64-zh-CN.zip")
$sourceZip = Join-Path $artifacts ("AppleMusicDesktopLyrics-$version-source.zip")
$manifest = Join-Path $artifacts ("AppleMusicDesktopLyrics-$version-SHA256SUMS.txt")
foreach ($folder in @($portable,$chinesePortable)) {
    if (-not (Test-Path -LiteralPath (Join-Path $folder 'AppleMusicLyrics.exe'))) { throw "Publish both portable editions first. Missing application: $folder" }
}
$languageMarker = Join-Path $chinesePortable 'ui-language.txt'
if (-not (Test-Path -LiteralPath $languageMarker) -or (Get-Content -Raw -LiteralPath $languageMarker).Trim() -ne 'zh-CN') { throw 'Chinese portable edition is missing its zh-CN language default.' }
foreach ($path in @($portableZip,$chineseZip,$sourceZip,$manifest)) {
    if (Test-Path -LiteralPath $path) { throw "Refusing to overwrite an existing release archive: $path" }
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
foreach ($edition in @(@{ Folder = $portable; Archive = $portableZip }, @{ Folder = $chinesePortable; Archive = $chineseZip })) {
    $portableArchive = [IO.Compression.ZipFile]::Open($edition.Archive, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in Get-ChildItem -LiteralPath $edition.Folder -File -Recurse -Force) {
            $relative = $file.FullName.Substring($edition.Folder.Length + 1).Replace('\','/')
            # A developer/user may already have run the portable app. Personal
            # settings, caches and logs must never become part of a release archive.
            if ($relative -match '^(data|diagnostics)/' -or $relative -match '\.(log|tmp|lnk)$' -or
                ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { continue }
            $null = [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($portableArchive,$file.FullName,$relative,[IO.Compression.CompressionLevel]::Optimal)
        }
    }
    finally { $portableArchive.Dispose() }
}
$archive = [IO.Compression.ZipFile]::Open($sourceZip, [IO.Compression.ZipArchiveMode]::Create)
try {
    $files = @()
    foreach ($name in @('.gitignore','AppleMusicLyrics.sln','global.json','NuGet.Config','README.md','README.zh-CN.md','Run-Portable.cmd','Run-Portable-zh-CN.cmd','Run-Probe.cmd')) {
        $files += Get-Item -LiteralPath (Join-Path $projectRoot $name)
    }
    foreach ($name in @('src','tests','scripts','docs')) {
        $files += Get-ChildItem -LiteralPath (Join-Path $projectRoot $name) -File -Recurse -Force |
            Where-Object {
                # Match relative paths: the Windows account itself may be named "bin".
                $relativePath = $_.FullName.Substring($projectRoot.Length + 1)
                $relativePath -notmatch '[\\/](bin|obj)[\\/]' -and ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0
            }
    }
    foreach ($file in $files | Sort-Object FullName -Unique) {
        $relative = $file.FullName.Substring($projectRoot.Length + 1).Replace('\','/')
        $null = [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive,$file.FullName,$relative,[IO.Compression.CompressionLevel]::Optimal)
    }
}
finally { $archive.Dispose() }
$check = [IO.Compression.ZipFile]::OpenRead($sourceZip)
try {
    foreach ($required in @('src/AppleMusicLyrics.App/MainController.cs','src/AppleMusicLyrics.Core/AppleNativeLyricsProvider.cs','tests/AppleMusicLyrics.Core.Tests/NativeCacheProviderTests.cs','docs/VALIDATION.md')) {
        if (-not $check.GetEntry($required)) { throw "Source package is incomplete: $required" }
    }
}
finally { $check.Dispose() }
$hashes = foreach ($path in @($portableZip,$chineseZip,$sourceZip)) {
    $hash = Get-FileHash -LiteralPath $path -Algorithm SHA256
    "$($hash.Hash)  $([IO.Path]::GetFileName($path))"
}
$hashes | Set-Content -LiteralPath $manifest -Encoding UTF8
Write-Host "Portable: $portableZip"
Write-Host "Chinese portable: $chineseZip"
Write-Host "Source: $sourceZip"
Write-Host "Checksums: $manifest"
