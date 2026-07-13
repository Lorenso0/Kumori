param([Parameter(Mandatory = $true)][string]$Path)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($env:KUMORI_SIGN_CERTIFICATE_BASE64) -or
    [string]::IsNullOrWhiteSpace($env:KUMORI_SIGN_CERTIFICATE_PASSWORD)) {
    throw 'Authenticode signing credentials are not configured.'
}

$certificatePath = Join-Path ([IO.Path]::GetTempPath()) ("kumori-signing-{0}.pfx" -f [guid]::NewGuid().ToString('N'))
$certificate = $null
try {
    [IO.File]::WriteAllBytes($certificatePath, [Convert]::FromBase64String($env:KUMORI_SIGN_CERTIFICATE_BASE64))
    $flags = [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet
    $certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new(
        $certificatePath,
        $env:KUMORI_SIGN_CERTIFICATE_PASSWORD,
        $flags)
    $signature = Set-AuthenticodeSignature `
        -FilePath $Path `
        -Certificate $certificate `
        -HashAlgorithm SHA256 `
        -TimestampServer 'http://timestamp.digicert.com'
    if ($signature.Status -ne 'Valid') {
        throw "Authenticode signing failed for $Path`: $($signature.StatusMessage)"
    }
}
finally {
    if ($null -ne $certificate) {
        $certificate.Dispose()
    }
    if (Test-Path -LiteralPath $certificatePath) {
        Remove-Item -LiteralPath $certificatePath -Force
    }
}
