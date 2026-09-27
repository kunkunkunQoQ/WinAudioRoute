<#
.SYNOPSIS
    Verify the SourceLink metadata embedded in the WinAudioRoute portable PDB.

.DESCRIPTION
    "SourceLink is enabled" is only a meaningful statement if the PDB actually contains a mapping,
    so this script reads the built portable PDB and asserts:

      1. a SourceLink custom debug information entry exists
      2. its document mapping points at the expected GitHub repository
      3. the mapping is bound to the exact current commit SHA
      4. the assembly carries AssemblyMetadata("RepositoryUrl", ...) and an
         AssemblyInformationalVersion of the form <version>+<commit>

    The portable PDB format cannot be read with plain PowerShell, so the reading is done by the
    small tool in eng/sourcelink-reader (not part of the solution and not referenced by anything,
    so it adds no dependency to the library, CLI, samples or tests).

    Exits non-zero with an explicit "SourceLink verification: FAIL" line when any check fails.

    This file is saved as UTF-8 with BOM on purpose (Windows PowerShell 5.1 reads BOM-less
    files as ANSI, which corrupts non-ASCII content and can break script syntax).

.PARAMETER PdbPath
    Portable PDB to inspect. Defaults to the Release build output of the library.

.PARAMETER ExpectedRepositoryUrl
    Expected repository URL.

.PARAMETER ExpectedCommit
    Expected commit SHA. Defaults to `git rev-parse HEAD`.

.PARAMETER Configuration
    Build configuration used to locate the default PDB path.
#>

[CmdletBinding()]
param(
    [string]$PdbPath,
    [string]$ExpectedRepositoryUrl = 'https://github.com/kunkunkunQoQ/WinAudioRoute',
    [string]$ExpectedCommit,
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot

function Fail {
    param([string]$Message)
    Write-Host "FAIL: $Message" -ForegroundColor Red
    Write-Host ''
    Write-Host 'SourceLink verification: FAIL' -ForegroundColor Red
    exit 1
}

if (-not $PdbPath) {
    $PdbPath = Join-Path $repoRoot "src\WinAudioRoute\bin\$Configuration\net8.0-windows10.0.19041.0\WinAudioRoute.pdb"
}

if (-not (Test-Path -LiteralPath $PdbPath)) {
    Fail "portable PDB not found: $PdbPath (build the library first)"
}

if (-not $ExpectedCommit) {
    $ExpectedCommit = "$(& git -C $repoRoot rev-parse HEAD 2>&1)".Trim()
    if ($LASTEXITCODE -ne 0 -or -not $ExpectedCommit) {
        Fail 'cannot determine the current commit; pass -ExpectedCommit explicitly'
    }
}

Write-Host 'Verifying SourceLink metadata'
Write-Host "  pdb              : $PdbPath"
Write-Host "  expected repo    : $ExpectedRepositoryUrl"
Write-Host "  expected commit  : $ExpectedCommit"
Write-Host ''

# ---------------------------------------------------------------------------
# Build and run the reader
# ---------------------------------------------------------------------------
$readerProject = Join-Path $repoRoot 'eng\sourcelink-reader\sourcelink-reader.csproj'

if (-not (Test-Path -LiteralPath $readerProject)) {
    Fail "the SourceLink reader project is missing: $readerProject"
}

$buildOutput = & dotnet build $readerProject -c Release --nologo 2>&1
if ($LASTEXITCODE -ne 0) {
    $buildOutput | ForEach-Object { Write-Host "  $_" }
    Fail 'could not build the SourceLink reader (eng/sourcelink-reader)'
}

$readerExe = Join-Path $repoRoot 'eng\sourcelink-reader\bin\Release\net8.0\sourcelink-reader.dll'
if (-not (Test-Path -LiteralPath $readerExe)) {
    Fail "the reader was built but not found at $readerExe"
}

$output = & dotnet $readerExe $PdbPath 2>&1
if ($LASTEXITCODE -ne 0) {
    $output | ForEach-Object { Write-Host "  $_" }
    Fail 'the SourceLink reader failed'
}

# ---------------------------------------------------------------------------
# Parse facts and assert
# ---------------------------------------------------------------------------
$facts = @{}
foreach ($line in $output) {
    if ("$line" -match '^(?<key>[A-Za-z.]+)=(?<value>.*)$') {
        $facts[$Matches['key']] = $Matches['value']
    }
}

Write-Host '  Observed PDB metadata:'
Write-Host ("    documents            = {0}" -f $facts['documents'])
Write-Host ("    sourcelink.present   = {0}" -f $facts['sourcelink.present'])
Write-Host ("    template             = {0}" -f $facts['template'])
Write-Host ("    repositoryUrl        = {0}" -f $facts['repositoryUrl'])
Write-Host ("    informationalVersion = {0}" -f $facts['informationalVersion'])
Write-Host ''

if ($facts['sourcelink.present'] -ne 'yes') {
    Fail 'the PDB contains no SourceLink custom debug information'
}

$template = $facts['template']
if (-not $template) {
    Fail 'the SourceLink document mapping is empty'
}

# SourceLink rewrites the repository URL onto a raw-content host, e.g.
#   https://github.com/<owner>/<repo>                          (repository URL)
#   https://raw.githubusercontent.com/<owner>/<repo>/<sha>/*   (mapping template)
# so compare the <owner>/<repo> identity rather than the host.
function Get-RepoIdentity {
    param([string]$Url)
    if ($Url -match '^https?://[^/]+/(?<owner>[^/]+)/(?<repo>[^/]+?)(?:\.git)?(?:/|$)') {
        return "$($Matches['owner'])/$($Matches['repo'])"
    }

    return $null
}

$expectedIdentity = Get-RepoIdentity $ExpectedRepositoryUrl
if (-not $expectedIdentity) {
    Fail "cannot parse an owner/repo identity out of '$ExpectedRepositoryUrl'"
}

$mappedIdentity = Get-RepoIdentity $template
if ($mappedIdentity -ne $expectedIdentity) {
    Fail "the SourceLink mapping points at '$mappedIdentity', expected '$expectedIdentity' (template: '$template')"
}

if ($template -notlike "*$ExpectedCommit*") {
    Fail "the SourceLink mapping is not bound to commit $ExpectedCommit (got '$template')"
}

if ($facts['repositoryUrl'] -ne $ExpectedRepositoryUrl) {
    Fail "AssemblyMetadata RepositoryUrl is '$($facts['repositoryUrl'])', expected '$ExpectedRepositoryUrl'"
}

$informational = $facts['informationalVersion']
if ($informational -notlike "*+$ExpectedCommit*") {
    Fail "AssemblyInformationalVersion '$informational' does not embed commit $ExpectedCommit"
}

$shortCommit = $ExpectedCommit.Substring(0, [Math]::Min(12, $ExpectedCommit.Length))
Write-Host ("  ok: SourceLink mapping targets {0} at commit {1}" -f $mappedIdentity, $shortCommit) -ForegroundColor Green
Write-Host '  ok: assembly metadata carries RepositoryUrl and the commit-bearing version' -ForegroundColor Green
Write-Host ''
Write-Host 'SourceLink verification: PASS' -ForegroundColor Green
exit 0
