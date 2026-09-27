<#
.SYNOPSIS
    Verify that the local artifacts match the published v0.1.0 release.

.DESCRIPTION
    Compares the SHA-256 digests of the four PUBLISHED release artifacts against the local build
    output, and confirms the CLI archives actually contain the expected CLI executable.

    Published artifacts (what the GitHub release ships and what SHA256SUMS.txt lists):
      WinAudioRoute.0.1.0.nupkg
      WinAudioRoute.0.1.0.snupkg
      WinAudioRoute.Cli-0.1.0-win-x64.zip
      WinAudioRoute.Cli-0.1.0-win-arm64.zip

    The canonical published checksums live in the release asset SHA256SUMS.txt next to the CLI
    archives. Prefer that file when validating a download; this script exists to check a local
    build tree against what was actually released.

    WHY THE ZIP AND NOT THE BARE winaudio.exe
    -----------------------------------------
    A bare cli/**/winaudio.exe is NOT bit-stable: the .NET apphost embeds build metadata, so
    republishing from a different commit yields a different digest even from identical source.
    The zip archives are the shipped artifacts and their digests are recorded in the release, so
    they are what this script compares. The executables inside them are checked for presence and
    architecture rather than by digest.

    The NuGet packages are also not bit-stable across commits: the PDB embeds a SourceLink mapping
    and an AssemblyInformationalVersion, both containing the commit SHA. Use -AllowPackageDrift to
    downgrade exactly that difference to a warning.

    This file is saved as UTF-8 with BOM on purpose (Windows PowerShell 5.1 reads BOM-less
    files as ANSI, which corrupts non-ASCII content and can break script syntax).

.PARAMETER ArtifactDirectory
    Directory holding the packages and the CLI archives. Defaults to ./artifacts.

.PARAMETER AllowPackageDrift
    Treat a .nupkg / .snupkg digest mismatch as a warning rather than a failure.
#>

