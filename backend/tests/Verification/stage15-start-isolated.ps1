[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
$projectRoot=Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))
$manifest=Get-Content -LiteralPath (Join-Path $projectRoot 'docs\stage15-evidence\integration\isolated-environment.json') -Raw | ConvertFrom-Json
if($manifest.database -ne 'cx_audit_test' -or $manifest.schema -notmatch '^foundry_test_[0-9a-f]{32}$' -or $manifest.applicationDatabaseTargeted) { throw 'Invalid isolated database manifest' }
$env:ASPNETCORE_ENVIRONMENT='Development'
$env:DOTNET_ENVIRONMENT='Development'
$env:ConnectionStrings__Postgres=$manifest.connectionString
$env:ConnectionStrings__Emr=$manifest.connectionString
$env:Extraction__Provider='LocalOnnx'
$env:Extraction__AllowDirectLocal='true'
$env:Extraction__DirectLocalReason='isolated_test'
$env:LocalOnnx__PipelineVersion='CandidateV2'
$env:Kestrel__Endpoints__Http5000__Url='http://127.0.0.1:5015'
$env:Kestrel__Endpoints__Http8000__Url='http://127.0.0.1:8015'
$env:Emr__Messaging__Mode='Simulated'
$env:Emr__Messaging__InferenceEnabled='true'
$env:Security__LocalDemoMode='true'
$env:Security__AllowedOrigins__0='http://127.0.0.1:5015'
$env:Foundry__Endpoint='https://offline-test.invalid'
$env:Foundry__ApiKey='no-live-provider-permitted'
$env:Emr__Seed__Login='doctor.krishnan@apollo.local'
$evidence=Join-Path $projectRoot 'docs\stage15-evidence\integration'
$runtime=Join-Path $projectRoot 'backend\bin\Stage15Serve\net10.0'
New-Item -ItemType Directory -Path $runtime -Force | Out-Null
Copy-Item -Path (Join-Path $projectRoot 'backend\bin\Stage15\net10.0\*') -Destination $runtime -Recurse -Force
$dll=Join-Path $runtime 'Apollo.Backend.dll'
$process=Start-Process -FilePath 'dotnet.exe' -ArgumentList @(('"'+$dll+'"'),'--urls','http://127.0.0.1:5015') -WorkingDirectory (Join-Path $projectRoot 'backend') -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $evidence 'isolated-api.stdout.log') -RedirectStandardError (Join-Path $evidence 'isolated-api.stderr.log')
@{processId=$process.Id;baseUrl=$manifest.baseUrl;database=$manifest.database;schema=$manifest.schema} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $evidence 'isolated-api-process.json') -Encoding UTF8
Write-Output "Started isolated CandidateV2 API process $($process.Id) on port 5015; database=cx_audit_test; messaging=Simulated; Foundry disabled."
