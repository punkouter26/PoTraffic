# filepath: SCRIPTS/arg-governance.ps1
<#
.SYNOPSIS
    CI/CD rule #7 — Azure Resource Graph governance audit.

.DESCRIPTION
    Runs a battery of KQL queries against ARG to flag:
      1. Orphan assets (no Po naming prefix)
      2. Naming-convention violations (must start with 'app-potraffic-' for web apps,
         'kv-po' for Key Vaults, 'mi-po' for managed identities, etc.)
      3. Idle compute (< 5% average CPU over 7 days, non-production only)

.PARAMETER SubscriptionId
    The subscription to audit. Defaults to $env:AZURE_SUBSCRIPTION_ID.

.PARAMETER NonProductionOnly
    When $true (default), only flags issues outside 'PoShared' and 'PoTraffic-prod'
    resource groups. Use $false to audit everything (including production).

.PARAMETER IdleCpuThreshold
    The CPU-percentage threshold under which a resource is considered idle.
    Default: 5 (%).

.PARAMETER Days
    Lookback window for idle-CPU analysis. Default: 7 days.

.PARAMETER OutputJson
    Optional path to write the findings as JSON.

.EXAMPLE
    pwsh ./SCRIPTS/arg-governance.ps1 -SubscriptionId bbb8dfbe-... -OutputJson ./artifacts/arg-findings.json
#>
[CmdletBinding()]
param(
    [Parameter()][string]$SubscriptionId = $env:AZURE_SUBSCRIPTION_ID,
    [Parameter()][switch]$NonProductionOnly = $true,
    [Parameter()][double]$IdleCpuThreshold = 5.0,
    [Parameter()][int]$Days = 7,
    [Parameter()][string]$OutputJson
)

$ErrorActionPreference = 'Stop'

if (-not $SubscriptionId) {
    throw "SubscriptionId is required (env AZURE_SUBSCRIPTION_ID or -SubscriptionId)"
}

# ── Naming-convention rules (CI/CD rule #6 + #7) ────────────────────────────
# PoTraffic Po naming convention:
#   Microsoft.Web/sites            → app-potraffic-<env>-<region>-<seq>
#   Microsoft.KeyVault/vaults      → kv-<shared-or-solution>
#   Microsoft.ManagedIdentity/...  → mi-poshared-...
#   Microsoft.Storage/storageAccounts → st<solution>...
#   PoShared hub: rg = PoShared; everything else: rg = PoTraffic
$allowedPrefixes = @{
    'microsoft.web/sites'                  = 'app-potraffic-'
    'microsoft.keyvault/vaults'            = 'kv-po'
    'microsoft.managedidentity/userassignedidentities' = 'mi-po'
    'microsoft.storage/storageaccounts'    = 'stpotraffic'
}

# ── Query 1: naming violations across the subscription ───────────────────────
$namingQuery = @"
Resources
| where type in~ (${($allowedPrefixes.Keys | ForEach-Object { "'$_'" }) -join ','})
| extend expectedPrefix = case(
    type =~ 'microsoft.web/sites', 'app-potraffic-',
    type =~ 'microsoft.keyvault/vaults', 'kv-po',
    type =~ 'microsoft.managedidentity/userassignedidentities', 'mi-po',
    type =~ 'microsoft.storage/storageaccounts', 'stpotraffic',
    '')
| where resourceGroup !~ '^(?:PoShared|PoShared-AI|PoShared-Build)$'  // exempt the central hub
| where isnotempty(expectedPrefix)
| where not(startswith(tolower(name), tolower(expectedPrefix)))
| project name, type, resourceGroup, subscriptionId, expectedPrefix
| limit 500
"@

Write-Host "→ Query 1: naming convention violations..." -ForegroundColor Cyan
$namingFindings = az graph query -q $namingQuery --subscriptions $SubscriptionId -o json | ConvertFrom-Json

# ── Query 2: orphan assets (not Po-prefixed and not in PoShared) ─────────────
$orphanQuery = @"
Resources
| where resourceGroup !~ '^(?:PoShared|PoShared-AI|PoShared-Build|PoShared-Network)$'
| where not(startswith(tolower(name), 'po'))
| project name, type, resourceGroup, location, tags
| limit 200
"@

Write-Host "→ Query 2: orphan assets..." -ForegroundColor Cyan
$orphanFindings = az graph query -q $orphanQuery --subscriptions $SubscriptionId -o json | ConvertFrom-Json

# ── Query 3: idle compute in non-production ─────────────────────────────────
$endTime = (Get-Date).ToUniversalTime()
$startTime = $endTime.AddDays(-1 * $Days)
$idleQuery = @"
Perf
| where TimeGenerated between (datetime('$($startTime.ToString('o'))') .. datetime('$($endTime.ToString('o'))'))
| where CounterName == '\Processor Information(_Total)\% Processor Utility'
    or CounterName == '\Processor(_Total)\% Processor Time'
| summarize avgCpu = avg(CounterValue), p95Cpu = percentile(CounterValue, 95) by ResourceId
| join kind=inner (
    Resources
    | where type =~ 'microsoft.compute/virtualmachines'
    | project ResourceId = id, name, resourceGroup, tags
) on ResourceId
| where resourceGroup !~ '^PoTraffic-prod$'
| where avgCpu < $IdleCpuThreshold
| project name, resourceGroup, avgCpu = round(avgCpu, 2), p95Cpu = round(p95Cpu, 2)
| limit 200
"@

Write-Host "→ Query 3: idle compute (avg CPU < $IdleCpuThreshold% over ${Days}d, non-prod)..." -ForegroundColor Cyan
$idleFindings = az graph query -q $idleQuery --subscriptions $SubscriptionId -o json | ConvertFrom-Json

# ── Triage: produce a single findings document ───────────────────────────────
$report = [pscustomobject]@{
    subscriptionId         = $SubscriptionId
    generatedUtc           = (Get-Date).ToUniversalTime().ToString('o')
    idleCpuThreshold       = $IdleCpuThreshold
    lookbackDays           = $Days
    nonProductionOnly      = [bool]$NonProductionOnly
    namingViolationsCount  = ($namingFindings.data | Measure-Object).Count
    orphanAssetsCount      = ($orphanFindings.data | Measure-Object).Count
    idleComputeCount       = ($idleFindings.data | Measure-Object).Count
    namingViolations       = $namingFindings.data
    orphanAssets           = $orphanFindings.data
    idleCompute            = $idleFindings.data
}

if ($OutputJson) {
    $dir = Split-Path -Parent $OutputJson
    if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    $report | ConvertTo-Json -Depth 6 | Set-Content -Path $OutputJson -Encoding UTF8
    Write-Host "✓ Wrote findings → $OutputJson" -ForegroundColor Green
}

# ── Console summary ─────────────────────────────────────────────────────────
Write-Host "`n=== ARG Governance Summary ===" -ForegroundColor Magenta
Write-Host ("  Naming violations: {0}" -f $report.namingViolationsCount)
Write-Host ("  Orphan assets:     {0}" -f $report.orphanAssetsCount)
Write-Host ("  Idle compute:      {0}" -f $report.idleComputeCount)

# Non-zero exit when any of the three checks fired — blocks the pipeline.
$total = $report.namingViolationsCount + $report.orphanAssetsCount + $report.idleComputeCount
if ($total -gt 0) {
    Write-Host "`n❌ Governance gate FAILED — $total issue(s) found." -ForegroundColor Red
    exit 1
}
Write-Host "`n✓ Governance gate passed." -ForegroundColor Green
exit 0