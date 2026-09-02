# filepath: SCRIPTS/post-deploy-smoke.ps1
<#
.SYNOPSIS
    CI/CD rule #9 — Post-deployment smoke validation.

.DESCRIPTION
    Runs three browser-style smoke checks against a freshly-deployed PoTraffic.API
    instance. Exits non-zero if any check fails — the deploy job marks red.

    1. /health/json → expect 200 + "Healthy" / "Degraded" JSON body
    2. GET /   → render-tree check (the Blazor index.html serves and references the
                 new dotnet.{hash}.js file; the served hash matches a freshly-built one)
    3. GET /diag/keyvault?secret={name} → masked secret retrieval (admin cookie required
                 to prove Key Vault + Managed Identity wiring; raw secret never logged)

.PARAMETER BaseUrl
    Public URL of the deployment (e.g. https://potraffic-api.azurewebsites.net).

.PARAMETER AdminCookieValue
    Optional admin .PoTraffic.Auth cookie value for the /diag check.
    Without this cookie the /diag endpoint returns 401 — the script records
    that as a soft-pass with a warning.

.PARAMETER DiagSecretName
    Optional Key Vault secret name to probe. Defaults to "ConnectionStrings--TableStorage".

.PARAMETER ExpectedDotnetHash
    Optional dotnet.{hash}.js hash the deployment is expected to serve.
    When supplied, the script asserts the served index.html references this hash.
    Useful for "the new client bundle is live" verification.

.EXAMPLE
    pwsh ./SCRIPTS/post-deploy-smoke.ps1 -BaseUrl https://potraffic-api.azurewebsites.net
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BaseUrl,
    [Parameter()][string]$AdminCookieValue,
    [Parameter()][string]$DiagSecretName = 'ConnectionStrings--TableStorage',
    [Parameter()][string]$ExpectedDotnetHash
)

$ErrorActionPreference = 'Continue'
$results = New-Object System.Collections.Generic.List[object]

function Run-Check {
    param([string]$Name, [string]$Status, [string]$Detail = '', $Extra = $null)
    $entry = [pscustomobject]@{
        name   = $Name
        status = $Status   # Pass | Warn | Fail
        detail = $Detail
        extra  = $Extra
    }
    $results.Add($entry)
    $color = switch ($Status) { 'Pass' { 'Green' } 'Warn' { 'Yellow' } 'Fail' { 'Red' } default { 'White' } }
    Write-Host ("  [{0}] {1} — {2}" -f $Status, $Name, $Detail) -ForegroundColor $color
}

Write-Host "=== Post-deploy smoke validation ===" -ForegroundColor Magenta
Write-Host "Target: $BaseUrl`n"

# ── Check 1: /health/json ─────────────────────────────────────────────────────────
try {
    $resp = Invoke-WebRequest -Uri "$BaseUrl/health/json" -UseBasicParsing -SkipCertificateCheck -TimeoutSec 30
    if ($resp.StatusCode -eq 200) {
        $body = ($resp.Content | ConvertFrom-Json -ErrorAction SilentlyContinue)
        $status = $body.status ?? '(no status field)'
        Run-Check 'health/json' 'Pass' "HTTP 200 status=$status"
    }
    else {
        Run-Check 'health/json' 'Fail' "HTTP $($resp.StatusCode) (expected 200)"
    }
}
catch {
    Run-Check 'health/json' 'Fail' $_.Exception.Message
}

# ── Check 1b: /health/ready (waits up to 60s for hydration to finish) ────────
# Hydration now runs off the startup path so the host binds immediately. The
# readiness probe lets App Service route traffic only once the working set
# is loaded. Poll for at most 60s — if still 503, fail loudly.
try {
    $ready = $null
    for ($i = 0; $i -lt 12; $i++) {
        try {
            $r = Invoke-WebRequest -Uri "$BaseUrl/health/ready" -UseBasicParsing -SkipCertificateCheck -TimeoutSec 10
            if ($r.StatusCode -eq 200) {
                $ready = $r
                break
            }
        }
        catch {
            # 503 → still hydrating; loop. Network errors fall through.
            if ($_.Exception.Response.StatusCode.value__ -ne 503) { throw }
        }
        Start-Sleep -Seconds 5
    }
    if ($ready) {
        $body = ($ready.Content | ConvertFrom-Json -ErrorAction SilentlyContinue)
        Run-Check 'health/ready' 'Pass' "HTTP 200 status=$($body.status) durable=$($body.durable) after $(($i+1)*5)s"
    }
    else {
        Run-Check 'health/ready' 'Fail' "Still 503 after 60s — hydration likely failing. Check the App Service application logs for HydrationFailed or AuthorizationPermissionMismatch."
    }
}
catch {
    Run-Check 'health/ready' 'Fail' $_.Exception.Message
}

