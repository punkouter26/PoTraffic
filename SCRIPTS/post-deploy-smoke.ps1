# filepath: SCRIPTS/post-deploy-smoke.ps1
<#
.SYNOPSIS
    CI/CD rule #9 — Post-deployment smoke validation.

.DESCRIPTION
    Runs three browser-style smoke checks against a freshly-deployed PoTraffic.Api
    instance. Exits non-zero if any check fails — the deploy job marks red.

    1. /health → expect 200 + "Healthy" / "Degraded" JSON body
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

# ── Check 1: /health ─────────────────────────────────────────────────────────
try {
    $resp = Invoke-WebRequest -Uri "$BaseUrl/health" -UseBasicParsing -SkipCertificateCheck -TimeoutSec 30
    if ($resp.StatusCode -eq 200) {
        $body = ($resp.Content | ConvertFrom-Json -ErrorAction SilentlyContinue)
        $status = $body.status ?? '(no status field)'
        Run-Check 'health' 'Pass' "HTTP 200 status=$status"
    }
    else {
        Run-Check 'health' 'Fail' "HTTP $($resp.StatusCode) (expected 200)"
    }
}
catch {
    Run-Check 'health' 'Fail' $_.Exception.Message
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
        Run-Check 'health/ready' 'Fail' "Still 503 after 60s — hydration likely failing. Run ./SCRIPTS/triage-50030.ps1"
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

# ── Check 2b: blazor.boot.json (Fix #5) ─────────────────────────────────────
# Confirms the WASM runtime manifest is reachable. In the deploy that
# produced NotFoundPage for every route, the manifest returned 401 and the
# client assembly never loaded. Probing this catches the regression class
# before users do.
try {
    $bootResp = Invoke-WebRequest -Uri "$BaseUrl/_framework/blazor.boot.json" -UseBasicParsing -SkipCertificateCheck -TimeoutSec 15
    if ($bootResp.StatusCode -ne 200) {
        Run-Check 'blazor-boot' 'Fail' "HTTP $($bootResp.StatusCode) — WASM client cannot boot (manifest unreachable)"
    }
    else {
        $boot = $bootResp.Content | ConvertFrom-Json -ErrorAction SilentlyContinue
        $entry = $boot.entryAssembly
        $assetCount = $boot.totalAssets
        if ($entry -ne 'PoTraffic.Client.dll') {
            Run-Check 'blazor-boot' 'Fail' "entryAssembly=$entry expected=PoTraffic.Client.dll"
        }
        elseif (-not $assetCount -or $assetCount -lt 5) {
            Run-Check 'blazor-boot' 'Fail' "totalAssets=$assetCount (too few — manifest looks incomplete)"
        }
        else {
            Run-Check 'blazor-boot' 'Pass' "entry=$entry totalAssets=$assetCount manifestLen=$($bootResp.Content.Length)"
        }
    }
}
catch {
    Run-Check 'blazor-boot' 'Fail' $_.Exception.Message
}

# ── Check 2b: blazor.boot.json (Fix #5) ─────────────────────────────────────
# Confirms the WASM runtime manifest is reachable. In the deploy that
# produced NotFoundPage for every route, the manifest returned 401 and the
# client assembly never loaded. Probing this catches the regression class
# before users do.
try {
    $bootResp = Invoke-WebRequest -Uri "$BaseUrl/_framework/blazor.boot.json" -UseBasicParsing -SkipCertificateCheck -TimeoutSec 15
    if ($bootResp.StatusCode -ne 200) {
        Run-Check 'blazor-boot' 'Fail' "HTTP $($bootResp.StatusCode) — WASM client cannot boot (manifest unreachable)"
    }
    else {
        $boot = $bootResp.Content | ConvertFrom-Json -ErrorAction SilentlyContinue
        $entry = $boot.entryAssembly
        $assetCount = $boot.totalAssets
        if ($entry -ne 'PoTraffic.Client.dll') {
            Run-Check 'blazor-boot' 'Fail' "entryAssembly=$entry expected=PoTraffic.Client.dll"
        }
        elseif (-not $assetCount -or $assetCount -lt 5) {
            Run-Check 'blazor-boot' 'Fail' "totalAssets=$assetCount (too few — manifest looks incomplete)"
        }
        else {
            Run-Check 'blazor-boot' 'Pass' "entry=$entry totalAssets=$assetCount manifestLen=$($bootResp.Content.Length)"
        }
    }
}
catch {
    Run-Check 'blazor-boot' 'Fail' $_.Exception.Message
}
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