#!/usr/bin/env pwsh
<#
.SYNOPSIS
    DESIGN.md §7 / M7 packaging (D7 items 1 and 6): builds the VSIX end to end, from a
    clean checkout, in one command.

.DESCRIPTION
    1. (optional) Bumps extension/package.json to -Version (no git tag), so the packaged
       VSIX carries that version.
    2. Publishes TsqlDbg.Adapter self-contained for the target runtime into
       extension/bin/<rid>/ -- the exact path extension/src/extension.ts's
       TsqlDebugAdapterDescriptorFactory.defaultAdapterPath() resolves at runtime
       (extensionPath/bin/win-x64/TsqlDbg.Adapter.exe).
    3. Installs the extension's npm dependencies (npm ci) and builds the esbuild
       bundle (dist/extension.js).
    4. Packages a platform-specific VSIX with @vscode/vsce (a devDependency, so this
       step needs no global tool install or network-interactive prompt), tagged with the
       marketplace target that matches the RID -- the same bytes .github/workflows/publish.yml
       produces per matrix leg.

.PARAMETER Configuration
    dotnet build configuration for the adapter publish. Default: Release.

.PARAMETER RuntimeIdentifier
    .NET RID for the self-contained adapter publish. Default: win-x64. Recognized:
    win-x64, linux-x64, osx-arm64 (their marketplace targets are derived automatically;
    override with -Target for anything else).

.PARAMETER Target
    VS Code marketplace target passed to `vsce package --target`. Default: derived from
    -RuntimeIdentifier (win-x64 -> win32-x64, linux-x64 -> linux-x64, osx-arm64 -> darwin-arm64).

.PARAMETER Version
    Optional. Sets extension/package.json to this version before packaging (npm version
    --no-git-tag-version --allow-same-version). Blank = leave the current version as-is.

.PARAMETER OutputPath
    Where to write the resulting .vsix. Default:
    <repo root>/tsql-step-debugger-<target>.vsix.

.PARAMETER SkipNpmCi
    Reuse the existing node_modules (skip `npm ci`) -- faster on repeat local runs.

.EXAMPLE
    pwsh -File scripts/package-vsix.ps1

.EXAMPLE
    pwsh -File scripts/package-vsix.ps1 -Version 0.0.4

.EXAMPLE
    pwsh -File scripts/package-vsix.ps1 -RuntimeIdentifier linux-x64
#>
[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$RuntimeIdentifier = "win-x64",
    [string]$Target,
    [string]$Version,
    [string]$OutputPath,
    [switch]$SkipNpmCi
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$adapterProject = Join-Path $repoRoot "src/TsqlDbg.Adapter"
$extensionDir = Join-Path $repoRoot "extension"
$adapterOut = Join-Path $extensionDir "bin/$RuntimeIdentifier"

# RID -> marketplace target (same mapping as .github/workflows/publish.yml). Override with -Target.
if (-not $Target) {
    $Target = switch ($RuntimeIdentifier) {
        "win-x64"   { "win32-x64" }
        "linux-x64" { "linux-x64" }
        "osx-arm64" { "darwin-arm64" }
        default {
            throw "No default marketplace target known for RID '$RuntimeIdentifier'. Pass -Target explicitly."
        }
    }
}

if (-not $OutputPath) {
    $OutputPath = Join-Path $repoRoot "tsql-step-debugger-$Target.vsix"
}

function Invoke-Step {
    param([string]$Description, [scriptblock]$Action)
    Write-Host ""
    Write-Host "==> $Description" -ForegroundColor Cyan
    & $Action
    if ($LASTEXITCODE -ne 0) {
        throw "Step failed (exit $LASTEXITCODE): $Description"
    }
}

if ($Version) {
    Push-Location $extensionDir
    try {
        Invoke-Step "Setting extension version -> $Version" {
            npm version $Version --no-git-tag-version --allow-same-version
        }
    }
    finally {
        Pop-Location
    }
}

Invoke-Step "Publishing self-contained adapter ($RuntimeIdentifier, $Configuration) -> $adapterOut" {
    if (Test-Path $adapterOut) {
        Remove-Item -Recurse -Force $adapterOut
    }
    dotnet publish $adapterProject -c $Configuration -r $RuntimeIdentifier --self-contained -o $adapterOut
}

$exeName = if ($RuntimeIdentifier.StartsWith("win-")) { "TsqlDbg.Adapter.exe" } else { "TsqlDbg.Adapter" }
$exePath = Join-Path $adapterOut $exeName
if (-not (Test-Path $exePath)) {
    throw "Expected adapter executable not found after publish: $exePath"
}
Write-Host "    adapter exe: $exePath ($([math]::Round((Get-Item $exePath).Length / 1MB, 1)) MB)"

Push-Location $extensionDir
try {
    if (-not $SkipNpmCi) {
        Invoke-Step "Installing extension dependencies (npm ci)" {
            npm ci
        }
    }

    Invoke-Step "Building the extension bundle (npm run build)" {
        npm run build
    }

    Invoke-Step "Packaging the VSIX (vsce package --target $Target)" {
        npx vsce package --target $Target --out $OutputPath
    }
}
finally {
    Pop-Location
}

$vsix = Get-Item $OutputPath
Write-Host ""
Write-Host "==> Done: $($vsix.FullName) ($([math]::Round($vsix.Length / 1MB, 1)) MB)" -ForegroundColor Green
