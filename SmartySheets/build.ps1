<#
.SYNOPSIS
    Builds KTA Smarty Sheets and installs it per-user for one or more Revit versions.

.DESCRIPTION
    Per-user install only. Revit 2027 moved machine-wide add-ins out of ProgramData and
    into Program Files; %APPDATA% is unchanged across 2025, 2026 and 2027, which is why
    this installer uses it and why no elevation is needed.

.EXAMPLE
    .\build.ps1 -Versions 2026
    .\build.ps1 -Versions 2025,2026,2027 -Config Release
    .\build.ps1 -Versions 2026 -Uninstall
#>

[CmdletBinding()]
param(
    [ValidateSet('2025', '2026', '2027')]
    [string[]] $Versions = @('2026'),

    [ValidateSet('Debug', 'Release')]
    [string] $Config = 'Debug',

    [switch] $Uninstall,

    # Overrides the Revit install root, for a machine where Revit is not on C:.
    [string] $RevitApiDir
)

$ErrorActionPreference = 'Stop'

$AddInId    = '562855e6-faac-4687-a913-43f47047888e'
$VendorId   = 'com.kta.smartysheets'
$AssemblyNm = 'KTA.SmartySheets'
$FullClass  = 'KTA.SmartySheets.App'

$Root    = Split-Path -Parent $MyInvocation.MyCommand.Path
$Project = Join-Path $Root 'src\KTA.SmartySheets\KTA.SmartySheets.csproj'

function Get-AddinRoot([string] $Version) {
    Join-Path $env:APPDATA "Autodesk\Revit\Addins\$Version"
}

function Remove-Install([string] $Version) {
    $addinRoot = Get-AddinRoot $Version
    $manifest  = Join-Path $addinRoot "$AssemblyNm.addin"
    $payload   = Join-Path $addinRoot $AssemblyNm

    if (Test-Path $manifest) { Remove-Item $manifest -Force; Write-Host "  removed $manifest" }
    if (Test-Path $payload)  { Remove-Item $payload -Recurse -Force; Write-Host "  removed $payload" }
}

function Write-Manifest([string] $Version) {
    $addinRoot = Get-AddinRoot $Version
    $manifest  = Join-Path $addinRoot "$AssemblyNm.addin"
    $dllPath   = Join-Path (Join-Path $addinRoot $AssemblyNm) "$AssemblyNm.dll"

    $xml = @"
<?xml version="1.0" encoding="utf-8"?>
<RevitAddIns>
  <AddIn Type="Application">
    <Name>KTA Smarty Sheets</Name>
    <Assembly>$dllPath</Assembly>
    <AddInId>$AddInId</AddInId>
    <FullClassName>$FullClass</FullClassName>
    <VendorId>$VendorId</VendorId>
    <VendorDescription>Kunal Trivedi Atelier, Ahmedabad</VendorDescription>
  </AddIn>
</RevitAddIns>
"@

    New-Item -ItemType Directory -Path $addinRoot -Force | Out-Null
    Set-Content -Path $manifest -Value $xml -Encoding UTF8
    Write-Host "  manifest $manifest"
}

if ($Uninstall) {
    foreach ($version in $Versions) {
        Write-Host "Uninstalling Revit $version" -ForegroundColor Cyan
        Remove-Install $version
    }
    Write-Host "Done. Restart Revit." -ForegroundColor Green
    return
}

foreach ($version in $Versions) {
    $configuration = "$Config R$version"
    Write-Host "Building $configuration" -ForegroundColor Cyan

    $buildArgs = @('build', $Project, '-c', $configuration, '--nologo', '-v', 'minimal')
    if ($RevitApiDir) { $buildArgs += "-p:RevitApiDir=$RevitApiDir" }

    & dotnet @buildArgs
    if ($LASTEXITCODE -ne 0) { throw "Build failed for $configuration." }

    $output = Join-Path $Root "src\KTA.SmartySheets\bin\$configuration"
    if (-not (Test-Path $output)) { throw "Build output not found at $output." }

    # A RevitAPI.dll beside the add-in is the classic silent load failure: Revit loads its
    # own copy first and the type identities stop matching. Fail loudly rather than ship it.
    $strays = Get-ChildItem $output -Filter 'RevitAPI*.dll' -ErrorAction SilentlyContinue
    if ($strays) {
        throw "RevitAPI dll(s) found in the build output: $($strays.Name -join ', '). Set <Private>False</Private> on those references."
    }

    Remove-Install $version

    $payload = Join-Path (Get-AddinRoot $version) $AssemblyNm
    New-Item -ItemType Directory -Path $payload -Force | Out-Null

    Get-ChildItem $output -Include '*.dll', '*.pdb' -Recurse |
        Where-Object { $_.Name -notlike 'RevitAPI*' } |
        Copy-Item -Destination $payload -Force

    Write-Manifest $version
    Write-Host "  installed to $payload" -ForegroundColor Green
}

Write-Host ""
Write-Host "Done. Start Revit and look for the KTA tab." -ForegroundColor Green
