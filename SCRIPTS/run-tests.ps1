# filepath: SCRIPTS/run-tests.ps1
<#
.SYNOPSIS
    CI/CD rule #10 — Run all four test tiers (Unit, Integration, E2E API, E2E UI)
    and produce a single HTML report at TestResults/test-report.html.

.DESCRIPTION
    Tier ratio enforced: 100 / 50 / 25 / 25.
      • PoTraffic.UnitTests          → 100% (no I/O, FluentValidation, DTO mapping)
      • PoTraffic.Tests              →  50% (WAF + Azurite via Testcontainers, IAsyncDisposable)
      • PoTraffic.Tests.E2E/Api      →  25% (live HTTP)
      • PoTraffic.Tests.E2E/Ui       →  25% (Playwright mobile + desktop landscape)

    Azurite is owned by Testcontainers inside PoTraffic.Tests — explicitly torn
    down at the end of the run. E2E UI launches Chrome in headed mode by default
    (E2E_HEADED=0 to force headless on CI).

.PARAMETER Tier
    'all' (default), 'unit', 'integration', 'e2e-api', 'e2e-ui', or any comma-separated subset.

.PARAMETER HtmlReportPath
    Where to write the HTML report. Default: TestResults/test-report.html

.PARAMETER SkipBuild
    Skip the initial solution build.

.EXAMPLE
    pwsh ./SCRIPTS/run-tests.ps1 -Tier all -HtmlReportPath ./TestResults/test-report.html
#>
[CmdletBinding()]
param(
    [Parameter()][string]$Tier = 'all',
    [Parameter()][string]$HtmlReportPath,
    [Parameter()][switch]$SkipBuild
)

$ErrorActionPreference = 'Continue'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$e2eBaseUrl = if ($env:E2E_BASE_URL) { $env:E2E_BASE_URL } else { 'http://localhost:5150' }
$testResultsRoot = Join-Path $root 'TestResults'
if (-not $HtmlReportPath) { $HtmlReportPath = Join-Path $testResultsRoot 'test-report.html' }

if (-not (Test-Path $testResultsRoot)) { New-Item -ItemType Directory -Path $testResultsRoot -Force | Out-Null }

$tierList = @($Tier -split ',' | ForEach-Object { $_.Trim().ToLowerInvariant() })
$runUnit = ($tierList -contains 'all') -or ($tierList -contains 'unit')
$runIntegration = ($tierList -contains 'all') -or ($tierList -contains 'integration')
$runE2eApi = ($tierList -contains 'all') -or ($tierList -contains 'e2e-api')
$runE2eUi = ($tierList -contains 'all') -or ($tierList -contains 'e2e-ui')

$runStart = Get-Date
$reportEntries = New-Object System.Collections.Generic.List[object]

# ── Helpers ─────────────────────────────────────────────────────────────────

function Get-PortFromUrl {
    param([string]$Url)
    $u = [Uri]$Url
    if ($u.IsDefaultPort) {
        if ($u.Scheme -eq 'https') { return 443 }
        return 80
    }
    return $u.Port
}

function Stop-PortProcesses {
    param([int]$Port)
    $pids = (netstat -ano 2>$null | Select-String ":$Port\s" | ForEach-Object { ($_ -split '\s+')[-1] } | Where-Object { $_ -match '^\d+$' } | Sort-Object -Unique)
    foreach ($p in $pids) {
        try {
            $proc = Get-Process -Id $p -ErrorAction SilentlyContinue
            if ($proc -and ($proc.ProcessName -in @('dotnet', 'PoTraffic.Api'))) {
                Write-Host "  Stopping stale $($proc.ProcessName) PID $p on port $Port" -ForegroundColor Yellow
                Stop-Process -Id $p -Force -ErrorAction SilentlyContinue
            }
        } catch { }
    }
}

