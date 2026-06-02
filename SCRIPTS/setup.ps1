# setup.ps1 — First-time local development setup.
# Idempotent — safe to re-run on a fresh checkout (Rule 9, Rule 10 — First-Run Success).
#
# Steps:
#   1. Verify .NET 10 SDK is installed (per global.json).
#   2. Verify Docker Desktop is installed and running.
#   3. Verify Azure CLI is installed.
#   4. Check `az login` status and remind the user to log in to access Key Vault.
#   5. Start Azurite via docker-compose.
#   6. Restore + build the solution.

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
Set-Location $repoRoot

function Step($n, $msg) {
    Write-Host "`n[$n] $msg" -ForegroundColor Cyan
}

function Ok($msg)  { Write-Host "  ✅ $msg" -ForegroundColor Green }
function Warn($msg){ Write-Host "  ⚠️  $msg" -ForegroundColor Yellow }
function Err($msg) { Write-Host "  ❌ $msg" -ForegroundColor Red }

Step '1/6' 'Verifying .NET 10 SDK...'
try {
    $dotnetVersion = (& dotnet --version 2>$null).Trim()
    if ($dotnetVersion -notmatch '^10\.') {
        Warn "Detected .NET $dotnetVersion — global.json expects 10.x."
        Warn 'Install via winget: winget install Microsoft.DotNet.SDK.10'
    } else {
        Ok "dotnet $dotnetVersion"
    }
} catch {
    Err 'dotnet not found on PATH.'
    Warn 'Install via winget: winget install Microsoft.DotNet.SDK.10'
}

Step '2/6' 'Verifying Docker Desktop...'
try {
    $dockerVersion = (& docker --version 2>$null).Trim()
    Ok $dockerVersion
    $running = (& docker info 2>$null) -match 'Server Version'
    if (-not $running) {
        Warn 'Docker daemon is not running. Start Docker Desktop, then re-run setup.ps1.'
    } else {
        Ok 'Docker daemon is up.'
    }
} catch {
    Err 'docker not found on PATH.'
    Warn 'Install via winget: winget install Docker.DockerDesktop'
}

Step '3/6' 'Verifying Azure CLI...'
try {
    $azVersion = (& az --version 2>$null | Select-Object -First 1).Trim()
    Ok $azVersion
} catch {
    Warn 'Azure CLI not found. Install via winget: winget install Microsoft.AzureCLI'
}

Step '4/6' 'Checking `az login` status...'
try {
    $azAccount = (& az account show --query "name" -o tsv 2>$null).Trim()
    if ([string]::IsNullOrWhiteSpace($azAccount)) {
        Warn 'Not logged in. Run `az login` to access Key Vault (kv-poshared).'
    } else {
        Ok "Logged in to subscription: $azAccount"
        try {
            $kv = (& az keyvault show --name kv-poshared --query "name" -o tsv 2>$null).Trim()
            if ($kv) { Ok "Key Vault accessible: $kv" }
            else { Warn 'Key Vault kv-poshared not visible — verify your access policies.' }
        } catch {
            Warn 'Key Vault kv-poshared not accessible — verify your access policies.'
        }
    }
} catch {
    Warn 'az CLI not available — skipping auth check.'
}

Step '5/6' 'Starting Azurite (Table Storage emulator)...'
try {
    & docker compose up -d
    if ($LASTEXITCODE -eq 0) { Ok 'Azurite container started (ports 10000/10001/10002).' }
    else { Warn 'docker compose up failed — check Docker Desktop.' }
} catch {
    Warn 'Could not start Azurite container.'
}

Step '6/6' 'Restoring + building solution...'
try {
    & dotnet restore
    if ($LASTEXITCODE -ne 0) { throw 'restore failed' }
    Ok 'dotnet restore complete.'
    & dotnet build --no-restore -c Debug
    if ($LASTEXITCODE -ne 0) { throw 'build failed' }
    Ok 'dotnet build complete.'
} catch {
    Err 'Build failed. See output above.'
}

Write-Host "`n=== Setup complete ===" -ForegroundColor Green
Write-Host 'Next steps:'
Write-Host "  1. (Optional) Run `az login` if you need Key Vault access."
Write-Host "  2. Launch the app:  ./SCRIPTS/start-dev.ps1"
Write-Host "  3. Run the test suite: ./SCRIPTS/run-tests.ps1"
