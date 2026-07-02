param(
    [string]$OutputDirectory = "D:\serial-log-data\certs",
    [string]$PfxName = "SerialLog-TestSigning.pfx",
    [string]$CerName = "SerialLog-TestSigning.cer",
    [string]$Password = ""
)

$ErrorActionPreference = "Stop"

if (-not $Password) {
    $Password = [Guid]::NewGuid().ToString("N") + "Aa1!"
}

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

$securePassword = ConvertTo-SecureString $Password -AsPlainText -Force
$cert = New-SelfSignedCertificate `
    -Type Custom `
    -Subject "CN=SerialLog" `
    -FriendlyName "Serial Log Test Signing" `
    -KeyUsage DigitalSignature `
    -KeyAlgorithm RSA `
    -KeyLength 2048 `
    -CertStoreLocation "Cert:\CurrentUser\My" `
    -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3")

$pfxPath = Join-Path $OutputDirectory $PfxName
$cerPath = Join-Path $OutputDirectory $CerName

Export-PfxCertificate -Cert $cert -FilePath $pfxPath -Password $securePassword | Out-Null
Export-Certificate -Cert $cert -FilePath $cerPath | Out-Null

[pscustomobject]@{
    PfxPath = $pfxPath
    CerPath = $cerPath
    Password = $Password
    Thumbprint = $cert.Thumbprint
}
