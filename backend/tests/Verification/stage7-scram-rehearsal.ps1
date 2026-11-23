$ErrorActionPreference = 'Stop'
$workspace = (Resolve-Path (Join-Path $PSScriptRoot '../../..')).Path
$binaries = Join-Path $workspace 'backend/tools/pgsql/bin'
$testDirectory = Join-Path $workspace ('backend/tools/stage7-auth-' + [guid]::NewGuid().ToString('N'))
$clusterDirectory = Join-Path $testDirectory 'cluster'
$passwordFile = Join-Path $testDirectory 'owner-password.tmp'
New-Item -ItemType Directory -Path $testDirectory | Out-Null
$randomBytes = New-Object byte[] 32
$generator = [System.Security.Cryptography.RandomNumberGenerator]::Create()
$generator.GetBytes($randomBytes)
$generator.Dispose()
$temporarySecret = [BitConverter]::ToString($randomBytes).Replace('-', '')
[System.IO.File]::WriteAllText($passwordFile, $temporarySecret)
$env:PGCONNECT_TIMEOUT = '3'
$env:PGPASSWORD = $temporarySecret
$results = [ordered]@{ database = 'fresh isolated cluster, port 55447'; scramCorrectPassword = $false; incorrectPasswordRejected = $false; rejectRuleVerified = $false; hbaRollbackVerified = $false; existingClusterChanged = $false; tlsTested = $false }
$started = $false
try {
    & (Join-Path $binaries 'initdb.exe') -D $clusterDirectory -U stage7_owner --auth=scram-sha-256 --pwfile=$passwordFile --no-locale -E UTF8 *> (Join-Path $testDirectory 'initdb.log')
    if ($LASTEXITCODE -ne 0) { throw 'Isolated initdb failed' }
    & (Join-Path $binaries 'pg_ctl.exe') -D $clusterDirectory -l (Join-Path $testDirectory 'postgres.log') -o '-h 127.0.0.1 -p 55447' -w start *> $null
    if ($LASTEXITCODE -ne 0) { throw 'Isolated cluster did not start' }
    $started = $true
    $psql = Join-Path $binaries 'psql.exe'
    & $psql -h 127.0.0.1 -p 55447 -U stage7_owner -d postgres -w -X -t -c 'SELECT 1;' *> $null
    $results.scramCorrectPassword = $LASTEXITCODE -eq 0
    $env:PGPASSWORD = 'explicitly-wrong-isolated-test-password'
    & $psql -h 127.0.0.1 -p 55447 -U stage7_owner -d postgres -w -X -t -c 'SELECT 1;' *> $null
    $results.incorrectPasswordRejected = $LASTEXITCODE -ne 0
    $env:PGPASSWORD = $temporarySecret
    $hbaPath = Join-Path $clusterDirectory 'pg_hba.conf'
    $originalHba = [System.IO.File]::ReadAllBytes($hbaPath)
    [System.IO.File]::WriteAllBytes((Join-Path $testDirectory 'pg_hba.before.conf'), $originalHba)
    # The resolved path is a newly initialized isolated cluster, never data_cx.
    if (!(Resolve-Path $hbaPath).Path.StartsWith($testDirectory + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe HBA test target' }
    [System.IO.File]::WriteAllText($hbaPath, "host all all 127.0.0.1/32 reject`n")
    & (Join-Path $binaries 'pg_ctl.exe') -D $clusterDirectory reload *> $null
    & $psql -h 127.0.0.1 -p 55447 -U stage7_owner -d postgres -w -X -t -c 'SELECT 1;' *> $null
    $results.rejectRuleVerified = $LASTEXITCODE -ne 0
    [System.IO.File]::WriteAllBytes($hbaPath, $originalHba)
    & (Join-Path $binaries 'pg_ctl.exe') -D $clusterDirectory reload *> $null
    & $psql -h 127.0.0.1 -p 55447 -U stage7_owner -d postgres -w -X -t -c 'SELECT 1;' *> $null
    $results.hbaRollbackVerified = $LASTEXITCODE -eq 0
    if (!$results.scramCorrectPassword -or !$results.incorrectPasswordRejected -or !$results.rejectRuleVerified -or !$results.hbaRollbackVerified) { throw 'Authentication rehearsal failed' }
}
finally {
    if ($started) { & (Join-Path $binaries 'pg_ctl.exe') -D $clusterDirectory -m fast -w stop *> $null }
    Remove-Item -LiteralPath $passwordFile -ErrorAction SilentlyContinue
    Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
    $results | ConvertTo-Json | Set-Content (Join-Path $workspace 'docs/stage7-evidence/scram-rehearsal.json')
}
$results | ConvertTo-Json
