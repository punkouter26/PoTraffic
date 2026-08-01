# start-dev.ps1 — Kill stale dotnet processes on 5000/5001, start Azurite, then launch the API.
# Run from repo root: ./SCRIPTS/start-dev.ps1

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Write-Host "`n=== PoTraffic Dev Startup ===" -ForegroundColor Cyan

# 1. Kill any dotnet processes already bound to 5000 or 5001
foreach ($port in @(5000, 5001)) {
    $pids = (netstat -ano 2>$null |
        Select-String ":$port\s" |
        ForEach-Object { ($_ -split '\s+')[-1] } |
        Where-Object { $_ -match '^\d+$' } |
        Sort-Object -Unique)

    foreach ($p in $pids) {
        try {
            $proc = Get-Process -Id $p -ErrorAction SilentlyContinue
            if ($proc -and $proc.Name -like '*dotnet*') {
                Write-Host "  Stopping stale dotnet process PID $p on port $port" -ForegroundColor Yellow
                Stop-Process -Id $p -Force
            }
        } catch { }
    }
}

# 2. Ensure Azurite is running via Docker Compose
Write-Host "`nStarting Azurite (docker compose up -d)..." -ForegroundColor Cyan
docker compose up -d
if ($LASTEXITCODE -ne 0) {
    Write-Error "docker compose up failed. Is Docker Desktop running?"
}

# 3. Wait for Azurite Table service to be healthy
Write-Host "Waiting for Azurite Table service on port 10002..." -ForegroundColor Yellow
$maxRetries = 20
for ($i = 1; $i -le $maxRetries; $i++) {
    try {
        $tcp = New-Object System.Net.Sockets.TcpClient
        $result = $tcp.BeginConnect("127.0.0.1", 10002, $null, $null)
        $wait = $result.AsyncWaitHandle.WaitOne(1000, $false)
        if ($wait -and $tcp.Connected) {
            $tcp.EndConnect($result)
            $tcp.Close()
            Write-Host "  Azurite is ready (attempt $i)." -ForegroundColor Green
            break
        }
        $tcp.Close()
    } catch { }
    if ($i -eq $maxRetries) {
        Write-Warning "Azurite not ready after $maxRetries attempts — continuing anyway."
    }
    Start-Sleep -Seconds 1
}

# 4. Launch API (Development profile, HTTPS)
Write-Host "`nLaunching PoTraffic API on https://localhost:5001 ..." -ForegroundColor Green
Set-Location (Join-Path $PSScriptRoot '..' 'src' 'PoTraffic.API')
dotnet run --launch-profile https
