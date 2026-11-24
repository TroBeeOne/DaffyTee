<#
.SYNOPSIS
    Packs DaffyTee and runs the Linux smoke test (scripts/linux-smoke.sh) in the .NET SDK container.

.DESCRIPTION
    Linux takes different paths than Windows through three things that matter here: TLS is OpenSSL
    rather than SChannel (in both of DaffyTee's roles), the store key is an owner-only file rather
    than DPAPI, and SqlClient uses its managed SNI rather than the native one. This installs the
    package exactly as a user would and runs every TeeProbe check against it.

    Needs Docker and the test SQL Server from docker/compose.yml, published on the host's 14333.
#>
param([string]$Version)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Version) {
    $Version = [regex]::Match((Get-Content -Raw "$root\src\DaffyTee\DaffyTee.csproj"), '<Version>([^<]+)</Version>').Groups[1].Value
}

$docker = (Get-Command docker -ErrorAction SilentlyContinue).Source
if (-not $docker) { $docker = "$env:LOCALAPPDATA\Programs\DockerDesktop\resources\bin\docker.exe" }

Remove-Item -Recurse -Force "$root\nupkg" -ErrorAction SilentlyContinue
dotnet pack "$root/src/DaffyTee" -c Release -o "$root/nupkg" -nologo -v q | Out-Null
if ($LASTEXITCODE -ne 0) { throw "pack failed" }
dotnet build "$root/probes/TeeProbe" -c Debug -nologo -v q | Out-Null
if ($LASTEXITCODE -ne 0) { throw "TeeProbe build failed" }

# The script must reach bash with Unix line endings, whatever git did to it on checkout.
$smoke = Join-Path ([IO.Path]::GetTempPath()) "daffytee-linux-smoke.sh"
[IO.File]::WriteAllText($smoke, ((Get-Content -Raw "$root\scripts\linux-smoke.sh") -replace "`r`n", "`n"))

& $docker run --rm -e "VERSION=$Version" `
    -v "${root}\nupkg:/nupkg:ro" `
    -v "${root}\probes\TeeProbe\bin\Debug\net10.0:/probe:ro" `
    -v "${smoke}:/linux-smoke.sh:ro" `
    mcr.microsoft.com/dotnet/sdk:10.0 bash /linux-smoke.sh
exit $LASTEXITCODE
