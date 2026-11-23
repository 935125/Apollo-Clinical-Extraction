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
function Invoke-IsolatedPg([string]$program, [string[]]$pgArguments) {
    $argumentString = ($pgArguments | ForEach-Object { '"' + $_.Replace('"', '\"') + '"' }) -join ' '
    $logId = [guid]::NewGuid().ToString('N')
    $child = Start-Process -FilePath $program -ArgumentList $argumentString -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $testDirectory ($logId + '.stdout.log')) -RedirectStandardError (Join-Path $testDirectory ($logId + '.stderr.log'))
    $processHandle = $child.Handle
    $child.WaitForExit()
    $child.Refresh()
    return $child.ExitCode
}
try {
    $exitStatus = Invoke-IsolatedPg (Join-Path $binaries 'initdb.exe') @('-D', $clusterDirectory, '-U', 'stage7_owner', '--auth=scram-sha-256', ('--pwfile=' + $passwordFile), '--no-locale', '-E', 'UTF8')
    if ($exitStatus -ne 0) { throw 'Isolated initdb failed' }
    $exitStatus = Invoke-IsolatedPg (Join-Path $binaries 'pg_ctl.exe') @('-D', $clusterDirectory, '-l', (Join-Path $testDirectory 'postgres.log'), '-o', '-h 127.0.0.1 -p 55447', '-w', 'start')
    if ($exitStatus -ne 0) { throw 'Isolated cluster did not start' }
    $started = $true
    $psql = Join-Path $binaries 'psql.exe'
    $connectArgs = @('-h', '127.0.0.1', '-p', '55447', '-U', 'stage7_owner', '-d', 'postgres', '-w', '-X', '-t', '-c', 'SELECT 1;')
    $results.scramCorrectPassword = (Invoke-IsolatedPg $psql $connectArgs) -eq 0
    $env:PGPASSWORD = 'explicitly-wrong-isolated-test-password'
    $results.incorrectPasswordRejected = (Invoke-IsolatedPg $psql $connectArgs) -ne 0
    $env:PGPASSWORD = $temporarySecret
    $hbaPath = Join-Path $clusterDirectory 'pg_hba.conf'
    $originalHba = [System.IO.File]::ReadAllBytes($hbaPath)
    [System.IO.File]::WriteAllBytes((Join-Path $testDirectory 'pg_hba.before.conf'), $originalHba)
    if (!(Resolve-Path $hbaPath).Path.StartsWith($testDirectory + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe HBA test target' }
    [System.IO.File]::WriteAllText($hbaPath, "host all all 127.0.0.1/32 reject`n")
    $ignored = Invoke-IsolatedPg (Join-Path $binaries 'pg_ctl.exe') @('-D', $clusterDirectory, 'reload')
    $results.rejectRuleVerified = (Invoke-IsolatedPg $psql $connectArgs) -ne 0
    [System.IO.File]::WriteAllBytes($hbaPath, $originalHba)
    $ignored = Invoke-IsolatedPg (Join-Path $binaries 'pg_ctl.exe') @('-D', $clusterDirectory, 'reload')
    $results.hbaRollbackVerified = (Invoke-IsolatedPg $psql $connectArgs) -eq 0
    if (!$results.scramCorrectPassword -or !$results.incorrectPasswordRejected -or !$results.rejectRuleVerified -or !$results.hbaRollbackVerified) { throw 'Authentication rehearsal failed' }
}
finally {
    if ($started -and (Test-Path (Join-Path $clusterDirectory 'postmaster.pid'))) {
        $ignored = Invoke-IsolatedPg (Join-Path $binaries 'pg_ctl.exe') @('-D', $clusterDirectory, '-m', 'fast', '-w', 'stop')
    }
    Remove-Item -LiteralPath $passwordFile -ErrorAction SilentlyContinue
    Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
    $results | ConvertTo-Json | Set-Content (Join-Path $workspace 'docs/stage7-evidence/scram-rehearsal.json')
}
$results | ConvertTo-Json
