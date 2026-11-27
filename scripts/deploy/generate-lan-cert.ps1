[CmdletBinding()]
param(
    [string]$DnsName = "concept-server.lan",
    [string]$CertPassword = "CharacterConceptAI2026!",
    [string]$OutputDir = ""
)

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = (Resolve-Path "$ScriptDir\..\..").Path

if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $RepoRoot "dist\certs"
}
if (-not (Test-Path $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
}

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "   CharacterConceptAI - Trusted LAN Certificate Tool      " -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

# Collect SAN names: Primary DNS, localhost, computer name, and local IPs
$sanList = [System.Collections.Generic.List[string]]::new()
$sanList.Add($DnsName)
$sanList.Add("localhost")
$sanList.Add($env:COMPUTERNAME)

# Get active IPv4 addresses
try {
    $ipAddresses = (Get-NetIPAddress -AddressFamily IPv4 -InterfaceAlias * -ErrorAction SilentlyContinue | Where-Object { $_.IPAddress -ne "127.0.0.1" -and $_.IPAddress -notlike "169.254.*" }).IPAddress
    foreach ($ip in $ipAddresses) {
        if (-not $sanList.Contains($ip)) {
            $sanList.Add($ip)
        }
    }
}
catch {
    Write-Warning "Could not enumerate NetIPAddress, using localhost only."
}

Write-Host "Generating certificate with Subject Alternative Names:" -ForegroundColor Yellow
$sanList | ForEach-Object { Write-Host "  - $_" -ForegroundColor Gray }

# Create Self-Signed Certificate
$cert = New-SelfSignedCertificate `
    -DnsName $sanList `
    -CertStoreLocation "Cert:\CurrentUser\My" `
    -KeyLength 2048 `
    -KeyAlgorithm RSA `
    -HashAlgorithm SHA256 `
    -NotAfter (Get-Date).AddYears(2) `
    -Type SSLServerAuthentication `
    -FriendlyName "CharacterConceptAI LAN Server Cert"

Write-Host "Certificate created with Thumbprint: $($cert.Thumbprint)" -ForegroundColor Green

# Export PFX for Server Kestrel
$pfxPath = Join-Path $OutputDir "concept-server.pfx"
$securePassword = ConvertTo-SecureString -String $CertPassword -Force -AsPlainText
Export-PfxCertificate -Cert $cert -FilePath $pfxPath -Password $securePassword | Out-Null
Write-Host "Exported Server PFX to: $pfxPath" -ForegroundColor Cyan

# Export CER (Public key) for Client PCs
$cerPath = Join-Path $OutputDir "concept-server-root.cer"
Export-Certificate -Cert $cert -FilePath $cerPath -Type CERT | Out-Null
Write-Host "Exported Client CER to: $cerPath" -ForegroundColor Cyan

# Create install script for client PCs
$clientLines = @(
    '# Run on Windows Client PC as Administrator to trust the server certificate:',
    '$certPath = Join-Path $PSScriptRoot "concept-server-root.cer"',
    'certutil.exe -addstore -f "Root" $certPath',
    'Write-Host "Server certificate successfully installed into Trusted Root Store." -ForegroundColor Green'
)
$clientScriptPath = Join-Path $OutputDir "trust-cert-on-client.ps1"
[System.IO.File]::WriteAllLines($clientScriptPath, $clientLines)
Write-Host "Client trust script written to: $clientScriptPath" -ForegroundColor Cyan

Write-Host ""
Write-Host "Certificate setup complete!" -ForegroundColor Green