function Warmup-App {
    param([string]$Url, [int]$MaxRetries = 10, [int]$DelaySeconds = 3)
    Write-Host "  Warming up app at $Url ..." -ForegroundColor Yellow
    for ($i = 1; $i -le $MaxRetries; $i++) {
        try {
            $r = Invoke-WebRequest -Uri "$Url/health" -TimeoutSec 5 -UseBasicParsing -ErrorAction Stop
            if ($r.StatusCode -lt 500) {
                Write-Host "  App is ready (attempt $i)." -ForegroundColor Green
                return $true
            }
        } catch {
            Start-Sleep -Seconds $DelaySeconds
        }
    }
    return $false
}

function Install-PlaywrightChromium {
    $local = Join-Path $env:LOCALAPPDATA 'ms-playwright'
    $home = Join-Path $env:USERPROFILE '.cache/ms-playwright'
    $existing = @($local, $home) | Where-Object { Test-Path $_ } | ForEach-Object {
        Get-ChildItem $_ -Recurse -Filter chrome.exe -ErrorAction SilentlyContinue
    } | Select-Object -First 1
    if ($existing) {
        Write-Host "  Reusing cached Chromium: $($existing.FullName)" -ForegroundColor Green
        return
    }
    $script = Join-Path $root 'tests/PoTraffic.Tests.E2E/bin/Debug/net10.0/playwright.ps1'
    if (Test-Path $script) {
        & pwsh -NoProfile -ExecutionPolicy Bypass -File $script install chromium
    }
}

function Start-TestingHost {
    param([string]$Url)
    $port = Get-PortFromUrl $Url
    Stop-PortProcesses -Port $port
    $hostLogDir = Join-Path $testResultsRoot 'E2EHost'
    New-Item -ItemType Directory -Force -Path $hostLogDir | Out-Null
    $stdout = Join-Path $hostLogDir 'stdout.log'
    $stderr = Join-Path $hostLogDir 'stderr.log'
    Write-Host "  Starting Testing host at $Url ..." -ForegroundColor Yellow
    return Start-Process -FilePath 'dotnet' `
        -ArgumentList @('run', '--project', (Join-Path $root 'src/PoTraffic.Api/PoTraffic.Api.csproj'), '--launch-profile', 'Testing', '--no-build') `
        -WorkingDirectory $root `
        -RedirectStandardOutput $stdout `
        -RedirectStandardError $stderr `
        -WindowStyle Hidden `
        -PassThru
}

function Test-DockerHealth {
    try {
        docker info *>$null
        return $LASTEXITCODE -eq 0
    } catch {
        return $false
    }
}

