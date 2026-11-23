# Bounded measurement, no live cloud faults and no application database writes.
$ErrorActionPreference = 'Stop'
if (!$env:TEST_POSTGRES_CONNECTION) { throw 'Set TEST_POSTGRES_CONNECTION to a dedicated cx_audit_test* database.' }
$env:FOUNDRY_LIVE_TEST = 'false'
dotnet test (Join-Path $PSScriptRoot '../Apollo.Backend.Tests.csproj') --configuration Release --no-restore --filter 'FullyQualifiedName~ReliabilityMeasurementTests|FullyQualifiedName~FoundryWorkflowTests|FullyQualifiedName~AuditRemediationTests' --logger 'trx;LogFileName=stage3-isolated.trx' --results-directory (Join-Path $PSScriptRoot '../../artifacts/audit-remediation')
if ($LASTEXITCODE -ne 0) { throw 'Isolated recovery verification failed.' }
