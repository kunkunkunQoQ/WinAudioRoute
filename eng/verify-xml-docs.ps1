<#
.SYNOPSIS
    Verify that the WinAudioRoute library builds with complete XML documentation.

.DESCRIPTION
    Builds src/WinAudioRoute with documentation generation enabled and treats any
    CS1591 warning ("missing XML comment for publicly visible type or member") as a failure.

    It also confirms the generated .xml file exists, parses, and is non-trivial.

    The stronger, reflection-based completeness check lives in the test suite
    (XmlDocumentationTests), which verifies that every public API member has a documentation entry.

    ENCODING NOTE: this file is saved as UTF-8 *with* BOM on purpose. Windows PowerShell 5.1
    reads BOM-less files using the ANSI code page, which corrupts non-ASCII comments and can
    break the script's syntax. Do not strip the BOM.
#>

[CmdletBinding()]
param(
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'src\WinAudioRoute\WinAudioRoute.csproj'

Write-Host 'Verifying XML documentation completeness'

$output = & dotnet build $project -c $Configuration --nologo 2>&1
$exitCode = $LASTEXITCODE
$text = ($output | Out-String)

if ($exitCode -ne 0) {
    Write-Host $text
    Write-Host 'FAIL: the library does not build.' -ForegroundColor Red
    exit 1
}

# CS1591 = missing XML comment for publicly visible type or member
$missingDocs = @($text -split "`r?`n" | Where-Object { $_ -match 'warning\s+CS1591' })

if ($missingDocs.Count -gt 0) {
    Write-Host 'FAIL: public API members without XML documentation:' -ForegroundColor Red
    $missingDocs | ForEach-Object { Write-Host "  $_" }
    exit 1
}

Write-Host '  ok: no CS1591 warnings (every public API member is documented)' -ForegroundColor Green

$xmlPath = Join-Path $repoRoot "src\WinAudioRoute\bin\$Configuration\net8.0-windows10.0.19041.0\WinAudioRoute.xml"

if (-not (Test-Path -LiteralPath $xmlPath)) {
    Write-Host "FAIL: XML documentation file was not produced at $xmlPath" -ForegroundColor Red
    exit 1
}

# Load with XmlDocument.Load rather than Get-Content: line-by-line reading mangles
# files that contain non-ASCII text and produces bogus "tag mismatch" errors.
$doc = New-Object System.Xml.XmlDocument

try {
    $doc.Load($xmlPath)
}
catch {
    Write-Host "FAIL: cannot parse the XML documentation at $xmlPath" -ForegroundColor Red
    Write-Host ("  {0}" -f $_.Exception.Message)
    exit 1
}

$memberCount = @($doc.SelectNodes('//member')).Count

if ($memberCount -lt 100) {
    Write-Host "FAIL: only $memberCount documented members found; expected the full public surface." -ForegroundColor Red
    exit 1
}

Write-Host "  ok: $memberCount documented members in WinAudioRoute.xml" -ForegroundColor Green
Write-Host ''
Write-Host 'XML documentation verification PASSED.' -ForegroundColor Green
exit 0
