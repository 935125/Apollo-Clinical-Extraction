# Isolated integration verification: simulated Foundry HTTP, real ONNX, real PostgreSQL.
# No cloud call and no mutation of the application database.
$ErrorActionPreference = 'Stop'
if (!$env:TEST_POSTGRES_CONNECTION) { throw 'Set TEST_POSTGRES_CONNECTION to a dedicated cx_audit_test* database.' }
$env:FOUNDRY_LIVE_TEST = 'false'
dotnet test (Join-Path $PSScriptRoot '../Apollo.Backend.Tests.csproj') --configuration Release --no-restore --filter 'FullyQualifiedName~FoundryPostgresTests' --logger 'trx;LogFileName=stage2-isolated.trx' --results-directory (Join-Path $PSScriptRoot '../../artifacts/audit-remediation')
if ($LASTEXITCODE -ne 0) { throw 'Isolated integration verification failed.' }
