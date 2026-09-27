<#
.SYNOPSIS
    WinAudioRoute real-hardware release gate.

.DESCRIPTION
    Runs the hardware-dependent test suites on a real Windows machine with real audio devices,
    and verifies that everything the mutation tests changed was restored.

    This is the release gate that GitHub Actions cannot replace:
    CI verifies builds, unit tests and packaging, but a hosted runner has no audio endpoints,
    so it cannot prove real Windows audio behaviour.

    What it runs:
      1. Release build
      2. Audio inventory (through the shipped CLI)
      3. Unit tests            (Category!=Integration&Category!=Mutation)
      4. Integration tests     (Category=Integration)          - read-only
      5. Mutation tests        (Category=Mutation)             - MODIFY state, gated, restored
      6. State restoration check (default devices + per-app routes)

    Mutation tests only run when this script sets RUN_AUDIO_MUTATION_TESTS=1.
    The script never installs drivers, never changes the audio configuration, and never
    prints device interface paths or application paths.

    NOTE: this file is intentionally ASCII-only. Windows PowerShell 5.1 reads BOM-less files
    as ANSI, which corrupts non-ASCII text (including regexes needed to parse localized
    xUnit output). Non-ASCII patterns are built from character codes instead.

.PARAMETER Configuration
    Build configuration. Default: Release.

.PARAMETER SkipMutation
    Run only the read-only parts (unit + integration).

.PARAMETER NoRestoreCheck
    Skip the before/after state comparison.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File ./scripts/test-real-audio.ps1
#>

