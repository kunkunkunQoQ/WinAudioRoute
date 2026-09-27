<#
.SYNOPSIS
    Verify that the local artifacts match the frozen handoff state.

.DESCRIPTION
    Confirms that the four deliverable artifacts in artifacts/ are the ones the development AI
    verified before handoff, by comparing their SHA-256 digests.

    WHY A SCRIPT INSTEAD OF ONLY A DOCUMENTED TABLE
    -----------------------------------------------
    The NuGet package digests are NOT stable across commits: the PDB embeds a SourceLink mapping
    and an AssemblyInformationalVersion, both of which contain the commit SHA. So ".nupkg" gets a
    new digest every time the artifact is rebuilt from a different commit. Recording a digest in a
    document therefore goes stale the moment anything is committed.

    This script records the currently-expected digests as constants (updated at freeze time) and
    reports a clear per-file MATCH / MISMATCH. A MISMATCH on the .nupkg/.snupkg is expected if the
    artifact was rebuilt at a different commit — in that case check
    `pwsh ./eng/verify-sourcelink.ps1` (its commit assertion) before treating it as corruption.
    The CLI digests do not embed the commit and should match exactly.

    This file is saved as UTF-8 with BOM on purpose (Windows PowerShell 5.1 reads BOM-less
    files as ANSI, which corrupts non-ASCII content and can break script syntax).

.PARAMETER ArtifactDirectory
    Directory holding WinAudioRoute.0.1.0.nupkg / .snupkg and cli/. Defaults to ./artifacts.

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
# Expected digests, frozen at handoff time.
# The packages were built at commit 3781cea5dfd64e429243eabf47bd2d639b769d0b. Any commit after that (Markdown-only or not)
# legitimately changes the .nupkg/.snupkg digests, because the PDB embeds the commit SHA.
# Use -AllowPackageDrift to downgrade that specific difference to a warning.
# ---------------------------------------------------------------------------
$expected = [ordered]@{
    'WinAudioRoute.0.1.0.nupkg'      = @{ Size = 94839;  Hash = '239A38E7997C4A7AF6E59B02D75881397994DD856C6E9B5DE299E7F6362E7249'; CommitDependent = $true }
    'WinAudioRoute.0.1.0.snupkg'     = @{ Size = 23263;  Hash = '855E76EABAA0C014F3F0925CACD43A493A8DEE86350C6153A9695B09D9EA9927'; CommitDependent = $true }
    'cli/win-x64/winaudio.exe'       = @{ Size = 152064; Hash = '67A25A4BC4B1F95C2FF6C20BFEC3124D1DFC6E6B7A7DBAABC3DE1753AD68E3C5'; CommitDependent = $false }
    'cli/win-arm64/winaudio.exe'     = @{ Size = 133120; Hash = '71AD7C93FEE1854B1C95417AA369047225B69CCF1448EC58D4CD592AABD1F074'; CommitDependent = $false }
}

if (-not (Test-Path -LiteralPath $ArtifactDirectory)) {
    Write-Host "FAIL: artifact directory not found: $ArtifactDirectory" -ForegroundColor Red
    Write-Host ''
    Write-Host 'Artifact freeze verification: FAIL' -ForegroundColor Red
    exit 1
}

Write-Host 'Verifying frozen artifacts'
Write-Host "  directory : $ArtifactDirectory"
Write-Host ''

$failures = 0
$warnings = 0

foreach ($relative in $expected.Keys) {
    $spec = $expected[$relative]
    $path = Join-Path $ArtifactDirectory ($relative -replace '/', '\')

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
            Write-Host '            (commit-dependent: rebuild at a different commit changes this digest)' -ForegroundColor Yellow
        }

        $failures++
    }
}

Write-Host ''

if ($failures -gt 0) {
    Write-Host "$failures artifact(s) did not match the frozen state." -ForegroundColor Red
    Write-Host 'If the .nupkg/.snupkg differ, check `pwsh ./eng/verify-sourcelink.ps1` first:' -ForegroundColor Yellow
    Write-Host 'a rebuild at a different commit legitimately changes those two digests.' -ForegroundColor Yellow
    Write-Host ''
    Write-Host 'Artifact freeze verification: FAIL' -ForegroundColor Red
    exit 1
}

if ($warnings -gt 0) {
    Write-Host "$warnings commit-dependent artifact(s) drifted (allowed)." -ForegroundColor Yellow
}

Write-Host 'Artifact freeze verification: PASS' -ForegroundColor Green
exit 0
