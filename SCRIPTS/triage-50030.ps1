# filepath: SCRIPTS/triage-50030.ps1
<#
.SYNOPSIS
    Diagnose a 500.30 (ANCM host startup failure) on PoTraffic.Api.

.DESCRIPTION
    Captures the four telemetry channels you need to determine *why* the host
    died, then prints them inline so the operator can decide on the fix:

      1. App Service instance state + recent container restarts.
      2. App Service application-log filesystem files (EventLog.xml + stdout).
      3. Role assignments on the storage account (verify RBAC propagation).
      4. Live probe of /health, /health/ready, and the root URL.

    The script never mutates anything — it is safe to run repeatedly. Print a
    single-line "EXEC" command for each remediation the operator can run by
    hand (role assignment, az webapp restart, etc.).

.PARAMETER WebAppName
    Name of the App Service (default: potraffic-api-win).

.PARAMETER ResourceGroup
    Resource group containing the App Service (default: PoTraffic).

.PARAMETER StorageAccount
    Storage account name to inspect RBAC on (default: potrafficstorage).

.PARAMETER OutputDir
    Directory to write log artifacts to (default: ./artifacts/triage).

.EXAMPLE
    pwsh ./SCRIPTS/triage-50030.ps1

.EXAMPLE
    pwsh ./SCRIPTS/triage-50030.ps1 -WebAppName potraffic-api-win -ResourceGroup PoTraffic
#>
[CmdletBinding()]
param(
    [Parameter()][string]$WebAppName      = 'potraffic-api-win',
    [Parameter()][string]$ResourceGroup   = 'PoTraffic',
    [Parameter()][string]$StorageAccount  = 'potrafficstorage',
    [Parameter()][string]$OutputDir       = "./artifacts/triage"
)

$ErrorActionPreference = 'Continue'

# ── Preflight ────────────────────────────────────────────────────────────────
if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    Write-Error "Azure CLI ('az') is not on PATH. Install via winget or https://aka.ms/InstallAzureCLIMacOS."
    exit 2
}

