param([switch]$ConfigurationOnly, [switch]$NoBrowser)
# Apollo Clinical Extraction Platform - Windows one-click launcher
# Put this file beside Start-Apollo.bat in the D:\AI Extraction folder.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $root 'Apollo-LaunchEnvironment.ps1')
$routing = Initialize-ApolloLaunchEnvironment $root
if ($ConfigurationOnly) { $routing | ConvertTo-Json -Compress; return }
Write-Host ("Environment: {0}; primary: {1}; fallback: {2}" -f $routing.environment, $routing.primaryProvider, $routing.fallbackProvider)

$pgCtl = Join-Path $root 'backend\tools\pgsql\bin\pg_ctl.exe'
$pgIsReady = Join-Path $root 'backend\tools\pgsql\bin\pg_isready.exe'
$pgData = Join-Path $root 'backend\tools\data_cx'
$pgLog = Join-Path $root 'backend\tools\postgres.log'
$apiProject = Join-Path $root 'backend\Apollo.Backend.csproj'
$frontend = Join-Path $root 'frontend'

function Test-LocalPort([int]$port) {
    $client = [System.Net.Sockets.TcpClient]::new()
    try {
        $async = $client.BeginConnect('127.0.0.1', $port, $null, $null)
        if (-not $async.AsyncWaitHandle.WaitOne(400)) { return $false }
        $client.EndConnect($async)
        return $true
    } catch { return $false }
    finally { $client.Dispose() }
}

function Wait-LocalPort([int]$port, [int]$attempts = 40) {
    for ($i = 0; $i -lt $attempts; $i++) {
        if (Test-LocalPort $port) { return $true }
        Start-Sleep -Milliseconds 500
    }
    return $false
}

function Start-Terminal([string]$directory, [string]$command, [string]$label) {
    # EncodedCommand avoids quoting problems in directories containing spaces.
    $escapedDir = $directory.Replace("'", "''")
    $content = "`$Host.UI.RawUI.WindowTitle = '$label'; Set-Location -LiteralPath '$escapedDir'; $command"
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($content))
    $logDirectory = Join-Path $root 'backend/logs'
    New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
    $logName = $label -replace '[^A-Za-z0-9]', ''
    Start-Process -FilePath 'powershell.exe' -WorkingDirectory $directory -WindowStyle Hidden -ArgumentList @('-EncodedCommand',$encoded) -RedirectStandardOutput (Join-Path $logDirectory ($logName + '.stdout.log')) -RedirectStandardError (Join-Path $logDirectory ($logName + '.stderr.log')) | Out-Null
}

Write-Host ''
Write-Host '=== Apollo Clinical Extraction Platform ===' -ForegroundColor Cyan
Write-Host "Project directory: $root"

if (-not (Test-Path -LiteralPath $pgCtl)) { throw "Missing PostgreSQL executable: $pgCtl" }
if (-not (Test-Path -LiteralPath (Join-Path $pgData 'PG_VERSION'))) { throw "Missing PostgreSQL cluster: $pgData" }
if (-not (Test-Path -LiteralPath $apiProject)) { throw "Missing backend project: $apiProject" }
if (-not (Test-Path -LiteralPath (Join-Path $frontend 'package.json'))) { throw "Missing Angular project: $frontend" }

Write-Host "`n[1/3] PostgreSQL (5432)" -ForegroundColor Cyan
if (Test-LocalPort 5432) {
    Write-Host 'A server is already listening on port 5432. Not starting another instance.' -ForegroundColor Yellow
} else {
    & $pgCtl -D $pgData status *> $null
    if ($LASTEXITCODE -eq 0) {
        throw 'The data_cx PostgreSQL cluster reports running, but port 5432 is unavailable. Check its configured port; no duplicate was started.'
    }
    Write-Host 'Starting bundled PostgreSQL from backend\tools\data_cx...'
    & $pgCtl -D $pgData -l $pgLog -o '-h 127.0.0.1 -p 5432' start
    if ($LASTEXITCODE -ne 0) { throw "Could not start PostgreSQL. Inspect log: $pgLog" }
    if (-not (Wait-LocalPort 5432)) { throw "PostgreSQL didn't begin listening on 5432. Inspect log: $pgLog" }
    Write-Host 'PostgreSQL is listening on 5432.' -ForegroundColor Green
}
if (Test-Path -LiteralPath $pgIsReady) {
    & $pgIsReady -h 127.0.0.1 -p 5432
    if ($LASTEXITCODE -ne 0) { Write-Warning 'Port 5432 is open, but pg_isready did not confirm PostgreSQL readiness.' }
}

Write-Host "`n[2/3] ASP.NET Core API (5000)" -ForegroundColor Cyan
if (Test-LocalPort 5000) {
    try { $activeRouting = Invoke-RestMethod 'http://127.0.0.1:5000/v1/routing' -TimeoutSec 5 }
    catch { throw 'Existing backend cannot verify its routing. Restart it explicitly; the launcher did not stop or replace it.' }
    if ($activeRouting.requestedPrimaryProvider -ine $routing.primaryProvider -or $activeRouting.environment -ine $routing.environment) {
        throw 'Existing backend routing differs from the selected configuration. Restart it explicitly; no existing process was changed.'
    }
    Write-Host 'Existing backend routing matches the selected environment and provider.' -ForegroundColor Green
} else {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'dotnet not found on PATH. Install the .NET SDK.' }
    Start-Terminal $root "dotnet run --no-launch-profile --project 'backend/Apollo.Backend.csproj'" 'Apollo Backend'
    Write-Host 'Launched backend in the background with the selected routing.'
}

Write-Host "`n[3/3] Angular UI (4200)" -ForegroundColor Cyan
if (Test-LocalPort 4200) {
    Write-Host 'Port 4200 is already listening; skipping new frontend instance.' -ForegroundColor Yellow
} else {
    if (-not (Get-Command npm.cmd -ErrorAction SilentlyContinue)) { throw 'npm not found on PATH. Install Node.js.' }
    if (-not (Test-Path -LiteralPath (Join-Path $frontend 'node_modules'))) {
        Write-Host 'Dependencies not installed: run npm ci inside frontend once before starting.' -ForegroundColor Yellow
        throw 'Missing frontend\node_modules'
    }
    Start-Terminal $frontend 'npm start' 'Apollo Frontend'
    Write-Host 'Launched Angular in the background.'
}

Write-Host "`nChecking application ports..." -ForegroundColor Cyan
$apiOk = Wait-LocalPort 5000
$uiOk = Wait-LocalPort 4200
if ($apiOk -and $uiOk) {
    Write-Host 'Backend 5000 and frontend 4200 are listening.' -ForegroundColor Green
    if (!$NoBrowser) { Start-Process 'http://localhost:4200/' }
    Write-Host 'Opened http://localhost:4200/ in your browser.' -ForegroundColor Green
} else {
    if (-not $apiOk) { Write-Warning 'Backend is not listening on 5000. Inspect backend/logs/ApolloBackend.stderr.log for startup errors.' }
    if (-not $uiOk) { Write-Warning 'Frontend is not listening on 4200. Inspect backend/logs/ApolloFrontend.stderr.log for startup errors.' }
}
Write-Host "`nServices run in the background."
Write-Host 'This launcher never stops existing processes or resets your PostgreSQL data.'
