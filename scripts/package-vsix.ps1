#!/usr/bin/env pwsh
<#
.SYNOPSIS
    DESIGN.md §7 / M7 packaging (D7 items 1 and 6): builds the VSIX end to end, from a
    clean checkout, in one command.

.DESCRIPTION
    1. Publishes TsqlDbg.Adapter self-contained for the target runtime into
       extension/bin/<rid>/ -- the exact path extension/src/extension.ts's
       TsqlDebugAdapterDescriptorFactory.defaultAdapterPath() resolves at runtime
       (extensionPath/bin/win-x64/TsqlDbg.Adapter.exe).
    2. Installs the extension's npm dependencies (npm ci) and builds the esbuild
       bundle (dist/extension.js).
    3. Packages the VSIX with @vscode/vsce (a devDependency, so this step needs no
       global tool install or network-interactive prompt).

.PARAMETER Configuration
    dotnet build configuration for the adapter publish. Default: Release.

.PARAMETER RuntimeIdentifier
    .NET RID for the self-contained adapter publish. Default: win-x64 (the only RID
    extension.ts's descriptor factory currently resolves; DESIGN.md's MVP scope).

.PARAMETER OutputPath
    Where to write the resulting .vsix. Default: <repo root>/tsql-step-debugger.vsix.

.EXAMPLE
    pwsh -File scripts/package-vsix.ps1
#>
[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$RuntimeIdentifier = "win-x64",
    [string]$OutputPath
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$adapterProject = Join-Path $repoRoot "src/TsqlDbg.Adapter"
$extensionDir = Join-Path $repoRoot "extension"
$adapterOut = Join-Path $extensionDir "bin/$RuntimeIdentifier"

if (-not $OutputPath) {
    $OutputPath = Join-Path $repoRoot "tsql-step-debugger.vsix"
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
    Invoke-Step "Installing extension dependencies (npm ci)" {
        npm ci
    }

    Invoke-Step "Building the extension bundle (npm run build)" {
        npm run build
    }

    Invoke-Step "Packaging the VSIX (vsce package)" {
        npx vsce package --out $OutputPath
    }
}
finally {
    Pop-Location
}

$vsix = Get-Item $OutputPath
Write-Host ""
Write-Host "==> Done: $($vsix.FullName) ($([math]::Round($vsix.Length / 1MB, 1)) MB)" -ForegroundColor Green