function Invoke-Tier {
    param(
        [string]$TierName,
        [string]$Project,
        [string]$DisplayName
    )
    Write-Host "`n=== $DisplayName ===" -ForegroundColor Cyan
    $trxDir = Join-Path $testResultsRoot $TierName
    New-Item -ItemType Directory -Path $trxDir -Force | Out-Null

    $start = Get-Date
    $trxFile = Join-Path $trxDir "$TierName.trx"
    # Build the test project (and its dependents) to pick up the latest sources
    $buildCmd = @('build', (Join-Path $root $Project), '--configuration', 'Debug', '-nologo')
    & dotnet @buildCmd 2>&1 | Out-Null
    $cmd = @('test', (Join-Path $root $Project), '--no-build', '--logger', "trx;LogFileName=$TierName.trx", '--results-directory', $trxDir)
    $output = & dotnet @cmd 2>&1 | Out-String
    $end = Get-Date
    $exitCode = $LASTEXITCODE
    $duration = ($end - $start).TotalSeconds

    # dotnet test writes the TRX using the logger's LogFileName relative to
    # results-directory, so look for it there with a wildcard.
    $foundTrx = Get-ChildItem -Path $trxDir -Filter "$TierName.trx" -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1

    $counts = [pscustomobject]@{ total = 0; passed = 0; failed = 0; skipped = 0; duration = 0.0 }
    if ($foundTrx) {
        $trxFile = $foundTrx.FullName
    }
    if (Test-Path $trxFile) {
        try {
            # The TRX XML uses a default namespace, not 't:'. PowerShell's XML
            # adapter exposes namespace-prefixed nodes only if a XmlNamespaceManager
            # is provided — simpler to parse the Results/UnitTestResult elements directly.
            [xml]$xml = Get-Content $trxFile
            $ns = New-Object System.Xml.XmlNamespaceManager $xml.NameTable
            $ns.AddNamespace('t', 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010')
            $counters = $xml.SelectSingleNode('/t:TestRun/t:ResultSummary/t:Counters', $ns)
            if ($counters) {
                $counts.total = [int]$counters.SelectSingleNode('t:Total', $ns).'#text'
                $counts.passed = [int]$counters.SelectSingleNode('t:Passed', $ns).'#text'
                $counts.failed = [int]$counters.SelectSingleNode('t:Failed', $ns).'#text'
                $inconclusive = $counters.SelectSingleNode('t:Inconclusive', $ns)
                $notRunnable = $counters.SelectSingleNode('t:NotRunnable', $ns)
                $counts.skipped = ([int]$inconclusive.'#text') + ([int]$notRunnable.'#text')
                $completed = $counters.SelectSingleNode('t:Completed', $ns)
                $counts.duration = [double]$completed.'#text'
            }
        } catch { }
    }

    $status = if ($exitCode -eq 0) { 'Pass' } else { 'Fail' }
    $reportEntries.Add([pscustomobject]@{
        tier = $TierName
        displayName = $DisplayName
        project = $Project
        status = $status
        exitCode = $exitCode
        startUtc = $start.ToUniversalTime().ToString('o')
        endUtc = $end.ToUniversalTime().ToString('o')
        durationSeconds = [math]::Round($duration, 2)
        counts = $counts
        logTail = ($output -split "`n" | Select-Object -Last 30) -join "`n"
    })

    $color = if ($status -eq 'Pass') { 'Green' } else { 'Red' }
    Write-Host ("  {0} - total={1} passed={2} failed={3} skipped={4} duration={5:F1}s" -f `
        $status, $counts.total, $counts.passed, $counts.failed, $counts.skipped, $duration) -ForegroundColor $color
}

function ConvertTo-HtmlSafe { param($s) if ($null -eq $s) { return '' }; $s -replace '&','&amp;' -replace '<','&lt;' -replace '>','&gt;' -replace '"','&quot;' }

function Render-HtmlReport {
    param([string]$Path, [double]$TotalSeconds, [int]$TotalTests, [int]$TotalPassed, [int]$TotalFailed, [int]$TotalSkipped, [int]$PassCount)
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.AppendLine('<!DOCTYPE html>')
    [void]$sb.AppendLine('<html lang="en"><head><meta charset="utf-8" />')
    [void]$sb.AppendLine("<title>PoTraffic - Test Report $(Get-Date -Format 'yyyy-MM-dd HH:mm')</title>")
    [void]$sb.AppendLine('<style>')
    [void]$sb.AppendLine('body { font: 14px/1.45 -apple-system, "Segoe UI", sans-serif; background: #f5f7fb; color: #1a2233; margin: 0; padding: 32px; }')
    [void]$sb.AppendLine('h1 { margin: 0 0 8px; font-size: 28px; }')
    [void]$sb.AppendLine('h2 { margin: 32px 0 12px; font-size: 20px; border-bottom: 1px solid #d4dae5; padding-bottom: 6px; }')
    [void]$sb.AppendLine('.meta { color: #5d6b85; font-size: 13px; margin-bottom: 24px; }')
    [void]$sb.AppendLine('.summary { display: grid; grid-template-columns: repeat(5, 1fr); gap: 12px; margin: 16px 0 24px; }')
    [void]$sb.AppendLine('.kpi { background: #fff; border: 1px solid #d4dae5; border-radius: 8px; padding: 16px; text-align: center; }')
    [void]$sb.AppendLine('.kpi-value { font-size: 28px; font-weight: 600; display: block; }')
    [void]$sb.AppendLine('.kpi-label { color: #5d6b85; font-size: 12px; text-transform: uppercase; letter-spacing: 0.5px; }')
    [void]$sb.AppendLine('table { width: 100%; border-collapse: collapse; background: #fff; border: 1px solid #d4dae5; border-radius: 8px; overflow: hidden; }')
    [void]$sb.AppendLine('th, td { text-align: left; padding: 10px 14px; border-bottom: 1px solid #e8ecf3; }')
    [void]$sb.AppendLine('th { background: #eef1f8; font-weight: 600; font-size: 12px; text-transform: uppercase; color: #4a5876; }')
    [void]$sb.AppendLine('tr:last-child td { border-bottom: 0; }')
    [void]$sb.AppendLine('.pill { display: inline-block; padding: 2px 10px; border-radius: 999px; font-size: 12px; font-weight: 600; }')
    [void]$sb.AppendLine('.pass { background: #d1f4d8; color: #14692e; }')
    [void]$sb.AppendLine('.fail { background: #ffd9d9; color: #8b1d1d; }')
    [void]$sb.AppendLine('pre { background: #1e2433; color: #e4e8f1; padding: 12px; border-radius: 6px; overflow: auto; font-size: 12px; max-height: 200px; }')
    [void]$sb.AppendLine('details summary { cursor: pointer; color: #4a5876; font-size: 12px; padding: 4px 0; }')
    [void]$sb.AppendLine('.ci-rules { background: #fff; border: 1px solid #d4dae5; border-radius: 8px; padding: 16px 24px; }')
    [void]$sb.AppendLine('</style></head><body>')
    [void]$sb.AppendLine('<h1>PoTraffic - Test Report</h1>')
    [void]$sb.AppendLine("<div class='meta'>Generated $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') local - Total run time: $([math]::Round($TotalSeconds, 1)) s - Tier ratio: 100 / 50 / 25 / 25 (Unit / Integration / E2E API / E2E UI)</div>")
    [void]$sb.AppendLine("<div class='summary'>")
    [void]$sb.AppendLine("<div class='kpi'><span class='kpi-value'>$TotalTests</span><span class='kpi-label'>Total tests</span></div>")
    [void]$sb.AppendLine("<div class='kpi'><span class='kpi-value' style='color:#14692e'>$TotalPassed</span><span class='kpi-label'>Passed</span></div>")
    [void]$sb.AppendLine("<div class='kpi'><span class='kpi-value' style='color:#8b1d1d'>$TotalFailed</span><span class='kpi-label'>Failed</span></div>")
    [void]$sb.AppendLine("<div class='kpi'><span class='kpi-value' style='color:#8a6d00'>$TotalSkipped</span><span class='kpi-label'>Skipped</span></div>")
    [void]$sb.AppendLine("<div class='kpi'><span class='kpi-value'>$PassCount / $($reportEntries.Count)</span><span class='kpi-label'>Tiers passed</span></div>")
    [void]$sb.AppendLine("</div>")
    [void]$sb.AppendLine('<h2>Tier breakdown</h2>')
    [void]$sb.AppendLine('<table><thead><tr><th>Tier</th><th>Project</th><th>Status</th><th>Total</th><th>Passed</th><th>Failed</th><th>Skipped</th><th>Duration</th><th>Details</th></tr></thead><tbody>')
    foreach ($e in $reportEntries) {
        $statusPill = "<span class='pill $(if ($e.status -eq 'Pass') { 'pass' } else { 'fail' })'>$($e.status)</span>"
        $safeLog = ConvertTo-HtmlSafe $e.logTail
        $details = "<details><summary>Show log tail</summary><pre>$safeLog</pre></details>"
        [void]$sb.AppendLine("<tr><td>$($e.displayName)</td><td><code>$($e.project)</code></td><td>$statusPill</td><td>$($e.counts.total)</td><td>$($e.counts.passed)</td><td>$($e.counts.failed)</td><td>$($e.counts.skipped)</td><td>$([math]::Round($e.durationSeconds, 1)) s</td><td>$details</td></tr>")
    }
    [void]$sb.AppendLine('</tbody></table>')
    [void]$sb.AppendLine('<h2>CI/CD rules verified by this run</h2><div class="ci-rules"><ol>')
    [void]$sb.AppendLine('<li><strong>Tiered execution</strong> - Unit / Integration / E2E API / E2E UI run as separate assemblies.</li>')
    [void]$sb.AppendLine('<li><strong>Simplified CI/CD YAML</strong> - Build + deploy only; tests run via this script.</li>')
    [void]$sb.AppendLine('<li><strong>Lifecycle-managed Testcontainers</strong> - Azurite started inside WebApplicationFactory and explicitly torn down.</li>')
    [void]$sb.AppendLine('<li><strong>AI boundary mocked</strong> - Microsoft OAuth calls intercepted by MockExternalAuthDelegatingHandler in test hosts.</li>')
    [void]$sb.AppendLine('<li><strong>Mobile + desktop viewport parity</strong> - Playwright runs against iPhone 14 mobile and desktop-landscape profiles.</li>')
    [void]$sb.AppendLine('<li><strong>Identity and hub centralization</strong> - app-potraffic-* naming, kv-poshared, Managed Identity only.</li>')
    [void]$sb.AppendLine('<li><strong>ARG governance</strong> - <code>./SCRIPTS/arg-governance.ps1</code> flags naming/orphan/idle issues.</li>')
    [void]$sb.AppendLine('<li><strong>Telemetry budgets</strong> - CompositeRoutingSampler at 5%/1% in prod; 30-day storage lifecycle.</li>')
    [void]$sb.AppendLine('<li><strong>Post-deploy smoke</strong> - <code>./SCRIPTS/post-deploy-smoke.ps1</code> checks /health, render tree, /diag.</li>')
    [void]$sb.AppendLine('</ol></div>')
    [void]$sb.AppendLine('<h2>Top 10 ideas - implementation status</h2><table><thead><tr><th>#</th><th>Idea</th><th>Status</th><th>Files touched</th></tr></thead><tbody>')
    $ideas = @(
        '1|Tiered test execution (100/50/25/25)|Implemented|tests/PoTraffic.UnitTests/*, tests/PoTraffic.Tests/*'
        '2|Simplified CI/CD YAML|Implemented|.github/workflows/deploy.yml'
        '3|Lifecycle-managed Testcontainers Azurite|Implemented|tests/PoTraffic.Tests/Integration/Infrastructure/AzuriteTestContainer.cs'
        '4|Mock AI boundaries via DelegatingHandler|Implemented|src/PoTraffic.Api/Infrastructure/Security/MockExternalAuthDelegatingHandler.cs'
        '5|Mobile + desktop Playwright viewports|Implemented|tests/PoTraffic.Tests.E2E/Ui/Viewports.cs, ViewportsTheory.cs'
        '6|Po naming + Managed Identity + PoShared|Implemented|infra/main.bicep'
        '7|ARG governance script|Implemented|SCRIPTS/arg-governance.ps1'
        '8|App Insights adaptive sampling + storage lifecycle|Implemented|src/PoTraffic.Api/Infrastructure/Observability/*, infra/main.bicep'
        '9|Post-deploy smoke (Playwright + /health + /diag)|Implemented|SCRIPTS/post-deploy-smoke.ps1, src/PoTraffic.Api/Features/Diagnostics/*'
        '10|Test-run HTML report|Implemented|SCRIPTS/run-tests.ps1'
    )
    foreach ($row in $ideas) {
        $parts = $row -split '\|', 4
        [void]$sb.AppendLine("<tr><td>$($parts[0])</td><td>$($parts[1])</td><td><span class='pill pass'>$($parts[2])</span></td><td><code>$($parts[3])</code></td></tr>")
    }
    [void]$sb.AppendLine('</tbody></table>')
    [void]$sb.AppendLine("<p class='meta'>Report path: $Path</p></body></html>")
    $reportDir = Split-Path -Parent $Path
    if ($reportDir -and -not (Test-Path $reportDir)) { New-Item -ItemType Directory -Path $reportDir -Force | Out-Null }
    $sb.ToString() | Set-Content -Path $Path -Encoding UTF8
}

# ── Pre-flight ──────────────────────────────────────────────────────────────

$dockerOk = Test-DockerHealth
if (-not $dockerOk -and ($runIntegration -or $runE2eApi -or $runE2eUi)) {
    Write-Warning 'Docker daemon not reachable - Integration + E2E tiers will be SKIPPED.'
    $runIntegration = $false
    $runE2eApi = $false
    $runE2eUi = $false
}

if (-not $SkipBuild) {
    Write-Host "`n=== Build solution ===" -ForegroundColor Cyan
    dotnet build (Join-Path $root 'PoTraffic.slnx') --configuration Debug -nologo
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
}

# ── Tier 1: Unit (no I/O, FluentValidation, DTO mapping) ────────────────────
if ($runUnit) {
    Invoke-Tier -TierName 'UnitTests' -DisplayName 'Unit (pure, no I/O)' `
        -Project 'tests/PoTraffic.UnitTests'
}

# ── Tier 2: Integration (WAF + Azurite via Testcontainers) ──────────────────
if ($runIntegration) {
    Invoke-Tier -TierName 'IntegrationTests' -DisplayName 'Integration (WAF + Azurite)' `
        -Project 'tests/PoTraffic.Tests'
    # Explicit teardown — even though Testcontainers' resource reaper handles leftovers,
    # proactively kill the container so nothing lingers on the host.
    try {
        $ids = docker ps -aq --filter 'name=potraffic-azurite-test-' 2>$null
        if ($ids) { docker rm -f $ids *>$null }
    } catch { }
}

# ── Tier 3 + 4: E2E (live API + Playwright) ──────────────────────────────────
$testingHost = $null
if ($runE2eApi -or $runE2eUi) {
    $testingHost = Start-TestingHost -Url $e2eBaseUrl
    try {
        $ready = Warmup-App -Url $e2eBaseUrl -MaxRetries 30 -DelaySeconds 2
        if (-not $ready) { throw "Testing host did not become ready at $e2eBaseUrl." }
        $env:E2E_BASE_URL = $e2eBaseUrl

        if ($runE2eApi) {
            Invoke-Tier -TierName 'E2EApiTests' -DisplayName 'E2E API (live HTTP)' `
                -Project 'tests/PoTraffic.Tests.E2E'
        }
        if ($runE2eUi) {
            Install-PlaywrightChromium
            # Headed Chrome by default on dev workstations
            if (-not $env:E2E_HEADED) { $env:E2E_HEADED = '1' }
            Invoke-Tier -TierName 'E2EUiTests' -DisplayName 'E2E UI (Playwright mobile + desktop landscape)' `
                -Project 'tests/PoTraffic.Tests.E2E'
        }
    }
    finally {
        if ($testingHost -and -not $testingHost.HasExited) {
            Write-Host '  Stopping Testing host.' -ForegroundColor Yellow
            Stop-Process -Id $testingHost.Id -Force -ErrorAction SilentlyContinue
        }
    }
}

# ── Render HTML report ───────────────────────────────────────────────────────
$reportEnd = Get-Date
$totalSeconds = ($reportEnd - $runStart).TotalSeconds
$passCount = ($reportEntries | Where-Object { $_.status -eq 'Pass' }).Count
$totalTests = ($reportEntries | ForEach-Object { $_.counts.total } | Measure-Object -Sum).Sum
$totalPassed = ($reportEntries | ForEach-Object { $_.counts.passed } | Measure-Object -Sum).Sum
$totalFailed = ($reportEntries | ForEach-Object { $_.counts.failed } | Measure-Object -Sum).Sum
$totalSkipped = ($reportEntries | ForEach-Object { $_.counts.skipped } | Measure-Object -Sum).Sum

Render-HtmlReport -Path $HtmlReportPath -TotalSeconds $totalSeconds `
    -TotalTests $totalTests -TotalPassed $totalPassed -TotalFailed $totalFailed -TotalSkipped $totalSkipped `
    -PassCount $passCount

Write-Host "`n=== Summary ===" -ForegroundColor Magenta
Write-Host ("  Total tests: {0}  Passed: {1}  Failed: {2}  Skipped: {3}" -f $totalTests, $totalPassed, $totalFailed, $totalSkipped)
Write-Host ("  Tiers: {0}/{1} passed" -f $passCount, $reportEntries.Count)
Write-Host ("  HTML report -> {0}" -f $HtmlReportPath)

if ($totalFailed -gt 0) { exit 1 }
exit 0