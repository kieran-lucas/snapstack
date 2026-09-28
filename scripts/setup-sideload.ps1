# Run elevated: enable MSIX sideloading and trust the selected dev certificate.
[CmdletBinding()]
param(
    [string]$CertificatePath = (Join-Path $PSScriptRoot '..\SnapStack.Dev.pfx')
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $CertificatePath -PathType Leaf)) {
    throw "Certificate file not found: $CertificatePath"
}

$regPath = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock'
if (-not (Test-Path $regPath)) {
    New-Item -Path $regPath -Force | Out-Null
}
New-ItemProperty -Path $regPath -Name 'AllowAllTrustedApps' -Value 1 -PropertyType DWord -Force | Out-Null

$password = Read-Host 'Certificate password' -AsSecureString
$certificate = Import-PfxCertificate -FilePath $CertificatePath -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople' -Password $password

Write-Output "SIDELOAD_SETUP_OK $($certificate.Thumbprint)"