if (-not (Test-Path $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
}

$stamp  = Get-Date -Format 'yyyyMMdd-HHmmss'
$runDir = Join-Path $OutputDir $stamp
New-Item -ItemType Directory -Path $runDir -Force | Out-Null

Write-Host "=== PoTraffic · 500.30 Triage ===" -ForegroundColor Magenta
Write-Host "WebApp:           $WebAppName"
Write-Host "ResourceGroup:    $ResourceGroup"
Write-Host "StorageAccount:   $StorageAccount"
Write-Host "Artifacts:        $runDir"
Write-Host ""

function Step {
    param([string]$Title, [scriptblock]$Body)
    Write-Host "── $Title ──" -ForegroundColor Cyan
    try { & $Body }
    catch { Write-Host "  ⚠ $($_.Exception.Message)" -ForegroundColor Yellow }
    Write-Host ""
}

# ── 1. App Service state + container logs ───────────────────────────────────
Step "1. App Service instance state" {
    $state = az webapp show -n $WebAppName -g $ResourceGroup `
        --query "{state:state, defaultHostName:defaultHostName, kind:kind, linuxFxVersion:siteConfig.linuxFxVersion, alwaysOn:siteConfig.alwaysOn}" -o json 2>$null
    if ($state) {
        $state | ConvertFrom-Json | Format-List | Out-String -Stream | ForEach-Object { Write-Host "  $_" }
    } else {
        Write-Host "  ⚠ Could not query App Service state (network/auth)." -ForegroundColor Yellow
    }
}

# ── 2. Pull log artifacts ────────────────────────────────────────────────────
Step "2. Pull application logs (filesystem)" {
    $cfg = az webapp log config -n $WebAppName -g $ResourceGroup --query "{app:applicationLogs.fileSystem.level, detailed:detailedErrorMessages.enabled, failed:httpLogs.fileSystem.enabled, docker:logsDirectory}" -o json 2>$null
    if ($cfg) { Write-Host "  Current logging config: $cfg" }

    $zipPath = Join-Path $runDir "logs.zip"
    az webapp log download -n $WebAppName -g $ResourceGroup --log-file $zipPath --only-show-errors 2>$null | Out-Null
    if (Test-Path $zipPath) {
        Expand-Archive -Path $zipPath -DestinationPath $runDir -Force
        Write-Host "  Logs extracted to: $runDir" -ForegroundColor Green

        # Highlight lines with 500.30 / HostingStartup / AuthorizationPermissionMismatch
        Get-ChildItem -Path $runDir -Recurse -Include *.log,*.txt,*.xml -ErrorAction SilentlyContinue |
            Select-String -Pattern '500\.30|HostingStartupException|AuthorizationPermissionMismatch|StorageAuthzDenied|HydrationFailed|HydrateAsync' -List |
            ForEach-Object {
                Write-Host "  ▶ $($_.Path):$($_.LineNumber)  $($_.Line.Trim())" -ForegroundColor Yellow
            }
    } else {
        Write-Host "  ⚠ No logs.zip returned. Enable logging first:" -ForegroundColor Yellow
        Write-Host "      az webapp log config -n $WebAppName -g $ResourceGroup \`"
        Write-Host "        --application-logging filesystem --detailed-error-messages true \`"
        Write-Host "        --web-server-logging filesystem --failed-request-tracing true \`"
        Write-Host "        --docker-container-logging filesystem"
    }
}

# ── 3. Storage RBAC inspection ──────────────────────────────────────────────
Step "3. Storage RBAC for '$StorageAccount'" {
    $subId = az account show --query id -o tsv 2>$null
    if (-not $subId) { Write-Host "  ⚠ Not logged in (az account show returned nothing)." -ForegroundColor Yellow; return }
    $scope = "/subscriptions/$subId/resourceGroups/$ResourceGroup/providers/Microsoft.Storage/storageAccounts/$StorageAccount"
    $assignments = az role assignment list --scope $scope --query '[?roleDefinitionName==''Storage Table Data Contributor''].{Principal:principalName, Type:principalType, Assigned:createdOn, Id:principalId}' -o json 2>$null
    if ($assignments -and $assignments -ne '[]') {
        $assignments | ConvertFrom-Json | Format-Table -AutoSize | Out-String -Stream | ForEach-Object { Write-Host "  $_" }
        Write-Host "  ✅ 'Storage Table Data Contributor' is present." -ForegroundColor Green
    } else {
        Write-Host "  ❌ NO 'Storage Table Data Contributor' role on $StorageAccount." -ForegroundColor Red
        Write-Host "  Remediation:" -ForegroundColor Yellow
        Write-Host "    1. Find the App Service principal IDs:"
        Write-Host "         `$ua = az identity show -g rg-poshared -n mi-poshared-default --query principalId -o tsv"
        Write-Host "         `$sa = az webapp identity show -n $WebAppName -g $ResourceGroup --query principalId -o tsv"
        Write-Host "    2. Grant both:"
        Write-Host "         az role assignment create --assignee-object-id `$ua --role 'Storage Table Data Contributor' --scope $scope --assignee-principal-type ServicePrincipal"
        Write-Host "         az role assignment create --assignee-object-id `$sa --role 'Storage Table Data Contributor' --scope $scope --assignee-principal-type ServicePrincipal"
        Write-Host "    3. Wait 3–5 minutes for RBAC propagation, then:"
        Write-Host "         az webapp restart -n $WebAppName -g $ResourceGroup"
    }
}

# ── 4. Live probe ───────────────────────────────────────────────────────────
Step "4. Live probe" {
    $fqdn = az webapp show -n $WebAppName -g $ResourceGroup --query defaultHostName -o tsv 2>$null
    if (-not $fqdn) { Write-Host "  ⚠ Could not resolve hostname." -ForegroundColor Yellow; return }

    $base = "https://$fqdn"
    foreach ($path in @('/health', '/health/ready', '/')) {
        try {
            $resp = Invoke-WebRequest -Uri "$base$path" -UseBasicParsing -SkipCertificateCheck -TimeoutSec 30
            $body = $resp.Content.Substring(0, [Math]::Min(120, $resp.Content.Length))
            Write-Host "  GET $path → $($resp.StatusCode)  ${body}…" -ForegroundColor $(if ($resp.StatusCode -eq 200) { 'Green' } else { 'Yellow' })
        }
        catch {
            $status = $_.Exception.Response.StatusCode.value__
            Write-Host "  GET $path → $status  $($_.Exception.Message)" -ForegroundColor Red
        }
    }
}

# ── 5. Tail live logs (background) ───────────────────────────────────────────
Write-Host "── 5. Tail live logs (Ctrl-C to stop) ──" -ForegroundColor Cyan
az webapp log tail -n $WebAppName -g $ResourceGroup 2>$null

Write-Host ""
Write-Host "=== Triage artifacts in $runDir ===" -ForegroundColor Magenta
Write-Host "Next step: review highlighted lines, then run the suggested remediation command."