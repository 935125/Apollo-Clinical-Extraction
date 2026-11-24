# Shared launcher configuration. Only routing/environment variables are set.
function Initialize-ApolloLaunchEnvironment([string]$projectRoot) {
    $dotnetEnvironment = [Environment]::GetEnvironmentVariable('DOTNET_ENVIRONMENT')
    $aspnetEnvironment = [Environment]::GetEnvironmentVariable('ASPNETCORE_ENVIRONMENT')
    if ($dotnetEnvironment -and $aspnetEnvironment -and $dotnetEnvironment -ine $aspnetEnvironment) {
        throw 'DOTNET_ENVIRONMENT and ASPNETCORE_ENVIRONMENT conflict. Choose one environment before launching.'
    }
    $selectedEnvironment = if ($dotnetEnvironment) { $dotnetEnvironment } elseif ($aspnetEnvironment) { $aspnetEnvironment } else { 'Development' }
    $env:DOTNET_ENVIRONMENT = $selectedEnvironment
    $env:ASPNETCORE_ENVIRONMENT = $selectedEnvironment
    $baseSettings = Get-Content -LiteralPath (Join-Path $projectRoot 'backend/appsettings.json') -Raw | ConvertFrom-Json
    $environmentPath = Join-Path $projectRoot ('backend/appsettings.' + $selectedEnvironment + '.json')
    $environmentSettings = if (Test-Path -LiteralPath $environmentPath) { Get-Content -LiteralPath $environmentPath -Raw | ConvertFrom-Json } else { $null }
    $secretSettings = $null
    $localRouting = @{}
    if ($selectedEnvironment -ieq 'Development') {
        $secretPath = Join-Path $env:APPDATA 'Microsoft/UserSecrets/Apollo-AIExtraction-Local/secrets.json'
        if (Test-Path -LiteralPath $secretPath) { $secretSettings = Get-Content -LiteralPath $secretPath -Raw | ConvertFrom-Json }
        $dotenvPath = Join-Path $projectRoot '.env'
        if (Test-Path -LiteralPath $dotenvPath) {
            foreach ($line in [System.IO.File]::ReadLines($dotenvPath)) {
                if ($line -match '^\s*(Extraction__(?:Provider|FallbackProvider|AllowDirectLocal|DirectLocalReason))\s*=\s*(.*?)\s*$') {
                    $localRouting[$Matches[1]] = $Matches[2].Trim('"', "'")
                }
            }
        }
    }
    foreach ($field in @('Provider','FallbackProvider','AllowDirectLocal','DirectLocalReason')) {
        $key = 'Extraction__' + $field
        if (![string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($key))) { continue }
        $flatSecret = if ($secretSettings) { $secretSettings.PSObject.Properties['Extraction:' + $field] } else { $null }
        $selected = if ($flatSecret) { $flatSecret.Value }
            elseif ($secretSettings -and $secretSettings.Extraction -and $secretSettings.Extraction.$field) { $secretSettings.Extraction.$field }
            elseif ($localRouting.ContainsKey($key)) { $localRouting[$key] }
            elseif ($environmentSettings -and $environmentSettings.Extraction -and $environmentSettings.Extraction.$field) { $environmentSettings.Extraction.$field }
            elseif ($baseSettings.Extraction -and $baseSettings.Extraction.$field) { $baseSettings.Extraction.$field }
            else { $null }
        if ($null -ne $selected) {
            [Environment]::SetEnvironmentVariable($key, $selected.ToString(), 'Process')
        }
    }
    [pscustomobject]@{
        environment = $selectedEnvironment
        primaryProvider = $env:Extraction__Provider
        fallbackProvider = $env:Extraction__FallbackProvider
        secretsPrinted = $false
    }
}
