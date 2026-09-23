#!/usr/bin/env pwsh
# Local build gate for the FanControl Lian Li plugin: restore, format-verify, then
# build and test ALL THREE shipped variants - the standard DLL, the ARGB DLL, and
# the Lighting DLL - each held to full line and branch coverage of the code compiled into it
# (see the test project). Mirrors what CI runs. Stops on the first failing step.

$ErrorActionPreference = 'Stop'

function Invoke-Step {
    param([string]$Name, [scriptblock]$Command)
    Write-Host "==> $Name" -ForegroundColor Cyan
    & $Command
    if ($LASTEXITCODE -ne 0) {
        throw "$Name failed with exit code $LASTEXITCODE"
    }
}

$plugin = 'src/FanControl.LianLi/FanControl.LianLi.csproj'

Invoke-Step 'restore' { dotnet restore }
Invoke-Step 'format' { dotnet format --verify-no-changes }

# Standard variant (no ARGB): the default plugin DLL.
Invoke-Step 'build (standard)' { dotnet build -c Release --no-restore }
Invoke-Step 'test (standard)' { dotnet test -c Release --no-build -p:CollectCoverage=true }

# ARGB variant behavior: EnableArgb defines ENABLE_ARGB, which compiles in the
# startup ARGB-sync write. Tested with the assembly name UNCHANGED - renaming the
# assembly under test breaks xUnit's resolution of the [InlineData] typeof(...)
# protocol arguments, so the rename is reserved for the shippable artifact below.
Invoke-Step 'test (argb)' { dotnet test -c Release -p:EnableArgb=true -p:CollectCoverage=true }

# Shippable ARGB artifact: same ENABLE_ARGB behavior, renamed to FanControl.LianLi.Argb
# so it ships as a distinct second DLL. Built (plugin project only, no test) so the
# release artifact is proven to compile locally.
Invoke-Step 'build (argb)' { dotnet build $plugin -c Release --no-restore -p:EnableArgb=true -p:AssemblyName=FanControl.LianLi.Argb }

# Lighting variant behavior: EnableLighting defines ENABLE_LIGHTING, which compiles in
# the startup L-Connect-look replay. Like the ARGB pass, the assembly name is kept
# UNCHANGED here so xUnit's [InlineData] typeof(...) resolution holds; the rename is
# reserved for the shippable artifact below. The lighting tests are gated on
# ENABLE_LIGHTING, so this pass is the only one that runs them.
Invoke-Step 'test (lighting)' { dotnet test -c Release -p:EnableLighting=true -p:CollectCoverage=true }

# Shippable Lighting artifact: same ENABLE_LIGHTING behavior, renamed to
# FanControl.LianLi.Lighting so it ships as a distinct third DLL. Built (plugin project
# only, no test) so the release artifact is proven to compile locally.
Invoke-Step 'build (lighting)' { dotnet build $plugin -c Release --no-restore -p:EnableLighting=true -p:AssemblyName=FanControl.LianLi.Lighting }

Write-Host 'All checks passed.' -ForegroundColor Green
