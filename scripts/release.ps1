<#
.SYNOPSIS
  Builds the OctezDesk Windows installer and update packages with Velopack, and optionally
  publishes them as a GitHub release, which installed copies update from.

.EXAMPLE
  ./scripts/release.ps1 -Version 0.2.0              # build into artifacts/releases only
  ./scripts/release.ps1 -Version 0.2.0 -Publish     # also create the GitHub release v0.2.0

.NOTES
  Needs the .NET 10 SDK and the Velopack CLI (dotnet tool install -g vpk). Publishing uses the
  GitHub CLI's token (gh auth login). Release notes are read from release-notes/<Version>.md if it
  exists. Unsigned for now: pass signing options to vpk pack (--signParams or
  --azureTrustedSignFile) when there's a certificate.
#>
param(
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+(-[0-9A-Za-z.]+)?$')][string]$Version,
    [switch]$Publish
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

$repoUrl   = 'https://github.com/drchrispinnock/octezdesk'
# Not "OctezDesk": Velopack installs into %LocalAppData%\<packId> and deletes that folder on
# uninstall, and %LocalAppData%\OctezDesk\wsl is where the node's WSL disk lives by default.
$packId    = 'OctezDesk.Win'
# Every release must use the same channel: installed copies only see releases on theirs.
$channel   = 'win'
$publishDir = Join-Path $root 'artifacts\publish'
$releases  = Join-Path $root 'artifacts\releases'
$notes     = Join-Path $root "release-notes\$Version.md"

function Run([string]$what, [scriptblock]$command) {
    Write-Host "==> $what" -ForegroundColor Cyan
    & $command
    if ($LASTEXITCODE -ne 0) { throw "$what failed (exit $LASTEXITCODE)" }
}

Run 'Unit tests' { dotnet test tests/OctezDesk.Core.Tests --nologo -v quiet }

if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }
Run 'Publish (self-contained, win-x64)' {
    dotnet publish win/OctezDesk.Win -c Release -r win-x64 --self-contained -p:Version=$Version -o $publishDir --nologo -v quiet
}

New-Item -ItemType Directory -Force $releases | Out-Null
# The previous release, so that vpk can make a delta package. Fails harmlessly for the first release.
Write-Host '==> Download the previous release (for deltas)' -ForegroundColor Cyan
$token = try { (gh auth token 2>$null).Trim() } catch { '' }
$download = @('download', 'github', '--repoUrl', $repoUrl, '-o', $releases, '-c', $channel)
if ($token) { $download += @('--token', $token) }
& vpk @download
if ($LASTEXITCODE -ne 0) { Write-Host '    none found; this release has no delta.' }

$pack = @('pack', '-u', $packId, '-v', $Version, '-p', $publishDir, '-o', $releases, '-r', 'win-x64', '-c', $channel,
          '-e', 'OctezDesk.Win.exe', '--packTitle', 'OctezDesk', '--packAuthors', 'Chris Pinnock',
          '-i', 'win\OctezDesk.Win\Assets\OctezDesk.ico', '--shortcuts', 'Desktop,StartMenuRoot')
if (Test-Path $notes) { $pack += @('--releaseNotes', $notes) }
Run "Pack $packId $Version" { vpk @pack }

if ($Publish) {
    if (-not $token) { throw 'Publishing needs the GitHub CLI to be logged in (gh auth login).' }
    Run "Publish GitHub release v$Version" {
        vpk upload github -o $releases -c $channel --repoUrl $repoUrl --token $token --publish --releaseName "OctezDesk $Version" --tag "v$Version"
    }
}

Write-Host ''
Write-Host "Done. Installer: $(Join-Path $releases "$packId-win-Setup.exe")" -ForegroundColor Green