[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [switch]$SkipMutation,
    [switch]$NoRestoreCheck
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$repoRoot = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $repoRoot 'WinAudioRoute.sln'
$libraryProject = Join-Path $repoRoot 'src\WinAudioRoute\WinAudioRoute.csproj'

$workDir = Join-Path ([System.IO.Path]::GetTempPath()) ('winaudio-real-audio-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $workDir | Out-Null

$script:FailureCount = 0

# xUnit localizes its summary. Build the alternates from code points so this file stays ASCII.
$cnFailed = [string][char]0x5931 + [char]0x8D25        # failed
$cnPassed = [string][char]0x901A + [char]0x8FC7        # passed
$cnSkipped = [string][char]0x5DF2 + [char]0x8DF3 + [char]0x8FC7   # skipped
$cnTotal = [string][char]0x603B + [char]0x8BA1         # total
$cnColon = [string][char]0xFF1A
$cnComma = [string][char]0xFF0C

# ----------------------------------------------------------------------------
# helpers
# ----------------------------------------------------------------------------

function Write-Section {
    param([string]$Title)
    Write-Host ''
    Write-Host ('=' * 78)
    Write-Host "  $Title"
    Write-Host ('=' * 78)
}

function Write-Field {
    param([string]$Name, [object]$Value)
    Write-Host ('  {0,-34} {1}' -f $Name, $Value)
}

function Invoke-DotnetTest {
    <#
      Run one filtered test pass and parse the summary plus the environment-bypass log.
    #>
    param(
        [string]$Label,
        [string]$Filter
    )

    $bypassLog = Join-Path $workDir ('bypass-' + [Guid]::NewGuid().ToString('N') + '.log')
    $env:WINAUDIOROUTE_TEST_BYPASS_LOG = $bypassLog

    $output = & dotnet test $solution -c $Configuration --no-build --nologo --filter $Filter 2>&1
    $exitCode = $LASTEXITCODE
    $output | ForEach-Object { Write-Host "  $_" }

    $env:WINAUDIOROUTE_TEST_BYPASS_LOG = $null

    # Normalize U+00A0 (used as a thousands separator in the localized summary)
    $text = ($output | Out-String) -replace ([string][char]0x00A0), ' '

    $pattern = '(?:{0}|Failed)\s*[:\uFF1A]?\s*(\d+)\s*[,\uFF0C]\s*(?:{1}|Passed)\s*[:\uFF1A]?\s*(\d+)\s*[,\uFF0C]\s*(?:{2}|Skipped)\s*[:\uFF1A]?\s*(\d+)\s*[,\uFF0C]\s*(?:{3}|Total)\s*[:\uFF1A]?\s*(\d+)' -f `
        [regex]::Escape($cnFailed), [regex]::Escape($cnPassed), [regex]::Escape($cnSkipped), [regex]::Escape($cnTotal)

    $failed = 0
    $passed = 0
    $skipped = 0
    $total = 0

    $m = [regex]::Match($text, $pattern)
    if ($m.Success) {
        $failed = [int]$m.Groups[1].Value
        $passed = [int]$m.Groups[2].Value
        $skipped = [int]$m.Groups[3].Value
        $total = [int]$m.Groups[4].Value
    }

    $bypasses = 0
    if (Test-Path $bypassLog) {
        $lines = @(Get-Content $bypassLog | Where-Object { $_ -and $_ -notmatch '^\[COUNT\]' })
        $bypasses = $lines.Count
    }

    return [pscustomobject]@{
        Label    = $Label
        ExitCode = $exitCode
        Passed   = $passed
        Failed   = $failed
        Skipped  = $skipped
        Total    = $total
        Bypasses = $bypasses
    }
}

function Get-AudioSnapshot {
    <#
      Capture the verify-relevant audio state through the library's public API:
      the six default endpoints plus the persisted per-app route of every PID with a session.
      Device interface paths are only written to a temp file, never printed.
    #>
    param([string]$Path)

    $probeDir = Join-Path $workDir ('snapshot-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $probeDir | Out-Null

    $projectTemplate = @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0-windows10.0.19041.0</TargetFramework>
    <SupportedOSPlatformVersion>10.0.19041.0</SupportedOSPlatformVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <PlatformTarget>x64</PlatformTarget>
    <AssemblyName>snapshotprobe</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="__PROJECT_REFERENCE__" />
  </ItemGroup>
</Project>
'@

    $projectTemplate.Replace('__PROJECT_REFERENCE__', $libraryProject) |
        Set-Content -Path (Join-Path $probeDir 'snapshotprobe.csproj') -Encoding UTF8

    $programTemplate = @'
using System.Text;
using WinAudioRoute;

var lines = new List<string>();
using var audio = new WindowsAudioManager();

foreach (AudioDataFlow flow in (AudioDataFlow[])[AudioDataFlow.Render, AudioDataFlow.Capture])
{
    foreach (AudioRole role in (AudioRole[])[AudioRole.Console, AudioRole.Multimedia, AudioRole.Communications])
    {
        lines.Add($"DEFAULT {flow}/{role} = {audio.GetDefaultDevice(flow, role)?.Id ?? "(none)"}");
    }
}

if (audio.IsPerAppRoutingSupported)
{
    var seen = new SortedSet<int>();
    foreach (AudioSession session in audio.GetSessions(bypassCache: true))
    {
        seen.Add(session.ProcessId);
    }

    foreach (int pid in seen)
    {
        string output;
        string input;
        try { output = audio.GetApplicationOutput(pid)?.Id ?? "(follow-default)"; }
        catch (Exception ex) { output = "<" + ex.GetType().Name + ">"; }
        try { input = audio.GetApplicationInput(pid)?.Id ?? "(follow-default)"; }
        catch (Exception ex) { input = "<" + ex.GetType().Name + ">"; }

        lines.Add($"ROUTE pid={pid} out={output} in={input}");
    }
}
else
{
    lines.Add("ROUTE <per-app routing not supported>");
}

File.WriteAllLines(args[0], lines, Encoding.UTF8);
'@

    $programTemplate | Set-Content -Path (Join-Path $probeDir 'Program.cs') -Encoding UTF8

    & dotnet run --project (Join-Path $probeDir 'snapshotprobe.csproj') -c $Configuration -- $Path 2>&1 |
        ForEach-Object { Write-Host "  $_" }

    return (Test-Path $Path)
}

# ----------------------------------------------------------------------------
# 0. environment
# ----------------------------------------------------------------------------

Write-Section 'WinAudioRoute - real-hardware release gate'

Write-Host '  Environment'
$os = Get-CimInstance Win32_OperatingSystem
Write-Field 'OS' ('{0} (build {1})' -f $os.Caption, $os.BuildNumber)
Write-Field 'Architecture (OS)' $os.OSArchitecture
Write-Field 'Architecture (process)' ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture)
Write-Field '64-bit process' ([Environment]::Is64BitProcess)
Write-Field 'PowerShell' $PSVersionTable.PSVersion.ToString()

# ----------------------------------------------------------------------------
# 1. build
# ----------------------------------------------------------------------------

Write-Section '1. Build'

Write-Host ''
Write-Host "> dotnet build $solution -c $Configuration"
& dotnet build $solution -c $Configuration --nologo 2>&1 | ForEach-Object { Write-Host "  $_" }
$buildExit = $LASTEXITCODE

if ($buildExit -ne 0) {
    Write-Host ''
    Write-Host 'BUILD FAILED - aborting.' -ForegroundColor Red
    exit 1
}

# ----------------------------------------------------------------------------
# 2. audio inventory (via the shipped CLI)
# ----------------------------------------------------------------------------

Write-Section '2. Audio inventory'

$cliExe = Join-Path $repoRoot ('src\WinAudioRoute.Cli\bin\' + $Configuration + '\net8.0-windows10.0.19041.0\winaudio.exe')

if (-not (Test-Path $cliExe)) {
    Write-Host "  CLI not found at $cliExe - skipping inventory." -ForegroundColor Yellow
}
else {
    try {
        $caps = (& $cliExe capabilities --json | ConvertFrom-Json).data
        Write-Field 'Windows build' $caps.windowsBuild
        Write-Field 'Architecture' $caps.architecture
        Write-Field 'Default device write' $(if ($caps.defaultDeviceWriteEnabled) { 'enabled' } else { 'disabled' })
        Write-Field 'Device notification' $(if ($caps.deviceNotificationRegistered) { 'registered' } else { 'not registered' })
        Write-Field 'Per-app routing supported' $caps.perAppRoutingSupported
        if (-not $caps.perAppRoutingSupported) {
            Write-Field 'Per-app routing reason' $caps.perAppRoutingReason
        }
    }
    catch {
        Write-Host "  capabilities failed: $($_.Exception.Message)" -ForegroundColor Yellow
    }

    try {
        $playback = @((& $cliExe devices --output --json | ConvertFrom-Json).data.devices)
        $recording = @((& $cliExe devices --input --json | ConvertFrom-Json).data.devices)
        $sessions = @((& $cliExe sessions --json | ConvertFrom-Json).data.sessions)

        Write-Field 'Playback devices (active)' $playback.Count
        Write-Field 'Recording devices (active)' $recording.Count
        Write-Field 'Audio sessions' $sessions.Count
    }
    catch {
        Write-Host "  inventory failed: $($_.Exception.Message)" -ForegroundColor Yellow
    }
}

# ----------------------------------------------------------------------------
# 3. snapshot (before)
# ----------------------------------------------------------------------------

$snapshotBefore = Join-Path $workDir 'state-before.txt'
$snapshotAfter = Join-Path $workDir 'state-after.txt'

if (-not $SkipMutation -and -not $NoRestoreCheck) {
    Write-Section '3. State snapshot (before mutation)'
    if (-not (Get-AudioSnapshot -Path $snapshotBefore)) {
        Write-Host '  snapshot failed.' -ForegroundColor Yellow
    }
}

# ----------------------------------------------------------------------------
# 4. unit tests (CI scope)
# ----------------------------------------------------------------------------

Write-Section '4. Unit tests (Category!=Integration&Category!=Mutation)'
$unit = Invoke-DotnetTest -Label 'unit' -Filter 'Category!=Integration&Category!=Mutation'
if ($unit.ExitCode -ne 0) { $script:FailureCount++ }

# ----------------------------------------------------------------------------
# 5. integration tests (read-only, real hardware)
# ----------------------------------------------------------------------------

Write-Section '5. Integration tests (Category=Integration, read-only)'
$integration = Invoke-DotnetTest -Label 'integration' -Filter 'Category=Integration'
if ($integration.ExitCode -ne 0) { $script:FailureCount++ }

# ----------------------------------------------------------------------------
# 6. mutation tests (gated)
# ----------------------------------------------------------------------------

$mutation = $null

if ($SkipMutation) {
    Write-Section '6. Mutation tests - SKIPPED (-SkipMutation)'
}
else {
    Write-Section '6. Mutation tests (Category=Mutation, RUN_AUDIO_MUTATION_TESTS=1)'
    Write-Host '  These tests MODIFY real audio state and restore it in finally blocks.'

    $env:RUN_AUDIO_MUTATION_TESTS = '1'
    try {
        $mutation = Invoke-DotnetTest -Label 'mutation' -Filter 'Category=Mutation'
        if ($mutation.ExitCode -ne 0) { $script:FailureCount++ }
    }
    finally {
        $env:RUN_AUDIO_MUTATION_TESTS = $null
    }
}

# ----------------------------------------------------------------------------
# 7. restoration check
# ----------------------------------------------------------------------------

Write-Section '7. Restoration check'

$restoreOk = $true

if ($SkipMutation -or $NoRestoreCheck) {
    Write-Host '  skipped.'
}
elseif (-not (Test-Path $snapshotBefore)) {
    Write-Host '  no before-snapshot available; cannot compare.' -ForegroundColor Yellow
    $restoreOk = $false
}
else {
    if (-not (Get-AudioSnapshot -Path $snapshotAfter)) {
        Write-Host '  after-snapshot failed.' -ForegroundColor Yellow
        $restoreOk = $false
    }
    else {
        $before = @(Get-Content $snapshotBefore)
        $after = @(Get-Content $snapshotAfter)
        $diff = Compare-Object $before $after

        if ($diff) {
            Write-Host '  STATE NOT RESTORED - differences:' -ForegroundColor Red
            $diff | ForEach-Object { Write-Host ('    {0} {1}' -f $_.SideIndicator, $_.InputObject) }
            $restoreOk = $false
            $script:FailureCount++
        }
        else {
            Write-Host '  Default devices and per-app routes match the before-snapshot.' -ForegroundColor Green
        }
    }
}

# ----------------------------------------------------------------------------
# 8. summary
# ----------------------------------------------------------------------------

Write-Section 'Summary'

function Write-TestRow {
    param($Result)
    if ($null -eq $Result) { return }
    Write-Host ('  {0,-12} passed={1,-4} failed={2,-3} skipped={3,-3} total={4,-4} env-bypassed={5}' -f `
        $Result.Label, $Result.Passed, $Result.Failed, $Result.Skipped, $Result.Total, $Result.Bypasses)
}

Write-TestRow $unit
Write-TestRow $integration
Write-TestRow $mutation

Write-Host ''
Write-Host '  About "env-bypassed":'
Write-Host '    xUnit 2.5.3 has no Assert.Skip, so a test that cannot run (no device, no session)'
Write-Host '    returns early and is still reported as PASSED. The env-bypassed count is how many'
Write-Host '    tests did NOT exercise their assertions; subtract it when reading "passed".'

Write-Host ''
if ($restoreOk -and -not $SkipMutation -and -not $NoRestoreCheck) {
    Write-Host '  State restoration: OK' -ForegroundColor Green
}

Write-Host ''
if ($script:FailureCount -eq 0) {
    Write-Host '  RESULT: real-audio verification PASSED' -ForegroundColor Green
    $finalExit = 0
}
else {
    Write-Host ('  RESULT: real-audio verification FAILED ({0} failing step(s))' -f $script:FailureCount) -ForegroundColor Red
    $finalExit = 1
}

if ($SkipMutation) {
    Write-Host '  NOTE: mutation tests were skipped, so this run is NOT a full release gate.' -ForegroundColor Yellow
}

Write-Host ''
Write-Host "  Working files: $workDir"

exit $finalExit
