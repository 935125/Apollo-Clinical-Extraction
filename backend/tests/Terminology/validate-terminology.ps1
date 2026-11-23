<#
  Validates every RxNorm CUI and LOINC code referenced by Clinical/IndianOntologyResolver.cs
  against public authoritative terminology services:
    - RxNorm: NLM RxNav REST API (https://rxnav.nlm.nih.gov/REST/rxcui/{cui}/properties.json)
    - LOINC : HL7 FHIR terminology server tx.fhir.org (CodeSystem/$lookup, system http://loinc.org)
  Output: JSON written to the path given by -OutFile. No patient data is transmitted.
#>
param(
    [string]$Resolver = (Join-Path $PSScriptRoot '..\..\Clinical\IndianOntologyResolver.cs'),
    [string]$OutFile = (Join-Path $PSScriptRoot 'terminology-validation.json')
)
$ErrorActionPreference = 'Stop'
$text = Get-Content -Raw -Path $Resolver
$drug = [regex]::Matches($text, '\["(?<key>[^"]+)"\]\s*=\s*new\("(?<name>[^"]+)",\s*"(?<catalog>RX-[^"]+)",\s*"(?<cui>\d+)"(?:,\s*"(?<ing>\d+)")?\)')
$lab = [regex]::Matches($text, '\["(?<key>[^"]+)"\]\s*=\s*new\("(?<name>[^"]+)",\s*"(?<catalog>(?:LAB|IMG)-[^"]+)",\s*"(?<loinc>\d+-\d)"')

$rx = @{}; $ln = @{}
function Get-Rx([string]$cui) {
    if ($rx.ContainsKey($cui)) { return $rx[$cui] }
    try {
        $p = Invoke-RestMethod -Uri "https://rxnav.nlm.nih.gov/REST/rxcui/$cui/properties.json" -TimeoutSec 30
        $s = Invoke-RestMethod -Uri "https://rxnav.nlm.nih.gov/REST/rxcui/$cui/historystatus.json" -TimeoutSec 30
        $r = [ordered]@{ exists = [bool]$p.properties; name = $p.properties.name; tty = $p.properties.tty;
            status = $s.rxcuiStatusHistory.metaData.status }
    } catch { $r = [ordered]@{ exists = $false; name = $null; tty = $null; status = "lookup_error: $($_.Exception.Message)" } }
    $rx[$cui] = $r; Start-Sleep -Milliseconds 120; return $r
}
function Get-Loinc([string]$code) {
    if ($ln.ContainsKey($code)) { return $ln[$code] }
    try {
        $j = Invoke-RestMethod -Uri "https://tx.fhir.org/r4/CodeSystem/`$lookup?system=http://loinc.org&code=$code&_format=json" -TimeoutSec 30
        $display = ($j.parameter | Where-Object name -eq 'display').valueString
        $version = ($j.parameter | Where-Object name -eq 'version').valueString
        $r = [ordered]@{ exists = [bool]$display; display = $display; version = $version }
    } catch { $r = [ordered]@{ exists = $false; display = $null; version = "lookup_error: $($_.Exception.Message)" } }
    $ln[$code] = $r; Start-Sleep -Milliseconds 120; return $r
}

$rxVersion = try { (Invoke-RestMethod -Uri 'https://rxnav.nlm.nih.gov/REST/version.json' -TimeoutSec 30).version } catch { 'unknown' }
$drugs = foreach ($m in $drug) {
    $r = Get-Rx $m.Groups['cui'].Value
    $ing = if ($m.Groups['ing'].Success) { Get-Rx $m.Groups['ing'].Value } else { $null }
    [ordered]@{ mention = $m.Groups['key'].Value; standardName = $m.Groups['name'].Value; catalogCode = $m.Groups['catalog'].Value;
        rxcui = $m.Groups['cui'].Value; rxnav = $r; ingredientRxcui = $m.Groups['ing'].Value; ingredientRxnav = $ing }
}
$labs = foreach ($m in $lab) {
    [ordered]@{ mention = $m.Groups['key'].Value; standardName = $m.Groups['name'].Value; catalogCode = $m.Groups['catalog'].Value;
        loinc = $m.Groups['loinc'].Value; lookup = (Get-Loinc $m.Groups['loinc'].Value) }
}
[ordered]@{
    generatedAt = (Get-Date).ToString('o')
    rxnormSource = 'NLM RxNav REST API'; rxnormVersion = $rxVersion
    loincSource = 'tx.fhir.org R4 CodeSystem/$lookup (http://loinc.org)'
    medications = @($drugs); investigations = @($labs)
} | ConvertTo-Json -Depth 8 | Set-Content -Path $OutFile -Encoding UTF8
"Wrote $OutFile : $($drugs.Count) medication mappings, $($labs.Count) investigation mappings"

