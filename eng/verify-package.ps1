<#
.SYNOPSIS
    Verify the contents of the WinAudioRoute NuGet packages.

.DESCRIPTION
    Checks that the .nupkg and .snupkg contain everything the project promises:
      .nupkg : lib/<tfm>/WinAudioRoute.dll, matching .xml documentation, README.md,
               and nuspec metadata (MIT license, repository URL, readme entry)
      .snupkg: the portable PDB

    Fails with a non-zero exit code and a clear message on the first missing item.

    This file is intentionally ASCII-only (Windows PowerShell 5.1 reads BOM-less files as ANSI).

.PARAMETER PackageDirectory
    Directory containing the produced packages.

.PARAMETER Version
    Expected package version. Default: read from the .nupkg file name.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PackageDirectory,

    [string]$Version
)

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.IO.Compression.FileSystem

function Fail {
    param([string]$Message)
    Write-Host "FAIL: $Message" -ForegroundColor Red
    exit 1
}

function Pass {
    param([string]$Message)
    Write-Host "  ok: $Message" -ForegroundColor Green
}

if (-not (Test-Path $PackageDirectory)) {
    Fail "package directory not found: $PackageDirectory"
}

$nupkgFiles = @(Get-ChildItem -Path $PackageDirectory -Filter '*.nupkg' -File)
if ($nupkgFiles.Count -eq 0) {
    Fail "no .nupkg found in $PackageDirectory"
}

if ($nupkgFiles.Count -gt 1) {
    Fail "expected exactly one .nupkg, found $($nupkgFiles.Count)"
}

$nupkg = $nupkgFiles[0]

if (-not $Version) {
    $match = [regex]::Match($nupkg.Name, '^(?<id>.+)\.(?<version>\d+\.\d+\.\d+[^.]*)\.nupkg$')
    if (-not $match.Success) {
        Fail "cannot derive the version from '$($nupkg.Name)'"
    }

    $Version = $match.Groups['version'].Value
}

$snupkgPath = Join-Path $PackageDirectory ($nupkg.BaseName + '.snupkg')

Write-Host "Verifying packages for version $Version"
Write-Host "  nupkg : $($nupkg.FullName)"

# ---------------------------------------------------------------------------
# .nupkg
# ---------------------------------------------------------------------------

$zip = [System.IO.Compression.ZipFile]::OpenRead($nupkg.FullName)

try {
    $entries = @($zip.Entries | ForEach-Object { $_.FullName })

    $dll = @($entries | Where-Object { $_ -match '^lib/[^/]+/WinAudioRoute\.dll$' })
    if ($dll.Count -ne 1) {
        Fail "expected exactly one lib/<tfm>/WinAudioRoute.dll, found $($dll.Count)"
    }
    Pass "assembly present: $($dll[0])"

    $xml = @($entries | Where-Object { $_ -match '^lib/[^/]+/WinAudioRoute\.xml$' })
    if ($xml.Count -ne 1) {
        Fail "XML documentation missing from the package (expected lib/<tfm>/WinAudioRoute.xml)"
    }
    Pass "XML documentation present: $($xml[0])"

    if ($entries -notcontains 'README.md') {
        Fail "README.md missing from the package"
    }
    Pass "README.md present"

    if ($entries -notcontains 'WinAudioRoute.nuspec') {
        Fail "nuspec missing from the package"
    }

    $nuspecEntry = $zip.Entries | Where-Object { $_.FullName -eq 'WinAudioRoute.nuspec' }
    $reader = New-Object System.IO.StreamReader($nuspecEntry.Open())
    $nuspecText = $reader.ReadToEnd()
    $reader.Close()

    [xml]$nuspec = $nuspecText
    $metadata = $nuspec.package.metadata

    if ($metadata.id -ne 'WinAudioRoute') { Fail "unexpected package id '$($metadata.id)'" }
    Pass "package id: $($metadata.id)"

    if ($metadata.version -ne $Version) { Fail "version mismatch: nuspec=$($metadata.version) expected=$Version" }
    Pass "version: $($metadata.version)"

    if (-not $metadata.license -or $metadata.license.type -ne 'expression' -or $metadata.license.'#text' -ne 'MIT') {
        # Older schema puts the expression in the element text
        $licenseText = $metadata.license
        if ($licenseText -ne 'MIT') {
            Fail "license must be the MIT expression; got '$licenseText'"
        }
    }
    Pass "license: MIT (expression)"

    if (-not $metadata.projectUrl) { Fail "projectUrl missing from nuspec" }
    Pass "projectUrl: $($metadata.projectUrl)"

    if (-not $metadata.repository -or -not $metadata.repository.url) { Fail "repository URL missing from nuspec" }
    Pass "repository: $($metadata.repository.url)"

    if ($metadata.readme -ne 'README.md') { Fail "nuspec readme entry must point at README.md" }
    Pass "readme entry: $($metadata.readme)"

    if (-not $metadata.description -or $metadata.description.Length -lt 40) {
        Fail "description is missing or too short"
    }
    Pass "description present ($($metadata.description.Length) chars)"

    if (-not $metadata.tags) { Fail "tags missing from nuspec" }
    Pass "tags: $($metadata.tags)"

    # A library that promises zero third-party runtime dependencies must not declare any
    # dependency group with entries. SourceLink is PrivateAssets=all and must not leak.
    $dependencyElements = @($nuspec.SelectNodes('//*[local-name()="dependencies"]/*[local-name()="group"]/*[local-name()="dependency"]'))
    if ($dependencyElements.Count -gt 0) {
        $names = ($dependencyElements | ForEach-Object { $_.id }) -join ', '
        Fail "package declares runtime dependencies ($names); WinAudioRoute must have none"
    }
    Pass "no runtime dependencies declared"
}
finally {
    $zip.Dispose()
}

# ---------------------------------------------------------------------------
# .snupkg
# ---------------------------------------------------------------------------

if (-not (Test-Path $snupkgPath)) {
    Fail "symbol package not found: $snupkgPath"
}

Write-Host "  snupkg: $snupkgPath"

$symbolZip = [System.IO.Compression.ZipFile]::OpenRead($snupkgPath)
try {
    $symbolEntries = @($symbolZip.Entries | ForEach-Object { $_.FullName })

    $pdb = @($symbolEntries | Where-Object { $_ -match '^lib/[^/]+/WinAudioRoute\.pdb$' })
    if ($pdb.Count -ne 1) {
        Fail "portable PDB missing from the symbol package (expected lib/<tfm>/WinAudioRoute.pdb)"
    }
    Pass "symbols present: $($pdb[0])"
}
finally {
    $symbolZip.Dispose()
}

Write-Host ''
Write-Host 'Package verification PASSED.' -ForegroundColor Green
exit 0