[CmdletBinding()]
param(
    [string]$ArtifactDirectory,
    [switch]$AllowPackageDrift
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot

if (-not $ArtifactDirectory) {
    $ArtifactDirectory = Join-Path $repoRoot 'artifacts'
}

# ---------------------------------------------------------------------------
# Digests of the published v0.1.0 release artifacts.
#
# Cross-checked against the release asset SHA256SUMS.txt:
#   https://github.com/kunkunkunQoQ/WinAudioRoute/releases/download/v0.1.0/SHA256SUMS.txt
#
# The packages were built at commit 89dcf7c691f165d26fec1f747726e27e0f2eee64 - the commit the
# shipped package's SourceLink mapping points at.
# ---------------------------------------------------------------------------
$expected = [ordered]@{
    'WinAudioRoute.0.1.0.nupkg'             = @{ Size = 94838;   Hash = '28CA36D47E665989BD45B186B003DC6A918D569625C4ADAAE3A485352D1B1E5A'; CommitDependent = $true }
    'WinAudioRoute.0.1.0.snupkg'            = @{ Size = 23255;   Hash = '98244A3CF4872BBB3E731E8637109E2ADF1C33B3ADC0DED917C487EE24382BC7'; CommitDependent = $true }
    'WinAudioRoute.Cli-0.1.0-win-x64.zip'   = @{ Size = 6524659; Hash = '6C7702FB5EC632DADC0C1A515C27A7D8B9C4A115A28CD04DC8D071B292B9A41D'; CommitDependent = $false }
    'WinAudioRoute.Cli-0.1.0-win-arm64.zip' = @{ Size = 6515156; Hash = '37B7AF97610CCE0AAD60D6288CF7D04D5083610A47EB5F16F16F011A35AE03B8'; CommitDependent = $false }
}

# Executables that must be present inside each archive, and their expected PE machine type.
$expectedArchiveContent = [ordered]@{
    'WinAudioRoute.Cli-0.1.0-win-x64.zip'   = @{ Entry = 'winaudio.exe'; Machine = 0x8664 }
    'WinAudioRoute.Cli-0.1.0-win-arm64.zip' = @{ Entry = 'winaudio.exe'; Machine = 0xAA64 }
}

if (-not (Test-Path -LiteralPath $ArtifactDirectory)) {
    Write-Host "FAIL: artifact directory not found: $ArtifactDirectory" -ForegroundColor Red
    Write-Host ''
    Write-Host 'Artifact verification: FAIL' -ForegroundColor Red
    exit 1
}

function Get-PeMachine {
    param([byte[]]$Bytes)

    # Offset 0x3C holds the PE header offset; the machine type sits 4 bytes after the signature.
    if ($Bytes.Length -lt 0x40) { return $null }

    $peOffset = [BitConverter]::ToInt32($Bytes, 0x3C)
    if ($peOffset -lt 0 -or ($peOffset + 6) -gt $Bytes.Length) { return $null }

    return [BitConverter]::ToUInt16($Bytes, $peOffset + 4)
}

Write-Host 'Verifying published artifacts'
Write-Host "  directory : $ArtifactDirectory"
Write-Host ''

$failures = 0
$warnings = 0

foreach ($relative in $expected.Keys) {
    $spec = $expected[$relative]
    $path = Join-Path $ArtifactDirectory $relative

    if (-not (Test-Path -LiteralPath $path)) {
        Write-Host ("  MISSING   {0}" -f $relative) -ForegroundColor Red
        $failures++
        continue
    }

    $item = Get-Item -LiteralPath $path
    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash

    $sizeOk = $item.Length -eq $spec.Size
    $hashOk = $hash -eq $spec.Hash

    if ($hashOk -and $sizeOk) {
        Write-Host ("  MATCH     {0}  ({1} B)" -f $relative, $item.Length) -ForegroundColor Green
        continue
    }

    $detail = "size $($item.Length) vs expected $($spec.Size); sha256 $hash vs expected $($spec.Hash)"

    if ($spec.CommitDependent -and $AllowPackageDrift) {
        Write-Host ("  DRIFT     {0}  {1}" -f $relative, $detail) -ForegroundColor Yellow
        $warnings++
    }
    else {
        Write-Host ("  MISMATCH  {0}  {1}" -f $relative, $detail) -ForegroundColor Red
        if ($spec.CommitDependent) {
            Write-Host '            (commit-dependent: rebuilding at a different commit changes this digest)' -ForegroundColor Yellow
        }

        $failures++
    }
}

# ---------------------------------------------------------------------------
# Confirm each archive really contains the CLI executable with the right architecture.
# ---------------------------------------------------------------------------
Add-Type -AssemblyName System.IO.Compression.FileSystem

Write-Host ''
Write-Host 'Verifying CLI archive contents'
Write-Host ''

foreach ($relative in $expectedArchiveContent.Keys) {
    $path = Join-Path $ArtifactDirectory $relative
    if (-not (Test-Path -LiteralPath $path)) {
        continue
    }

    $content = $expectedArchiveContent[$relative]
    $archive = [System.IO.Compression.ZipFile]::OpenRead($path)

    try {
        $entry = $archive.Entries | Where-Object { $_.FullName -eq $content.Entry }

        if (-not $entry) {
            Write-Host ("  MISSING   {0} -> {1}" -f $relative, $content.Entry) -ForegroundColor Red
            $failures++
            continue
        }

        $stream = $entry.Open()
        $buffer = New-Object byte[] $entry.Length
        $read = 0
        while ($read -lt $buffer.Length) {
            $n = $stream.Read($buffer, $read, $buffer.Length - $read)
            if ($n -le 0) { break }
            $read += $n
        }
        $stream.Dispose()

        $machine = Get-PeMachine -Bytes $buffer
        $expectedMachine = $content.Machine

        if ($machine -ne $expectedMachine) {
            Write-Host ("  BADARCH   {0} -> {1} PE 0x{2:X} (expected 0x{3:X})" -f $relative, $content.Entry, $machine, $expectedMachine) -ForegroundColor Red
            $failures++
        }
        else {
            Write-Host ("  OK        {0} -> {1} PE 0x{2:X}" -f $relative, $content.Entry, $machine) -ForegroundColor Green
        }
    }
    finally {
        $archive.Dispose()
    }
}

Write-Host ''

if ($failures -gt 0) {
    Write-Host "$failures check(s) did not match the published v0.1.0 release." -ForegroundColor Red
    Write-Host 'If the .nupkg/.snupkg differ, check `pwsh ./eng/verify-sourcelink.ps1` first:' -ForegroundColor Yellow
    Write-Host 'a rebuild at a different commit legitimately changes those two digests.' -ForegroundColor Yellow
    Write-Host ''
    Write-Host 'Artifact verification: FAIL' -ForegroundColor Red
    exit 1
}

if ($warnings -gt 0) {
    Write-Host "$warnings commit-dependent artifact(s) drifted (allowed)." -ForegroundColor Yellow
}

Write-Host 'Artifact verification: PASS' -ForegroundColor Green
exit 0