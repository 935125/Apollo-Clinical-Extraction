param([switch]$ClearClipboard)

# The password is encrypted by Windows DPAPI for the Windows user who seeded it.
# Neither the password nor its encrypted copy belongs in source control.
$ErrorActionPreference = 'Stop'
if ($ClearClipboard) {
    Set-Clipboard -Value ''
    Write-Host 'Clipboard cleared.'
    return
}

$secretPath = Join-Path $PSScriptRoot '.apollo-demo-password.local'
if (-not (Test-Path -LiteralPath $secretPath)) {
    throw 'The encrypted local demonstration password has not been provisioned for this checkout.'
}

$password = Get-Content -Raw -LiteralPath $secretPath | ConvertTo-SecureString
$pointer = [IntPtr]::Zero
try {
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($password)
    Set-Clipboard -Value ([Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer))
    Write-Host 'Local demonstration password copied. Paste it into the Apollo password field.'
    Write-Host 'After signing in, run .\Copy-ApolloDemoPassword.ps1 -ClearClipboard.'
} finally {
    if ($pointer -ne [IntPtr]::Zero) {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)
    }
    $password.Dispose()
}