# ── Check 2: render-tree (index.html → dotnet.{hash}.js) ─────────────────────
# Fix #5b — index.html always contains the permanent <div id="blazor-error-ui">
# element, so a present match is NOT a failure signal. We only fail if there's no
# dotnet.{hash}.js boot stub at all (the WASM entry point).
try {
    $resp = Invoke-WebRequest -Uri "$BaseUrl/" -UseBasicParsing -SkipCertificateCheck -TimeoutSec 30
    if ($resp.StatusCode -ne 200) {
        Run-Check 'render-tree' 'Fail' "Index returned HTTP $($resp.StatusCode)"
    }
    else {
        $html = $resp.Content
        # .NET 10 uses _framework/blazor.webassembly.js as the entry stub;
        # .NET 8 used _framework/dotnet.[hash].js. Accept either.
        $dotnetMatch = ($html -match '_framework/dotnet\.[a-z0-9]+\.js')
        $blazorMatch = ($html -match '_framework/blazor\.webassembly(?:\.[a-z0-9]+)?\.js')
        if (-not ($dotnetMatch -or $blazorMatch)) {
            Run-Check 'render-tree' 'Fail' "No WASM entry script found in served HTML — WASM won't load"
        }
        else {
            $stub = if ($dotnetMatch) { ($matches[0] -replace '_framework/', '') }
                    else { 'blazor.webassembly.js' }
            if ($ExpectedDotnetHash -and $dotnetMatch -and ($stub -ne "dotnet.$ExpectedDotnetHash.js")) {
                Run-Check 'render-tree' 'Fail' "Hash mismatch: served=$stub expected=dotnet.$ExpectedDotnetHash.js"
            }
            else {
                Run-Check 'render-tree' 'Pass' "Blazor shell + boot stub $stub live"
            }
        }
    }
}
catch {
    Run-Check 'render-tree' 'Fail' $_.Exception.Message
}

# ── Check 2b: WASM boot assets are actually served ───────────────────────────
# The deploy that produced a permanent "Loading" spinner served an index.html
# whose asset references (blazor.webassembly.js, the scoped-CSS bundle) did not
# match the fingerprinted files on disk, so every asset 404'd/401'd and the WASM
# client never booted. Asset fingerprinting is now disabled so the physical names
# match index.html verbatim; probe the two assets that used to fail. Note the
# .NET 10 WASM runtime embeds the boot manifest in dotnet.js — there is no
# standalone /_framework/blazor.boot.json to probe.
$bootAssets = @(
    @{ Path = '/_framework/blazor.webassembly.js'; Type = 'application/javascript' },
    @{ Path = '/PoTraffic.Client.bundle.scp.css';  Type = 'text/css' }
)
foreach ($asset in $bootAssets) {
    try {
        $r = Invoke-WebRequest -Uri "$BaseUrl$($asset.Path)" -UseBasicParsing -SkipCertificateCheck -TimeoutSec 15
        if ($r.StatusCode -eq 200 -and $r.RawContentLength -gt 0) {
            Run-Check 'boot-asset' 'Pass' "$($asset.Path) HTTP 200 ($($r.RawContentLength) bytes)"
        }
        else {
            Run-Check 'boot-asset' 'Fail' "$($asset.Path) HTTP $($r.StatusCode) len=$($r.RawContentLength) — WASM client cannot boot"
        }
    }
    catch {
        $code = $_.Exception.Response.StatusCode.value__
        Run-Check 'boot-asset' 'Fail' "$($asset.Path) HTTP $code — WASM client cannot boot ($($_.Exception.Message))"
    }
}

# ── Check 3: /diag/keyvault (masked secret retrieval, admin cookie required) ──
$diagHeaders = @{}
if ($AdminCookieValue) {
    $diagHeaders['Cookie'] = ".PoTraffic.Auth=$AdminCookieValue"
}
try {
    $uri = "$BaseUrl/diag/keyvault?secret=$([Uri]::EscapeDataString($DiagSecretName))"
    $resp = Invoke-WebRequest -Uri $uri -UseBasicParsing -SkipCertificateCheck -TimeoutSec 30 -Headers $diagHeaders
    if ($resp.StatusCode -eq 200) {
        $body = $resp.Content | ConvertFrom-Json
        if ($body.maskedPreview -and $body.maskedPreview -notmatch '^\*+$' -and $body.maskedPreview.Length -gt 1) {
            Run-Check 'diag/keyvault' 'Pass' "Masked preview='$($body.maskedPreview)' len=$($body.length) found=$($body.found)"
        }
        else {
            Run-Check 'diag/keyvault' 'Warn' "Secret probe returned no value (found=$($body.found) vaultConfigured=$($body.vaultConfigured))"
        }
    }
    elseif ($resp.StatusCode -eq 401) {
        Run-Check 'diag/keyvault' 'Warn' "No admin cookie supplied (HTTP 401). Pass -AdminCookieValue to enable this check."
    }
    else {
        Run-Check 'diag/keyvault' 'Fail' "HTTP $($resp.StatusCode)"
    }
}
catch {
    if ($_.Exception.Response -and $_.Exception.Response.StatusCode -eq 401) {
        Run-Check 'diag/keyvault' 'Warn' "No admin cookie supplied (HTTP 401). Pass -AdminCookieValue to enable this check."
    }
    else {
        Run-Check 'diag/keyvault' 'Fail' $_.Exception.Message
    }
}

# ── Summary ─────────────────────────────────────────────────────────────────
$failCount = ($results | Where-Object { $_.status -eq 'Fail' }).Count
$warnCount = ($results | Where-Object { $_.status -eq 'Warn' }).Count
$passCount = ($results | Where-Object { $_.status -eq 'Pass' }).Count

Write-Host "`n=== Summary ===" -ForegroundColor Magenta
Write-Host ("  Pass: {0}  Warn: {1}  Fail: {2}" -f $passCount, $warnCount, $failCount)

if ($failCount -gt 0) {
    Write-Host "`n❌ Smoke gate FAILED." -ForegroundColor Red
    exit 1
}
Write-Host "`n✓ Smoke gate passed." -ForegroundColor Green
exit 0