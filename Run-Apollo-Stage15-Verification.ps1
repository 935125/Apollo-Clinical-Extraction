[CmdletBinding()]
param([switch]$IncludePostgres)
$ErrorActionPreference='Stop'
Set-Location -LiteralPath $PSScriptRoot
$evidence=Join-Path $PSScriptRoot 'docs\stage15-evidence'
New-Item -ItemType Directory -Path (Join-Path $evidence 'regression') -Force | Out-Null
$env:FOUNDRY_LIVE_TEST='false'
$env:AUTHORIZE_SINGLE_LIVE_DISCHARGE_TEST=''
$env:AUTHORIZE_STAGE8_SINGLE_LIVE_CALL=''
$env:FINAL_EVALUATION_MANIFEST=$null
$env:APOLLO_STAGE15_PREPARE_E2E='0'
$env:APOLLO_STAGE15_EVALUATE='0'
$env:APOLLO_STAGE15_PHASE='candidate-v2-verification'
$env:APOLLO_EVALUATION_OUTPUT_ROOT=Join-Path $evidence 'regression'
$env:Extraction__Provider='LocalOnnx'
$env:Extraction__AllowDirectLocal='true'
$env:Extraction__DirectLocalReason='isolated_test'
$env:Foundry__ApiKey='offline-tests-no-real-credential'
$env:Emr__Messaging__Mode='Disabled'
if(-not $IncludePostgres) { $env:TEST_POSTGRES_CONNECTION=$null }
elseif($env:TEST_POSTGRES_CONNECTION -notmatch '(?i)Database=cx_audit_test(?:[;_]|$)') { throw 'Set TEST_POSTGRES_CONNECTION to a dedicated cx_audit_test database; never target clinical application data.' }
dotnet test backend/tests/Apollo.Backend.Tests.csproj -c Stage15 --no-restore -p:UseAppHost=false --filter 'FullyQualifiedName!~Stage9RealServiceBusSmokeTests' --logger 'trx;LogFileName=stage15-full-final.trx' --results-directory docs/stage15-evidence/regression/test-results
if($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$env:APOLLO_STAGE15_EVALUATE='1'
dotnet test backend/tests/Apollo.Backend.Tests.csproj -c Stage15 --no-build --filter 'FullyQualifiedName~Stage15AblationTests' --logger 'trx;LogFileName=stage15-ablation-final.trx' --results-directory docs/stage15-evidence/evaluation/test-results
if($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
python backend/tests/Evaluation/stage15-score.py
exit $LASTEXITCODE
