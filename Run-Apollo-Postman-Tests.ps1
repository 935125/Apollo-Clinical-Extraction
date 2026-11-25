<#
.SYNOPSIS
    One-command headless Postman CLI verification runner for the Apollo Clinical Extraction Platform.
.DESCRIPTION
    Resolves the installed Postman CLI (even when npm's global prefix is not on PATH),
    validates collection and environment files, verifies backend liveness and readiness
    (without requiring Angular), optionally launches local backend dependencies when
    explicitly opted in via -StartBackend (never enabling billable Foundry calls or
    duplicating running services), executes the full 9-folder Postman CLI collection,
    saves sanitized diagnostic output under docs/postman-evidence/, and returns the
    real Postman CLI exit code.
.PARAMETER StartBackend
    Optional opt-in switch. If PostgreSQL (5432) or ASP.NET Core API (5000) are not
    running, starts them safely in zero-cost LocalOnnx authorized_offline mode. Never
    stops pre-existing services or starts duplicates.
.PARAMETER Folder
    Optional Postman folder name to run a subset of the collection.
#>
[CmdletBinding()]
param(
    [switch]$StartBackend,
    [string]$Folder = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$collectionPath = Join-Path $root 'postman\Apollo-Clinical-Extraction.postman_collection.json'
$environmentPath = Join-Path $root 'postman\Apollo-Local.postman_environment.json'
$evidenceDir = Join-Path $root 'docs\postman-evidence'
New-Item -ItemType Directory -Path $evidenceDir -Force | Out-Null

function Resolve-PostmanCli {
    $cmd = Get-Command postman.cmd -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $npmPrefix = (& npm config get prefix 2>$null)
    if ($npmPrefix) {
        $candidate = Join-Path ($npmPrefix.Trim()) 'postman.cmd'
        if (Test-Path -LiteralPath $candidate) { return $candidate }
    }
    $fallback = Join-Path $env:APPDATA 'npm\postman.cmd'
    if (Test-Path -LiteralPath $fallback) { return $fallback }
    throw 'Postman CLI (postman.cmd) was not found. Install via: npm install -g postman-cli'
}

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

function Start-LocalDependenciesIfRequested([string]$projectRoot) {
    $pgCtl = Join-Path $projectRoot 'backend\tools\pgsql\bin\pg_ctl.exe'
    $pgData = Join-Path $projectRoot 'backend\tools\data_cx'
    $pgLog = Join-Path $projectRoot 'backend\tools\postgres.log'

    if (-not (Test-LocalPort 5432)) {
        if (-not (Test-Path -LiteralPath $pgCtl)) { throw "PostgreSQL is not listening on 5432 and bundled pg_ctl was not found: $pgCtl" }
        Write-Host '[Runner] Starting bundled PostgreSQL on 127.0.0.1:5432...' -ForegroundColor Cyan
        & $pgCtl -D $pgData -l $pgLog -o '-h 127.0.0.1 -p 5432' start | Out-Null
        for ($i = 0; $i -lt 30; $i++) { if (Test-LocalPort 5432) { break }; Start-Sleep -Milliseconds 500 }
        if (-not (Test-LocalPort 5432)) { throw 'PostgreSQL did not start on port 5432.' }
    }

    if (-not (Test-LocalPort 5000)) {
        Write-Host '[Runner] Starting ASP.NET Core backend on 127.0.0.1:5000 (LocalOnnx authorized_offline; zero billable Foundry calls)...' -ForegroundColor Cyan
        $escapedRoot = $projectRoot.Replace("'", "''")
        $azDir = Join-Path $env:LOCALAPPDATA 'ApolloTools\AzureCLI\bin'
        $cmdScript = @"
Set-Location -LiteralPath '$escapedRoot'
`$env:ASPNETCORE_ENVIRONMENT = 'Development'
`$env:DOTNET_ENVIRONMENT = 'Development'
`$env:Extraction__Provider = 'LocalOnnx'
`$env:Extraction__AllowDirectLocal = 'true'
`$env:Extraction__DirectLocalReason = 'authorized_offline'
if (Test-Path '$azDir') { `$env:PATH = '$azDir;' + `$env:PATH }
dotnet run --no-launch-profile --project 'backend/Apollo.Backend.csproj'
"@
        $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($cmdScript))
        $logDir = Join-Path $projectRoot 'backend\logs'
        New-Item -ItemType Directory -Path $logDir -Force | Out-Null
        Start-Process -FilePath 'powershell.exe' -WorkingDirectory $projectRoot -WindowStyle Hidden `
            -ArgumentList @('-EncodedCommand', $encoded) `
            -RedirectStandardOutput (Join-Path $logDir 'PostmanBackend.stdout.log') `
            -RedirectStandardError (Join-Path $logDir 'PostmanBackend.stderr.log') | Out-Null

        for ($i = 0; $i -lt 50; $i++) {
            if (Test-LocalPort 5000) { break }
            Start-Sleep -Milliseconds 500
        }
    }
}

Write-Host ''
Write-Host '=== Apollo Clinical Extraction — Headless Postman CLI Verification ===' -ForegroundColor Cyan

# 1. Resolve Postman CLI
$postmanCli = Resolve-PostmanCli
$postmanVersion = (& $postmanCli -v 2>&1 | Out-String).Trim()
Write-Host "Postman CLI : $postmanCli (v$postmanVersion)"

# 2. Validate required JSON files
foreach ($file in @($collectionPath, $environmentPath)) {
    if (-not (Test-Path -LiteralPath $file)) { throw "Missing required Postman file: $file" }
    try { Get-Content -LiteralPath $file -Raw | ConvertFrom-Json | Out-Null }
    catch { throw "Invalid JSON in $file : $($_.Exception.Message)" }
}

$envJson = Get-Content -LiteralPath $environmentPath -Raw | ConvertFrom-Json
$baseUrl = ($envJson.values | Where-Object { $_.key -eq 'baseUrl' } | Select-Object -First 1).value
if (-not $baseUrl) { $baseUrl = 'http://127.0.0.1:5000' }
Write-Host "Target API  : $baseUrl"

# 3. Optional startup mode
if ($StartBackend) {
    Start-LocalDependenciesIfRequested $root
}

# 4. Preflight reachability & readiness checks (fail clearly with BLOCKED if unreachable)
try {
    $health = Invoke-RestMethod "$baseUrl/health" -TimeoutSec 5
    $ready = Invoke-RestMethod "$baseUrl/health/ready" -TimeoutSec 5
    $routing = Invoke-RestMethod "$baseUrl/v1/routing" -TimeoutSec 5
} catch {
    Write-Host ''
    Write-Host "BLOCKED: Backend API unreachable at $baseUrl ($($_.Exception.Message))" -ForegroundColor Red
    Write-Host 'Start the backend first (or re-run with: .\Run-Apollo-Postman-Tests.ps1 -StartBackend).' -ForegroundColor Yellow
    exit 2
}

Write-Host "Health      : $($health.status) ($($health.service) $($health.version))" -ForegroundColor Green
Write-Host "Readiness   : $($ready.status) [$(($ready.checks | ForEach-Object { "$($_.name)=$($_.status)" }) -join ', ')]" -ForegroundColor Green
Write-Host "Routing     : env=$($routing.environment), primary=$($routing.requestedPrimaryProvider), fallback=$($routing.fallbackProvider), foundry=$($routing.foundryDeployment)" -ForegroundColor Green

# 5. Execute Postman CLI collection
$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$runArgs = @(
    'collection', 'run', $collectionPath,
    '-e', $environmentPath,
    '--no-report-events',
    '--output', $evidenceDir
)
if (-not [string]::IsNullOrWhiteSpace($Folder)) {
    $runArgs += @('-i', $Folder)
}

Write-Host ''
Write-Host "Running Postman CLI collection..." -ForegroundColor Cyan
$transcriptPath = Join-Path $evidenceDir "postman-cli-run-$timestamp.log"

$prevPreference = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
$outputLines = & $postmanCli @runArgs 2>&1
$exitCode = $LASTEXITCODE
$ErrorActionPreference = $prevPreference

# Sanitize any accidental cookie/token strings before saving diagnostic transcript
$sanitized = $outputLines | ForEach-Object {
    ($_ -replace '(?i)(Apollo\.Doctor|XSRF-TOKEN|X-XSRF-TOKEN|Authorization)\s*[:=]\s*[^\s;,]+', '$1=[REDACTED]')
}
$sanitized | Tee-Object -FilePath $transcriptPath

Write-Host ''
Write-Host "Sanitized transcript saved: $transcriptPath" -ForegroundColor DarkGray
if ($exitCode -eq 0) {
    Write-Host 'POSTMAN CLI SUITE RESULT: PASS (exit code 0)' -ForegroundColor Green
} else {
    Write-Host "POSTMAN CLI SUITE RESULT: FAIL (exit code $exitCode)" -ForegroundColor Red
}
exit $exitCode
