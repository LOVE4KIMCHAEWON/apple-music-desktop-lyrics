#requires -Version 5.1
<#
.SYNOPSIS
Discovers current-user Apple Music application identities without a fixed package ID.
.DESCRIPTION
Reads the Start menu and installed package manifests. Only an exact Apple Music
display name or an AppleMusic package-name component qualifies. It does not start
Apple Music or modify its installation. The only write is the JSON report.
#>
[CmdletBinding()]
param(
    [string]$OutputPath = (Join-Path $PSScriptRoot '..\diagnostics\apple-music-discovery.json'),
    [switch]$UseCurrentHost
)

$ErrorActionPreference = 'Stop'
$reportPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputPath)

# The inbox Appx and StartLayout modules work reliably in Windows PowerShell 5.1.
# Re-enter that host from PowerShell 7 without requiring elevated permissions.
if (-not $UseCurrentHost -and $env:OS -eq 'Windows_NT' -and $PSVersionTable.PSEdition -eq 'Core') {
    $windowsPowerShell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    if (Test-Path -LiteralPath $windowsPowerShell -PathType Leaf) {
        & $windowsPowerShell -NoProfile -File $PSCommandPath -OutputPath $reportPath -UseCurrentHost
        exit $LASTEXITCODE
    }
}

function Test-AppleMusicDisplayName {
    param([AllowNull()][string]$Value)
    if ([string]::IsNullOrWhiteSpace($Value)) { return $false }
    $normalized = ($Value.Trim() -replace '\s+', ' ')
    return [string]::Equals($normalized, 'Apple Music', [StringComparison]::OrdinalIgnoreCase)
}

function Test-AppleMusicPackageName {
    param([AllowNull()][string]$Value)
    # Match a complete final component, never Apple TV, a browser, or an Apple prefix.
    return -not [string]::IsNullOrWhiteSpace($Value) -and $Value -match '(?i)(^|[._-])AppleMusic$'
}

$appsById = [System.Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
$discoveryErrors = [System.Collections.Generic.List[string]]::new()

function Add-DiscoveredApp {
    param(
        [string]$Aumid,
        [string]$DisplayName,
        [string]$Method,
        [AllowNull()][string]$PackageName,
        [AllowNull()][string]$PackageFamilyName,
        [AllowNull()][string]$Version
    )
    if ([string]::IsNullOrWhiteSpace($Aumid)) { return }

    if ($appsById.ContainsKey($Aumid)) {
        $existing = $appsById[$Aumid]
        if (($existing.discoveryMethod -split '\+') -notcontains $Method) {
            $existing.discoveryMethod += '+' + $Method
        }
        if (-not [string]::IsNullOrWhiteSpace($PackageName)) { $existing.packageName = $PackageName }
        if (-not [string]::IsNullOrWhiteSpace($PackageFamilyName)) { $existing.packageFamilyName = $PackageFamilyName }
        if (-not [string]::IsNullOrWhiteSpace($Version)) { $existing.version = $Version }
        return
    }

    $appsById.Add($Aumid, [pscustomobject][ordered]@{
        aumid = $Aumid
        displayName = $DisplayName
        discoveryMethod = $Method
        packageName = $PackageName
        packageFamilyName = $PackageFamilyName
        version = $Version
    })
}

if ($env:OS -ne 'Windows_NT') {
    $discoveryErrors.Add('Apple Music application discovery requires Windows.')
}
else {
    try {
        foreach ($startApp in @(Get-StartApps -ErrorAction Stop)) {
            if (Test-AppleMusicDisplayName ([string]$startApp.Name)) {
                Add-DiscoveredApp -Aumid ([string]$startApp.AppID) -DisplayName ([string]$startApp.Name) -Method 'StartMenu'
            }
        }
    }
    catch {
        $discoveryErrors.Add('Get-StartApps: ' + $_.Exception.Message)
    }

    try {
        # Current user only: no -AllUsers, other-user query, or administrator access.
        $packages = @(Get-AppxPackage -ErrorAction Stop)
    }
    catch {
        $packages = @()
        $discoveryErrors.Add('Get-AppxPackage: ' + $_.Exception.Message)
    }

    foreach ($package in $packages) {
        try {
            $manifest = Get-AppxPackageManifest -Package $package.PackageFullName -ErrorAction Stop
            $packageDisplayNode = $manifest.SelectSingleNode("/*[local-name()='Package']/*[local-name()='Properties']/*[local-name()='DisplayName']")
            $packageDisplayName = ''
            if ($null -ne $packageDisplayNode) { $packageDisplayName = [string]$packageDisplayNode.InnerText }
            $packageFamilyName = [string]$package.PackageFamilyName
            $packageName = [string]$package.Name
            $packageIdentifiesAppleMusic = (Test-AppleMusicPackageName $packageName) -or (Test-AppleMusicDisplayName $packageDisplayName)

            foreach ($application in @($manifest.SelectNodes("/*[local-name()='Package']/*[local-name()='Applications']/*[local-name()='Application']"))) {
                $applicationId = [string]$application.GetAttribute('Id')
                $aumid = $packageFamilyName + '!' + $applicationId
                $applicationDisplayNode = $application.SelectSingleNode("./*[local-name()='VisualElements']/@DisplayName")
                $applicationDisplayName = ''
                if ($null -ne $applicationDisplayNode) { $applicationDisplayName = [string]$applicationDisplayNode.Value }

                $startMenuIdentifiesAppleMusic = $appsById.ContainsKey($aumid)
                if (-not ($packageIdentifiesAppleMusic -or $startMenuIdentifiesAppleMusic -or (Test-AppleMusicDisplayName $applicationDisplayName))) {
                    continue
                }
                if ([string]::IsNullOrWhiteSpace($packageFamilyName) -or [string]::IsNullOrWhiteSpace($applicationId)) {
                    $discoveryErrors.Add('Incomplete Apple Music application identity in package ' + [string]$package.PackageFullName)
                    continue
                }

                # Keep a real discovered label; do not pretend an unresolved ms-resource
                # reference is a resolved display name. Package name remains useful evidence.
                $displayName = $packageName
                if (Test-AppleMusicDisplayName $applicationDisplayName) { $displayName = $applicationDisplayName }
                elseif (Test-AppleMusicDisplayName $packageDisplayName) { $displayName = $packageDisplayName }
                Add-DiscoveredApp -Aumid $aumid -DisplayName $displayName -Method 'PackageManifest' `
                    -PackageName $packageName -PackageFamilyName $packageFamilyName -Version ([string]$package.Version)
            }
        }
        catch {
            $discoveryErrors.Add('Get-AppxPackageManifest [' + [string]$package.PackageFullName + ']: ' + $_.Exception.Message)
        }
    }
}

$report = [ordered]@{
    apps = @($appsById.Values | Sort-Object aumid)
    errors = @($discoveryErrors.ToArray())
}
$reportDirectory = Split-Path -Parent $reportPath
if (-not (Test-Path -LiteralPath $reportDirectory -PathType Container)) {
    $null = New-Item -ItemType Directory -Path $reportDirectory -Force
}
$json = ConvertTo-Json -InputObject $report -Depth 6
[System.IO.File]::WriteAllText($reportPath, $json + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))
Write-Host ('Discovered {0} Apple Music application identity/identities; {1} error(s). Report: {2}' -f $appsById.Count, $discoveryErrors.Count, $reportPath)
