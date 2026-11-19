# Signs the Debug test build with the Certum certificate, so Smart App Control on this PC lets it
# run (it blocks freshly built, unsigned DLLs). SimplySign Desktop must be connected.
#
#   dotnet build tests/OctezDesk.Core.Tests
#   ./scripts/sign-tests.ps1
#   dotnet test tests/OctezDesk.Core.Tests --no-build
param([string] $Configuration = 'Debug')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'signing.ps1')

$path = Join-Path $root "tests\OctezDesk.Core.Tests\bin\$Configuration\net10.0"
$files = 'OctezDesk.Core.Tests.dll', 'OctezDesk.Core.dll' | ForEach-Object { Join-Path $path $_ } | Where-Object { Test-Path $_ }
if (-not $files) { throw "No $Configuration test build in $path; dotnet build tests/OctezDesk.Core.Tests first" }

$cert = Get-SigningCertificate
$signtool = Get-SignTool
# One file per call: SimplySign handles a long batch badly.
foreach ($file in $files) {
    & $signtool sign ((Get-SignParams $cert) -split ' ') $file
    if ($LASTEXITCODE -ne 0) { throw "signtool failed on $file" }
    Assert-Signed $file $cert
}
